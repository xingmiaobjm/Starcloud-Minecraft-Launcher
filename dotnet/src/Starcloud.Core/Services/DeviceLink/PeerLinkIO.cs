// 链路帧协议：JSON 行（UTF-8 + '\n'）+ 长度前缀二进制块（8 字节小端 + 流）。
// 承载无关：只面向 Stream，LAN / 蓝牙 / 星闪通道共用同一套。

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>一条命令请求（JSON 行反序列化；Args 保留原始元素，由各命令自行取参）。
/// BodyFile 由服务端填充：JSON 里带 has_body=true 时，命令行后紧跟一个文件块，读下来落到
/// 临时文件后把路径放这里（saves.put 的 zip 上传方向）。</summary>
public sealed class PeerRequest
{
    public string Cmd { get; init; } = "";

    /// <summary>命令的其余参数（extension data：JSON 里除已知属性外的全部键都落在这里）</summary>
    [System.Text.Json.Serialization.JsonExtensionData]
    public Dictionary<string, JsonElement> Args { get; init; } = new();

    /// <summary>随命令上传的文件块（服务端读取后的临时路径；处理完由服务端删除）</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string? BodyFile { get; set; }

    public string Str(string key, string fallback = "") =>
        Args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? fallback : fallback;

    public bool Bool(string key, bool fallback = false) =>
        Args.TryGetValue(key, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : fallback;

    public long Long(string key, long fallback = 0) =>
        Args.TryGetValue(key, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : fallback;

    /// <summary>命令是否附带文件块（发送方写 JSON 时带上这个标记）</summary>
    public bool HasBody => Bool("has_body");
}

/// <summary>命令响应：Payload 序列化为 JSON 行；SourceFile 非空表示 JSON 行后紧跟该文件的内容流。
/// DeleteAfterSend：文件是临时产物（如现场打包的 zip），发送成功后由服务端立即删除。</summary>
public sealed record PeerReply(
    bool Ok,
    object? Payload = null,
    string? SourceFile = null,
    string? Error = null,
    bool DeleteAfterSend = false)
{
    public static PeerReply Fail(string error) => new(false, Error: error);
}

public static class PeerLinkIO
{
    // 命令属性名与 JSON 键大小写不敏感（hello ↔ Cmd）；Args 字典仍保留原始键
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    public static async Task WriteLineAsync(Stream stream, object payload, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(payload, JsonOpts) + "\n");
        await stream.WriteAsync(bytes, ct);
        await stream.FlushAsync(ct);
    }

    public static async Task<string?> ReadLineAsync(Stream stream, CancellationToken ct)
    {
        var buf = new ArrayBufferWriter<byte>();
        var one = new byte[1];
        while (true)
        {
            var read = await stream.ReadAsync(one, ct);
            if (read == 0) return null; // 对端断开
            if (one[0] == (byte)'\n') break;
            buf.Write(one);
            if (buf.WrittenCount > 64 * 1024) throw new IOException("链路行超长");
        }
        return Encoding.UTF8.GetString(buf.WrittenSpan);
    }

    public static async Task<PeerRequest?> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var line = await ReadLineAsync(stream, ct);
        if (line is null) return null;
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<PeerRequest>(line, JsonOpts);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>写一个长度前缀块（内容来自文件；对大文件流式读，不整载内存）</summary>
    public static async Task WriteFileBlockAsync(Stream stream, string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read,
            1 << 16, FileOptions.Asynchronous | FileOptions.SequentialScan);
        var len = new byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(len, (ulong)fs.Length);
        await stream.WriteAsync(len, ct);

        var buffer = new byte[1 << 16];
        int read;
        while ((read = await fs.ReadAsync(buffer, ct)) > 0)
            await stream.WriteAsync(buffer.AsMemory(0, read), ct);
        await stream.FlushAsync(ct);
    }

    /// <summary>读一个长度前缀块落盘为文件；返回实际接收字节数（校验由调用方做）</summary>
    public static async Task<long> ReadBlockToFileAsync(Stream stream, long expectedSize, string destFile, CancellationToken ct)
    {
        var len = new byte[8];
        await ReadExactlyAsync(stream, len, ct);
        var size = (long)BinaryPrimitives.ReadUInt64LittleEndian(len);
        if (size is < 0 or > 8L * 1024 * 1024 * 1024) throw new IOException("链路块尺寸非法");
        if (expectedSize > 0 && size != expectedSize) throw new IOException($"链路块尺寸不符（声明 {expectedSize} / 实到 {size}）");

        var dir = Path.GetDirectoryName(destFile)!;
        Directory.CreateDirectory(dir);
        await using var fs = new FileStream(destFile, FileMode.Create, FileAccess.Write, FileShare.None,
            1 << 16, FileOptions.Asynchronous);
        var buffer = new byte[1 << 16];
        long remaining = size;
        while (remaining > 0)
        {
            var take = (int)Math.Min(remaining, buffer.Length);
            var read = await stream.ReadAsync(buffer.AsMemory(0, take), ct);
            if (read == 0) throw new IOException("对端在传输中途断开");
            await fs.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
        }
        return size;
    }

    public static async Task ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken ct)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), ct);
            if (read == 0) throw new IOException("对端在传输中途断开");
            offset += read;
        }
    }
}
