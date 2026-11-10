using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core;
using Starcloud.Core.Models;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Multiplayer;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Bridge;

/// <summary>桥接宿主入口：装配 Core 服务，stdin 行式 JSON 分发，stdout 输出。</summary>
public static partial class Program
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static LauncherSettings _settings = null!;
    private static DownloadService _download = null!;
    private static ManifestService _manifest = null!;
    private static InstallerService _installer = null!;
    private static JavaService _java = null!;
    private static GameLauncher _launcher = null!;
    private static AccountStore _accounts = null!;
    private static MicrosoftAuthService _msAuth = null!;
    private static YggdrasilService _yggdrasil = null!;
    private static readonly ConcurrentDictionary<string, MicrosoftAuthService.DeviceCodeInfo>
        PendingDeviceCodes = new();
    private static SaveService _saves = null!;
    private static SaveLibraryService _saveLibrary = null!;
    private static ModrinthService _modrinth = null!;
    private static ContentLibraryService _contentLibrary = null!;
    private static SkinLibraryService _skins = null!;
    private static SkinStationService _skinStation = null!;
    private static MultiplayerService _multiplayer = null!;
    private static ProjectionLibrary _projections = null!;
    private static StarcloudIdService _starcloudId = null!;
    private static FriendsService _friends = null!;
    private static CancellationTokenSource? FriendsEventsCts;
    private static CurseForgeService _curseForge = null!;
    private static ForgeInstallerService _forge = null!;
    private static ModPackExportService _modPackExport = null!;
    private static MrPackInstallerService _mrPack = null!;
    private static CurseForgePackInstaller _cfPacks = null!;
    private static MultiMcPackService _multiMcPacks = null!;
    private static VersionBackupService _versionBackup = null!;
    private static GameDiagnosticsService _diagnostics = null!;
    private static ResourceCacheService _resourceCache = null!;
    private static SelfUpdateService _selfUpdate = null!;
    private static SearchTranslationService _translation = null!;
    private static NovaService _nova = null!;
    private static PluginHost _pluginHost = null!;
    private static readonly ConcurrentDictionary<string, NovaPlan> PendingNovaPlans = new();
    private static readonly ConcurrentDictionary<int, GameSession> Sessions = new();
    private static int _nextSessionId;

    public static async Task<int> Main(string[] args)
    {
        // stdio 固定 UTF-8：Windows 控制台默认代码页会让中文路径/版本名乱码，
        // Dart 侧的严格 UTF-8 解码会因此把整条协议流判死。
        var utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        using var stdin = new StreamReader(Console.OpenStandardInput(), utf8);
        var stdout = new StreamWriter(Console.OpenStandardOutput(), utf8)
        {
            AutoFlush = true,
        };

        // 父进程消失（崩溃/热重启）时自杀，避免留下孤儿桥接进程。
        StartParentWatchdog();

        // 数据根由拉起方（Flutter 壳）经环境变量注入，两端共享同一份数据。
        _settings = LauncherSettings.Load();
        _download = new DownloadService(() => _settings);
        _manifest = new ManifestService(_download);
        _installer = new InstallerService(_download);
        _java = new JavaService(_download);
        _launcher = new GameLauncher(_installer, _java);
        _accounts = AccountStore.Load();
        _msAuth = new MicrosoftAuthService();
        _yggdrasil = new YggdrasilService();

        // Starcloud ID（好友/联机邀请的账号体系）：会话持久化在数据根，重启自动恢复。
        _starcloudId = new StarcloudIdService();
        _friends = new FriendsService(() => _starcloudId.Current?.ApiKey);
        _curseForge = new CurseForgeService(_download, () => _settings);
        _forge = new ForgeInstallerService(_download, _java);
        _modPackExport = new ModPackExportService(_modrinth, _curseForge);
        _mrPack = new MrPackInstallerService(_download);
        _cfPacks = new CurseForgePackInstaller(_download, _curseForge);
        _multiMcPacks = new MultiMcPackService();
        _versionBackup = new VersionBackupService();

        // 运行态监控（Core 单例）：1s 采样、跨重启恢复、结束进程都走它，
        // 与 Win 的 RunningStatusPage 同一份数据源。
        GameRuntimeMonitor.Instance.Configure(() => _settings.DotMinecraft);
        GameRuntimeMonitor.Instance.Changed += _ =>
        {
            try { Emit(stdout, "runtime.changed", RuntimePayload()); }
            catch { /* 采样线程里不抛 */ }
        };
        GameRuntimeMonitor.Instance.Start();
        _saves = new SaveService();
        _saveLibrary = new SaveLibraryService(_manifest, _saves);
        _projections = new ProjectionLibrary(() => _settings);
        _modrinth = new ModrinthService(_download);
        _contentLibrary = new ContentLibraryService(
            _download, _modrinth, _manifest, () => _settings);
        _skins = new SkinLibraryService();
        _skinStation = new SkinStationService();
        // 离线/微软账户的本地皮肤通过回环 HTTP 服务 + --userProperties 注入游戏。
        OfflineSkins.Configure(_accounts, _skins);
        _multiplayer = new MultiplayerService(
            _download,
            () => _settings,
            () => _accounts.Default?.Username,
            () => _settings.Save());
        _multiplayer.Changed += snapshot =>
            Emit(stdout, "multiplayer.changed", MultiplayerPayload(snapshot));
        _multiplayer.StartPolling();

        // Nova：只读工具集（跨端最小实现先打通对话与查询；写操作留给后续确认清单）。
        _diagnostics = new GameDiagnosticsService(_installer, _manifest);
        _resourceCache = new ResourceCacheService(() => _settings);
        _selfUpdate = new SelfUpdateService(() => _settings);
        var diagnostics = _diagnostics;
        var conflicts = new ModConflictService();
        var novaPermissions = new NovaPermissionStore();
        var novaLog = new NovaActionLog();
        var novaMoments = new NovaMomentsService();
        var novaInvites = new NovaInviteService(
            _installer, _manifest, _java, _launcher, _download, AgentJarPath);
        _translation = new SearchTranslationService(_download);
        var translation = _translation;
        var novaTools = new NovaToolRegistry(new NovaToolContext
        {
            Manifest = _manifest,
            Installer = _installer,
            Mods = _modrinth,
            Library = _contentLibrary,
            Settings = () => _settings,
            SaveSettings = () => _settings.Save(),
            Forge = new ForgeInstallerService(_download, _java),
            Fabric = new FabricService(_download),
            CurseForge = new CurseForgeService(_download, () => _settings),
            Diagnostics = diagnostics,
            Conflicts = conflicts,
            Java = _java,
            Launcher = _launcher,
            Store = _accounts,
            Saves = _saves,
            SaveLibrary = _saveLibrary,
            Multiplayer = _multiplayer,
            Translation = translation,
        }, readOnlyOnly: false);
        _nova = new NovaService(
            _modrinth, diagnostics, conflicts,
            novaPermissions, novaLog, novaMoments, novaInvites, _saves,
            novaTools, _starcloudId, translation, new NovaLlmGateway(() => _settings));

        // v2 插件运行时：JS（Jint）/ Python（python3 子进程）/ 纯声明式（Noop）。
        _pluginHost = new PluginHost(
            LauncherVersion,
            LauncherPaths.PluginLogsDir,
            PluginSdkPath(),
            new PluginHostServices
            {
                Settings = () => _settings,
                SaveSettings = () => _settings.Save(),
                Manifest = _manifest,
                Installer = _installer,
                Download = _download,
                Accounts = _accounts,
                Tasks = TaskCenterService.Instance,
                Tools = novaTools,
                Bridge = new BridgePluginHostBridge(),
            });

        Emit(stdout, "bridge.ready", new
        {
            dataDir = LauncherPaths.DataDir,
            dotMinecraft = _settings.DotMinecraft,
            downloadSource = _settings.DownloadSource,
        });

        // 好友实时通道：登录状态下桥接启动即拉起 SSE 循环（断线指数退避重连），
        // 事件以 friends.event 转发给壳端（Win 端等价物是 FriendsRuntime）。
        if (_starcloudId.IsLoggedIn) StartFriendsEvents(stdout);

        while (await stdin.ReadLineAsync() is { } line)
        {
            if (line.Length == 0) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var id = root.GetProperty("id").GetInt32();
                var method = root.GetProperty("method").GetString() ?? "";
                JsonElement parameters = root.TryGetProperty("params", out var p)
                    ? p.Clone()
                    : JsonSerializer.Deserialize<JsonElement>("{}");

                // 方法表是唯一的真相来源：名字 → 语义 → 实现都在 Program.Methods.cs。
                // 这里只按 Kind 分派，不再持有任何方法名清单。
                if (!Methods.TryGet(method, out var entry))
                    throw new Exception($"未知方法：{method}");

                switch (entry.Kind)
                {
                    case BridgeMethodKind.Streaming:
                    {
                        // 单飞长任务：先回执"已受理"，进度与终态走事件流。
                        lock (entry.Gate)
                        {
                            if (entry.Running is { IsCompleted: false })
                            {
                                Reply(stdout, id, ok: false, error: entry.BusyError);
                                continue;
                            }
                            entry.Running = entry.Starter!(stdout, id, parameters);
                        }
                        Reply(stdout, id, ok: true, result: new { accepted = true });
                        break;
                    }

                    case BridgeMethodKind.Concurrent:
                    {
                        // 会阻塞的请求不能串行占住主循环：否则下载/扫 Java 期间 ping、
                        // game.kill 全部排队，壳端的短超时会把"其实已受理"误判为失败。
                        var capturedId = id;
                        var handler = entry.Handler!;
                        var capturedParams = parameters;
                        Inflight.Add(Task.Run(async () =>
                        {
                            try
                            {
                                Reply(stdout, capturedId, ok: true,
                                    result: await handler(stdout, capturedParams));
                            }
                            catch (Exception e)
                            {
                                Reply(stdout, capturedId, ok: false, error: e.Message);
                            }
                        }));
                        break;
                    }

                    default:
                    {
                        var result = await entry.Handler!(stdout, parameters);
                        Reply(stdout, id, ok: true, result: result);
                        break;
                    }
                }
            }
            catch (Exception e)
            {
                // 单请求异常不拖垮宿主；错误经对应 id 回给调用方（id 取不到就发事件）。
                int? failedId = null;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    if (doc.RootElement.TryGetProperty("id", out var idEl)) failedId = idEl.GetInt32();
                }
                catch { /* 忽略：连请求本身都解析不了 */ }
                if (failedId.HasValue)
                    Reply(stdout, failedId.Value, ok: false, error: e.Message);
                else
                    Emit(stdout, "bridge.error", new { message = e.Message });
            }
        }

        // stdin 关闭（壳端退出或重启桥接）时，并发请求可能还在路上。直接 return 会让
        // java.list / content.search 这类慢查询的响应随进程一起消失，壳端只能等超时。
        // 这里给它们一个收尾窗口；上限 10s，避免残留任务拖住退出。
        var pending = Inflight.ToArray();
        if (pending.Length > 0)
        {
            var all = Task.WhenAll(pending);
            await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(10)));
            _ = all.Exception; // 观察掉异常，避免 UnobservedTaskException
        }

        return 0;
    }

    /// <summary>EOF 之后仍需回包的并发请求（退出前统一收尾）。</summary>
    private static readonly ConcurrentBag<Task> Inflight = new();

    private static void StartParentWatchdog()
    {
        if (!int.TryParse(
                Environment.GetEnvironmentVariable("STARCLOUD_BRIDGE_PARENT_PID"),
                out var parentPid) || parentPid <= 0)
            return;

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(2000);
                try
                {
                    using var parent = Process.GetProcessById(parentPid);
                    if (parent.HasExited) Environment.Exit(0);
                }
                catch (ArgumentException)
                {
                    Environment.Exit(0); // 父进程已不存在
                }
                catch
                {
                    // 权限等临时错误不当作父进程消失
                }
            }
        });
    }

    // ── 方法分发 ─────────────────────────────────────────────────────────────
}
