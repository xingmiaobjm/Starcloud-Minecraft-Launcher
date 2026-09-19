// 硬件录屏：Windows.Graphics.Capture 取帧 + Media Foundation 硬件 H.264 编码。
//
// 为什么这么选（而不是 GDI 抓屏）：
//   WGC 的帧是 DWM 直接共享给本进程的 D3D11 纹理——画面从未离开显卡缓冲区，
//   没有 BitBlt/PrintWindow 那种"把屏幕拷回 CPU"的路径，也不受窗口遮挡影响。
//   编码侧用 IMFSinkWriter + MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS：
//   驱动注册的 H.264 MFT（NVENC / Quick Sync / AMF）会被优先选中，拿不到时 MF 自动退回软编。
//
// 线程模型：FrameArrived 只做"把最新一帧取到 CPU 缓冲"，写入固定帧率的编码线程负责
//   按 1/fps 节拍交样——WGC 是按画面变化推帧的，静止画面收不到回调，交给编码线程补重复帧
//   才能保证时长与时间轴正确。

using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas;
using Starcloud.App.Services.Recording;
using Starcloud.Core.Services;
using WinRT;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;

namespace Starcloud.App.Services;

/// <summary>一次录屏的请求参数</summary>
public sealed record RecordingRequest(
    string OutputPath,
    int Fps = 30,
    uint BitrateBps = 12_000_000);

/// <summary>录屏运行状态（给 UI 用的快照）</summary>
public sealed record RecordingStatus(
    bool Active,
    string Target,
    string Encoder,
    string OutputPath,
    int Width,
    int Height,
    int Fps,
    long Frames,
    string Error)
{
    public string Describe() => Active
        ? $"正在录屏：{Target} · {Width}×{Height} @{Fps}fps · {Encoder} · 已写 {Frames} 帧\n{OutputPath}"
          + (Error.Length > 0 ? $"\n注意：{Error}" : "")
        : Encoder.Length > 0 ? $"已停止：{OutputPath}\n编码器：{Encoder}" : "";
}

/// <summary>
/// 硬件录屏器。跨线程使用的只有 <see cref="StopAsync"/> 与帧回调，内部以锁串行化。
/// </summary>
public sealed class ScreenRecorder : IDisposable
{
    /// <summary>MFT_CATEGORY_VIDEO_ENCODER</summary>
    private static readonly Guid VideoEncoderCategory = new("f79eac7d-e545-4387-bdee-d647d7bde42a");

    /// <summary>本机注册的硬件视频编码器（NVENC / Quick Sync / AMF）；空 = 只能软编</summary>
    public static IReadOnlyList<string> HardwareEncoders { get; private set; } = [];

    private static bool _mfStarted;
    private static readonly object MfGate = new();

    private readonly object _gate = new();

    private GraphicsCaptureItem? _item;
    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private CanvasDevice? _device;
    private IMFSinkWriter? _writer;

    private byte[]? _latest;           // 最新一帧的 BGRA 像素（FrameArrived 写、编码线程读）
    private byte[]? _spare;            // 复用缓冲，避免每帧分配
    private bool _hasFrame;
    private int _width;
    private int _height;
    private int _stride;
    private uint _streamIndex;
    private long _frameDurationHns;
    private long _framesWritten;
    private Task? _pump;
    private CancellationTokenSource? _cts;
    private string _encoderName = "";
    private string _target = "";
    private string _outputPath = "";
    private string _lastError = "";
    private bool _frameErrorLogged;

    /// <summary>是否支持 WGC（Windows 10 1903+ 恒为 true；失败一律降级为"不支持"而不是崩）</summary>
    public static bool IsSupported()
    {
        try { return GraphicsCaptureSession.IsSupported(); }
        catch { return false; }
    }

    /// <summary>探测本机硬件编码器（幂等，供 UI 显示"这台机器会用什么编码"）</summary>
    public static IReadOnlyList<string> ProbeHardwareEncoders()
    {
        EnsureMfStarted();
        var names = MediaFoundation.EnumerateHardwareTransforms(VideoEncoderCategory)
            .Select(m => m.Name).ToList();
        HardwareEncoders = names;
        return names;
    }

