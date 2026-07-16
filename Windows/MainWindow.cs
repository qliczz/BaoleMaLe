namespace BaoleMaLe.Windows;

/// <summary>
/// 主窗口：分 Tab 展示
///  1) 统计：本场 / 总计 / 分职业 的技能统计与直暴运气；
///  2) 战斗记录：过去 N 场战斗，可点开查看单场技能明细；
///  3) 团队数据：本队全员 DPS / aDPS / 近似 rDPS、技能时间轴、多目标统计、BUFF 时间轴与战斗点评。
/// 暴击率 / 直击率直接取自角色面板（PlayerStats），无需手动填写。
/// </summary>
public class MainWindow : Window
{
    private const uint TotalSentinel = 0xFFFFFFFF;

    private readonly CombatTracker tracker;
    private readonly Configuration config;
    private readonly IDataManager dataManager;
    private readonly IconCache? iconCache;

    private readonly Dictionary<uint, string> nameCache = new();
    private readonly object nameLock = new();

    private int scopeIndex; // 统计页内的范围选择
    private string selectedBattleId = "";
    private uint selectedActorId; // 团队数据页中选中的队员，用于展示其技能明细

    // 团队数据页的节流快照（每 500ms 刷新一次，避免每帧复制时间轴）
    private DateTime lastTeamRefresh = DateTime.MinValue;
    private BattleRecord? cachedTeamView;
    private Dictionary<uint, double>? cachedTeamRdps;

    // 时间轴交互状态（按战斗/视图重置：拖拽平移 + 滚轮缩放）
    private string tlViewKey = "";
    private float tlPanX;
    private float tlScale = 1f;

    public MainWindow(CombatTracker tracker, Configuration config, IDataManager dataManager, IconCache? iconCache)
        : base("Is that a crit？###IsThatACritMainWindow")
    {
        this.tracker = tracker;
        this.config = config;
        this.dataManager = dataManager;
        this.iconCache = iconCache;

        this.Size = new Vector2(920, 600);
        this.SizeCondition = ImGuiCond.FirstUseEver;
    }

