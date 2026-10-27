// STUN Binding 客户端：只用「问一句我是谁」这一件事（RFC 5389 的最小子集）。
//
// 为什么客户端自己测：星云后端在 Railway，公网入口只支持 TCP/HTTP，开不了 UDP 端口，
// 没法像常规做法那样由服务器观测客户端的 NAT 映射。所以客户端在与打洞同一个 UDP
// socket 上向公开 STUN 取 XOR-MAPPED-ADDRESS，再把结果通过 HTTPS 上报（设计文档 §2）。
//
// 只依赖公开 STUN 的 20 字节 Binding Request / 应答解析，不引第三方库。

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace Starcloud.Core.Services.P2P;

internal static class StunClient
{
    private const uint MagicCookie = 0x2112A442;
    private const ushort BindingRequest = 0x0001;
    private const ushort BindingSuccess = 0x0101;
    private const ushort AttrMappedAddress = 0x0001;
    private const ushort AttrXorMappedAddress = 0x0020;

    /// <summary>公开 STUN（只用于发现映射；将来有带 UDP 的 VPS 可换成自建，协议不变）</summary>
    public static readonly (string Host, int Port)[] Servers =
    [
        // Cloudflare 在国内可达；Google 需要加速器，放后面兜底
        ("stun.cloudflare.com", 3478),
        ("stun.l.google.com", 19302),
    ];

    /// <summary>
    /// 探测某个地址族的公网映射；失败返回 null（打洞不可用，但下载照常）。
    /// 收发都走 <see cref="UdpSocketHost"/>：它持有该 socket 唯一的收包循环，
    /// 这里绝不自己 ReceiveFrom，否则 STUN 应答会被收包循环抢走。
    /// 双栈 socket 上要分别探 v4 / v6：只探一个地址族会让对端少一半候选。
    /// </summary>
    public static async Task<IPEndPoint?> DiscoverAsync(UdpSocketHost host, string stunHost, int port,
        AddressFamily prefer, TimeSpan timeout, CancellationToken ct)
    {
        IReadOnlyList<IPAddress> targets;
        try
        {
            targets = await Dns.GetHostAddressesAsync(stunHost, ct).ConfigureAwait(false);
        }
        catch (SocketException)
        {
            return null;
        }
        var target = targets.FirstOrDefault(a => a.AddressFamily == prefer);
        if (target is null) return null;

        var transactionId = RandomNumberGenerator.GetBytes(12);
        var request = BuildBindingRequest(transactionId);
        var key = BinaryPrimitives.ReadUInt32BigEndian(transactionId);
        var response = await host.SendStunAndWaitAsync(request, key,
            new IPEndPoint(target, port), timeout, ct).ConfigureAwait(false);
        return response is not null && TryParseBindingResponse(response, transactionId, out var mapped)
            ? mapped
            : null;
    }

    internal static byte[] BuildBindingRequest(byte[] transactionId)
    {
        if (transactionId.Length != 12) throw new ArgumentException("事务 ID 必须是 12 字节", nameof(transactionId));
        var buffer = new byte[20];
        BinaryPrimitives.WriteUInt16BigEndian(buffer, BindingRequest);
        BinaryPrimitives.WriteUInt16BigEndian(buffer.AsSpan(2), 0);
        BinaryPrimitives.WriteUInt32BigEndian(buffer.AsSpan(4), MagicCookie);
        transactionId.CopyTo(buffer.AsSpan(8));
        return buffer;
    }

    /// <summary>解析 Binding Success Response；优先 XOR-MAPPED-ADDRESS，退回 MAPPED-ADDRESS。</summary>
    internal static bool TryParseBindingResponse(ReadOnlySpan<byte> data, ReadOnlySpan<byte> transactionId,
        out IPEndPoint? mapped)
    {
        mapped = null;
        if (data.Length < 20) return false;
        if (BinaryPrimitives.ReadUInt16BigEndian(data) != BindingSuccess) return false;
        var length = BinaryPrimitives.ReadUInt16BigEndian(data[2..]);
        if (data.Length < 20 + length) return false;
        if (BinaryPrimitives.ReadUInt32BigEndian(data[4..]) != MagicCookie) return false;
        if (!data.Slice(8, 12).SequenceEqual(transactionId)) return false;

        IPEndPoint? plain = null;
        var pos = 20;
        var end = 20 + length;
        while (pos + 4 <= end)
        {
            var type = BinaryPrimitives.ReadUInt16BigEndian(data[pos..]);
            var attrLength = BinaryPrimitives.ReadUInt16BigEndian(data[(pos + 2)..]);
            var valueStart = pos + 4;
            if (valueStart + attrLength > end) return false;
            var value = data.Slice(valueStart, attrLength);
            if (type == AttrXorMappedAddress && TryParseAddress(value, transactionId, xor: true, out var xor))
            {
                mapped = xor;
                return true;
            }
            if (type == AttrMappedAddress && TryParseAddress(value, transactionId, xor: false, out var raw))
                plain = raw;
            pos = valueStart + ((attrLength + 3) & ~3);
        }

        mapped = plain;
        return mapped is not null;
    }

    private static bool TryParseAddress(ReadOnlySpan<byte> value, ReadOnlySpan<byte> transactionId,
        bool xor, out IPEndPoint? endpoint)
    {
        endpoint = null;
        if (value.Length < 8) return false;
        var family = value[1];
        var port = BinaryPrimitives.ReadUInt16BigEndian(value[2..]);
        if (xor) port ^= 0x2112;

        if (family == 0x01)
        {
            if (value.Length < 8) return false;
            Span<byte> address = stackalloc byte[4];
            value.Slice(4, 4).CopyTo(address);
            if (xor)
            {
                Span<byte> mask = stackalloc byte[4];
                BinaryPrimitives.WriteUInt32BigEndian(mask, MagicCookie);
                for (var i = 0; i < 4; i++) address[i] ^= mask[i];
            }
            endpoint = new IPEndPoint(new IPAddress(address), port);
            return true;
        }

        if (family == 0x02)
        {
            if (value.Length < 20) return false;
            Span<byte> address = stackalloc byte[16];
            value.Slice(4, 16).CopyTo(address);
            if (xor)
            {
                Span<byte> mask = stackalloc byte[16];
                BinaryPrimitives.WriteUInt32BigEndian(mask, MagicCookie);
                transactionId.CopyTo(mask[4..]);
                for (var i = 0; i < 16; i++) address[i] ^= mask[i];
            }
            endpoint = new IPEndPoint(new IPAddress(address), port);
            return true;
        }

        return false;
    }
}
