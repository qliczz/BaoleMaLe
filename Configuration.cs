namespace BaoleMaLe;

using Dalamud.Configuration;

/// <summary>
/// 插件配置，随 Dalamud 插件配置持久化（Newtonsoft JSON）。
/// </summary>
[Serializable]
public class Configuration : IPluginConfiguration
{
    /// <summary>配置版本，IPluginConfiguration 要求。</summary>
    public int Version { get; set; } = 3;

    /// <summary>是否启用伤害技能统计（含原暴击/运气功能）。</summary>
    public bool EnableTracking { get; set; } = true;

    /// <summary>是否只在表格里显示至少造成过一次伤害的技能。</summary>
    public bool ShowOnlyDamageSkills { get; set; } = true;

    /// <summary>释放次数低于该值的技能不显示在表格里（减少噪声）。</summary>
    public int MinCastsToShow { get; set; } = 1;

    /// <summary>数据浏览库保留的最近战斗场次（超出后丢弃最旧）。</summary>
    public int MaxBattles { get; set; } = 50;

    /// <summary>主窗口是否打开。</summary>
    public bool MainWindowOpen { get; set; } = true;

    // ---------- v0.4 新增：团队数据 / DPS 计量 ----------

    /// <summary>是否采集本队全员（本地玩家 + 同队队员）的 DPS / 时间轴 / BUFF。</summary>
    public bool EnablePartyMeter { get; set; } = true;

    /// <summary>技能表与技能列表中是否显示技能图标。</summary>
    public bool ShowSkillIcons { get; set; } = true;

    /// <summary>是否记录并展示 BUFF（团辅/增伤）时间轴。</summary>
    public bool TrackBuffs { get; set; } = true;

    /// <summary>近似 rDPS 的归因系数（0–1）。1=把受影响者在该 BUFF 期间的全部伤害归因到施放者。</summary>
    public double RdpsAttribution { get; set; } = 1.0;

    /// <summary>单场战斗记录的时间轴事件上限（防止极端情况内存膨胀）。</summary>
    public int TimelineMaxEvents { get; set; } = 20000;
}
