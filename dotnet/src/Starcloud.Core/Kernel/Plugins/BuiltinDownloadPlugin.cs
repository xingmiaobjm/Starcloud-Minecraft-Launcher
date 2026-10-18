// 内置插件：多线程下载引擎。
//
// 这是"万物皆插件"的第一个样板：**老的 DownloadService 一行没改**，
// 插件把它按 IDownloadEngine 契约注册进内核。后续把调用点从 AppServices.Download
// 改成 kernel.Get<IDownloadEngine>() 之后，换实现就只是"注册一个优先级更高的插件"。

using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Services;

namespace Starcloud.Core.Kernel.Plugins;

/// <summary>内置下载引擎插件：包住既有的 <see cref="DownloadService"/></summary>
public sealed class BuiltinDownloadPlugin(Func<LauncherSettings> settings) : PluginBase
{
    public const string PluginId = "starcloud.download";

    public static PluginDescriptor Descriptor { get; } = new()
    {
        Id = PluginId,
        Name = "多线程下载引擎",
        Version = "1.0.0",
        Description = "多文件批量下载 + 多源分片竞速 + 重试 + 断点续传；可在局域网内复用同账号设备的已下载文件",
        Kind = PluginKind.Engine,
        Activation = PluginActivation.Eager,
        Builtin = true,
        Provides = [typeof(IDownloadEngine)],
        Permissions = ["net.http", "fs.write:launcher"],
    };

    private DownloadService? _engine;

    public override Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        base.InitializeAsync(context, ct);
        _engine = new DownloadService(settings)
        {
            Log = message => context.Log.Info(message),
        };
        // 契约（给新代码用）
        context.Services.Add<IDownloadEngine>(new DownloadEngineAdapter(_engine));
        // 过渡期：ManifestService / IconCacheService 的构造参数还是具体类，
        // 所以登记具体类型让它们能链上来（不走 Add<T>——契约必须是接口）。
        // 等 P1/P2 把这些服务本身改成契约就撤掉这两行。
        context.Services.AddBuiltin(typeof(DownloadService), _engine);
        context.Services.AddBuiltin(typeof(ResourceCacheService), new ResourceCacheService(settings));
        return Task.CompletedTask;
    }

    public override Task ShutdownAsync(CancellationToken ct = default)
    {
        _engine = null;   // 契约注销由内核按 provider 批量处理，这里只需松开引用
        return Task.CompletedTask;
    }

    /// <summary>适配器：把老服务的签名翻成契约签名（老服务不改，改造面最小）</summary>
    private sealed class DownloadEngineAdapter(DownloadService inner) : IDownloadEngine
    {
        public string DisplayName => "内置多线程引擎";

        public Task DownloadFileAsync(string url, string destFile, string? sha1 = null, int maxRetries = 3,
            CancellationToken ct = default) =>
            inner.DownloadFileAsync(url, destFile, sha1, maxRetries, ct);

        public Task DownloadBatchAsync(IReadOnlyList<DownloadItem> files, string stage, int concurrency,
            int maxRetries, IProgress<BatchProgress>? progress = null, CancellationToken ct = default) =>
            inner.DownloadBatchAsync(
                [.. files.Select(f => (f.Url, f.Dest, f.Sha1, f.Size))],
                stage, concurrency, maxRetries, progress, ct);

        public Task<string> GetStringAsync(string url, CancellationToken ct = default) =>
            inner.GetStringAsync(url, ct);

        public Task<string> PostJsonAsync(string url, string jsonBody, CancellationToken ct = default) =>
            inner.PostJsonAsync(url, jsonBody, ct);
    }
}
