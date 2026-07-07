namespace BaoleMaLe;

using Dalamud.Plugin.Services;

/// <summary>
/// 极简的静态服务访问器，供不需要注入的辅助类（如 PlayerStats）取用 Dalamud 服务。
/// 在 Plugin 构造函数里赋值。
/// </summary>
public static class DalamudApi
{
    public static IObjectTable? ObjectTable;
    public static IDataManager? DataManager;
    public static IClientState? ClientState;
}
