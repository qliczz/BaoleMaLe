namespace BaoleMaLe.Windows;

/// <summary>
/// 主窗口：分 Tab 展示
///  1) 本场 / 总计 / 分职业 的技能统计与直暴运气；
///  2) 战斗记录库（过去 N 场战斗，可点开查看单场）。
/// 暴击率 / 直击率直接取自角色面板（PlayerStats），无需手动填写。
/// </summary>
public class MainWindow : Window
{
    private const uint TotalSentinel = 0xFFFFFFFF;

    private readonly CombatTracker tracker;
    private readonly Configuration config;
    private readonly IDataManager dataManager;

    private readonly Dictionary<uint, string> nameCache = new();
    private readonly object nameLock = new();

    private int scopeIndex; // 统计页内的范围选择
    private string selectedBattleId = "";

    public MainWindow(CombatTracker tracker, Configuration config, IDataManager dataManager)
        : base("Is that a crit？###IsThatACritMainWindow")
    {
        this.tracker = tracker;
        this.config = config;
        this.dataManager = dataManager;

        this.Size = new Vector2(880, 560);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        DrawToolbar();

        if (!ImGui.BeginTabBar("##tabs"))
            return;
        if (ImGui.BeginTabItem("统计"))
        {
            DrawStatsTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("战斗记录"))
        {
            DrawHistoryTab();
            ImGui.EndTabItem();
        }
        ImGui.EndTabBar();
    }

