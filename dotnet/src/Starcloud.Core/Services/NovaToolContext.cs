// Nova 工具的运行环境：一次性把「启动器能做的事」交给工具层，避免注册表构造函数堆二十个参数。
// 必需项 = 不管在哪种宿主里都有的服务；可选项 = 该宿主没提供时对应工具直接不注册
// （宁可不给模型这个能力，也不给一个必然失败的工具）。

using Starcloud.Core.Services.Multiplayer;

namespace Starcloud.Core.Services;

/// <summary>
/// App 侧才有的能力（录屏 / 插件 / 外观 / 启动后回调）。
/// Core 里只留接口：Core 的单元测试不依赖 WinUI，缺实现时这些工具自动缺席。
/// </summary>
public interface INovaHostActions
{
    /// <summary>录屏状态一句话；返回 null = 这台机器不支持录屏</summary>
    string? RecordingStatus();

    Task<string> StartRecordingAsync(CancellationToken ct = default);

    Task<string> StopRecordingAsync(CancellationToken ct = default);

    /// <summary>已安装插件清单（含启用状态与来源）；null = 宿主没提供插件能力</summary>
    string? ListPlugins();

    /// <summary>启停插件；返回一句结果说明</summary>
    string SetPluginEnabled(string id, bool enabled);

    /// <summary>从 .mclplug 文件安装插件（同 id 覆盖）；返回一句结果说明</summary>
    string InstallPlugin(string path);

    /// <summary>卸载插件；返回一句结果说明</summary>
    string UninstallPlugin(string id);

    /// <summary>改外观（主题 / 壁纸 / 遮罩）；只传需要改的项</summary>
    string SetAppearance(
        string? theme = null, string? backgroundMode = null,
        string? backgroundImagePath = null, double? opacity = null, int? blurRadius = null);

    /// <summary>切到启动器的某个页面（账户 / 设置 / 下载…），用于引导用户完成只有界面能做的手动步骤</summary>
    string OpenPage(string page);

    /// <summary>更新包已就绪：拉起更新脚本并退出当前进程（启动器随即自动重启）</summary>
    void RestartForUpdate(string artifactPath, string version);

    /// <summary>游戏进程拉起后的宿主收尾（运行态标记、Nova 时刻观察会话、浮窗 watcher）</summary>
    void OnGameLaunched(string versionId, int gamePid);
}

/// <summary>工具层能看到的一整套启动器服务（App 侧全部填满，Core 测试按需填）</summary>
public sealed record NovaToolContext
{
    public required ManifestService Manifest { get; init; }
    public required InstallerService Installer { get; init; }
    public required ModrinthService Mods { get; init; }
    public required ContentLibraryService Library { get; init; }
    public required Func<LauncherSettings> Settings { get; init; }

    /// <summary>写设置后落盘（App 传 Settings.Save）</summary>
    public Action? SaveSettings { get; init; }

    public ForgeInstallerService? Forge { get; init; }
    public FabricService? Fabric { get; init; }
    public CurseForgeService? CurseForge { get; init; }
    public SearchTranslationService? Translation { get; init; }
    public GameDiagnosticsService? Diagnostics { get; init; }
    public CrashAnalysisService? Crash { get; init; }
    public ModConflictService? Conflicts { get; init; }
    public EnvironmentCheckService? EnvCheck { get; init; }
    public LaunchPreflightService? Preflight { get; init; }
    public JavaService? Java { get; init; }
    public GameLauncher? Launcher { get; init; }
    public LaunchAccountService? Accounts { get; init; }

    /// <summary>已保存账户表（App 传入运行中的同一实例，写操作才能和界面同步）</summary>
    public AccountStore? Store { get; init; }

    public SaveService? Saves { get; init; }
    public SaveLibraryService? SaveLibrary { get; init; }
    public VersionBackupService? Backups { get; init; }
    public ModPackExportService? Export { get; init; }
    public MrPackInstallerService? MrPack { get; init; }
    public CurseForgePackInstaller? CurseForgePacks { get; init; }
    public MultiplayerService? Multiplayer { get; init; }
    public SelfUpdateService? Updates { get; init; }
    public INovaHostActions? Host { get; init; }
}
