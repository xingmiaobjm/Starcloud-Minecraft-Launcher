// Java 发行版与版本列表：Temurin（Adoptium）与龙井（Alibaba Dragonwell）。
//
// 只负责「查有什么版本」与「下载并解压到 java/<vendor>-<major>/」；解压/校验复用 JavaService。
//
// 为什么要有第二家：Adoptium 的 API 与下载域名在部分国内网络下会连不上（用户反馈「显示无连接」），
// 龙井是阿里维护的 OpenJDK 发行版，走 GitHub Release，网络路径通常不同——多一个兜底选择，
// 而不是让用户卡在一个源上。

using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>Java 发行版</summary>
public enum JavaVendor
{
    /// <summary>Eclipse Temurin（Adoptium）</summary>
    Temurin,

    /// <summary>Alibaba Dragonwell（龙井）</summary>
    Dragonwell,
}

/// <summary>一个可安装的 Java 版本</summary>
public sealed record JavaRelease(
    JavaVendor Vendor,
    int Major,
    string Display,
    string? DirectUrl = null,
    string? Version = null);

public sealed class JavaDistributionsService
{
    private const string AdoptiumApi = "https://api.adoptium.net/v3";
    private const string GithubApi = "https://api.github.com";

    private readonly DownloadService _downloader;
    private readonly JavaService _java;
    private static readonly HttpClient Http = new(new SocketsHttpHandler
    {
        ConnectTimeout = TimeSpan.FromSeconds(8),
    })
    { Timeout = TimeSpan.FromSeconds(15) };

    public JavaDistributionsService(DownloadService downloader, JavaService java)
    {
        _downloader = downloader;
        _java = java;
    }

    /// <summary>发行版展示名</summary>
    public static string Name(JavaVendor vendor) => vendor switch
    {
        JavaVendor.Dragonwell => "龙井（Dragonwell）",
        _ => "Temurin（OpenJDK）",
    };

    /// <summary>
    /// 列出该发行版可安装的版本（大版本粒度）。失败抛可读异常（UI 直接展示）。
    /// </summary>
    public async Task<IReadOnlyList<JavaRelease>> ListAsync(JavaVendor vendor, CancellationToken ct = default)
    {
        return vendor == JavaVendor.Dragonwell ? await ListDragonwellAsync(ct) : await ListTemurinAsync(ct);
    }

    /// <summary>Temurin：官方 available_releases 接口给出当前支持的大版本</summary>
    private async Task<IReadOnlyList<JavaRelease>> ListTemurinAsync(CancellationToken ct) =>
        ParseTemurinReleases(await GetAsync($"{AdoptiumApi}/info/available_releases", ct));

