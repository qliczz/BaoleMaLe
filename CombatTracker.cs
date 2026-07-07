namespace BaoleMaLe;

using FFXIVClientStructs.FFXIV.Client.Game.Character;
using GameObjectId = FFXIVClientStructs.FFXIV.Client.Game.Object.GameObjectId;

/// <summary>
/// 单个动作（技能）的累计统计。所有计数线程安全（由 CombatTracker 的锁保护）。
/// </summary>
public sealed class SkillStat
{
    public uint ActionId;

    /// <summary>释放次数：本地玩家施放该伤害技能的次数（一次 Receive 调用计 1 次，多段/AoE 也只算 1 次释放）。</summary>
    public long Casts;

    /// <summary>命中次数：造成实际伤害(Effect.Type==0x03)的效果条目数（多段/AoE 会 &gt; 释放次数）。</summary>
    public long Hits;

    /// <summary>暴击次数（含直暴）。</summary>
    public long Crit;

    /// <summary>直击次数（含直暴）。</summary>
    public long DirectHit;

    /// <summary>直暴（暴击且直击）次数。</summary>
    public long CritDirect;

    public double CritRate => Hits > 0 ? (double)Crit / Hits : 0;
    public double DirectHitRate => Hits > 0 ? (double)DirectHit / Hits : 0;
    public double CritDirectRate => Hits > 0 ? (double)CritDirect / Hits : 0;
}

/// <summary>
/// 战斗统计核心：钩住 <see cref="ActionEffectHandler.Receive"/>（FFXIV 7.x / Dawntrail 的战斗效果入口），
/// 捕获本地玩家打出的每一个伤害技能，逐条累计 释放/命中/暴击/直击/直暴 次数。
///
/// 7.x 效果条目编码（参见 ACT_Tech_Guide 7.0）：
///   每条 Effect 8 字节：[Type][Param0]…[Value]
///   - Type == 0x03 表示"造成伤害"（一次命中）
///   - Param0 为严重度：0x20=暴击，0x40=直击，0x60=暴击+直击（普通为 0）
/// </summary>
public unsafe class CombatTracker : IDisposable
{
    // ActionEffectHandler.Receive 的签名（FFXIVClientStructs 7.x / Dawntrail 文档签名）。
    // 每个大版本可能变动；失效时请更新此签名（插件会优雅降级并在日志提示）。
    private const string ReceiveSig =
        "E8 ?? ?? ?? ?? 48 8B 8D ?? ?? ?? ?? 48 33 CC E8 ?? ?? ?? ?? 48 81 C4 00 05 00 00";

    // 效果条目里"造成伤害"的 Type 值，以及严重度位。
    private const byte TypeDamage = 0x03;
    private const byte SeverityCrit = 0x20;
    private const byte SeverityDirect = 0x40;

    private readonly IGameInteropProvider interop;
    private readonly ISigScanner sigScanner;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;

    private readonly Dictionary<uint, SkillStat> stats = new();
    private readonly object lockObj = new();

    private Hook<ReceiveDelegate>? hook;

    // 诊断计数：前若干次回调记录到日志，便于确认钩子是否生效、来源过滤是否正确。
    private int diagCount;
    private bool firstLocalDetailLogged;

    public bool IsTrackingEnabled { get; set; } = true;

    // 与 ActionEffectHandler.Receive 完全一致的委托签名。
    private unsafe delegate void ReceiveDelegate(
        uint casterEntityId,
        Character* casterPtr,
        Vector3* targetPos,
        ActionEffectHandler.Header* header,
        ActionEffectHandler.TargetEffects* effects,
        GameObjectId* targetEntityIds);


    public CombatTracker(IGameInteropProvider interop, ISigScanner sigScanner,
        IObjectTable objects, IPluginLog log)
    {
        this.interop = interop;
        this.sigScanner = sigScanner;
        this.objects = objects;
        this.log = log;
    }

    /// <summary>尝试安装钩子；失败（签名不匹配）时优雅降级，统计停用。</summary>
    public void Enable()
    {
        try
        {
            var addr = this.sigScanner.ScanText(ReceiveSig);
            this.hook = this.interop.HookFromAddress<ReceiveDelegate>(addr, Detour);
            this.hook.Enable();
            this.log.Info("[爆了吗？] ActionEffectHandler.Receive 钩子已启用。");
        }
        catch (Exception ex)
        {
            this.hook = null;
            this.log.Error(ex, "[爆了吗？] 无法安装 Receive 钩子，伤害统计将不可用（可能是游戏版本更新导致签名失效，请更新 ReceiveSig）。");
        }
    }

