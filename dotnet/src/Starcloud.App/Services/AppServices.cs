// 共享服务单例（对应旧 Tauri AppState）

using Starcloud.Core.Services;

namespace Starcloud.App.Services;

public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    public LauncherSettings Settings { get; private set; }
    public DownloadService Download { get; }
    public ResourceCacheService ResourceCache { get; }
    public IconCacheService IconCache { get; }
    public ManifestService Manifest { get; }
    public InstallerService Installer { get; }
    public FabricService Fabric { get; }
    public ModrinthService Mods { get; }
    public ContentLibraryService ContentLibrary { get; }
    public GameLauncher Launcher { get; }
    public AccountStore Accounts { get; }
    public MicrosoftAuthService MicrosoftAuth { get; }
    public JavaService Java { get; }
    public ForgeInstallerService Forge { get; }
    public MrPackInstallerService MrPack { get; }
    public ModPackExportService ModPackExport { get; }
    public VersionBackupService VersionBackup { get; }
    public SaveService Saves { get; }
    public SaveLibraryService SaveLibrary { get; }
    public LaunchAccountService LaunchAccount { get; }
    public StarcloudIdService StarcloudId { get; }
    public GameDiagnosticsService Diagnostics { get; }
    public LaunchPreflightService Preflight { get; }
    public CrashAnalysisService CrashAnalyzer { get; }
    public EnvironmentCheckService EnvCheck { get; }
    public ModConflictService ModConflicts { get; }
    public NovaPermissionStore NovaPermissions { get; }
    public NovaActionLog NovaLog { get; }
    public NovaMomentsService NovaMoments { get; }
    public NovaInviteService NovaInvites { get; }
    public NovaService Nova { get; }
    public NovaRuntime NovaRuntime { get; }

    /// <summary>自更新（检查清单 / 下载校验 / 拉起更新脚本）</summary>
    public SelfUpdateService SelfUpdate { get; }

    /// <summary>外观应用中心（主题 / 内置壁纸 / 毛玻璃遮罩）</summary>
    public AppearanceService Appearance { get; }

    /// <summary>界面动效总开关（跟随系统 / 始终开启 / 关闭）</summary>
    public MotionService Motion { get; }

    private AppServices()
    {
        LauncherPaths.EnsureDataDir();
        Settings = LauncherSettings.Load();
        Download = new DownloadService(() => Settings);
        // 下载源命中情况写进启动器日志，用于确认多源竞速是否生效
        Download.Log = msg => LauncherLog.Write("download", msg);
        ResourceCache = new ResourceCacheService(() => Settings);
        IconCache = new IconCacheService(Download, () => Settings);
        Manifest = new ManifestService(Download, ResourceCache);
        Installer = new InstallerService(Download);
        Fabric = new FabricService(Download);
        Java = new JavaService(Download);
        Forge = new ForgeInstallerService(Download, Java);
        Mods = new ModrinthService(Download, ResourceCache);
        ContentLibrary = new ContentLibraryService(Download, Mods, Manifest, () => Settings);
        Launcher = new GameLauncher(Installer, Java);
        Accounts = AccountStore.Load();
        MicrosoftAuth = new MicrosoftAuthService();
        MrPack = new MrPackInstallerService(Download);
        ModPackExport = new ModPackExportService(Mods);
        VersionBackup = new VersionBackupService();
        Saves = new SaveService();
        SaveLibrary = new SaveLibraryService(Manifest, Saves);
        LaunchAccount = new LaunchAccountService(Accounts, MicrosoftAuth, () => Settings);
        StarcloudId = new StarcloudIdService();
        Diagnostics = new GameDiagnosticsService(Installer, Manifest);
        Preflight = new LaunchPreflightService(Installer, Download, Manifest);
        CrashAnalyzer = new CrashAnalysisService(Installer);
        EnvCheck = new EnvironmentCheckService(Installer, Java);
        ModConflicts = new ModConflictService();
        NovaPermissions = new NovaPermissionStore();
        NovaLog = new NovaActionLog();
        NovaMoments = new NovaMomentsService();
        NovaInvites = new NovaInviteService(Installer, Manifest, Java, Launcher, Download);
        Nova = new NovaService(StarcloudId, Mods, Diagnostics, ModConflicts, NovaPermissions,
            NovaLog, NovaMoments, NovaInvites, Saves);
        // Nova 全局运行时最后建：订阅时刻提案 + 建观察定时器（依赖上面的服务）
        NovaRuntime = new NovaRuntime(this);
        SelfUpdate = new SelfUpdateService(() => Settings);
        Appearance = new AppearanceService();
        Motion = new MotionService(() => Settings);
        LauncherLog.Write("app", $"启动器就绪，数据目录 {LauncherPaths.DataDir}");
    }

    public void ReloadSettings() => Settings = LauncherSettings.Load();

    public void SaveSettings() => Settings.Save();
}
