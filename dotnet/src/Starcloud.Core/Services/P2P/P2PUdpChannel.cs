// 数据面：UDP socket 宿主、打洞、以及打洞成功后的可靠分块传输（设计文档 §6/§7）。
//
// 不复用局域网那套 TCP 链路：TCP 穿不过 NAT，跨互联网只能靠 UDP 同时打洞。
// 传输层是极简的选择重传：20 字节头 + 1100 字节块、窗口 64、累计 ACK + 32 位位图，
// 整文件 SHA1 仍由调用方校验——P2P 只是加速，任何异常都必须能干净地回退公网。

using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Threading.Channels;
using Microsoft.Win32.SafeHandles;

namespace Starcloud.Core.Services.P2P;

internal interface IP2PUdpListener
{
    void OnFrame(P2PFrame frame, IPEndPoint from);
}

/// <summary>一个绑定了临时端口的 UDP socket：常驻收包循环，按会话标签分发给监听者。</summary>
internal sealed class UdpSocketHost : IDisposable
{
    private readonly ConcurrentDictionaryShim _listeners = new();
    private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, TaskCompletionSource<byte[]>>
        _stunWaiters = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public UdpSocketHost()
    {
        Socket = CreateSocket();
        Socket.ReceiveBufferSize = 1 << 20;
        Socket.SendBufferSize = 1 << 20;
        Socket.Bind(new IPEndPoint(
            Socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0));
    }

    public Socket Socket { get; }

    /// <summary>STUN 探到的公网映射（v4 / v6 各一条；空 = 该 socket 不可被发现）</summary>
    public List<IPEndPoint> Endpoints { get; } = [];

    public int Port => ((IPEndPoint)Socket.LocalEndPoint!).Port;

    public void Start() => _loop ??= Task.Run(ReceiveLoopAsync);

    public void Add(uint tag, IP2PUdpListener listener) => _listeners.Add(tag, listener);

    /// <summary>只在监听者仍是自己时才移除，避免把后来注册的同标签会话误删。</summary>
    public void Remove(uint tag, IP2PUdpListener listener) => _listeners.Remove(tag, listener);

    public ValueTask<int> SendAsync(ReadOnlyMemory<byte> data, IPEndPoint to, CancellationToken ct)
    {
        // 双栈 socket 上不能直接发 IPv4 端点（会报地址族不符）；映射成 ::ffff: 再发
        var target = Socket.AddressFamily == AddressFamily.InterNetworkV6 &&
                     to.AddressFamily == AddressFamily.InterNetwork
            ? new IPEndPoint(to.Address.MapToIPv6(), to.Port)
            : to;
        return Socket.SendToAsync(data, SocketFlags.None, target, ct);
    }