    public void Dispose()
    {
        this.hook?.Disable();
        this.hook?.Dispose();
        this.hook = null;
    }

    /// <summary>清空所有统计。</summary>
    public void Reset()
    {
        lock (this.lockObj)
            this.stats.Clear();
    }

    /// <summary>快照当前统计（按释放次数降序）。在锁内复制，供 UI 线程读取。</summary>
    public List<SkillStat> Snapshot()
    {
        lock (this.lockObj)
        {
            return this.stats.Values
                .Select(s => new SkillStat
                {
                    ActionId = s.ActionId,
                    Casts = s.Casts,
                    Hits = s.Hits,
                    Crit = s.Crit,
                    DirectHit = s.DirectHit,
                    CritDirect = s.CritDirect,
                })
                .OrderByDescending(s => s.Casts)
                .ToList();
        }
    }

    private void Detour(uint casterEntityId, Character* casterPtr, Vector3* targetPos,
        ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects, GameObjectId* targetEntityIds)
    {
        try
        {
            var local = this.objects.LocalPlayer;
            var localAddr = local?.Address ?? nint.Zero;

            // 诊断：前 20 次任意回调记录来源匹配情况，确认钩子生效且过滤正确。
            if (this.diagCount < 20)
            {
                this.diagCount++;
                var match = localAddr != nint.Zero && (nint)casterPtr == localAddr;
                var actionId = header != null ? header->ActionId : 0u;
                var numT = header != null ? header->NumTargets : (byte)0;
                var localObjId = local != null ? (uint)local.GameObjectId : 0u;
                this.log.Info($"[爆了吗？][诊断] Receive #{this.diagCount} caster={casterEntityId} local={localObjId} match={match} action={actionId} numTargets={numT}");
            }

            if (this.IsTrackingEnabled && localAddr != nint.Zero && casterPtr != null &&
                header != null && effects != null && (nint)casterPtr == localAddr)
            {
                ProcessActionEffect(header, effects);
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[爆了吗？] Receive 回调异常。");
        }
        finally
        {
            this.hook!.Original(casterEntityId, casterPtr, targetPos, header, effects, targetEntityIds);
        }
    }

    private void ProcessActionEffect(ActionEffectHandler.Header* header, ActionEffectHandler.TargetEffects* effects)
    {
        var actionId = header->ActionId;
        if (actionId == 0)
            return;

        var numTargets = header->NumTargets;
        if (numTargets == 0 || numTargets > 8)
            return;

        // effects 指向 numTargets 个 TargetEffects，每个 0x40 字节（即 8 个连续的 Effect）。
        // TargetEffects 在偏移 0 处就是 8 个 Effect，故可直接把首地址当作 Effect* 线性索引。
        var allEffects = (ActionEffectHandler.Effect*)effects;

        SkillStat? stat = null;
        lock (this.lockObj)
        {
            if (!this.stats.TryGetValue(actionId, out stat))
            {
                stat = new SkillStat { ActionId = actionId };
                this.stats[actionId] = stat;
            }

            var hasDamage = false;
            for (var t = 0; t < numTargets; t++)
            {
                for (var e = 0; e < 8; e++)
                {
                    var eff = allEffects[t * 8 + e];
                    if (eff.Type != TypeDamage)
                        continue;

                    hasDamage = true;
                    stat.Hits++;

                    var sev = eff.Param0;
                    if ((sev & SeverityCrit) != 0)
                        stat.Crit++;
                    if ((sev & SeverityDirect) != 0)
                        stat.DirectHit++;
                    if ((sev & (SeverityCrit | SeverityDirect)) == (SeverityCrit | SeverityDirect))
                        stat.CritDirect++;
                }
            }

            // 只有产生了伤害的技能才计入"释放次数"，自动过滤治疗/ buff 等非伤害技能。
            if (hasDamage)
                stat.Casts++;
        }

        // 首次捕获到本地伤害技能时，记录首条效果的原始字节，便于核对解码是否正确。
        if (!this.firstLocalDetailLogged && stat.Casts > 0)
        {
            this.firstLocalDetailLogged = true;
            var first = allEffects[0];
            this.log.Info($"[爆了吗？][解码] 首个本地伤害技能 action={actionId} 首效果 Type=0x{first.Type:X2} Param0=0x{first.Param0:X2} Value={first.Value}");
        }
    }
}