    public override void Draw()
    {
        // 游戏尚未就绪(加载/标题界面)时不渲染重内容(图标纹理 / Lumina 技能表查询等)，
        // 避免首帧在脆弱的启动阶段阻塞游戏线程导致卡死。窗口已在登录后才会打开，
        // 这一守卫是额外保险。
        if (DalamudApi.ClientState?.IsLoggedIn != true)
        {
            ImGui.TextWrapped("游戏加载中，稍候…");
            return;
        }

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
        if (ImGui.BeginTabItem("团队数据"))
        {
            DrawTeamTab();
            ImGui.EndTabItem();
        }
        if (ImGui.BeginTabItem("旋转教练"))
        {
            DrawRotationTab();
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
        ImGui.SetNextItemWidth(110);
        int minCasts = this.config.MinCastsToShow;
        if (ImGui.InputInt("最低释放", ref minCasts, 1, 5))
        {
            this.config.MinCastsToShow = minCasts < 1 ? 1 : minCasts;
            SaveConfig();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(110);
        int maxB = this.config.MaxBattles;
        if (ImGui.InputInt("保留场次", ref maxB, 5, 10))
        {
            maxB = Math.Clamp(maxB, 1, 500);
            this.config.MaxBattles = maxB;
            this.tracker.MaxBattles = maxB;
            SaveConfig();
        }

        // ----- v0.4 团队相关开关 -----
        ImGui.SameLine();
        bool party = this.tracker.EnablePartyMeter;
        if (ImGui.Checkbox("团队采集", ref party))
        {
            this.tracker.EnablePartyMeter = party;
            this.config.EnablePartyMeter = party;
            SaveConfig();
        }

        ImGui.SameLine();
        bool icons = this.config.ShowSkillIcons;
        if (ImGui.Checkbox("技能图标", ref icons))
        {
            this.config.ShowSkillIcons = icons;
            SaveConfig();
        }

        ImGui.SameLine();
        bool buffs = this.tracker.TrackBuffs;
        if (ImGui.Checkbox("记录BUFF", ref buffs))
        {
            this.tracker.TrackBuffs = buffs;
            this.config.TrackBuffs = buffs;
            SaveConfig();
        }

        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        float attrF = (float)this.config.RdpsAttribution;
        if (ImGui.SliderFloat("近似rDPS系数", ref attrF, 0f, 1f, "%.2f"))
        {
            this.config.RdpsAttribution = attrF;
            this.tracker.RdpsAttribution = attrF;
            SaveConfig();
        }
    }

    // ---------- 统计页 ----------

    private void DrawStatsTab()
    {
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
            this.selectedBattleId = "";
        }

        PlayerStats.TryGetRates(out double pCrit, out double pDh);
        double baseCd = this.tracker.GetLuckBaselineCdRate();
        double useCd = baseCd > 0 ? baseCd : pCrit * pDh;
        ImGui.SameLine();
        if (baseCd > 0)
            ImGui.TextUnformatted($"面板理论：暴击 {pCrit * 100:F1}% ｜ 直击 {pDh * 100:F1}% ｜ 个人基线直暴率 {baseCd * 100:F1}%");
        else
            ImGui.TextUnformatted($"面板理论：暴击 {pCrit * 100:F1}% ｜ 直击 {pDh * 100:F1}%（暂无个人历史，运气以面板值为基准）");

        List<SkillStat> rows;
        if (this.scopeIndex == 0)
        {
            rows = this.tracker.SnapshotGrand();
        }
        else if (this.tracker.IsInBattle && this.scopeIndex == options.Count - 1)
        {
            rows = this.tracker.SnapshotCurrent();
        }
        else
        {
            int jobIdx = this.scopeIndex - 1;
            byte jobId = jobIdx >= 0 && jobIdx < jobs.Count ? jobs[jobIdx] : (byte)0;
            rows = this.tracker.SnapshotJob(jobId);
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
        ulong totalDmgSum = filtered.Aggregate(0UL, (a, r) => a + r.DamageSum);
        uint totalDmgMax = filtered.Where(r => r.DamageCount > 0).Aggregate(0u, (m, r) => Math.Max(m, r.DamageMax));
        uint totalDmgMin = filtered.Where(r => r.DamageCount > 0).Aggregate(uint.MaxValue, (m, r) => Math.Min(m, r.DamageMin));
        uint totalDmgCount = (uint)filtered.Sum(r => (long)r.DamageCount);

        if (ImGui.BeginTable("##IsThatACritTable", 12,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX))
        {
            ImGui.TableSetupColumn("技能", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("释放", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("命中", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("最高伤害", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("最低伤害", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("平均伤害", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("直击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("暴击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("直暴", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("期望直暴", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("运气", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("评分", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableHeadersRow();

            double totalScore = LuckRating.ComputeScore(totalHits, totalCd, useCd);
            double totalExpected = LuckRating.ExpectedCritDirect(totalHits, useCd);
            DrawRow("合计", totalCasts, totalHits, totalCrit, totalDh, totalCd, totalExpected, TotalSentinel, pCrit, pDh, totalScore,
                totalDmgSum, totalDmgMax, totalDmgMin, totalDmgCount);

            foreach (var s in filtered)
            {
                double expected = LuckRating.ExpectedCritDirect(s.Hits, useCd);
                double score = LuckRating.ComputeScore(s.Hits, s.CritDirect, useCd);
                DrawRow(LookupName(s.ActionId), s.Casts, s.Hits, s.Crit, s.DirectHit, s.CritDirect, expected, s.ActionId, pCrit, pDh, score,
                    s.DamageSum, s.DamageMax, s.DamageMin, s.DamageCount);
            }

            ImGui.EndTable();
        }
    }

    private void DrawRow(string name, long casts, long hits, long crit, long dh, long cd,
        double expectedCd, uint actionId, double pCrit, double pDh, double score,
        ulong dmgSum, uint dmgMax, uint dmgMin, uint dmgCount)
    {
        ImGui.TableNextRow();

        ImGui.TableNextColumn();
        // 技能图标（v0.4）
        if (this.iconCache != null && this.config.ShowSkillIcons && actionId != TotalSentinel)
        {
            var handle = this.iconCache.GetHandle(actionId);
            if (!handle.IsNull)
            {
                ImGui.Image(handle, new Vector2(20, 20));
                ImGui.SameLine(0, 4);
            }
        }
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

        DrawDamageCells(dmgSum, dmgMax, dmgMin, dmgCount);

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

    private static void DrawDamageCells(ulong dmgSum, uint dmgMax, uint dmgMin, uint dmgCount)
    {
        if (dmgCount == 0)
        {
            ImGui.TableNextColumn(); ImGui.TextUnformatted("—");
            ImGui.TableNextColumn(); ImGui.TextUnformatted("—");
            ImGui.TableNextColumn(); ImGui.TextUnformatted("—");
            return;
        }
        ImGui.TableNextColumn(); ImGui.TextUnformatted(dmgMax.ToString("N0"));
        ImGui.TableNextColumn(); ImGui.TextUnformatted(dmgMin.ToString("N0"));
        ImGui.TableNextColumn(); ImGui.TextUnformatted((dmgSum / (double)dmgCount).ToString("N0"));
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

        ImGui.TextUnformatted($"共记录 {history.Count} 场战斗（保留上限 {this.config.MaxBattles}）。点击一场查看该场的技能明细与时间轴；也可切到「团队数据」页看团队详情。");

        // 列表区限制高度，给下方明细/时间轴留出空间（修复「点开没反应」：原表格吃满整窗高，
        // 导致下方 BeginChild(-1,-1) 明细面板被压成 0 高，看起来像没反应）。
        float listH = Math.Min(ImGui.GetContentRegionAvail().Y * 0.4f, 240f);
        if (ImGui.BeginChild("##HistoryList", new Vector2(-1, listH), true))
        {
            if (ImGui.BeginTable("##BattleList", 6, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX))
            {
                // 本表此前 6 列全为 WidthFixed，导致「开始时间」最左列只能拖短、无法拖长
                // （没有 WidthStretch 列吸收宽度变化）。现把「场地」设为 WidthStretch，使各列可双向拖拽。
                ImGui.TableSetupColumn("开始时间", ImGuiTableColumnFlags.WidthFixed, 150f);
                ImGui.TableSetupColumn("职业", ImGuiTableColumnFlags.WidthFixed, 110f);
                ImGui.TableSetupColumn("场地", ImGuiTableColumnFlags.WidthStretch, 2.0f);
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
                    ImGui.TextUnformatted(string.IsNullOrWhiteSpace(b.ZoneName) ? "未知区域" : b.ZoneName);
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
        }
        ImGui.EndChild();

        var battle = this.selectedBattleId != "" ? this.tracker.GetBattle(this.selectedBattleId) : null;
        if (battle == null)
            return;

        // 单场明细放进可滚动子区域，确保一定可见（修复「详情点不开」的观感问题）
        if (!ImGui.BeginChild("##BattleDetailPane", new Vector2(-1, -1), true))
            return;

        ImGui.Separator();
        double headerBaseCd = this.tracker.GetLuckBaselineCdRate();
        ImGui.TextUnformatted($"【{battle.JobName} @ {battle.ZoneName}】开始 {FormatTime(battle.StartedUnix)} ｜ 时长 {FormatDuration(battle.StartedUnix, battle.EndedUnix)} ｜ 面板理论 暴击 {battle.CritRatePct:F1}% / 直击 {battle.DhRatePct:F1}% ｜ 个人基线直暴率 {headerBaseCd * 100:F1}%");

        double pCrit = battle.CritRatePct / 100.0;
        double pDh = battle.DhRatePct / 100.0;
        double baseCd = this.tracker.GetLuckBaselineCdRate();
        double useCd = baseCd > 0 ? baseCd : pCrit * pDh;
        var rows = battle.Skills.Values
            .Where(r => r.Casts >= this.config.MinCastsToShow)
            .Where(r => !this.config.ShowOnlyDamageSkills || r.Hits > 0)
            .OrderByDescending(r => r.Casts)
            .ToList();

        if (ImGui.BeginTable("##BattleDetail", 12,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX))
        {
            ImGui.TableSetupColumn("技能", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("释放", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("命中", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("最高伤害", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("最低伤害", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("平均伤害", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("直击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("暴击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("直暴", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("期望直暴", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("运气", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("评分", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableHeadersRow();

            foreach (var s in rows)
            {
                double expected = LuckRating.ExpectedCritDirect(s.Hits, useCd);
                double score = LuckRating.ComputeScore(s.Hits, s.CritDirect, useCd);
                DrawRow(LookupName(s.ActionId), s.Casts, s.Hits, s.Crit, s.DirectHit, s.CritDirect, expected, s.ActionId, pCrit, pDh, score,
                    s.DamageSum, s.DamageMax, s.DamageMin, s.DamageCount);
            }
            ImGui.EndTable();
        }

        // 时间轴（本场有数据时直接在此渲染，无需切到「团队数据」页）
        var tlNames = battle.Actors?.ToDictionary(kv => kv.Key, kv => kv.Value.Name) ?? new Dictionary<uint, string>();
        double tlDur = battle.DurationSec > 0 ? battle.DurationSec.Value : 1;
        if (battle.Timeline != null && battle.Timeline.Count > 0)
        {
            DrawTeamTimeline(battle, tlNames, tlDur, "battle:" + this.selectedBattleId);
        }
        else
        {
            ImGui.Separator();
            ImGui.TextWrapped("该记录录制于旧版本或未开启「团队采集」，没有时间轴 / 团队数据。技能明细如上。");
        }

        if (!string.IsNullOrWhiteSpace(battle.Commentary))
        {
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Border, 0xFFC8A000);
            ImGui.TextWrapped(battle.Commentary);
            ImGui.PopStyleColor();
        }

        ImGui.EndChild();
    }

    // ---------- 旋转教练页 ----------
    private void DrawRotationTab()
    {
        var battle = this.tracker.IsInBattle
            ? this.tracker.GetCurrentBattleView()
            : (this.selectedBattleId != "" ? this.tracker.GetBattle(this.selectedBattleId) : null);

        if (battle == null || battle.ActionLog == null || battle.ActionLog.Count == 0)
        {
            ImGui.TextWrapped("还没有出手记录。进入战斗后，「旋转教练」会实时分析你（及队员）实际按下的技能序列，指出发呆、卡手等问题。\n也可在「战斗记录」页选中一场历史战斗，再切回本页查看。");
            return;
        }

        // 聚焦本地玩家；取不到则取出手最多的成员
        uint focus = DalamudApi.ObjectTable?.LocalPlayer?.EntityId ?? 0;
        if (focus == 0 || !battle.ActionLog.ContainsKey(focus))
            focus = battle.ActionLog.OrderByDescending(kv => kv.Value.Count).First().Key;
        string focusName = (battle.Actors != null && battle.Actors.TryGetValue(focus, out var fa)) ? fa.Name : "你";
        double dur = battle.DurationSec ?? 1;

        var rep = RotationAnalysis.Analyze(battle.ActionLog, focus, dur, this.dataManager);

        ImGui.TextUnformatted($"对象：{focusName}　总出手 {rep.TotalActions}　GCD {rep.GcdCount}　能力 {rep.OgcdCount}　APM {rep.Apm:F1}");
        ImGui.SameLine();
        if (rep.GcdCadenceSec > 0)
            ImGui.TextUnformatted($"GCD节奏 ≈ {rep.GcdCadenceSec:F2}s");
        ImGui.TextUnformatted($"发呆 {rep.IdleSec:F1}s（{rep.IdlePct:F0}%）　发呆段 {rep.IdleGaps}　最长停顿 {rep.LongestIdleSec:F1}s");

        if (rep.Issues.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted("待改进（按时间）：");
            foreach (var iss in rep.Issues)
                ImGui.TextColored(new Vector4(1f, 0.55f, 0.45f, 1f), $"  • {iss.Detail}");
        }
        else
        {
            ImGui.Separator();
            ImGui.TextUnformatted("没发现明显发呆/卡手，节奏不错。");
        }

        ImGui.Separator();
        ImGui.TextUnformatted("出手序列（实际按下的技能）：");
        if (ImGui.BeginChild("##RotSeq", new Vector2(-1, -1), true))
        {
            foreach (var a in rep.Sequence)
            {
                ImGui.TextUnformatted($"{(a.TimeMs / 1000.0):F1}s");
                ImGui.SameLine();
                ImGui.TextUnformatted(a.IsGcd ? "[GCD]" : "[能力]");
                ImGui.SameLine();
                ImGui.TextUnformatted(RotationAnalysis.ActionName(a.ActionId, this.dataManager));
            }
        }
        ImGui.EndChild();
    }

    // ---------- 团队数据页 ----------

    private void DrawTeamTab()
    {
        // 节流刷新快照（避免每帧复制时间轴）
        if ((DateTime.Now - this.lastTeamRefresh).TotalMilliseconds > 500 || this.cachedTeamView == null)
        {
            this.lastTeamRefresh = DateTime.Now;
            BattleRecord? fresh = null;
            Dictionary<uint, double>? rdps = null;
            if (!string.IsNullOrEmpty(this.selectedBattleId))
            {
                var h = this.tracker.GetBattle(this.selectedBattleId);
                if (h != null) { fresh = h; rdps = h.ActorRdps; }
            }
            if (fresh == null)
            {
                fresh = this.tracker.GetCurrentBattleView();
                rdps = this.tracker.GetCurrentRdps();
            }
            this.cachedTeamView = fresh;
            this.cachedTeamRdps = rdps;
        }

        var view = this.cachedTeamView;
        if (view == null || view.Actors == null || view.Actors.Count == 0)
        {
            ImGui.TextUnformatted("当前没有团队战斗数据。");
            if (!this.tracker.EnablePartyMeter)
                ImGui.TextUnformatted("（团队数据采集已关闭，可在上方工具栏勾选「团队采集」开启。）");
            else
                ImGui.TextUnformatted("进入副本 / 战斗后，这里会实时显示本队全员 DPS、时间轴与 BUFF。\n也可在「战斗记录」页选中一场战斗，再切回本页查看其历史团队数据。");
            return;
        }

        string source = string.IsNullOrEmpty(this.selectedBattleId) ? "当前战斗（实时）" : $"历史战斗 {FormatTime(view.StartedUnix)}";
        ImGui.TextUnformatted($"数据来源：{source}");
        ImGui.SameLine();
        ImGui.TextUnformatted($"｜ {view.JobName} @ {view.ZoneName} ｜ 时长 {FormatDuration(0, (long)(view.DurationSec ?? 0))} ｜ 面板理论 暴击 {view.CritRatePct:F1}% / 直击 {view.DhRatePct:F1}%");

        var names = view.Actors.ToDictionary(kv => kv.Key, kv => kv.Value.Name);
        double dur = view.DurationSec > 0 ? view.DurationSec.Value : 1;
        ulong totalDmg = view.Actors.Values.Aggregate(0UL, (a, s) => a + s.DamageSum);

        ImGui.Separator();
        DrawTeamDpsTable(view, this.cachedTeamRdps, names, totalDmg, dur);

        // 选中队员的技能明细
        if (this.selectedActorId != 0 && view.Actors.TryGetValue(this.selectedActorId, out var selActor) && selActor.Skills.Count > 0)
        {
            ImGui.Separator();
            ImGui.TextUnformatted($"【{selActor.Name}（{selActor.JobName}）】技能明细");
            DrawActorSkills(selActor, view.CritRatePct / 100.0, view.DhRatePct / 100.0);
        }

        if (view.Targets != null && view.Targets.Count > 0)
        {
            ImGui.Separator();
            DrawTeamMultiTarget(view, names);
        }

        if (view.Buffs != null && view.Buffs.Count > 0)
        {
            ImGui.Separator();
            DrawTeamBuffs(view, names, dur);
        }

        if (view.Timeline != null && view.Timeline.Count > 0)
        {
            ImGui.Separator();
            DrawTeamTimeline(view, names, dur, string.IsNullOrEmpty(this.selectedBattleId) ? "current" : "battle:" + this.selectedBattleId);
        }

        if (!string.IsNullOrWhiteSpace(view.Commentary))
        {
            ImGui.Separator();
            ImGui.PushStyleColor(ImGuiCol.Border, 0xFFC8A000);
            ImGui.TextWrapped(view.Commentary);
            ImGui.PopStyleColor();
        }
    }

    private void DrawTeamDpsTable(BattleRecord view, Dictionary<uint, double>? rdps, Dictionary<uint, string> names, ulong totalDmg, double dur)
    {
        ImGui.TextUnformatted("队员 DPS 排行（点击队员查看其技能明细）");
        var actors = view.Actors!.Values.OrderByDescending(a => a.DamageSum).ToList();

        if (ImGui.BeginTable("##TeamDps", 8,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY))
        {
            ImGui.TableSetupColumn("队员", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("职业", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("伤害", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("占比", ImGuiTableColumnFlags.WidthFixed, 150f);
            ImGui.TableSetupColumn("aDPS", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableSetupColumn("近似rDPS", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("命中", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("最高一击", ImGuiTableColumnFlags.WidthFixed, 100f);
            ImGui.TableHeadersRow();

            foreach (var a in actors)
            {
                ImGui.TableNextRow();
                bool selected = a.EntityId == this.selectedActorId;

                ImGui.TableNextColumn();
                if (ImGui.Selectable(a.Name + (a.IsLocal ? "（你）" : ""), selected))
                    this.selectedActorId = selected ? 0u : a.EntityId; // 再次点击同一队员可收起明细

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(a.JobName);

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(a.DamageSum.ToString("N0"));

                ImGui.TableNextColumn();
                double share = totalDmg > 0 ? (double)a.DamageSum / totalDmg : 0;
                ImGui.PushStyleColor(ImGuiCol.PlotHistogram, 0xFF20A0FF);
                ImGui.ProgressBar((float)share, new Vector2(-1, 0), $"{share * 100:F1}%");
                ImGui.PopStyleColor();

                ImGui.TableNextColumn();
                double adps = BattleAnalysis.Adps(a.DamageSum, dur);
                ImGui.TextUnformatted(adps.ToString("N0"));

                ImGui.TableNextColumn();
                double r = rdps != null && rdps.TryGetValue(a.EntityId, out var rv) ? rv : adps;
                ImGui.TextUnformatted(r.ToString("N0"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(a.Hits.ToString("N0"));

                ImGui.TableNextColumn();
                ImGui.TextUnformatted(a.DamageMax.ToString("N0"));
            }
            ImGui.EndTable();
        }
    }

    private void DrawActorSkills(ActorStat actor, double pCrit, double pDh)
    {
        double baseCd = this.tracker.GetLuckBaselineCdRate();
        double useCd = baseCd > 0 ? baseCd : pCrit * pDh;
        var rows = actor.Skills.Values
            .Where(s => s.Casts >= this.config.MinCastsToShow)
            .Where(s => !this.config.ShowOnlyDamageSkills || s.Hits > 0)
            .OrderByDescending(s => s.Casts)
            .ToList();

        if (ImGui.BeginTable("##ActorSkills", 12,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollY | ImGuiTableFlags.ScrollX))
        {
            ImGui.TableSetupColumn("技能", ImGuiTableColumnFlags.WidthStretch, 2.4f);
            ImGui.TableSetupColumn("释放", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("命中", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableSetupColumn("最高伤害", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("最低伤害", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("平均伤害", ImGuiTableColumnFlags.WidthFixed, 92f);
            ImGui.TableSetupColumn("直击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("暴击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("直暴", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("期望直暴", ImGuiTableColumnFlags.WidthFixed, 90f);
            ImGui.TableSetupColumn("运气", ImGuiTableColumnFlags.WidthFixed, 170f);
            ImGui.TableSetupColumn("评分", ImGuiTableColumnFlags.WidthFixed, 56f);
            ImGui.TableHeadersRow();

            foreach (var s in rows)
            {
                double expected = LuckRating.ExpectedCritDirect(s.Hits, useCd);
                double score = LuckRating.ComputeScore(s.Hits, s.CritDirect, useCd);
                DrawRow(LookupName(s.ActionId), s.Casts, s.Hits, s.Crit, s.DirectHit, s.CritDirect, expected, s.ActionId, pCrit, pDh, score,
                    s.DamageSum, s.DamageMax, s.DamageMin, s.DamageCount);
            }
            ImGui.EndTable();
        }
    }

    private void DrawTeamMultiTarget(BattleRecord view, Dictionary<uint, string> names)
    {
        ImGui.TextUnformatted("多目标统计（同一场战斗中对不同目标的伤害分解）");
        var targets = view.Targets!.Values.OrderByDescending(t => t.DamageTaken).ToList();

        if (ImGui.BeginTable("##TeamTargets", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX))
        {
            ImGui.TableSetupColumn("目标", ImGuiTableColumnFlags.WidthStretch, 2.0f);
            ImGui.TableSetupColumn("承受伤害", ImGuiTableColumnFlags.WidthFixed, 120f);
            ImGui.TableSetupColumn("命中", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("最高一击", ImGuiTableColumnFlags.WidthFixed, 110f);
            ImGui.TableSetupColumn("来源分解", ImGuiTableColumnFlags.WidthStretch, 3.0f);
            ImGui.TableHeadersRow();

            foreach (var t in targets)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(t.TargetName) ? $"目标 {t.TargetEntityId}" : t.TargetName);
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(t.DamageTaken.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(t.Hits.ToString("N0"));
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(t.MaxHit.ToString("N0"));
                ImGui.TableNextColumn();
                var parts = t.ByActor.OrderByDescending(kv => kv.Value).ToList();
                bool first = true;
                foreach (var kv in parts)
                {
                    if (!first) ImGui.SameLine(0, 6);
                    first = false;
                    string an = names.TryGetValue(kv.Key, out var n) ? n : $"实体 {kv.Key}";
                    double pct = t.DamageTaken > 0 ? (double)kv.Value / t.DamageTaken * 100.0 : 0;
                    ImGui.TextUnformatted($"{an}: {kv.Value:N0} ({pct:F0}%)");
                }
            }
            ImGui.EndTable();
        }
    }

    private void DrawTeamBuffs(BattleRecord view, Dictionary<uint, string> names, double dur)
    {
        ImGui.TextUnformatted("BUFF（团辅 / 增伤）时间轴");
        var buffs = view.Buffs!.OrderBy(b => b.StartMs).ToList();
        double durMs = dur * 1000.0;
        if (durMs <= 0) durMs = 1;

        float barW = 220f;
        if (ImGui.BeginTable("##TeamBuffs", 5,
                ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.ScrollX))
        {
            ImGui.TableSetupColumn("来源", ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableSetupColumn("受影响者", ImGuiTableColumnFlags.WidthStretch, 1.6f);
            ImGui.TableSetupColumn("BUFF", ImGuiTableColumnFlags.WidthStretch, 1.8f);
            ImGui.TableSetupColumn("时长", ImGuiTableColumnFlags.WidthFixed, 70f);
            ImGui.TableSetupColumn("时间轴", ImGuiTableColumnFlags.WidthFixed, barW);
            ImGui.TableHeadersRow();

            foreach (var b in buffs)
            {
                ImGui.TableNextRow();
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(names.TryGetValue(b.SourceEntityId, out var s) ? s : $"实体 {b.SourceEntityId}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(names.TryGetValue(b.ActorEntityId, out var a) ? a : $"实体 {b.ActorEntityId}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted(string.IsNullOrWhiteSpace(b.StatusName) ? $"状态 {b.StatusId}" : b.StatusName);
                ImGui.TableNextColumn();
                double len = (b.EndMs - b.StartMs) / 1000.0;
                ImGui.TextUnformatted($"{len:F1}s");
                ImGui.TableNextColumn();
                float frac = (float)Math.Min(1.0, Math.Max(0.0, (b.EndMs - b.StartMs) / durMs));
                float x0 = (float)Math.Min(1.0, b.StartMs / durMs);
                var dl = ImGui.GetWindowDrawList();
                var p = ImGui.GetCursorScreenPos();
                dl.AddRectFilled(p, new Vector2(p.X + barW, p.Y + 12f), 0x33000000);
                dl.AddRectFilled(new Vector2(p.X + barW * x0, p.Y),
                    new Vector2((float)(p.X + barW * Math.Min(1.0, x0 + frac)), p.Y + 12f), 0xFF20C0A0);
                ImGui.Dummy(new Vector2(barW, 12f));
            }
            ImGui.EndTable();
        }
    }

    private void DrawTeamTimeline(BattleRecord view, Dictionary<uint, string> names, double dur, string viewKey)
    {
        // 切换战斗/视图时重置平移与缩放
        if (tlViewKey != viewKey)
        {
            tlViewKey = viewKey;
            tlPanX = 0f;
            tlScale = 1f;
        }

        ImGui.TextUnformatted("伤害时间轴（拖拽平移 · 滚轮缩放 · 每人均一行泳道；红=暴击 蓝=直击 金=直暴 灰=普通）");
        ImGui.SameLine();
        if (ImGui.Button("重置视图"))
        {
            tlPanX = 0f;
            tlScale = 1f;
        }

        var tl = view.Timeline!;
        double durMs = dur * 1000.0;
        if (durMs <= 0) durMs = 1;

        // 聚合每个施放者的事件
        var byActor = new Dictionary<uint, List<DamageEvent>>();
        foreach (var e in tl)
        {
            if (!byActor.TryGetValue(e.ActorEntityId, out var list))
                byActor[e.ActorEntityId] = list = new List<DamageEvent>();
            list.Add(e);
        }

        // 更大的泳道与图标
        int maxIconsPerLane = 200;
        float laneH = 36f;
        float iconSize = 28f;
        float laneGap = 6f;
        float headerH = 20f;
        float nameColW = 150f;

        int laneCount = byActor.Count;
        float innerH = headerH + laneCount * (laneH + laneGap) + 8f;

        float availW = ImGui.GetContentRegionAvail().X;
        // NoScrollWithMouse + NoScrollbar：阻断滚轮冒泡到父窗口（否则缩放时父窗口/明细面板会跟着滚动）
        if (ImGui.BeginChild("##TLCanvas", new Vector2(availW, Math.Max(innerH, 200f)), true,
                ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoScrollWithMouse | ImGuiWindowFlags.NoMove))
        {
            var canvasMin = ImGui.GetCursorScreenPos();
            var canvasAvail = ImGui.GetContentRegionAvail();
            float canvasW = canvasAvail.X;
            float canvasH = canvasAvail.Y;
            var canvasMax = new Vector2(canvasMin.X + canvasW, canvasMin.Y + canvasH);

            // 用一个覆盖整块画布的隐形按钮"吃掉"鼠标点击/拖拽，避免被父窗口当作移动窗口
            ImGui.InvisibleButton("##TLCanvasBtn", new Vector2(Math.Max(1f, canvasW), Math.Max(1f, canvasH)));
            bool hovering = ImGui.IsItemHovered();
            bool active = ImGui.IsItemActive();

            float trackX0 = canvasMin.X + nameColW;
            float trackX1 = canvasMin.X + canvasW - 2f;
            float trackW = Math.Max(1f, trackX1 - trackX0);
            float pxPerMsBase = trackW / (float)durMs;   // scale=1 时整场恰好铺满
            float pxPerMs = pxPerMsBase * tlScale;

            // —— 交互：滚轮缩放（以光标为锚点）+ 左键拖拽平移 ——
            var io = ImGui.GetIO();
            if (hovering && io.MouseWheel != 0f)
            {
                float mx = io.MousePos.X;
                float relX = mx - (trackX0 + tlPanX);          // 当前光标在内容坐标中的像素位置
                double tAtCursor = relX / pxPerMs;             // 对应时间点(ms)
                float newScale = Math.Clamp(tlScale * (io.MouseWheel > 0 ? 1.12f : 1f / 1.12f), 0.2f, 24f);
                float newPxPerMs = pxPerMsBase * newScale;
                tlPanX = mx - trackX0 - (float)(tAtCursor * newPxPerMs);
                tlScale = newScale;
            }
            if (active && ImGui.IsMouseDragging(ImGuiMouseButton.Left, 1f))
            {
                tlPanX += io.MouseDelta.X;
            }

            var dl = ImGui.GetWindowDrawList();
            dl.PushClipRect(canvasMin, canvasMax, true);

            // 顶部标尺：秒数网格 + 时间标签
            float rulerY = canvasMin.Y + 2f;
            int gridN = 12;
            for (int g = 0; g <= gridN; g++)
            {
                double tMs = durMs * g / gridN;
                float x = trackX0 + tlPanX + (float)(tMs * pxPerMs);
                if (x < trackX0 - 1f || x > trackX1 + 1f) continue;
                dl.AddLine(new Vector2(x, rulerY), new Vector2(x, canvasMax.Y - 2f), 0x22000000, 1f);
                dl.AddText(new Vector2(x + 2f, rulerY), 0xFF909090, $"{tMs / 1000.0:F1}s");
            }
            dl.AddLine(new Vector2(trackX0, rulerY + headerH - 4f), new Vector2(trackX1, rulerY + headerH - 4f), 0x33000000, 1f);

            float y = canvasMin.Y + headerH;
            foreach (var kv in byActor.OrderBy(k => names.TryGetValue(k.Key, out var n) ? n : ""))
            {
                uint aid = kv.Key;
                var events = kv.Value;
                string raw = names.TryGetValue(aid, out var n2) ? n2 : $"实体 {aid}";
                string disp = raw.Length > 12 ? raw.Substring(0, 11) + "…" : raw;

                // 左侧名字（裁剪在泳道左侧，不侵入轨道）
                dl.AddText(new Vector2(canvasMin.X + 4f, y + laneH / 2f - 6f), 0xFFFFFFFF, disp);
                // 泳道背景
                dl.AddRectFilled(new Vector2(trackX0, y), new Vector2(trackX1, y + laneH), 0x33000000);

                dl.PushClipRect(new Vector2(trackX0, y), new Vector2(trackX1, y + laneH), true);
                float rowY = y + (laneH - iconSize) / 2f;
                int stepEv = events.Count > maxIconsPerLane ? events.Count / maxIconsPerLane : 1;
                int idx = 0;
                float prevX = -1e9f;
                foreach (var e in events)
                {
                    if (idx++ % stepEv != 0) continue;
                    float x = trackX0 + tlPanX + (float)(e.TimeMs * pxPerMs);
                    uint col = e.CritDirect ? 0xFF00C8FF : e.Crit ? 0xFF3030FF : e.DirectHit ? 0xFFE08000 : 0xFFC0C0C0;

                    bool drew = false;
                    if (this.iconCache != null && this.config.ShowSkillIcons && e.ActionId != 0 &&
                        x - prevX >= iconSize * 0.55f)
                    {
                        var h = this.iconCache.GetHandle(e.ActionId);
                        if (!h.IsNull)
                        {
                            dl.AddImage(h, new Vector2(x, rowY), new Vector2(x + iconSize, rowY + iconSize));
                            dl.AddRect(new Vector2(x, rowY), new Vector2(x + iconSize, rowY + iconSize), col, 0f, ImDrawFlags.None, 2f);
                            prevX = x;
                            drew = true;
                        }
                    }
                    if (!drew)
                    {
                        float bx = x - 1.5f;
                        if (bx < trackX0) bx = trackX0;
                        dl.AddRectFilled(new Vector2(bx, y + 4f), new Vector2(bx + 3f, y + laneH - 4f), col);
                        prevX = x;
                    }
                }
                dl.PopClipRect();

                y += laneH + laneGap;
            }

            dl.PopClipRect();
        }
        ImGui.EndChild();

        ImGui.TextUnformatted($"当前缩放 {tlScale:F1}x ｜ 拖拽可平移，滚轮缩放，点「重置视图」复位");
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
