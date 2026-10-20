// 远程图片源（社区资源封面 / 玩家皮肤头像）：本地磁盘缓存优先，未命中先直连显示再后台落盘。
// 移植自桌面 Starcloud.App.Views.Shared.RemoteImageHelpers.cs。
// 唯一的适配点：桌面直连兜底把 http URL 直接塞给 ResolveIconSource（桌面 BitmapImage 吃 http），
// 安卓侧 VersionRowHelpers.ResolveIconSource 只认 ms-appx: 与 file:// 两种本地源，
// 远程 URL 改走 Services/Ui.HttpImage（BitmapImage 直连 http URI）。

using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Rows;

namespace Starcloud.Launcher.Uno.Views.Shared;

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
        return Ui.HttpImage(url);
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
    public static string? LocalSkinPath(StoredAccount? account)
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
        return url is null ? null : RemoteIconHelper.Resolve(url);
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
