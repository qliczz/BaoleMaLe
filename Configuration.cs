namespace BaoleMaLe;

using Dalamud.Configuration;

/// <summary>
/// 插件配置，随 Dalamud 插件配置持久化（Newtonsoft JSON）。
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>配置版本，IPluginConfiguration 要求。</summary>
    public int Version { get; set; } = 2;

    /// <summary>是否启用伤害技能统计。</summary>
    public bool EnableTracking { get; set; } = true;

    /// <summary>是否只在表格里显示至少造成过一次伤害的技能。</summary>
    public bool ShowOnlyDamageSkills { get; set; } = true;

    /// <summary>释放次数低于该值的技能不显示在表格里（减少噪声）。</summary>
    public int MinCastsToShow { get; set; } = 1;

    /// <summary>数据浏览库保留的最近战斗场次（超出后丢弃最旧）。</summary>
    public int MaxBattles { get; set; } = 50;

    /// <summary>主窗口是否打开。</summary>
    public bool MainWindowOpen { get; set; } = true;
}
