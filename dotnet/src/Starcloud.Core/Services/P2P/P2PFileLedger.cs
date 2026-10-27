// 互联网 P2P 的本地文件索引：记录「我确实有这么一份文件」的 (sha1, size, 路径)。
//
// 只在下载成功后记账（下载路径本来就知道 manifest 给的 SHA1），不做全盘扫描——
// 几百 GB 的 .minecraft 边扫边算哈希不现实。索引用于两件事：
//   ① 定期 announce 给发现服务，让别人查得到我；
//   ② 收到 p2p.request 时按 sha1 找回本地文件，供之前再整文件算一次 SHA1（防投毒）。
//
// 安全线：只索引 ≥2 MiB 的文件（小文件走 P2P 不划算），落盘用 JSON，上限 5000 条。

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services.P2P;

/// <summary>announce 用的一行：sha1 + 大小（路径不出本机）</summary>
public sealed record P2PIndexEntry(
    [property: JsonPropertyName("sha1")] string Sha1,
    [property: JsonPropertyName("size")] long Size);

/// <summary>落盘的一条索引（路径只在本地用）</summary>
internal sealed class P2PLedgerEntry
{
    [JsonPropertyName("sha1")] public string Sha1 { get; set; } = "";
    [JsonPropertyName("size")] public long Size { get; set; }
    [JsonPropertyName("path")] public string Path { get; set; } = "";
    [JsonPropertyName("at")] public long At { get; set; }
}

public sealed class P2PFileLedger
{
    /// <summary>低于这个大小不进索引：小文件走公共源比打洞快</summary>
    public const long MinIndexedSize = 2 * 1024 * 1024;

    private const int MaxEntries = 5000;

    private readonly string _path;
    private readonly object _gate = new();
    private readonly object _saveGate = new();
    private readonly Dictionary<string, P2PLedgerEntry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, (long Length, DateTime WriteTime, string Sha1)> _hashCache =
        new(StringComparer.OrdinalIgnoreCase);
    private long _lastSaveAt;
    private volatile bool _dirty;

    public P2PFileLedger(string? path = null)
    {
        _path = path ?? System.IO.Path.Combine(LauncherPaths.DataDir, "p2p-index.json");
        Load();
    }

