// 游戏启动器：参数构建（新旧两代格式）+ 进程启动与退出监控

using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

public sealed record LaunchOptions(
    string VersionId,
    string PlayerName,
    string GameDir,
    int MinMemoryMb,
    int MaxMemoryMb,
    string? JavaExecutable,
    List<string> ExtraJvmArgs,
    bool UseBuiltinJvmArgs,
    LaunchCredentials? Credentials = null,
    string? QuickPlayMultiplayer = null);

/// <summary>已认证账户凭据（微软账户）；离线账户传 null 走 OfflineUuid 回退</summary>
public sealed record LaunchCredentials(string Uuid, string AccessToken, string UserType, string? Xuid = null);

public sealed class GameLauncher
{
    private readonly InstallerService _installer;
    private readonly JavaService? _java;

    public GameLauncher(InstallerService installer, JavaService? java = null)
    {
        _installer = installer;
        _java = java;
    }

    /// <summary>离线 UUID：Java UUID.nameUUIDFromBytes("OfflinePlayer:"+name)（MD5 v3）</summary>
    public static Guid OfflineUuid(string playerName)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes("OfflinePlayer:" + playerName));
        hash[6] = (byte)((hash[6] & 0x0F) | 0x30);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash);
    }

    /// <summary>构建完整启动命令行（java.exe + 参数）</summary>
    public async Task<string> BuildCommandLineAsync(MinecraftVersion version, LaunchOptions options,
        CancellationToken ct = default)
    {
        var dotMc = options.GameDir;
        var versionId = version.Id;
        var versionDir = _installer.VersionDir(dotMc, versionId);
        var nativesDir = Path.Combine(versionDir, "natives");

        // 类路径：windows 内的库（规则过滤；NeoForge 版本 JSON 有重复库条目，需保序去重）
        var classpathEntries = version.Libraries
            .Where(l => InstallerService.RulesAllow(l.Rules))
            .Where(l => l.Downloads?.Artifact is { Url: not "" })
            .Select(l => Path.Combine(dotMc, "libraries", l.Downloads!.Artifact!.Path ?? ""))
            .ToList();
        var ownJar = _installer.VersionJarPath(dotMc, versionId);
        classpathEntries.Add(ownJar);
        var classpath = string.Join(";",
            classpathEntries.Where(File.Exists).Distinct(StringComparer.OrdinalIgnoreCase));

        // 参数替换
        var assets = version.Assets ?? version.AssetIndex?.Id ?? "legacy";
        var cred = options.Credentials;
        var authUuid = cred?.Uuid ?? OfflineUuid(options.PlayerName).ToString("N");
        var authToken = cred?.AccessToken ?? "0";
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = options.PlayerName,
            ["version_name"] = versionId,
            ["game_directory"] = options.GameDir,
            ["assets_root"] = Path.Combine(dotMc, "assets"),
            ["game_assets"] = Path.Combine(dotMc, "assets", "virtual", assets),
            ["assets_index_name"] = version.AssetIndex?.Id ?? assets,
            ["auth_uuid"] = authUuid,
            ["auth_access_token"] = authToken,
            ["auth_session"] = $"token:{authToken}:{authUuid}",
            ["auth_xuid"] = cred?.Xuid ?? "0",
            ["clientid"] = "starcloud",
            ["user_type"] = cred?.UserType ?? "msa",
            ["user_properties"] = "{}",
            ["version_type"] = version.Type ?? "release",
            ["launcher_name"] = "Starcloud",
            ["launcher_version"] = "1.0.0",
            ["natives_directory"] = nativesDir,
            ["library_directory"] = Path.Combine(dotMc, "libraries"),
            ["classpath_separator"] = ";",
            ["classpath"] = classpath,
            ["resolution_width"] = "1280",
            ["resolution_height"] = "720",
            ["quickPlayPath"] = "",
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = options.QuickPlayMultiplayer ?? "",
            ["quickPlayRealms"] = "",
            ["quickPlayBroadBand"] = "",
        };

        string Substitute(string s) =>
            System.Text.RegularExpressions.Regex.Replace(
                s, @"\$\{([a-zA-Z0-9_]+)\}",
                m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        var jvmArgs = new List<string>
        {
            "-Xms" + options.MinMemoryMb + "M",
            "-Xmx" + options.MaxMemoryMb + "M",
            $"-Djava.library.path={nativesDir}",
            "-Dorg.lwjgl.opengl.Display.allowSoftwareOpenGL=true",
        };
        if (options.UseBuiltinJvmArgs)
        {
            // 内置优化参数（对应旧 use_builtin_jvm_args）
            jvmArgs.AddRange(
            [
                "-XX:+UseG1GC",
                "-XX:+UnlockExperimentalVMOptions",
                "-XX:G1NewSizePercent=20",
                "-XX:G1ReservePercent=20",
                "-XX:MaxGCPauseMillis=50",
                "-XX:G1HeapRegionSize=32M",
                "-Dsun.rmi.dgc.server.gcInterval=2147483646",
                "-Dfile.encoding=UTF-8",
            ]);
        }
        jvmArgs.AddRange(options.ExtraJvmArgs.Where(a => !string.IsNullOrWhiteSpace(a)));

        var gameArgs = new List<string>();

        // 新代参数（arguments.jvm / arguments.game）
        if (version.Arguments is { } args)
        {
            foreach (var item in args.Jvm.Where(i => RulesAllowValue(i)))
                AppendValue(jvmArgs, item, Substitute);
            foreach (var item in args.Game.Where(i => RulesAllowValue(i)))
                AppendValue(gameArgs, item, Substitute);
        }

        // 旧代参数字符串
        if (!string.IsNullOrEmpty(version.MinecraftArguments))
        {
            gameArgs.AddRange(version.MinecraftArguments
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Substitute));
        }

        // 现代版本没有 minecraftArguments 时补全必需 game 参数
        if (version.MinecraftArguments is null && version.Arguments is not null)
        {
            var has = (string prefix) => gameArgs.Any(a => a.Equals(prefix, StringComparison.OrdinalIgnoreCase));
            if (!has("--username"))
            {
                gameArgs.Add("--username"); gameArgs.Add(options.PlayerName);
                gameArgs.Add("--version"); gameArgs.Add(versionId);
                gameArgs.Add("--gameDir"); gameArgs.Add(options.GameDir);
                gameArgs.Add("--assetsDir"); gameArgs.Add(Path.Combine(dotMc, "assets"));
                gameArgs.Add("--assetIndex"); gameArgs.Add(version.AssetIndex?.Id ?? assets);
                gameArgs.Add("--uuid"); gameArgs.Add(authUuid);
                gameArgs.Add("--accessToken"); gameArgs.Add(authToken);
                gameArgs.Add("--userType"); gameArgs.Add(cred?.UserType ?? "msa");
                gameArgs.Add("--versionType"); gameArgs.Add(version.Type ?? "release");
            }
        }

        var java = string.IsNullOrWhiteSpace(options.JavaExecutable)
            ? await ResolveJavaAsync(version, dotMc, ct)
            : options.JavaExecutable!;
        var parts = new List<string> { QuoteIfNeeded(java) };
        parts.AddRange(jvmArgs.Select(QuoteIfNeeded));
        parts.Add(version.MainClass);
        parts.AddRange(gameArgs.Select(QuoteIfNeeded));
        return string.Join(' ', parts);
    }

    /// <summary>设置未指定 Java 时：按版本要求的 Java 主版本选运行时（精确匹配优先，其次最小超集），缺则自动下载</summary>
    private async Task<string> ResolveJavaAsync(MinecraftVersion version, string dotMc, CancellationToken ct)
    {
        if (_java is null)
            throw new InvalidOperationException(
                "未找到可用的 Java。请在设置页指定 java.exe 路径");
        var required = version.JavaVersion?.MajorVersion ?? 8;
        var runtimes = await _java.ListRuntimesAsync(ct, Path.Combine(dotMc, "runtime"));
        var fit = runtimes
            .Where(r => r.MajorVersion >= required)
            .OrderByDescending(r => r.MajorVersion == required)
            .ThenBy(r => r.MajorVersion)
            .FirstOrDefault();
        if (fit is not null) return fit.Executable;
        // 系统与启动器都没有合适版本：自动下载（与 Forge 安装器同款兜底）
        return await _java.DownloadRuntimeAsync(required, null, ct);
    }

    private static bool RulesAllowValue(ArgumentItem item)
    {
        if (item.Rules is null || item.Rules.Count == 0) return true;
        return InstallerService.RulesAllow(item.Rules);
    }

    private static void AppendValue(List<string> list, ArgumentItem item,
        Func<string, string> substitute)
    {
        switch (item.Value.ValueKind)
        {
            case JsonValueKind.String:
                var s = item.Value.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(substitute(s));
                break;
            case JsonValueKind.Array:
                foreach (var v in item.Value.EnumerateArray())
                {
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        var sv = v.GetString();
                        if (!string.IsNullOrEmpty(sv)) list.Add(substitute(sv));
                    }
                }
                break;
        }
    }

    private static string QuoteIfNeeded(string arg) =>
        arg.Contains(' ') ? '"' + arg + '"' : arg;

    /// <summary>启动游戏进程；返回进程（退出由调用方监控 Exited 事件）</summary>
    public Process Launch(string commandLine, string workingDirectory)
    {
        var psi = new ProcessStartInfo
        {
            FileName = commandLine.Split(' ')[0].Trim('"'),
            Arguments = commandLine.Substring(commandLine.Split(' ')[0].Length).TrimStart(),
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
        };
        return Process.Start(psi) ?? throw new InvalidOperationException("游戏进程启动失败");
    }

    /// <summary>便捷：加载版本并启动（供 UI 一次调用）</summary>
    public async Task<Process> LaunchVersionAsync(string dotMinecraft, string versionId,
        LaunchOptions options, CancellationToken ct = default)
    {
        var version = await _installer.LoadVersionAsync(dotMinecraft, versionId, ct);
        var cmd = await BuildCommandLineAsync(version, options, ct);
        var gameDir = options.GameDir;
        Directory.CreateDirectory(gameDir);
        return Launch(cmd, gameDir);
    }
}
