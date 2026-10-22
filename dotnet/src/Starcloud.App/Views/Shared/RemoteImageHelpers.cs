// 远程图片源（社区资源封面 / 玩家皮肤头像）：本地磁盘缓存优先，未命中先直连显示再后台落盘。

using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Starcloud.App.Views.Rows;

namespace Starcloud.App.Views.Shared;

/// <summary>
/// 社区资源远程图标（Modrinth CDN）解析：优先本地磁盘缓存，命中直接读文件、不再发网络请求；
/// 未命中先直连远程显示，同时后台落盘，供下次复用。
/// </summary>
public static class RemoteIconHelper
{
    public static ImageSource? Resolve(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return null;
        var cache = AppServices.Instance.IconCache;
        if (cache.TryGetCachedPath(url) is { } local)
            return VersionRowHelpers.ResolveIconSource(local);
        cache.BeginCacheInBackground(url);
        return VersionRowHelpers.ResolveIconSource(url);
    }
}

/// <summary>
/// 玩家皮肤头像（第三方服务 minotar.net，helm = 头部含帽子层）：微软账户用 UUID（不随改名失效），
/// 第三方账户用认证服务器的会话皮肤。复用社区图标的「缓存优先 + 直连兜底」逻辑；请求失败（无网络 /
/// 名字不存在返回 404）时 <see cref="Microsoft.UI.Xaml.Controls.Image"/> 不渲染，由底层的首字母色块自然兜底。
/// 注：曾用 mc-heads.net，实测对本机微软档案间歇性返回 403（Cloudflare 拦截），故弃用。
/// </summary>
public static class AvatarHelper
{
    /// <summary>头像边长（像素）：128 供首页 72dip 大头像放大后仍清晰，列表小头像同源复用。</summary>
    private const int Size = 128;

    /// <summary>拼头像 URL；账户标识缺失返回 null（调用方保持首字母色块）。</summary>
    public static string? UrlFor(string? type, string? uuid, string? username)
    {
        var id = type == "microsoft" ? uuid : username;
        if (string.IsNullOrWhiteSpace(id)) return null;
        return $"https://minotar.net/helm/{Uri.EscapeDataString(id)}/{Size}";
    }

    /// <summary>
    /// 解析账户头像；无标识 / 无网络 / 名字不存在时返回 null。
    /// 皮肤库里给该账户指定了皮肤时优先用它渲染的头像（离线账户唯一能显示自己皮肤的路子）：
    /// 注意这里要**渲染**而不是直接把 64×64 纹理当图片显示 —— 那样出来的是头、身子、
    /// 胳膊挤在一个小方框里的一团色块，看起来就不像个头像。
    /// </summary>
    public static ImageSource? Resolve(string? type, string? uuid, string? username,
        string? externalBase = null, string? localSkinPath = null)
    {
        if (!string.IsNullOrWhiteSpace(localSkinPath) && File.Exists(localSkinPath))
            return SkinThumbnails.HeadFromFile(localSkinPath);
        return type == "external" ? ResolveExternal(uuid, externalBase)
                                  : RemoteIconHelper.Resolve(UrlFor(type, uuid, username));
    }

    /// <summary>皮肤库里该账户指定的皮肤文件；没指定 / 文件已丢返回 null</summary>
    public static string? LocalSkinPath(Starcloud.Core.Services.StoredAccount? account)
    {
        if (account?.SkinId is not { Length: > 0 } skinId) return null;
        var library = AppServices.Instance.Skins;
        var entry = library.Find(skinId);
        if (entry is null) return null;
        var path = library.PathOf(entry);
        return File.Exists(path) ? path : null;
    }

    /// <summary>external 内存缓存 (serverBase, uuid) → 皮肤 URL；null 值也缓存，避免重复请求。</summary>
    private static readonly Dictionary<(string, string), string?> ExternalUrlCache = [];

    /// <summary>外部账户皮肤纹理的本地缓存目录（头像从这里裁头部，不直接显示整张皮肤）。</summary>
    private static string ExternalSkinDir => Path.Combine(LauncherPaths.CacheDir, "external-skins");

    /// <summary>后台下载/渲染完成后触发：页面回到 UI 线程刷新头像。</summary>
    public static event Action? AvatarUpdated;

