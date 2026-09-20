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
    private const int RenderWidth = 256;    // 渲染分辨率（像素风放大到实际尺寸，够清晰）
    private const int RenderHeight = 384;
    private const int WalkIntervalMs = 33;  // 约 30fps

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "starcloud-launcher", "skin_cache");

    private readonly CanvasControl _canvas;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(WalkIntervalMs) };

    private SkinModel? _model;
    private CanvasBitmap? _bitmap;
    private double _yaw = 24, _pitch = 6, _zoom = 1.4;
    private double _phase;
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
            if (!value) { _phase = 0; _canvas.Invalidate(); }
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
        SizeChanged += (_, _) => _canvas.Invalidate();

        _timer.Tick += (_, _) =>
        {
            _phase += WalkIntervalMs / 1000.0;
            if (_phase > 1e6) _phase -= 1e6;   // 长时间挂机不让相位无限增大丢精度
            _canvas.Invalidate();
        };
        SyncTimer();
    }

    private void SyncTimer()
    {
        if (_walkEnabled && _model is not null) _timer.Start();
        else _timer.Stop();
    }

    // ── 渲染 ──

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_model is null) return;

        var pose = _walkEnabled ? WalkAnimator.FromPhase(_phase * 7.0) : Pose.Still;
        var camera = new Camera(_yaw, _pitch, 96.0 / _zoom);
        var frame = SkinRenderer.Render(_model, new RenderOptions(camera), RenderWidth, RenderHeight, pose);

        // 位图只建一次，之后每帧只回写像素（避免逐帧分配 GPU 资源）
        _bitmap ??= CanvasBitmap.CreateFromBytes(sender, frame.Bgra, frame.Width, frame.Height,
            DirectXPixelFormat.B8G8R8A8UIntNormalized, 96, CanvasAlphaMode.Premultiplied);
        _bitmap.SetPixelBytes(frame.Bgra);

        // 等比铺满可用区域，保持像素风（近邻放大）
        var scale = Math.Min(sender.ActualWidth / frame.Width, sender.ActualHeight / frame.Height);
        if (scale <= 0) return;
        var w = frame.Width * scale;
        var h = frame.Height * scale;
        var rect = new Windows.Foundation.Rect(
            (sender.ActualWidth - w) / 2, (sender.ActualHeight - h) / 2, w, h);
        args.DrawingSession.DrawImage(_bitmap, rect, new Windows.Foundation.Rect(0, 0, frame.Width, frame.Height),
            1f, CanvasImageInterpolation.NearestNeighbor);
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
        _canvas.Invalidate();
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
        _canvas.Invalidate();
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
            _canvas.Invalidate();
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
