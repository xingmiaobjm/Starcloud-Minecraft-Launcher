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
    public SaveLibraryService SaveLibrary { get; }
    public LaunchAccountService LaunchAccount { get; }
    public StarcloudIdService StarcloudId { get; }
    public GameDiagnosticsService Diagnostics { get; }
    public LaunchPreflightService Preflight { get; }
    public ModConflictService ModConflicts { get; }
    public NovaPermissionStore NovaPermissions { get; }
    public NovaActionLog NovaLog { get; }
    public NovaMomentsService NovaMoments { get; }
    public NovaInviteService NovaInvites { get; }
    public NovaService Nova { get; }
    public NovaRuntime NovaRuntime { get; }

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
        Launcher = new GameLauncher(Installer, Java);
        Accounts = AccountStore.Load();
        MicrosoftAuth = new MicrosoftAuthService();
        MrPack = new MrPackInstallerService(Download);
        Saves = new SaveService();
        SaveLibrary = new SaveLibraryService(Manifest, Saves);
        LaunchAccount = new LaunchAccountService(Accounts, MicrosoftAuth, () => Settings);
        StarcloudId = new StarcloudIdService();
        Diagnostics = new GameDiagnosticsService(Installer, Manifest);
        Preflight = new LaunchPreflightService(Installer, Download, Manifest);
        ModConflicts = new ModConflictService();
        NovaPermissions = new NovaPermissionStore();
        NovaLog = new NovaActionLog();
        NovaMoments = new NovaMomentsService();
        NovaInvites = new NovaInviteService(Installer, Manifest, Java, Launcher, Download);
        Nova = new NovaService(StarcloudId, Mods, Diagnostics, ModConflicts, NovaPermissions,
            NovaLog, NovaMoments, NovaInvites, Saves);
        // Nova 全局运行时最后建：订阅时刻提案 + 建观察定时器（依赖上面的服务）
        NovaRuntime = new NovaRuntime(this);
    }

    public void ReloadSettings() => Settings = LauncherSettings.Load();

    public void SaveSettings() => Settings.Save();
}
