namespace BaoleMaLe;

using Newtonsoft.Json;
using System.Collections.Generic;
using System.Linq;

/// <summary>
/// 一次伤害事件，用于时间轴可视化与近似 rDPS 归因。
/// 时间均为相对战斗开始的毫秒数。
/// </summary>
public sealed class DamageEvent
{
    public uint TimeMs;
    public uint ActorEntityId;   // 施放者
    public uint ActionId;
    public uint TargetEntityId;  // 受击目标
    public uint Damage;
    public bool Crit;
    public bool DirectHit;
    public bool CritDirect;
}

/// <summary>单个战斗参与者（本队成员）的统计聚合。</summary>
public sealed class ActorStat
{
    public uint EntityId;
    public string Name = "";
    public byte JobId;
    public string JobName = "";
    public bool IsLocal;
    public Dictionary<uint, SkillStat> Skills = new();

    [JsonIgnore] public ulong DamageSum => Skills.Values.Aggregate(0UL, (a, s) => a + s.DamageSum);
    [JsonIgnore] public long Hits => Skills.Values.Sum(s => s.Hits);
    [JsonIgnore] public long Casts => Skills.Values.Sum(s => s.Casts);
    [JsonIgnore] public long Crit => Skills.Values.Sum(s => s.Crit);
    [JsonIgnore] public long DirectHit => Skills.Values.Sum(s => s.DirectHit);
    [JsonIgnore] public long CritDirect => Skills.Values.Sum(s => s.CritDirect);
    [JsonIgnore] public uint DamageMax => Skills.Values.Where(s => s.DamageCount > 0)
        .Aggregate(0u, (m, s) => Math.Max(m, s.DamageMax));
}

/// <summary>同一场战斗中对某个目标（敌人）的统计聚合。</summary>
public sealed class TargetStat
{
    public uint TargetEntityId;
    public string TargetName = "";
    public ulong DamageTaken;
    public uint Hits;
    public uint MaxHit;
    public Dictionary<uint, ulong> ByActor = new(); // actorEntityId -> 对其造成的伤害
}

/// <summary>BUFF（团辅/增伤）时间窗，用于近似 rDPS 归因与时间轴标注。</summary>
public sealed class BuffWindow
{
    public uint ActorEntityId;   // 受影响者
    public uint SourceEntityId;  // 施放者（增益来源，即被归因到 rDPS 的人）
    public uint StatusId;
    public string StatusName = "";
    public uint StartMs;
    public uint EndMs;
}

/// <summary>已知的团辅 / 增伤 BUFF 状态 ID 集合，用于近似 rDPS 归因。</summary>
/// <remarks>
/// 这不是 FFLogs 官方清单，而是社区常用团辅/增伤 BUFF 的近似集合。
/// 在窗口期间，受影响者造成的伤害会按系数归因到施放者（SourceEntityId）的 rDPS。
/// 如需更精确，可在此扩充 ID。
/// </remarks>
public static class DamageBuffs
{
    public static readonly HashSet<uint> RaidBuffStatusIds = new()
    {
        786,   // 战斗神谕 Battle Litany（暴击）
        1184,  // 兄弟之证 Brotherhood（暴击/直击，近战）
        1825,  // 妖异 Devilment（暴击/直击，舞者）
        1820,  // 技巧收尾 Technical Finish
        1821,  // 标准收尾 Standard Finish
        1878,  // 占卜 Divination（暴击，学者/占星）
        1297,  // 鼓舞 Embolden（魔法增伤）
        1234,  // 左眼 Left Eye（机工）
        1235,  // 右眼 Right Eye（机工）
        1183,  // 诡计 Trick Attack（物理团辅）
        1221,  // 连锁策略 Chain Stratagem（暴击团辅）
        141,   // 战斗赞歌 Battle Voice（吟游）
        1719,  // 丰收祭 ?（增殖型 buff）
        1832,  // 贤者增益 ?
        325,   // 物理/魔法易伤类（部分敌人 debuff 视为增伤）
        1946,  // 责任 ?（部分职业增益）
    };
}
