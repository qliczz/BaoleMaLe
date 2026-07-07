namespace BaoleMaLe.Windows;

/// <summary>
/// 主窗口：用表格展示每个伤害技能的统计，并为"直暴运气"着色。
/// </summary>
public class MainWindow : Window
{
    private const uint TotalSentinel = 0xFFFFFFFF;

    private readonly CombatTracker tracker;
    private readonly Configuration config;
    private readonly IDataManager dataManager;

    private readonly Dictionary<uint, string> nameCache = new();
    private readonly object nameLock = new();

    public MainWindow(CombatTracker tracker, Configuration config, IDataManager dataManager)
        : base("爆了吗？###BaoleMaLeMainWindow")
    {
        this.tracker = tracker;
        this.config = config;
        this.dataManager = dataManager;

        this.Size = new Vector2(780, 540);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        // 顶部工具条
        if (ImGui.Button("重置统计"))
            this.tracker.Reset();

        ImGui.SameLine();
        bool enabled = this.tracker.IsTrackingEnabled;
        if (ImGui.Checkbox("启用统计", ref enabled))
            this.tracker.IsTrackingEnabled = enabled;

        ImGui.SameLine();
        bool onlyDmg = this.config.ShowOnlyDamageSkills;
        if (ImGui.Checkbox("仅显示造成伤害的技能", ref onlyDmg))
        {
            this.config.ShowOnlyDamageSkills = onlyDmg;
            SaveConfig();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(130);
        int minCasts = this.config.MinCastsToShow;
        if (ImGui.InputInt("最低释放次数", ref minCasts, 1, 5))
        {
            this.config.MinCastsToShow = minCasts < 1 ? 1 : minCasts;
            SaveConfig();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        float manualCrit = this.config.ManualCritRate;
        if (ImGui.InputFloat("理论暴击%", ref manualCrit, 1, 5))
        {
            this.config.ManualCritRate = Math.Clamp(manualCrit, 0f, 100f);
            SaveConfig();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(90);
        float manualDh = this.config.ManualDirectHitRate;
        if (ImGui.InputFloat("理论直击%", ref manualDh, 1, 5))
        {
            this.config.ManualDirectHitRate = Math.Clamp(manualDh, 0f, 100f);
            SaveConfig();
        }

        ImGui.Separator();

        var rows = this.tracker.Snapshot();
        var filtered = rows
            .Where(r => r.Casts >= this.config.MinCastsToShow)
            .Where(r => !this.config.ShowOnlyDamageSkills || r.Hits > 0)
            .ToList();

        if (filtered.Count == 0)
        {
            ImGui.TextUnformatted("还没有数据。进本打几套技能，再回来看看你爆了没。");
            ImGui.TextUnformatted("（提示：若统计一直为 0，多半是游戏版本更新导致钩子签名失效，请更新 CombatTracker 里的 ReceiveActionEffectSig。）");
            return;
        }

        long totalCasts = filtered.Sum(r => r.Casts);
        long totalHits = filtered.Sum(r => r.Hits);
        long totalCrit = filtered.Sum(r => r.Crit);
        long totalDh = filtered.Sum(r => r.DirectHit);
        long totalCd = filtered.Sum(r => r.CritDirect);

        if (ImGui.BeginTable("##BaoleMaLeTable", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("技能", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("释放", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("命中", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableSetupColumn("直击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("暴击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("直暴", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("运气", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("评分", ImGuiTableColumnFlags.WidthFixed, 60f);
            ImGui.TableHeadersRow();

            DrawRow("合计", totalCasts, totalHits, totalCrit, totalDh, totalCd, TotalSentinel);

            foreach (var s in filtered)
                DrawRow(LookupName(s.ActionId), s.Casts, s.Hits, s.Crit, s.DirectHit, s.CritDirect, s.ActionId);

            ImGui.EndTable();
        }
    }

    private void DrawRow(string name, long casts, long hits, long crit, long dh, long cd, uint actionId)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(name);
        if (actionId != TotalSentinel)
        {
            ImGui.SameLine();
            ImGui.PushStyleColor(ImGuiCol.Text, 0xFF808080);
            ImGui.TextUnformatted($" #{actionId}");
            ImGui.PopStyleColor();
        }

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(casts.ToString("N0"));

        ImGui.TableNextColumn();
        ImGui.TextUnformatted(hits.ToString("N0"));

        ImGui.TableNextColumn();
        DrawRate(dh, hits);

        ImGui.TableNextColumn();
        DrawRate(crit, hits);

        ImGui.TableNextColumn();
        DrawRate(cd, hits);

        bool isTotal = actionId == TotalSentinel;
        double manualCrit = this.config.ManualCritRate / 100.0;
        double manualDh = this.config.ManualDirectHitRate / 100.0;
        double score = isTotal ? -1 : LuckRating.ComputeScore(hits, crit, dh, cd, manualCrit, manualDh);
        uint color = isTotal ? 0xFFC8C8C8u : LuckRating.ScoreToColor(score);
        string label = isTotal ? "—" : LuckRating.ScoreToLabel(score);

        ImGui.TableNextColumn();
        if (isTotal)
        {
            ImGui.TextUnformatted("—");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.PlotHistogram, color);
            ImGui.ProgressBar((float)(score / 100.0), new Vector2(-1, 0), string.Empty);
            ImGui.PopStyleColor();
            ImGui.SameLine(0, 4);
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted(label);
            ImGui.PopStyleColor();
        }

        ImGui.TableNextColumn();
        if (isTotal)
        {
            ImGui.TextUnformatted("—");
        }
        else
        {
            ImGui.PushStyleColor(ImGuiCol.Text, color);
            ImGui.TextUnformatted($"{score:F0}");
            ImGui.PopStyleColor();
        }
    }

    private void DrawRate(long count, long hits)
    {
        if (hits <= 0)
        {
            ImGui.TextUnformatted("0 (0.0%)");
            return;
        }
        double rate = (double)count / hits * 100.0;
        uint tint = rate >= 50 ? 0xFF00C800u : 0xFFC8C800u;
        ImGui.PushStyleColor(ImGuiCol.Text, tint);
        ImGui.TextUnformatted($"{count} ({rate:F1}%)");
        ImGui.PopStyleColor();
    }

    private string LookupName(uint actionId)
    {
        lock (this.nameLock)
        {
            if (this.nameCache.TryGetValue(actionId, out var cached))
                return cached;
        }

        string name = $"(动作 {actionId})";
        try
        {
            var sheet = this.dataManager.GetExcelSheet<Action>();
            var row = sheet.GetRow(actionId);
            var txt = row.Name.ToString();
            if (!string.IsNullOrWhiteSpace(txt))
                name = txt;
        }
        catch
        {
            // 某些动作 id 不在 Action 表中，保留默认名
        }

        lock (this.nameLock)
            this.nameCache[actionId] = name;

        return name;
    }

    private void SaveConfig() => ConfigChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? ConfigChanged;
}
