// 内核 · 契约：下载引擎。
//
// **契约按能力切，不按具体类切**：这里只描述"一个下载引擎该能做什么"，
// 谁实现、是内置还是第三方，运行时由注册表决定（设置里可以换实现）。
// 现有实现见 Kernel/Plugins/BuiltinDownloadPlugin.cs（包着老的 DownloadService）。

using Starcloud.Core.Models;
using Starcloud.Core.Services;

namespace Starcloud.Core.Kernel.Contracts;

/// <summary>批量下载的一项</summary>
public sealed record DownloadItem(string Url, string Dest, string? Sha1, long Size);

public interface IDownloadEngine
{
    /// <summary>实现名（设置/插件页给人看的，如"内置多线程引擎"）</summary>
    string DisplayName { get; }

    /// <summary>单文件下载（含重试），可选 SHA1 校验</summary>
    Task DownloadFileAsync(string url, string destFile, string? sha1 = null, int maxRetries = 3,
        CancellationToken ct = default);

    /// <summary>批量下载（多源分片竞速 + 并发治理 + 进度/速度/ETA 回调）</summary>
    Task DownloadBatchAsync(IReadOnlyList<DownloadItem> files, string stage, int concurrency, int maxRetries,
        IProgress<BatchProgress>? progress = null, CancellationToken ct = default);

    /// <summary>取文本（版本清单、JSON 接口）</summary>
    Task<string> GetStringAsync(string url, CancellationToken ct = default);

    /// <summary>POST JSON（LLM / 接口用）</summary>
    Task<string> PostJsonAsync(string url, string jsonBody, CancellationToken ct = default);
}
