// 共享服务单例（对应旧 Tauri AppState）

using Starcloud.Core.Services;

namespace Starcloud.App.Services;

public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    public LauncherSettings Settings { get; private set; }
    public DownloadService Download { get; }
    public ManifestService Manifest { get; }
    public InstallerService Installer { get; }
    public FabricService Fabric { get; }
    public ModrinthService Mods { get; }
    public GameLauncher Launcher { get; }
    public AccountStore Accounts { get; }
    public MicrosoftAuthService MicrosoftAuth { get; }
    public JavaService Java { get; }
    public ForgeInstallerService Forge { get; }
    public MrPackInstallerService MrPack { get; }
    public SaveService Saves { get; }
    public StarcloudIdService StarcloudId { get; }
    public GameDiagnosticsService Diagnostics { get; }
    public ModConflictService ModConflicts { get; }
    public AiAssistantService Ai { get; }

    private AppServices()
    {
        LauncherPaths.EnsureDataDir();
        Settings = LauncherSettings.Load();
        Download = new DownloadService();
        Manifest = new ManifestService(Download);
        Installer = new InstallerService(Download);
        Fabric = new FabricService(Download);
        Java = new JavaService(Download);
        Forge = new ForgeInstallerService(Download, Java);
        Mods = new ModrinthService(Download);
        Launcher = new GameLauncher(Installer);
        Accounts = AccountStore.Load();
        MicrosoftAuth = new MicrosoftAuthService();
        MrPack = new MrPackInstallerService(Download);
        Saves = new SaveService();
        StarcloudId = new StarcloudIdService();
        Diagnostics = new GameDiagnosticsService(Installer, Manifest);
        ModConflicts = new ModConflictService();
        Ai = new AiAssistantService(StarcloudId, Mods, Diagnostics, ModConflicts);
    }

    public void ReloadSettings() => Settings = LauncherSettings.Load();

    public void SaveSettings() => Settings.Save();
}
