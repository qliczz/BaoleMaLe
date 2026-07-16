namespace BaoleMaLe;

using FFXIVClientStructs.FFXIV.Client.Game.Character;
using GameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;
using Dalamud.Plugin.Services;
using Dalamud.Game.ClientState.Party;
using Dalamud.Game.ClientState.Objects.Types;
using Newtonsoft.Json;
using System.IO;
using Lumina.Excel.Sheets;

/// <summary>单个技能（动作）的累计统计。</summary>
public sealed class SkillStat
{
    public uint ActionId;
    public long Casts;
    public long Hits;
    public long Crit;
    public long DirectHit;
    public long CritDirect;
    public ulong DamageSum;
    public uint DamageMax;
    public uint DamageMin = uint.MaxValue;
    public uint DamageCount;

    [JsonIgnore]
    public double CritRate => Hits > 0 ? (double)Crit / Hits : 0;
    [JsonIgnore]
    public double DirectHitRate => Hits > 0 ? (double)DirectHit / Hits : 0;
    [JsonIgnore]
    public double CritDirectRate => Hits > 0 ? (double)CritDirect / Hits : 0;
    [JsonIgnore]
    public double AvgDamage => DamageCount > 0 ? (double)DamageSum / DamageCount : 0;
}

/// <summary>一次出手记录（用于「旋转教练」：记录玩家实际按下的技能序列）。</summary>
public sealed class ActionCast
{
    public uint TimeMs;
    public uint ActionId;
    public float AnimLock;   // 引擎下发的动画锁（秒，约 = 复唱时间；GCD≈2.5，能力≈0.6）
    public bool IsGcd;
}

/// <summary>一场已结束（或正在进行）的战斗记录，可序列化落盘。</summary>
public sealed class BattleRecord
{
    public string Id = "";
    public long StartedUnix;
    public long EndedUnix;
    public byte JobId;
    public string JobName = "";
    public string ZoneName = "";
    public double CritRatePct;
    public double DhRatePct;

    /// <summary>本地玩家（原"统计"页）的技能统计，保留以兼容暴击/运气功能。</summary>
    public Dictionary<uint, SkillStat> Skills = new();

    // ---------- v0.4 新增：本队全员 / 时间轴 / 多目标 / BUFF ----------
    public Dictionary<uint, ActorStat>? Actors = new();
    public List<DamageEvent>? Timeline = new();
    public Dictionary<uint, TargetStat>? Targets = new();
    public List<BuffWindow>? Buffs = new();
    // ---------- v0.4.4 新增：旋转教练出手日志 ----------
    public Dictionary<uint, List<ActionCast>>? ActionLog = new();
    public double? DurationSec;
    public Dictionary<uint, double>? ActorRdps;
    public string? Commentary;

    public double? BattleLuck;
}

public unsafe class CombatTracker : IDisposable
{
    private const string ReceiveSig =
        "E8 ?? ?? ?? ?? 48 8B 8D ?? ?? ?? ?? 48 33 CC E8 ?? ?? ?? ?? 48 81 C4 00 05 00 00";

    private const byte TypeDamage = 0x03;
    private const byte SeverityCrit = 0x20;
    private const byte SeverityDirect = 0x40;
    private const int CombatGraceSeconds = 8;

    private readonly IGameInteropProvider interop;
    private readonly ISigScanner sigScanner;
    private readonly IObjectTable objects;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly string configDir;
    private readonly IPartyList? partyList;
    private int maxBattles;

    private Hook<ReceiveDelegate>? hook;
    private readonly object lockObj = new();

    private Dictionary<uint, SkillStat> grandTotals = new();
    private Dictionary<byte, Dictionary<uint, SkillStat>> jobTotals = new();
    private List<BattleRecord> history = new();

    private BattleRecord? current;
    private DateTime lastCombatTime = DateTime.MinValue;
    private int diagCount;

    // v0.4 本队采集状态
    private readonly HashSet<uint> partySet = new();
    private DateTime battleStart = DateTime.MinValue;
    private List<DamageEvent> timeline = new();
    private Dictionary<uint, List<ActionCast>> actionLog = new();
    private Dictionary<uint, TargetStat> targets = new();
    private List<BuffWindow> buffWindows = new();
    private readonly Dictionary<uint, List<ActiveBuff>> activeBuffs = new();
    private DateTime lastBuffPoll = DateTime.MinValue;
    private DateTime lastPartyRefresh = DateTime.MinValue;

