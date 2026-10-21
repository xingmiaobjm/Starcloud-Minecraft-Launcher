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
public static class Program
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
    private static CurseForgeService _curseForge = null!;
    private static ForgeInstallerService _forge = null!;
    private static ModPackExportService _modPackExport = null!;
    private static MrPackInstallerService _mrPack = null!;
    private static CurseForgePackInstaller _cfPacks = null!;
    private static MultiMcPackService _multiMcPacks = null!;
    private static VersionBackupService _versionBackup = null!;
    private static ProjectionCommunityService _projectionCommunity = null!;
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
        _projectionCommunity = new ProjectionCommunityService(() => _settings, _download);
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
        var diagnostics = new GameDiagnosticsService(_installer, _manifest);
        var conflicts = new ModConflictService();
        var novaPermissions = new NovaPermissionStore();
        var novaLog = new NovaActionLog();
        var novaMoments = new NovaMomentsService();
        var novaInvites = new NovaInviteService(
            _installer, _manifest, _java, _launcher, _download, AgentJarPath);
        var translation = new SearchTranslationService(_download);
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
            novaTools, null, translation);

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
            or "nova.plan.execute"
            or "lab.load" or "lab.worldEater" or "lab.assets"
            or "projections.scan" or "projections.copy" or "projections.move"
            or "projections.delete"
            or "plugins.invoke"
            or "lab.colorText" or "lab.commands" or "lab.buildCommand"
            or "lab.communitySearch" or "lab.communityFiles" or "lab.communityDownload"
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
        switch (method)
        {
            case "ping":
                return new { pong = true, apiVersion = "bridge.1" };

            case "catalog.versions":
            {
                var force = p.TryGetProperty("force", out var f) && f.GetBoolean();
                var manifest = await _manifest.GetManifestAsync(force);
                var installed = _manifest.ListInstalled(_settings.DotMinecraft);
                var installedIds = installed.Select(i => i.Id).ToHashSet();
                return new
                {
                    remote = manifest.Versions.Select(v => new
                    {
                        v.Id,
                        v.Type,
                        v.Url,
                        v.ReleaseTime,
                        installed = installedIds.Contains(v.Id),
                    }),
                    installed = installed.Select(i => new { i.Id, i.SizeBytes }),
                };
            }

            case "catalog.installed":
            {
                var instances = new InstanceSettingsStore(_settings.DotMinecraft);
                return new
                {
                    installed = _manifest
                        .ListInstalled(_settings.DotMinecraft)
                        .Select(i => DescribeInstalled(i, instances)),
                };
            }

            case "versions.delete":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                if (!InstanceLayout.IsValidVersionId(versionId))
                    throw new Exception("非法版本 id");
                if (_installTask is { IsCompleted: false })
                    throw new Exception("安装进行中，暂不能删除版本");
                if (Sessions.Values.Any(s => s.VersionId == versionId && !s.HasExited))
                    throw new Exception("该版本正在运行，请先关闭游戏");

                var versionsRoot = Path.Combine(_settings.DotMinecraft, "versions");
                var dir = Path.Combine(versionsRoot, versionId);
                if (!Directory.Exists(dir))
                    throw new Exception($"版本目录不存在：{versionId}");

                // 被其它版本的 inheritsFrom 依赖 → 拒绝：删掉父版本会让加载器实例无法启动。
                foreach (var sub in Directory.EnumerateDirectories(versionsRoot))
                {
                    var otherId = Path.GetFileName(sub);
                    if (otherId == versionId) continue;
                    var json = Path.Combine(sub, otherId + ".json");
                    try
                    {
                        using var doc = JsonDocument.Parse(File.ReadAllText(json));
                        if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh) &&
                            inh.ValueKind == JsonValueKind.String &&
                            inh.GetString() == versionId)
                            throw new Exception($"版本 {otherId} 依赖该版本（inheritsFrom），不能删除");
                    }
                    catch (JsonException) { /* 坏 JSON 不阻塞删除 */ }
                    catch (IOException) { /* 读不到就跳过 */ }
                }

                long freed = 0;
                try
                {
                    freed = new DirectoryInfo(dir)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length);
                }
                catch { /* 统计失败不影响删除 */ }

                Directory.Delete(dir, recursive: true);
                var wasDefault = _settings.LastVersion == versionId;
                if (wasDefault)
                {
                    _settings.LastVersion = null;
                    _settings.Save();
                }
                return new { deleted = true, freedBytes = freed, wasDefault };
            }

            case "versions.openFolder":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                if (!InstanceLayout.IsValidVersionId(versionId))
                    throw new Exception("非法版本 id");
                // 文件夹入口跟随实例根（Win 同款）：隔离实例各自持有一份，共享实例落在 .minecraft 下
                var kind = p.TryGetProperty("kind", out var ofKind) && ofKind.ValueKind == JsonValueKind.String
                    ? ofKind.GetString()
                    : null;
                string dir;
                if (kind is "saves" or "mods")
                {
                    var store = new InstanceSettingsStore(_settings.DotMinecraft);
                    var isolated = InstanceLayout.IsIsolated(store.Get(versionId), _settings);
                    var root = InstanceLayout.GameRoot(_settings.DotMinecraft, versionId, isolated);
                    dir = Path.Combine(root, kind);
                }
                else
                {
                    dir = Path.Combine(_settings.DotMinecraft, "versions", versionId);
                }
                Directory.CreateDirectory(dir);
                OpenFolder(dir);
                return new { ok = true, path = dir };
            }

            case "versions.setDefault":
            {
                var versionId = p.TryGetProperty("versionId", out var v) &&
                                v.ValueKind == JsonValueKind.String
                    ? v.GetString()
                    : null;
                if (!string.IsNullOrEmpty(versionId) &&
                    !Directory.Exists(Path.Combine(_settings.DotMinecraft, "versions", versionId)))
                    throw new Exception($"版本目录不存在：{versionId}");
                _settings.LastVersion = string.IsNullOrEmpty(versionId) ? null : versionId;
                _settings.Save();
                return new { ok = true, lastVersion = _settings.LastVersion };
            }

            case "saves.icon":
            {
                // 世界缩略图是本地文件，渲染层（Electron）读不到文件系统，
                // 这里读成 data URL 回传，避免为图片放开 file:// CSP。
                var iconPath = p.GetProperty("path").GetString()!;
                var full = LabSafePath(iconPath);
                var bytes = File.ReadAllBytes(full);
                var mime = full.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                    ? "image/png" : "image/jpeg";
                return new { dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" };
            }

            case "saves.list":
            {
                var list = _saveLibrary.ListAll(_settings.DotMinecraft, _settings);
                return new
                {
                    saves = list.Select(s => new
                    {
                        s.FolderName,
                        s.LevelName,
                        lastPlayed = s.LastPlayed?.ToString("O"),
                        s.SizeBytes,
                        s.IconPath,
                        s.VersionId,
                        s.Isolated,
                        s.GameRoot,
                    }),
                };
            }

            case "saves.openFolder":
            {
                var (root, folder) = SaveTarget(p);
                var dir = Path.Combine(root, "saves", folder);
                if (!Directory.Exists(dir))
                    throw new Exception($"存档目录不存在：{folder}");
                OpenFolder(dir);
                return new { ok = true, path = dir };
            }

            case "saves.backup":
            {
                var (root, folder) = SaveTarget(p);
                var backupsDir = Path.Combine(_settings.DotMinecraft, "backups");
                var zip = _saves.Backup(root, backupsDir, folder);
                return new { ok = true, path = zip, sizeBytes = new FileInfo(zip).Length };
            }

            case "saves.delete":
            {
                var (root, folder) = SaveTarget(p);
                if (Sessions.Values.Any(s => !s.HasExited))
                    throw new Exception("游戏正在运行，请先关闭游戏再删除存档");
                var dir = Path.Combine(root, "saves", folder);
                if (!Directory.Exists(dir))
                    throw new Exception($"存档目录不存在：{folder}");
                long freed = 0;
                try
                {
                    freed = new DirectoryInfo(dir)
                        .EnumerateFiles("*", SearchOption.AllDirectories)
                        .Sum(f => f.Length);
                }
                catch { /* 统计失败不影响删除 */ }
                _saves.Delete(root, folder);
                return new { ok = true, freedBytes = freed };
            }

            case "plugins.list":
            {
                var state = LoadPluginState();
                var installed = ScanPlugins();
                return new
                {
                    plugins = installed.Select(p =>
                        PluginPayload(p.Manifest, p.Dir, p.Official, state)),
                    pageTipShown = state.PageTipShown,
                };
            }

            case "plugins.install":
            {
                var path = p.GetProperty("path").GetString()!;
                var (plugin, errors) = InstallPlugin(path);
                if (plugin is null)
                    throw new Exception(string.Join("；", errors));
                var state = LoadPluginState();
                return new
                {
                    plugin = PluginPayload(plugin.Value.Manifest, plugin.Value.Dir,
                        plugin.Value.Official, state),
                };
            }

            case "plugins.uninstall":
            {
                var id = SafePluginId(p.GetProperty("id").GetString()!);
                var dir = Path.Combine(LauncherPaths.PluginUnpackedDir, id);
                if (Directory.Exists(dir))
                {
                    try { Directory.Delete(dir, recursive: true); }
                    catch (Exception e)
                    {
                        throw new Exception($"卸载失败：{e.Message}");
                    }
                }
                SetPluginState(id, _ => { }, remove: true);
                return new { ok = true };
            }

            case "plugins.setEnabled":
            {
                var id = SafePluginId(p.GetProperty("id").GetString()!);
                var enabled = p.GetProperty("enabled").GetBoolean();
                var acceptDisclaimer = p.TryGetProperty("acceptDisclaimer", out var ad) &&
                                       ad.ValueKind == JsonValueKind.True;
                var installed = ScanPlugins();
                var target = installed.FirstOrDefault(x => x.Manifest.Id == id);
                if (target.Manifest is null)
                    throw new Exception($"插件未安装：{id}");
                var current = LoadPluginState().Plugins.TryGetValue(id, out var entry)
                    ? entry
                    : null;
                if (enabled)
                {
                    if (!target.Official && !acceptDisclaimer &&
                        (current?.DisclaimerAccepted ?? false) == false)
                        throw new Exception("第三方插件需要先确认免责声明");
                    // 依赖闸门：把本插件视为待启用，缺依赖/依赖未启用/版本不满足即拒绝。
                    var resolution = PluginDependencyResolver.Resolve(
                        installed.Select(x => x.Manifest),
                        other => other == id || IsPluginEnabled(other));
                    var reason = PluginDependencyResolver.BlockReason(resolution, id);
                    if (reason is not null) throw new Exception(reason);
                    // 真正启动运行时（JS/Python/声明式）；失败则不落启用状态。
                    EnsurePluginStarted(id, allowDisabled: true);
                }
                else
                {
                    _pluginHost.Stop(id);
                }
                SetPluginState(id, s =>
                {
                    s.Enabled = enabled;
                    if (acceptDisclaimer) s.DisclaimerAccepted = true;
                });
                return new
                {
                    ok = true,
                    enabled,
                    runtimeStarted = enabled && _pluginHost.IsLoaded(id),
                };
            }

            case "plugins.panels":
            {
                var id = SafePluginId(p.GetProperty("id").GetString()!);
                EnsurePluginStarted(id);
                return new
                {
                    panels = _pluginHost.Panels
                        .Where(panel => panel.PluginId == id)
                        .Select(panel => new
                        {
                            id = panel.PanelId,
                            title = panel.Title,
                            declarative = panel.Declarative,
                            entry = panel.EntryPath,
                        }),
                };
            }

            case "plugins.view":
            {
                var id = SafePluginId(p.GetProperty("id").GetString()!);
                var panelId = p.GetProperty("panelId").GetString()!;
                EnsurePluginStarted(id);
                var panel = _pluginHost.Panels.FirstOrDefault(panel =>
                    panel.PluginId == id && panel.PanelId == panelId)
                    ?? throw new Exception($"面板不存在：{panelId}");
                if (!PluginViewParser.TryParseFile(
                        panel.EntryPath, out var root, out var errors) || root is null)
                {
                    return new
                    {
                        ok = false,
                        errors,
                        view = (object?)null,
                        bindings = new Dictionary<string, object?>(),
                    };
                }
                var keys = new HashSet<string>(StringComparer.Ordinal);
                CollectBindings(root, keys);
                var bindings = new Dictionary<string, object?>(StringComparer.Ordinal);
                foreach (var key in keys)
                {
                    var parts = key.Split('.', 2);
                    if (parts.Length != 2 || parts[0] != "storage") continue;
                    var result = _pluginHost.InvokeAction(
                        id, "storage.get",
                        JsonSerializer.Serialize(new object?[] { parts[1] }));
                    if (!result.Ok || result.ResultJson is null)
                    {
                        bindings[key] = null;
                        continue;
                    }
                    try
                    {
                        bindings[key] = JsonSerializer.Deserialize<JsonElement>(
                            result.ResultJson);
                    }
                    catch
                    {
                        bindings[key] = result.ResultJson;
                    }
                }
                return new
                {
                    ok = true,
                    errors = Array.Empty<string>(),
                    view = root,
                    bindings,
                };
            }

            case "plugins.invoke":
            {
                var id = SafePluginId(p.GetProperty("id").GetString()!);
                var api = p.GetProperty("api").GetString()!;
                var args = p.TryGetProperty("args", out var argsElement)
                    ? argsElement.GetRawText()
                    : "[]";
                EnsurePluginStarted(id);
                var result = _pluginHost.InvokeAction(id, api, args);
                return new
                {
                    ok = result.Ok,
                    result = result.ResultJson,
                    error = result.Error,
                };
            }

            case "plugins.openFolder":
            {
                var id = SafePluginId(p.GetProperty("id").GetString()!);
                var dir = Path.Combine(LauncherPaths.PluginUnpackedDir, id);
                if (!Directory.Exists(dir)) throw new Exception($"插件目录不存在：{id}");
                OpenFolder(dir);
                return new { ok = true, path = dir };
            }

            case "lab.load":
            {
                var path = p.GetProperty("path").GetString()!;
                if (!File.Exists(path))
                    throw new Exception($"文件不存在：{path}");
                var voxels = VoxelExporter.Read(path, VoxelExporter.MaxBlocks);
                if (voxels.Unsupported is { } unsupported)
                    return new { ok = false, path, unsupported };
                var stats = voxels.Blocks
                    .GroupBy(b => LabStripState(b.State))
                    .Select(g => new { id = g.Key, count = g.Count() })
                    .OrderByDescending(x => x.count)
                    .ToList();
                const int previewLimit = 8000;
                var stride = Math.Max(1, voxels.Blocks.Count / previewLimit);
                var preview = new List<object>();
                for (var i = 0; i < voxels.Blocks.Count && preview.Count < previewLimit; i += stride)
                {
                    var block = voxels.Blocks[i];
                    preview.Add(new
                    {
                        block.X,
                        block.Y,
                        block.Z,
                        id = LabStripState(block.State),
                    });
                }
                return new
                {
                    ok = true,
                    path,
                    sizeX = voxels.SizeX,
                    sizeY = voxels.SizeY,
                    sizeZ = voxels.SizeZ,
                    truncated = voxels.Truncated,
                    totalBlocks = voxels.Blocks.Count,
                    distinctBlocks = stats.Count,
                    stats = stats.Take(60),
                    preview,
                    previewTruncated = preview.Count < voxels.Blocks.Count,
                };
            }

            case "lab.assets":
            {
                // 投影预览用 Win 端同一套渲染器（three.js + deepslate）。
                // 它需要的不只是方块坐标，还有 blockstates / models / textures，
                // 这些由 Core 的 ProjectionAssetCollector 从客户端 jar 里抽（Win 端也是这条路）。
                var assetsPath = LabSafePath(p.GetProperty("path").GetString()!);
                var budget = LabInt(p, "budget", VoxelExporter.MaxBlocks);
                var assets = new MinecraftAssets(() => _settings);
                var voxels = VoxelExporter.Read(assetsPath, budget, assets);
                var pack = ProjectionAssetCollector.Collect(voxels.BlockIds, assets.Read);
                return new
                {
                    sizeX = voxels.SizeX,
                    sizeY = voxels.SizeY,
                    sizeZ = voxels.SizeZ,
                    truncated = voxels.Truncated,
                    assetSource = assets.SourceLabel ?? "",
                    blocks = voxels.Blocks.Select(b => new object[] { b.X, b.Y, b.Z, b.State }),
                    states = pack.BlockStates,
                    models = pack.Models,
                    textures = pack.Textures.ToDictionary(
                        kv => kv.Key,
                        kv => "data:image/png;base64," + Convert.ToBase64String(kv.Value)),
                };
            }

            case "lab.worldEater":
            {
                var options = new WorldEaterOptions(
                    Length: LabInt(p, "length", 64),
                    Width: LabInt(p, "width", 16),
                    Layers: LabInt(p, "layers", 3),
                    Spacing: LabInt(p, "spacing", 2),
                    LayerGap: LabInt(p, "layerGap", 1),
                    Offset: LabInt(p, "offset", 2));
                var error = WorldEater.Validate(options);
                if (error is not null) throw new Exception(error);
                var dir = Path.Combine(LauncherPaths.DataDir, "lab");
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir,
                    $"world-eater-{DateTime.Now:yyyyMMdd-HHmmss}.litematic");
                var path = WorldEater.GenerateLitematic(options, dest);
                return new { ok = true, path };
            }

            case "projections.scan":
            {
                var files = _projections.Scan();
                return new
                {
                    files = files.Select(f => new
                    {
                        filePath = f.FilePath,
                        fileName = f.FileName,
                        sizeBytes = f.SizeBytes,
                        modifiedAt = f.ModifiedAt.ToString("O"),
                        versionId = f.VersionId,
                    }),
                    versions = _projections.ListVersions(),
                };
            }

            case "projections.copy":
            {
                var src = LabSafePath(p.GetProperty("filePath").GetString()!);
                var versionId = LabNullableString(p, "versionId");
                var dest = _projections.CopyTo(src, versionId);
                return new { ok = true, path = dest };
            }

            case "projections.move":
            {
                var src = LabSafePath(p.GetProperty("filePath").GetString()!);
                var versionId = LabNullableString(p, "versionId");
                var dest = _projections.MoveTo(src, versionId);
                return new { ok = true, path = dest };
            }

            case "projections.delete":
            {
                var src = LabSafePath(p.GetProperty("filePath").GetString()!);
                _projections.Delete(src);
                return new { ok = true };
            }

            case "version.settings":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                return VersionSettingsPayload(versionId);
            }

            case "version.saveSettings":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                if (!InstanceLayout.IsValidVersionId(versionId))
                    throw new Exception("非法版本 id");
                var store = new InstanceSettingsStore(_settings.DotMinecraft);
                var profile = store.Get(versionId);
                if (p.TryGetProperty("isolation", out var iso))
                {
                    profile.Isolation = iso.ValueKind == JsonValueKind.String &&
                                        iso.GetString() is "shared" or "isolated"
                        ? iso.GetString()
                        : null;
                }
                if (p.TryGetProperty("memoryMode", out var mm))
                    profile.MemoryMode = NormalizeMode(mm);
                if (p.TryGetProperty("memoryMinMb", out var mn))
                    profile.MemoryMinMb = ReadNullableInt(mn);
                if (p.TryGetProperty("memoryMaxMb", out var mx))
                    profile.MemoryMaxMb = ReadNullableInt(mx);
                if (p.TryGetProperty("javaMode", out var jm))
                    profile.JavaMode = NormalizeMode(jm);
                if (p.TryGetProperty("javaPath", out var jp))
                    profile.JavaPath = jp.ValueKind == JsonValueKind.String
                        ? jp.GetString()
                        : null;
                if (p.TryGetProperty("windowMode", out var wm))
                    profile.WindowMode = NormalizeMode(wm);
                if (p.TryGetProperty("windowWidth", out var ww))
                    profile.WindowWidth = ReadNullableInt(ww);
                if (p.TryGetProperty("windowHeight", out var wh))
                    profile.WindowHeight = ReadNullableInt(wh);
                if (p.TryGetProperty("fullscreen", out var fs))
                    profile.Fullscreen = ReadNullableBool(fs);
                store.Set(versionId, profile);
                store.Save();
                return VersionSettingsPayload(versionId);
            }

            case "version.checkUpdates":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var ctx = VersionContentContext(versionId);
                var modsDir = Path.Combine(ctx.GameRoot, "mods");
                if (!Directory.Exists(modsDir)) return new { updates = Array.Empty<object>() };
                var files = Directory.EnumerateFiles(modsDir)
                    .Where(f => f.Contains(".jar", StringComparison.OrdinalIgnoreCase))
                    .Take(200)
                    .ToList();
                var updates = new List<object>();
                var gate = new SemaphoreSlim(4);
                var tasks = files.Select(async file =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var result = await _contentLibrary.CheckUpdateAsync(
                            file, ctx.McVersion, ctx.Loader);
                        if (result is null) return;
                        lock (updates)
                        {
                            updates.Add(new
                            {
                                result.FileName,
                                result.CurrentVersion,
                                result.LatestVersion,
                                result.LatestFileName,
                                result.LatestUrl,
                                result.LatestSha1,
                            });
                        }
                    }
                    catch
                    {
                        // 单个文件检查失败不阻塞其它更新
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
                await Task.WhenAll(tasks);
                return new { updates };
            }

            case "version.updateMod":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var fileName = SafeFileName(p.GetProperty("fileName").GetString()!);
                var ctx = VersionContentContext(versionId);
                var modsDir = Path.Combine(ctx.GameRoot, "mods");
                var oldPath = Path.Combine(modsDir, fileName);
                if (!File.Exists(oldPath)) throw new Exception($"文件不存在：{fileName}");
                var update = await _contentLibrary.CheckUpdateAsync(
                    oldPath, ctx.McVersion, ctx.Loader)
                    ?? throw new Exception("该模组没有可用更新");
                Emit(stdout, "content.progress",
                    new { projectId = "", stage = $"下载 {update.LatestFileName}" });
                var entry = await _contentLibrary.DownloadAndAdoptAsync(
                    "mod", update.LatestUrl, update.LatestFileName, update.LatestSha1,
                    new LibraryAdoptInfo(
                        Path.GetFileNameWithoutExtension(update.LatestFileName),
                        null, null, null, null, update.LatestVersion,
                        new List<string>(), new List<string>(), null,
                        ContentSourceIds.Modrinth));
                var newPath = await _contentLibrary.InstallToVersionAsync(entry, versionId);
                // 原本停用的模组更新后保持停用。
                if (fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) &&
                    !newPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
                {
                    var disabledPath = newPath + ".disabled";
                    File.Move(newPath, disabledPath, overwrite: true);
                    newPath = disabledPath;
                }
                if (!string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase))
                {
                    try { File.Delete(oldPath); } catch { /* 占用中则保留旧文件 */ }
                }
                return new
                {
                    ok = true,
                    fileName = Path.GetFileName(newPath),
                    oldFileName = fileName,
                    latestVersion = update.LatestVersion,
                };
            }

            case "version.checkDependencies":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var ctx = VersionContentContext(versionId);
                var modsDir = Path.Combine(ctx.GameRoot, "mods");
                var missing = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                var optional = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                var incompatible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                if (!Directory.Exists(modsDir))
                {
                    return new
                    {
                        missing = Array.Empty<object>(),
                        optional = Array.Empty<object>(),
                        incompatible = Array.Empty<string>(),
                        truncated = false,
                    };
                }

                var files = Directory.EnumerateFiles(modsDir)
                    .Where(f => f.Contains(".jar", StringComparison.OrdinalIgnoreCase))
                    .Take(200)
                    .ToList();
                var hashes = await Task.Run(() => files.Select(f =>
                    Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(f)))).ToList());
                var identities = await _contentLibrary.ResolveIdentitiesAsync(hashes);
                var installedProjects = identities.Values
                    .Select(i => i.ProjectId)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
                var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var inspected = 0;

                var gate = new SemaphoreSlim(4);
                var tasks = identities.Values.Select(async identity =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        var versions = await _modrinth.ListVersionsAsync(
                            identity.ProjectId, ctx.Loader, ctx.McVersion);
                        var version = versions.FirstOrDefault(v => v.Id == identity.VersionId);
                        if (version is null)
                        {
                            versions = await _modrinth.ListVersionsAsync(
                                identity.ProjectId, null, null);
                            version = versions.FirstOrDefault(v => v.Id == identity.VersionId);
                        }
                        if (version is null) return;
                        Interlocked.Increment(ref inspected);

                        foreach (var dep in version.Dependencies)
                        {
                            if (dep.ProjectId is null) continue;
                            switch (dep.DependencyType)
                            {
                                case "required":
                                    if (installedProjects.Contains(dep.ProjectId)) break;
                                    var title = await TitleOf(dep.ProjectId, titles);
                                    lock (missing)
                                    {
                                        missing[dep.ProjectId] = new
                                        {
                                            projectId = dep.ProjectId,
                                            title,
                                            versionId = dep.VersionId,
                                            fileName = dep.FileName,
                                        };
                                    }
                                    break;
                                case "optional":
                                    var optionalTitle = await TitleOf(dep.ProjectId, titles);
                                    lock (optional)
                                    {
                                        optional[dep.ProjectId] = new
                                        {
                                            projectId = dep.ProjectId,
                                            title = optionalTitle,
                                        };
                                    }
                                    break;
                                case "incompatible":
                                    if (!installedProjects.Contains(dep.ProjectId)) break;
                                    var incompatibleTitle = await TitleOf(dep.ProjectId, titles);
                                    lock (incompatible)
                                    {
                                        incompatible.Add(incompatibleTitle);
                                    }
                                    break;
                            }
                        }
                    }
                    catch
                    {
                        // 单个模组解析失败不影响整体报告
                    }
                    finally
                    {
                        gate.Release();
                    }
                });
                await Task.WhenAll(tasks);
                return new
                {
                    missing = missing.Values,
                    optional = optional.Values,
                    incompatible = incompatible.ToArray(),
                    truncated = files.Count >= 200,
                    checkedMods = files.Count,
                    resolvedMods = identities.Count,
                    inspectedVersions = inspected,
                };
            }

            case "version.detail":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var ctx = VersionContentContext(versionId);
                return new
                {
                    versionId,
                    gameRoot = ctx.GameRoot,
                    isolated = ctx.Isolated,
                    mcVersion = ctx.McVersion,
                    loader = ctx.Loader,
                    loaderVersion = ctx.LoaderVersion,
                    lastPlayedAt = new InstanceSettingsStore(_settings.DotMinecraft)
                        .Get(versionId).LastPlayedAt?.ToString("O"),
                    mods = ContentFiles(ctx.GameRoot, "mod"),
                    resourcepacks = ContentFiles(ctx.GameRoot, "resourcepack"),
                    shaderpacks = ContentFiles(ctx.GameRoot, "shader"),
                };
            }

            case "version.toggleContent":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var kind = NormalizeKind(p.GetProperty("kind").GetString());
                var fileName = SafeFileName(p.GetProperty("fileName").GetString()!);
                var enabled = p.GetProperty("enabled").GetBoolean();
                var ctx = VersionContentContext(versionId);
                var dir = Path.Combine(ctx.GameRoot, InstanceLayout.ContentDirFor(kind));
                var current = Path.Combine(dir, fileName);
                if (!File.Exists(current)) throw new Exception($"文件不存在：{fileName}");
                var disabled = current.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                if (enabled != disabled) return new { files = ContentFiles(ctx.GameRoot, kind) };
                var target = enabled ? current[..^".disabled".Length] : current + ".disabled";
                if (File.Exists(target)) throw new Exception("目标文件已存在，未做改名");
                File.Move(current, target);
                return new { files = ContentFiles(ctx.GameRoot, kind) };
            }

            case "version.deleteContent":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var kind = NormalizeKind(p.GetProperty("kind").GetString());
                var fileName = SafeFileName(p.GetProperty("fileName").GetString()!);
                var ctx = VersionContentContext(versionId);
                var path = Path.Combine(
                    ctx.GameRoot, InstanceLayout.ContentDirFor(kind), fileName);
                if (!File.Exists(path)) throw new Exception($"文件不存在：{fileName}");
                File.Delete(path);
                return new { files = ContentFiles(ctx.GameRoot, kind) };
            }

            case "version.openContentFolder":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var kind = NormalizeKind(p.GetProperty("kind").GetString());
                var ctx = VersionContentContext(versionId);
                var dir = Path.Combine(ctx.GameRoot, InstanceLayout.ContentDirFor(kind));
                Directory.CreateDirectory(dir);
                OpenFolder(dir);
                return new { ok = true, path = dir };
            }

            case "content.search":
            {
                var kind = NormalizeKind(p.GetProperty("kind").GetString());
                var projectType = kind switch
                {
                    "mod" => "mod",
                    "resourcepack" => "resourcepack",
                    _ => "shader",
                };
                var query = p.TryGetProperty("query", out var q) &&
                            q.ValueKind == JsonValueKind.String
                    ? q.GetString() ?? ""
                    : "";
                var gameVersion = p.TryGetProperty("gameVersion", out var gv) &&
                                  gv.ValueKind == JsonValueKind.String
                    ? gv.GetString()
                    : null;
                var loader = p.TryGetProperty("loader", out var ld) &&
                             ld.ValueKind == JsonValueKind.String
                    ? ld.GetString()
                    : null;
                var offset = p.TryGetProperty("offset", out var off) && off.TryGetInt32(out var o)
                    ? o
                    : 0;
                var limit = p.TryGetProperty("limit", out var lim) && lim.TryGetInt32(out var l)
                    ? l
                    : 20;
                var result = await _modrinth.SearchAsync(
                    query, projectType, loader, gameVersion, offset, limit);
                return new
                {
                    totalHits = result.TotalHits,
                    hits = result.Hits.Select(h => new
                    {
                        h.ProjectId,
                        h.Slug,
                        h.Title,
                        h.Description,
                        h.Author,
                        h.Downloads,
                        h.IconUrl,
                        h.DisplayCategories,
                        h.Loaders,
                        h.WebUrl,
                    }),
                };
            }

            case "content.install":
            {
                var kind = NormalizeKind(p.GetProperty("kind").GetString());
                var projectId = p.GetProperty("projectId").GetString()!;
                var title = p.TryGetProperty("title", out var t) &&
                            t.ValueKind == JsonValueKind.String
                    ? t.GetString()
                    : null;
                var targetVersionId = p.GetProperty("targetVersionId").GetString()!;
                var ctx = VersionContentContext(targetVersionId);
                var installed = new List<object>();
                var missing = new List<object>();
                var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                async Task InstallOneAsync(
                    string pid, string? versionHint, string? displayTitle, int depth)
                {
                    if (depth > 5 || !visited.Add(pid)) return;
                    var versions = await _modrinth.ListVersionsAsync(
                        pid, ctx.Loader, ctx.McVersion);
                    if (versions.Count == 0)
                        versions = await _modrinth.ListVersionsAsync(pid, ctx.Loader, null);
                    if (versions.Count == 0)
                        versions = await _modrinth.ListVersionsAsync(pid, null, ctx.McVersion);
                    if (versions.Count == 0)
                    {
                        missing.Add(new { projectId = pid, reason = "没有兼容版本" });
                        return;
                    }
                    var version = versionHint is null
                        ? null
                        : versions.FirstOrDefault(v => v.Id == versionHint);
                    version ??= versions[0];
                    var file = version.PrimaryFile;
                    if (file is null)
                    {
                        missing.Add(new { projectId = pid, reason = "没有可下载文件" });
                        return;
                    }
                    var name = displayTitle ??
                               await _modrinth.GetProjectTitleAsync(pid) ?? pid;
                    Emit(stdout, "content.progress",
                        new { projectId = pid, stage = $"下载 {file.Filename}" });
                    var entry = await _contentLibrary.DownloadAndAdoptAsync(
                        kind, file.Url, file.Filename, file.Sha1,
                        new LibraryAdoptInfo(
                            name, null, null, pid, version.Id, version.VersionNumber,
                            version.GameVersions, version.Loaders,
                            $"https://modrinth.com/project/{pid}",
                            ContentSourceIds.Modrinth));
                    var path = await _contentLibrary.InstallToVersionAsync(
                        entry, targetVersionId);
                    installed.Add(new
                    {
                        projectId = pid,
                        title = name,
                        fileName = entry.FileName,
                        versionNumber = entry.VersionNumber,
                        path,
                    });
                    foreach (var dep in version.Dependencies.Where(d =>
                                 d.DependencyType == "required" &&
                                 !string.IsNullOrEmpty(d.ProjectId)))
                    {
                        await InstallOneAsync(dep.ProjectId!, dep.VersionId, null, depth + 1);
                    }
                }

                await InstallOneAsync(projectId, null, title, 0);
                return new { installed, missing };
            }

            case "nova.config.get":
            {
                var profile = _settings.ActiveLlmProfile();
                return new
                {
                    baseUrl = profile?.BaseUrl ?? "",
                    apiKey = profile?.ApiKey ?? "",
                    model = profile?.Model ?? "",
                };
            }

            case "nova.config.set":
            {
                var baseUrl = p.TryGetProperty("baseUrl", out var bu) &&
                              bu.ValueKind == JsonValueKind.String
                    ? bu.GetString() ?? ""
                    : "";
                var apiKey = p.TryGetProperty("apiKey", out var ak) &&
                             ak.ValueKind == JsonValueKind.String
                    ? ak.GetString() ?? ""
                    : "";
                var model = p.TryGetProperty("model", out var md) &&
                            md.ValueKind == JsonValueKind.String
                    ? md.GetString() ?? ""
                    : "";
                if (baseUrl.Length == 0 && apiKey.Length == 0 && model.Length == 0)
                {
                    _settings.NovaLlmProfiles.Clear();
                    _settings.NovaLlmActiveId = null;
                }
                else
                {
                    var profile = _settings.ActiveLlmProfile()
                        ?? new NovaLlmProfile { Id = "custom", Name = "自定义模型" };
                    profile.BaseUrl = baseUrl;
                    profile.ApiKey = apiKey;
                    profile.Model = model;
                    if (string.IsNullOrWhiteSpace(profile.Name))
                        profile.Name = "自定义模型";
                    _settings.NovaLlmProfiles.Clear();
                    _settings.NovaLlmProfiles.Add(profile);
                    _settings.NovaLlmActiveId = profile.Id;
                }
                _settings.Save();
                return new { baseUrl, apiKey, model };
            }

            case "nova.plan.execute":
            {
                var planId = p.GetProperty("id").GetString()!;
                if (!PendingNovaPlans.TryRemove(planId, out var plan))
                    throw new Exception("确认清单已失效，请重新生成");
                var message = await plan.ExecuteAsync(CancellationToken.None);
                return new { ok = true, message };
            }

            case "nova.plan.discard":
            {
                var planId = p.GetProperty("id").GetString()!;
                PendingNovaPlans.TryRemove(planId, out _);
                return new { ok = true };
            }

            case "multiplayer.state":
            {
                var snap = _multiplayer.Snapshot ?? await _multiplayer.RefreshAsync();
                return MultiplayerPayload(snap);
            }

            case "multiplayer.selectProvider":
            {
                _multiplayer.SelectProvider(p.GetProperty("id").GetString()!);
                return MultiplayerPayload(await _multiplayer.RefreshAsync());
            }

            case "multiplayer.setNickname":
            {
                _multiplayer.Nickname = p.GetProperty("name").GetString() ?? "";
                return MultiplayerPayload(await _multiplayer.RefreshAsync());
            }

            case "multiplayer.setNodeRegion":
            {
                _multiplayer.NodeRegion = p.GetProperty("region").GetString() ?? "auto";
                return MultiplayerPayload(await _multiplayer.RefreshAsync());
            }

            case "multiplayer.host":
            {
                await _multiplayer.HostAsync(CancellationToken.None);
                return MultiplayerPayload(await _multiplayer.RefreshAsync());
            }

            case "multiplayer.join":
            {
                await _multiplayer.JoinAsync(
                    p.GetProperty("room").GetString()!, CancellationToken.None);
                return MultiplayerPayload(await _multiplayer.RefreshAsync());
            }

            case "multiplayer.idle":
            {
                await _multiplayer.IdleAsync(CancellationToken.None);
                return MultiplayerPayload(await _multiplayer.RefreshAsync());
            }

            case "multiplayer.stop":
            {
                await _multiplayer.StopAsync(CancellationToken.None);
                return MultiplayerPayload(await _multiplayer.RefreshAsync());
            }

            case "multiplayer.logs":
                return new { logs = await _multiplayer.FetchLogsAsync(CancellationToken.None) };

            case "multiplayer.nodes":
                return new { nodes = await _multiplayer.FetchNodesAsync(CancellationToken.None) };

            case "java.list":
            {
                var runtimes = await _java.ListRuntimesAsync(
                    CancellationToken.None,
                    Path.Combine(_settings.DotMinecraft, "runtime"));
                return new
                {
                    runtimes = runtimes.Select(r => new
                    {
                        r.Executable, r.MajorVersion, r.VersionString, r.Is64Bit, r.Source,
                    }),
                };
            }

            case "accounts.list":
                return new
                {
                    accounts = _accounts.Accounts.Select(AccountPayload),
                    current = _accounts.Default?.Uuid,
                };

            case "accounts.addOffline":
            {
                var name = p.GetProperty("name").GetString() ?? "";
                _accounts.AddOrUpdateOffline(name);
                return new { uuid = AccountStore.OfflineUuid(name), name };
            }

            case "accounts.setCurrent":
            {
                // 首选按类型各记一个（Core 语义）：换离线首选不会清掉正版首选。
                var uuid = p.GetProperty("uuid").GetString()!;
                _accounts.SetDefault(uuid);
                return new { ok = true };
            }

            case "accounts.remove":
            {
                var uuid = p.GetProperty("uuid").GetString()!;
                _accounts.Remove(uuid);
                return new { ok = true };
            }

            case "accounts.setSkin":
            {
                var uuid = p.GetProperty("uuid").GetString()!;
                string? skinId = p.TryGetProperty("skinId", out var sid) &&
                                 sid.ValueKind == JsonValueKind.String
                    ? sid.GetString()
                    : null;
                string? model = p.TryGetProperty("model", out var md) &&
                                md.ValueKind == JsonValueKind.String
                    ? md.GetString()
                    : null;
                var account = _accounts.SetSkin(uuid, skinId, model)
                    ?? throw new Exception($"账户不存在：{uuid}");
                return new { account = AccountPayload(account) };
            }

            case "skins.list":
                return new { skins = _skins.List().Select(SkinPayload) };

            case "skins.importPath":
            {
                var path = p.GetProperty("path").GetString()!;
                string? name = p.TryGetProperty("name", out var nm) &&
                               nm.ValueKind == JsonValueKind.String
                    ? nm.GetString()
                    : null;
                var (entry, error) = _skins.ImportPath(path, name);
                if (entry is null) throw new Exception(error ?? "导入失败");
                return new { skin = SkinPayload(entry) };
            }

            case "skins.importUrl":
            {
                var url = p.GetProperty("url").GetString()!;
                string? name = p.TryGetProperty("name", out var nm) &&
                               nm.ValueKind == JsonValueKind.String
                    ? nm.GetString()
                    : null;
                var (entry, error) = await _skins.ImportUrlAsync(url, name, _download);
                if (entry is null) throw new Exception(error ?? "导入失败");
                return new { skin = SkinPayload(entry) };
            }

            case "skins.rename":
            {
                var id = p.GetProperty("id").GetString()!;
                var name = p.GetProperty("name").GetString()!;
                var (ok, error) = _skins.Rename(id, name);
                if (!ok) throw new Exception(error ?? "重命名失败");
                return new { skin = SkinPayload(_skins.Find(id)!) };
            }

            case "skins.setModel":
            {
                var id = p.GetProperty("id").GetString()!;
                var model = SkinModels.Normalize(p.GetProperty("model").GetString());
                if (!_skins.SetModel(id, model))
                    throw new Exception("皮肤不存在或型号非法");
                return new { skin = SkinPayload(_skins.Find(id)!) };
            }

            case "skins.delete":
            {
                var id = p.GetProperty("id").GetString()!;
                if (!_skins.Delete(id)) throw new Exception("皮肤不存在");
                // 账户上指向这张皮肤的引用一并清掉（与 Windows 端一致）。
                _accounts.ClearSkinReference(id);
                return new { ok = true };
            }

            case "skins.station.list":
            {
                var uuid = p.GetProperty("uuid").GetString()!;
                var account = _accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid)
                    ?? throw new Exception("账户不存在");
                if (string.IsNullOrEmpty(account.ServerBase))
                    throw new Exception("该账户没有认证服务器地址");
                var token = _accounts.GetSession(uuid)?.AccessToken
                    ?? throw new Exception("第三方账户登录已过期，请重新登录");
                var list = await _skinStation.ListAsync(account.ServerBase, token);
                return new
                {
                    skins = list.Select(s => new
                    {
                        s.Id,
                        s.Name,
                        s.Model,
                        s.Url,
                        s.Slim,
                    }),
                };
            }

            case "skins.station.apply":
            {
                var uuid = p.GetProperty("uuid").GetString()!;
                var skinId = p.GetProperty("skinId").GetString()!;
                var account = _accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid)
                    ?? throw new Exception("账户不存在");
                if (string.IsNullOrEmpty(account.ServerBase))
                    throw new Exception("该账户没有认证服务器地址");
                var token = _accounts.GetSession(uuid)?.AccessToken
                    ?? throw new Exception("第三方账户登录已过期，请重新登录");
                var list = await _skinStation.ListAsync(account.ServerBase, token);
                var skin = list.FirstOrDefault(s => s.Id == skinId)
                    ?? throw new Exception("皮肤站衣柜里没有这张皮肤");
                var message = await _skinStation.ApplyAsync(
                    account.ServerBase, token, skin);
                return new { message };
            }

            case "auth.microsoft.start":
            {
                var code = await _msAuth.StartDeviceCodeAsync();
                PendingDeviceCodes[code.DeviceCode] = code;
                return new
                {
                    userCode = code.UserCode,
                    deviceCode = code.DeviceCode,
                    verificationUri = code.VerificationUri,
                    interval = code.Interval,
                };
            }

            case "auth.microsoft.poll":
            {
                var deviceCode = p.GetProperty("deviceCode").GetString()!;
                if (!PendingDeviceCodes.TryGetValue(deviceCode, out var code))
                    throw new Exception("设备码已失效，请重新开始登录");
                var (state, message, result) = await _msAuth.PollAsync(code);
                if (state == "pending") return new { state, message };
                PendingDeviceCodes.TryRemove(deviceCode, out _);
                if (state == "error" || result is null)
                    throw new Exception(message);
                var account = _accounts.AddOrUpdateMicrosoft(result.Uuid, result.Username);
                _accounts.SetSession(result.Uuid,
                    new AccountSession(result.AccessToken, result.RefreshToken, result.Xuid));
                if (result.RefreshToken is { Length: > 0 } bridgeRefresh)
                    MicrosoftTokenStore.SaveNew(result.Uuid, bridgeRefresh);
                return new
                {
                    state = "success",
                    account = new
                    {
                        uuid = account.Uuid,
                        name = account.Username,
                        type = account.Type,
                    },
                };
            }

            case "auth.yggdrasil.login":
            {
                var serverUrl = p.GetProperty("url").GetString()!;
                var username = p.GetProperty("username").GetString()!;
                var password = p.GetProperty("password").GetString()!;
                var server = await _yggdrasil.ResolveServerAsync(serverUrl);
                // clientToken 是设备标识（非鉴权 token）：同服务器同用户复用，否则新生成。
                var existing = _accounts.Accounts.FirstOrDefault(a =>
                    a.Type == "external" && a.Username == username &&
                    (a.ServerBase == server.Base || a.ServerAlias == serverUrl));
                var clientToken = string.IsNullOrEmpty(existing?.ClientToken)
                    ? Guid.NewGuid().ToString()
                    : AsUuid(existing!.ClientToken!);
                var result = await _yggdrasil.AuthenticateAsync(
                    server.Base, username, password, clientToken);
                var account = _accounts.AddOrUpdateExternal(
                    result.ProfileId, result.ProfileName, server.Base, serverUrl, server.ServerName);
                _accounts.SetSession(result.ProfileId, new AccountSession(result.AccessToken));
                return new
                {
                    state = "success",
                    account = new
                    {
                        uuid = account.Uuid,
                        name = account.Username,
                        type = account.Type,
                        serverName = account.ServerName,
                    },
                };
            }

            case "download.text":
            {
                var url = p.GetProperty("url").GetString()!;
                return await _download.GetStringAsync(url);
            }

            case "download.file":
            {
                var url = p.GetProperty("url").GetString()!;
                var dest = p.GetProperty("dest").GetString()!;
                string? sha1 = p.TryGetProperty("sha1", out var s1) && s1.ValueKind == JsonValueKind.String
                    ? s1.GetString()
                    : null;

                // 字节进度：DownloadService 的回调是**增量**（分片下载会并发回调），
                // 这里累加并按 5 Hz 节流成事件；total 未知时按契约填 -1。
                var gate = new object();
                long received = 0;
                var lastEmit = 0L;
                void OnBytes(long increment)
                {
                    long snapshot = 0;
                    var emit = false;
                    lock (gate)
                    {
                        received += increment;
                        var now = Environment.TickCount64;
                        if (now - lastEmit >= 200)
                        {
                            lastEmit = now;
                            snapshot = received;
                            emit = true;
                        }
                    }
                    if (emit)
                    {
                        Emit(stdout, "download.progress",
                            new { url, received = snapshot, total = -1L, done = false });
                    }
                }

                await _download.DownloadFileAsync(
                    url, dest, sha1, _settings.DownloadMaxRetries, onBytes: OnBytes);
                long total;
                lock (gate) total = received;
                Emit(stdout, "download.progress",
                    new { url, received = total, total, done = true });
                return new { ok = true, bytes = total };
            }

            case "shell.openPath":
            {
                // 打开目录 / 在文件夹中显示。Linux 下走 xdg-open，Windows 走 explorer。
                var target = p.GetProperty("path").GetString()!;
                if (string.IsNullOrWhiteSpace(target)) throw new Exception("路径为空");
                var full = Path.GetFullPath(target);
                var dir = Directory.Exists(full) ? full : Path.GetDirectoryName(full);
                if (dir is null || !Directory.Exists(dir))
                    throw new Exception($"路径不存在：${target}");
                OpenFolder(dir);
                return new { ok = true, path = dir };
            }

            case "app.paths":
                return new
                {
                    dataDir = LauncherPaths.DataDir,
                    logDir = LauncherPaths.LogsDir,
                    logFile = LauncherLog.CurrentFile,
                };
            case "settings.get":
                return SnapshotSettings();

            case "settings.apply":
            {
                if (p.TryGetProperty("gameDir", out var gd))
                    _settings.DotMinecraft = gd.GetString() ?? _settings.DotMinecraft;
                if (p.TryGetProperty("javaExecutable", out var je))
                    _settings.JavaExecutable = je.GetString() ?? _settings.JavaExecutable;
                if (p.TryGetProperty("maxMemoryMb", out var mm) && mm.TryGetInt32(out var mb))
                    _settings.DefaultMaxMemoryMb = mb;
                if (p.TryGetProperty("downloadSource", out var ds))
                    _settings.DownloadSource = ds.GetString() ?? _settings.DownloadSource;
                if (p.TryGetProperty("minMemoryMb", out var mn) && mn.TryGetInt32(out var mnv))
                    _settings.DefaultMinMemoryMb = mnv;
                if (p.TryGetProperty("downloadConcurrency", out var dc) && dc.TryGetInt32(out var dcv))
                    _settings.DownloadConcurrency = Math.Clamp(dcv, 1, 64);
                if (p.TryGetProperty("downloadMaxRetries", out var dr) && dr.TryGetInt32(out var drv))
                    _settings.DownloadMaxRetries = Math.Clamp(drv, 0, 10);
                if (p.TryGetProperty("downloadCustomMirrorUrl", out var dm))
                    _settings.DownloadCustomMirrorUrl = dm.GetString() ?? "";
                if (p.TryGetProperty("versionIsolated", out var vi) &&
                    (vi.ValueKind == JsonValueKind.True || vi.ValueKind == JsonValueKind.False))
                    _settings.VersionIsolated = vi.GetBoolean();
                if (p.TryGetProperty("useBuiltinJvmArgs", out var uj) &&
                    (uj.ValueKind == JsonValueKind.True || uj.ValueKind == JsonValueKind.False))
                    _settings.UseBuiltinJvmArgs = uj.GetBoolean();
                if (p.TryGetProperty("extraJvmArgs", out var ea) && ea.ValueKind == JsonValueKind.Array)
                    _settings.ExtraJvmArgs = ea.EnumerateArray()
                        .Where(x => x.ValueKind == JsonValueKind.String)
                        .Select(x => x.GetString()!)
                        .Where(x => x.Length > 0)
                        .ToList();
                if (p.TryGetProperty("language", out var lg))
                    _settings.Language = lg.GetString() ?? _settings.Language;
                if (p.TryGetProperty("theme", out var th))
                    _settings.Theme = th.GetString() ?? _settings.Theme;
                if (p.TryGetProperty("accountWalkAnimation", out var aw) &&
                    (aw.ValueKind == JsonValueKind.True || aw.ValueKind == JsonValueKind.False))
                    _settings.AccountWalkAnimation = aw.GetBoolean();
                if (p.TryGetProperty("microsoftLoginEmbedded", out var ml) &&
                    (ml.ValueKind == JsonValueKind.True || ml.ValueKind == JsonValueKind.False))
                    _settings.MicrosoftLoginEmbedded = ml.GetBoolean();
                if (p.TryGetProperty("curseforgeApiKey", out var cf))
                    _settings.CurseForgeApiKey = cf.GetString() ?? "";
                if (p.TryGetProperty("windowWidth", out var ww) && ww.TryGetInt32(out var wwv))
                    _settings.DefaultWindowWidth = Math.Clamp(wwv, 320, 7680);
                if (p.TryGetProperty("windowHeight", out var wh) && wh.TryGetInt32(out var whv))
                    _settings.DefaultWindowHeight = Math.Clamp(whv, 240, 4320);
                if (p.TryGetProperty("fullscreen", out var fs2) && IsBool(fs2))
                    _settings.DefaultFullscreen = fs2.GetBoolean();
                if (p.TryGetProperty("globalEnvVars", out var gev) && gev.ValueKind == JsonValueKind.Object)
                {
                    var env = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var kv in gev.EnumerateObject())
                        if (kv.Value.ValueKind == JsonValueKind.String)
                            env[kv.Name] = kv.Value.GetString() ?? "";
                    _settings.GlobalEnvVars = env;
                }
                if (p.TryGetProperty("launchAutoRetry", out var lar) && IsBool(lar))
                    _settings.LaunchAutoRetry = lar.GetBoolean();
                if (p.TryGetProperty("showGameLogOnLaunch", out var sgl) && IsBool(sgl))
                    _settings.ShowGameLogOnLaunch = sgl.GetBoolean();
                if (p.TryGetProperty("novaOverlayEnabled", out var noe) && IsBool(noe))
                    _settings.NovaOverlayEnabled = noe.GetBoolean();
                if (p.TryGetProperty("resourceCacheEnabled", out var rce) && IsBool(rce))
                    _settings.ResourceCacheEnabled = rce.GetBoolean();
                if (p.TryGetProperty("motionLevel", out var mvl))
                    _settings.UiMotionLevel = mvl.GetString() ?? "";
                if (p.TryGetProperty("backgroundMode", out var bgm))
                    _settings.BackgroundMode = bgm.GetString() ?? "none";
                if (p.TryGetProperty("backgroundBuiltinId", out var bgi))
                    _settings.BackgroundBuiltinId = bgi.GetString() ?? "";
                if (p.TryGetProperty("backgroundImagePath", out var bgp))
                    _settings.BackgroundImagePath = bgp.GetString() ?? "";
                if (p.TryGetProperty("backgroundImageOpacity", out var bgo) && bgo.TryGetDouble(out var bgov))
                    _settings.BackgroundImageOpacity = Math.Clamp(bgov, 0, 1);
                if (p.TryGetProperty("backgroundBlurRadius", out var bgb) && bgb.TryGetDouble(out var bgbv))
                    _settings.BackgroundBlurRadius = Math.Clamp(bgbv, 0, 64);
                if (p.TryGetProperty("backgroundMaskOpacity", out var bgk) && bgk.TryGetDouble(out var bgkv))
                    _settings.BackgroundMaskOpacity = Math.Clamp(bgkv, 0, 1);
                if (p.TryGetProperty("backgroundFollowTheme", out var bgf) && IsBool(bgf))
                    _settings.BackgroundFollowTheme = bgf.GetBoolean();
                if (p.TryGetProperty("deviceLinkEnabled", out var dle) && IsBool(dle))
                    _settings.DeviceLinkEnabled = dle.GetBoolean();
                if (p.TryGetProperty("deviceName", out var dvn))
                    _settings.DeviceName = dvn.GetString() ?? "";
                if (p.TryGetProperty("p2pDownloadEnabled", out var p2p) && IsBool(p2p))
                    _settings.P2pDownloadEnabled = p2p.GetBoolean();
                if (p.TryGetProperty("autoUpdateEnabled", out var aue) && IsBool(aue))
                    _settings.AutoUpdateEnabled = aue.GetBoolean();
                if (p.TryGetProperty("updateManifestUrl", out var umu))
                    _settings.UpdateManifestUrl = umu.GetString() ?? "";
                _settings.Save();
                return SnapshotSettings();
            }

            case "launch.start":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var playerName = p.TryGetProperty("playerName", out var pn) &&
                                 pn.ValueKind == JsonValueKind.String
                    ? pn.GetString() ?? ""
                    : "";
                var accountUuid = p.TryGetProperty("accountUuid", out var au) &&
                                  au.ValueKind == JsonValueKind.String
                    ? au.GetString()
                    : null;

                // 账户解析：显式 uuid → 默认账户 → 按玩家名补一个离线身份。
                StoredAccount? account = null;
                if (!string.IsNullOrEmpty(accountUuid))
                    account = _accounts.Accounts.FirstOrDefault(a => a.Uuid == accountUuid);
                account ??= _accounts.Default;
                if (account is null && !string.IsNullOrWhiteSpace(playerName))
                    account = _accounts.AddOrUpdateOffline(playerName);
                if (account is null)
                    throw new Exception("没有可用的游戏账户，请先在账户页添加");

                // 与 Windows 端同一套实例层叠：隔离三态、内存/Java/窗口覆盖。
                var dotMc = _settings.DotMinecraft;
                var profile = new InstanceSettingsStore(dotMc).Get(versionId);
                var isolated = InstanceLayout.IsIsolated(profile, _settings);
                var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
                var eff = InstanceLayout.Resolve(
                    dotMc, versionId, profile, _settings, MemoryAdvisor.Probe(gameRoot));

                LaunchCredentials credentials;
                string? agentJar = null;
                string? agentBase = null;
                switch (account.Type)
                {
                    case "microsoft":
                    {
                        var msSession = _accounts.GetSession(account.Uuid)
                            ?? throw new Exception("微软账户的登录已过期，请在账户页重新登录");
                        credentials = new LaunchCredentials(
                            account.Uuid, msSession.AccessToken, "msa", msSession.Xuid);
                        break;
                    }
                    case "external":
                    {
                        var extSession = _accounts.GetSession(account.Uuid)
                            ?? throw new Exception("第三方账户的登录已过期，请在账户页重新登录");
                        agentJar = AgentJarPath()
                            ?? throw new Exception("缺少 authlib-injector，无法启动第三方账户");
                        agentBase = account.ServerBase
                            ?? throw new Exception("第三方账户缺少认证服务器地址");
                        credentials = new LaunchCredentials(
                            account.Uuid, extSession.AccessToken, "legacy");
                        break;
                    }
                    default:
                        credentials = new LaunchCredentials(account.Uuid, "0", "legacy");
                        break;
                }

                var options = new LaunchOptions(
                    VersionId: versionId,
                    PlayerName: account.Username,
                    GameDir: eff.GameRoot,
                    SharedDir: dotMc,
                    MinMemoryMb: eff.MinMem,
                    MaxMemoryMb: eff.MaxMem,
                    JavaExecutable: eff.Java,
                    ExtraJvmArgs: eff.ExtraJvmArgs,
                    UseBuiltinJvmArgs: _settings.UseBuiltinJvmArgs,
                    Credentials: credentials,
                    Language: eff.Language,
                    LanguageOverride: eff.LanguageOverride,
                    WindowWidth: eff.WindowW,
                    WindowHeight: eff.WindowH,
                    Fullscreen: eff.Fullscreen,
                    EnvVars: eff.EnvVars,
                    PreLaunchCommand: eff.PreCmd,
                    PostLaunchCommand: eff.PostCmd,
                    AgentJarPath: agentJar,
                    AgentBase: agentBase,
                    // 本地皮肤：启动器起 127.0.0.1 回环服务托管 PNG，经 userProperties 注入。
                    PlayerSkinUrl: OfflineSkins.UrlFor(account.Uuid));

                var session = await _launcher.LaunchVersionAsync(
                    _settings.DotMinecraft, versionId, options);
                var sessionId = ++_nextSessionId;
                Sessions[sessionId] = session;
                session.OutputLine += line =>
                    Emit(stdout, "game.output", new { sessionId, line });
                // 退出事件只触发一次；进程可能在订阅前就已退出（参数错误/缺 jar），
                // 用 Interlocked 兜底补发，避免 Dart 侧 exitCode 永远不完成。
                var exitRaised = 0;
                void RaiseExited(int code)
                {
                    if (Interlocked.Exchange(ref exitRaised, 1) != 0) return;
                    Emit(stdout, "game.exited", new { sessionId, code });
                    Sessions.TryRemove(sessionId, out _);
                }
                session.Exited += RaiseExited;
                if (session.HasExited) RaiseExited(session.ExitCode);

                // 记住"最近启动的版本"（版本页/首页的默认项），并刷新实例游玩时间。
                _settings.LastVersion = versionId;
                _settings.Save();
                try
                {
                    new InstanceSettingsStore(_settings.DotMinecraft).TouchPlayed(versionId);
                }
                catch { /* 档案写失败不影响启动 */ }

                return new { sessionId, pid = session.Pid, logFile = session.LogFile };
            }

            case "game.kill":
            {
                var sessionId = p.GetProperty("sessionId").GetInt32();
                if (!Sessions.TryGetValue(sessionId, out var session))
                    return new { ok = false, alreadyExited = true };
                session.Kill(entireTree: true);
                return new { ok = true };
            }

            case "starcloud.state":
                return new
                {
                    loggedIn = _starcloudId.IsLoggedIn,
                    userId = _starcloudId.Current?.UserId,
                    displayName = _starcloudId.Current?.DisplayName,
                    channel = _starcloudId.Current?.Channel,
                    loginAt = _starcloudId.Current?.LoginAt,
                };

            case "starcloud.login":
            {
                var session = await _starcloudId.LoginAsync(
                    p.GetProperty("id").GetString() ?? "",
                    p.GetProperty("password").GetString() ?? "");
                return new
                {
                    loggedIn = true,
                    userId = session.UserId,
                    displayName = session.DisplayName,
                    channel = session.Channel,
                    loginAt = session.LoginAt,
                };
            }

            case "starcloud.sendCode":
            {
                var retryAfter = await _starcloudId.SendEmailCodeAsync(
                    p.GetProperty("email").GetString() ?? "");
                return new { retryAfter };
            }

            case "starcloud.register":
            {
                await _starcloudId.RegisterAsync(
                    p.GetProperty("id").GetString() ?? "",
                    p.GetProperty("password").GetString() ?? "",
                    p.GetProperty("email").GetString() ?? "",
                    p.GetProperty("code").GetString() ?? "");
                return new { ok = true };
            }

            case "starcloud.logout":
                _starcloudId.Logout();
                return new { ok = true };

            case "friends.list":
                return new
                {
                    signedIn = _friends.IsSignedIn,
                    friends = await _friends.GetFriendsAsync(),
                };

            case "friends.requests":
                return new
                {
                    signedIn = _friends.IsSignedIn,
                    requests = await _friends.GetRequestsAsync(),
                };

            case "friends.search":
            {
                var results = await _friends.SearchAsync(p.GetProperty("query").GetString() ?? "");
                return new { results };
            }

            case "friends.sendRequest":
            {
                await _friends.SendRequestAsync(
                    p.GetProperty("target").GetString() ?? "",
                    p.TryGetProperty("message", out var reqMsg) ? reqMsg.GetString() : null);
                return new { ok = true };
            }

            case "friends.acceptRequest":
                await _friends.AcceptRequestAsync(p.GetProperty("requestId").GetString()!);
                return new { ok = true };

            case "friends.rejectRequest":
                await _friends.RejectRequestAsync(p.GetProperty("requestId").GetString()!);
                return new { ok = true };

            case "friends.cancelRequest":
                await _friends.CancelRequestAsync(p.GetProperty("requestId").GetString()!);
                return new { ok = true };

            case "friends.remove":
                await _friends.RemoveFriendAsync(p.GetProperty("userId").GetString()!);
                return new { ok = true };

            case "friends.invites":
                return new
                {
                    signedIn = _friends.IsSignedIn,
                    invites = await _friends.GetInvitesAsync(),
                };

            case "friends.createInvite":
            {
                var invite = await _friends.CreateInviteAsync(
                    p.GetProperty("userId").GetString() ?? "",
                    p.TryGetProperty("address", out var invAddr) ? invAddr.GetString() : null,
                    p.TryGetProperty("port", out var invPort) && invPort.ValueKind == JsonValueKind.Number
                        ? invPort.GetInt32()
                        : null,
                    p.TryGetProperty("roomCode", out var invRoom) ? invRoom.GetString() : null,
                    p.TryGetProperty("message", out var invMsg) ? invMsg.GetString() : null);
                return new { invite };
            }

            case "friends.acceptInvite":
                await _friends.AcceptInviteAsync(p.GetProperty("inviteId").GetString()!);
                return new { ok = true };

            case "friends.declineInvite":
                await _friends.DeclineInviteAsync(p.GetProperty("inviteId").GetString()!);
                return new { ok = true };

            case "friends.cancelInvite":
                await _friends.CancelInviteAsync(p.GetProperty("inviteId").GetString()!);
                return new { ok = true };

            case "friends.conversation":
            {
                long? beforeId = p.TryGetProperty("beforeId", out var convBefore) &&
                                convBefore.ValueKind == JsonValueKind.Number
                    ? convBefore.GetInt64()
                    : null;
                var page = await _friends.GetConversationAsync(
                    p.GetProperty("userId").GetString()!, beforeId);
                return new { page };
            }

            case "friends.send":
            {
                var message = await _friends.SendMessageAsync(
                    p.GetProperty("userId").GetString()!,
                    p.GetProperty("content").GetString() ?? "");
                return new { message };
            }

            case "friends.markRead":
            {
                var updated = await _friends.MarkReadAsync(p.GetProperty("userId").GetString()!);
                return new { updated };
            }
            case "versions.rename":
            {
                var oldId = p.GetProperty("versionId").GetString()!;
                var newId = p.GetProperty("newName").GetString()!.Trim();
                if (!InstanceLayout.IsValidVersionId(oldId) || !InstanceLayout.IsValidVersionId(newId))
                    throw new Exception("版本名不能为空，且不能包含 < > : \" / \\ | ? *");
                if (newId != oldId)
                {
                    InstanceLayout.RenameInstance(_settings.DotMinecraft, oldId, newId);
                    new InstanceSettingsStore(_settings.DotMinecraft).RenameKey(oldId, newId);
                }
                return new { ok = true, versionId = newId };
            }

            case "versions.copy":
            {
                var srcId = p.GetProperty("versionId").GetString()!;
                var newId = p.GetProperty("newId").GetString()!.Trim();
                if (!InstanceLayout.IsValidVersionId(srcId) || !InstanceLayout.IsValidVersionId(newId))
                    throw new Exception("版本名不能为空，且不能包含 < > : \" / \\ | ? *");
                var srcStore = new InstanceSettingsStore(_settings.DotMinecraft);
                var copyContent = p.TryGetProperty("copyContent", out var cc) && cc.GetBoolean();
                InstanceLayout.CopyInstance(_settings.DotMinecraft, srcId, newId, copyContent);
                // 档案深拷贝（Win 同款）：类别/备注等沿用，时间与图标从零开始
                var copy = JsonSerializer.Deserialize<VersionProfile>(
                    JsonSerializer.Serialize(srcStore.Get(srcId))) ?? new VersionProfile();
                copy.LastPlayedAt = null;
                copy.IconPath = null;
                srcStore.Set(newId, copy);
                srcStore.Save();
                return new { ok = true, versionId = newId };
            }

            case "versions.exportProbe":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var (gameVersion, deps) = ResolvePackDeps(_settings.DotMinecraft, versionId);
                var isolated = InstanceLayout.IsIsolated(
                    new InstanceSettingsStore(_settings.DotMinecraft).Get(versionId), _settings);
                var gameRoot = InstanceLayout.GameRoot(_settings.DotMinecraft, versionId, isolated);
                return new
                {
                    versionId,
                    gameVersion,
                    dependencies = deps,
                    loader = DescribePackLoader(deps),
                    dirs = EnumerateContentDirs(gameRoot),
                };
            }

            case "versions.exportModpack":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var store = new InstanceSettingsStore(_settings.DotMinecraft);
                var isolated = InstanceLayout.IsIsolated(store.Get(versionId), _settings);
                var gameRoot = InstanceLayout.GameRoot(_settings.DotMinecraft, versionId, isolated);
                var outPath = p.GetProperty("outPath").GetString()!;
                var format = p.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String
                    ? fmt.GetString()
                    : "modrinth";
                var deps = new Dictionary<string, string>();
                if (p.TryGetProperty("dependencies", out var depEl) && depEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in depEl.EnumerateObject())
                        deps[kv.Name] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.ToString();
                }
                if (deps.Count == 0)
                {
                    var (_, auto) = ResolvePackDeps(_settings.DotMinecraft, versionId);
                    deps = new Dictionary<string, string>(auto);
                }
                var dirs = new List<string>();
                if (p.TryGetProperty("dirs", out var dirsEl) && dirsEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in dirsEl.EnumerateArray())
                    {
                        var name = d.GetString();
                        if (!string.IsNullOrWhiteSpace(name)) dirs.Add(name);
                    }
                }
                if (dirs.Count == 0) throw new Exception("请至少勾选一个内容目录。");
                var exclude = new List<string>();
                if (p.TryGetProperty("excludeRules", out var exEl) && exEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var e in exEl.EnumerateArray())
                    {
                        var r = e.GetString();
                        if (!string.IsNullOrWhiteSpace(r)) exclude.Add(r);
                    }
                }
                var request = new ExportRequest(
                    gameRoot, outPath,
                    p.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String && nEl.GetString() is { Length: > 0 } nm ? nm : versionId,
                    p.TryGetProperty("gameVersion", out var gvEl) && gvEl.ValueKind == JsonValueKind.String && gvEl.GetString() is { Length: > 0 } gv ? gv : deps.GetValueOrDefault("minecraft", ""),
                    deps, dirs)
                {
                    Format = format == "curseforge" ? ModPackExportFormat.CurseForge
                        : format == "multimc" ? ModPackExportFormat.MultiMc
                        : ModPackExportFormat.Modrinth,
                    VersionId = p.TryGetProperty("packageVersion", out var pvEl) && pvEl.ValueKind == JsonValueKind.String && pvEl.GetString() is { Length: > 0 } pv ? pv : "1.0.0",
                    Author = p.TryGetProperty("author", out var aEl) ? aEl.GetString() ?? "" : "",
                    Description = p.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "",
                    ExcludeRules = exclude,
                    IncludeGameSettings = p.TryGetProperty("includeGameSettings", out var igs) && igs.GetBoolean(),
                    UnresolvedAsOverrides = !p.TryGetProperty("unresolvedAsOverrides", out var uao) || uao.GetBoolean(),
                };
                var exportStatus = new Progress<string>(st =>
                    Emit(stdout, "modpack.progress", new { stage = st, percent = -1 }));
                var result = await _modPackExport.ExportAsync(request, exportStatus);
                return new
                {
                    ok = true,
                    path = outPath,
                    result.DownloadableFiles,
                    result.OverriddenFiles,
                    result.SkippedFiles,
                    result.TotalBytes,
                    result.LoaderWarning,
                };
            }

            case "versions.resolvePackLink":
            {
                var link = p.GetProperty("url").GetString() ?? "";
                var (url, fileName) = await ResolvePackUrlAsync(link, CancellationToken.None);
                return new { url, fileName };
            }

            case "versions.downloadPack":
            {
                var url = p.GetProperty("url").GetString()!;
                var fileName = Path.GetFileName(p.TryGetProperty("fileName", out var fnEl)
                    ? fnEl.GetString() ?? "" : new Uri(url).AbsolutePath);
                if (fileName.Length == 0) fileName = "modpack.zip";
                var ext = fileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ? ".mrpack" : ".zip";
                var dir = Path.Combine(Path.GetTempPath(), "starcloud-pack");
                Directory.CreateDirectory(dir);
                var dest = Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");
                Emit(stdout, "modpack.progress", new { stage = $"正在下载 {fileName}…", percent = -1 });
                await _download.DownloadFileAsync(url, dest, null, 3);
                return new { path = dest, fileName };
            }

            case "versions.packPreview":
            {
                var packPath = p.GetProperty("packPath").GetString()!;
                var probe = ModPackDetector.Detect(packPath)
                    ?? throw new Exception("未能识别整合包格式。支持 Modrinth（.mrpack）、CurseForge（manifest.json）与 MultiMC / Prism（mmc-pack.json）。");
                string name; string gameVersion = "";
                var deps = new Dictionary<string, string>();
                int fileCount; long totalBytes; var contentRoot = probe.Root;
                switch (probe.Format)
                {
                    case ModPackFormat.CurseForge:
                    {
                        var info = _cfPacks.ReadPackageInfo(packPath);
                        name = info.Name; gameVersion = info.GameVersion;
                        if (!string.IsNullOrWhiteSpace(info.GameVersion)) deps["minecraft"] = info.GameVersion;
                        if (CurseForgePackInstaller.ParseLoader(info.LoaderId) is var (lname, lver) && lname is not null)
                            deps[lname] = lver ?? "";
                        fileCount = info.FileCount + info.OverrideFileCount;
                        totalBytes = info.TotalBytes;
                        break;
                    }
                    case ModPackFormat.MultiMc:
                    {
                        var info = _multiMcPacks.ReadPackageInfo(packPath);
                        name = info.Name; gameVersion = info.GameVersion;
                        foreach (var kv in info.Dependencies) deps[kv.Key] = kv.Value;
                        fileCount = info.FileCount; totalBytes = info.TotalBytes;
                        break;
                    }
                    case ModPackFormat.Raw:
                    {
                        var (count, bytes) = MeasurePackPrefix(packPath, probe.Root);
                        name = Path.GetFileNameWithoutExtension(packPath);
                        fileCount = count; totalBytes = bytes;
                        break;
                    }
                    default:
                    {
                        var info = _mrPack.ReadPackageInfo(packPath);
                        name = info.Name; gameVersion = info.GameVersion;
                        foreach (var kv in info.Dependencies) deps[kv.Key] = kv.Value;
                        fileCount = info.DownloadFileCount + info.OverrideFileCount;
                        totalBytes = info.TotalBytes;
                        break;
                    }
                }
                return new
                {
                    format = probe.Format.ToString().ToLowerInvariant(),
                    name, gameVersion, dependencies = deps,
                    loader = DescribePackLoader(deps),
                    fileCount, totalBytes, contentRoot,
                };
            }

            case "versions.importPack":
            {
                var packPath = p.GetProperty("packPath").GetString()!;
                var probe = ModPackDetector.Detect(packPath)
                    ?? throw new Exception("未能识别整合包格式。");
                var effective = probe.Format == ModPackFormat.Raw
                    ? packPath
                    : ModPackArchive.FlattenIfNested(packPath, probe);
                try
                {
                    var targetId = p.GetProperty("targetId").GetString()!.Trim();
                    if (!InstanceLayout.IsValidVersionId(targetId))
                        throw new Exception("版本名不能为空，且不能包含 < > : \" / \\ | ? *");
                    var isolated = !p.TryGetProperty("isolated", out var isoEl) || isoEl.GetBoolean();
                    var renamed = false;
                    if (isolated && Directory.Exists(Path.Combine(_settings.DotMinecraft, "versions", targetId)))
                    {
                        targetId = NextVersionName(_settings.DotMinecraft, targetId);
                        renamed = true;
                    }
                    var installRoot = isolated
                        ? Path.Combine(_settings.DotMinecraft, "versions", targetId)
                        : _settings.DotMinecraft;
                    var progress = new Progress<BatchProgress>(b =>
                        Emit(stdout, "modpack.progress", new { stage = b.Stage, percent = b.Percentage }));
                    if (isolated) Directory.CreateDirectory(installRoot);
                    var contentRoot = p.TryGetProperty("contentRoot", out var crEl) ? crEl.GetString() ?? "" : "";
                    var mcVersion = p.TryGetProperty("mcVersion", out var mcEl) ? mcEl.GetString() ?? "" : "";
                    var result = probe.Format switch
                    {
                        ModPackFormat.CurseForge => await _cfPacks.InstallAsync(effective, _settings, installRoot, null, progress),
                        ModPackFormat.MultiMc => await _multiMcPacks.InstallAsync(effective, _settings, installRoot, null, progress),
                        ModPackFormat.Raw => await InstallPackRawAsync(effective, contentRoot, installRoot, mcVersion, progress),
                        _ => await _mrPack.InstallAsync(effective, _settings, installRoot, null, progress),
                    };
                    var summary = await EnsurePackGameAndLoaderAsync(result, progress);
                    if (isolated)
                    {
                        var parent = PackLoaderVersionId(_settings.DotMinecraft, result.GameVersion, result.Dependencies)
                            ?? result.GameVersion;
                        var jsonPath = Path.Combine(installRoot, $"{targetId}.json");
                        if (!File.Exists(jsonPath) && parent.Length > 0)
                        {
                            var skeleton = new { id = targetId, inheritsFrom = parent, type = "release" };
                            File.WriteAllText(jsonPath, JsonSerializer.Serialize(skeleton, JsonOpts));
                        }
                        var store = new InstanceSettingsStore(_settings.DotMinecraft);
                        store.Set(targetId, new VersionProfile { Isolation = "isolated" });
                        store.Save();
                    }
                    return new { ok = true, targetId, renamed, isolated, summary, gameVersion = result.GameVersion };
                }
                finally
                {
                    if (!ReferenceEquals(effective, packPath))
                    {
                        try { File.Delete(effective); } catch { }
                    }
                }
            }

            case "versions.backupList":
            {
                var versionId = p.TryGetProperty("versionId", out var blEl) && blEl.ValueKind == JsonValueKind.String
                    ? blEl.GetString()
                    : null;
                return new { entries = _versionBackup.List(_settings.DotMinecraft, versionId) };
            }

            case "versions.backupCreate":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var store = new InstanceSettingsStore(_settings.DotMinecraft);
                var isolated = InstanceLayout.IsIsolated(store.Get(versionId), _settings);
                var dirs = new List<string>();
                if (p.TryGetProperty("dirs", out var bdEl) && bdEl.ValueKind == JsonValueKind.Array)
                {
                    foreach (var d in bdEl.EnumerateArray())
                    {
                        var dirName = d.GetString();
                        if (!string.IsNullOrWhiteSpace(dirName)) dirs.Add(dirName);
                    }
                }
                var includeSaves = !p.TryGetProperty("includeSaves", out var isEl) || isEl.GetBoolean();
                var backupName = p.TryGetProperty("name", out var bnEl) && bnEl.ValueKind == JsonValueKind.String
                    && bnEl.GetString() is { Length: > 0 } bn ? bn : "备份";
                var status = new Progress<string>(st =>
                    Emit(stdout, "modpack.progress", new { stage = st, percent = -1 }));
                var entry = await _versionBackup.BackupAsync(_settings.DotMinecraft,
                    new VersionBackupRequest(versionId, isolated, backupName, dirs, includeSaves), status);
                return new { ok = true, entry };
            }

            case "versions.backupRestore":
            {
                var entryId = p.GetProperty("entryId").GetString()!;
                var versionId = p.TryGetProperty("versionId", out var brEl) ? brEl.GetString() : null;
                var entry = _versionBackup.List(_settings.DotMinecraft, versionId)
                    .FirstOrDefault(e => e.Id == entryId)
                    ?? throw new Exception("找不到这条备份（索引可能已重建）。");
                var store = new InstanceSettingsStore(_settings.DotMinecraft);
                var isolated = InstanceLayout.IsIsolated(store.Get(entry.VersionId), _settings);
                var status = new Progress<string>(s =>
                    Emit(stdout, "modpack.progress", new { stage = s, percent = -1 }));
                await _versionBackup.RestoreAsync(_settings.DotMinecraft, entry, isolated, status);
                return new { ok = true, entryId };
            }

            case "versions.backupDelete":
                _versionBackup.Delete(_settings.DotMinecraft, p.GetProperty("entryId").GetString()!);
                return new { ok = true };

            case "versions.backupRebuild":
                return new { ok = true, entries = _versionBackup.RebuildIndex(_settings.DotMinecraft) };
            case "lab.palette":
                return new
                {
                    colors = McText.LegacyColors.Select(c => new { code = c.Code.ToString(), name = c.Name, hex = c.Hex }),
                };

            case "lab.colorText":
            {
                var text = p.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : "";
                var mode = p.TryGetProperty("mode", out var mEl) ? mEl.GetString() ?? "solid" : "solid";
                var hex = p.TryGetProperty("hexColor", out var hEl) ? hEl.GetString() ?? "#FFD322" : "#FFD322";
                var fromHex = p.TryGetProperty("fromHex", out var fEl) ? fEl.GetString() ?? "#FFD322" : "#FFD322";
                var toHex = p.TryGetProperty("toHex", out var toEl) ? toEl.GetString() ?? "#00AAFF" : "#00AAFF";
                var legacy = p.TryGetProperty("legacyPalette", out var lEl) && lEl.GetBoolean();
                var exact = !p.TryGetProperty("exact", out var eEl) || eEl.GetBoolean();
                var segment = p.TryGetProperty("segmentLength", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                    ? Math.Max(1, sEl.GetInt32())
                    : 1;
                var bold = p.TryGetProperty("bold", out var bEl) && bEl.GetBoolean();
                var italic = p.TryGetProperty("italic", out var iEl) && iEl.GetBoolean();
                var underline = p.TryGetProperty("underline", out var uEl) && uEl.GetBoolean();
                var strike = p.TryGetProperty("strikethrough", out var stEl) && stEl.GetBoolean();
                var magic = p.TryGetProperty("obfuscated", out var obEl) && obEl.GetBoolean();
                var style = McText.StyleCodes(bold, italic, underline, strike, magic);
                var result = mode switch
                {
                    "gradient" => McText.Gradient(text, fromHex, toHex, exact, segment, style),
                    "rainbow" => McText.Rainbow(text, exact, segment, style),
                    _ => McText.Solid(text, hex, legacy, style),
                };
                return new { ok = true, text = result };
            }

            case "lab.commands":
                return new
                {
                    categories = CommandHelper.Categories,
                    templates = CommandHelper.Templates.Select(t => new
                    {
                        t.Id, t.Category, t.Name, t.Template, t.Note,
                        @params = t.Params.Select(x => new
                        {
                            x.Key, x.Label, defaultValue = x.Default, x.Suggestions, x.Required,
                        }),
                    }),
                };

            case "lab.buildCommand":
            {
                var templateId = p.GetProperty("templateId").GetString()!;
                var template = CommandHelper.Templates.FirstOrDefault(t => t.Id == templateId)
                    ?? throw new Exception($"未知指令模板：{templateId}");
                var values = new Dictionary<string, string>();
                if (p.TryGetProperty("values", out var vEl) && vEl.ValueKind == JsonValueKind.Object)
                {
                    foreach (var kv in vEl.EnumerateObject())
                        values[kv.Name] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.ToString();
                }
                var command = CommandHelper.Build(template, values);
                return new
                {
                    ok = true,
                    command,
                    hasUnfilled = CommandHelper.HasUnfilled(command),
                    error = CommandHelper.Validate(command),
                };
            }

            case "lab.communitySearch":
            {
                var query = p.TryGetProperty("query", out var qEl) ? qEl.GetString() ?? "" : "";
                var force = p.TryGetProperty("forceRefresh", out var frEl) && frEl.GetBoolean();
                var (items, failed) = await _projectionCommunity.SearchAsync(query, force);
                return new { items, failedSources = failed, sources = _projectionCommunity.SourceNames };
            }

            case "lab.communityFiles":
            {
                var item = ReadCommunityItem(p.GetProperty("item"));
                var files = await _projectionCommunity.ListFilesAsync(item);
                return new { files };
            }

            case "lab.communityDownload":
            {
                var file = new CommunityFile(
                    p.GetProperty("fileName").GetString() ?? "projection.litematic",
                    p.TryGetProperty("sizeBytes", out var szEl) && szEl.ValueKind == JsonValueKind.Number ? szEl.GetInt64() : 0,
                    p.GetProperty("downloadUrl").GetString()!);
                var dir = Path.Combine(_settings.DotMinecraft, "schematics");
                Directory.CreateDirectory(dir);
                var saved = await _projectionCommunity.DownloadAsync(file, dir);
                return new { ok = true, path = saved };
            }
            default:
                throw new Exception($"未知方法：{method}");
        }
    }

    private static async Task RunInstallAsync(TextWriter stdout, int id, string versionId)
    {
        try
        {
            var entry = await FindManifestEntryAsync(versionId)
                        ?? throw new Exception($"版本 {versionId} 不在官方清单里");
            var progress = new Progress<BatchProgress>(b => Emit(stdout, "install.progress", new
            {
                versionId,
                stage = b.Stage,
                detail = $"{b.DoneFiles}/{b.TotalFiles}",
                current = b.DoneFiles,
                total = b.TotalFiles,
                percent = b.Percentage,
                speedBytesPerSec = b.SpeedBytesPerSec,
            }));
            await _installer.InstallVanillaAsync(versionId, entry.Url, _settings, progress);
            Emit(stdout, "install.done", new { versionId });
            Reply(stdout, id, ok: true, result: new { versionId, installed = true });
        }
        catch (Exception e)
        {
            Emit(stdout, "install.failed", new { versionId, error = e.Message });
            Reply(stdout, id, ok: false, error: e.Message);
        }
    }

    private static async Task<ManifestVersion?> FindManifestEntryAsync(string versionId)
    {
        var manifest = await _manifest.GetManifestAsync(forceRefresh: false);
        return manifest.Versions.FirstOrDefault(v => v.Id == versionId);
    }

    // ── 整合包 / 备份辅助（对齐 Win 端 ModPackDialogs / MrPackFlow） ──────

    private static IProgress<BatchProgress> PackProgress(TextWriter stdout) =>
        new Progress<BatchProgress>(b =>
            Emit(stdout, "modpack.progress", new { stage = b.Stage, percent = b.Percentage }));

    /// <summary>推断 MC 版本与加载器依赖（JSON inheritsFrom/libraries → 目录名兜底）。</summary>
    private static (string GameVersion, Dictionary<string, string> Deps) ResolvePackDeps(
        string dotMc, string versionId)
    {
        var found = new Dictionary<string, string>();
        var game = versionId;
        var jsonPath = Path.Combine(dotMc, "versions", versionId, $"{versionId}.json");
        string? raw = null;
        try { if (File.Exists(jsonPath)) raw = File.ReadAllText(jsonPath); } catch { }
        if (raw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var rootEl = doc.RootElement;
                if (rootEl.TryGetProperty("inheritsFrom", out var inh) &&
                    inh.ValueKind == JsonValueKind.String && inh.GetString() is { Length: > 0 } parent)
                    game = parent;
                else if (rootEl.TryGetProperty("id", out var idEl) &&
                         idEl.ValueKind == JsonValueKind.String && idEl.GetString() is { Length: > 0 } self)
                    game = self;
                if (rootEl.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lib in libs.EnumerateArray())
                    {
                        if (!lib.TryGetProperty("name", out var nameEl)) continue;
                        var name = nameEl.GetString();
                        if (string.IsNullOrEmpty(name)) continue;
                        var parts = name.Split(':');
                        if (parts.Length < 3) continue;
                        var (group, artifact, ver) = (parts[0], parts[1], parts[2]);
                        if (group == "net.fabricmc" && artifact == "fabric-loader") found["fabric-loader"] = ver;
                        else if (group == "org.quiltmc" && artifact == "quilt-loader") found["quilt-loader"] = ver;
                        else if (group == "net.minecraftforge" && artifact == "forge")
                            found["forge"] = TrimMcVersion(ver, game);
                        else if (group == "net.neoforged" && artifact == "neoforge") found["neoforge"] = ver;
                    }
                }
            }
            catch { }
        }
        if (found.Count == 0)
        {
            var m = Regex.Match(versionId, @"^(fabric-loader|quilt-loader)-(.+?)-(\d[\w.]*)$",
                RegexOptions.IgnoreCase);
            if (m.Success) found[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;
            else if ((m = Regex.Match(versionId, @"-forge-(.+)$", RegexOptions.IgnoreCase)).Success)
                found["forge"] = m.Groups[1].Value;
            else if ((m = Regex.Match(versionId, @"neoforge-(.+)$", RegexOptions.IgnoreCase)).Success)
                found["neoforge"] = m.Groups[1].Value;
        }
        var deps = new Dictionary<string, string> { ["minecraft"] = game };
        foreach (var kv in found) deps[kv.Key] = kv.Value;
        return (game, deps);
    }

    private static string TrimMcVersion(string version, string mc)
    {
        var prefix = mc + "-";
        return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? version[prefix.Length..]
            : version;
    }

    private static string DescribePackLoader(IReadOnlyDictionary<string, string> deps)
    {
        var parts = new List<string>();
        if (deps.TryGetValue("fabric-loader", out var f)) parts.Add($"Fabric {f}");
        if (deps.TryGetValue("quilt-loader", out var q)) parts.Add($"Quilt {q}");
        if (deps.TryGetValue("forge", out var fo)) parts.Add($"Forge {fo}");
        if (deps.TryGetValue("neoforge", out var n)) parts.Add($"NeoForge {n}");
        return string.Join(" · ", parts);
    }

    /// <summary>实例根下的顶层内容目录（排除运行期/公共目录），带文件数与体积。</summary>
    private static object EnumerateContentDirs(string gameRoot)
    {
        var standard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "mods", "config", "resourcepacks", "shaderpacks", "kubejs", "scripts", "defaultconfigs" };
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "logs", "crash-reports", "caches", ".mixin.out", ".fabric", ".quilt" };
        if (!Directory.Exists(gameRoot)) return Array.Empty<object>();
        return Directory.EnumerateDirectories(gameRoot)
            .Select(d => new DirectoryInfo(d))
            .Where(d => !d.Name.StartsWith("."))
            .Where(d => !skip.Contains(d.Name))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d =>
            {
                var files = 0; var bytes = 0L;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d.FullName, "*", SearchOption.AllDirectories))
                    {
                        files++;
                        try { bytes += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
                return new { name = d.Name, fileCount = files, totalBytes = bytes, standard = standard.Contains(d.Name) };
            })
            .ToArray();
    }

    /// <summary>把粘贴的链接解析成整合包直链（Modrinth / CurseForge 页面或压缩包直链）。</summary>
    private static async Task<(string Url, string FileName)> ResolvePackUrlAsync(
        string link, CancellationToken ct)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new Exception("请输入完整的 http(s) 链接。");

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host.EndsWith("modrinth.com", StringComparison.Ordinal))
        {
            var index = Array.FindIndex(segments, x => x.Equals("modpack", StringComparison.OrdinalIgnoreCase) ||
                                                       x.Equals("modpacks", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= segments.Length)
                throw new Exception("不是有效的 Modrinth 整合包链接。");
            var versions = await _modrinth.ListVersionsAsync(segments[index + 1], null, null, ct);
            if (versions.Count == 0) throw new Exception("该整合包没有可下载的文件。");
            var pick = versions[0];
            if (index + 3 < segments.Length && segments[index + 2].Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                var want = segments[index + 3];
                pick = versions.FirstOrDefault(v =>
                           string.Equals(v.VersionNumber, want, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(v.Id, want, StringComparison.OrdinalIgnoreCase)) ?? pick;
            }
            var file = pick.PrimaryFile ?? throw new Exception("该整合包没有可下载的文件。");
            return (file.Url, file.Filename);
        }

        if (host.EndsWith("curseforge.com", StringComparison.Ordinal))
        {
            var index = Array.FindIndex(segments, x => x.Equals("modpacks", StringComparison.OrdinalIgnoreCase) ||
                                                       x.Equals("modpack", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= segments.Length)
                throw new Exception("不是有效的 CurseForge 整合包链接。");
            var slug = segments[index + 1];
            var hits = (await _curseForge.SearchAsync(slug, "modpack", null, null, 0, 20, "relevance", ct)).Hits;
            var hit = hits.FirstOrDefault(h => string.Equals(h.Slug, slug, StringComparison.OrdinalIgnoreCase))
                      ?? hits.FirstOrDefault()
                      ?? throw new Exception("未找到该整合包。");
            var fileId = index + 3 < segments.Length &&
                         segments[index + 2].Equals("files", StringComparison.OrdinalIgnoreCase)
                ? segments[index + 3]
                : null;
            var version = fileId is null
                ? (await _curseForge.ListVersionsAsync(hit.ProjectId, null, null, ct)).FirstOrDefault()
                : await _curseForge.GetFileAsync(hit.ProjectId, fileId, ct);
            var cfFile = version?.PrimaryFile ?? throw new Exception("该整合包没有可下载的文件。");
            return (cfFile.Url, cfFile.Filename);
        }

        var ext = Path.GetExtension(uri.AbsolutePath);
        if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
            throw new Exception("无法识别的链接。请粘贴 Modrinth / CurseForge 整合包页面或压缩包直链。");
        return (uri.ToString(), Path.GetFileName(uri.AbsolutePath));
    }

    /// <summary>统计裸内容包在指定内容根下的文件数 / 字节数。</summary>
    private static (int Count, long Bytes) MeasurePackPrefix(string packPath, string root)
    {
        var prefix = string.IsNullOrEmpty(root) ? "" : root + "/";
        using var zip = PackZip.Open(packPath);
        var count = 0;
        long bytes = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue;
            var name = entry.FullName.Replace('\\', '/');
            if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            bytes += Math.Max(entry.Length, 0);
        }
        return (count, bytes);
    }

    /// <summary>裸内容包：把内容根下的文件原样铺进实例目录（不联网）。</summary>
    private static Task<MrPackInstallResult> InstallPackRawAsync(
        string packPath, string contentRoot, string installRoot, string mcVersion,
        IProgress<BatchProgress>? progress)
    {
        return Task.Run(() =>
        {
            var prefix = string.IsNullOrEmpty(contentRoot) ? "" : contentRoot + "/";
            using var zip = PackZip.Open(packPath);
            var done = 0;
            long doneBytes = 0;
            var (total, totalBytes) = (0, 0L);
            foreach (var entry in zip.Entries)
            {
                if (entry.Name.Length == 0) continue;
                var name = entry.FullName.Replace('\\', '/');
                if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                totalBytes += Math.Max(entry.Length, 0);
            }
            foreach (var entry in zip.Entries)
            {
                if (entry.Name.Length == 0) continue;
                var name = entry.FullName.Replace('\\', '/');
                if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = prefix.Length == 0 ? name : name[prefix.Length..];
                var dest = PackPathGuard.Resolve(installRoot, rel);
                if (dest is null)
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                done++;
                doneBytes += Math.Max(entry.Length, 0);
                if (done % 32 == 0 || done == total)
                    progress?.Report(new BatchProgress("实例文件", done, total, doneBytes, totalBytes, 0, null));
            }
            return new MrPackInstallResult(mcVersion, new Dictionary<string, string>(), done, doneBytes);
        });
    }

    /// <summary>按整合包依赖联动安装原版与加载器；返回中文摘要（Win MrPackFlow 同款）。</summary>
    private static async Task<string> EnsurePackGameAndLoaderAsync(
        MrPackInstallResult result, IProgress<BatchProgress>? progress)
    {
        var game = result.GameVersion;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(game))
        {
            if (!_installer.IsInstalled(_settings.DotMinecraft, game))
            {
                var entry = await FindManifestEntryAsync(game);
                if (entry is not null)
                    await _installer.InstallVanillaAsync(game, entry.Url, _settings, progress);
            }
            parts.Add($"Minecraft {game}");

            var javaMajor = 8;
            try
            {
                var v = await _installer.LoadVersionAsync(_settings.DotMinecraft, game);
                javaMajor = v.JavaVersion?.MajorVersion ?? 8;
            }
            catch { }

            if (result.Dependencies.TryGetValue("fabric-loader", out var fab))
            {
                await _installer.InstallFabricAsync(game, fab, _settings, progress);
                parts.Add($"Fabric {fab}");
            }
            if (result.Dependencies.TryGetValue("quilt-loader", out var quilt))
            {
                await _installer.InstallQuiltAsync(game, quilt, _settings, progress);
                parts.Add($"Quilt {quilt}");
            }
            if (result.Dependencies.TryGetValue("forge", out var forge))
            {
                await _forge.InstallViaInstallerAsync(
                    ForgeInstallerService.ForgeInstallerUrl(game, forge), game, javaMajor, _settings, progress);
                parts.Add($"Forge {forge}");
            }
            if (result.Dependencies.TryGetValue("neoforge", out var neo))
            {
                await _forge.InstallViaInstallerAsync(
                    ForgeInstallerService.NeoForgeInstallerUrl(game, neo), game, javaMajor, _settings, progress);
                parts.Add($"NeoForge {neo}");
            }
        }

        return parts.Count > 0
            ? "已联动安装 " + string.Join(" + ", parts)
            : $"共下载 {result.DownloadedFiles} 个文件";
    }

    /// <summary>名称冲突时递增 " (n)"。</summary>
    private static string NextVersionName(string dotMc, string baseName)
    {
        if (!Directory.Exists(Path.Combine(dotMc, "versions", baseName))) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!Directory.Exists(Path.Combine(dotMc, "versions", candidate))) return candidate;
        }
    }

    /// <summary>联动安装后实际存在的加载器版本目录名。</summary>
    private static string? PackLoaderVersionId(string dotMc, string mc, IReadOnlyDictionary<string, string> deps)
    {
        string? id = null;
        if (deps.TryGetValue("fabric-loader", out var fab) && fab.Length > 0) id = $"fabric-loader-{fab}-{mc}";
        else if (deps.TryGetValue("quilt-loader", out var quilt) && quilt.Length > 0) id = $"quilt-loader-{quilt}-{mc}";
        else if (deps.TryGetValue("forge", out var forge) && forge.Length > 0) id = $"{mc}-forge-{forge}";
        else if (deps.TryGetValue("neoforge", out var neo) && neo.Length > 0) id = $"neoforge-{neo}";
        return id is not null && File.Exists(Path.Combine(dotMc, "versions", id, $"{id}.json")) ? id : null;
    }
    private static CommunityProjection ReadCommunityItem(JsonElement e)
    {
        string S(string key, string fallback = "") =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;
        int I(string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : 0;
        DateTime D(string key) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
            DateTime.TryParse(v.GetString(), out var at) ? at : DateTime.MinValue;
        return new CommunityProjection(
            S("source"), S("name"), S("author"), S("description"), I("stars"), D("updatedAt"),
            S("fullName"), S("htmlUrl"), S("imageUrl"));
    }
    private static bool IsBool(JsonElement e) =>
        e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False;

    private static object SnapshotSettings() => new
    {
        gameDir = _settings.DotMinecraft,
        javaExecutable = _settings.JavaExecutable,
        minMemoryMb = _settings.DefaultMinMemoryMb,
        maxMemoryMb = _settings.DefaultMaxMemoryMb,
        downloadSource = _settings.DownloadSource,
        downloadConcurrency = _settings.DownloadConcurrency,
        downloadMaxRetries = _settings.DownloadMaxRetries,
        downloadCustomMirrorUrl = _settings.DownloadCustomMirrorUrl ?? "",
        versionIsolated = _settings.VersionIsolated,
        useBuiltinJvmArgs = _settings.UseBuiltinJvmArgs,
        extraJvmArgs = _settings.ExtraJvmArgs,
        language = _settings.Language,
        theme = _settings.Theme,
        accountWalkAnimation = _settings.AccountWalkAnimation,
        microsoftLoginEmbedded = _settings.MicrosoftLoginEmbedded,
        curseforgeApiKey = _settings.CurseForgeApiKey,
        windowWidth = _settings.DefaultWindowWidth ?? 854,
        windowHeight = _settings.DefaultWindowHeight ?? 480,
        fullscreen = _settings.DefaultFullscreen,
        globalEnvVars = _settings.GlobalEnvVars,
        launchAutoRetry = _settings.LaunchAutoRetry,
        showGameLogOnLaunch = _settings.ShowGameLogOnLaunch,
        novaOverlayEnabled = _settings.NovaOverlayEnabled,
        resourceCacheEnabled = _settings.ResourceCacheEnabled,
        motionLevel = _settings.UiMotionLevel,
        backgroundMode = _settings.BackgroundMode,
        backgroundBuiltinId = _settings.BackgroundBuiltinId ?? "",
        backgroundImagePath = _settings.BackgroundImagePath ?? "",
        backgroundImageOpacity = _settings.BackgroundImageOpacity,
        backgroundBlurRadius = _settings.BackgroundBlurRadius,
        backgroundMaskOpacity = _settings.BackgroundMaskOpacity ?? 0,
        backgroundFollowTheme = _settings.BackgroundFollowTheme,
        deviceLinkEnabled = _settings.DeviceLinkEnabled,
        deviceName = _settings.DeviceName,
        p2pDownloadEnabled = _settings.P2pDownloadEnabled,
        autoUpdateEnabled = _settings.AutoUpdateEnabled,
        updateManifestUrl = _settings.UpdateManifestUrl ?? "",
    };

    /// <summary>clientToken 规范是 UUID；老数据可能是不带连字符的 32 位十六进制。</summary>
    private static string AsUuid(string token)
    {
        var hex = token.Replace("-", "");
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit)) return token;
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }

    /// <summary>authlib-injector（第三方账户启动必需）：环境变量 → 发布目录 → 仓库资产。</summary>
    private static string? AgentJarPath()
    {
        var env = Environment.GetEnvironmentVariable("STARCLOUD_AGENT_JAR");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env)) return env;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "authlib-injector",
                "authlib-injector-1.2.8.jar"),
            Path.Combine(AppContext.BaseDirectory, "..", "dotnet", "src", "Starcloud.App",
                "Assets", "authlib-injector", "authlib-injector-1.2.8.jar"),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(File.Exists);
    }

    /// <summary>校验存档操作目标：根目录必须在游戏目录内，目录名不得含路径符号。</summary>
    private static (string Root, string Folder) SaveTarget(JsonElement p)
    {
        var root = NormalizeGameRoot(p.GetProperty("gameRoot").GetString()!);
        var folder = p.GetProperty("folderName").GetString()!;
        if (string.IsNullOrWhiteSpace(folder) || folder is "." or ".." ||
            folder.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new Exception("非法存档目录名");
        return (root, folder);
    }

    private static string NormalizeGameRoot(string gameRoot)
    {
        var full = Path.GetFullPath(gameRoot);
        var dotMc = Path.GetFullPath(_settings.DotMinecraft);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(full, dotMc, comparison) &&
            !full.StartsWith(dotMc + Path.DirectorySeparatorChar, comparison))
            throw new Exception("存档根目录不在游戏目录内");
        return full;
    }

    private static void OpenFolder(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
            {
                UseShellExecute = true,
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open", $"\"{dir}\""));
        }
        else
        {
            Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\""));
        }
    }

    private static object AccountPayload(StoredAccount account) => new
    {
        account.Uuid,
        name = account.Username,
        account.Type,
        isDefault = account.IsDefault,
        account.ServerName,
        account.ServerAlias,
        account.SkinId,
        account.SkinModel,
        // 凭证只在内存：登录过期后 hasSession=false，UI 可提示重新登录。
        hasSession = _accounts.GetSession(account.Uuid) is not null,
    };

    private static object SkinPayload(SkinEntry entry) => new
    {
        entry.Id,
        entry.Name,
        entry.Model,
        entry.FileName,
        path = _skins.PathOf(entry),
        entry.Source,
        entry.SourceUrl,
        entry.Width,
        entry.Height,
        addedAt = entry.AddedAt.ToString("O"),
    };

    /// <summary>投影文件路径校验：必须真实存在，且落在游戏目录（全局或某实例）之内。</summary>
    private static string LabSafePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new Exception("投影路径不能为空");
        var full = Path.GetFullPath(filePath);
        if (!File.Exists(full)) throw new Exception($"投影文件不存在：{filePath}");
        var dotMc = Path.GetFullPath(_settings.DotMinecraft);
        if (!full.StartsWith(dotMc + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new Exception("只能管理游戏目录内的投影文件");
        return full;
    }

    private static string? LabNullableString(JsonElement p, string key) =>
        p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string LabStripState(string state)
    {
        var bracket = state.IndexOf('[');
        return bracket < 0 ? state : state[..bracket];
    }

    private static int LabInt(JsonElement p, string key, int fallback) =>
        p.TryGetProperty(key, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;

    // ── 外部插件（v2 沙箱）：清单/权限/安装/启停状态 ─────────────────────────

    /// <summary>与 Windows 端一致的启动器版本（manifest 兼容性校验用）。</summary>
    private const string LauncherVersion = "0.3.7";

    private static readonly object PluginGate = new();

    private static readonly JsonSerializerOptions PluginStateJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class PluginStateFile
    {
        [JsonPropertyName("plugins")]
        public Dictionary<string, PluginEntryState> Plugins { get; set; } = new();

        [JsonPropertyName("pageTipShown")]
        public bool PageTipShown { get; set; }
    }

    private sealed class PluginEntryState
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("disclaimerAccepted")] public bool DisclaimerAccepted { get; set; }
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("panelTipShown")] public bool PanelTipShown { get; set; }
    }

    private static PluginStateFile LoadPluginState()
    {
        try
        {
            if (File.Exists(LauncherPaths.PluginStateFile))
                return JsonSerializer.Deserialize<PluginStateFile>(
                    File.ReadAllText(LauncherPaths.PluginStateFile),
                    PluginStateJsonOpts) ?? new PluginStateFile();
        }
        catch
        {
            // 损坏按全新状态处理（与 Windows 端一致）
        }
        return new PluginStateFile();
    }

    private static void SavePluginState(PluginStateFile state)
    {
        Directory.CreateDirectory(LauncherPaths.PluginsDir);
        var tmp = LauncherPaths.PluginStateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, PluginStateJsonOpts));
        File.Move(tmp, LauncherPaths.PluginStateFile, overwrite: true);
    }

    private static void SetPluginState(
        string id, Action<PluginEntryState> mutate, bool remove = false)
    {
        lock (PluginGate)
        {
            var state = LoadPluginState();
            if (remove)
            {
                state.Plugins.Remove(id);
            }
            else
            {
                if (!state.Plugins.TryGetValue(id, out var entry))
                    state.Plugins[id] = entry = new PluginEntryState();
                mutate(entry);
            }
            SavePluginState(state);
        }
    }

    private static bool IsPluginEnabled(string id) =>
        LoadPluginState().Plugins.TryGetValue(id, out var entry) && entry.Enabled;

    private static string SafePluginId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." ||
            id.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new Exception("非法插件 id");
        return id;
    }

    private static List<(PluginManifest Manifest, string Dir, bool Official)> ScanPlugins()
    {
        var root = LauncherPaths.PluginUnpackedDir;
        var result = new List<(PluginManifest, string, bool)>();
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            var manifest = PluginManifest.ParseFile(manifestPath);
            if (manifest is null || manifest.Validate(LauncherVersion).Count > 0) continue;
            result.Add((manifest, dir, IsOfficialPlugin(manifest, dir, manifestPath)));
        }
        return result
            .OrderBy(p => p.Item1.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static bool IsOfficialPlugin(
        PluginManifest manifest, string dir, string manifestPath)
    {
        if (!manifest.IsApi2) return manifest.VerifyOfficialSignature(manifestPath);
        var integrity = PluginIntegrity.ParseFile(
            Path.Combine(dir, PluginIntegrity.FileName));
        if (integrity is null || integrity.VerifyFiles(dir).Count > 0) return false;
        return integrity.Verify(out _) == PluginTrustState.Verified;
    }

    private static object PluginPayload(
        PluginManifest manifest, string dir, bool official, PluginStateFile state)
    {
        state.Plugins.TryGetValue(manifest.Id, out var entry);
        return new
        {
            id = manifest.Id,
            name = manifest.Name,
            version = manifest.Version,
            apiVersion = manifest.ApiVersion,
            author = manifest.Author,
            description = manifest.Description,
            permissions = manifest.Permissions,
            optionalPermissions = manifest.OptionalPermissions,
            runtime = manifest.Runtime,
            official,
            enabled = entry?.Enabled ?? false,
            disclaimerAccepted = entry?.DisclaimerAccepted ?? false,
            loaded = _pluginHost.IsLoaded(manifest.Id),
            panelCount = manifest.Contributes?.Panels?.Length ?? 0,
            dir,
            dependencies = manifest.Dependencies,
            conflicts = manifest.Conflicts,
            homepage = manifest.Homepage,
            license = manifest.License,
            keywords = manifest.Keywords,
            api2 = manifest.IsApi2,
        };
    }

    /// <summary>
    /// 安装 .mclplug（zip）：防穿越 → 解压到 staging → 校验 manifest/integrity →
    /// 原子替换 unpacked/&lt;id&gt;/。与 Windows 端 PluginService 同一套规则；
    /// 区别是不启动运行时（Linux 端暂无 v2 视图宿主）。
    /// </summary>
    private static ((PluginManifest Manifest, string Dir, bool Official)? Plugin,
        List<string> Errors) InstallPlugin(string mclplugPath)
    {
        var errors = new List<string>();
        if (!File.Exists(mclplugPath))
        {
            errors.Add($"安装包不存在：{mclplugPath}");
            return (null, errors);
        }
        Directory.CreateDirectory(LauncherPaths.PluginPacksDir);
        Directory.CreateDirectory(LauncherPaths.PluginUnpackedDir);

        var archivePath = Path.Combine(
            LauncherPaths.PluginPacksDir,
            $"{Path.GetFileNameWithoutExtension(mclplugPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try { File.Copy(mclplugPath, archivePath, overwrite: true); }
        catch { /* 归档失败不阻塞安装 */ }

        var staging = Path.Combine(
            LauncherPaths.PluginUnpackedDir,
            ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var archive = ZipFile.OpenRead(mclplugPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.FullName)) continue;
                    var norm = entry.FullName.Replace('\\', '/');
                    if (Path.IsPathRooted(norm) || norm.Split('/').Contains(".."))
                    {
                        errors.Add($"zip 条目路径非法：{entry.FullName}");
                        return (null, errors);
                    }
                }
                if (!archive.Entries.Any(e =>
                        e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add("包内缺少 manifest.json（拒绝加载）");
                    return (null, errors);
                }
                archive.ExtractToDirectory(staging);
            }

            var manifestPath = Path.Combine(staging, "manifest.json");
            var manifest = PluginManifest.ParseFile(manifestPath);
            if (manifest is null)
            {
                errors.Add("manifest.json 解析失败");
                return (null, errors);
            }
            errors = manifest.Validate(LauncherVersion);
            if (errors.Count > 0) return (null, errors);

            if (manifest.IsApi2)
            {
                var integrity = PluginIntegrity.ParseFile(
                    Path.Combine(staging, PluginIntegrity.FileName));
                if (integrity is null)
                {
                    errors.Add($"2.0 插件包缺少 {PluginIntegrity.FileName}（整包摘要与签名）");
                    return (null, errors);
                }
                if (!string.Equals(integrity.Id, manifest.Id, StringComparison.Ordinal) ||
                    !string.Equals(integrity.Version, manifest.Version, StringComparison.Ordinal))
                {
                    errors.Add("integrity.json 的 id/version 与 manifest 不一致");
                    return (null, errors);
                }
                var mismatches = integrity.VerifyFiles(staging);
                if (mismatches.Count > 0)
                {
                    errors.AddRange(mismatches);
                    return (null, errors);
                }
                if (integrity.Verify(out var detail) == PluginTrustState.Invalid)
                {
                    errors.Add($"插件签名无效（拒绝安装）：{detail}");
                    return (null, errors);
                }
            }

            var target = Path.Combine(LauncherPaths.PluginUnpackedDir, manifest.Id);
            if (Directory.Exists(target))
            {
                var trash = target + ".old-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(target, trash);
                try { Directory.Delete(trash, recursive: true); }
                catch { /* 旧目录删除失败不阻塞新装 */ }
            }
            Directory.Move(staging, target);
            staging = target;

            SetPluginState(manifest.Id, s => s.Version = manifest.Version);
            var installed = (manifest, target,
                IsOfficialPlugin(manifest, target,
                    Path.Combine(target, "manifest.json")));
            return (installed, errors);
        }
        catch (Exception e)
        {
            errors.Add($"安装失败：{e.Message}");
            return (null, errors);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging) && staging.Contains(".staging-"))
                    Directory.Delete(staging, recursive: true);
            }
            catch { /* 清理失败忽略 */ }
        }
    }

    private static void EnsurePluginStarted(string id, bool allowDisabled = false)
    {
        if (!allowDisabled && !IsPluginEnabled(id))
            throw new Exception("插件未启用，请先启用（第三方插件需要免责确认）");
        if (_pluginHost.IsLoaded(id)) return;
        var installed = ScanPlugins();
        var target = installed.FirstOrDefault(x => x.Manifest.Id == id);
        if (target.Manifest is null) throw new Exception($"插件未安装：{id}");
        var error = _pluginHost.Start(target.Manifest, target.Dir);
        if (error is not null) throw new Exception(error.Message);
    }

    /// <summary>插件宿主 SDK（Python 需要 starcloud_host.py；JS 由 Jint 注入）。</summary>
    private static string PluginSdkPath()
    {
        var env = Environment.GetEnvironmentVariable("STARCLOUD_PLUGIN_SDK");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "pluginsdk"),
            Path.Combine(AppContext.BaseDirectory, "..", "dotnet", "src", "Starcloud.App",
                "Assets", "pluginsdk"),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists) ?? "";
    }

    private static void CollectBindings(PluginViewNode node, HashSet<string> keys)
    {
        if (node.Value.Bind is { } valueBind)
            keys.Add($"{valueBind.Scope}.{valueBind.Key}");
        if (node.Bind is { } bind)
            keys.Add($"{bind.Scope}.{bind.Key}");
        if (node.ItemsBind is { } itemsBind)
            keys.Add($"{itemsBind.Scope}.{itemsBind.Key}");
        foreach (var child in node.Children) CollectBindings(child, keys);
    }

    /// <summary>只有 App 宿主才有的 UI 能力：桥接进程没有窗口，确认/剪贴板按“不可用”处理。</summary>
    private sealed class BridgePluginHostBridge : IPluginHostBridge
    {
        public bool? Confirm(string title, string message) => null;

        public bool? ShowInfo(string title, string message) => null;

        public void OpenPage(string page)
        {
            // 无 UI 页面栈；插件调用只作为提示忽略。
        }

        public string OpenUrl(string url) => OpenExternal(url);

        public string OpenPath(string path) => OpenExternal(path);

        public string? ClipboardText() => null;

        public void SetClipboardText(string text)
        {
            // 无剪贴板访问
        }

        private static string OpenExternal(string target)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                else if (OperatingSystem.IsMacOS())
                    Process.Start(new ProcessStartInfo("open", $"\"{target}\""));
                else
                    Process.Start(new ProcessStartInfo("xdg-open", $"\"{target}\""));
                return "已打开";
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
    }

    /// <summary>版本实例设置：档案原值 + 层叠后的生效值。</summary>
    private static object VersionSettingsPayload(string versionId)
    {
        if (!InstanceLayout.IsValidVersionId(versionId))
            throw new Exception("非法版本 id");
        var dotMc = _settings.DotMinecraft;
        var profile = new InstanceSettingsStore(dotMc).Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _settings);
        var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
        var eff = InstanceLayout.Resolve(
            dotMc, versionId, profile, _settings, MemoryAdvisor.Probe(gameRoot));
        return new
        {
            versionId,
            isolation = profile.Isolation,
            memoryMode = profile.MemoryMode ?? "global",
            memoryMinMb = profile.MemoryMinMb,
            memoryMaxMb = profile.MemoryMaxMb,
            javaMode = profile.JavaMode ?? "global",
            javaPath = profile.JavaPath,
            windowMode = profile.WindowMode ?? "global",
            windowWidth = profile.WindowWidth,
            windowHeight = profile.WindowHeight,
            fullscreen = profile.Fullscreen,
            serverAddress = profile.ServerAddress,
            extraJvmArgs = profile.ExtraJvmArgs,
            extraGameArgs = profile.ExtraGameArgs,
            effective = new
            {
                gameRoot,
                isolated,
                minMem = eff.MinMem,
                maxMem = eff.MaxMem,
                java = eff.Java,
                windowW = eff.WindowW,
                windowH = eff.WindowH,
                fullscreen = eff.Fullscreen,
            },
        };
    }

    private static string? NormalizeMode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) return null;
        var value = element.GetString();
        return value is "manual" or "auto" ? value : null;
    }

    private static int? ReadNullableInt(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;

    private static bool? ReadNullableBool(JsonElement element) =>
        element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    private static async Task<string> TitleOf(
        string projectId, Dictionary<string, string> cache)
    {
        lock (cache)
        {
            if (cache.TryGetValue(projectId, out var cached)) return cached;
        }
        var title = await _modrinth.GetProjectTitleAsync(projectId) ?? projectId;
        lock (cache)
        {
            cache[projectId] = title;
        }
        return title;
    }

    /// <summary>版本内容上下文：实例根、隔离、MC 版本、加载器（mods/资源包/光影共用）。</summary>
    private static (string GameRoot, bool Isolated, string McVersion,
        string Loader, string? LoaderVersion) VersionContentContext(string versionId)
    {
        if (!InstanceLayout.IsValidVersionId(versionId))
            throw new Exception("非法版本 id");
        var dotMc = _settings.DotMinecraft;
        var profile = new InstanceSettingsStore(dotMc).Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _settings);
        var root = InstanceLayout.GameRoot(dotMc, versionId, isolated);
        var loader = ContentLibraryService.ResolveLoader(versionId, out var loaderVersion);
        return (root, isolated, ContentLibraryService.ResolveMc(versionId),
            loader, loaderVersion);
    }

    private static string NormalizeKind(string? kind) => kind switch
    {
        "mod" or "resourcepack" or "shader" => kind,
        _ => throw new Exception($"未知内容类别：{kind}"),
    };

    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new Exception("非法文件名");
        return name;
    }

    /// <summary>列出实例内容目录里的 jar/zip（`.disabled` 视为已停用）。</summary>
    private static object[] ContentFiles(string gameRoot, string kind)
    {
        var dir = Path.Combine(gameRoot, InstanceLayout.ContentDirFor(kind));
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir)
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                return n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                       n.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) ||
                       n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                       n.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase);
            })
            .Select(f =>
            {
                var name = Path.GetFileName(f);
                var disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                return new
                {
                    fileName = name,
                    displayName = disabled ? name[..^".disabled".Length] : name,
                    enabled = !disabled,
                    sizeBytes = new FileInfo(f).Length,
                    path = f,
                };
            })
            .OrderBy(x => x.displayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static async Task RunNovaChatAsync(
        TextWriter stdout, int id, JsonElement parameters)
    {
        try
        {
            var message = parameters.GetProperty("message").GetString() ?? "";
            var versionId = parameters.TryGetProperty("versionId", out var vid) &&
                            vid.ValueKind == JsonValueKind.String
                ? vid.GetString()
                : null;
            if (!string.IsNullOrWhiteSpace(versionId))
                _nova.SetCurrentInstance(versionId);

            var history = new List<NovaChatTurn>();
            if (parameters.TryGetProperty("history", out var h) &&
                h.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in h.EnumerateArray())
                {
                    var role = item.TryGetProperty("role", out var r) &&
                               r.ValueKind == JsonValueKind.String
                        ? r.GetString() ?? "user"
                        : "user";
                    var content = item.TryGetProperty("content", out var c) &&
                                  c.ValueKind == JsonValueKind.String
                        ? c.GetString() ?? ""
                        : "";
                    if (content.Length > 0) history.Add(new NovaChatTurn(role, content));
                }
            }

            var buffer = new StringBuilder();
            NovaReply? full = null;
            await foreach (var delta in _nova.ChatStreamAsync(
                               message, _settings, history,
                               onFullReply: reply =>
                               {
                                   full = reply;
                                   if (reply.Plan is { } plan)
                                   {
                                       if (PendingNovaPlans.Count > 20)
                                           PendingNovaPlans.Clear();
                                       PendingNovaPlans[plan.Id] = plan;
                                   }
                               },
                               onToolStarted: tool =>
                                   Emit(stdout, "nova.tool", new { tool })))
            {
                buffer.Append(delta);
                Emit(stdout, "nova.delta", new { text = delta });
            }

            var text = string.IsNullOrEmpty(full?.Text) ? buffer.ToString() : full!.Text;
            Emit(stdout, "nova.done", new
            {
                text,
                fromCloud = full?.FromCloud ?? false,
                planId = full?.Plan?.Id,
                planTitle = full?.Plan?.Title,
                plan = full?.Plan?.Items.Select(i => new { i.Title, i.Detail }),
            });
            Reply(stdout, id, ok: true, result: new { ok = true });
        }
        catch (Exception e)
        {
            Emit(stdout, "nova.failed", new { error = e.Message });
            Reply(stdout, id, ok: false, error: e.Message);
        }
    }

    /// <summary>联机快照 + 提供方/玩家名/节点地区，一次给全（UI 只渲染）。</summary>
    private static object MultiplayerPayload(MultiplayerSnapshot snap)
    {
        var state = snap.State;
        return new
        {
            providers = _multiplayer.Providers.Select(p => new
            {
                p.Id,
                p.DisplayName,
                p.Vendor,
                p.VendorUrl,
                p.Description,
                p.BuiltIn,
            }),
            activeId = snap.ProviderId,
            nickname = _multiplayer.Nickname,
            nodeRegion = _multiplayer.NodeRegion,
            core = new
            {
                installed = snap.Core.Installed,
                version = snap.Core.Version,
                sessionRunning = snap.Core.SessionRunning,
            },
            state = new
            {
                phase = PhaseKey(state.Phase),
                room = state.Room,
                address = state.Address,
                difficulty = state.Difficulty,
                players = state.Players,
                errorKind = state.ErrorKind,
                error = state.Error,
                isBusy = state.IsBusy,
            },
            lastError = snap.LastError,
        };
    }

    private static string PhaseKey(MultiplayerPhase phase) => phase switch
    {
        MultiplayerPhase.Idle => "idle",
        MultiplayerPhase.HostScanning => "hostScanning",
        MultiplayerPhase.HostStarting => "hostStarting",
        MultiplayerPhase.HostReady => "hostReady",
        MultiplayerPhase.GuestConnecting => "guestConnecting",
        MultiplayerPhase.GuestEntering => "guestEntering",
        MultiplayerPhase.GuestReady => "guestReady",
        _ => "failed",
    };

    private static async Task RunMultiplayerInstallAsync(TextWriter stdout, int id)
    {
        try
        {
            var progress = new Progress<MultiplayerProgress>(p =>
                Emit(stdout, "multiplayer.progress", new
                {
                    stage = p.Stage,
                    bytes = p.Bytes,
                    total = p.Total,
                    speedBps = p.SpeedBps,
                }));
            await _multiplayer.InstallCoreAsync(progress, CancellationToken.None);
            Emit(stdout, "multiplayer.install.done", new { });
            Reply(stdout, id, ok: true, result: new { installed = true });
        }
        catch (Exception e)
        {
            Emit(stdout, "multiplayer.install.failed", new { error = e.Message });
            Reply(stdout, id, ok: false, error: e.Message);
        }
    }

    /// <summary>版本列表条目：目录/JSON/jar/依赖/默认/隔离状态一次给全，UI 不再自己猜。</summary>
    private static object DescribeInstalled(
        InstalledVersionInfo info, InstanceSettingsStore instances)
    {
        var dir = Path.Combine(_settings.DotMinecraft, "versions", info.Id);
        var json = Path.Combine(dir, info.Id + ".json");
        string? inheritsFrom = null;
        string? versionType = null;
        // 整合包的版本 JSON 常没有 inheritsFrom，加载器信息藏在 libraries 里
        // （net.neoforged:neoforge:...），所以按 Win 端口径扫描全文。
        string? jsonText = null;
        var jarExists = false;
        try
        {
            if (File.Exists(json))
            {
                var text = File.ReadAllText(json);
                jsonText = text;
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh) &&
                    inh.ValueKind == JsonValueKind.String)
                    inheritsFrom = inh.GetString();
                // 版本类型（release / snapshot / old_beta / old_alpha）：
                // 壳端据此选图标（草方块 / 紫水晶 / 圆石 / 泥土），与 Win 端口径一致。
                if (doc.RootElement.TryGetProperty("type", out var tp) &&
                    tp.ValueKind == JsonValueKind.String)
                    versionType = tp.GetString();
            }
            // 与 GameLauncher.ResolveClientJar 的回退口径一致：同名 jar 或目录内唯一客户端 jar。
            jarExists = File.Exists(Path.Combine(dir, info.Id + ".jar")) ||
                Directory.EnumerateFiles(dir, "*.jar").Any(f =>
                    !f.Contains("-sources", StringComparison.OrdinalIgnoreCase) &&
                    !f.Contains("-javadoc", StringComparison.OrdinalIgnoreCase) &&
                    !f.Contains("-dev", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // 单个版本读失败不影响整个列表
        }

        return new
        {
            info.Id,
            sizeBytes = info.SizeBytes,
            directory = dir,
            jsonPath = json,
            jarExists,
            inheritsFrom,
            loader = DetectLoader(info.Id, inheritsFrom, jsonText),
            type = versionType ?? InferVersionType(info.Id),
            lastPlayedAt = instances.Get(info.Id).LastPlayedAt?.ToString("O"),
            isDefault = _settings.LastVersion == info.Id,
            isolated = _settings.VersionIsolated,
        };
    }

    /// <summary>从版本 id 推断加载器（顺序敏感：neoforge 必须先于 forge）。</summary>
    private static string DetectLoader(string id, string? inheritsFrom, string? jsonText)
    {
        var lower = (id + " " + (inheritsFrom ?? "") + " " + (jsonText ?? "")).ToLowerInvariant();
        if (lower.Contains("neoforge")) return "neoforge";
        if (lower.Contains("forge")) return "forge";
        if (lower.Contains("fabric")) return "fabric";
        if (lower.Contains("quilt")) return "quilt";
        return "vanilla";
    }

    /// <summary>版本 JSON 缺 type 时的兜底推断。</summary>
    private static string InferVersionType(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("-rc") || lower.Contains("-pre") || lower.Contains("-snapshot"))
            return "snapshot";
        return "release";
    }

    // ── 输出 ─────────────────────────────────────────────────────────────────

    private static readonly object StdoutLock = new();

    private static void Reply(TextWriter writer, int id, bool ok, object? result = null, string? error = null)
    {
        WriteLine(writer, JsonSerializer.Serialize(new
        {
            id,
            ok,
            result,
            error,
        }, JsonOpts));
    }

    private static void Emit(TextWriter writer, string eventName, object parameters)
    {
        WriteLine(writer, JsonSerializer.Serialize(new
        {
            @event = eventName,
            @params = parameters,
        }, JsonOpts));
    }

    private static void WriteLine(TextWriter writer, string line)
    {
        lock (StdoutLock)
        {
            writer.WriteLine(line);
            writer.Flush();
        }
    }
}
