namespace BaoleMaLe;

using System.Collections.Generic;
using System.Linq;
using System.Text;

/// <summary>
/// 战斗数据分析：aDPS / 近似 rDPS 计算，以及战斗后的风趣 FF14 风格点评。
/// </summary>
public static class BattleAnalysis
{
    /// <summary>每秒伤害 = 总伤害 / 战斗时长(秒)。</summary>
    public static double Adps(ulong damage, double durationSec)
        => durationSec > 0 ? damage / durationSec : 0;

    /// <summary>
    /// 近似 rDPS：本人 aDPS + 在他人身上由本队员施放的增伤 BUFF 期间，他人造成的伤害（按 attribution 系数归因）。
    /// 这是 FFLogs rDPS 的简化近似，并非官方还原。
    /// </summary>
    public static Dictionary<uint, double> ComputeRdps(
        List<DamageEvent> timeline, List<BuffWindow> buffs,
        Dictionary<uint, ActorStat> actors, double durationSec, double attribution)
    {
        var rdps = new Dictionary<uint, double>();
        foreach (var a in actors.Values)
            rdps[a.EntityId] = Adps(a.DamageSum, durationSec);

        if (attribution <= 0 || buffs.Count == 0 || timeline.Count == 0)
            return rdps;

        // 预建：目标实体 -> 活跃增伤BUFF窗口列表
        var byTarget = new Dictionary<uint, List<BuffWindow>>();
        foreach (var b in buffs)
        {
            if (!DamageBuffs.RaidBuffStatusIds.Contains(b.StatusId))
                continue;
            if (!byTarget.TryGetValue(b.ActorEntityId, out var list))
                byTarget[b.ActorEntityId] = list = new List<BuffWindow>();
            list.Add(b);
        }

        foreach (var e in timeline)
        {
            if (!byTarget.TryGetValue(e.TargetEntityId, out var list))
                continue;
            foreach (var b in list)
            {
                if (e.TimeMs < b.StartMs || e.TimeMs > b.EndMs)
                    continue;
                if (rdps.ContainsKey(b.SourceEntityId))
                    rdps[b.SourceEntityId] += e.Damage * attribution;
            }
        }
        return rdps;
    }

    /// <summary>
    /// 生成战斗后风趣点评（FF14 风格）。基于直暴运气分、与理论几率的偏离、以及是否出现"非酋时刻"。
    /// </summary>
    public static string GenerateCommentary(BattleRecord rec)
    {
        var sb = new StringBuilder();
        double luck = rec.BattleLuck ?? 50;
        double pCrit = rec.CritRatePct / 100.0;
        double pDh = rec.DhRatePct / 100.0;

        long hits = 0, cd = 0;
        foreach (var a in rec.Actors?.Values ?? Enumerable.Empty<ActorStat>())
        {
            hits += a.Hits; cd += a.CritDirect;
        }
        // 若没有 Actor 数据（旧记录），回退用本地技能统计
        if (hits == 0)
        {
            foreach (var s in rec.Skills.Values) { hits += s.Hits; cd += s.CritDirect; }
        }

        double expectedCd = hits * pCrit * pDh;
        double actualCd = cd;
        double dev = expectedCd > 0 ? (actualCd - expectedCd) : 0;

        sb.Append("[战斗简评] ");
        if (luck >= 90)
            sb.Append("欧皇附体！这波直暴率爆表，水晶塔的传令官都想给你递辞职信。");
        else if (luck >= 75)
            sb.Append("运气相当欧气，直暴像不要钱一样往外蹦，队友投来敬畏的目光。");
        else if (luck >= 55)
            sb.Append("手感不错，直暴基本在线，属于\"正常人类能打出的范围\"。");
        else if (luck >= 40)
            sb.Append("中规中矩，偶尔脸黑，但还没到要找占星师算命的程度。");
        else if (luck >= 25)
            sb.Append("今天这脸……建议先去神典石前面拜一拜再来。");
        else
            sb.Append("非酋本酋！这直暴率连水晶都看不下去了，建议深呼吸，下一波交给我。");

        if (expectedCd > 0)
        {
            if (dev >= expectedCd * 0.3)
                sb.Append(" 实际直暴比理论高出一截，这波稳了。");
            else if (dev <= -expectedCd * 0.3)
                sb.Append(" 实际直暴比理论低了一截，典型的\"说好的暴击呢\"现场。");
        }

        // 队伍规模点评
        int members = rec.Actors?.Count ?? 0;
        if (members > 1)
            sb.Append($" 本场共记录 {members} 名队员的战斗数据，详见团队数据页。");

        return sb.ToString();
    }
}