    public bool IsTrackingEnabled { get; set; } = true;
    public bool EnablePartyMeter { get; set; } = true;
    public bool TrackBuffs { get; set; } = true;
    public double RdpsAttribution { get; set; } = 1.0;
    public int TimelineMaxEvents { get; set; } = 20000;

    private unsafe delegate void ReceiveDelegate(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds);

    public CombatTracker(IGameInteropProvider interop, ISigScanner sigScanner,
        IObjectTable objects, IDataManager dataManager, IPluginLog log, string configDir,
        int maxBattles, IPartyList? partyList)
    {
        this.interop = interop;
        this.sigScanner = sigScanner;
        this.objects = objects;
        this.dataManager = dataManager;
        this.log = log;
        this.configDir = configDir;
        this.partyList = partyList;
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

        // 周期刷新本队集合 + 轮询 BUFF
        if (this.current != null)
        {
            if ((DateTime.Now - this.lastPartyRefresh).TotalSeconds > 3)
            {
                this.lastPartyRefresh = DateTime.Now;
                RefreshParty(addOnly: true);
            }
            if (this.TrackBuffs && (DateTime.Now - this.lastBuffPoll).TotalMilliseconds > 250)
            {
                this.lastBuffPoll = DateTime.Now;
                PollBuffs();
            }
        }
    }

    private void StartSession()
    {
        byte jobId = PlayerStats.GetCurrentClassJobId();
        string jobName = LookupJobName(jobId);
        string zoneName = PlayerStats.GetCurrentZoneName();
        PlayerStats.TryGetRates(out double crit, out double dh);
        this.current = new BattleRecord
        {
            Id = Guid.NewGuid().ToString("N"),
            StartedUnix = DateTimeOffset.Now.ToUnixTimeSeconds(),
            JobId = jobId,
            JobName = jobName,
            ZoneName = zoneName,
            CritRatePct = crit * 100.0,
            DhRatePct = dh * 100.0,
            Actors = new Dictionary<uint, ActorStat>(),
            Timeline = new List<DamageEvent>(),
            Targets = new Dictionary<uint, TargetStat>(),
            Buffs = new List<BuffWindow>(),
        };
        this.battleStart = DateTime.Now;
        this.timeline = this.current.Timeline;
        this.actionLog = this.current.ActionLog!;
        this.targets = this.current.Targets;
        this.buffWindows = this.current.Buffs;
        this.activeBuffs.Clear();
        RefreshParty(addOnly: false);
        this.log.Info($"[Is that a crit？] 开始记录战斗：{jobName} @ {zoneName}（理论暴击 {crit * 100:F1}% / 直击 {dh * 100:F1}%）");
    }

    private void EndSession()
    {
        var rec = this.current;
        this.current = null;
        if (rec == null)
            return;

        rec.EndedUnix = DateTimeOffset.Now.ToUnixTimeSeconds();
        rec.DurationSec = Math.Max(1, rec.EndedUnix - rec.StartedUnix);
        rec.BattleLuck = ComputeBattleLuck(rec);

        // 收尾 BUFF 窗口
        if (this.TrackBuffs)
        {
            var endMs = (uint)(DateTime.Now - this.battleStart).TotalMilliseconds;
            foreach (var kv in this.activeBuffs)
                foreach (var ab in kv.Value)
                {
                    var w = this.buffWindows.LastOrDefault(b =>
                        b.ActorEntityId == kv.Key && b.SourceEntityId == ab.SourceId &&
                        b.StatusId == ab.StatusId && b.EndMs == ab.StartMs);
                    if (w != null) w.EndMs = endMs;
                }
            this.activeBuffs.Clear();
        }

        // 近似 rDPS
        if (this.EnablePartyMeter && rec.Actors != null)
        {
            try
            {
                rec.ActorRdps = BattleAnalysis.ComputeRdps(
                    rec.Timeline ?? new List<DamageEvent>(),
                    rec.Buffs ?? new List<BuffWindow>(),
                    rec.Actors, rec.DurationSec.Value, this.RdpsAttribution);
            }
            catch { rec.ActorRdps = null; }
            try { rec.Commentary = BattleAnalysis.GenerateCommentary(rec); }
            catch { rec.Commentary = ""; }
        }

        lock (this.lockObj)
        {
            this.history.Add(rec);
            while (this.history.Count > this.maxBattles)
                this.history.RemoveAt(0);
        }
        SaveHistory();
        SaveTotals();
        this.log.Info($"[Is that a crit？] 战斗结束：{rec.JobName}，技能 {rec.Skills.Count} 种，队员 {rec.Actors?.Count ?? 0} 人，运气 {rec.BattleLuck:F0}");
    }

