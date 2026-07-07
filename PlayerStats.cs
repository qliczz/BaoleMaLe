namespace BaoleMaLe;

using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

/// <summary>
/// 从角色面板（游戏内真实数据）读取暴击率 / 直击率，不再让用户手动填写。
///
/// 数据来源（与 CharacterPanelRefined 完全一致，已反编译确认）：
///   UIState.Instance() -> PlayerState.Attributes[(int)PlayerAttribute.CriticalHit / DirectHitRate]
/// 再用游戏自身的换算公式把"属性值"变成"几率%"：
///   暴击率 = floor(200 * (暴击属性 - Sub) / Div + 50) / 1000
///   直击率 = floor(550 * (直击属性 - Sub) / Div) / 1000
/// 其中 Sub / Div 来自当前等级的 Level 表（这里内置一份 1–100 的快照，等价于游戏 Level 表）。
/// 返回的 rate 是小数（0.25 = 25%）。
/// </summary>
public static class PlayerStats
{
    // (Main, Sub, Div) —— 复制自 CharacterPanelRefined 的 LevelModifiers.LevelTable（即游戏 Level 表）。
    private static readonly Dictionary<int, (int Main, int Sub, int Div)> LevelTable = new()
    {
        { 1, (20, 56, 56) }, { 2, (21, 57, 57) }, { 3, (22, 60, 60) }, { 4, (24, 62, 62) },
        { 5, (26, 65, 65) }, { 6, (27, 68, 68) }, { 7, (29, 70, 70) }, { 8, (31, 73, 73) },
        { 9, (33, 76, 76) }, { 10, (35, 78, 78) }, { 11, (36, 82, 82) }, { 12, (38, 85, 85) },
        { 13, (41, 89, 89) }, { 14, (44, 93, 93) }, { 15, (46, 96, 96) }, { 16, (49, 100, 100) },
        { 17, (52, 104, 104) }, { 18, (54, 109, 109) }, { 19, (57, 113, 113) }, { 20, (60, 116, 116) },
        { 21, (63, 122, 122) }, { 22, (67, 127, 127) }, { 23, (71, 133, 133) }, { 24, (74, 138, 138) },
        { 25, (78, 144, 144) }, { 26, (81, 150, 150) }, { 27, (85, 155, 155) }, { 28, (89, 162, 162) },
        { 29, (92, 168, 168) }, { 30, (97, 173, 173) }, { 31, (101, 181, 181) }, { 32, (106, 188, 188) },
        { 33, (110, 194, 194) }, { 34, (115, 202, 202) }, { 35, (119, 209, 209) }, { 36, (124, 215, 215) },
        { 37, (128, 223, 223) }, { 38, (134, 229, 229) }, { 39, (139, 236, 236) }, { 40, (144, 244, 244) },
        { 41, (150, 253, 253) }, { 42, (155, 263, 263) }, { 43, (161, 272, 272) }, { 44, (166, 283, 283) },
        { 45, (171, 292, 292) }, { 46, (177, 302, 302) }, { 47, (183, 311, 311) }, { 48, (189, 322, 322) },
        { 49, (196, 331, 331) }, { 50, (202, 341, 341) }, { 51, (204, 342, 366) }, { 52, (205, 344, 392) },
        { 53, (207, 345, 418) }, { 54, (209, 346, 444) }, { 55, (210, 347, 470) }, { 56, (212, 349, 496) },
        { 57, (214, 350, 522) }, { 58, (215, 351, 548) }, { 59, (217, 352, 574) }, { 60, (218, 354, 600) },
        { 61, (224, 355, 630) }, { 62, (228, 356, 660) }, { 63, (236, 357, 690) }, { 64, (244, 358, 720) },
        { 65, (252, 359, 750) }, { 66, (260, 360, 780) }, { 67, (268, 361, 810) }, { 68, (276, 362, 840) },
        { 69, (284, 363, 870) }, { 70, (292, 364, 900) }, { 71, (296, 365, 940) }, { 72, (300, 366, 980) },
        { 73, (305, 367, 1020) }, { 74, (310, 368, 1060) }, { 75, (315, 370, 1100) }, { 76, (320, 372, 1140) },
        { 77, (325, 374, 1180) }, { 78, (330, 376, 1220) }, { 79, (335, 378, 1260) }, { 80, (340, 380, 1300) },
        { 81, (345, 382, 1360) }, { 82, (350, 384, 1420) }, { 83, (355, 386, 1480) }, { 84, (360, 388, 1540) },
        { 85, (365, 390, 1600) }, { 86, (370, 392, 1660) }, { 87, (375, 394, 1720) }, { 88, (380, 396, 1780) },
        { 89, (385, 398, 1840) }, { 90, (390, 400, 1900) }, { 91, (395, 402, 1988) }, { 92, (400, 404, 2076) },
        { 93, (405, 406, 2164) }, { 94, (410, 408, 2252) }, { 95, (415, 410, 2340) }, { 96, (420, 412, 2428) },
        { 97, (425, 414, 2516) }, { 98, (430, 416, 2604) }, { 99, (435, 418, 2692) }, { 100, (440, 420, 2780) },
    };

