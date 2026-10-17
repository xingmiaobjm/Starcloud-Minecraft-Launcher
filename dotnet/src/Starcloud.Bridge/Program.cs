using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core;
using Starcloud.Core.Models;
using Starcloud.Core.Services;

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
    private static readonly ConcurrentDictionary<int, GameSession> Sessions = new();
    private static int _nextSessionId;
    private static Task? _installTask;
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
        method is "download.file" or "download.text" or "java.list" or "launch.start";

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
                return new
                {
                    installed = _manifest
                        .ListInstalled(_settings.DotMinecraft)
                        .Select(i => new { i.Id, i.SizeBytes }),
                };

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
                    accounts = _accounts.Accounts.Select(a => new
                    {
                        a.Uuid,
                        name = a.Username,
                        a.Type,
                        isDefault = a.IsDefault,
                    }),
                    current = _accounts.Accounts.FirstOrDefault(a => a.IsDefault)?.Uuid,
                };

            case "accounts.addOffline":
            {
                var name = p.GetProperty("name").GetString() ?? "";
                _accounts.AddOrUpdateOffline(name);
                return new { uuid = AccountStore.OfflineUuid(name), name };
            }

            case "accounts.setCurrent":
            {
                // 默认账户用 IsDefault 标记（与 Windows 版一致）。
                var uuid = p.GetProperty("uuid").GetString()!;
                foreach (var a in _accounts.Accounts)
                    a.IsDefault = a.Uuid == uuid;
                return new { ok = true };
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
                await _download.DownloadFileAsync(url, dest, sha1);
                return new { ok = true };
            }

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
                _settings.Save();
                return SnapshotSettings();
            }

            case "launch.start":
            {
                var versionId = p.GetProperty("versionId").GetString()!;
                var playerName = p.GetProperty("playerName").GetString()!;
                var maxMemory = p.TryGetProperty("maxMemoryMb", out var mm) && mm.TryGetInt32(out var m2)
                    ? m2
                    : _settings.DefaultMaxMemoryMb;
                // 离线账户：C# 侧官方算法（md5 OfflinePlayer:name v3），与 Windows 端一致。
                _accounts.AddOrUpdateOffline(playerName);
                var uuid = AccountStore.OfflineUuid(playerName);
                var options = new LaunchOptions(
                    VersionId: versionId,
                    PlayerName: playerName,
                    // 与 Windows 端共用 version_isolated 语义：关闭隔离时
                    // 存档/mods 落在 .minecraft 根，而不是 versions/<id>。
                    GameDir: _settings.VersionIsolated
                        ? Path.Combine(_settings.DotMinecraft, "versions", versionId)
                        : _settings.DotMinecraft,
                    SharedDir: _settings.DotMinecraft,
                    MinMemoryMb: 512,
                    MaxMemoryMb: maxMemory,
                    JavaExecutable: _settings.JavaExecutable,
                    ExtraJvmArgs: new List<string>(),
                    UseBuiltinJvmArgs: true,
                    Credentials: new LaunchCredentials(uuid, "0", "legacy"));

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

    private static object SnapshotSettings() => new
    {
        gameDir = _settings.DotMinecraft,
        javaExecutable = _settings.JavaExecutable,
        maxMemoryMb = _settings.DefaultMaxMemoryMb,
        downloadSource = _settings.DownloadSource,
    };

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
