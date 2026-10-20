// 共享服务单例（对应旧 Tauri AppState）

using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Multiplayer;

namespace Starcloud.Launcher.Uno.Services;

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

    /// <summary>CurseForge 资源源（搜索 / 项目文件 / 直链）与整合包安装</summary>
    public CurseForgeService CurseForge { get; }
    public CurseForgePackInstaller CurseForgePacks { get; }

    /// <summary>
    /// 搜索词翻译（中文 → 英文）：Modrinth / CurseForge 只有英文项目名，
    /// 走内置词典 → MC 百科 → 机器翻译，带内存 + 磁盘缓存
    /// </summary>
    public SearchTranslationService SearchTranslation { get; }

    /// <summary>模组简介中文翻译（MCIM 的 /translate；只在中文界面启用，失败保留原文）</summary>
    public ModSummaryTranslationService ModSummaries { get; }

    public ContentLibraryService ContentLibrary { get; }
    public GameLauncherService Launcher { get; }
    public AccountStore Accounts { get; }
    public MicrosoftAuthService MicrosoftAuth { get; }
    public YggdrasilService Yggdrasil { get; }
    public JavaService Java { get; }
    public ForgeInstallerService Forge { get; }
    public MrPackInstallerService MrPack { get; }

    /// <summary>MultiMC / Prism 实例包导入（自包含，解压即用）</summary>
    public MultiMcPackService MultiMc { get; }
    public ModPackExportService ModPackExport { get; }
    public VersionBackupService VersionBackup { get; }
    public SaveService Saves { get; }
    public SaveLibraryService SaveLibrary { get; }
    public LaunchAccountService LaunchAccount { get; }

    /// <summary>联机：内置陶瓦联机 + 第三方联机 SDK 清单（提供方注册表 / 会话 / 状态轮询）</summary>
    public MultiplayerService Multiplayer { get; }

    public StarcloudIdService StarcloudId { get; }
    public GameDiagnosticsService Diagnostics { get; }
    public LaunchPreflightService Preflight { get; }
    public CrashAnalysisService CrashAnalyzer { get; }
    public EnvironmentCheckService EnvCheck { get; }
    public ModConflictService ModConflicts { get; }
    public NovaPermissionStore NovaPermissions { get; }
    public NovaActionLog NovaLog { get; }
    public RecordingService Recording { get; }
    public NovaMomentsService NovaMoments { get; }
    public NovaInviteService NovaInvites { get; }
    public NovaToolRegistry NovaTools { get; }
    public NovaService Nova { get; }
    public NovaRuntime NovaRuntime { get; }

    /// <summary>插件系统（plugin-spec.md）：宿主 + 管理（安装/启停/免责确认）</summary>
    public PluginHost PluginHost { get; }
    public PluginService Plugins { get; }

    /// <summary>皮肤库（多皮肤管理：导入 / 重命名 / 删除 / 型号）</summary>
    public SkinLibraryService Skins { get; }

    /// <summary>换肤上传（微软官方 API / 第三方 Yggdrasil）</summary>
    public SkinUploadService SkinUpload { get; }

    /// <summary>皮肤站衣柜（第三方账号：列出皮肤站上的皮肤并切换）</summary>
    public SkinStationService SkinStation { get; }

    /// <summary>自更新（检查清单 / 下载校验 / 拉起更新脚本）</summary>
    public SelfUpdateService SelfUpdate { get; }

    /// <summary>外观应用中心（主题 / 内置壁纸 / 毛玻璃遮罩）</summary>
    public AppearanceService Appearance { get; }

    /// <summary>界面动效总开关（跟随系统 / 始终开启 / 关闭）</summary>
    public MotionService Motion { get; }

    /// <summary>游戏运行态（1s 采样 / 跨启动器恢复 / 运行历史），Core 单例</summary>
    public GameRuntimeMonitor Runtime => GameRuntimeMonitor.Instance;

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
        CurseForge = new CurseForgeService(Download, () => Settings);
        CurseForgePacks = new CurseForgePackInstaller(Download, CurseForge);
        SearchTranslation = new SearchTranslationService(Download);
        ModSummaries = new ModSummaryTranslationService(Download, () => Settings);
        ContentLibrary = new ContentLibraryService(Download, Mods, Manifest, () => Settings);
        // 安卓侧启动链走 GameLauncherService（组件部署 → Java 桥 → JVM 拉起）；
        // Installer / Java 仍单独挂出，供页面与各安装服务复用
        Launcher = GameLauncherService.Instance;
        Accounts = AccountStore.Load();
        MicrosoftAuth = new MicrosoftAuthService();
        Yggdrasil = new YggdrasilService();
        MrPack = new MrPackInstallerService(Download);
        MultiMc = new MultiMcPackService();
        ModPackExport = new ModPackExportService(Mods, CurseForge);
        VersionBackup = new VersionBackupService();
        Saves = new SaveService();
        SaveLibrary = new SaveLibraryService(Manifest, Saves);
        LaunchAccount = new LaunchAccountService(Accounts, () => Settings);
        // 联机：默认玩家名跟随当前账户（没单独设联机昵称时），与启动身份同名才不会在游戏里认错人
        Multiplayer = new MultiplayerService(
            Download,
            () => Settings,
            () => Accounts.DefaultOf(LaunchAccount.Mode)?.Username
                ?? Accounts.Accounts.FirstOrDefault()?.Username,
            SaveSettings);
        StarcloudId = new StarcloudIdService();
        // 皮肤库（多皮肤管理）+ 离线皮肤注入：账户指向库内条目，启动器按需起本机纹理服务
        Skins = new SkinLibraryService();
        SkinUpload = new SkinUploadService();
        SkinStation = new SkinStationService();
        OfflineSkins.Configure(Accounts, Skins);
        // 清掉 0.3.1 早期「代码画出来的」老预置皮肤（用户自己导入的不动）
        SkinPresetMigration.PurgeLegacyPresetSkins(Skins, Accounts);
        Diagnostics = new GameDiagnosticsService(Installer, Manifest);
        Preflight = new LaunchPreflightService(Installer, Download, Manifest);
        CrashAnalyzer = new CrashAnalysisService(Installer);
        EnvCheck = new EnvironmentCheckService(Installer, Java);
        ModConflicts = new ModConflictService();
        NovaPermissions = new NovaPermissionStore();
        NovaLog = new NovaActionLog();
        Recording = new RecordingService(NovaPermissions, NovaLog);
        // 自更新只依赖设置，提前建：Nova 工具表要用它（check_updates）
        SelfUpdate = new SelfUpdateService(() => Settings);
        NovaMoments = new NovaMomentsService();
        // NovaInvites 构造必需 Core 的 GameLauncher（邀请打包 / 加入流程签名依赖）；
        // 安卓实际拉起走 GameLauncherService，这里仅提供一个不落设置的最小实例
        NovaInvites = new NovaInviteService(Installer, Manifest, Java, new GameLauncher(Installer, Java), Download,
            AgentJarPath, () => PluginHost);
        // Agent 工具注册表先于 Nova 建（Nova 持有 Agent 循环，工具复用上面的服务）。
        // 工具面覆盖启动器全部功能：必需服务直接传，只有 UI 层才有的能力（录屏/插件/外观）走宿主适配器。
        // 注意：不给工具层 Core 的 GameLauncher——安卓拉起必须走 GameLauncherService，
        // 桌面式进程启动在安卓必然失败，宁可让启动类工具缺席（NovaToolContext 的可选项语义）
        NovaTools = new NovaToolRegistry(new NovaToolContext
        {
            Manifest = Manifest,
            Installer = Installer,
            Mods = Mods,
            Library = ContentLibrary,
            Settings = () => Settings,
            SaveSettings = SaveSettings,
            Forge = Forge,
            Fabric = Fabric,
            CurseForge = CurseForge,
            Translation = SearchTranslation,
            Diagnostics = Diagnostics,
            Crash = CrashAnalyzer,
            Conflicts = ModConflicts,
            EnvCheck = EnvCheck,
            Preflight = Preflight,
            Java = Java,
            Accounts = LaunchAccount,
            Store = Accounts,
            Saves = Saves,
            SaveLibrary = SaveLibrary,
            Backups = VersionBackup,
            Export = ModPackExport,
            MrPack = MrPack,
            CurseForgePacks = CurseForgePacks,
            Multiplayer = Multiplayer,
            Updates = SelfUpdate,
            Host = new NovaHostActions(this),
        });
        Nova = new NovaService(Mods, Diagnostics, ModConflicts, NovaPermissions,
            NovaLog, NovaMoments, NovaInvites, Saves, NovaTools, StarcloudId, SearchTranslation);
        // Nova 全局运行时最后建：订阅时刻提案 + 建观察定时器（依赖上面的服务）
        NovaRuntime = new NovaRuntime(this);
        // 插件系统：宿主版本号取自启动器程序集（minLauncherVersion 校验 / ctx 透出）
        var asmVer = typeof(AppServices).Assembly.GetName().Version ?? new Version(0, 0, 0);
        PluginHost = new PluginHost(
            $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}",
            LauncherPaths.PluginLogsDir, ResolvePluginHostSdk(),
            // 插件 SDK 服务包：与 Nova 共用同一套执行层，两条入口行为一致
            new Starcloud.Core.Plugins.PluginHostServices
            {
                Settings = () => Settings,
                SaveSettings = SaveSettings,
                Manifest = Manifest,
                Installer = Installer,
                Download = Download,
                Accounts = Accounts,
                LaunchAccount = LaunchAccount,
                Tasks = TaskCenterService.Instance,
                Tools = NovaTools,
                Bridge = new PluginHostActions(),
            });
        Plugins = new PluginService(PluginHost);
        Appearance = new AppearanceService();
        Motion = new MotionService(() => Settings);
        // 运行态监控：先注入依赖再起心跳（恢复探测需要 dotMinecraft 与历史库）
        GameRuntimeMonitor.Instance.Configure(() => Settings.DotMinecraft);
        GameRuntimeMonitor.Instance.Start();
        LauncherLog.Write("app", $"启动器就绪，数据目录 {LauncherPaths.DataDir}");
    }

    public void ReloadSettings() => Settings = LauncherSettings.Load();

    public void SaveSettings() => Settings.Save();

    /// <summary>
    /// 插件宿主 SDK 路径：安卓把 APK assets 里的 starcloud_host.py 解包到数据目录后传路径；
    /// assets 缺失 / 解包失败时退回原样路径，由 PluginHost 启动插件时按文件缺失报错（与桌面缺文件同路径）。
    /// </summary>
    private static string ResolvePluginHostSdk()
    {
        const string assetPath = "Assets/pluginsdk/starcloud_host.py";
        var target = Path.Combine(LauncherPaths.DataDir, "pluginsdk", "starcloud_host.py");
        try
        {
            using var src = global::Android.App.Application.Context.Assets!.Open(assetPath);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            using var dst = File.Create(target);
            src.CopyTo(dst);
            return target;
        }
        catch
        {
            return Path.Combine(AppContext.BaseDirectory,
                "Assets", "pluginsdk", "starcloud_host.py");
        }
    }

    /// <summary>
    /// authlib-injector jar 路径：安卓从 APK assets 解包到数据目录（缺失 / 解包失败返回 null，由调用方报错）
    /// </summary>
    public static string? AgentJarPath()
    {
        const string assetPath = "Assets/authlib-injector/authlib-injector-1.2.8.jar";
        var target = Path.Combine(LauncherPaths.DataDir,
            "authlib-injector", "authlib-injector-1.2.8.jar");
        try
        {
            if (!File.Exists(target))
            {
                using var src = global::Android.App.Application.Context.Assets!.Open(assetPath);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using var dst = File.Create(target);
                src.CopyTo(dst);
            }
            return target;
        }
        catch
        {
            return null;
        }
    }
}
