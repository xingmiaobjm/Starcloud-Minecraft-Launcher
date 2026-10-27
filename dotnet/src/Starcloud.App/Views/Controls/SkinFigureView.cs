// 账户页右侧的玩家 3D 形象（原生渲染版）。
//
// 与旧实现（Skin3DView：WebView2 装 skinview3d，再靠 CapturePreview 抓帧绕开
// WebView2 无法透明的限制）的区别：这里没有任何浏览器、没有抓帧、没有帧缓存。
// 每帧由 Starcloud.Core.Rendering 算出来（预乘 BGRA），再写进一张 Win2D 位图：
//   · 首选 SkinGpuRenderer —— D3D11 硬件光栅化，630×1020 约 1ms；
//   · 没有可用显卡时回退 SkinRenderer —— 软件光栅化，同一台机器约 50ms/帧。
// 两条路径共用投影与采样规则，像素级差异只落在轮廓/接缝那一圈（见 SkinGpuTests）。
//
// 交互与旧版保持一致：拖拽转视角（yaw/pitch）、滚轮缩放、可选走路动画。

using System.Net.Http;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Starcloud.App.Services;
using Starcloud.Core.Rendering;
using Starcloud.Core.Services;
using Windows.Graphics.DirectX;

namespace Starcloud.App.Views.Controls;

public sealed class SkinFigureView : UserControl
{
    private const int WalkIntervalMs = 33;  // 约 30fps

    /// <summary>
    /// 走路步态周期（秒）：一个完整周期 = 左右腿各迈一次。1.0 = 每秒走一个周期。
    /// <para>取值经过三轮实机调整，记下来避免以后又来回试：设计文档原值 0.9 秒看着偏慢，
    /// 但那次实际是被"定时器驱动相位 + 帧率只有 10fps"拖成了约 2.7 秒一圈；
    /// 修好时钟与帧率后 0.57 秒明显偏快、0.75 秒仍偏快，用户最终定为每秒一个周期。</para>
    /// </summary>
    private const double WalkCycleSeconds = 1.0;

