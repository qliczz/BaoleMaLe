namespace BaoleMaLe;

using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

/// <summary>旋转教练：一次待改进提示。</summary>
public sealed class RotationIssue
{
    public double AtSec;
    public string Kind = "";   // "idle" | "clip"
    public string Detail = "";
}

/// <summary>旋转教练：单个成员的分析报告。</summary>
public sealed class RotationReport
{
    public uint ActorId;
    public int TotalActions;
    public int GcdCount;
    public int OgcdCount;
    public double DurationSec;
    public double Apm;
    public double GcdCadenceSec;   // GCD 之间的中位间隔（秒）
    public double IdleSec;
    public double IdlePct;
    public int IdleGaps;
    public double LongestIdleSec;
    public List<RotationIssue> Issues = new();
    public List<ActionCast> Sequence = new();
}

/// <summary>
/// 旋转分析层（v0.4.4）。
/// 输入：按成员聚合的出手序列（时间 + 技能ID + 是否GCD）。
/// 输出：APM、GCD 节奏、发呆统计，以及「发呆段 / 疑似卡手」待改进列表。
/// 说明：GCD 判定依赖引擎下发的 AnimationLock 粗判，属启发式；发呆/APM 等不依赖该判定，始终可靠。
/// </summary>
public static class RotationAnalysis
{
    /// <summary>发呆阈值：相邻出手间隔超过该秒数即记为一段发呆。</summary>
    public const double IdleThresholdSec = 2.0;

    public static RotationReport Analyze(Dictionary<uint, List<ActionCast>>? log, uint focus, double durSec, IDataManager dm)
    {
        var rep = new RotationReport { ActorId = focus, DurationSec = durSec > 0 ? durSec : 1 };
        if (log == null || !log.TryGetValue(focus, out var seq) || seq == null || seq.Count == 0)
            return rep;

        var sorted = seq.OrderBy(a => a.TimeMs).ToList();
        rep.Sequence = sorted;
        rep.TotalActions = sorted.Count;
        rep.GcdCount = sorted.Count(a => a.IsGcd);
        rep.OgcdCount = sorted.Count - rep.GcdCount;
        double dur = rep.DurationSec;
        rep.Apm = dur > 0 ? rep.TotalActions / (dur / 60.0) : 0;

        // GCD 节奏 = GCD 之间间隔的中位数
        var gcdTimes = sorted.Where(a => a.IsGcd).Select(a => a.TimeMs).ToList();
        if (gcdTimes.Count >= 2)
        {
            var intervals = new List<double>();
            for (int i = 1; i < gcdTimes.Count; i++)
                intervals.Add((gcdTimes[i] - gcdTimes[i - 1]) / 1000.0);
            intervals.Sort();
            rep.GcdCadenceSec = intervals[intervals.Count / 2];
        }

        // 发呆检测：相邻出手间隔过大
        double idle = 0;
        int gaps = 0;
        double longest = 0;
        for (int i = 1; i < sorted.Count; i++)
        {
            double gap = (sorted[i].TimeMs - sorted[i - 1].TimeMs) / 1000.0;
            if (gap > IdleThresholdSec)
            {
                idle += gap;
                gaps++;
                if (gap > longest) longest = gap;
                rep.Issues.Add(new RotationIssue
                {
                    AtSec = sorted[i - 1].TimeMs / 1000.0,
                    Kind = "idle",
                    Detail = $"{(sorted[i - 1].TimeMs / 1000.0):F1}s 后停顿 {gap:F1}s 才出手（已 {gaps} 段发呆）",
                });
            }
        }
        rep.IdleSec = idle;
        rep.IdlePct = dur > 0 ? idle / dur * 100.0 : 0;
        rep.IdleGaps = gaps;
        rep.LongestIdleSec = longest;

        // 疑似卡手：GCD 间隔远低于常态（< 60% 中位节奏且 < 1s）
        if (gcdTimes.Count >= 3 && rep.GcdCadenceSec > 0)
        {
            for (int i = 1; i < gcdTimes.Count; i++)
            {
                double iv = (gcdTimes[i] - gcdTimes[i - 1]) / 1000.0;
                if (iv < rep.GcdCadenceSec * 0.6 && iv < 1.0)
                {
                    rep.Issues.Add(new RotationIssue
                    {
                        AtSec = gcdTimes[i] / 1000.0,
                        Kind = "clip",
                        Detail = $"{(gcdTimes[i] / 1000.0):F1}s 处 GCD 间隔仅 {iv:F2}s，疑似卡手/提前（常态 {rep.GcdCadenceSec:F2}s）",
                    });
                }
            }
        }

        rep.Issues = rep.Issues.OrderBy(x => x.AtSec).ToList();
        return rep;
    }

    /// <summary>查技能中文名（Lumina Action 表）。查不到回退为「技能{id}」。</summary>
    public static string ActionName(uint id, IDataManager dm)
    {
        try
        {
            var row = dm.GetExcelSheet<Action>()?.GetRow(id);
            if (row != null)
            {
                var n = row.Value.Name.ToString();
                if (!string.IsNullOrWhiteSpace(n))
                    return n;
            }
        }
        catch { }
        return id == 0 ? "(无)" : $"技能{id}";
    }
}