    private async Task ReceiveLoopAsync()
    {
        var buffer = new byte[P2PFrame.HeaderSize + P2PFrame.MaxPayload];
        var from = new IPEndPoint(
            Socket.AddressFamily == AddressFamily.InterNetworkV6 ? IPAddress.IPv6Any : IPAddress.Any, 0);
        while (!_cts.IsCancellationRequested)
        {
            SocketReceiveFromResult result;
            try
            {
                result = await Socket.ReceiveFromAsync(buffer, SocketFlags.None, from, _cts.Token)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (SocketException)
            {
                // ICMP 端口不可达 / 瞬时错误：继续收，别把整个宿主拖死
                continue;
            }

            var datagram = new ReadOnlyMemory<byte>(buffer, 0, result.ReceivedBytes);
            if (!P2PFrame.TryParse(datagram.Span, out var frame))
            {
                // 不是 P2P 帧：可能是等着的 STUN 应答，交给等待者（收包只能有一个读者，
                // 不能像原来那样让 STUN 自己和收包循环抢同一 socket 的数据报）
                CompleteStunWaiter(datagram.Span);
                continue;
            }
            if (_listeners.TryGet(frame.SessionTag, out var listener) && result.RemoteEndPoint is IPEndPoint remote)
                listener.OnFrame(frame, Normalize(remote));
        }
    }

    /// <summary>
    /// 在同一个 socket 上发一条 STUN 请求并等应答：应答由常驻收包循环按事务 ID 分发过来。
    /// 超时返回 null，不抛异常（打洞不可用只是少一个加速通道）。
    /// </summary>
    public async Task<byte[]?> SendStunAndWaitAsync(ReadOnlyMemory<byte> request, uint transactionKey,
        IPEndPoint target, TimeSpan timeout, CancellationToken ct)
    {
        var waiter = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _stunWaiters[transactionKey] = waiter;
        try
        {
            await SendAsync(request, target, ct).ConfigureAwait(false);
            return await waiter.Task.WaitAsync(timeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            _stunWaiters.TryRemove(transactionKey, out _);
        }
    }

    /// <summary>STUN 头：type(2) length(2) magic(4) txid(12)；用 txid 前 4 字节做键。</summary>
    private void CompleteStunWaiter(ReadOnlySpan<byte> datagram)
    {
        if (datagram.Length < 20) return;
        if (BinaryPrimitives.ReadUInt32BigEndian(datagram[4..]) != 0x2112A442) return;
        var key = BinaryPrimitives.ReadUInt32BigEndian(datagram[8..]);
        if (_stunWaiters.TryRemove(key, out var waiter))
            waiter.TrySetResult(datagram.ToArray());
    }

    /// <summary>双栈 socket 上 IPv4 会以 ::ffff: 形式出现；统一成 IPv4 便于比较与上报。</summary>
    internal static IPEndPoint Normalize(IPEndPoint endpoint) =>
        endpoint.Address.IsIPv4MappedToIPv6
            ? new IPEndPoint(endpoint.Address.MapToIPv4(), endpoint.Port)
            : endpoint;

    private static Socket CreateSocket()
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetworkV6, SocketType.Dgram, ProtocolType.Udp)
            {
                DualMode = true,
            };
            return socket;
        }
        catch (SocketException)
        {
            return new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        }
    }

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { }
        try { Socket.Dispose(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(1)); } catch { }
        _cts.Dispose();
    }

    /// <summary>ConcurrentDictionary 的「按值移除」封装（TryRemove 只按 key）。</summary>
    private sealed class ConcurrentDictionaryShim
    {
        private readonly System.Collections.Concurrent.ConcurrentDictionary<uint, IP2PUdpListener> _map = new();

        public void Add(uint tag, IP2PUdpListener listener) => _map[tag] = listener;

        public bool TryGet(uint tag, out IP2PUdpListener listener) => _map.TryGetValue(tag, out listener!);

        public void Remove(uint tag, IP2PUdpListener listener) =>
            ((ICollection<KeyValuePair<uint, IP2PUdpListener>>)_map)
            .Remove(new KeyValuePair<uint, IP2PUdpListener>(tag, listener));
    }
}

/// <summary>令牌桶限速（供文件时用；默认 4 MB/s，设置页可调）。</summary>
internal sealed class P2PRateLimiter(double bytesPerSecond)
{
    private const double BurstSeconds = 0.5;

    private readonly double _rate = Math.Max(64 * 1024, bytesPerSecond);
    private double _credit = Math.Max(64 * 1024, bytesPerSecond) * BurstSeconds;
    private long _lastAt = Environment.TickCount64;

    public async Task AcquireAsync(int bytes, CancellationToken ct)
    {
        while (true)
        {
            double waitMs;
            lock (this)
            {
                var now = Environment.TickCount64;
                _credit = Math.Min(_rate * BurstSeconds, _credit + (now - _lastAt) / 1000.0 * _rate);
                _lastAt = now;
                if (_credit >= bytes)
                {
                    _credit -= bytes;
                    return;
                }
                waitMs = (bytes - _credit) / _rate * 1000.0;
            }
            await Task.Delay(TimeSpan.FromMilliseconds(Math.Clamp(waitMs, 1, 200)), ct)
                .ConfigureAwait(false);
        }
    }
}