    private static readonly HttpClient ExternalHttp = new() { Timeout = TimeSpan.FromSeconds(20) };
    private static readonly HashSet<string> ExternalHeadDownloads = [];

    private static readonly TimeSpan ExternalSkinTtl = TimeSpan.FromDays(7);

    /// <summary>
    /// 第三方皮肤：会话服务器查询是异步的，首次调用后台解析（此时显示首字母色块），
    /// 解析完成后下一次刷新列表即命中缓存显示皮肤；失败保持首字母色块。
    /// </summary>
    private static ImageSource? ResolveExternal(string? uuid, string? externalBase)
    {
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(externalBase)) return null;
        var cacheKey = (externalBase, uuid);
        if (!ExternalUrlCache.TryGetValue(cacheKey, out var url))
        {
            ExternalUrlCache[cacheKey] = null; // 占位防并发重复请求
            _ = ResolveExternalCoreAsync(externalBase, uuid);
            return null;
        }
        if (url is null) return null;

        // 直接把 64×64 皮肤纹理丢给 Image 会显示成"整张皮肤"（正是之前的 bug）。
        // 正确做法：先把纹理落到本地，再按头部 UV 裁出方形头像。
        var cached = CachedExternalSkin(url);
        if (cached is not null)
        {
            if (DateTime.UtcNow - File.GetLastWriteTimeUtc(cached) > ExternalSkinTtl)
                StartExternalHeadDownload(url); // 过期就先显示旧头像，后台悄悄刷新
            return SkinThumbnails.HeadFromFile(cached);
        }
        StartExternalHeadDownload(url);
        return null;
    }

    private static string? CachedExternalSkin(string url)
    {
        try
        {
            var path = ExternalSkinPath(url);
            return File.Exists(path) ? path : null;
        }
        catch
        {
            return null;
        }
    }

    private static string ExternalSkinPath(string url) =>
        Path.Combine(ExternalSkinDir, Hash(url) + ".png");

    private static string Hash(string text) =>
        Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    /// <summary>下载皮肤纹理 → 校验是合法皮肤尺寸 → 落盘 → 通知页面刷新（每张皮肤同时只下一次）。</summary>
    private static void StartExternalHeadDownload(string url)
    {
        lock (ExternalHeadDownloads)
        {
            if (!ExternalHeadDownloads.Add(url)) return;
        }
        _ = Task.Run(async () =>
        {
            try
            {
                var bytes = await ExternalHttp.GetByteArrayAsync(url);
                if (!LooksLikeSkin(bytes))
                {
                    LauncherLog.Write("skin", $"外部账户皮肤不是有效 PNG：{url}");
                    return;
                }
                Directory.CreateDirectory(ExternalSkinDir);
                await File.WriteAllBytesAsync(ExternalSkinPath(url), bytes);
                AvatarUpdated?.Invoke();
            }
            catch (Exception ex)
            {
                LauncherLog.Write("skin", $"外部账户皮肤下载失败：{ex.Message}");
            }
            finally
            {
                lock (ExternalHeadDownloads) ExternalHeadDownloads.Remove(url);
            }
        });
    }

    /// <summary>最小 PNG 校验：签名 + IHDR 尺寸（64×64 / 64×32，以及等比例的 HD 皮肤）。</summary>
    private static bool LooksLikeSkin(byte[] bytes)
    {
        if (bytes.Length < 24) return false;
        ReadOnlySpan<byte> png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
        if (!bytes.AsSpan(0, 8).SequenceEqual(png)) return false;
        var width = (bytes[16] << 24) | (bytes[17] << 16) | (bytes[18] << 8) | bytes[19];
        var height = (bytes[20] << 24) | (bytes[21] << 16) | (bytes[22] << 8) | bytes[23];
        return width >= 64 && height >= 32 && (width == height || width == 2 * height);
    }

    private static async Task ResolveExternalCoreAsync(string externalBase, string uuid)
    {
        string? url = null;
        try
        {
            url = await AppServices.Instance.Yggdrasil.GetSkinUrlAsync(externalBase, uuid);
        }
        catch { /* 皮肤是尽力而为 */ }
        ExternalUrlCache[(externalBase, uuid)] = string.IsNullOrWhiteSpace(url) ? null : url;
    }
}
