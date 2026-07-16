namespace BaoleMaLe;

using System.Collections.Generic;
using System.Linq;
using Dalamud.Plugin.Services;
using Lumina.Excel.Sheets;

public sealed class RotationIssue
{
    public double AtSec;
    public string Kind = "";
    public string Detail = "";
}
public sealed class RotationReport
{
    public uint ActorId;
    public int TotalActions;
    public int GcdCount;
    public int OgcdCount;
    public double DurationSec;
    public double Apm;
    public double GcdCadenceSec;
    public double IdleSec;
    public double IdlePct;
    public int IdleGaps;
    public double LongestIdleSec;
    public List<RotationIssue> Issues = new();
    public List<ActionCast> Sequence = new();
}

/// <summary>
/// 输入出手序列，输出 APM、GCD 节奏以及 GCD 延迟估算。
/// GCD 由 Action 表公共复唱组判定；卡手与停顿只累计超出正常复唱的部分。
/// </summary>
public static class RotationAnalysis
{
    public const double ClipToleranceSec = 0.15;
    public const double MinDowntimeThresholdSec = 4.0;

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
        rep.Apm = rep.TotalActions / (rep.DurationSec / 60.0);

        var gcdTimes = sorted.Where(a => a.IsGcd).Select(a => a.TimeMs).ToList();
        var intervals = new List<double>();
        for (var i = 1; i < gcdTimes.Count; i++)
            intervals.Add((gcdTimes[i] - gcdTimes[i - 1]) / 1000.0);

        var active = intervals.Where(x => x >= 0.8 && x <= 4.0).OrderBy(x => x).ToList();
        if (active.Count > 0)
        {
            var lowerHalf = active.Take(Math.Max(1, (active.Count + 1) / 2)).ToList();
            rep.GcdCadenceSec = Median(lowerHalf);
        }

        var idle = 0.0;
        var gaps = 0;
        var longest = 0.0;
        if (rep.GcdCadenceSec > 0)
        {
            var downtimeThreshold = Math.Max(MinDowntimeThresholdSec, rep.GcdCadenceSec * 1.8);
            for (var i = 0; i < intervals.Count; i++)
            {
                var gap = intervals[i];
                var atSec = gcdTimes[i + 1] / 1000.0;
                var excess = Math.Max(0, gap - rep.GcdCadenceSec);

                if (gap >= downtimeThreshold)
                {
                    idle += excess;
                    gaps++;
                    longest = Math.Max(longest, excess);
                    rep.Issues.Add(new RotationIssue
                    {
                        AtSec = atSec,
                        Kind = "idle",
                        Detail = $"{atSec:F1}s 的 GCD 间隔为 {gap:F2}s，估计少打/停顿 {excess:F2}s",
                    });
                }
                else if (excess > ClipToleranceSec)
                {
                    rep.Issues.Add(new RotationIssue
                    {
                        AtSec = atSec,
                        Kind = "clip",
                        Detail = $"{atSec:F1}s 的 GCD 比基准晚 {excess:F2}s（{gap:F2}s / 基准 {rep.GcdCadenceSec:F2}s）",
                    });
                }
            }
        }

        rep.IdleSec = idle;
        rep.IdlePct = Math.Min(100, idle / rep.DurationSec * 100.0);
        rep.IdleGaps = gaps;
        rep.LongestIdleSec = longest;
        rep.Issues = rep.Issues.OrderBy(x => x.AtSec).ToList();
        return rep;
    }

    private static double Median(List<double> sorted)
    {
        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 0
            ? (sorted[middle - 1] + sorted[middle]) / 2.0
            : sorted[middle];
    }

    public static string ActionName(uint id, IDataManager dm)
    {
        try
        {
            var row = dm.GetExcelSheet<Action>()?.GetRow(id);
            if (row != null)
            {
                var name = row.Value.Name.ToString();
                if (!string.IsNullOrWhiteSpace(name))
                    return name;
            }
        }
        catch
        {
        }

        return id == 0 ? "(无)" : $"技能{id}";
    }
}
