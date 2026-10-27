// 跨互联网 P2P 的 UDP 帧格式与会话密钥（设计：2026-10-22-cross-internet-p2p-rendezvous.md §7）。
//
// 定长 20 字节头 + ≤1100 字节载荷，整体低于 IPv6 最小 MTU（1280），不触发 IP 分片。
// 会话密钥不来自 apiKey（跨账号双方不共享），而是服务器经两条已鉴权通道各发一份的随机
// token：HKDF 派生数据面密钥与 4 字节会话标签。离线单测覆盖编解码与 MAC。

using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace Starcloud.Core.Services.P2P;

internal enum P2PFrameType : byte
{
    /// <summary>打洞探测（等长 32 字节，无放大比）</summary>
    Punch = 1,

    /// <summary>打洞应答（回显 nonce）</summary>
    PunchAck = 2,

    /// <summary>握手请求（sha1 + size + offset + nonce + MAC）</summary>
    Hello = 3,

    /// <summary>握手应答（nonce + MAC）</summary>
    HelloAck = 4,

    /// <summary>文件块</summary>
    Data = 5,

    /// <summary>累计确认（ack_seq = 下一个期望块号，ack_bits 为其后 64 块位图）</summary>
    Ack = 6,

    /// <summary>发送完毕</summary>
    Fin = 7,

    /// <summary>中止（载荷首字节为原因码）</summary>
    Abort = 8,
}

internal readonly struct P2PFrame(P2PFrameType type, uint sessionTag, uint seq = 0,
    uint ackSeq = 0, ulong ackBits = 0, ReadOnlyMemory<byte> payload = default)
{
    public const int HeaderSize = 24;

    /// <summary>单块载荷：24 字节头 + 1100 = 1124，低于 1280</summary>
    public const int MaxPayload = 1100;

    /// <summary>小端读出来是 'S' 'C'</summary>
    public const ushort Magic = 0x5343;

    public const byte Version = 1;

    public P2PFrameType Type { get; } = type;
    public uint SessionTag { get; } = sessionTag;
    public uint Seq { get; } = seq;
    public uint AckSeq { get; } = ackSeq;
    public ulong AckBits { get; } = ackBits;
    public ReadOnlyMemory<byte> Payload { get; } = payload;

    public byte[] Encode()
    {
        var buffer = new byte[HeaderSize + Payload.Length];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt16LittleEndian(span, Magic);
        span[2] = Version;
        span[3] = (byte)Type;
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], SessionTag);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], Seq);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], AckSeq);
        BinaryPrimitives.WriteUInt64LittleEndian(span[16..], AckBits);
        Payload.Span.CopyTo(span[HeaderSize..]);
        return buffer;
    }

    /// <summary>解析一个数据报；载荷做一次拷贝（接收缓冲会复用）。</summary>
    public static bool TryParse(ReadOnlySpan<byte> buffer, out P2PFrame frame)
    {
        frame = default;
        if (buffer.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(buffer) != Magic) return false;
        if (buffer[2] != Version) return false;
        var type = (P2PFrameType)buffer[3];
        if (type is < P2PFrameType.Punch or > P2PFrameType.Abort) return false;
        var payloadLength = buffer.Length - HeaderSize;
        if (payloadLength > MaxPayload) return false;
        frame = new P2PFrame(
            type,
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[4..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[8..]),
            BinaryPrimitives.ReadUInt32LittleEndian(buffer[12..]),
            BinaryPrimitives.ReadUInt64LittleEndian(buffer[16..]),
            payloadLength == 0 ? ReadOnlyMemory<byte>.Empty : buffer[HeaderSize..].ToArray());
        return true;
    }
}

/// <summary>
/// 会话密钥：token → 32 字节 HMAC 密钥 + 4 字节会话标签。
/// token 由服务器分别投递给双方，两端各派生一次即可互相验证，不经过数据面。
/// </summary>
internal sealed class P2PSessionKey
{
    private const string Label = "sc-p2p-v1";

