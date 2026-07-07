namespace BaoleMaLe;

using FFXIVClientStructs.FFXIV.Client.Game.Character;
using GameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;
using Dalamud.Plugin.Services;
using Newtonsoft.Json;
using System.IO;
using Lumina.Excel.Sheets;

/// <summary>
/// 单个技能（动作）的累计统计。
/// </summary>
public sealed class SkillStat
{
    public uint ActionId;

    /// <summary>释放次数：本地玩家施放该伤害技能的次数（一次 Receive 调用计 1 次）。</summary>
    public long Casts;

    /// <summary>命中次数：造成实际伤害(Effect.Type==0x03)的效果条目数（多段/AoE 会 &gt; 释放次数）。</summary>
    public long Hits;

    /// <summary>暴击次数（含直暴）。</summary>
    public long Crit;

    /// <summary>直击次数（含直暴）。</summary>
    public long DirectHit;

    /// <summary>直暴（暴击且直击）次数。</summary>
    public long CritDirect;

    [JsonIgnore]
    public double CritRate => Hits > 0 ? (double)Crit / Hits : 0;
    [JsonIgnore]
    public double DirectHitRate => Hits > 0 ? (double)DirectHit / Hits : 0;
    [JsonIgnore]
    public double CritDirectRate => Hits > 0 ? (double)CritDirect / Hits : 0;
}

/// <summary>
/// 一场已结束（或正在进行）的战斗记录，可序列化落盘，构成"数据浏览库"。
/// </summary>
public sealed class BattleRecord
{
    public string Id = "";
    public long StartedUnix;
    public long EndedUnix;
    public byte JobId;
    public string JobName = "";
    /// <summary>开场时从角色面板读取的理论暴击率（%，0–100）。</summary>
    public double CritRatePct;
    /// <summary>开场时从角色面板读取的理论直击率（%，0–100）。</summary>
    public double DhRatePct;
    public Dictionary<uint, SkillStat> Skills = new();
    /// <summary>整场战斗的直暴运气评分（0–100），结束时计算并缓存。</summary>
    public double? BattleLuck;
}

/// <summary>
/// 战斗统计核心：
///  - 钩住 ActionEffectHandler.Receive（7.x 战斗效果入口）采集本地玩家的伤害技能数据；
///  - 监听 Character.InCombat 自动把数据分段成"一场场战斗"；
///  - 同时维护 总计数（全职业累计）、分职业计数、以及最近 N 场战斗的历史库（落盘）。
///
/// 7.x 效果编码：每条 Effect 8 字节，Type==0x03 为伤害，Param0 严重度 0x20=暴击 / 0x40=直击 / 0x60=直暴。
/// </summary>
public unsafe class CombatTracker : IDisposable
{
    private const string ReceiveSig =
        "E8 ?? ?? ?? ?? 48 8B 8D ?? ?? ?? ?? 48 33 CC E8 ?? ?? ?? ?? 48 81 C4 00 05 00 00";

    private const byte TypeDamage = 0x03;
    private const byte SeverityCrit = 0x20;
    private const byte SeverityDirect = 0x40;

    // 连续脱战超过该秒数，判定当前战斗结束。
    private const int CombatGraceSeconds = 8;

    private readonly IGameInteropProvider interop;
    private readonly ISigScanner sigScanner;
    private readonly IObjectTable objects;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly string configDir;
    private int maxBattles;

    private Hook<ReceiveDelegate>? hook;
    private readonly object lockObj = new();

    private Dictionary<uint, SkillStat> grandTotals = new();
    private Dictionary<byte, Dictionary<uint, SkillStat>> jobTotals = new();
    private List<BattleRecord> history = new();

    private BattleRecord? current;
    private DateTime lastCombatTime = DateTime.MinValue;
    private int diagCount;

    public bool IsTrackingEnabled { get; set; } = true;

    private unsafe delegate void ReceiveDelegate(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds);

    public CombatTracker(IGameInteropProvider interop, ISigScanner sigScanner,
        IObjectTable objects, IDataManager dataManager, IPluginLog log, string configDir, int maxBattles)
    {
        this.interop = interop;
        this.sigScanner = sigScanner;
        this.objects = objects;
        this.dataManager = dataManager;
        this.log = log;
        this.configDir = configDir;
        this.maxBattles = Math.Max(1, maxBattles);
        LoadAll();
    }

    public void Enable()
    {
        try
        {
            var addr = this.sigScanner.ScanText(ReceiveSig);
            this.hook = this.interop.HookFromAddress<ReceiveDelegate>(addr, Detour);
            this.hook.Enable();
            this.log.Info("[Is that a crit？] ActionEffectHandler.Receive 钩子已启用。");
        }
        catch (Exception ex)
        {
            this.hook = null;
            this.log.Error(ex, "[Is that a crit？] 无法安装 Receive 钩子，伤害统计将不可用（可能是游戏版本更新导致签名失效，请更新 ReceiveSig）。");
        }
    }

    public void Dispose()
    {
        EndSession();
        this.hook?.Disable();
        this.hook?.Dispose();
        this.hook = null;
    }