    private static void EnsureMfStarted()
    {
        lock (MfGate)
        {
            if (_mfStarted) return;
            var hr = MediaFoundation.MFStartup(MfGuids.MF_VERSION, MfGuids.MFSTARTUP_FULL);
            if (hr < 0) throw new InvalidOperationException($"MFStartup 失败：0x{hr:X8}");
            _mfStarted = true;
        }
    }

    /// <summary>开始录制。目标为 null 时录主显示器（游戏窗口由调用方给 hwnd）。</summary>
    public void Start(RecordingRequest request, IntPtr targetWindow, string targetName)
    {
        lock (_gate)
        {
            if (_session is not null) throw new InvalidOperationException("已经在录屏中");

            _item = targetWindow != IntPtr.Zero
                ? CaptureItemInterop.CreateForWindow(targetWindow)
                : CaptureItemInterop.CreateForMonitor(CaptureItemInterop.PrimaryMonitor());

            var size = _item.Size;
            // 宽高都取偶数：H.264 的 4:2:0 色度是 2×2 下采样，奇数边会被编码器拒绝
            _width = Math.Max(2, size.Width & ~1);
            _height = Math.Max(2, size.Height & ~1);
            _stride = _width * 4;
            _frameDurationHns = 10_000_000L / Math.Clamp(request.Fps, 1, 60);
            _outputPath = request.OutputPath;
            _target = targetName;
            _framesWritten = 0;
            _hasFrame = false;

            EnsureMfStarted();
            _device = new CanvasDevice(forceSoftwareRenderer: false);
            _writer = CreateWriter(request, _width, _height);
            _encoderName = HardwareEncoders.Count > 0
                ? $"硬件编码（{string.Join(" / ", HardwareEncoders)}）"
                : "软件编码（未检测到硬件编码器）";

            _pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                _device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2,
                new Windows.Graphics.SizeInt32 { Width = _width, Height = _height });
            _session = _pool.CreateCaptureSession(_item);
            try { _session.IsCursorCaptureEnabled = true; } catch { /* 老版本系统不支持 */ }
            // IsBorderRequired（关掉系统黄框）是 21H2+ 的属性，本项目的 SDK 投影（19041）里没有，
            // 就不为了它抬 TargetPlatformVersion——黄框是系统的"正在被捕获"提示，留着也不算坏。

            _cts = new CancellationTokenSource();
            _pump = Task.Run(() => PumpAsync(_cts.Token));
            _session.StartCapture();
        }
    }

    /// <summary>停止并封装文件（写完 moov 才算可播放）</summary>
    public async Task<string> StopAsync()
    {
        Task? pump;
        lock (_gate)
        {
            if (_session is null) return _outputPath;
            pump = _pump;
            _cts?.Cancel();
        }

        if (pump is not null)
        {
            try { await pump.ConfigureAwait(false); } catch { /* 收尾异常不影响落盘 */ }
        }

        lock (_gate)
        {
            try { _session?.Dispose(); } catch { }
            try { _pool?.Dispose(); } catch { }
            try { _writer?.FinishWriting(); } catch { }
            try { _device?.Dispose(); } catch { }
            _session = null;
            _pool = null;
            _writer = null;
            _device = null;
            _item = null;
            _latest = null;
            _spare = null;
            return _outputPath;
        }
    }

    public RecordingStatus Snapshot()
    {
        lock (_gate)
        {
            return new RecordingStatus(_session is not null, _target, _encoderName, _outputPath,
                _width, _height, (int)(10_000_000L / Math.Max(1, _frameDurationHns)),
                Interlocked.Read(ref _framesWritten), _lastError);
        }
    }

    // ── 编码线程：按固定节拍取帧 + 交样，画面没变化时补重复帧 ──
    //
    // 这里不用 FrameArrived：WGC 是按画面变化推帧的，静止桌面收不到回调，
    // 靠事件驱动会让视频时长停住。改成按 1/fps 主动 TryGetNextFrame，
    // 拿不到新帧就重复上一帧，时间轴与真实时长始终一致。
    private async Task PumpAsync(CancellationToken ct)
    {
        var clock = Stopwatch.StartNew();
        var next = 0L;
        while (!ct.IsCancellationRequested)
        {
            var due = next - clock.ElapsedTicks * 10_000_000L / Stopwatch.Frequency;
            if (due > 0)
            {
                var waitMs = (int)Math.Min(20, due / 10_000 + 1);
                try { await Task.Delay(Math.Max(1, waitMs), ct).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; }
                continue;
            }

            try
            {
                GrabLatestFrame();
                if (_latest is not null) WriteFrame(_latest, next);
                next += _frameDurationHns;
                if (_latest is not null) Interlocked.Increment(ref _framesWritten);
            }
            catch (Exception ex)
            {
                // 取帧/写入失败（磁盘满、编码器出错）：停掉编码线程，保留已写入的部分
                _lastError = ex.Message;
                LauncherLog.Write("record", $"编码循环中断：{ex}");
                break;
            }
        }
    }

    /// <summary>取这一拍的最新画面（同一拍内只做一次 GPU→CPU 拷贝；缓冲轮换复用）</summary>
    private void GrabLatestFrame()
    {
        using var frame = _pool?.TryGetNextFrame();
        if (frame is null) return;
        try
        {
            using var bitmap = CanvasBitmap.CreateFromDirect3D11Surface(_device!, frame.Surface);
            var pixels = bitmap.GetPixelBytes();
            if (pixels.Length < _stride * _height) return; // 尺寸对不上（源变了）：这一拍跳过
            (_latest, _spare) = (pixels, _latest);
            _hasFrame = true;
            _lastError = "";
        }
        catch (Exception ex)
        {
            // 取帧失败只记第一次，避免 30fps 刷屏；同时如实反映到状态里
            if (_frameErrorLogged) return;
            _frameErrorLogged = true;
            _lastError = $"取帧失败：{ex.Message}";
            LauncherLog.Write("record", $"取帧失败：{ex}");
        }
    }

    private void WriteFrame(byte[] pixels, long timestampHns)
    {
        var writer = _writer ?? throw new InvalidOperationException("编码器已关闭");
        var length = (uint)(_stride * _height);
        MediaFoundation.Check(MediaFoundation.MFCreateMemoryBuffer(length, out var buffer), "创建缓冲区");
        MediaFoundation.Check(buffer.Lock(out var ptr, out _, out _), "锁定缓冲区");
        try
        {
            Marshal.Copy(pixels, 0, ptr, (int)length);
        }
        finally
        {
            buffer.Unlock();
        }
        MediaFoundation.Check(buffer.SetCurrentLength(length), "设置缓冲区长度");

        MediaFoundation.Check(MediaFoundation.MFCreateSample(out var sample), "创建样本");
        MediaFoundation.Check(sample.AddBuffer(buffer), "挂载缓冲区");
        MediaFoundation.Check(sample.SetSampleTime(timestampHns), "设置时间戳");
        MediaFoundation.Check(sample.SetSampleDuration(_frameDurationHns), "设置时长");
        MediaFoundation.Check(writer.WriteSample(_streamIndex, sample), "写入样本");
    }

    private IMFSinkWriter CreateWriter(RecordingRequest request, int width, int height)
    {
        MediaFoundation.Check(MediaFoundation.MFCreateAttributes(out var attrs, 3), "创建属性");
        MediaFoundation.SetUInt32(attrs, MfGuids.MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS, 1);
        MediaFoundation.SetUInt32(attrs, MfGuids.MF_SINK_WRITER_DISABLE_THROTTLING, 1);
        MediaFoundation.SetUInt32(attrs, MfGuids.MF_LOW_LATENCY, 1);

        MediaFoundation.Check(MediaFoundation.MFCreateSinkWriterFromURL(request.OutputPath, IntPtr.Zero,
            attrs, out var writer), "创建 Sink Writer");

        // 输出：H.264
        MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out var outType), "创建输出类型");
        MediaFoundation.SetGuid(outType, MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
        MediaFoundation.SetGuid(outType, MfGuids.MF_MT_SUBTYPE, MfGuids.MFVideoFormat_H264);
        MediaFoundation.SetUInt32(outType, MfGuids.MF_MT_AVG_BITRATE, request.BitrateBps);
        MediaFoundation.SetUInt32(outType, MfGuids.MF_MT_INTERLACE_MODE,
            MfGuids.MFVideoInterlace_Progressive);
        // MF_MT_FRAME_SIZE / FRAME_RATE / PIXEL_ASPECT_RATIO 都是 HI32:LO32 打包的 UINT64
        MediaFoundation.SetPackedPair(outType, MfGuids.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
        MediaFoundation.SetPackedPair(outType, MfGuids.MF_MT_FRAME_RATE, (uint)request.Fps, 1);
        MediaFoundation.SetPackedPair(outType, MfGuids.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        MediaFoundation.Check(writer.AddStream(outType, out _streamIndex), "添加流");

        // 输入：WGC 给的就是 BGRA8 自上而下，用 ARGB32（同名不同序：MF 的 ARGB32 就是 BGRA 内存序、top-down）
        MediaFoundation.Check(MediaFoundation.MFCreateMediaType(out var inType), "创建输入类型");
        MediaFoundation.SetGuid(inType, MfGuids.MF_MT_MAJOR_TYPE, MfGuids.MFMediaType_Video);
        MediaFoundation.SetGuid(inType, MfGuids.MF_MT_SUBTYPE, MfGuids.MFVideoFormat_ARGB32);
        MediaFoundation.SetUInt32(inType, MfGuids.MF_MT_INTERLACE_MODE,
            MfGuids.MFVideoInterlace_Progressive);
        MediaFoundation.SetPackedPair(inType, MfGuids.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
        MediaFoundation.SetPackedPair(inType, MfGuids.MF_MT_FRAME_RATE, (uint)request.Fps, 1);
        MediaFoundation.SetPackedPair(inType, MfGuids.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
        // 输入是自上而下的一整行像素：显式给正 stride，避免 MF 按 DIB 习惯当成倒过来的图
        MediaFoundation.SetUInt32(inType, MfGuids.MF_MT_DEFAULT_STRIDE, (uint)_stride);
        MediaFoundation.Check(writer.SetInputMediaType(_streamIndex, inType, null), "设置输入类型");
        MediaFoundation.Check(writer.BeginWriting(), "开始写入");
        return writer;
    }

    public void Dispose()
    {
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }
}

/// <summary>从 HWND / HMONITOR 造 GraphicsCaptureItem（WinRT 没给这个口子，只能走 COM 激活工厂）</summary>
internal static class CaptureItemInterop
{
    [ComImport, Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IGraphicsCaptureItemInterop
    {
        [PreserveSig]
        int CreateForWindow(IntPtr window, ref Guid riid, out IntPtr result);
        [PreserveSig]
        int CreateForMonitor(IntPtr monitor, ref Guid riid, out IntPtr result);
    }

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr activatableClassId, ref Guid iid, out IntPtr factory);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string sourceString,
        int length, out IntPtr hstring);

    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    public static IntPtr PrimaryMonitor()
    {
        // MONITOR_DEFAULTTOPRIMARY = 1
        var pt = new POINT { X = 0, Y = 0 };
        return MonitorFromPoint(pt, 1);
    }

    public static GraphicsCaptureItem CreateForWindow(IntPtr hwnd) => Create(hwnd, forMonitor: false);

    public static GraphicsCaptureItem CreateForMonitor(IntPtr hmonitor) => Create(hmonitor, forMonitor: true);

    private static GraphicsCaptureItem Create(IntPtr handle, bool forMonitor)
    {
        const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var hr = WindowsCreateString(className, className.Length, out var hstring);
        MediaFoundation.Check(hr, "创建类名字符串");
        try
        {
            var iid = new Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");
            MediaFoundation.Check(RoGetActivationFactory(hstring, ref iid, out var factoryPtr), "取激活工厂");
            try
            {
                var factory = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPtr);
                var itemIid = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
                var hr2 = forMonitor
                    ? factory.CreateForMonitor(handle, ref itemIid, out var itemPtr)
                    : factory.CreateForWindow(handle, ref itemIid, out itemPtr);
                MediaFoundation.Check(hr2, "创建捕获目标");
                if (itemPtr == IntPtr.Zero) throw new InvalidOperationException("捕获目标创建失败");
                // FromAbi 接管这个引用，不能再手动 Release，否则会双重释放
                return MarshalInspectable<GraphicsCaptureItem>.FromAbi(itemPtr);
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        finally
        {
            WindowsDeleteString(hstring);
        }
    }
}
