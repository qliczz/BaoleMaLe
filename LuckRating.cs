namespace BaoleMaLe;

/// <summary>
/// 直暴运气评分。
///
/// 思路：FFXIV 中"暴击"与"直击"是两次独立判定，因此一个技能打出"直暴(CD)"的
/// 理论概率 = 暴击率 × 直击率。
///
/// 这里用<b>角色面板真实理论几率</b>（从游戏内数值换算得到，见 PlayerStats）作为零假设，
/// 比较"实际直暴次数"与"期望直暴次数"的偏差（以二项分布标准差为单位），
/// 把偏差映射成 0–100 分，并用 FFLogs 同款渐变色带着色。
///
/// 分数越高 = 这波直暴越欧；越低 = 越非酋。
/// 当面板数据暂不可用时，回退为用会话内观测到的边际暴击/直击率估算（降级，仅供参考）。
/// </summary>
public static class LuckRating
{
    // FFLogs 百分位 → 颜色（低→高）：灰(最非) → 绿 → 蓝 → 紫 → 橙 → 粉(最欧)。
    private static readonly (double Lower, uint Color, string Label)[] Bands =
    {
        (0.0, 0xFF9D9D9D, "大非酋"),
        (10.0, 0xFF00FF1E, "偏非"),
        (25.0, 0xFFDD7000, "略非"),
        (50.0, 0xFFEE35A3, "略欧"),
        (75.0, 0xFF0080FF, "欧气"),
        (95.0, 0xFFA868E2, "欧皇"),
    };

    /// <summary>
    /// 计算直暴运气分数（0–100）。
    /// </summary>
    /// <param name="hits">命中次数（分母）。</param>
    /// <param name="crit">暴击次数（含直暴）。</param>
    /// <param name="directHit">直击次数（含直暴）。</param>
    /// <param name="critDirect">直暴（暴击且直击）次数。</param>
    /// <param name="pCrit">理论暴击率(0–1)，≤0 时用观测边际率回退。</param>
    /// <param name="pDh">理论直击率(0–1)，≤0 时用观测边际率回退。</param>
    public static double ComputeScore(long hits, long crit, long directHit, long critDirect,
        double pCrit = 0, double pDh = 0)
    {
        if (hits <= 0)
            return 50;

        double useCrit = pCrit > 0 ? pCrit : (double)crit / hits;
        double useDh = pDh > 0 ? pDh : (double)directHit / hits;

        double pCd = useCrit * useDh;
        if (pCd <= 0)
            return 50;

        double expectedCd = hits * pCd;
        double observedCd = critDirect;

        double variance = expectedCd * (1 - pCd);
        if (variance <= 0)
            return observedCd >= expectedCd ? 100 : 0;

        // 以标准差为单位衡量运气偏离
        double z = (observedCd - expectedCd) / Math.Sqrt(variance);

        // 约 ±4.35 个标准差映射到 0–100
        double score = 50 + z * 11.5;
        return Math.Clamp(score, 0, 100);
    }

    /// <summary>理论期望直暴次数（用于 UI 展示 期望 vs 实际）。</summary>
    public static double ExpectedCritDirect(long hits, double pCrit, double pDh)
    {
        if (hits <= 0 || pCrit <= 0 || pDh <= 0)
            return 0;
        return hits * pCrit * pDh;
    }

    /// <summary>分数 → FFLogs 风格颜色（ImGui 的 uint 格式 0xAABBGGRR）。</summary>
    public static uint ScoreToColor(double score)
    {
        for (int i = Bands.Length - 1; i >= 0; i--)
            if (score >= Bands[i].Lower)
                return Bands[i].Color;
        return Bands[0].Color;
    }

    /// <summary>分数 → 运气标签。</summary>
    public static string ScoreToLabel(double score)
    {
        for (int i = Bands.Length - 1; i >= 0; i--)
            if (score >= Bands[i].Lower)
                return Bands[i].Label;
        return Bands[0].Label;
    }
}