/// <summary>
/// 打洞：从本机所有 socket 向对端所有候选地址重复发 PUNCH，收到对端 PUNCH/PUNCH_ACK 即打通。
/// 成功返回「哪条 socket 对哪条地址」，后续传输固定走这一对。
/// </summary>
internal static class P2PHolePunch
{
    public static async Task<(UdpSocketHost Host, IPEndPoint Remote)?> RunAsync(
        IReadOnlyList<UdpSocketHost> hosts, IReadOnlyList<IPEndPoint> candidates,
        uint tag, TimeSpan budget, CancellationToken ct)
    {
        if (hosts.Count == 0 || candidates.Count == 0) return null;
        var completion = new TaskCompletionSource<(UdpSocketHost, IPEndPoint)>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var listeners = new List<(UdpSocketHost Host, PunchListener Listener)>();
        foreach (var host in hosts)
        {
            var listener = new PunchListener(host, completion);
            host.Add(tag, listener);
            listeners.Add((host, listener));
        }

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stop.CancelAfter(budget);
        var senders = (from host in hosts
                       from candidate in candidates
                       select Task.Run(() => PunchLoopAsync(host, candidate, tag, stop.Token)))
            .ToArray();

        try
        {
            var winner = await completion.Task.WaitAsync(budget, ct).ConfigureAwait(false);
            return winner;
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            try { stop.Cancel(); } catch { }
            foreach (var (host, listener) in listeners) host.Remove(tag, listener);
            try { await Task.WhenAll(senders).WaitAsync(TimeSpan.FromSeconds(1), CancellationToken.None); }
            catch { }
        }
    }