    /// <summary>读不到（未在游戏中 / 数据未加载）时返回 false，调用方降级处理。</summary>
    public static unsafe bool TryGetRates(out double critRate, out double dhRate)
    {
        critRate = 0;
        dhRate = 0;
        try
        {
            var ui = UIState.Instance();
            if (ui == null)
                return false;

            var ps = &ui->PlayerState;
            int level = ps->CurrentLevel;
            if (level <= 0)
                return false;

            var mod = ResolveLevel(level);

            int critStat = ps->Attributes[(int)PlayerAttribute.CriticalHit];
            int dhStat = ps->Attributes[(int)PlayerAttribute.DirectHitRate];

            critRate = Math.Floor(200.0 * (critStat - mod.Sub) / mod.Div + 50.0) / 1000.0;
            dhRate = Math.Floor(550.0 * (dhStat - mod.Sub) / mod.Div) / 1000.0;
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前职业的 ClassJob RowId（PlayerState.CurrentClassJobId）。读不到返回 0。</summary>
    public static unsafe byte GetCurrentClassJobId()
    {
        try
        {
            var ui = UIState.Instance();
            return ui == null ? (byte)0 : ui->PlayerState.CurrentClassJobId;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>当前是否处于战斗中（用于自动分段一场战斗）。</summary>
    public static unsafe bool IsLocalPlayerInCombat()
    {
        try
        {
            var obj = DalamudApi.ObjectTable?.LocalPlayer;
            if (obj == null)
                return false;
            var c = (FFXIVClientStructs.FFXIV.Client.Game.Character.Character*)obj.Address;
            return c->InCombat;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>当前所在地区的显示名（副本名 / 地图名）。读不到时返回 "未知区域"。</summary>
    public static string GetCurrentZoneName()
    {
        try
        {
            var cs = DalamudApi.ClientState;
            if (cs == null)
                return "未知区域";
            uint tid = cs.TerritoryType;
            var sheet = DalamudApi.DataManager?.GetExcelSheet<TerritoryType>();
            var row = sheet?.GetRow(tid);
            if (row == null)
                return "未知区域";
            var r = row.Value;

            string? TryName(Func<string> f)
            {
                try
                {
                    var s = f();
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                }
                catch
                {
                    return null;
                }
            }

            // 优先用副本/任务名（ContentFinderCondition），其次地图分区名，最后地区本名。
            return TryName(() => r.ContentFinderCondition.Value.Name.ToString())
                   ?? TryName(() => r.PlaceNameZone.Value.Name.ToString())
                   ?? TryName(() => r.Name.ToString())
                   ?? $"区域 {tid}";
        }
        catch
        {
            return "未知区域";
        }
    }

    private static (int Main, int Sub, int Div) ResolveLevel(int level)
    {
        if (LevelTable.TryGetValue(level, out var m))
            return m;
        // 超出内置范围时，钳到最近的边界档，避免崩溃。
        if (level < 1)
            return LevelTable[1];
        return LevelTable[100];
    }
}