    /// <summary>每帧由 Plugin 的 Framework tick 调用，负责自动开/关战斗分段。</summary>
    public void Tick()
    {
        bool inCombat;
        try { inCombat = PlayerStats.IsLocalPlayerInCombat(); }
        catch { inCombat = false; }

        if (inCombat)
            this.lastCombatTime = DateTime.Now;

        if (!this.IsTrackingEnabled)
        {
            if (this.current != null)
                EndSession();
            return;
        }

        if (inCombat && this.current == null)
            StartSession();
        else if (!inCombat && this.current != null &&
                 (DateTime.Now - this.lastCombatTime).TotalSeconds > CombatGraceSeconds)
            EndSession();
    }

    private void StartSession()
    {
        byte jobId = PlayerStats.GetCurrentClassJobId();
        string jobName = LookupJobName(jobId);
        PlayerStats.TryGetRates(out double crit, out double dh);
        this.current = new BattleRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
            JobId = jobId,
            JobName = jobName,
            CritRatePct = crit * 100.0,
            DhRatePct = dh * 100.0,
        };
        this.log.Info($"[Is that a crit？] 开始记录战斗：{jobName}（理论暴击 {crit * 100:F1}% / 直击 {dh * 100:F1}%）");
    }

    private void EndSession()
    {
        var rec = this.current;
        this.current = null;
        if (rec == null)
            return;

        rec.EndedUnix = DateTimeOffset.Now.ToUnixTimeSeconds();
        rec.BattleLuck = ComputeBattleLuck(rec);

        lock (this.lockObj)
        {
            this.history.Add(rec);
            while (this.history.Count > this.maxBattles)
                this.history.RemoveAt(0);
        }
        SaveHistory();
        SaveTotals();
        this.log.Info($"[Is that a crit？] 战斗结束：{rec.JobName}，技能 {rec.Skills.Count} 种，运气 {rec.BattleLuck:F0}");
    }

    private void Detour(uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        try
        {
            var local = this.objects.LocalPlayer;
            var localAddr = local?.Address ?? nint.Zero;

            if (this.diagCount < 20)
            {
                this.diagCount++;
                var match = localAddr != nint.Zero && (nint)casterPtr == localAddr;
                var actionId = header != null ? header->ActionId : 0u;
                var numT = header != null ? header->NumTargets : (byte)0;
                this.log.Info($"[Is that a crit？][诊断] Receive #{this.diagCount} caster={casterEntityId} match={match} action={actionId} numTargets={numT}");
            }

            if (this.IsTrackingEnabled && localAddr != nint.Zero && casterPtr != null &&
                header != null && effects != null && (nint)casterPtr == localAddr)
            {
                RecordEffects(header, effects);
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Is that a crit？] Receive 回调异常。");
        }
        finally
        {
            this.hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
        }
    }

    private void RecordEffects(ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects)
    {
        var actionId = header->ActionId;
        if (actionId == 0)
            return;

        var numTargets = header->NumTargets;
        if (numTargets == 0 || numTargets > 8)
            return;

        var allEffects = (ActionEffectHandler.Effect*)effects;

        long hits = 0, crit = 0, dh = 0, cd = 0;
        for (var t = 0; t < numTargets; t++)
        {
            for (var e = 0; e < 8; e++)
            {
                var eff = allEffects[t * 8 + e];
                if (eff.Type != TypeDamage)
                    continue;
                hits++;
                var sev = eff.Param0;
                if ((sev & SeverityCrit) != 0) crit++;
                if ((sev & SeverityDirect) != 0) dh++;
                if ((sev & (SeverityCrit | SeverityDirect)) == (SeverityCrit | SeverityDirect)) cd++;
            }
        }

        if (hits == 0)
            return;

        var jobId = this.current?.JobId ?? PlayerStats.GetCurrentClassJobId();
        var hasLocalDetail = false;

        lock (this.lockObj)
        {
            MergeInto(this.grandTotals, actionId, hits, crit, dh, cd);
            if (!this.jobTotals.ContainsKey(jobId))
                this.jobTotals[jobId] = new Dictionary<uint, SkillStat>();
            MergeInto(this.jobTotals[jobId], actionId, hits, crit, dh, cd);
            if (this.current != null)
            {
                MergeInto(this.current.Skills, actionId, hits, crit, dh, cd);
                hasLocalDetail = this.current.Skills.Count <= 1;
            }
        }

        if (hasLocalDetail)
        {
            this.log.Info($"[Is that a crit？][解码] 首个伤害技能 action={actionId} 首效果: hits={hits} crit={crit} dh={dh} cd={cd}");
        }
    }

    private static void MergeInto(Dictionary<uint, SkillStat> store, uint actionId, long hits, long crit, long dh, long cd)
    {
        if (!store.TryGetValue(actionId, out var s))
        {
            s = new SkillStat { ActionId = actionId };
            store[actionId] = s;
        }
        s.Casts++;
        s.Hits += hits;
        s.Crit += crit;
        s.DirectHit += dh;
        s.CritDirect += cd;
    }

    private double ComputeBattleLuck(BattleRecord rec)
    {
        long hits = 0, crit = 0, dh = 0, cd = 0;
        foreach (var s in rec.Skills.Values)
        {
            hits += s.Hits; crit += s.Crit; dh += s.DirectHit; cd += s.CritDirect;
        }
        double pCrit = rec.CritRatePct / 100.0;
        double pDh = rec.DhRatePct / 100.0;
        return LuckRating.ComputeScore(hits, crit, dh, cd, pCrit, pDh);
    }

    // ---------- 快照 / 查询 ----------

    public List<SkillStat> SnapshotGrand()
    {
        lock (this.lockObj)
            return this.grandTotals.Values.Select(s => Clone(s)).OrderByDescending(s => s.Casts).ToList();
    }

    public List<SkillStat> SnapshotJob(byte jobId)
    {
        lock (this.lockObj)
            return this.jobTotals.TryGetValue(jobId, out var d)
                ? d.Values.Select(s => Clone(s)).OrderByDescending(s => s.Casts).ToList()
                : new List<SkillStat>();
    }

    public List<SkillStat> SnapshotCurrent()
    {
        lock (this.lockObj)
            return this.current?.Skills.Values.Select(s => Clone(s)).OrderByDescending(s => s.Casts).ToList()
                   ?? new List<SkillStat>();
    }

    public List<BattleRecord> GetHistory()
    {
        lock (this.lockObj)
            return this.history.OrderByDescending(r => r.StartedUnix).ToList();
    }

    public BattleRecord? GetBattle(string id)
    {
        lock (this.lockObj)
            return this.history.FirstOrDefault(r => r.Id == id);
    }

    public List<byte> GetTrackedJobIds()
    {
        lock (this.lockObj)
            return this.jobTotals.Keys.OrderBy(k => k).ToList();
    }

    public bool IsInBattle => this.current != null;

    /// <summary>运行时调整保留战斗场次上限（立即生效并裁剪历史）。</summary>
    public int MaxBattles
    {
        set
        {
            this.maxBattles = Math.Max(1, value);
            lock (this.lockObj)
            {
                while (this.history.Count > this.maxBattles)
                    this.history.RemoveAt(0);
            }
            SaveHistory();
        }
    }

    public void Reset()
    {
        EndSession();
        lock (this.lockObj)
        {
            this.grandTotals.Clear();
            this.jobTotals.Clear();
            this.history.Clear();
        }
        SaveAll();
    }

    private static SkillStat Clone(SkillStat s) => new()
    {
        ActionId = s.ActionId, Casts = s.Casts, Hits = s.Hits,
        Crit = s.Crit, DirectHit = s.DirectHit, CritDirect = s.CritDirect,
    };

    private string LookupJobName(byte jobId)
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<ClassJob>();
            var row = sheet.GetRow(jobId);
            var name = row.Name.ToString();
            return string.IsNullOrWhiteSpace(name) ? $"职业{jobId}" : name;
        }
        catch
        {
            return $"职业{jobId}";
        }
    }

    // ---------- 持久化 ----------

    private void LoadAll()
    {
        try
        {
            var histPath = Path.Combine(this.configDir, "history.json");
            var totPath = Path.Combine(this.configDir, "totals.json");
            if (File.Exists(totPath))
            {
                var tot = JsonConvert.DeserializeObject<TotalsStore>(File.ReadAllText(totPath));
                if (tot != null)
                {
                    this.grandTotals = tot.GrandTotals ?? new Dictionary<uint, SkillStat>();
                    this.jobTotals = tot.JobTotals ?? new Dictionary<byte, Dictionary<uint, SkillStat>>();
                }
            }
            if (File.Exists(histPath))
            {
                var hist = JsonConvert.DeserializeObject<List<BattleRecord>>(File.ReadAllText(histPath));
                if (hist != null)
                    this.history = hist;
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Is that a crit？] 读取历史数据失败，将从空开始。");
        }
    }

    private void SaveHistory()
    {
        try
        {
            Directory.CreateDirectory(this.configDir);
            List<BattleRecord> copy;
            lock (this.lockObj) copy = this.history.ToList();
            File.WriteAllText(Path.Combine(this.configDir, "history.json"),
                JsonConvert.SerializeObject(copy, Formatting.Indented));
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Is that a crit？] 保存历史失败。");
        }
    }

    private void SaveTotals()
    {
        try
        {
            Directory.CreateDirectory(this.configDir);
            TotalsStore store;
            lock (this.lockObj)
                store = new TotalsStore { GrandTotals = this.grandTotals, JobTotals = this.jobTotals };
            File.WriteAllText(Path.Combine(this.configDir, "totals.json"),
                JsonConvert.SerializeObject(store, Formatting.Indented));
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[Is that a crit？] 保存总计失败。");
        }
    }

    private void SaveAll()
    {
        SaveHistory();
        SaveTotals();
    }

    private sealed class TotalsStore
    {
        public Dictionary<uint, SkillStat>? GrandTotals;
        public Dictionary<byte, Dictionary<uint, SkillStat>>? JobTotals;
    }
}
