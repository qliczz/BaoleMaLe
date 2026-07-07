namespace BaoleMaLe.Structs;

/// <summary>
/// 单个效果条目（8 字节）。来源：游戏 ReceiveActionEffect 回调里 actionEffectArray 中、
/// 从偏移 0x20 开始、按 8 字节排列的 EffectEntry 数组。
///
/// HitSeverity 取值（社区公认）：
///   0 = 普通（既没暴击也没直击）
///   1 = 暴击（Crit）
///   2 = 直击（Direct Hit）
///   3 = 暴击 + 直击（Crit &amp; Direct Hit，简称"直暴"）
///
/// 注：FFXIV 中"暴击"和"直击"是两次独立判定，所以 3 = 同时触发。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x8)]
public struct EffectEntry
{
    /// <summary>效果类型。1 = 造成伤害（Damage）。其余为治疗/格挡/未命中等，本插件不计。</summary>
    [FieldOffset(0x0)] public byte EffectType;

    /// <summary>命中严重度：0=普通 1=暴击 2=直击 3=直暴。</summary>
    [FieldOffset(0x1)] public byte HitSeverity;

    [FieldOffset(0x2)] public ushort Param;

    /// <summary>效果数值（伤害量等）。</summary>
    [FieldOffset(0x4)] public uint Value;
}

/// <summary>
/// ReceiveActionEffect 回调的 actionEffectArray 指向的结构体（社区通用布局）。
/// 头部 0x20 字节后是 64 个 EffectEntry（8 目标 × 8 效果），共 0x200 字节。
/// 我们只需 ActionId（偏移 0x08）与从 0x20 起的效果数组。
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 0x220)]
public unsafe struct ActionEffect
{
    [FieldOffset(0x00)] public uint AnimationId;
    [FieldOffset(0x04)] public uint UnknownId;
    [FieldOffset(0x08)] public uint ActionId;
    [FieldOffset(0x0C)] public uint Unknown2;
    [FieldOffset(0x10)] public uint Sequence;
    [FieldOffset(0x14)] public uint Unknown3;
    [FieldOffset(0x18)] public ulong Unknown4;

    // 0x20 起：64 × EffectEntry(8) = 512 字节
    [FieldOffset(0x20)] public fixed byte Effects[0x200];
}
