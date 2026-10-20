// 共享服务单例（对应旧 Tauri AppState）

using Starcloud.Core.Kernel.Plugins;
using Starcloud.Core.Plugins;
using Starcloud.App.Shell;
using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Multiplayer;

namespace Starcloud.App.Services;

public sealed class AppServices
{
    public static AppServices Instance { get; } = new();

    public LauncherSettings Settings { get; private set; }

    /// <summary>内核：插件宿主 + 契约注册表 + 事件总线（万物皆插件的地基）</summary>
    public Starcloud.Core.Kernel.KernelHost Kernel { get; }

    /// <summary>投影管理（内核插件 starcloud.projections 提供）</summary>
    public Starcloud.Core.Kernel.Contracts.IProjectionLibrary ProjectionLibrary { get; }

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
    public GameLauncher Launcher { get; }
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

    /// <summary>
    /// 跨设备链路（设计：2026-10-14-cross-device-sync-design.md）：同 Starcloud ID 设备发现、
    /// 存档/版本数据同步、下载 P2P 加速共用的唯一链路。需登录 + 设置开关才真正启动。
    /// </summary>
    public DeviceLinkService DeviceLink { get; }

    /// <summary>存档同步语义（推送 / 拉取 / 清单），走 <see cref="DeviceLink"/>。</summary>
    public SaveSyncService SaveSync { get; }

    /// <summary>SC 实验室 · 投影工坊（.litematic / 结构 nbt / schematic / schem 的解析、元数据编辑与转换）</summary>
    public IProjectionEngine Projections { get; }
    public ILlmGateway LlmGateway { get; }

    /// <summary>SC 实验室 · 投影社区（多源聚合投影资源列表 + 下载；GitHub 经镜像加速，基址可在界面配置）</summary>
    public ProjectionCommunityService ProjectionCommunity { get; }

    /// <summary>SC 实验室 · 社区文本翻译（英文描述 → 中文，卡片先原文后换译文）</summary>
    public CommunityTranslator CommunityTranslation { get; }

    /// <summary>SC 实验室 · 本机 Minecraft 资源包（投影 3D 预览的真实方块纹理来源）</summary>
    public MinecraftAssets MinecraftAssets { get; }
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

        // ── 内核：登记内置插件并启动 ──
        // 插件化改造的 P0：内核先跑起来，下面那几个服务改成"从内核取"。
        // 老调用点（页面里直接 AppServices.Instance.Download）暂时不动，行为完全不变。
        Kernel = new Starcloud.Core.Kernel.KernelHost(new Starcloud.Core.Kernel.KernelOptions
        {
            DataRoot = LauncherPaths.DataDir,
        });
        Kernel.Register(BuiltinDownloadPlugin.Descriptor, _ => new BuiltinDownloadPlugin(() => Settings));
        Kernel.Register(BuiltinVersionPlugin.Descriptor, _ => new BuiltinVersionPlugin());
        Kernel.Register(BuiltinTranslatePlugin.Descriptor, _ => new BuiltinTranslatePlugin());
        Kernel.Register(BuiltinIconCachePlugin.Descriptor, _ => new BuiltinIconCachePlugin(() => Settings));
        Kernel.Register(BuiltinProjectionPlugin.Descriptor, _ => new BuiltinProjectionPlugin(() => Settings));
        Kernel.Register(BuiltinShellPlugin.Descriptor, _ => new BuiltinShellPlugin());
        Kernel.Register(BuiltinProjectionEnginePlugin.Descriptor, _ => new BuiltinProjectionEnginePlugin(() => Settings));
        Kernel.Register(BuiltinLlmGatewayPlugin.Descriptor, _ => new BuiltinLlmGatewayPlugin(() => Settings));
        // 内置插件的 InitializeAsync 都是纯内存构造，同步启动即可（有 I/O 的插件应声明 lazy）
        Kernel.StartAsync().GetAwaiter().GetResult();

