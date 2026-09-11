// 并发下载引擎：多文件批量下载 + 重试 + 进度/速度/ETA 回调（对应旧 Rust download 模块）

using System.Diagnostics;
using System.Text;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

/// <summary>批量下载进度快照</summary>
public sealed record BatchProgress(
    string Stage,
    int DoneFiles,
    int TotalFiles,
    long DoneBytes,
    long TotalBytes,
    double SpeedBytesPerSec,
    double? EtaSecs)
{
    public int Percentage => TotalBytes > 0 ? (int)(DoneBytes * 100 / TotalBytes) : 0;
}

public sealed class DownloadService
{
    private readonly HttpClient _http;

    public DownloadService()
    {
        var sockets = new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.None,
            MaxConnectionsPerServer = 64,
        };
        _http = new HttpClient(sockets) { Timeout = TimeSpan.FromSeconds(60) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Starcloud-Launcher");
    }

    /// <summary>单文件下载（含重试），可选 SHA1 校验</summary>
    public async Task DownloadFileAsync(
        string url, string destFile, string? sha1 = null, int maxRetries = 3,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destFile)!);
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                var tmp = destFile + ".sc-tmp";
                using (var resp = await _http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct))
                {
                    resp.EnsureSuccessStatusCode();
                    await using var fs = File.Create(tmp);
                    await resp.Content.CopyToAsync(fs, ct);
                }
                if (!string.IsNullOrEmpty(sha1))
                {
                    var actual = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(tmp)));
                    if (!actual.Equals(sha1, StringComparison.OrdinalIgnoreCase))
                        throw new IOException($"SHA1 校验失败: {Path.GetFileName(destFile)}");
                }
                File.Move(tmp, destFile, overwrite: true);
                return;
            }
            catch (Exception) when (attempt < maxRetries && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(attempt, 5)), ct);
            }
        }
    }

    /// <summary>字符串内容下载</summary>
    public async Task<string> GetStringAsync(string url, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(url, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>POST JSON 并返回响应文本</summary>
    public async Task<string> PostJsonAsync(string url, string jsonBody, CancellationToken ct = default)
    {
        using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>并发批量下载文件（相同 URL 集合去重），汇报整体进度</summary>
    public async Task DownloadBatchAsync(
        IReadOnlyList<(string Url, string Dest, string? Sha1, long Size)> files,
        string stage,
        int concurrency,
        int maxRetries,
        IProgress<BatchProgress>? progress,
        CancellationToken ct = default)
    {
        // 去重（同 hash 已存在则跳过）
        var pending = new List<(string Url, string Dest, string? Sha1, long Size)>();
        foreach (var f in files.DistinctBy(f => f.Dest))
        {
            if (File.Exists(f.Dest) && f.Size > 0 && new FileInfo(f.Dest).Length == f.Size)
                continue;
            pending.Add(f);
        }

        long totalBytes = pending.Sum(f => Math.Max(f.Size, 0));
        long doneBytes = 0;
        int doneFiles = 0;
        var total = pending.Count + (files.Count - pending.Count);
        var done = files.Count - pending.Count;
        var sw = Stopwatch.StartNew();
        var lastReported = 0L;

        void Report()
        {
            if (progress is null) return;
            var elapsed = sw.Elapsed.TotalSeconds;
            var speed = elapsed > 0.5 ? doneBytes / elapsed : 0;
            double? eta = speed > 1 && totalBytes > doneBytes ? (totalBytes - doneBytes) / speed : null;
            progress.Report(new BatchProgress(stage, done + doneFiles, files.Count, doneBytes + lastReported,
                Math.Max(totalBytes, 1), speed, eta));
        }

        if (pending.Count == 0)
        {
            doneBytes = totalBytes;
            doneFiles = files.Count;
            Report();
            return;
        }

        using var gate = new SemaphoreSlim(Math.Max(1, concurrency));
        var tasks = pending.Select(async f =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await DownloadFileAsync(f.Url, f.Dest, f.Sha1, maxRetries, ct);
                Interlocked.Add(ref doneBytes, Math.Max(f.Size, 0));
                Interlocked.Increment(ref doneFiles);
                Report();
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>获取 Java 主版本要求的 Adoptium 下载地址（记忆约束：解析 binary 字段）</summary>
    public async Task<string> GetAdoptiumDownloadUrlAsync(int majorVersion, CancellationToken ct = default)
    {
        var url = $"https://api.adoptium.net/v3/assets/latest/{majorVersion}/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse";
        var json = await GetStringAsync(url, ct);
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        foreach (var release in doc.RootElement.EnumerateArray())
        {
            if (!release.TryGetProperty("binary", out var binary)) continue;
            if (!binary.TryGetProperty("package", out var package)) continue;
            if (!package.TryGetProperty("link", out var link)) continue;
            var linkStr = link.GetString();
            if (!string.IsNullOrEmpty(linkStr) && linkStr.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                return linkStr;
        }
        throw new InvalidOperationException($"Adoptium 未返回可用的 Java {majorVersion} 下载地址");
    }
}
