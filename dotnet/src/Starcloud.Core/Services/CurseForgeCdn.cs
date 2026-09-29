// CurseForge 文件 CDN 的两件事：下载请求补 `x-api-key`、CDN 彻底不可用时改用 MCIM 文件镜像。
//
// ── 为什么要补 key ──────────────────────────────────────────────────────────
// 2026-07-16 起官方 CDN（edge.forgecdn.net / mediafilez.forgecdn.net）要求有效 API Key，
// 不带一律 401。补 key 走 <see cref="CurseForgeCdnAuthHandler"/>（挂在 HttpClient 上）：
// 一个是整文件/分片/探测所有路径都覆盖，二是调用方一行不用改。
//
// ── 镜像兜底 ────────────────────────────────────────────────────────────────
// MCIM 官方文档的改写规则是「路径不变，只换主机」：
//   mediafilez.forgecdn.net/files/1234/56/x.jar → mod.mcimirror.top/files/1234/56/x.jar
// 只作为**最后一层**：官方 CDN 下载失败后才试一次，且不给镜像发密钥
// （镜像用不到，密钥也不该外泄到第三方）。
// 文件字节仍以官方 CDN 为准 —— MCIM 在这里只是通道，不参与任何分发判断。

using System.Net.Http;

namespace Starcloud.Core.Services;

public static class CurseForgeCdn
{
    /// <summary>MCIM 文件镜像根（与官方 CDN 路径同构）</summary>
    public const string MirrorRoot = "https://mod.mcimirror.top";

    /// <summary>官方 CDN 主机（下载必须带 x-api-key 的两台）</summary>
    private static readonly string[] CdnHosts =
    [
        "edge.forgecdn.net",
        "mediafilez.forgecdn.net",
    ];

    /// <summary>该主机是不是 CurseForge 文件 CDN</summary>
    public static bool IsCdnHost(string host) =>
        host.Length > 0 && CdnHosts.Any(h => h.Equals(host, StringComparison.OrdinalIgnoreCase));

    /// <summary>该 URL 是不是 CurseForge 文件 CDN 直链</summary>
    public static bool IsCdnUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && IsCdnHost(uri.Host);

    /// <summary>
    /// 改写为 MCIM 镜像直链（主机换成镜像根，路径与查询串原样保留）；
    /// 非 CDN 直链原样返回。
    /// </summary>
    public static string Mirror(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || !IsCdnHost(uri.Host)) return url;
        var builder = new UriBuilder(uri) { Host = new Uri(MirrorRoot).Host, Scheme = "https", Port = -1 };
        return builder.Uri.AbsoluteUri;
    }
}

/// <summary>
/// 给 CurseForge CDN 的请求自动补 `x-api-key`（官方自 2026-07-16 起强制）。
/// 只对 CDN 主机生效：API 请求的密钥由 <see cref="CurseForgeService"/> 自己按接口层决定，
/// 镜像与社区代理永不发送密钥。
/// </summary>
internal sealed class CurseForgeCdnAuthHandler : DelegatingHandler
{
    private readonly Func<LauncherSettings> _settings;

    public CurseForgeCdnAuthHandler(Func<LauncherSettings> settings, HttpMessageHandler inner)
        : base(inner) => _settings = settings;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.RequestUri is { } uri && CurseForgeCdn.IsCdnHost(uri.Host) &&
            CurseForgeKey.Effective(_settings()) is { } key)
            request.Headers.TryAddWithoutValidation("x-api-key", key);
        return base.SendAsync(request, cancellationToken);
    }
}
