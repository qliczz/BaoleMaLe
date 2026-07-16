namespace BaoleMaLe;

/// <summary>
/// 直暴运气评分。
///
/// 【v0.4.2 起改用「自我历史基线」】不再用角色面板的理论基础概率当尺子——
/// 因为实战里很多动作本就不进暴击判定（DoT、部分 oGCD、某些职业平A），
/// 真实平均直暴率远低于面板值，拿它当基线会把"正常发挥"误判成"非酋"，
/// 也违背"玩家不可能全程百分百暴击"的直觉。
///
/// 现在以<b>玩家自己的历史平均直暴率</b>（跨所有战斗累计，见 CombatTracker.GetLuckBaselineCdRate）
/// 作为零假设：本场/本技能的直暴率相对自己常态偏高=欧、偏低=非酋。
/// 比较"实际直暴次数"与"期望直暴次数(=命中×个人基线直暴率)"的偏差（二项分布标准差为单位），
/// 映射成 0–100 分，并用 FFLogs 同款渐变色带着色。分数越高越欧。
/// 尚无个人历史时（首场/无数据）回退用面板理论值，仅作降级展示。
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
    /// <param name="critDirect">直暴（暴击且直击）次数（分子）。</param>
    /// <param name="expectedCdRate">期望直暴率(0–1)，即「个人历史基线直暴率」；≤0 时回退为中性 50。</param>
    public static double ComputeScore(long hits, long critDirect, double expectedCdRate)
    {
        if (hits <= 0)
            return 50;

        double pCd = expectedCdRate;
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

    /// <summary>期望直暴次数（用于 UI 展示 期望 vs 实际）。</summary>
    public static double ExpectedCritDirect(long hits, double expectedCdRate)
    {
        if (hits <= 0 || expectedCdRate <= 0)
            return 0;
        return hits * expectedCdRate;
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