        Download = Kernel.Get<DownloadService>();
        // 下载源命中情况写进启动器日志，用于确认多源竞速是否生效
        Download.Log = msg => LauncherLog.Write("download", msg);
        ResourceCache = Kernel.Get<ResourceCacheService>();
        IconCache = Kernel.Get<IconCacheService>();
        Manifest = Kernel.Get<ManifestService>();
        Installer = new InstallerService(Download);
        Fabric = new FabricService(Download);
        Java = new JavaService(Download);
        Forge = new ForgeInstallerService(Download, Java);
        Mods = new ModrinthService(Download, ResourceCache);
        CurseForge = new CurseForgeService(Download, () => Settings);
        CurseForgePacks = new CurseForgePackInstaller(Download, CurseForge);
        ProjectionLibrary = Kernel.Get<Starcloud.Core.Kernel.Contracts.IProjectionLibrary>();
        SearchTranslation = Kernel.Get<SearchTranslationService>();
        ModSummaries = new ModSummaryTranslationService(Download, () => Settings);
        ContentLibrary = new ContentLibraryService(Download, Mods, Manifest, () => Settings);
        Launcher = new GameLauncher(Installer, Java);
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
        // 跨设备链路：需要 SaveLibrary / Saves / StarcloudId（都已在上面就位）；
        // 下载管线从这里拿 P2P 对等源（PeerCount=0 时管线零开销跳过）
        DeviceLink = new DeviceLinkService(() => Settings, StarcloudId, SaveLibrary, Saves)
        {
            Log = msg => LauncherLog.Write("devlink", msg),
        };
        SaveSync = new SaveSyncService(() => Settings, StarcloudId, DeviceLink, Saves,
            msg => LauncherLog.Write("devlink", msg));
        Kernel.Register(BuiltinSaveSyncPlugin.Descriptor, _ => new BuiltinSaveSyncPlugin(SaveSync));
        Kernel.ActivateAsync(BuiltinSaveSyncPlugin.PluginId).GetAwaiter().GetResult();
        Download.PeerSource = () => DeviceLink;
        Projections = Kernel.Get<IProjectionEngine>();
        LlmGateway = Kernel.Get<ILlmGateway>();
        ProjectionCommunity = new ProjectionCommunityService(() => Settings, Download);
        CommunityTranslation = new CommunityTranslator(Download);
        MinecraftAssets = new MinecraftAssets(() => Settings);
        // 通道探针：蓝牙 / 星闪可用性要 Windows 专属 API（Radio / 注册表），在 App 层注入
        DeviceLink.RegisterProbe("蓝牙", LinkProbes.BluetoothAsync);
        DeviceLink.RegisterProbe("星闪（NearLink）", LinkProbes.NearLinkAsync);
        // 皮肤库（多皮肤管理）+ 离线皮肤注入：账户指向库内条目，启动器按需起本机纹理服务
        Skins = new SkinLibraryService();
        // 内核已启动：实例建好后立即注册并激活（引擎面板要能看到这条能力）
        Kernel.Register(BuiltinSkinPlugin.Descriptor, _ => new BuiltinSkinPlugin(Skins));
        Kernel.ActivateAsync(BuiltinSkinPlugin.PluginId).GetAwaiter().GetResult();
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
        NovaInvites = new NovaInviteService(Installer, Manifest, Java, Launcher, Download,
            AgentJarPath, () => PluginHost);
        // Agent 工具注册表先于 Nova 建（Nova 持有 Agent 循环，工具复用上面的服务）。
        // 工具面覆盖启动器全部功能：必需服务直接传，只有 UI 层才有的能力（录屏/插件/外观）走宿主适配器。
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
            Launcher = Launcher,
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
            NovaLog, NovaMoments, NovaInvites, Saves, NovaTools, StarcloudId, SearchTranslation,
            LlmGateway);
        // Nova 全局运行时最后建：订阅时刻提案 + 建观察定时器（依赖上面的服务）
        NovaRuntime = new NovaRuntime(this);
        // 插件系统：宿主版本号取自启动器程序集（minLauncherVersion 校验 / ctx 透出）
        var asmVer = typeof(AppServices).Assembly.GetName().Version ?? new Version(0, 0, 0);
        var pluginHostSdk = Path.Combine(AppContext.BaseDirectory,
            "Assets", "pluginsdk", "starcloud_host.py");
        PluginHost = new PluginHost(
            $"{asmVer.Major}.{asmVer.Minor}.{asmVer.Build}",
            LauncherPaths.PluginLogsDir, pluginHostSdk,
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

    /// <summary>authlib-injector jar 路径（Assets 内置；缺失返回 null，由调用方报错）</summary>
    public static string? AgentJarPath()
    {
        var path = Path.Combine(AppContext.BaseDirectory,
            "Assets", "authlib-injector", "authlib-injector-1.2.8.jar");
        return File.Exists(path) ? path : null;
    }
}
