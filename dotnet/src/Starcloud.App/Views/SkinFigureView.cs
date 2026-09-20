// 账户页右侧的玩家 3D 形象（原生渲染版）。
//
// 与旧实现（Skin3DView：WebView2 装 skinview3d，再靠 CapturePreview 抓帧绕开
// WebView2 无法透明的限制）的区别：这里没有任何浏览器、没有抓帧、没有帧缓存，
// 每一帧都由 Starcloud.Core.Rendering 的软件光栅化器直接算出来，再把 BGRA
// 写进一张 Win2D 位图。符合 DESIGN.md §1「拒绝 WebView 假原生」与皮肤查看器
// 设计文档 §2 记录的方案。
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

namespace Starcloud.App.Views;

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
    private CanvasBitmap? _bitmap;
    private int _bitmapW, _bitmapH;
    private double _bitmapScale = 1.0;      // 当前位图相对控件物理像素的倍率
    private double _renderScale = 1.0;      // 自适应渲染倍率：默认 1:1（清晰），跟不上才降
    private bool _renderBusy;               // 后台渲染互斥（同一时刻只跑一帧）
    private bool _renderPending;            // 渲染期间又有新请求：完成后再补一帧
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
        // 走动时若这台机器算不过来，RenderScale 会自适应降档换帧率，静止时始终 1:1。
        var scale = _walkEnabled ? _renderScale : 1.0;
        var dpiScale = _canvas.Dpi / 96.0 * scale;
        var pxW = Math.Max(32, (int)Math.Round(_canvas.ActualWidth * dpiScale));
        var pxH = Math.Max(32, (int)Math.Round(_canvas.ActualHeight * dpiScale));

        Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            BitmapFrame frame;
            try
            {
                frame = SkinRenderer.Render(model, new RenderOptions(camera), pxW, pxH, pose, _target);
            }
            catch
            {
                DispatcherQueue.TryEnqueue(() => _renderBusy = false);
                return;
            }
            clock.Stop();

            // 自适应：只在明显跟不上时降档（>60ms ≈ 不到 16fps），有余量再升回去。
            // 这台机器单核算力波动很大（实测同密度循环 37-51ns/像素），
            // 但它能维持约 25fps，所以正常情况下会一直停在 1:1 的清晰档。
            var ms = clock.Elapsed.TotalMilliseconds;
            if (_walkEnabled)
            {
                if (ms > 60) _renderScale = Math.Max(MinAdaptiveScale, _renderScale - 0.05);
                else if (ms < 25) _renderScale = Math.Min(1.0, _renderScale + 0.05);
            }
            DispatcherQueue.TryEnqueue(() => Present(frame, scale));
        });
    }

    /// <summary>把后台算好的帧贴上屏幕（UI 线程；只做上传与重绘，不做光栅化）</summary>
    private void Present(BitmapFrame frame, double scale)
    {
        _renderBusy = false;
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
        _pitch = Math.Clamp(_pitch - (p.Y - _lastPos.Y) * 0.45, -80, 80);
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
    public void SetAccount(string? type, string? uuid, string? username, string? externalBase = null)
        => _ = LoadSkinAsync(type, uuid, username, externalBase);

    private async Task LoadSkinAsync(string? type, string? uuid, string? username, string? externalBase)
    {
        // 缓存键：微软=uuid；第三方=服务器 SHA1 前 8 位 + uuid（防不同服务器 uuid 碰撞）；旧数据兜底=玩家名
        var key = type switch
        {
            "microsoft" when !string.IsNullOrWhiteSpace(uuid) => $"m_{uuid}",
            "external" when !string.IsNullOrWhiteSpace(uuid) && !string.IsNullOrWhiteSpace(externalBase)
                => $"e_{Sha1Prefix8(externalBase!)}_{uuid}",
            _ when !string.IsNullOrWhiteSpace(username) => $"u_{username}",
            _ => "steve",
        };
        if (_skinKey == key && _model is not null) return;
        _skinKey = key;

        byte[]? png = null;
        if (key != "steve")
        {
            var file = Path.Combine(CacheDir, $"{Sanitize(key)}.png");
            if (!File.Exists(file))
            {
                var url = key.StartsWith("e_", StringComparison.Ordinal)
                    ? await ExternalSkinUrlAsync(externalBase!, uuid!)
                    : $"https://minotar.net/skin/{Uri.EscapeDataString((type == "microsoft" ? uuid : username)!)}";
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
        }
        if (png is null)
        {
            var steve = Path.Combine(AppContext.BaseDirectory, "Assets", "steve.png");
            if (!File.Exists(steve)) return;
            png = await File.ReadAllBytesAsync(steve);
        }
        if (_skinKey != key) return;   // 期间已切换账户

        try
        {
            var texture = PngDecoder.Decode(png);
            // slim 版型：64×64 皮肤在 (54,20) 处细臂为透明、宽臂不透明
            var slim = texture.Height == texture.Width &&
                       texture.Rgba[((20 * texture.Height / 64) * texture.Width + 54 * texture.Width / 64) * 4 + 3] == 0;
            _model = new SkinModel(texture, slim);
            _bitmap = null;                  // 尺寸没变，但内容变了：下次 Draw 重建
            SyncTimer();
            RequestRender();
        }
        catch (SkinTextureException ex)
        {
            LauncherLog.Write("player3d", $"皮肤纹理不可用，回退内置 Steve：{ex.Message}");
        }
    }

    private async Task<string?> ExternalSkinUrlAsync(string externalBase, string uuid)
    {
        try
        {
            return await AppServices.Instance.Yggdrasil.GetSkinUrlAsync(externalBase, uuid);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("player3d", "第三方皮肤地址解析失败", ex);
            return null;
        }
    }

    private static string Sha1Prefix8(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(s)), 0, 4).ToLowerInvariant();

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_'));
}
