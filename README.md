# Is that a crit？ (BaoleMaLe)

一个 FFXIV (国服 XIVLauncherCN / 国际服) 的 Dalamud 插件，统计**你打出的伤害技能**，
并像 FFLogs 一样给每个技能的**直暴（暴击 + 直击）运气**打个分、配上色。

> "Is that a crit？"

## 功能

- 实时统计**每个伤害技能**的：
  - **释放次数** —— 你打出了多少次这个技能
  - **命中次数** —— 实际造成多少次伤害（多段 / AoE 会多于释放次数）
  - **直击次数与几率** （直击率）
  - **暴击次数与几率** （暴击率）
  - **直暴次数与几率** （同时暴击 + 直击）
- **暴击率 / 直击率自动取自角色面板**：不再需要手动填，插件直接读游戏内 `UIState.PlayerState`
  的属性值并用游戏自身公式换算（与 CharacterPanelRefined 同源），理论几率永远是最新的。
- **FFLogs 风格运气评分**：用灰 → 绿 → 蓝 → 紫 → 橙 → 粉 的渐变颜色，
  直观显示某个技能的"直暴运气"是欧皇还是非酋。评分以**面板理论几率**为零假设。
- **自动分职业统计 + 战斗记录库**：
  - 监听 `Character.InCombat`，自动把数据切成"一场场战斗"；
  - 同时维护**全职业总计**与**分职业**累计；
  - "战斗记录"页可查看过去 N 场战斗（时间、职业、时长、整场运气），点开任一场看逐技能明细。
- 命令：
  - `/isthatacrit` —— 打开 / 关闭统计窗口
  - `/isthatacrit reset` —— 清空全部统计与历史
- 窗口内配置（自动保存）：启用统计 / 仅显示造成伤害的技能 / 最低释放次数 / 保留战斗场次。

## 运气评分怎么算

FFXIV 里"暴击"和"直击"是**两次独立判定**，所以一个技能打出"直暴"的理论概率：

```
P(直暴) = P(暴击) × P(直击)
```

其中 P(暴击)、P(直击) 直接来自角色面板的真实理论几率（不再用手动填或实测边际率近似）。
插件比较"实际直暴次数"与"期望直暴次数（命中数 × P暴击 × P直击）"的偏差，
以二项分布标准差为单位，把偏差映射到 **0–100 分**：约 50 分=正常，越高越欧、越低越非。
分数再用 FFLogs 同款配色呈现，并在表格里给出"期望直暴"列做参照。

> 小样本（释放次数少）时分数波动大，这正是"运气"的体现；样本越多越回归理论值。

## 目录结构

```
BaoleMaLe/
├─ BaoleMaLe.csproj        # Dalamud.NET.Sdk 15 工程
├─ plugin.json             # Dalamud 插件清单
├─ GlobalUsings.cs
├─ Plugin.cs               # 入口：服务注入、命令、窗口装配、逐帧战斗分段
├─ Configuration.cs        # 配置（持久化）
├─ CombatTracker.cs        # 核心：Receive 钩子 + 战斗分段 + 分职业/总计/历史聚合 + 落盘
├─ PlayerStats.cs          # 从角色面板读取暴击/直击理论几率（基于 CharacterPanelRefined 的公式）
├─ LuckRating.cs           # 直暴运气评分 + FFLogs 配色
├─ DalamudApi.cs           # 静态服务访问器
├─ Windows/
│  └─ MainWindow.cs        # ImGui：统计 Tab + 战斗记录 Tab
└─ repo/
   └─ pluginmaster.json    # 第三方裤链模板
```

## 编译（本地）

需要一个已安装 Dalamud 的 FFXIV 客户端，取其 `DALAMUD_HOME` 指到 Dalamud 程序集目录
（通常在 `XIVLauncherCN/addon/Hooks/<版本号>/`）。

```bash
export DALAMUD_HOME="C:/Users/<你>/AppData/Roaming/XIVLauncherCN/addon/Hooks/26-06-27-01"
dotnet build BaoleMaLe.csproj -c Release
```

产物在 `bin/BaoleMaLe/latest.zip`，丢进 Dalamud 的 `devPlugins` 目录或走第三方裤链安装。

## 重要提示（钩子签名）

伤害数据来自对 `ActionEffectHandler.Receive` 的**签名扫描钩子**
（签名见 `CombatTracker.cs` 的 `ReceiveSig`，对应 FFXIV 7.x / Dawntrail 的战斗效果入口）。
每个大版本更新后该签名可能变动；若插件加载后统计一直为 0，
请查看 Dalamud 日志中 `[Is that a crit？]` 的诊断行（会显示钩子是否生效、来源是否命中本地玩家），
并按新版本更新 `ReceiveSig`。

## 发布第三方裤链（GitHub Actions）

仓库内含 `repo/pluginmaster.json` 模板。参照 FFXIV Dalamud 插件标准发布流程：
打 `v*` tag 触发 Actions，用 `Dalamud.NET.Sdk` 编译并生成 `latest.zip`，
替换模板里的 `你的名字` / 版本号后发布即可。
