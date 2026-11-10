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


public static partial class Program
{

    private static async Task<object?> HandleJavaListAsync(TextWriter stdout, JsonElement p)
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

    private static async Task<object?> HandleLaunchStartAsync(TextWriter stdout, JsonElement p)
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
            try
            {
                GameRuntimeMonitor.Instance.Attach(session,
                    GameLaunchContextStore.Capture(versionId, eff,
                        GameLaunchContextStore.CountFiles(Path.Combine(eff.GameRoot, "mods")),
                        GameLaunchContextStore.CountFiles(Path.Combine(eff.GameRoot, "shaderpacks")),
                        GameLaunchContextStore.CountFiles(Path.Combine(eff.GameRoot, "resourcepacks"))),
                    session.CommandLine);
            }
            catch { /* 监控接管失败不阻塞启动 */ }
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

    private static async Task<object?> HandleGameKillAsync(TextWriter stdout, JsonElement p)
    {
            var sessionId = p.GetProperty("sessionId").GetInt32();
            if (!Sessions.TryGetValue(sessionId, out var session))
                return new { ok = false, alreadyExited = true };
            session.Kill(entireTree: true);
            return new { ok = true };
    }

    private static async Task<object?> HandleRuntimeStateAsync(TextWriter stdout, JsonElement p)
    {
            return RuntimePayload();
    }

    private static async Task<object?> HandleRuntimeSelectAsync(TextWriter stdout, JsonElement p)
    {
            GameRuntimeMonitor.Instance.Select(p.GetProperty("pid").GetInt32());
            return RuntimePayload();
    }

    private static async Task<object?> HandleRuntimeIntervalAsync(TextWriter stdout, JsonElement p)
    {
            var ms = p.GetProperty("ms").GetInt32();
            GameRuntimeMonitor.Instance.IntervalMs = Math.Clamp(ms, 500, 10000);
            return RuntimePayload();
    }

    private static async Task<object?> HandleRuntimeGpuAsync(TextWriter stdout, JsonElement p)
    {
            GameRuntimeMonitor.Instance.CollectGpu =
                p.TryGetProperty("enabled", out var ge) && ge.GetBoolean();
            return RuntimePayload();
    }

    private static async Task<object?> HandleRuntimeKillAsync(TextWriter stdout, JsonElement p)
    {
            var pid = p.TryGetProperty("pid", out var kp) ? kp.GetInt32() : 0;
            GameRuntimeMonitor.Instance.Kill(pid);
            return new { ok = true };
    }

    private static async Task<object?> HandleRuntimeLogsAsync(TextWriter stdout, JsonElement p)
    {
            var count = p.TryGetProperty("lines", out var ln) ? ln.GetInt32() : 200;
            return new { lines = GameRuntimeMonitor.Instance.TailLog(Math.Clamp(count, 1, 500)) };
    }

    private static async Task<object?> HandleRuntimeDiagnoseAsync(TextWriter stdout, JsonElement p)
    {
            var report = await _diagnostics.DiagnoseAsync(_settings.DotMinecraft);
            return new
            {
                scannedCount = report.ScannedCount,
                issues = report.Issues.Select(i => new
                {
                    versionId = i.VersionId, kind = i.Kind, detail = i.Detail, autoFixable = i.AutoFixable,
                }),
            };
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
}
