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
    private static Task? _novaTask;
    private static readonly object NovaGate = new();
    private static readonly ConcurrentDictionary<string, NovaPlan> PendingNovaPlans = new();
    private static readonly ConcurrentDictionary<int, GameSession> Sessions = new();
    private static int _nextSessionId;
    private static Task? _installTask;
    private static Task? _mpInstallTask;
    private static readonly object InstallGate = new();

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

                // install.vanilla 是长任务：先回执"已受理"，进度走事件流，完成再补终态响应。
                if (method == "install.vanilla")
                {
                    lock (InstallGate)
                    {
                        if (_installTask is { IsCompleted: false })
                        {
                            Reply(stdout, id, ok: false, error: "已有安装任务在进行");
                            continue;
                        }
                        var versionId = parameters.GetProperty("versionId").GetString()!;
                        _installTask = RunInstallAsync(stdout, id, versionId);
                    }
                    Reply(stdout, id, ok: true, result: new { accepted = true });
                    continue;
                }

                // 联机核心安装同样是长任务：先回执，进度/终态走事件。
                if (method == "multiplayer.install")
                {
                    lock (InstallGate)
                    {
                        if (_mpInstallTask is { IsCompleted: false })
                        {
                            Reply(stdout, id, ok: false, error: "联机核心正在安装");
                            continue;
                        }
                        _mpInstallTask = RunMultiplayerInstallAsync(stdout, id);
                    }
                    Reply(stdout, id, ok: true, result: new { accepted = true });
                    continue;
                }

                // Nova 对话：流式增量走事件，终态走 nova.done / nova.failed。
                if (method == "nova.chat")
                {
                    lock (NovaGate)
                    {
                        if (_novaTask is { IsCompleted: false })
                        {
                            Reply(stdout, id, ok: false, error: "Nova 正在回复上一条消息");
                            continue;
                        }
                        _novaTask = RunNovaChatAsync(stdout, id, parameters);
                    }
                    Reply(stdout, id, ok: true, result: new { accepted = true });
                    continue;
                }

                // 长请求不能串行阻塞主循环：否则下载/扫描 Java 期间 ping、game.kill
                // 全部排队，Dart 侧的短超时会把"其实已受理"的操作误判为失败。
                if (IsLongRunning(method))
                {
                    var capturedId = id;
                    var capturedMethod = method;
                    var capturedParams = parameters;
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var result = await DispatchAsync(stdout, capturedMethod, capturedParams);
                            Reply(stdout, capturedId, ok: true, result: result);
                        }
                        catch (Exception e)
                        {
                            Reply(stdout, capturedId, ok: false, error: e.Message);
                        }
                    });
                    continue;
                }

                var result = await DispatchAsync(stdout, method, parameters);
                Reply(stdout, id, ok: true, result: result);
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
        return 0;
    }

    private static bool IsLongRunning(string method) =>
        method is "download.file" or "download.text" or "java.list" or "launch.start"
            or "multiplayer.host" or "multiplayer.join" or "multiplayer.stop"
            or "multiplayer.logs" or "multiplayer.nodes"
            or "content.search" or "content.install"
            or "version.checkUpdates" or "version.updateMod" or "version.checkDependencies"
            or "skins.importUrl" or "skins.station.list" or "skins.station.apply"
            or "skins.bs6.authorize" or "skins.bs6.closet" or "skins.bs6.apply"
            or "nova.plan.execute"
            or "lab.load" or "lab.worldEater" or "lab.assets"
            or "projections.scan" or "projections.copy" or "projections.move"
            or "projections.delete"
            or "plugins.invoke"
            or "lab.colorText"
            or "settings.checkUpdate" or "saves.exportShare" or "saves.importShare"
            or "saves.copyToVersion"
            or "runtime.diagnose"
            or "versions.rename" or "versions.copy" or "versions.exportProbe"
            or "versions.exportModpack" or "versions.resolvePackLink"
            or "versions.downloadPack" or "versions.packPreview" or "versions.importPack"
            or "versions.backupList" or "versions.backupCreate" or "versions.backupRestore"
            or "versions.backupDelete" or "versions.backupRebuild"
            or "starcloud.login" or "starcloud.register" or "starcloud.sendCode"
            or "friends.list" or "friends.requests" or "friends.search" or "friends.invites"
            or "friends.sendRequest" or "friends.createInvite" or "friends.conversation"
            or "friends.send" or "friends.markRead";

    /// <summary>父进程消失后退出（stdin 管道通常也会 EOF，这里是双保险）。</summary>
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

    private static async Task<object?> DispatchAsync(TextWriter stdout, string method, JsonElement p)
    {
        return method switch
        {
            "ping" => await HandlePingAsync(stdout, p),
            "catalog.versions" => await HandleCatalogVersionsAsync(stdout, p),
            "catalog.installed" => await HandleCatalogInstalledAsync(stdout, p),
            "versions.delete" => await HandleVersionsDeleteAsync(stdout, p),
            "versions.openFolder" => await HandleVersionsOpenFolderAsync(stdout, p),
            "versions.setDefault" => await HandleVersionsSetDefaultAsync(stdout, p),
            "versions.setMarks" => await HandleVersionsSetMarksAsync(stdout, p),
            "saves.icon" => await HandleSavesIconAsync(stdout, p),
            "saves.list" => await HandleSavesListAsync(stdout, p),
            "saves.openFolder" => await HandleSavesOpenFolderAsync(stdout, p),
            "saves.backup" => await HandleSavesBackupAsync(stdout, p),
            "saves.delete" => await HandleSavesDeleteAsync(stdout, p),
            "plugins.list" => await HandlePluginsListAsync(stdout, p),
            "plugins.install" => await HandlePluginsInstallAsync(stdout, p),
            "plugins.uninstall" => await HandlePluginsUninstallAsync(stdout, p),
            "plugins.setEnabled" => await HandlePluginsSetEnabledAsync(stdout, p),
            "plugins.panels" => await HandlePluginsPanelsAsync(stdout, p),
            "plugins.view" => await HandlePluginsViewAsync(stdout, p),
            "plugins.invoke" => await HandlePluginsInvokeAsync(stdout, p),
            "plugins.openFolder" => await HandlePluginsOpenFolderAsync(stdout, p),
            "lab.load" => await HandleLabLoadAsync(stdout, p),
            "lab.assets" => await HandleLabAssetsAsync(stdout, p),
            "lab.worldEater" => await HandleLabWorldEaterAsync(stdout, p),
            "projections.scan" => await HandleProjectionsScanAsync(stdout, p),
            "projections.copy" => await HandleProjectionsCopyAsync(stdout, p),
            "projections.move" => await HandleProjectionsMoveAsync(stdout, p),
            "projections.delete" => await HandleProjectionsDeleteAsync(stdout, p),
            "version.settings" => await HandleVersionSettingsAsync(stdout, p),
            "version.saveSettings" => await HandleVersionSaveSettingsAsync(stdout, p),
            "version.checkUpdates" => await HandleVersionCheckUpdatesAsync(stdout, p),
            "version.updateMod" => await HandleVersionUpdateModAsync(stdout, p),
            "version.checkDependencies" => await HandleVersionCheckDependenciesAsync(stdout, p),
            "version.detail" => await HandleVersionDetailAsync(stdout, p),
            "version.toggleContent" => await HandleVersionToggleContentAsync(stdout, p),
            "version.deleteContent" => await HandleVersionDeleteContentAsync(stdout, p),
            "version.openContentFolder" => await HandleVersionOpenContentFolderAsync(stdout, p),
            "content.search" => await HandleContentSearchAsync(stdout, p),
            "content.install" => await HandleContentInstallAsync(stdout, p),
            "nova.config.get" => await HandleNovaConfigGetAsync(stdout, p),
            "nova.config.set" => await HandleNovaConfigSetAsync(stdout, p),
            "nova.plan.execute" => await HandleNovaPlanExecuteAsync(stdout, p),
            "nova.plan.discard" => await HandleNovaPlanDiscardAsync(stdout, p),
            "multiplayer.state" => await HandleMultiplayerStateAsync(stdout, p),
            "multiplayer.selectProvider" => await HandleMultiplayerSelectProviderAsync(stdout, p),
            "multiplayer.setNickname" => await HandleMultiplayerSetNicknameAsync(stdout, p),
            "multiplayer.setNodeRegion" => await HandleMultiplayerSetNodeRegionAsync(stdout, p),
            "multiplayer.host" => await HandleMultiplayerHostAsync(stdout, p),
            "multiplayer.join" => await HandleMultiplayerJoinAsync(stdout, p),
            "multiplayer.idle" => await HandleMultiplayerIdleAsync(stdout, p),
            "multiplayer.stop" => await HandleMultiplayerStopAsync(stdout, p),
            "multiplayer.logs" => await HandleMultiplayerLogsAsync(stdout, p),
            "multiplayer.nodes" => await HandleMultiplayerNodesAsync(stdout, p),
            "java.list" => await HandleJavaListAsync(stdout, p),
            "accounts.list" => await HandleAccountsListAsync(stdout, p),
            "accounts.addOffline" => await HandleAccountsAddOfflineAsync(stdout, p),
            "accounts.setCurrent" => await HandleAccountsSetCurrentAsync(stdout, p),
            "accounts.remove" => await HandleAccountsRemoveAsync(stdout, p),
            "accounts.setSkin" => await HandleAccountsSetSkinAsync(stdout, p),
            "skins.list" => await HandleSkinsListAsync(stdout, p),
            "skins.importPath" => await HandleSkinsImportPathAsync(stdout, p),
            "skins.importUrl" => await HandleSkinsImportUrlAsync(stdout, p),
            "skins.rename" => await HandleSkinsRenameAsync(stdout, p),
            "skins.setModel" => await HandleSkinsSetModelAsync(stdout, p),
            "skins.delete" => await HandleSkinsDeleteAsync(stdout, p),
            "skins.station.list" => await HandleSkinsStationListAsync(stdout, p),
            "skins.station.apply" => await HandleSkinsStationApplyAsync(stdout, p),
            "skins.bs6.status" => await HandleSkinsBs6StatusAsync(stdout, p),
            "skins.bs6.authorize" => await HandleSkinsBs6AuthorizeAsync(stdout, p),
            "skins.bs6.closet" => await HandleSkinsBs6ClosetAsync(stdout, p),
            "skins.bs6.apply" => await HandleSkinsBs6ApplyAsync(stdout, p),
            "auth.microsoft.start" => await HandleAuthMicrosoftStartAsync(stdout, p),
            "auth.microsoft.poll" => await HandleAuthMicrosoftPollAsync(stdout, p),
            "auth.yggdrasil.login" => await HandleAuthYggdrasilLoginAsync(stdout, p),
            "download.text" => await HandleDownloadTextAsync(stdout, p),
            "download.file" => await HandleDownloadFileAsync(stdout, p),
            "shell.openPath" => await HandleShellOpenPathAsync(stdout, p),
            "app.paths" => await HandleAppPathsAsync(stdout, p),
            "settings.get" => await HandleSettingsGetAsync(stdout, p),
            "settings.apply" => await HandleSettingsApplyAsync(stdout, p),
            "launch.start" => await HandleLaunchStartAsync(stdout, p),
            "game.kill" => await HandleGameKillAsync(stdout, p),
            "starcloud.state" => await HandleStarcloudStateAsync(stdout, p),
            "starcloud.login" => await HandleStarcloudLoginAsync(stdout, p),
            "starcloud.sendCode" => await HandleStarcloudSendCodeAsync(stdout, p),
            "starcloud.register" => await HandleStarcloudRegisterAsync(stdout, p),
            "starcloud.logout" => await HandleStarcloudLogoutAsync(stdout, p),
            "friends.list" => await HandleFriendsListAsync(stdout, p),
            "friends.requests" => await HandleFriendsRequestsAsync(stdout, p),
            "friends.search" => await HandleFriendsSearchAsync(stdout, p),
            "friends.sendRequest" => await HandleFriendsSendRequestAsync(stdout, p),
            "friends.acceptRequest" => await HandleFriendsAcceptRequestAsync(stdout, p),
            "friends.rejectRequest" => await HandleFriendsRejectRequestAsync(stdout, p),
            "friends.cancelRequest" => await HandleFriendsCancelRequestAsync(stdout, p),
            "friends.remove" => await HandleFriendsRemoveAsync(stdout, p),
            "friends.invites" => await HandleFriendsInvitesAsync(stdout, p),
            "friends.createInvite" => await HandleFriendsCreateInviteAsync(stdout, p),
            "friends.acceptInvite" => await HandleFriendsAcceptInviteAsync(stdout, p),
            "friends.declineInvite" => await HandleFriendsDeclineInviteAsync(stdout, p),
            "friends.cancelInvite" => await HandleFriendsCancelInviteAsync(stdout, p),
            "friends.conversation" => await HandleFriendsConversationAsync(stdout, p),
            "friends.send" => await HandleFriendsSendAsync(stdout, p),
            "friends.markRead" => await HandleFriendsMarkReadAsync(stdout, p),
            "friends.groups" => await HandleFriendsGroupsAsync(stdout, p),
            "friends.groupDetail" => await HandleFriendsGroupDetailAsync(stdout, p),
            "friends.groupMessages" => await HandleFriendsGroupMessagesAsync(stdout, p),
            "friends.groupSend" => await HandleFriendsGroupSendAsync(stdout, p),
            "friends.groupMarkRead" => await HandleFriendsGroupMarkReadAsync(stdout, p),
            "friends.groupCreate" => await HandleFriendsGroupCreateAsync(stdout, p),
            "friends.groupAddMembers" => await HandleFriendsGroupAddMembersAsync(stdout, p),
            "friends.groupDissolve" => await HandleFriendsGroupDissolveAsync(stdout, p),
            "friends.uploadFile" => await HandleFriendsUploadFileAsync(stdout, p),
            "friends.downloadFile" => await HandleFriendsDownloadFileAsync(stdout, p),
            "versions.rename" => await HandleVersionsRenameAsync(stdout, p),
            "versions.copy" => await HandleVersionsCopyAsync(stdout, p),
            "versions.exportProbe" => await HandleVersionsExportProbeAsync(stdout, p),
            "versions.exportModpack" => await HandleVersionsExportModpackAsync(stdout, p),
            "versions.resolvePackLink" => await HandleVersionsResolvePackLinkAsync(stdout, p),
            "versions.downloadPack" => await HandleVersionsDownloadPackAsync(stdout, p),
            "versions.packPreview" => await HandleVersionsPackPreviewAsync(stdout, p),
            "versions.importPack" => await HandleVersionsImportPackAsync(stdout, p),
            "content.translate" => await HandleContentTranslateAsync(stdout, p),
            "content.installModpack" => await HandleContentInstallModpackAsync(stdout, p),
            "versions.backupList" => await HandleVersionsBackupListAsync(stdout, p),
            "versions.backupCreate" => await HandleVersionsBackupCreateAsync(stdout, p),
            "versions.backupRestore" => await HandleVersionsBackupRestoreAsync(stdout, p),
            "versions.backupDelete" => await HandleVersionsBackupDeleteAsync(stdout, p),
            "versions.backupRebuild" => await HandleVersionsBackupRebuildAsync(stdout, p),
            "lab.palette" => await HandleLabPaletteAsync(stdout, p),
            "lab.colorText" => await HandleLabColorTextAsync(stdout, p),
            "settings.clearCache" => await HandleSettingsClearCacheAsync(stdout, p),
            "settings.checkUpdate" => await HandleSettingsCheckUpdateAsync(stdout, p),
            "saves.exportShare" => await HandleSavesExportShareAsync(stdout, p),
            "saves.importShare" => await HandleSavesImportShareAsync(stdout, p),
            "saves.copyToVersion" => await HandleSavesCopyToVersionAsync(stdout, p),
            "runtime.state" => await HandleRuntimeStateAsync(stdout, p),
            "runtime.select" => await HandleRuntimeSelectAsync(stdout, p),
            "runtime.interval" => await HandleRuntimeIntervalAsync(stdout, p),
            "runtime.gpu" => await HandleRuntimeGpuAsync(stdout, p),
            "runtime.kill" => await HandleRuntimeKillAsync(stdout, p),
            "runtime.logs" => await HandleRuntimeLogsAsync(stdout, p),
            "runtime.diagnose" => await HandleRuntimeDiagnoseAsync(stdout, p),
            _ => throw new Exception($"未知方法：{method}"),
        };
    }
}
