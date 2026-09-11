// 多下载源聚合：Mojang 域名改写 + 源健康度降级 + 竞速池
// （移植并升级自旧 Rust src/download/sources.rs；设计见 docs/superpowers/specs/2026-09-12-multi-source-download-design.md）

using System.Collections.Concurrent;

namespace Starcloud.Core.Services;

/// <summary>下载源类型</summary>
public enum SourceKind { Official, Bmclapi, Fastmirror, Rainyun, Custom }

/// <summary>
/// 下载源注册表：域名改写（纯函数）+ 健康度状态。
/// 仅改写 Mojang 官方域名，未命中一律原样放行（Modrinth / Fabric / Forge / Starcloud API 等不受影响）。
/// </summary>
public sealed class DownloadSourceRegistry
{
    /// <summary>
    /// 连续失败达到该次数即降级。取 2（而非旧 Rust 的 5）是实测结论：
    /// 不可达镜像的连接超时约 3.7 s/次，阈值 5 会让每个新进程的头几个分片白等
    /// 十几秒（auto 31.1 s vs 仅官方 3.7 s，见 spec §9 风险 4）。
    /// 误降级风险由 RecordSuccess 立即恢复兜底。
    /// </summary>
    internal const int DegradeAfterFailures = 2;

    /// <summary>Mojang 域名前缀（只有这些 URL 会被镜像改写）</summary>
    private static readonly string[] MojangPrefixes =
    [
        "https://piston-meta.mojang.com",
        "https://piston-data.mojang.com",
        "https://launchermeta.mojang.com",
        "https://launcher.mojang.com",
        "https://resources.download.minecraft.net",
        "https://libraries.minecraft.net",
        "https://libraries.mojang.com",
    ];

    /// <summary>自动模式下的候选镜像（不含官方）</summary>
    private static readonly SourceKind[] MirrorKinds =
        [SourceKind.Bmclapi, SourceKind.Fastmirror, SourceKind.Rainyun, SourceKind.Custom];

    private sealed class Health
    {
        public int Failures;
        public bool Degraded;
        public bool NoRangeSupport;
    }

    private readonly Func<LauncherSettings> _settings;
    private readonly ConcurrentDictionary<SourceKind, Health> _health = new();

    public DownloadSourceRegistry(Func<LauncherSettings> settings) => _settings = settings;

    /// <summary>源 ID（与设置项 download_source 取值一致）</summary>
    public static string Id(SourceKind k) => k switch
    {
        SourceKind.Bmclapi => "bmclapi",
        SourceKind.Fastmirror => "fastmirror",
        SourceKind.Rainyun => "rainyun",
        SourceKind.Custom => "custom",
        _ => "official",
    };

    /// <summary>源显示名（用于日志）</summary>
    public static string Name(SourceKind k) => k switch
    {
        SourceKind.Bmclapi => "BMCLAPI",
        SourceKind.Fastmirror => "FastMinecraftMirror",
        SourceKind.Rainyun => "RainYun 雨云",
        SourceKind.Custom => "自定义镜像",
        _ => "Mojang 官方",
    };

    /// <summary>解析源 ID；未知 ID 返回 false（调用方回落到自动模式）</summary>
    public static bool TryFromId(string? id, out SourceKind kind)
    {
        switch (id?.Trim().ToLowerInvariant())
        {
            case "official": kind = SourceKind.Official; return true;
            case "bmclapi": kind = SourceKind.Bmclapi; return true;
            case "fastmirror": kind = SourceKind.Fastmirror; return true;
            case "rainyun": kind = SourceKind.Rainyun; return true;
            case "custom": kind = SourceKind.Custom; return true;
            default: kind = SourceKind.Official; return false;
        }
    }

    /// <summary>URL 是否属于 Mojang 官方域名（只有这类 URL 会启用分片竞速）</summary>
    public static bool IsMojangUrl(string url) =>
        url.Length > 0 && MojangPrefixes.Any(p => url.StartsWith(p, StringComparison.OrdinalIgnoreCase));

    /// <summary>按指定源改写 Mojang 域名 URL（纯函数，非 Mojang 域名原样返回）</summary>
    public static string Rewrite(string url, SourceKind kind, string? customMirrorUrl)
    {
        if (kind == SourceKind.Official) return url;
        switch (kind)
        {
            case SourceKind.Bmclapi:
                return RewriteRoot(url, "bmclapi2.bangbang93.com");
            case SourceKind.Rainyun:
                return RewriteRoot(url, "mirrors.rainyun.com");
            case SourceKind.Fastmirror:
                return RewriteFastmirror(url);
            case SourceKind.Custom:
                var root = NormalizeMirrorRoot(customMirrorUrl);
                return root is null ? url : RewriteRoot(url, root);
            default:
                return url;
        }
    }

    /// <summary>按当前设置改写 URL（首选源）</summary>
    public string RewritePreferred(string url) =>
        Rewrite(url, Preferred(), _settings().DownloadCustomMirrorUrl);