    private void Detour(uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        try
        {
            var local = this.objects.LocalPlayer;
            var localId = local?.EntityId ?? 0u;

            if (this.diagCount < 10)
            {
                this.diagCount++;
                bool inParty = this.partySet.Contains(casterEntityId);
                var actionId = header != null ? header->ActionId : 0u;
                this.log.Info($"[Is that a crit？][诊断] Receive #{this.diagCount} caster={casterEntityId} inParty={inParty} local={localId} action={actionId}");
            }

            if (this.IsTrackingEnabled && header != null && effects != null && targetEntityIds != null)
            {
                bool shouldProcess = this.EnablePartyMeter
                    ? this.partySet.Contains(casterEntityId)
                    : (casterEntityId == localId);
                if (shouldProcess)
                {
                    RecordCast(casterEntityId, header);
                    RecordEffectsForActor(casterEntityId, header, effects, targetEntityIds);
                }
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

    private void RecordEffectsForActor(uint casterEntityId, ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        var actor = EnsureActor(casterEntityId);
        var actionId = header->ActionId;
        if (actionId == 0)
            return;

        var numTargets = header->NumTargets;
        if (numTargets == 0 || numTargets > 8)
            return;

        var allEffects = (ActionEffectHandler.Effect*)effects;
        long hits = 0, crit = 0, dh = 0, cd = 0;
        ulong dmgSum = 0;
        uint dmgMax = 0;
        uint dmgMin = uint.MaxValue;
        uint dmgCount = 0;

        var battleMs = (uint)(DateTime.Now - this.battleStart).TotalMilliseconds;

        for (var t = 0; t < numTargets; t++)
        {
            for (var e = 0; e < 8; e++)
            {
                var eff = allEffects[t * 8 + e];
                if (eff.Type != TypeDamage)
                    continue;
                hits++;
                var sev = eff.Param0;
                bool isCrit = (sev & SeverityCrit) != 0;
                bool isDh = (sev & SeverityDirect) != 0;
                bool isCd = (sev & (SeverityCrit | SeverityDirect)) == (SeverityCrit | SeverityDirect);
                if (isCrit) crit++;
                if (isDh) dh++;
                if (isCd) cd++;

                var dmg = (uint)eff.Value;
                dmgCount++;
                dmgSum += dmg;
                if (dmg > dmgMax) dmgMax = dmg;
                if (dmg < dmgMin) dmgMin = dmg;

                // 目标与多目标统计
                uint tid = (uint)targetEntityIds[t];
                RecordTarget(tid, dmg, casterEntityId);

                // 时间轴事件
                if (this.timeline.Count < this.TimelineMaxEvents)
                {
                    this.timeline.Add(new DamageEvent
                    {
                        TimeMs = battleMs,
                        ActorEntityId = casterEntityId,
                        ActionId = actionId,
                        TargetEntityId = tid,
                        Damage = dmg,
                        Crit = isCrit,
                        DirectHit = isDh,
                        CritDirect = isCd,
                    });
                }
            }
        }

        if (hits == 0)
            return;

        // 计入该队员的技能统计
        lock (this.lockObj)
        {
            MergeInto(actor.Skills, actionId, hits, crit, dh, cd, dmgSum, dmgMax, dmgMin, dmgCount);
            // 本地玩家同时维护原"统计"页数据（暴击/运气）
            if (actor.IsLocal)
            {
                var jobId = actor.JobId;
                MergeInto(this.grandTotals, actionId, hits, crit, dh, cd, dmgSum, dmgMax, dmgMin, dmgCount);
                if (!this.jobTotals.ContainsKey(jobId))
                    this.jobTotals[jobId] = new Dictionary<uint, SkillStat>();
                MergeInto(this.jobTotals[jobId], actionId, hits, crit, dh, cd, dmgSum, dmgMax, dmgMin, dmgCount);
                if (this.current != null)
                    MergeInto(this.current.Skills, actionId, hits, crit, dh, cd, dmgSum, dmgMax, dmgMin, dmgCount);
            }
        }
    }

    /// <summary>
    /// 记录一次「出手」（无论是否造成伤害），用于「旋转教练」的技能序列分析。
    /// 通过引擎下发的 AnimationLock 粗判 GCD/能力：值越大越可能是 GCD（复唱锁）。
    /// </summary>
    private void RecordCast(uint casterEntityId, ActionEffectHandler.Header* header)
    {
        if (header->ActionId == 0)
            return;
        var castMs = (uint)(DateTime.Now - this.battleStart).TotalMilliseconds;
        float animLock = header->AnimationLock;
        bool isGcd = animLock >= 1.2f;
        lock (this.lockObj)
        {
            if (!this.actionLog.ContainsKey(casterEntityId))
                this.actionLog[casterEntityId] = new List<ActionCast>();
            var lst = this.actionLog[casterEntityId];
            if (lst.Count < this.TimelineMaxEvents)
                lst.Add(new ActionCast { TimeMs = castMs, ActionId = header->ActionId, AnimLock = animLock, IsGcd = isGcd });
        }
    }

    private void RecordTarget(uint tid, uint dmg, uint actorEntityId)
    {
        if (tid == 0 || dmg == 0)
            return;
        string tname;
        try
        {
            var obj = this.objects.SearchByEntityId(tid);
            tname = obj?.Name.ToString() ?? $"目标 {tid}";
        }
        catch { tname = $"目标 {tid}"; }

        lock (this.lockObj)
        {
            TargetStat? ts = null;
            if (this.targets != null)
            {
                if (!this.targets.TryGetValue(tid, out ts))
                {
                    ts = new TargetStat { TargetEntityId = tid, TargetName = tname };
                    this.targets[tid] = ts;
                }
                else if (string.IsNullOrEmpty(ts.TargetName) || ts.TargetName == $"目标 {tid}")
                {
                    ts.TargetName = tname;
                }
                ts.DamageTaken += dmg;
                ts.Hits++;
                if (dmg > ts.MaxHit) ts.MaxHit = dmg;
                if (!ts.ByActor.ContainsKey(actorEntityId))
                    ts.ByActor[actorEntityId] = 0;
                ts.ByActor[actorEntityId] += dmg;
            }
        }
    }

    private ActorStat EnsureActor(uint entityId)
    {
        lock (this.lockObj)
        {
            if (this.current != null && this.current.Actors != null &&
                this.current.Actors.TryGetValue(entityId, out var existing))
                return existing;
        }

        string name = "玩家"; byte jobId = 0; string jobName = ""; bool isLocal = false;
        try
        {
            var obj = this.objects.SearchByEntityId(entityId);
            if (obj != null)
            {
                name = obj.Name.ToString();
                var ch = obj as ICharacter;
                if (ch != null) { jobId = (byte)ch.ClassJob.RowId; jobName = LookupJobName(jobId); }
                var lp = this.objects.LocalPlayer;
                isLocal = lp != null && lp.EntityId == entityId;
            }
        }
        catch { }

        var st = new ActorStat { EntityId = entityId, Name = name, JobId = jobId, JobName = jobName, IsLocal = isLocal };
        lock (this.lockObj)
        {
            this.partySet.Add(entityId);
            if (this.current != null && this.current.Actors != null)
            {
                if (!this.current.Actors.ContainsKey(entityId))
                    this.current.Actors[entityId] = st;
                else
                    st = this.current.Actors[entityId];
            }
        }
        return st;
    }

    private void RefreshParty(bool addOnly)
    {
        if (!addOnly)
            this.partySet.Clear();
        try
        {
            var lp = this.objects.LocalPlayer;
            if (lp != null) this.partySet.Add(lp.EntityId);
            if (this.partyList != null)
            {
                int n = this.partyList.Length;
                for (int i = 0; i < n; i++)
                {
                    var m = this.partyList[i];
                    if (m != null && m.EntityId != 0)
                        this.partySet.Add(m.EntityId);
                }
            }
        }
        catch { }
    }

    private void PollBuffs()
    {
        if (this.current == null || !this.TrackBuffs || this.partyList == null)
            return;
        var nowMs = (uint)(DateTime.Now - this.battleStart).TotalMilliseconds;
        var seen = new HashSet<(uint, uint, uint)>();

        int n = this.partyList.Length;
        for (int i = 0; i < n; i++)
        {
            var m = this.partyList[i];
            if (m == null) continue;
            uint affected = m.EntityId;
            if (affected == 0) continue;

            foreach (var st in m.Statuses)
            {
                uint sid = st.StatusId;
                if (sid == 0) continue;
                if (!DamageBuffs.RaidBuffStatusIds.Contains(sid)) continue;
                uint src = st.SourceId;
                if (src == 0 || src == affected) continue;
                if (!this.partySet.Contains(src)) continue;

                seen.Add((affected, src, sid));
                bool already = this.activeBuffs.TryGetValue(affected, out var list) &&
                               list.Any(b => b.SourceId == src && b.StatusId == sid);
                if (!already)
                {
                    if (!this.activeBuffs.ContainsKey(affected))
                        this.activeBuffs[affected] = new List<ActiveBuff>();
                    this.activeBuffs[affected].Add(new ActiveBuff { SourceId = src, StatusId = sid, StartMs = nowMs });

                    string sname = "";
                    try
                    {
                        var row = this.dataManager.GetExcelSheet<Status>()?.GetRow(sid);
                        if (row != null) sname = row.Value.Name.ToString();
                    }
                    catch { }
                    lock (this.lockObj)
                    {
                        this.buffWindows.Add(new BuffWindow
                        {
                            ActorEntityId = affected,
                            SourceEntityId = src,
                            StatusId = sid,
                            StatusName = sname,
                            StartMs = nowMs,
                            EndMs = nowMs,
                        });
                    }
                }
            }
        }

        // 关闭未持续出现的窗口
        foreach (var kv in this.activeBuffs)
        {
            uint affected = kv.Key;
            var list = kv.Value;
            for (int j = list.Count - 1; j >= 0; j--)
            {
                var ab = list[j];
                if (!seen.Contains((affected, ab.SourceId, ab.StatusId)))
                {
                    lock (this.lockObj)
                    {
                        var w = this.buffWindows.LastOrDefault(b =>
                            b.ActorEntityId == affected && b.SourceEntityId == ab.SourceId &&
                            b.StatusId == ab.StatusId && b.EndMs == ab.StartMs);
                        if (w != null) w.EndMs = nowMs;
                    }
                    list.RemoveAt(j);
                }
            }
        }
    }

    private static void MergeInto(Dictionary<uint, SkillStat> store, uint actionId,
        long hits, long crit, long dh, long cd, ulong dmgSum, uint dmgMax, uint dmgMin, uint dmgCount)
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
        s.DamageSum += dmgSum;
        s.DamageCount += dmgCount;
        if (dmgMax > s.DamageMax) s.DamageMax = dmgMax;
        if (dmgCount > 0 && dmgMin < s.DamageMin) s.DamageMin = dmgMin;
    }

    private double ComputeBattleLuck(BattleRecord rec)
    {
        long hits = 0, cd = 0;
        foreach (var s in rec.Skills.Values)
        {
            hits += s.Hits; cd += s.CritDirect;
        }
        // 以「自我历史基线直暴率」为零假设（v0.4.2 起不再用面板基础概率）。
        double baseCd = this.GetLuckBaselineCdRate();
        if (baseCd <= 0)
        {
            // 尚无个人历史（首场/无数据）时，回退用面板理论值作降级展示。
            baseCd = (rec.CritRatePct / 100.0) * (rec.DhRatePct / 100.0);
        }
        return LuckRating.ComputeScore(hits, cd, baseCd);
    }

    /// <summary>
    /// 个人历史基线直暴率 = 本地玩家在所有已记录战斗中累计的 直暴数 / 命中数。
    /// 作为「运气」评价的零假设（相对你自己常态偏高=欧、偏低=非酋）。
    /// grandTotals 只累计本地玩家的技能统计，故天然就是"你自己"的基线。
    /// 无数据时返回 0（调用方据此回退）。
    /// </summary>
    public double GetLuckBaselineCdRate()
    {
        lock (this.lockObj)
        {
            long hits = 0, cd = 0;
            foreach (var s in this.grandTotals.Values)
            {
                hits += s.Hits;
                cd += s.CritDirect;
            }
            return hits > 0 ? (double)cd / hits : 0;
        }
    }

    private sealed class ActiveBuff
    {
        public uint SourceId;
        public uint StatusId;
        public uint StartMs;
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

    /// <summary>当前战斗的本队成员快照（按总伤害降序）。</summary>
    public List<ActorStat> SnapshotCurrentActors()
    {
        lock (this.lockObj)
            return this.current?.Actors?.Values.Select(a => CloneActor(a)).OrderByDescending(a => a.DamageSum).ToList()
                   ?? new List<ActorStat>();
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

    /// <summary>当前战斗的只读视图副本（用于团队数据页实时展示）。Timeline 最多复制 maxTimeline 条（取最近）。</summary>
    public BattleRecord? GetCurrentBattleView(int maxTimeline = 3000)
    {
        lock (this.lockObj)
        {
            if (this.current == null)
                return null;
            List<DamageEvent>? tlCopy = null;
            var tl = this.current.Timeline;
            if (tl != null && tl.Count > 0)
            {
                int take = Math.Min(tl.Count, maxTimeline);
                tlCopy = tl.GetRange(tl.Count - take, take);
            }
            double dur = (DateTime.Now - this.battleStart).TotalSeconds;
            return new BattleRecord
            {
                Id = this.current.Id,
                StartedUnix = this.current.StartedUnix,
                EndedUnix = this.current.EndedUnix,
                JobId = this.current.JobId,
                JobName = this.current.JobName,
                ZoneName = this.current.ZoneName,
                CritRatePct = this.current.CritRatePct,
                DhRatePct = this.current.DhRatePct,
                DurationSec = dur > 0 ? dur : 1,
                Actors = this.current.Actors?.ToDictionary(kv => kv.Key, kv => CloneActor(kv.Value)),
                Timeline = tlCopy,
                Targets = this.current.Targets?.Values.ToDictionary(t => t.TargetEntityId, t => new TargetStat
                {
                    TargetEntityId = t.TargetEntityId,
                    TargetName = t.TargetName,
                    DamageTaken = t.DamageTaken,
                    Hits = t.Hits,
                    MaxHit = t.MaxHit,
                    ByActor = new Dictionary<uint, ulong>(t.ByActor),
                }),
                Buffs = this.current.Buffs?.ToList(),
                ActionLog = this.current.ActionLog?.ToDictionary(kv => kv.Key, kv => kv.Value.ToList()),
                BattleLuck = this.current.BattleLuck,
            };
        }
    }

    /// <summary>对当前战斗实时计算近似 rDPS（复用 BattleAnalysis）。无数据返回 null。</summary>
    public Dictionary<uint, double>? GetCurrentRdps()
    {
        lock (this.lockObj)
        {
            if (this.current == null || this.current.Actors == null)
                return null;
            double dur = (DateTime.Now - this.battleStart).TotalSeconds;
            if (dur <= 0)
                return null;
            try
            {
                return BattleAnalysis.ComputeRdps(
                    this.current.Timeline ?? new List<DamageEvent>(),
                    this.current.Buffs ?? new List<BuffWindow>(),
                    this.current.Actors, dur, this.RdpsAttribution);
            }
            catch
            {
                return null;
            }
        }
    }

    public List<byte> GetTrackedJobIds()
    {
        lock (this.lockObj)
            return this.jobTotals.Keys.OrderBy(k => k).ToList();
    }

    public bool IsInBattle => this.current != null;

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
        DamageSum = s.DamageSum, DamageMax = s.DamageMax,
        DamageMin = s.DamageMin, DamageCount = s.DamageCount,
    };

    private static ActorStat CloneActor(ActorStat a) => new()
    {
        EntityId = a.EntityId, Name = a.Name, JobId = a.JobId, JobName = a.JobName, IsLocal = a.IsLocal,
        Skills = a.Skills.ToDictionary(kv => kv.Key, kv => Clone(kv.Value)),
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
                {
                    // 迁移：旧版本（v0.3 及以前）的记录没有 Id 字段，
                    // 而 v0.4 用 Guid 作为「战斗记录」页的选中键，缺失 Id 会导致 GetBattle 永远返回 null、
                    // 详情打不开。这里给缺失 Id 的记录补一个，并回存。
                    bool migrated = false;
                    foreach (var r in hist)
                    {
                        if (string.IsNullOrEmpty(r.Id))
                        {
                            r.Id = Guid.NewGuid().ToString("N");
                            migrated = true;
                        }
                        // 兼容旧字段名
                        if (r.Skills == null)
                            r.Skills = new Dictionary<uint, SkillStat>();
                        if (r.Actors == null)
                            r.Actors = new Dictionary<uint, ActorStat>();
                        if (r.Timeline == null)
                            r.Timeline = new List<DamageEvent>();
                        if (r.Targets == null)
                            r.Targets = new Dictionary<uint, TargetStat>();
                        if (r.Buffs == null)
                            r.Buffs = new List<BuffWindow>();
                    }
                    this.history = hist;
                    if (migrated)
                    {
                        this.log.Info("[Is that a crit？] 检测到旧版战斗记录，已补充 Id 并回存。");
                        SaveHistory();
                    }
                }
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