    /// <summary>解析 Adoptium 的 available_releases 响应（拆出来便于离线测试）</summary>
    internal static IReadOnlyList<JavaRelease> ParseTemurinReleases(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var list = new List<JavaRelease>();
        // 只有 available_lts_releases 里的才是 LTS，其余是普通特性版本
        var lts = new HashSet<int>();
        if (doc.RootElement.TryGetProperty("available_lts_releases", out var ltsEl) &&
            ltsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in ltsEl.EnumerateArray())
            {
                if (item.TryGetInt32(out var n)) lts.Add(n);
            }
        }
        if (doc.RootElement.TryGetProperty("available_releases", out var majors) &&
            majors.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in majors.EnumerateArray())
            {
                if (!item.TryGetInt32(out var major)) continue;
                list.Add(new JavaRelease(JavaVendor.Temurin, major,
                    $"Temurin {major}" + (lts.Contains(major) ? "（LTS）" : "")));
            }
        }
        return list.OrderByDescending(r => r.Major).ToList();
    }

    /// <summary>龙井：按大版本仓库取 latest release，挑平台对应的压缩包</summary>
    private async Task<IReadOnlyList<JavaRelease>> ListDragonwellAsync(CancellationToken ct)
    {
        var list = new List<JavaRelease>();
        // 25 也纳入：新 MC 的 Forge 已要求 Java 25，列表空会让用户以为「龙井没有可用版本」
        foreach (var major in new[] { 8, 11, 17, 21, 25 })
        {
            try
            {
                var json = await GetAsync($"{GithubApi}/repos/alibaba/dragonwell{major}/releases/latest", ct);
                using var doc = JsonDocument.Parse(json);
                var tag = doc.RootElement.TryGetProperty("tag_name", out var t) ? t.GetString() : null;
                var url = PickAsset(doc.RootElement, ct);
                if (url is null) continue;
                list.Add(new JavaRelease(JavaVendor.Dragonwell, major,
                    $"Dragonwell {major}" + (string.IsNullOrEmpty(tag) ? "" : $"（{tag}）"), url, tag));
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                // 某个大版本查不到就跳过，别的版本照常可用
            }
        }
        return list;
    }

    /// <summary>从 release assets 里挑当前平台的 JDK 压缩包（Windows .zip；unix .tar.gz）</summary>
    internal static string? PickAsset(JsonElement release, CancellationToken ct = default)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return null;
        var wantWindows = PlatformInfo.IsWindows;
        var wantArm = PlatformInfo.AdoptiumArch.Contains("aarch", StringComparison.OrdinalIgnoreCase);
        string? fallback = null;
        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var nameEl) || nameEl.GetString() is not { } name) continue;
            if (!asset.TryGetProperty("browser_download_url", out var urlEl) ||
                urlEl.GetString() is not { Length: > 0 } url) continue;
            var lower = name.ToLowerInvariant();
            // 资产名不一定带 jdk（龙井官方包名是 Alibaba_Dragonwell_Standard_*）
            if (!lower.Contains("jdk") && !lower.Contains("dragonwell")) continue;
            if (wantWindows && !lower.EndsWith(".zip")) continue;
            if (!wantWindows && !lower.EndsWith(".tar.gz")) continue;
            var archOk = wantArm ? lower.Contains("aarch64") || lower.Contains("arm64")
                                 : lower.Contains("x64") || lower.Contains("x86_64");
            if (archOk) return url;
            fallback ??= url;
        }
        return fallback;
    }

    /// <summary>
    /// 下载并安装：落盘到 java/&lt;vendor&gt;-&lt;major&gt;/，返回 bin/java 完整路径。
    /// Temurin 沿用 JavaService 的地址解析（含 Adoptium 的重定向与平台参数）。
    /// </summary>
    public async Task<string> InstallAsync(JavaRelease release,
        IProgress<string>? status, CancellationToken ct = default)
    {
        status?.Report($"正在获取 {release.Display} 下载地址…");
        var url = release.DirectUrl;
        if (string.IsNullOrEmpty(url))
            url = await _downloader.GetAdoptiumDownloadUrlAsync(release.Major, ct);

        var folder = release.Vendor == JavaVendor.Temurin
            ? Path.Combine(LauncherPaths.JavaDir, release.Major.ToString())   // 与旧版兼容
            : Path.Combine(LauncherPaths.JavaDir, $"dragonwell-{release.Major}");
        Directory.CreateDirectory(LauncherPaths.JavaDir);

        var archive = Path.Combine(LauncherPaths.JavaDir,
            $"{(release.Vendor == JavaVendor.Temurin ? "temurin" : "dragonwell")}-{release.Major}{PlatformInfo.JavaArchiveExtension}");
        status?.Report($"正在下载 {release.Display}…");
        await _downloader.DownloadFileAsync(url, archive, null, 3, ct);

        status?.Report("下载完成，正在解压…");
        if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        Directory.CreateDirectory(folder);
        JavaService.ExtractRuntimeArchive(archive, folder);
        try { File.Delete(archive); } catch { }
        JavaService.PromoteJavaHome(folder);
        JavaService.MakeRuntimeExecutable(folder);
        var exe = Path.Combine(folder, "bin", PlatformInfo.JavaExeName);
        if (!File.Exists(exe))
            throw new InvalidOperationException("解压后未找到 bin/" + PlatformInfo.JavaExeName + "，可能下载到了错误的包");
        status?.Report($"已安装到 {folder}");
        return exe;
    }

    private static async Task<string> GetAsync(string url, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        // GitHub API 要求 UA；不带头会 403
        req.Headers.UserAgent.ParseAdd("StarcloudLauncher/1.0");
        // vnd.github+json 只对 GitHub 有效：发给 api.adoptium.net 会因无法满足该媒体类型回 406
        if (new Uri(url).Host.Contains("github", StringComparison.OrdinalIgnoreCase))
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await Http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new InvalidOperationException($"请求 {new Uri(url).Host} 失败（HTTP {(int)resp.StatusCode}）。" +
                                               (new Uri(url).Host.Contains("github") ? "可稍后重试或改用 Temurin。" : ""));
        return body;
    }
}