    private static async Task PunchLoopAsync(UdpSocketHost host, IPEndPoint candidate, uint tag,
        CancellationToken ct)
    {
        var payload = P2PHandshake.BuildPunch(0);
        var frame = new P2PFrame(P2PFrameType.Punch, tag, payload: payload).Encode();
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await host.SendAsync(frame, candidate, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
                // 单个候选不可达：继续按节拍重试
            }
            try
            {
                await Task.Delay(300, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    /// <summary>
    /// 只按会话标签匹配：标签由双方 token 派生，是打洞期间唯一的准入凭据。
    /// 双方各自生成的 nonce 不能用来互相校验（否则永远不匹配）。
    /// </summary>
    private sealed class PunchListener(
        UdpSocketHost host, TaskCompletionSource<(UdpSocketHost, IPEndPoint)> completion)
        : IP2PUdpListener
    {
        public void OnFrame(P2PFrame frame, IPEndPoint from)
        {
            switch (frame.Type)
            {
                case P2PFrameType.Punch:
                    // 回显载荷，让对端确认「你打过来的这条路径我也能收到」
                    _ = SendQuietlyAsync(new P2PFrame(P2PFrameType.PunchAck, frame.SessionTag,
                        payload: frame.Payload).Encode(), from);
                    completion.TrySetResult((host, from));
                    break;
                case P2PFrameType.PunchAck:
                    completion.TrySetResult((host, from));
                    break;
            }
        }

        private async Task SendQuietlyAsync(byte[] data, IPEndPoint to)
        {
            try { await host.SendAsync(data, to, CancellationToken.None).ConfigureAwait(false); }
            catch { }
        }
    }
}

/// <summary>打通后的一条会话：收发帧、可靠传文件。</summary>
internal sealed class P2PSession : IDisposable
{
    private const int Window = 64;
    private const int RetransmitMs = 400;
    private const int IdleTimeoutMs = 20000;

    private readonly List<(UdpSocketHost Host, IP2PUdpListener Listener)> _registrations = [];
    private readonly P2PSessionKey _key;
    // 有界：对端异常刷包时丢弃最旧的帧（传输层本来就会重传），不让内存无限涨
    private readonly Channel<P2PFrame> _inbox = Channel.CreateBounded<P2PFrame>(
        new BoundedChannelOptions(4096)
        {
            SingleReader = true,
            SingleWriter = true,
            FullMode = BoundedChannelFullMode.DropOldest,
        });
    private volatile UdpSocketHost _sendHost;
    private volatile IPEndPoint _remote;

    /// <summary>
    /// 在所有本机 socket 上注册同一会话：两端打洞时各自可能选中不同的 socket 对，
    /// 任何一条路径上到达的帧都要能进会话，回包再从同一条路径发出去。
    /// </summary>
    public P2PSession(IReadOnlyList<UdpSocketHost> hosts, UdpSocketHost primary,
        IPEndPoint remote, P2PSessionKey key)
    {
        _sendHost = primary;
        _remote = remote;
        _key = key;
        foreach (var host in hosts)
        {
            var listener = new HostListener(this, host);
            host.Add(key.Tag, listener);
            _registrations.Add((host, listener));
        }
    }

    public uint Tag => _key.Tag;

    internal void OnFrame(UdpSocketHost host, P2PFrame frame, IPEndPoint from)
    {
        // 以最近一次来源为准：同一会话可能从对端的另一个 socket 到达，回包跟着换路径
        _sendHost = host;
        _remote = from;
        _inbox.Writer.TryWrite(frame);
    }

    public ValueTask<int> SendAsync(P2PFrameType type, uint seq = 0, uint ackSeq = 0,
        uint ackBits = 0, ReadOnlyMemory<byte> payload = default) =>
        _sendHost.SendAsync(new P2PFrame(type, _key.Tag, seq, ackSeq, ackBits, payload).Encode(),
            _remote, CancellationToken.None);

    /// <summary>等一帧；超时返回 null（会话本身没坏）。</summary>
    public async Task<P2PFrame?> ReadAsync(TimeSpan timeout, CancellationToken ct)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            return await _inbox.Reader.ReadAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return null;
        }
    }

    // ── 发送端 ──

    public async Task SendFileAsync(SafeFileHandle file, long size, long offset,
        Action<long>? onBytes, P2PRateLimiter? limiter, CancellationToken ct)
    {
        var count = (uint)((size - offset + P2PFrame.MaxPayload - 1) / P2PFrame.MaxPayload);
        var outstanding = new Dictionary<uint, (byte[] Data, long SentAt)>();
        uint baseSeq = 0, next = 0;
        long lastAdvance = Environment.TickCount64;
        long lastResend = 0;

        while (baseSeq < count)
        {
            while (next < count && next - baseSeq < Window)
            {
                var chunkOffset = offset + (long)next * P2PFrame.MaxPayload;
                var length = (int)Math.Min(P2PFrame.MaxPayload, size - chunkOffset);
                var buffer = new byte[length];
                await RandomAccess.ReadAsync(file, buffer, chunkOffset, ct).ConfigureAwait(false);
                await SendAsync(P2PFrameType.Data, next, payload: buffer).ConfigureAwait(false);
                outstanding[next] = (buffer, Environment.TickCount64);
                next++;
                if (limiter is not null) await limiter.AcquireAsync(length, ct).ConfigureAwait(false);
            }

            var frame = await ReadAsync(TimeSpan.FromMilliseconds(50), ct).ConfigureAwait(false);
            if (frame is { Type: P2PFrameType.Ack } ack)
            {
                if (ack.AckSeq > baseSeq)
                {
                    baseSeq = ack.AckSeq;
                    lastAdvance = Environment.TickCount64;
                }
                foreach (var seq in outstanding.Keys.Where(s => s < baseSeq).ToList())
                    outstanding.Remove(seq);
                for (var bit = 0; bit < 32; bit++)
                    if ((ack.AckBits & (1u << bit)) != 0)
                        outstanding.Remove(baseSeq + (uint)bit);
            }
            else if (frame is { Type: P2PFrameType.Abort })
            {
                throw new IOException("对端中止了传输");
            }

            var now = Environment.TickCount64;
            if (now - lastResend >= 100)
            {
                lastResend = now;
                foreach (var seq in outstanding.Where(e => now - e.Value.SentAt > RetransmitMs).Select(e => e.Key).ToList())
                {
                    if (!outstanding.TryGetValue(seq, out var chunk)) continue;
                    await SendAsync(P2PFrameType.Data, seq, payload: chunk.Data).ConfigureAwait(false);
                    outstanding[seq] = (chunk.Data, now);
                }
            }

            if (now - lastAdvance > IdleTimeoutMs)
                throw new TimeoutException("P2P 传输卡住：对端长时间没有确认");
        }

        await SendAsync(P2PFrameType.Fin).ConfigureAwait(false);
        onBytes?.Invoke(size - offset);
    }

    // ── 接收端 ──

    public async Task ReceiveFileAsync(SafeFileHandle file, long size, long offset,
        Action<long>? onBytes, CancellationToken ct)
    {
        var count = (uint)((size - offset + P2PFrame.MaxPayload - 1) / P2PFrame.MaxPayload);
        var received = new bool[count];
        uint expected = 0;
        var sinceAck = 0;
        long lastData = Environment.TickCount64;

        while (expected < count)
        {
            var frame = await ReadAsync(TimeSpan.FromMilliseconds(200), ct).ConfigureAwait(false);
            if (frame is null)
            {
                if (Environment.TickCount64 - lastData > IdleTimeoutMs)
                    throw new TimeoutException("P2P 接收超时");
                continue;
            }

            if (frame.Value.Type == P2PFrameType.Abort)
                throw new IOException("对端中止了传输");
            if (frame.Value.Type != P2PFrameType.Data) continue;

            var seq = frame.Value.Seq;
            if (seq < count && !received[seq])
            {
                received[seq] = true;
                var position = offset + (long)seq * P2PFrame.MaxPayload;
                await RandomAccess.WriteAsync(file, frame.Value.Payload, position, ct).ConfigureAwait(false);
                lastData = Environment.TickCount64;
                onBytes?.Invoke(frame.Value.Payload.Length);
                sinceAck++;
            }

            while (expected < count && received[expected]) expected++;

            if (sinceAck >= 8)
            {
                sinceAck = 0;
                await SendAsync(P2PFrameType.Ack, ackSeq: expected, ackBits: Bits(received, expected))
                    .ConfigureAwait(false);
            }
        }

        await SendAsync(P2PFrameType.Ack, ackSeq: expected, ackBits: Bits(received, expected))
            .ConfigureAwait(false);
    }

    /// <summary>ack_seq 之后 32 块的接收位图</summary>
    private static uint Bits(bool[] received, uint expected)
    {
        var bits = 0u;
        for (var i = 0; i < 32; i++)
        {
            var index = expected + (uint)i;
            if (index < received.Length && received[index]) bits |= 1u << i;
        }
        return bits;
    }

    public ValueTask<int> AbortAsync(byte reason)
    {
        try
        {
            return SendAsync(P2PFrameType.Abort, payload: new[] { reason });
        }
        catch
        {
            return default;
        }
    }

    public void Dispose()
    {
        foreach (var (host, listener) in _registrations) host.Remove(_key.Tag, listener);
        _registrations.Clear();
    }

    /// <summary>把「哪个 socket 收到的」带回会话（host 只存在于监听器里）。</summary>
    private sealed class HostListener(P2PSession session, UdpSocketHost host) : IP2PUdpListener
    {
        public void OnFrame(P2PFrame frame, IPEndPoint from) => session.OnFrame(host, frame, from);
    }
}
