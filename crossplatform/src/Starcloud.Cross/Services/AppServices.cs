// 共享服务单例（镜像 Windows 版 AppServices；App 层服务——外观/动效/Nova 运行时/自更新启动器——
// 随 UI 阶段（Phase 3/4）落地后接入。此处只含 Core 层服务，全部已在 Phase 1 平台抽象完成）

using System.IO;
using System.Reflection;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;

namespace Starcloud.Cross.Services;

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
    public YggdrasilService Yggdrasil { get; }
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
    public NovaToolRegistry NovaTools { get; }
    public NovaService Nova { get; }

    /// <summary>插件宿主（plugin-spec.md）：管理界面随 Phase 4 插件面板页接入</summary>
    public PluginHost PluginHost { get; }

    /// <summary>自更新（macOS/Linux 为 ManualDownload 能力，UI 引导手动安装）</summary>
    public SelfUpdateService SelfUpdate { get; }

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
        Yggdrasil = new YggdrasilService();
        MrPack = new MrPackInstallerService(Download);
        ModPackExport = new ModPackExportService(Mods);
        VersionBackup = new VersionBackupService();
        Saves = new SaveService();
        SaveLibrary = new SaveLibraryService(Manifest, Saves);
        LaunchAccount = new LaunchAccountService(Accounts, () => Settings);
        StarcloudId = new StarcloudIdService();
        Diagnostics = new GameDiagnosticsService(Installer, Manifest);
        Preflight = new LaunchPreflightService(Installer, Download, Manifest);
        CrashAnalyzer = new CrashAnalysisService(Installer);
        EnvCheck = new EnvironmentCheckService(Installer, Java);
        ModConflicts = new ModConflictService();
        NovaPermissions = new NovaPermissionStore();
        NovaLog = new NovaActionLog();
        NovaMoments = new NovaMomentsService();
        NovaInvites = new NovaInviteService(Installer, Manifest, Java, Launcher, Download,
            AgentJarPath, () => PluginHost);
        // Agent 工具注册表先于 Nova 建（Nova 持有 Agent 循环，工具复用上面的服务）
        NovaTools = new NovaToolRegistry(Manifest, Installer, Forge, Fabric, Mods, ContentLibrary,
            () => Settings, SaveSettings);
        Nova = new NovaService(Mods, Diagnostics, ModConflicts, NovaPermissions,
            NovaLog, NovaMoments, NovaInvites, Saves, NovaTools);
        // 插件系统：宿主版本号取自启动器 InformationalVersion（minLauncherVersion 校验 / ctx 透出）
        PluginHost = new PluginHost(
            HostVersion(), LauncherPaths.PluginLogsDir, PluginHostSdkPath());
        SelfUpdate = new SelfUpdateService(() => Settings);
        LauncherLog.Write("app", $"启动器就绪，数据目录 {LauncherPaths.DataDir}");
    }

    public void ReloadSettings() => Settings = LauncherSettings.Load();

    public void SaveSettings() => Settings.Save();

    /// <summary>authlib-injector jar 路径（Assets 内置；缺失返回 null，由调用方报错）</summary>
    public static string? AgentJarPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "Assets", "authlib-injector", "authlib-injector-1.2.8.jar");
        return File.Exists(path) ? path : null;
    }

    private static string PluginHostSdkPath() =>
        Path.Combine(AppContext.BaseDirectory, "Assets", "pluginsdk", "starcloud_host.py");

    /// <summary>插件宿主协议版本：AssemblyInformationalVersion 去 +commit 后缀（如 0.2.4）</summary>
    private static string HostVersion()
    {
        var raw = typeof(AppServices).Assembly
            .GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "0.0.0";
        var i = raw.IndexOf('+');
        return (i > 0 ? raw[..i] : raw).Trim();
    }
}