    /// <summary>按当前设置改写 URL（指定源）</summary>
    public string Rewrite(string url, SourceKind kind) =>
        Rewrite(url, kind, _settings().DownloadCustomMirrorUrl);

    /// <summary>首选源：竞速池首个（自动模式 = 最优镜像；指定单一源 = 该源）</summary>
    public SourceKind Preferred() => RacingPool()[0];

    /// <summary>
    /// 竞速池：健康镜像 → 降级镜像 → 官方兜底；官方恒为最后一个。
    /// 用户显式指定单一源时只含该源（不受降级影响，否则会把用户锁死）；
    /// 已被判定不支持 Range 的源退出分片池，未配置地址的自定义源不参与。
    /// </summary>
    public IReadOnlyList<SourceKind> RacingPool()
    {
        if (Pinned() is { } pinned) return [pinned];

        var custom = NormalizeMirrorRoot(_settings().DownloadCustomMirrorUrl);
        var mirrors = new List<SourceKind>();
        foreach (var k in MirrorKinds)
        {
            if (k == SourceKind.Custom && custom is null) continue;
            if (Get(k).NoRangeSupport) continue;
            mirrors.Add(k);
        }
        // 降级源靠后；OrderBy 稳定，故同一健康档位内保持声明顺序
        var pool = mirrors.OrderBy(k => Get(k).Degraded ? 1 : 0).ToList();
        pool.Add(SourceKind.Official);
        return pool;
    }

    /// <summary>记录请求成功：重置连续失败计数并恢复降级</summary>
    public void RecordSuccess(SourceKind k)
    {
        var h = Get(k);
        Interlocked.Exchange(ref h.Failures, 0);
        h.Degraded = false;
    }

    /// <summary>记录请求失败：连续失败达到阈值自动降级</summary>
    public void RecordFailure(SourceKind k)
    {
        var h = Get(k);
        if (Interlocked.Increment(ref h.Failures) >= DegradeAfterFailures) h.Degraded = true;
    }

    /// <summary>该源不支持 HTTP Range：退出分片池（进程内有效）</summary>
    public void MarkNoRangeSupport(SourceKind k) => Get(k).NoRangeSupport = true;

    /// <summary>重置全部源健康度</summary>
    public void ResetHealth() => _health.Clear();

    private Health Get(SourceKind k) => _health.GetOrAdd(k, _ => new Health());

    /// <summary>用户显式指定的单一源；auto / 未知 ID → null（多源竞速）</summary>
    private SourceKind? Pinned()
    {
        var id = _settings().DownloadSource;
        if (string.IsNullOrWhiteSpace(id) || id.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase))
            return null;
        return TryFromId(id, out var k) ? k : null;
    }

    /// <summary>自定义镜像根（去空白、去协议前缀、去结尾 /）；未配置或非法 → null</summary>
    private static string? NormalizeMirrorRoot(string? raw)
    {
        var v = raw?.Trim();
        if (string.IsNullOrEmpty(v)) return null;
        if (v.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) v = v[8..];
        else if (v.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) v = v[7..];
        v = v.Trim().Trim('/');
        return v.Length == 0 ? null : v;
    }

    /// <summary>单一镜像根改写（BMCLAPI 风格：meta → 根，assets → /assets，maven → /maven）</summary>
    private static string RewriteRoot(string url, string mirror)
    {
        foreach (var host in new[]
                 {
                     "piston-meta.mojang.com", "piston-data.mojang.com",
                     "launchermeta.mojang.com", "launcher.mojang.com",
                 })
        {
            if (Prefix(url, $"https://{host}", out var meta))
                return $"https://{mirror}{meta}";
        }
        if (Prefix(url, "https://resources.download.minecraft.net", out var rest))
            return $"https://{mirror}/assets{rest}";
        if (Prefix(url, "https://libraries.minecraft.net", out rest))
            return $"https://{mirror}/maven{rest}";
        return url;
    }

    /// <summary>FastMinecraftMirror 改写（按官方文档：服务 → 独立子域）</summary>
    private static string RewriteFastmirror(string url)
    {
        foreach (var (host, target) in new[]
                 {
                     ("piston-meta.mojang.com", "launchermeta.fastmcmirror.org"),
                     ("launchermeta.mojang.com", "launchermeta.fastmcmirror.org"),
                     ("piston-data.mojang.com", "launcher.fastmcmirror.org"),
                     ("launcher.mojang.com", "launcher.fastmcmirror.org"),
                 })
        {
            if (Prefix(url, $"https://{host}", out var rest))
                return $"https://{target}{rest}";
        }
        if (Prefix(url, "https://resources.download.minecraft.net", out var r))
            return $"https://resources.fastmcmirror.org{r}";
        if (Prefix(url, "https://libraries.minecraft.net", out r))
            return $"https://libraries.fastmcmirror.org{r}";
        if (Prefix(url, "https://libraries.mojang.com", out r))
            return $"https://libraries.fastmcmirror.org{r}";
        return url;
    }

    private static bool Prefix(string url, string prefix, out string rest)
    {
        if (url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            rest = url[prefix.Length..];
            return true;
        }
        rest = "";
        return false;
    }
}
