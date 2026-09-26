// 空闲看门狗：一条下载流一个实例，读成功后续期；连续无数据超过阈值即取消这次读。
//
// 为什么不用「每次读都新建 CancellationTokenSource + CancelAfter」：
// 那是每次 256 KiB 一次 CTS 分配 + 一次定时器入队，100 MB/s 下每秒数百个定时器，
// 在满速下载时反而成了主要开销。这里只在距上次续期 ≥1 秒时才真正重排定时器，
// 语义不变（连续无数据仍会判死），开销降到每秒 ≤1 次。

namespace Starcloud.Core.Services;

internal sealed class IdleTimeout : IDisposable
{
    private readonly CancellationTokenSource _cts;
    private readonly int _limitMs;
    private readonly int _rearmEveryMs;
    private long _armedAt;

    public IdleTimeout(CancellationToken ct, int limitMs, int rearmEveryMs = 1000)
    {
        _cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _limitMs = limitMs;
        _rearmEveryMs = rearmEveryMs;
        _armedAt = Environment.TickCount64;
        _cts.CancelAfter(limitMs);
    }

    /// <summary>这次读使用的令牌：空闲超时或调用方取消都会触发</summary>
    public CancellationToken Token => _cts.Token;

    /// <summary>读到数据后调用（距上次续期不足 1 秒时零开销）</summary>
    public void Touch()
    {
        var now = Environment.TickCount64;
        if (now - Interlocked.Read(ref _armedAt) < _rearmEveryMs) return;
        Interlocked.Exchange(ref _armedAt, now);
        try
        {
            _cts.CancelAfter(_limitMs);
        }
        catch (ObjectDisposedException)
        {
            // 正好在收尾时调用：忽略
        }
    }

    public void Dispose() => _cts.Dispose();
}
