namespace BaoleMaLe;

/// <summary>
/// 插件入口。通过构造函数注入 Dalamud 服务，装配 CombatTracker 与主窗口。
/// </summary>
public class Plugin : IDalamudPlugin
{
    private readonly IDalamudPluginInterface pluginInterface;
    private readonly ICommandManager commandManager;
    private readonly IDataManager dataManager;
    private readonly IPluginLog log;
    private readonly IFramework framework;
    private readonly IClientState clientState;

    private readonly Configuration config;
    private readonly CombatTracker tracker;
    private readonly IconCache iconCache;
    private readonly WindowSystem windowSystem;
    private readonly Windows.MainWindow mainWindow;

    // 防止开机/读条阶段就渲染重 UI 卡死游戏：窗口不立即恢复开启，
    // 而是等游戏登录完成后再恢复；autoOpenDone 处理"加载时已在游戏内"的一次性恢复。
    private bool autoOpenDone;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IObjectTable objects,
        IDataManager dataManager,
        IPluginLog log,
        IFramework framework,
        IClientState clientState,
        IPartyList partyList,
        ITextureProvider textureProvider)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.dataManager = dataManager;
        this.log = log;
        this.framework = framework;
        this.clientState = clientState;

        DalamudApi.ObjectTable = objects;
        DalamudApi.DataManager = dataManager;
        DalamudApi.ClientState = clientState;

        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var configDir = pluginInterface.GetPluginConfigDirectory();
        this.tracker = new CombatTracker(interop, sigScanner, objects, dataManager, log, configDir, this.config.MaxBattles, partyList);
        this.tracker.IsTrackingEnabled = this.config.EnableTracking;
        this.tracker.EnablePartyMeter = this.config.EnablePartyMeter;
        this.tracker.TrackBuffs = this.config.TrackBuffs;
        this.tracker.RdpsAttribution = this.config.RdpsAttribution;
        this.tracker.TimelineMaxEvents = this.config.TimelineMaxEvents;
        this.tracker.Enable();

        this.iconCache = new IconCache(textureProvider);

        this.mainWindow = new Windows.MainWindow(this.tracker, this.config, this.dataManager, this.iconCache);
        this.mainWindow.ConfigChanged += (_, _) => SaveConfig();

        this.windowSystem = new WindowSystem("Is that a crit？");
        this.windowSystem.AddWindow(this.mainWindow);
        // 关键修复：启动/读条阶段不立即打开窗口（否则重 UI 在脆弱的首帧渲染会卡死游戏）。
        // 改为等游戏登录完成(OnClientLogin)后再恢复用户记忆的开关。
        this.mainWindow.IsOpen = false;

        this.pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi += ToggleMain;

        this.framework.Update += this.OnFrameworkUpdate;
        this.clientState.Login += this.OnClientLogin;

        commandManager.AddHandler("/blm", new CommandInfo(OnCommand)
        {
            HelpMessage = "Is that a crit？ —— 打开统计窗口；/blm reset 清空所有统计与历史",
        });
        // 兼容旧指令
        commandManager.AddHandler("/isthatacrit", new CommandInfo(OnCommand)
        {
            HelpMessage = "Is that a crit？ —— 同 /blm（旧指令，保留兼容）",
        });

        this.log.Info("[Is that a crit？] 插件已加载。");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        this.tracker.Tick();
        // 插件在已进入游戏时被加载(如 /xlplugins 重载)，Login 事件不会再触发，
        // 因此在这里等游戏就绪后一次性恢复用户记忆的窗口开关。
        if (!this.autoOpenDone && this.clientState.IsLoggedIn)
        {
            this.autoOpenDone = true;
            if (this.config.MainWindowOpen)
                this.mainWindow.IsOpen = true;
        }
    }

    private void OnClientLogin()
    {
        // 游戏登录完成后再恢复主窗口，避免启动/读条阶段渲染重 UI 卡死游戏。
        this.autoOpenDone = true;
        if (this.config.MainWindowOpen)
            this.mainWindow.IsOpen = true;
    }

    private void OnCommand(string command, string arguments)
    {
        var arg = arguments.Trim().ToLowerInvariant();
        if (arg == "reset")
        {
            this.tracker.Reset();
            return;
        }

        this.mainWindow.Toggle();
    }

    private void ToggleMain() => this.mainWindow.Toggle();

    private void SaveConfig()
    {
        this.config.MainWindowOpen = this.mainWindow.IsOpen;
        this.pluginInterface.SavePluginConfig(this.config);
    }

    public void Dispose()
    {
        this.framework.Update -= this.OnFrameworkUpdate;
        this.clientState.Login -= this.OnClientLogin;
        this.commandManager.RemoveHandler("/blm");
        this.commandManager.RemoveHandler("/isthatacrit");
        this.pluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        this.windowSystem.RemoveAllWindows();
        this.tracker.Dispose();
        this.iconCache.Dispose();
    }
}
