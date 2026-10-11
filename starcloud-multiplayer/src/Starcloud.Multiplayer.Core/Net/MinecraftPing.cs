using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace Starcloud.Multiplayer.Core.Net;

/// <summary>Minecraft 探测结果。</summary>
public sealed record PingResult(bool Ok, string? Version, string? Motd, long LatencyMs, string? Error);

/// <summary>
/// Minecraft Server List Ping 探针。自检就靠它：能拿到合法应答，就证明「客人本地端口 →
/// 房主 MC 端口」这条隧道真的通了。
/// <para>
/// 两套协议都实现：现代（1.7+ 的 Handshake + Status）优先，失败再退老式（1.6 的 0xFE）。
/// </para>
/// </summary>
public static class MinecraftPing
{
    public static async Task<PingResult> ProbeAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct = default)
    {
        try
        {
            return await ProbeModernAsync(host, port, timeout, ct);
        }
        catch (Exception modernEx) when (modernEx is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            try
            {
                return await ProbeLegacyAsync(host, port, timeout, ct);
            }
            catch (Exception legacyEx)
            {
                return new PingResult(false, null, null, 0, $"现代协议：{modernEx.Message}；老式协议：{legacyEx.Message}");
            }
        }
    }

    private static async Task<PingResult> ProbeModernAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var client = new System.Net.Sockets.TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await client.ConnectAsync(host, port, cts.Token);
        await using var stream = client.GetStream();

        using var handshake = new MemoryStream();
        McWire.WriteVarInt(handshake, 0x00);   // 包 id：Handshake
        McWire.WriteVarInt(handshake, -1);     // 协议版本 -1：只问状态、不真进服
        McWire.WriteString(handshake, host);
        handshake.WriteByte((byte)(port >> 8));
        handshake.WriteByte((byte)(port & 0xFF));
        McWire.WriteVarInt(handshake, 1);      // next state = status

        var body = handshake.ToArray();
        McWire.WriteVarInt(stream, body.Length);
        await stream.WriteAsync(body, cts.Token);

        McWire.WriteVarInt(stream, 1);         // Status Request：长度 1、id 0x00
        stream.WriteByte(0x00);
        await stream.FlushAsync(cts.Token);

        _ = await McWire.ReadVarIntAsync(stream, cts.Token);   // 整个响应包的长度
        var packetId = await McWire.ReadVarIntAsync(stream, cts.Token);
        if (packetId != 0x00)
            throw new FormatException($"状态响应包 id 意外：0x{packetId:X2}");

        var json = await McWire.ReadStringAsync(stream, cts.Token);
        var (version, motd) = ExtractModern(json);

        return new PingResult(true, version, motd, started.ElapsedMilliseconds, null);
    }

    private static async Task<PingResult> ProbeLegacyAsync(
        string host, int port, TimeSpan timeout, CancellationToken ct)
    {
        var started = System.Diagnostics.Stopwatch.StartNew();
        using var client = new System.Net.Sockets.TcpClient();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);

        await client.ConnectAsync(host, port, cts.Token);
        await using var stream = client.GetStream();

        stream.WriteByte(0xFE);
        await stream.FlushAsync(cts.Token);

        var head = new byte[1];
        await McWire.ReadExactlyAsync(stream, head, cts.Token);
        if (head[0] != 0xFF)
            throw new FormatException($"老式响应首字节意外：0x{head[0]:X2}");

        var lengthBytes = new byte[2];
        await McWire.ReadExactlyAsync(stream, lengthBytes, cts.Token);
        var chars = BinaryPrimitives.ReadUInt16BigEndian(lengthBytes);

        var payload = new byte[chars * 2];
        await McWire.ReadExactlyAsync(stream, payload, cts.Token);
        // 老式响应用 UTF-16BE：§1\0协议号\0版本\0MOTD\0在线\0上限
        var parts = Encoding.BigEndianUnicode.GetString(payload).Split('\0');
        var version = parts.Length > 2 ? parts[2] : null;
        var motd = parts.Length > 3 ? parts[3] : null;

        return new PingResult(true, version, motd, started.ElapsedMilliseconds, null);
    }

    private static (string? Version, string? Motd) ExtractModern(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string? version = null;
        if (root.TryGetProperty("version", out var ver) && ver.TryGetProperty("name", out var name))
            version = name.GetString();

        string? motd = null;
        if (root.TryGetProperty("description", out var desc))
            motd = desc.ValueKind == JsonValueKind.String ? desc.GetString() : FlattenChat(desc);

        return (version, motd);
    }

    /// <summary>把聊天组件拍平成纯文本（只取 text 与 extra，够自检展示用）。</summary>
    private static string FlattenChat(JsonElement element)
    {
        var sb = new StringBuilder();
        if (element.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
            sb.Append(text.GetString());
        if (element.TryGetProperty("extra", out var extra) && extra.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in extra.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String) sb.Append(item.GetString());
                else if (item.ValueKind == JsonValueKind.Object) sb.Append(FlattenChat(item));
            }
        }
        return sb.ToString();
    }
}

/// <summary>Minecraft 协议的读写原语（VarInt / String）。</summary>
internal static class McWire
{
    public static void WriteVarInt(Stream stream, int value)
    {
        var remaining = (uint)value;
        while (true)
        {
            if ((remaining & ~0x7Fu) == 0)
            {
                stream.WriteByte((byte)remaining);
                return;
            }
            stream.WriteByte((byte)((remaining & 0x7F) | 0x80));
            remaining >>= 7;
        }
    }

    public static async Task<int> ReadVarIntAsync(Stream stream, CancellationToken ct)
    {
        var result = 0;
        var shift = 0;
        var buffer = new byte[1];
        while (true)
        {
            await ReadExactlyAsync(stream, buffer, ct);
            result |= (buffer[0] & 0x7F) << shift;
            if ((buffer[0] & 0x80) == 0) return result;
            shift += 7;
            if (shift > 35) throw new FormatException("VarInt 超过 5 字节");
        }
    }

    public static void WriteString(Stream stream, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        WriteVarInt(stream, bytes.Length);
        stream.Write(bytes);
    }

    public static async Task<string> ReadStringAsync(Stream stream, CancellationToken ct)
    {
        var length = await ReadVarIntAsync(stream, ct);
        if (length < 0 || length > 1 << 20) throw new FormatException($"字符串长度不合理：{length}");
        var buffer = new byte[length];
        await ReadExactlyAsync(stream, buffer, ct);
        return Encoding.UTF8.GetString(buffer);
    }

    public static async Task ReadExactlyAsync(Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer[read..], ct);
            if (n <= 0) throw new EndOfStreamException("连接在读完数据前被关闭");
            read += n;
        }
    }
}
