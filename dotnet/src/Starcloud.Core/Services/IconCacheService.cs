// 社区资源图标磁盘缓存：一 URL 一文件，文件名 = URL 的 SHA1（小写十六进制）

using System.Security.Cryptography;
using System.Text;

namespace Starcloud.Core.Services;

/// <summary>
/// 社区资源图标（Mod / 整合包 / 数据包 / 资源包 / 光影包封面）磁盘缓存。
/// 命中缓存后不再请求 CDN；开关实时读 <see cref="LauncherSettings.ResourceCacheEnabled"/>，
/// 关闭时完全不读不写（此时界面直连图标地址，行为与旧版一致）。
/// </summary>
public sealed class IconCacheService(DownloadService downloader, Func<LauncherSettings> settings)
{
    /// <summary>已在进行中的下载 URL，避免快速翻页时同一图标重复落盘</summary>
    private readonly HashSet<string> _inflight = new(StringComparer.Ordinal);

    private static readonly string[] KnownExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".bmp"];

    /// <summary>缓存是否启用（镜像 LauncherSettings.ResourceCacheEnabled）。</summary>
    public bool Enabled => settings().ResourceCacheEnabled;

    private static string FileNameFor(string url)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(url))).ToLowerInvariant();
        var ext = ".png";
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var fromUrl = Path.GetExtension(uri.AbsolutePath).ToLowerInvariant();
            if (KnownExtensions.Contains(fromUrl)) ext = fromUrl;
        }
        return hash + ext;
    }

    /// <summary>命中缓存返回本地图片路径；未启用 / 未命中返回 null。</summary>
    public string? TryGetCachedPath(string? url)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(url)) return null;
        var path = Path.Combine(LauncherPaths.IconCacheDir, FileNameFor(url));
        return File.Exists(path) ? path : null;
    }

    /// <summary>
    /// 确保图标已落盘（下载失败返回 null，不抛异常）。
    /// 供「先直连显示、后台落盘」的调用方与非阻塞预取使用。
    /// </summary>
    public async Task<string?> EnsureCachedAsync(string? url, CancellationToken ct = default)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(url)) return null;
        var path = Path.Combine(LauncherPaths.IconCacheDir, FileNameFor(url));
        if (File.Exists(path)) return path;
        await downloader.DownloadFileAsync(url, path, maxRetries: 2, ct: ct).ConfigureAwait(false);
        return path;
    }

    /// <summary>后台预取图标（同一 URL 同时只跑一次，失败静默忽略、下次再试）。</summary>
    public void BeginCacheInBackground(string? url)
    {
        if (!Enabled || string.IsNullOrWhiteSpace(url)) return;
        lock (_inflight)
        {
            if (!_inflight.Add(url)) return;
        }
        _ = Task.Run(async () =>
        {
            try { await EnsureCachedAsync(url).ConfigureAwait(false); }
            catch { /* 网络失败：下次访问时重试 */ }
            finally
            {
                lock (_inflight) { _inflight.Remove(url); }
            }
        });
    }

    /// <summary>清空图标缓存。</summary>
    public void ClearAll()
    {
        if (!Directory.Exists(LauncherPaths.IconCacheDir)) return;
        foreach (var f in Directory.EnumerateFiles(LauncherPaths.IconCacheDir))
        {
            try { File.Delete(f); }
            catch { /* 忽略占用 */ }
        }
    }
}
