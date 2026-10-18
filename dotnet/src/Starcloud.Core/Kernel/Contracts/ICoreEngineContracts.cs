// 内核 · 契约：版本目录 / 翻译引擎 / 图标缓存。
//
// 三个都是"能力"，不是"类"：换实现（换镜像源、换翻译服务、换图标后端）不该动调用点。

using Starcloud.Core.Models;
using Starcloud.Core.Services;

namespace Starcloud.Core.Kernel.Contracts;

/// <summary>版本目录：远程清单 + 本地已安装版本</summary>
public interface IVersionCatalog
{
    /// <summary>实现名（设置/插件页展示）</summary>
    string DisplayName { get; }

    /// <summary>拉取（带缓存）官方版本清单</summary>
    Task<MinecraftManifest> GetManifestAsync(bool forceRefresh, CancellationToken ct = default);

    /// <summary>枚举本地已安装版本</summary>
    IReadOnlyList<InstalledVersionInfo> ListInstalled(string dotMinecraft);
}

/// <summary>搜索词翻译引擎（中文 → 英文；社区只有英文项目名）</summary>
public interface ITranslationEngine
{
    string DisplayName { get; }

    /// <summary>翻译搜索词（内置词典 → MC 百科 → 机器翻译，带缓存）</summary>
    Task<TranslatedQuery> TranslateAsync(string query, CancellationToken ct = default);
}

/// <summary>图标缓存（模组/整合包封面等远程图片落地到本地）</summary>
public interface IIconCache
{
    string DisplayName { get; }

    string? TryGetCachedPath(string? url);
    Task<string?> EnsureCachedAsync(string? url, CancellationToken ct = default);
    void BeginCacheInBackground(string? url);
    void ClearAll();
}
