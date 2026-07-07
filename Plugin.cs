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

    private readonly Configuration config;
    private readonly CombatTracker tracker;
    private readonly WindowSystem windowSystem;
    private readonly Windows.MainWindow mainWindow;

    public Plugin(
        IDalamudPluginInterface pluginInterface,
        ICommandManager commandManager,
        IGameInteropProvider interop,
        ISigScanner sigScanner,
        IObjectTable objects,
        IDataManager dataManager,
        IPluginLog log,
        IFramework framework)
    {
        this.pluginInterface = pluginInterface;
        this.commandManager = commandManager;
        this.dataManager = dataManager;
        this.log = log;
        this.framework = framework;

        DalamudApi.ObjectTable = objects;
        DalamudApi.DataManager = dataManager;

        this.config = pluginInterface.GetPluginConfig() as Configuration ?? new Configuration();

        var configDir = pluginInterface.GetPluginConfigDirectory();
        this.tracker = new CombatTracker(interop, sigScanner, objects, dataManager, log, configDir, this.config.MaxBattles);
        this.tracker.IsTrackingEnabled = this.config.EnableTracking;
        this.tracker.Enable();

        this.mainWindow = new Windows.MainWindow(this.tracker, this.config, this.dataManager);
        this.mainWindow.ConfigChanged += (_, _) => SaveConfig();

        this.windowSystem = new WindowSystem("Is that a crit？");
        this.windowSystem.AddWindow(this.mainWindow);
        this.mainWindow.IsOpen = this.config.MainWindowOpen;

        this.pluginInterface.UiBuilder.Draw += this.windowSystem.Draw;
        this.pluginInterface.UiBuilder.OpenMainUi += ToggleMain;

        this.framework.Update += this.OnFrameworkUpdate;

        commandManager.AddHandler("/isthatacrit", new CommandInfo(OnCommand)
        {
            HelpMessage = "Is that a crit？ —— 打开统计窗口；/isthatacrit reset 清空所有统计与历史",
        });

        this.log.Info("[Is that a crit？] 插件已加载。");
    }

    private void OnFrameworkUpdate(IFramework _)
    {
        this.tracker.Tick();
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
        this.commandManager.RemoveHandler("/isthatacrit");
        this.pluginInterface.UiBuilder.OpenMainUi -= ToggleMain;
        this.pluginInterface.UiBuilder.Draw -= this.windowSystem.Draw;
        this.windowSystem.RemoveAllWindows();
        this.tracker.Dispose();
    }
}