    /// <summary>
    /// 自适应分辨率的下限。只在"明显跟不上"（一帧超过 60ms，不到 16fps）时才降，
    /// 而且最低只到 0.85——宁可帧率低一点也要保住像素风的锐利，
    /// 这是取舍：像素风糊掉比掉几帧更伤观感。
    /// <para>只在软件回退路径上生效：走 GPU 时固定 1:1（实测一帧约 1ms，不需要降档）。</para>
    /// </summary>
    private const double MinAdaptiveScale = 0.85;

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "starcloud-launcher", "skin_cache");

    private readonly CanvasControl _canvas;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(WalkIntervalMs) };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    private SkinModel? _model;
    private SkinGpuRenderer? _gpu;          // 非空 = 走 GPU；null = 软件光栅化
    private bool _gpuUnavailable;           // 试过且失败（或设备丢失）：不再重试
    private CanvasBitmap? _bitmap;
    private int _bitmapW, _bitmapH;
    private double _bitmapScale = 1.0;      // 当前位图相对控件物理像素的倍率
    private double _renderScale = 1.0;      // 自适应渲染倍率：默认 1:1（清晰），跟不上才降
    private bool _renderBusy;               // 后台渲染互斥（同一时刻只跑一帧）
    private bool _renderPending;            // 渲染期间又有新请求：完成后再补一帧
    private bool _unloaded;                 // 已卸载：在途的那一帧不再建新设备、不再出图
    private readonly SkinRenderTarget _target = new();
    private double _yaw = 24, _pitch = 6, _zoom = 1.4;
    private string? _skinKey;
    private bool _walkEnabled = true;
    private bool _dragging;
    private Windows.Foundation.Point _lastPos;

    /// <summary>是否播放走路动画（设置项 account_walk_animation）</summary>
    public bool WalkEnabled
    {
        get => _walkEnabled;
        set
        {
            if (_walkEnabled == value) return;
            _walkEnabled = value;
            SyncTimer();
            RequestRender();
        }
    }

    public SkinFigureView()
    {
        var panel = new Grid();
        Content = panel;

        _canvas = new CanvasControl { ClearColor = Microsoft.UI.Colors.Transparent };
        _canvas.Draw += OnDraw;
        panel.Children.Add(_canvas);

        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += (_, _) => _dragging = false;
        PointerWheelChanged += OnWheel;
        SizeChanged += (_, _) => RequestRender();
        Loaded += (_, _) =>
        {
            _unloaded = false;
            SyncTimer();
            RequestRender();
        };
        Unloaded += (_, _) =>
        {
            _timer.Stop();
            _unloaded = true;
            var gpu = _gpu;
            _gpu = null;
            _gpuUnavailable = false;        // 再次加载时重新尝试
            // 卸载时无条件放掉设备：Dispose 内部与正在跑的那一帧互斥（SkinGpuRenderer._gate）。
            // 过去这里写着 "! _renderBusy" 才放，结果那一帧一跑起来设备就永远漏了。
            gpu?.Dispose();
        };

        _timer.Tick += (_, _) =>
        {
            // 相位由真实时钟推出：定时器被 UI 线程拖慢时动画速度不变
            // （之前是每次 tick 加固定 33ms，tick 一慢动画就跟着变慢）
            RequestRender();
        };
        SyncTimer();
    }

    private void SyncTimer()
    {
        if (_walkEnabled && _model is not null) _timer.Start();
        else _timer.Stop();
    }

    // ── 渲染 ──

    /// <summary>
    /// 请求渲染一帧。真正的光栅化放后台线程——这台机器一帧要几十毫秒，
    /// 压在 UI 线程上会把整个窗口拖顿（表现为按钮悬停、调度中心一起卡）。
    /// 同一时刻只跑一帧；期间的新请求合并成一次补帧，避免堆积。
    /// </summary>
    private void RequestRender()
    {
        if (_model is not { } model) return;
        if (_renderBusy) { _renderPending = true; return; }
        _renderBusy = true;

        var pose = _walkEnabled
            ? WalkAnimator.FromPhase(_clock.Elapsed.TotalSeconds / WalkCycleSeconds * 2 * Math.PI)
            : Pose.Still;
        var camera = new Camera(_yaw, _pitch, 96.0 / _zoom);

        // 按控件物理像素渲染：之前写死 256×384 再最近邻放大，高 DPI 下每个渲染像素
        // 被放大成方块、轮廓全是台阶（用户反馈的"锯齿"）。默认 1:1；
        // 软件路径走动时若算不过来，RenderScale 会自适应降档换帧率；GPU 路径固定 1:1。
        var scale = _gpu is not null || !_walkEnabled ? 1.0 : _renderScale;
        var dpiScale = _canvas.Dpi / 96.0 * scale;
        var pxW = Math.Max(32, (int)Math.Round(_canvas.ActualWidth * dpiScale));
        var pxH = Math.Max(32, (int)Math.Round(_canvas.ActualHeight * dpiScale));

        Task.Run(() =>
        {
            var gpu = EnsureGpu();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            BitmapFrame frame;
            try
            {
                var options = new RenderOptions(camera);
                frame = gpu is not null
                    ? gpu.Render(model, options, pxW, pxH, pose)
                    : SkinRenderer.Render(model, options, pxW, pxH, pose, _target);
            }
            catch (Exception ex)
            {
                // 设备丢失 / 驱动异常：这一次及以后都走软件路径，并补一帧
                if (gpu is not null)
                {
                    LauncherLog.Error("player3d", "GPU 渲染失败，回退软件光栅化", ex);
                    _gpu = null;
                    _gpuUnavailable = true;
                    gpu.Dispose();
                    _renderScale = 1.0;
                }
                DispatcherQueue.TryEnqueue(() => { _renderBusy = false; RequestRender(); });
                return;
            }
            clock.Stop();

            // 自适应：只在明显跟不上时降档（>60ms ≈ 不到 16fps），有余量再升回去。
            // 这台机器单核算力波动很大（实测同密度循环 37-51ns/像素），
            // 但它能维持约 25fps，所以正常情况下会一直停在 1:1 的清晰档。
            var ms = clock.Elapsed.TotalMilliseconds;
            if (gpu is null && _walkEnabled)
            {
                if (ms > 60) _renderScale = Math.Max(MinAdaptiveScale, _renderScale - 0.05);
                else if (ms < 25) _renderScale = Math.Min(1.0, _renderScale + 0.05);
            }
            DispatcherQueue.TryEnqueue(() => Present(frame, scale, gpu));
        });
    }

    /// <summary>
    /// 取 GPU 渲染器；只尝试一次（失败就一直用软件路径）。在后台线程创建：
    /// 建 D3D11 设备 + 编译着色器约 30~50ms，压在 UI 线程上会卡一下。
    /// </summary>
    private SkinGpuRenderer? EnsureGpu()
    {
        if (_gpu is not null || _gpuUnavailable) return _gpu;
        // 已卸载：这一帧别再建设备了——建出来的设备挂到 _gpu 上就再没人放（视图已经卸载），
        // 每进一次详情页就漏一个 D3D11 设备。这一帧改走软件路径，反正马上就不显示了。
        if (_unloaded) return null;
        _gpuUnavailable = true;
        var gpu = SkinGpuRenderer.TryCreate();
        if (gpu is null) return null;

        _gpu = gpu;
        _renderScale = 1.0;
        LauncherLog.Write("player3d", $"皮肤预览启用 GPU 硬件加速（{gpu.DriverName}）");
        return gpu;
    }

    /// <summary>把后台算好的帧贴上屏幕（UI 线程；只做上传与重绘，不做光栅化）</summary>
    private void Present(BitmapFrame frame, double scale, SkinGpuRenderer? gpu)
    {
        // 已经卸载（或已回退软件路径）时，这一帧用完就把设备放掉
        if (gpu is not null && !ReferenceEquals(gpu, _gpu)) gpu.Dispose();

        if (_bitmap is null || _bitmapW != frame.Width || _bitmapH != frame.Height)
        {
            _bitmap?.Dispose();
            _bitmap = CanvasBitmap.CreateFromBytes(_canvas, frame.Bgra, frame.Width, frame.Height,
                DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
            _bitmapW = frame.Width;
            _bitmapH = frame.Height;
        }
        else
        {
            _bitmap.SetPixelBytes(frame.Bgra);
        }
        _bitmapScale = scale;
        _canvas.Invalidate();

        // 上传完才能放行下一帧：frame.Bgra 是渲染器内部复用的缓冲，
        // 提前放行会让后台那一帧覆盖正在上传的内容（GPU 路径一帧只要 1ms，更容易撞上）。
        _renderBusy = false;
        if (_renderPending) { _renderPending = false; RequestRender(); }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_bitmap is null || sender.ActualWidth <= 0 || sender.ActualHeight <= 0) return;

        // 位图按 96 DPI 建（1 像素 = 1 DIP）→ 源矩形就是像素尺寸、目标矩形是控件尺寸：
        // 倍率为 1 时是 1:1（最近邻，保持像素风），降档时用线性放大（不出方块）。
        var dest = new Windows.Foundation.Rect(0, 0, sender.ActualWidth, sender.ActualHeight);
        var src = new Windows.Foundation.Rect(0, 0, _bitmap.SizeInPixels.Width, _bitmap.SizeInPixels.Height);
        args.DrawingSession.DrawImage(_bitmap, dest, src, 1f,
            _bitmapScale >= 0.999 ? CanvasImageInterpolation.NearestNeighbor : CanvasImageInterpolation.Linear);
    }

    // ── 交互：拖拽转视角 / 滚轮缩放 ──

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        _dragging = true;
        _lastPos = e.GetCurrentPoint(this).Position;
        CapturePointer(e.Pointer);
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging) return;
        var p = e.GetCurrentPoint(this).Position;
        _yaw += (p.X - _lastPos.X) * 0.45;
        // 垂直方向用"抓着模型转"的约定：往下拖 = 把正面往下压 = 看到头顶（相机升高）。
        // 反过来的话，往下拖会变成从下往上看，用户反馈"上下是反的"就是这个。
        _pitch = Math.Clamp(_pitch + (p.Y - _lastPos.Y) * 0.45, -80, 80);
        _lastPos = p;
        RequestRender();
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        _dragging = false;
        ReleasePointerCapture(e.Pointer);
    }

    private void OnWheel(object sender, PointerRoutedEventArgs e)
    {
        var delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        _zoom = Math.Clamp(_zoom * (delta > 0 ? 1.1 : 0.9), 0.7, 3.0);
        RequestRender();
    }

    // ── 皮肤：磁盘缓存 → 皮肤源（minotar / 认证服务器）→ 内置 Steve ──

    /// <summary>按账户切换皮肤（微软=UUID、第三方=认证服务器会话皮肤）；空账户渲染内置 Steve。</summary>
    public void SetAccount(string? type, string? uuid, string? username, string? externalBase = null,
        string? localSkinPath = null, bool forceReload = false)
        => _ = LoadSkinAsync(type, uuid, username, externalBase, localSkinPath, forceReload);

    /// <summary>
    /// 直接渲染一张本地皮肤文件（皮肤库预览用）；null = 回落到内置 Steve。
    /// 本地文件不写缓存目录，避免把用户库里的图再复制一份。
    /// </summary>
    public void SetLocalSkin(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            _skinKey = null; // 强制重新走一次加载，否则会停在上一张自定义皮肤上
            _ = LoadSkinAsync(null, null, null, null, null);
            return;
        }
        _skinKey = "local_" + path;
        _ = LoadLocalSkinAsync(path);
    }

    private async Task LoadLocalSkinAsync(string path)
    {
        byte[]? png = null;
        try { png = await File.ReadAllBytesAsync(path); }
        catch (Exception ex) { LauncherLog.Error("player3d", "本地皮肤读取失败", ex); }
        ApplySkin(png, null, "local_" + path, path);
    }

    private async Task LoadSkinAsync(string? type, string? uuid, string? username, string? externalBase,
        string? localSkinPath = null, bool forceReload = false)
    {
        // 皮肤库里指定了本机皮肤：直接用它（离线账户、以及"先看效果"的场景）
        if (!string.IsNullOrWhiteSpace(localSkinPath) && File.Exists(localSkinPath))
        {
            await LoadLocalSkinAsync(localSkinPath);
            return;
        }
        // 缓存键：微软=uuid；第三方=服务器 SHA1 前 8 位 + uuid（防不同服务器 uuid 碰撞）；旧数据兜底=玩家名
        var key = type switch
        {
            "microsoft" when !string.IsNullOrWhiteSpace(uuid) => $"m_{uuid}",
            "external" when !string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(externalBase)
                => $"e_{Sha1Prefix8(externalBase!)}_{uuid}",
            _ when !string.IsNullOrWhiteSpace(username) => $"u_{username}",
            _ => "steve",
        };
        if (!forceReload && _skinKey == key && _model is not null) return;
        // 强刷 = 换肤后磁盘缓存必是旧图：删掉，下面的下载才会真的发出去
        if (forceReload)
            try { File.Delete(Path.Combine(CacheDir, $"{Sanitize(key)}.png")); } catch { }
        _skinKey = key;

        byte[]? png = null;
        byte[]? capePng = null;
        if (key != "steve")
        {
            var isExternal = key.StartsWith("e_", StringComparison.Ordinal);
            string? capeUrl = null;
            var file = Path.Combine(CacheDir, $"{Sanitize(key)}.png");

            // 皮肤与披风的地址都来自会话接口：披风地址必须在「皮肤缓存命中」时也去取，
            // 否则第一次下载完皮肤后缓存一直命中，capeUrl 永远为 null（披风始终不显示）
            string? skinUrl = null;
            if (isExternal)
            {
                (skinUrl, capeUrl) = await ExternalTexturesAsync(externalBase!, uuid!);
            }

            if (!File.Exists(file))
            {
                var url = skinUrl ??
                    (isExternal ? null
                        : $"https://minotar.net/skin/{Uri.EscapeDataString((type == "microsoft" ? uuid : username)!)}");
                if (url is not null)
                {
                    try
                    {
                        var bytes = await Http.GetByteArrayAsync(url);
                        Directory.CreateDirectory(CacheDir);
                        await File.WriteAllBytesAsync(file, bytes);
                    }
                    catch (Exception ex)
                    {
                        LauncherLog.Error("player3d", $"皮肤下载失败 url={url}", ex);
                        try { File.Delete(file); } catch { /* 尽力而为 */ }
                    }
                }
            }
            if (File.Exists(file))
            {
                try { png = await File.ReadAllBytesAsync(file); }
                catch (Exception ex) { LauncherLog.Error("player3d", "缓存皮肤读取失败", ex); }
            }

            // 披风：只有第三方账户有（会话接口的 CAPE.url）；缓存按披风 URL 指纹，
            // 换披风 = URL 变 = 缓存自动失效，无需强刷
            if (isExternal)
            {
                if (string.IsNullOrEmpty(capeUrl))
                    LauncherLog.Write("player3d", "会话接口没有 CAPE.url（该账号当前无披风）");
                else
                {
                    var capeFile = Path.Combine(CacheDir,
                        $"cape_{Sha1Prefix8(capeUrl!)}_{Sanitize(key)}.png");
                    if (!File.Exists(capeFile))
                    {
                        try
                        {
                            await File.WriteAllBytesAsync(capeFile, await Http.GetByteArrayAsync(capeUrl!));
                        }
                        catch (Exception ex)
                        {
                            LauncherLog.Error("player3d", $"披风下载失败 url={capeUrl}", ex);
                        }
                    }
                    if (File.Exists(capeFile))
                    {
                        try { capePng = await File.ReadAllBytesAsync(capeFile); }
                        catch (Exception ex) { LauncherLog.Error("player3d", "披风缓存读取失败", ex); }
                    }
                    LauncherLog.Write("player3d",
                        $"披风纹理已就绪（{capeFile}，{capePng?.Length ?? 0} 字节）");
                }
            }
        }
        ApplySkin(png, capePng, key, null);
    }

    /// <summary>解码并换模型；纹理不可用 / 为空时回退内置 Steve（键不变则不动，防抖）。
    /// 披风纹理有效（尺寸为宽:高 = 2:1 且宽是 64 的倍数）时交给模型渲染成背部披风面。</summary>
    private void ApplySkin(byte[]? png, byte[]? capePng, string key, string? logPath)
    {
        if (_skinKey != key) return;   // 期间已切换账户
        if (png is null)
        {
            var steve = Path.Combine(AppContext.BaseDirectory, "Assets", "steve.png");
            if (!File.Exists(steve)) return;
            png = File.ReadAllBytes(steve);
        }
        try
        {
            var texture = PngDecoder.Decode(png);
            // slim 版型：64×64 皮肤在 (54,20) 处细臂为透明、宽臂不透明（与皮肤库同一判定）
            var slim = SkinTexture.DetectSlim(texture);
            SkinBitmap? cape = null;
            if (capePng is not null)
            {
                try
                {
                    var c = PngDecoder.Decode(capePng);
                    // 64×32 = 老式披风；64×64 = 鞘翅（披风正面在 (1,1)）
                    if (c.Width % 64 == 0 && (c.Height == c.Width / 2 || c.Height == c.Width)) cape = c;
                    else LauncherLog.Write("player3d",
                        $"披风纹理尺寸不支持（{c.Width}×{c.Height}），忽略");
                }
                catch (Exception ex)
                {
                    LauncherLog.Write("player3d", $"披风纹理解码失败：{ex.Message}");
                }
            }
            _model = new SkinModel(texture, slim, cape);
            _bitmap = null;                  // 尺寸没变，但内容变了：下次 Draw 重建
            SyncTimer();
            RequestRender();
        }
        catch (SkinTextureException ex)
        {
            LauncherLog.Write("player3d",
                $"皮肤纹理不可用，回退内置 Steve：{ex.Message}（{logPath ?? key}）");
        }
    }

    private async Task<(string? SkinUrl, string? CapeUrl)> ExternalTexturesAsync(
        string externalBase, string uuid)
    {
        try
        {
            return await AppServices.Instance.Yggdrasil.GetTexturesAsync(externalBase, uuid);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("player3d", "第三方纹理地址解析失败", ex);
            return (null, null);
        }
    }

    private static string Sha1Prefix8(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(s)), 0, 4).ToLowerInvariant();

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
}
