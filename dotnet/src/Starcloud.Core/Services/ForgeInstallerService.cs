// Forge / NeoForge 支持：构建列表查询 + 官方安装器静默运行（java -jar installer --installClient）

using System.Diagnostics;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>加载器构建（Version 为原始构建号，Note 为推荐标记如 "推荐"/"最新稳定版"）</summary>
public sealed record LoaderBuild(string Version, string? Note = null, bool Stable = true);

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

    /// <summary>Forge 构建列表：maven-metadata 全量构建（新→旧），promotions 提供"推荐/最新"标记</summary>
    public async Task<List<LoaderBuild>> ListForgeBuildsAsync(string mcVersion, CancellationToken ct = default)
    {
        var meta = await _downloader.GetStringAsync(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct);
        var promosJson = await _downloader.GetStringAsync(
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct);

        var prefix = $"{mcVersion}-";
        var builds = new List<string>();
        foreach (var tag in SplitVersions(meta))
        {
            if (tag.StartsWith(prefix, StringComparison.Ordinal) && IsForgeBuildSuffix(tag[prefix.Length..]))
                builds.Add(tag);
        }
        // maven-metadata 顺序不保证按时间，按构建号数字排序（新→旧）
        builds.Sort((a, b) => CompareForgeBuilds(b[prefix.Length..], a[prefix.Length..]));

        string? recommended = null, latest = null;
        using (var doc = JsonDocument.Parse(promosJson))
        {
            var promos = doc.RootElement.GetProperty("promos");
            if (promos.TryGetProperty($"{mcVersion}-recommended", out var rec))
                recommended = rec.GetString();
            if (promos.TryGetProperty($"{mcVersion}-latest", out var lat))
                latest = lat.GetString();
        }

        return builds
            .Select(v => new LoaderBuild(v[prefix.Length..],
                v == $"{prefix}{recommended}" ? "推荐"
                : v == $"{prefix}{latest}" ? "最新" : null))
            .ToList();
    }

    /// <summary>
    /// Forge 构建号判定：现代形如 "47.4.5"（含点号），旧版形如 "10.13.4.1614-1.7.10" / "10.13.3.1401-1710ls"（带后缀）。
    /// 原先用 int.TryParse 判定，现代构建号必然解析失败，导致列表恒为空。
    /// </summary>
    private static bool IsForgeBuildSuffix(string s)
    {
        if (s.Length == 0 || !char.IsAsciiDigit(s[0])) return false;
        foreach (var c in s)
            if (!char.IsAsciiDigit(c) && !char.IsAsciiLetter(c) && c != '.' && c != '-') return false;
        return true;
    }

    /// <summary>Forge 构建号比较：忽略 "-" 后缀，逐段按数字比较，段数多者视为更新</summary>
    private static int CompareForgeBuilds(string x, string y)
    {
        static int[] Key(string v) =>
            v.Split('-')[0].Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var xs = Key(x);
        var ys = Key(y);
        for (var i = 0; i < Math.Min(xs.Length, ys.Length); i++)
            if (xs[i] != ys[i]) return xs[i].CompareTo(ys[i]);
        return xs.Length.CompareTo(ys.Length);
    }

    /// <summary>从 maven-metadata.xml 提取 &lt;version&gt; 标签内容</summary>
    private static IEnumerable<string> SplitVersions(string xml)
    {
        const string open = "<version>", close = "</version>";
        var i = 0;
        while ((i = xml.IndexOf(open, i, StringComparison.Ordinal)) >= 0)
        {
            var start = i + open.Length;
            var end = xml.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0) yield break;
            yield return xml[start..end].Trim();
            i = end;
        }
    }

    /// <summary>
    /// NeoForge 构建列表（按 MC 版本前缀过滤，新→旧；不适配的版本返回空）。
    /// 1.20.1 的产物发布在 legacy 的 forge 构件下（版本号形如 1.20.1-47.1.106），其余在 neoforge 构件下。
    /// </summary>
    public async Task<List<LoaderBuild>> ListNeoForgeBuildsAsync(string mcVersion, CancellationToken ct = default)
    {
        var prefix = NeoForgePrefix(mcVersion);
        if (prefix is null) return [];

        var legacy = mcVersion == "1.20.1";
        var artifact = legacy ? "forge" : "neoforge";
        var json = await _downloader.GetStringAsync(
            $"https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/{artifact}", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // API 返回格式兼容：字符串数组，或含 "versions" 数组的对象
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("versions", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray()
                : default;

        if (items.Equals(default)) return [];
        // legacy 构件版本号带 MC 前缀，展示与安装链接均只用构建号，故先剥离再排序
        var skip = legacy ? mcVersion.Length + 1 : 0;
        var all = items
            .Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : null)
            .OfType<string>()
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .Select(v => v[skip..])
            .OrderByDescending(NeoForgeOrderKey)
            .ToList();
        return all
            .Select((v, i) => new LoaderBuild(v, i == 0 ? "最新稳定版" : null))
            .ToList();
    }

    /// <summary>
    /// NeoForge 版本号前缀：1.20.1 → "1.20.1-47.1."（legacy forge 构件）；
    /// 其余 1.x[.y] → "x.y."（如 1.20.4 → "20.4."、1.21 → "21.0."）
    /// </summary>
    private static string? NeoForgePrefix(string mcVersion)
    {
        if (mcVersion == "1.20.1") return "1.20.1-47.1.";
        if (!mcVersion.StartsWith("1.", StringComparison.Ordinal)) return null;

        var parts = mcVersion[2..].Split('.');
        if (parts.Length is < 1 or > 2) return null;
        if (!int.TryParse(parts[0], out _)) return null;
        if (parts.Length == 2 && !int.TryParse(parts[1], out _)) return null;
        return $"{parts[0]}.{(parts.Length == 2 ? parts[1] : "0")}.";
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

    /// <summary>
    /// NeoForge 安装器直链。1.20.1 产物在 legacy 的 forge 构件下（版本号形如 1.20.1-47.1.106），
    /// 其余在 neoforge 构件下；build 若带 MC 前缀会自动剥离。
    /// </summary>
    public static string NeoForgeInstallerUrl(string mcVersion, string build)
    {
        var ver = build.StartsWith($"{mcVersion}-", StringComparison.Ordinal)
            ? build[(mcVersion.Length + 1)..] : build;
        return mcVersion == "1.20.1"
            ? $"https://maven.neoforged.net/releases/net/neoforged/forge/{mcVersion}-{ver}/forge-{mcVersion}-{ver}-installer.jar"
            : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{ver}/neoforge-{ver}-installer.jar";
    }

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