    /// <summary>索引条目数（测试与日志用）</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _entries.Count;
        }
    }

    /// <summary>下载完成后记账；sha1 缺失或文件不存在时什么也不做（不影响下载）。</summary>
    public void Record(string destFile, string? sha1, long size)
    {
        try
        {
            var hash = NormalizeSha1(sha1);
            if (hash is null) return;
            if (size < MinIndexedSize) return;
            if (!File.Exists(destFile)) return;
            var actual = new FileInfo(destFile).Length;
            if (actual < MinIndexedSize) return;

            lock (_gate)
            {
                _entries[hash] = new P2PLedgerEntry
                {
                    Sha1 = hash,
                    Size = actual,
                    Path = Path.GetFullPath(destFile),
                    At = DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
                };
                if (_entries.Count > MaxEntries) TrimLocked();
                _dirty = true;
            }
            SaveIfDue();
        }
        catch
        {
            // 索引只是加速手段：记账失败不影响下载
        }
    }

    /// <summary>announce 快照：文件仍存在且大小没变，按体积从大到小（先传最值钱的）。</summary>
    public IReadOnlyList<P2PIndexEntry> Snapshot(int max = 2000)
    {
        List<P2PLedgerEntry> alive;
        lock (_gate) alive = _entries.Values.ToList();

        var result = new List<P2PIndexEntry>(Math.Min(max, alive.Count));
        foreach (var entry in alive
                     .Where(Exists)
                     .OrderByDescending(e => e.Size)
                     .Take(max))
            result.Add(new P2PIndexEntry(entry.Sha1, entry.Size));
        return result;
    }

    /// <summary>按 sha1 找本地文件；大小不符视为过期。</summary>
    public bool TryResolve(string sha1, long? size, out string path, out long actualSize)
    {
        path = "";
        actualSize = 0;
        var hash = NormalizeSha1(sha1);
        if (hash is null) return false;
        P2PLedgerEntry? entry;
        lock (_gate) _entries.TryGetValue(hash, out entry);
        if (entry is null || !File.Exists(entry.Path)) return false;
        var length = new FileInfo(entry.Path).Length;
        if (size is > 0 && length != size.Value) return false;
        if (length != entry.Size) return false;
        path = entry.Path;
        actualSize = length;
        return true;
    }

    /// <summary>
    /// 供文件前整文件核对 SHA1（命中缓存按「长度 + 修改时间」失效）。
    /// 对不上说明文件被换过：绝不能把别人的文件当这份发出去。
    /// </summary>
    public async Task<bool> VerifyAsync(string path, string sha1, CancellationToken ct = default)
    {
        var expected = NormalizeSha1(sha1);
        if (expected is null) return false;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return false;
            lock (_gate)
            {
                if (_hashCache.TryGetValue(path, out var cached) &&
                    cached.Length == info.Length && cached.WriteTime == info.LastWriteTimeUtc &&
                    cached.Sha1.Equals(expected, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.Read, 1 << 16, useAsync: true);
            var actual = Convert.ToHexString(await SHA1.HashDataAsync(stream, ct).ConfigureAwait(false))
                .ToLowerInvariant();
            if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase)) return false;

            lock (_gate)
            {
                _hashCache[path] = (info.Length, info.LastWriteTimeUtc, actual);
                if (_hashCache.Count > MaxEntries) _hashCache.Clear();
            }
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>测试探针：强制落盘等场合直接调用。</summary>
    public void Flush()
    {
        lock (_saveGate)
        {
            try
            {
                List<P2PLedgerEntry> snapshot;
                lock (_gate)
                {
                    snapshot = _entries.Values.ToList();
                    _dirty = false;
                    _lastSaveAt = Environment.TickCount64;
                }
                var dir = Path.GetDirectoryName(_path);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                var tmp = _path + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(snapshot, JsonOpts));
                File.Move(tmp, _path, overwrite: true);
            }
            catch
            {
                // 落盘失败不影响运行期索引
            }
        }
    }

    private void SaveIfDue()
    {
        var now = Environment.TickCount64;
        if (!_dirty || now - Interlocked.Read(ref _lastSaveAt) < 5000) return;
        Flush();
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path)) return;
            var items = JsonSerializer.Deserialize<List<P2PLedgerEntry>>(
                File.ReadAllText(_path), JsonOpts) ?? [];
            lock (_gate)
            {
                foreach (var item in items)
                {
                    if (NormalizeSha1(item.Sha1) is not { } hash) continue;
                    item.Sha1 = hash;
                    _entries[hash] = item;
                }
            }
        }
        catch
        {
            // 索引文件坏了就当没有：重新记账即可
        }
    }

    private static bool Exists(P2PLedgerEntry entry)
    {
        try
        {
            var info = new FileInfo(entry.Path);
            return info.Exists && info.Length == entry.Size;
        }
        catch
        {
            return false;
        }
    }

    private void TrimLocked()
    {
        foreach (var key in _entries.OrderBy(e => e.Value.At)
                     .Take(_entries.Count - MaxEntries).Select(e => e.Key).ToList())
            _entries.Remove(key);
    }

    /// <summary>只接受 40 位十六进制；没有哈希的条目一律不进索引。</summary>
    private static string? NormalizeSha1(string? sha1)
    {
        if (string.IsNullOrWhiteSpace(sha1) || sha1.Length != 40) return null;
        foreach (var c in sha1)
        {
            var hex = c is >= '0' and <= '9' or >= 'a' and <= 'f' or >= 'A' and <= 'F';
            if (!hex) return null;
        }
        return sha1.ToLowerInvariant();
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = false,
    };
}