    private void DrawToolbar()
    {
        if (ImGui.Button("清空全部"))
            this.tracker.Reset();

        ImGui.SameLine();
        bool enabled = this.tracker.IsTrackingEnabled;
        if (ImGui.Checkbox("启用统计", ref enabled))
        {
            this.tracker.IsTrackingEnabled = enabled;
            this.config.EnableTracking = enabled;
            SaveConfig();
        }

        ImGui.SameLine();
        bool onlyDmg = this.config.ShowOnlyDamageSkills;
        if (ImGui.Checkbox("仅显示造成伤害的技能", ref onlyDmg))
        {
            this.config.ShowOnlyDamageSkills = onlyDmg;
            SaveConfig();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        int minCasts = this.config.MinCastsToShow;
        if (ImGui.InputInt("最低释放次数", ref minCasts, 1, 5))
        {
            this.config.MinCastsToShow = minCasts < 1 ? 1 : minCasts;
            SaveConfig();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(120);
        int maxB = this.config.MaxBattles;
        if (ImGui.InputInt("保留战斗场次", ref maxB, 5, 10))
        {
            maxB = Math.Clamp(maxB, 1, 500);
            this.config.MaxBattles = maxB;
            this.tracker.MaxBattles = maxB;
            SaveConfig();
        }
    }

    // ---------- 统计页 ----------

    private void DrawStatsTab()
    {
        // 范围选择：全职业总计 / 各职业 / 当前战斗
        var jobs = this.tracker.GetTrackedJobIds();
        var options = new List<string> { "全职业总计" };
        foreach (var j in jobs)
            options.Add($"{LookupJobName(j)}（职业 {j}）");
        if (this.tracker.IsInBattle)
            options.Add("当前战斗（进行中）");

        ImGui.SetNextItemWidth(260);
        if (this.scopeIndex >= options.Count)
            this.scopeIndex = 0;
        if (ImGui.Combo("统计范围", ref this.scopeIndex, options.ToArray(), options.Count))
        {
            // 切换范围时清空选中的历史战斗
            this.selectedBattleId = "";
        }

        // 理论几率（取自角色面板）
        PlayerStats.TryGetRates(out double pCrit, out double pDh);
        ImGui.SameLine();
        ImGui.TextUnformatted($"面板理论：暴击 {pCrit * 100:F1}% ｜ 直击 {pDh * 100:F1}%");

        List<SkillStat> rows;
        double scopeCrit, scopeDh;
        if (this.scopeIndex == 0)
        {
            rows = this.tracker.SnapshotGrand();
            scopeCrit = pCrit; scopeDh = pDh;
        }
        else if (this.tracker.IsInBattle && this.scopeIndex == options.Count - 1)
        {
            rows = this.tracker.SnapshotCurrent();
            scopeCrit = pCrit; scopeDh = pDh;
        }
        else
        {
            // 某个职业（scopeIndex-1 是 jobs 中的下标）
            int jobIdx = this.scopeIndex - 1;
            byte jobId = jobIdx >= 0 && jobIdx < jobs.Count ? jobs[jobIdx] : (byte)0;
            rows = this.tracker.SnapshotJob(jobId);
            scopeCrit = pCrit; scopeDh = pDh;
        }

        var filtered = rows
            .Where(r => r.Casts >= this.config.MinCastsToShow)
            .Where(r => !this.config.ShowOnlyDamageSkills || r.Hits > 0)
            .ToList();

        if (filtered.Count == 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("还没有数据。进本打几套技能，再回来看看你爆了没。");
            ImGui.TextUnformatted("（提示：统计一直为 0 时，多半是游戏版本更新导致钩子签名失效，或来源过滤未命中本地玩家，请查看 Dalamud 日志中 [Is that a crit？] 的诊断行。）");
            return;
        }

        long totalCasts = filtered.Sum(r => r.Casts);
        long totalHits = filtered.Sum(r => r.Hits);
        long totalCrit = filtered.Sum(r => r.Crit);
        long totalDh = filtered.Sum(r => r.DirectHit);
        long totalCd = filtered.Sum(r => r.CritDirect);

        if (ImGui.BeginTable("##IsThatACritTable", 9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("技能", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("释放", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("命中", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("直击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("暴击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("直暴", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("期望直暴", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("运气", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("评分", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableHeadersRow();

            double totalScore = LuckRating.ComputeScore(totalHits, totalCrit, totalDh, totalCd, scopeCrit, scopeDh);
            double totalExpected = LuckRating.ExpectedCritDirect(totalHits, scopeCrit, scopeDh);
            DrawRow("合计", totalCasts, totalHits, totalCrit, totalDh, totalCd, totalExpected, TotalSentinel, scopeCrit, scopeDh, totalScore);

            foreach (var s in filtered)
            {
                double expected = LuckRating.ExpectedCritDirect(s.Hits, scopeCrit, scopeDh);
                double score = LuckRating.ComputeScore(s.Hits, s.Crit, s.DirectHit, s.CritDirect, scopeCrit, scopeDh);
                DrawRow(LookupName(s.ActionId), s.Casts, s.Hits, s.Crit, s.DirectHit, s.CritDirect, expected, s.ActionId, scopeCrit, scopeDh, score);
            }

            ImGui.EndTable();
        }
    }

    private void DrawRow(string name, long casts, long hits, long crit, long dh, long cd,
        double expectedCd, uint actionId, double pCrit, double pDh, double score)
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

        ImGui.TableNextColumn();
        if (hits > 0 && pCrit > 0 && pDh > 0)
            ImGui.TextUnformatted($"{expectedCd:F1}");
        else
            ImGui.TextUnformatted("—");

        bool isTotal = actionId == TotalSentinel;
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

    // ---------- 战斗记录页 ----------

    private void DrawHistoryTab()
    {
        var history = this.tracker.GetHistory();
        if (history.Count == 0)
        {
            ImGui.TextUnformatted("还没有记录到任何一场战斗。进入战斗（副本/野外均可）后，插件会自动分段记录。");
            return;
        }

        ImGui.TextUnformatted($"共记录 {history.Count} 场战斗（保留上限 {this.config.MaxBattles}）。点击一场查看该场的技能明细。");

        if (ImGui.BeginTable("##BattleList", 5, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable))
        {
            ImGui.TableSetupColumn("开始时间", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("职业", ImGuiTableColumnFlags.WidthFixed, 140f);
            ImGui.TableSetupColumn("时长", ImGuiTableColumnFlags.WidthFixed, 80f);
            ImGui.TableSetupColumn("技能数", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("整场运气", ImGuiTableColumnFlags.WidthFixed, 160f);
            ImGui.TableHeadersRow();

            foreach (var b in history)
            {
                ImGui.TableNextRow();
                bool selected = b.Id == this.selectedBattleId;
                ImGui.TableNextColumn();
                if (ImGui.Selectable(FormatTime(b.StartedUnix), selected, ImGuiSelectableFlags.SpanAllColumns))
                    this.selectedBattleId = b.Id;
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(b.JobName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(FormatDuration(b.StartedUnix, b.EndedUnix));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(b.Skills.Count.ToString("N0"));
                ImGui.TableNextColumn();
                if (b.BattleLuck.HasValue)
                {
                    uint c = LuckRating.ScoreToColor(b.BattleLuck.Value);
                    ImGui.PushStyleColor(ImGuiCol.Text, c);
                    ImGui.TextUnformatted($"{b.BattleLuck.Value:F0} {LuckRating.ScoreToLabel(b.BattleLuck.Value)}");
                    ImGui.PopStyleColor();
                }
                else
                    ImGui.TextUnformatted("—");
            }
            ImGui.EndTable();
        }

        var battle = this.selectedBattleId != "" ? this.tracker.GetBattle(this.selectedBattleId) : null;
        if (battle == null)
            return;

        ImGui.Separator();
        ImGui.TextUnformatted($"【{battle.JobName}】开始 {FormatTime(battle.StartedUnix)} ｜ 时长 {FormatDuration(battle.StartedUnix, battle.EndedUnix)} ｜ 面板理论 暴击 {battle.CritRatePct:F1}% / 直击 {battle.DhRatePct:F1}%");

        double pCrit = battle.CritRatePct / 100.0;
        double pDh = battle.DhRatePct / 100.0;
        var rows = battle.Skills.Values
            .Where(r => r.Casts >= this.config.MinCastsToShow)
            .Where(r => !this.config.ShowOnlyDamageSkills || r.Hits > 0)
            .OrderByDescending(r => r.Casts)
            .ToList();

        if (ImGui.BeginTable("##BattleDetail", 9,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("技能", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("释放", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("命中", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("直击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("暴击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("直暴", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("期望直暴", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("运气", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("评分", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableHeadersRow();

            foreach (var s in rows)
            {
                double expected = LuckRating.ExpectedCritDirect(s.Hits, pCrit, pDh);
                double score = LuckRating.ComputeScore(s.Hits, s.Crit, s.DirectHit, s.CritDirect, pCrit, pDh);
                DrawRow(LookupName(s.ActionId), s.Casts, s.Hits, s.Crit, s.DirectHit, s.CritDirect, expected, s.ActionId, pCrit, pDh, score);
            }
            ImGui.EndTable();
        }
    }

    // ---------- 工具 ----------

    private static string FormatTime(long unix)
    {
        try { return DateTimeOffset.FromUnixTimeSeconds(unix).LocalDateTime.ToString("MM-dd HH:mm"); }
        catch { return unix.ToString(); }
    }

    private static string FormatDuration(long start, long end)
    {
        var sec = Math.Max(0, end - start);
        if (sec < 60)
            return $"{sec}s";
        return $"{sec / 60}m{sec % 60}s";
    }

    private string LookupName(uint actionId)
    {
        lock (this.nameLock)
            if (this.nameCache.TryGetValue(actionId, out var cached))
                return cached;

        string name = $"(动作 {actionId})";
        try
        {
            var sheet = this.dataManager.GetExcelSheet<Action>();
            var row = sheet.GetRow(actionId);
            var txt = row.Name.ToString();
            if (!string.IsNullOrWhiteSpace(txt))
                name = txt;
        }
        catch { }

        lock (this.nameLock)
            this.nameCache[actionId] = name;
        return name;
    }

    private string LookupJobName(byte jobId)
    {
        try
        {
            var sheet = this.dataManager.GetExcelSheet<ClassJob>();
            var row = sheet.GetRow(jobId);
            var txt = row.Name.ToString();
            if (!string.IsNullOrWhiteSpace(txt))
                return txt;
        }
        catch { }
        return $"职业 {jobId}";
    }

    private void SaveConfig() => ConfigChanged?.Invoke(this, EventArgs.Empty);

    public event EventHandler? ConfigChanged;
}
