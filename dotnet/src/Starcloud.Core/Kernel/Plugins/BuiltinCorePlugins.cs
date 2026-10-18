// 内置插件：版本目录 / 搜索翻译 / 图标缓存。
//
// 这三个是"功能也能是插件"的样板：老服务一行没改，插件把它们按契约注册进内核。
// 图标缓存插件**依赖**下载插件——内核会先激活依赖，插件之间只通过契约取用
// （看 BuiltinIconCachePlugin：它从内核拿 DownloadService，不自己 new）。

using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Models;
using Starcloud.Core.Services;

namespace Starcloud.Core.Kernel.Plugins;

/// <summary>内置版本目录插件（官方清单 + 本地版本枚举）</summary>
public sealed class BuiltinVersionPlugin : PluginBase
{
    public const string PluginId = "starcloud.versions";

    public static PluginDescriptor Descriptor { get; } = new()
    {
        Id = PluginId,
        Name = "版本管理",
        Version = "1.0.0",
        Description = "官方版本清单拉取与缓存、本地已安装版本枚举、版本删除",
        Kind = PluginKind.Feature,
        Activation = PluginActivation.Eager,
        Builtin = true,
        Provides = [typeof(IVersionCatalog)],
        Depends = new Dictionary<string, string> { [BuiltinDownloadPlugin.PluginId] = "^1.0.0" },
        Permissions = ["net.http", "fs.write:minecraft"],
    };

    public override Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        base.InitializeAsync(context, ct);
        var service = new ManifestService(
            context.Kernel.Get<DownloadService>(),
            context.Kernel.Get<ResourceCacheService>());
        context.Services.Add<IVersionCatalog>(new VersionCatalog(service));
        context.Services.AddBuiltin(typeof(ManifestService), service);   // 过渡期：老调用点按具体类型用
        return Task.CompletedTask;
    }

    private sealed class VersionCatalog(ManifestService inner) : IVersionCatalog
    {
        public string DisplayName => "官方清单（Mojang）";

        public Task<MinecraftManifest> GetManifestAsync(bool forceRefresh, CancellationToken ct = default) =>
            inner.GetManifestAsync(forceRefresh, ct);

        public IReadOnlyList<InstalledVersionInfo> ListInstalled(string dotMinecraft) =>
            inner.ListInstalled(dotMinecraft);
    }
}

/// <summary>内置搜索翻译插件</summary>
public sealed class BuiltinTranslatePlugin : PluginBase
{
    public const string PluginId = "starcloud.translate";

    public static PluginDescriptor Descriptor { get; } = new()
    {
        Id = PluginId,
        Name = "搜索词翻译",
        Version = "1.0.0",
        Description = "中文搜索词 → 英文项目名（内置词典 → MC 百科 → 机器翻译，带缓存）",
        Kind = PluginKind.Engine,
        Activation = PluginActivation.Eager,
        Builtin = true,
        Provides = [typeof(ITranslationEngine)],
        Depends = new Dictionary<string, string> { [BuiltinDownloadPlugin.PluginId] = "^1.0.0" },
        Permissions = ["net.http"],
    };

    public override Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        base.InitializeAsync(context, ct);
        var service = new SearchTranslationService(context.Kernel.Get<DownloadService>());
        context.Services.Add<ITranslationEngine>(new TranslationAdapter(service));
        context.Services.AddBuiltin(typeof(SearchTranslationService), service);   // 过渡期：老调用点按具体类型用
        return Task.CompletedTask;
    }

    private sealed class TranslationAdapter(SearchTranslationService inner) : ITranslationEngine
    {
        public string DisplayName => "内置词典 + 机器翻译";
        public Task<TranslatedQuery> TranslateAsync(string query, CancellationToken ct = default) =>
            inner.TranslateAsync(query, ct);
    }
}

/// <summary>内置图标缓存插件：**依赖下载插件**（内核保证依赖先激活）</summary>
public sealed class BuiltinIconCachePlugin(Func<LauncherSettings> settings) : PluginBase
{
    public const string PluginId = "starcloud.icons";

    public static PluginDescriptor Descriptor { get; } = new()
    {
        Id = PluginId,
        Name = "图标缓存",
        Version = "1.0.0",
        Description = "把远程图标/封面落到本地磁盘并复用（走下载引擎）",
        Kind = PluginKind.Engine,
        Activation = PluginActivation.Eager,
        Builtin = true,
        Provides = [typeof(IIconCache)],
        Depends = new Dictionary<string, string> { [BuiltinDownloadPlugin.PluginId] = "^1.0.0" },
        Permissions = ["fs.write:launcher"],
    };

    public override Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        base.InitializeAsync(context, ct);
        var service = new IconCacheService(context.Kernel.Get<DownloadService>(), settings);
        context.Services.Add<IIconCache>(new IconCacheAdapter(service));
        context.Services.AddBuiltin(typeof(IconCacheService), service);   // 过渡期：老调用点按具体类型用
        return Task.CompletedTask;
    }

    private sealed class IconCacheAdapter(IconCacheService inner) : IIconCache
    {
        public string DisplayName => "本地磁盘缓存";
        public string? TryGetCachedPath(string? url) => inner.TryGetCachedPath(url);
        public Task<string?> EnsureCachedAsync(string? url, CancellationToken ct = default) => inner.EnsureCachedAsync(url, ct);
        public void BeginCacheInBackground(string? url) => inner.BeginCacheInBackground(url);
        public void ClearAll() => inner.ClearAll();
    }
}
