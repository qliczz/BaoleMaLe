namespace BaoleMaLe;

using BaoleMaLe.Structs;

/// <summary>
/// 单个动作（技能）的累计统计。所有计数线程安全（由 CombatTracker 的锁保护）。
/// </summary>
public sealed class SkillStat
{
    public uint ActionId;

    /// <summary>释放次数：ReceiveActionEffect 回调中、来源为本地玩家、ActionId 有效的次数。</summary>
    public long Casts;

    /// <summary>命中次数：造成实际伤害(EffectType==1)的效果条目数（多段/AoE 会 &gt; 释放次数）。</summary>
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
/// 战斗统计核心：通过钩住 ActionManager.ReceiveActionEffect 捕获玩家打出的每一个伤害技能，
/// 并逐条累计 释放/命中/暴击/直击/直暴 次数。
/// </summary>
public unsafe class CombatTracker : IDisposable
{
    // 经典 ReceiveActionEffect 签名（每个大版本可能变动，失效时请更新此签名）。
    private const string ReceiveActionEffectSig =
        "48 89 5C 24 ?? 55 56 57 41 54 41 55 41 56 41 57 48 83 EC 60 48 8B D9";

    private readonly IGameInteropProvider interop;
    private readonly ISigScanner sigScanner;
    private readonly IObjectTable objects;
    private readonly IPluginLog log;

    private readonly Dictionary<uint, SkillStat> stats = new();
    private readonly object lockObj = new();

    private Hook<ReceiveActionEffectDelegate>? hook;

    public bool IsTrackingEnabled { get; set; } = true;

    // ReceiveActionEffect 回调委托签名（agent 为 ActionManager 实例指针）。
    private unsafe delegate void ReceiveActionEffectDelegate(
        IntPtr agent,
        uint sourceId,
        IntPtr sourceStruct,
        IntPtr dataGlobal,
        IntPtr dataHome,
        IntPtr dataOrigin,
        uint flags,
        IntPtr actionEffectArray,
        IntPtr targetActor,
        void* param);

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
            var addr = this.sigScanner.ScanText(ReceiveActionEffectSig);
            this.hook = this.interop.HookFromAddress<ReceiveActionEffectDelegate>(addr, Detour);
            this.hook.Enable();
            this.log.Info("[爆了吗？] ReceiveActionEffect 钩子已启用。");
        }
        catch (Exception ex)
        {
            this.hook = null;
            this.log.Error(ex, "[爆了吗？] 无法安装 ReceiveActionEffect 钩子，伤害统计将不可用（可能是游戏版本更新导致签名失效，请更新 ReceiveActionEffectSig）。");
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

    private void Detour(IntPtr agent, uint sourceId, IntPtr sourceStruct, IntPtr dataGlobal,
        IntPtr dataHome, IntPtr dataOrigin, uint flags, IntPtr actionEffectArray, IntPtr targetActor, void* param)
    {
        try
        {
            var localPlayer = this.objects.LocalPlayer;
            if (this.IsTrackingEnabled && localPlayer != null && actionEffectArray != IntPtr.Zero)
            {
                var localId = localPlayer.GameObjectId;
                if (sourceId == localId)
                    ProcessActionEffect(actionEffectArray);
            }
        }
        catch (Exception ex)
        {
            this.log.Error(ex, "[爆了吗？] ReceiveActionEffect 回调异常。");
        }
        finally
        {
            this.hook!.Original(agent, sourceId, sourceStruct, dataGlobal, dataHome, dataOrigin, flags, actionEffectArray, targetActor, param);
        }
    }

    private void ProcessActionEffect(IntPtr ptr)
    {
        var ae = (ActionEffect*)ptr;
        var actionId = ae->ActionId;
        if (actionId == 0)
            return;

        // 效果数组从偏移 0x20 开始，共 64 个 EffectEntry（8 目标 × 8 效果）。
        var entries = (EffectEntry*)((byte*)ptr + 0x20);

        SkillStat? stat = null;
        lock (this.lockObj)
        {
            if (!this.stats.TryGetValue(actionId, out stat))
            {
                stat = new SkillStat { ActionId = actionId };
                this.stats[actionId] = stat;
            }
            stat.Casts++;

            for (int i = 0; i < 64; i++)
            {
                var e = entries[i];
                if (e.EffectType != 1) // 1 = 造成伤害
                    continue;

                stat.Hits++;

                switch (e.HitSeverity)
                {
                    case 1: // 暴击
                        stat.Crit++;
                        break;
                    case 2: // 直击
                        stat.DirectHit++;
                        break;
                    case 3: // 直暴（暴击 + 直击）
                        stat.Crit++;
                        stat.DirectHit++;
                        stat.CritDirect++;
                        break;
                    // 0 = 普通，不计额外标记
                }
            }
        }
    }
}