    private readonly byte[] _key;

    public P2PSessionKey(byte[] token)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Length < 16) throw new ArgumentException("会话令牌太短", nameof(token));
        using var hkdf = new HMACSHA256(token);
        _key = hkdf.ComputeHash(Encoding.ASCII.GetBytes(Label + ":key"));
        var tag = hkdf.ComputeHash(Encoding.ASCII.GetBytes(Label + ":tag"));
        Tag = BinaryPrimitives.ReadUInt32LittleEndian(tag);
    }

    /// <summary>4 字节会话标签：接收端据此把数据报分给对应会话</summary>
    public uint Tag { get; }

    public byte[] Mac(ReadOnlySpan<byte> data)
    {
        using var hmac = new HMACSHA256(_key);
        return hmac.ComputeHash(data.ToArray())[..16];
    }

    public bool Verify(ReadOnlySpan<byte> data, ReadOnlySpan<byte> mac)
    {
        if (mac.Length != 16) return false;
        var expected = Mac(data);
        return CryptographicOperations.FixedTimeEquals(expected, mac);
    }
}

/// <summary>握手载荷的构造与校验（字段布局固定，跨语言实现时照抄 §7）</summary>
internal static class P2PHandshake
{
    public const int HelloLength = 20 + 8 + 8 + 8 + 16;   // sha1 + size + offset + nonce + MAC
    public const int HelloAckLength = 8 + 16;             // nonce + MAC
    public const int PunchLength = 8;                     // nonce

    public static byte[] BuildHello(string sha1, long size, long offset, ulong nonce, P2PSessionKey key)
    {
        var buffer = new byte[HelloLength];
        var span = buffer.AsSpan();
        Convert.FromHexString(sha1).CopyTo(span);
        BinaryPrimitives.WriteInt64LittleEndian(span[20..], size);
        BinaryPrimitives.WriteInt64LittleEndian(span[28..], offset);
        BinaryPrimitives.WriteUInt64LittleEndian(span[36..], nonce);
        key.Mac(span[..44]).CopyTo(span[44..]);
        return buffer;
    }

    public static bool TryParseHello(ReadOnlySpan<byte> payload, P2PSessionKey key,
        out string sha1, out long size, out long offset, out ulong nonce)
    {
        sha1 = "";
        size = 0;
        offset = 0;
        nonce = 0;
        if (payload.Length != HelloLength) return false;
        if (!key.Verify(payload[..44], payload[44..])) return false;
        sha1 = Convert.ToHexString(payload[..20]).ToLowerInvariant();
        size = BinaryPrimitives.ReadInt64LittleEndian(payload[20..]);
        offset = BinaryPrimitives.ReadInt64LittleEndian(payload[28..]);
        nonce = BinaryPrimitives.ReadUInt64LittleEndian(payload[36..]);
        return size >= 0 && offset >= 0 && offset <= size;
    }

    public static byte[] BuildHelloAck(ulong nonce, P2PSessionKey key)
    {
        var buffer = new byte[HelloAckLength];
        var span = buffer.AsSpan();
        BinaryPrimitives.WriteUInt64LittleEndian(span, nonce);
        key.Mac(span[..8]).CopyTo(span[8..]);
        return buffer;
    }

    public static bool VerifyHelloAck(ReadOnlySpan<byte> payload, ulong nonce, P2PSessionKey key)
    {
        if (payload.Length != HelloAckLength) return false;
        if (BinaryPrimitives.ReadUInt64LittleEndian(payload) != nonce) return false;
        return key.Verify(payload[..8], payload[8..]);
    }

    public static byte[] BuildPunch(ulong nonce)
    {
        var buffer = new byte[PunchLength];
        BinaryPrimitives.WriteUInt64LittleEndian(buffer, nonce);
        return buffer;
    }
}
