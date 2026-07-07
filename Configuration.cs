namespace BaoleMaLe;

using Dalamud.Configuration;

/// <summary>
/// 插件配置，随 Dalamud 插件配置持久化（Newtonsoft JSON）。
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>配置版本，IPluginConfiguration 要求。</summary>
    public int Version { get; set; } = 1;

    /// <summary>是否启用伤害技能统计。</summary>
    public bool EnableTracking { get; set; } = true;

    /// <summary>是否只在表格里显示至少造成过一次伤害的技能。</summary>
    public bool ShowOnlyDamageSkills { get; set; } = true;

    /// <summary>释放次数低于该值的技能不显示在表格里（减少噪声）。</summary>
    public int MinCastsToShow { get; set; } = 1;

    /// <summary>
    /// 手动暴击率（0–100）。为 0 时使用会话内观测到的边际暴击率自动估算
    /// （即当前实际打出来的暴击比例）。填非 0 则按理论值比较"直暴"运气。
    /// </summary>
    public float ManualCritRate { get; set; } = 0f;

    /// <summary>手动直击率（0–100）。为 0 时同 ManualCritRate 自动估算。</summary>
    public float ManualDirectHitRate { get; set; } = 0f;

    /// <summary>主窗口是否打开。</summary>
    public bool MainWindowOpen { get; set; } = true;
}
