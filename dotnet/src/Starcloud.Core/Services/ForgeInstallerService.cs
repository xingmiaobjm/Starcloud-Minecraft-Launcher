// Forge / NeoForge 支持：构建列表查询 + 官方安装器静默运行（java -jar installer --installClient）

using System.Diagnostics;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>加载器构建（Version 为原始构建号，Label 为显示文本，如 "47.2.0（推荐）"）</summary>
public sealed record LoaderBuild(string Version, string Label);

public sealed class ForgeInstallerService
{
    private readonly DownloadService _downloader;
    private readonly JavaService _java;

    public ForgeInstallerService(DownloadService downloader, JavaService java)
    {
        _downloader = downloader;
        _java = java;
    }

    // ── 构建列表 ──

    /// <summary>Forge 构建列表（promotions：推荐 / 最新）</summary>
    public async Task<List<LoaderBuild>> ListForgeBuildsAsync(string mcVersion, CancellationToken ct = default)
    {
        var json = await _downloader.GetStringAsync(
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct);
        using var doc = JsonDocument.Parse(json);
        var promos = doc.RootElement.GetProperty("promos");
        var builds = new List<LoaderBuild>();
        if (promos.TryGetProperty($"{mcVersion}-recommended", out var rec) &&
            rec.GetString() is { Length: > 0 } recVer)
            builds.Add(new LoaderBuild(recVer, $"{recVer}（推荐）"));
        if (promos.TryGetProperty($"{mcVersion}-latest", out var lat) &&
            lat.GetString() is { Length: > 0 } latVer &&
            builds.All(b => b.Version != latVer))
            builds.Add(new LoaderBuild(latVer, $"{latVer}（最新）"));
        return builds;
    }

    /// <summary>NeoForge 构建列表（按 MC 版本前缀过滤，取最新 8 个；不适配的版本返回空）</summary>
    public async Task<List<LoaderBuild>> ListNeoForgeBuildsAsync(string mcVersion, CancellationToken ct = default)
    {
        var prefix = NeoForgePrefix(mcVersion);
        if (prefix is null) return [];

        var json = await _downloader.GetStringAsync(
            "https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/neoforge", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // API 返回格式兼容：字符串数组，或含 "versions" 数组的对象
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("versions", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray()
                : default;

        if (items.Equals(default)) return [];
        return items
            .Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : null)
            .OfType<string>()
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .OrderByDescending(NeoForgeOrderKey)
            .Take(8)
            .Select(v => new LoaderBuild(v, v))
            .ToList();
    }

    /// <summary>NeoForge 版本号前缀：1.20.1 → "47.1."；其余 1.x.y → "x.y."（如 1.20.4 → "20.4."）</summary>
    private static string? NeoForgePrefix(string mcVersion)
    {
        if (mcVersion == "1.20.1") return "47.1.";
        if (mcVersion.StartsWith("1."))
        {
            var rest = mcVersion[2..];
            var parts = rest.Split('.');
            if (parts.Length >= 2 && int.TryParse(parts[0], out _) && int.TryParse(parts[1], out _))
                return rest + ".";
        }
        return null;
    }

    private static (int Major, int Minor, int Patch) NeoForgeOrderKey(string v)
    {
        var parts = v.Split('.');
        static int P(string[] a, int i) => i < a.Length && int.TryParse(a[i], out var n) ? n : 0;
        return (P(parts, 0), P(parts, 1), P(parts, 2));
    }

    // ── 安装器直链 ──

    public static string ForgeInstallerUrl(string mcVersion, string build) =>
        $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{build}/forge-{mcVersion}-{build}-installer.jar";

    public static string NeoForgeInstallerUrl(string build) =>
        $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{build}/neoforge-{build}-installer.jar";

    // ── 静默安装 ──

    /// <summary>
    /// 下载官方安装器并以 --installClient 静默安装。
    /// 前置：原版版本 JSON 已存在于游戏目录；requiredJavaMajor 来自版本 JSON 的 javaVersion。
    /// </summary>
    public async Task InstallViaInstallerAsync(
        string installerUrl, string mcVersion, int requiredJavaMajor,
        LauncherSettings settings, IProgress<BatchProgress>? progress, CancellationToken ct = default)
    {
        // 1. 确定可用的 Java（不满足则自动下载 Adoptium 运行时）
        var javaExe = await EnsureJavaAsync(requiredJavaMajor, progress, ct);

        // 2. 下载安装器
        var tempDir = Path.Combine(Path.GetTempPath(), "starcloud-installer");
        Directory.CreateDirectory(tempDir);
        var name = Path.GetFileName(new Uri(installerUrl).LocalPath);
        var installerPath = Path.Combine(tempDir, name);
        Report(progress, $"下载 {name}…");
        await _downloader.DownloadFileAsync(installerUrl, installerPath, null,
            settings.DownloadMaxRetries, ct);

        // 3. 运行官方安装器（--installClient 会写入 versions/<id>-forge-<build> 并补齐库文件）
        Report(progress, "正在运行官方安装器（可能需要数分钟）…");
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = tempDir,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(settings.DotMinecraft);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Java 进程");
        var stdErrTask = p.StandardError.ReadToEndAsync(ct);
        string? lastLine = null;
        var logTask = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length > 0)
                {
                    lastLine = line;
                    Report(progress, line);
                }
            }
        }, ct);
        await p.WaitForExitAsync(ct);
        try { await logTask; } catch (OperationCanceledException) { }

        if (p.ExitCode != 0)
        {
            var err = await stdErrTask;
            throw new InvalidOperationException(
                $"安装器执行失败（exit {p.ExitCode}）：{(err.Length > 0 ? err : lastLine)}");
        }
        Report(progress, $"安装器执行完成（{mcVersion}）");
    }

    private async Task<string> EnsureJavaAsync(int requiredMajor,
        IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var runtimes = await _java.ListRuntimesAsync(ct);
        var fit = runtimes
            .Where(r => r.MajorVersion >= requiredMajor)
            .OrderBy(r => r.MajorVersion)
            .FirstOrDefault();
        if (fit is not null) return fit.Executable;

        Report(progress, $"未找到 Java {requiredMajor}+，正在自动下载运行时…");
        return await _java.DownloadRuntimeAsync(requiredMajor, progress, ct);
    }

    private static void Report(IProgress<BatchProgress>? progress, string stage) =>
        progress?.Report(new BatchProgress(stage, 0, 0, 0, 0, 0, null));
}
