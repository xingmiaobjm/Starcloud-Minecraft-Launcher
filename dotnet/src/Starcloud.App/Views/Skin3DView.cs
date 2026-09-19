// 账户页右侧悬浮的 Minecraft 玩家 3D 形象。
//
// 架构（绕开 WinUI3 WebView2 无法透明的官方限制 #2992）：
//   WebView2（隐藏，仅作 skinview3d 引擎宿主）
//     → CapturePreview 抓帧（浏览器侧背景全透明，PNG 带透明通道）
//     → CanvasBitmap 解码
//     → Win2D CanvasControl 呈现（透明组合层，模型悬浮于亚克力背景）
//
// 性能（隐藏页面连续抓帧很烧 CPU）：
//   走路动画按相位确定性渲染（sin(8p) 周期 = π/4 progress，36 帧/周期，
//   30fps 下 1.2 秒/周期，步速放缓）。皮肤就绪后逐帧录制一个完整周期：
//   C# 逐帧发 {tick k} → JS 渲染精确相位 k/36，双 rAF 等合成完成后回执
//   [tick done] → 宿主再抓帧，帧序与相位严格一一对应。页面自身不开任何
//   rAF/interval（引擎时钟全程冻结），杜绝双时钟打架导致的快走/抽搐。
//   播放由 CompositionTarget.Rendering（vsync）按 33ms 步进 Invalidate 换帧
//   （CanvasControl 是按需重绘控件，必须显式 Invalidate），循环绝对无缝；
//   稳态零抓帧零渲染。仅 换肤/拖拽结束/尺寸变化/走路开关切换 时重录一次。
//   走路关闭：单帧静止姿态，抓 2 帧后停表。
//
// 皮肤来源：微软账户用 UUID、旧数据用玩家名走 minotar.net/skin（与头像同源）；
// 第三方账户由认证服务器 sessionserver 解析皮肤地址 →
// 磁盘缓存 skin_cache → 全部失败用内置 Assets/steve.png；以 data URI 传给引擎
//（绕过跨域限制）；slim 版型在 C# 侧检测后传 loadSkin 的 {model} 选项。
//
// 交互：拖拽旋转（XAML 层捕获指针位移 → 消息驱动模型自转，拖拽期间 30fps 实时抓帧）。

using System.Diagnostics;
using System.Net.Http;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.Web.WebView2.Core;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Starcloud.App.Views;

public sealed class Skin3DView : UserControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "starcloud-launcher", "skin_cache");

    private const int LoopFrames = 36;              // 1 个走路周期（36 帧）
    private const int LoopIntervalMs = 33;          // 播放步长：30fps（36 帧 = 1.2 秒/周期）

    private readonly Grid _panel;
    private readonly CanvasControl _canvas;
    private readonly WebView2 _web;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(LoopIntervalMs) };

    private bool _initStarted, _webReady, _capturing, _recording;
    private string? _skinKey;
    private byte[]? _lastPng;        // 引擎就绪前已解析好的皮肤，就绪后补发
    private bool _lastSlim;
    private bool _skinApplied;       // JS 端皮肤纹理已完成加载

    private CanvasBitmap? _bitmap;   // 实时模式当前帧
    private readonly List<CanvasBitmap> _loopFrames = new();   // 走路循环帧（1 个整周期）
    private bool _loopMode;          // true = 循环播放（零抓帧，vsync 按时间取帧）
    private int _staticFramesLeft;   // 走路关闭后仍需补抓的静止帧数
    private int _recordEpoch;        // 录制世代号：Clear/Start 都自增，旧世代的在途录制一律作废

    private double _lastVsyncMs = -1;   // 最近一次 CompositionTarget.Rendering 的 vsync 时间戳
    private double _playStartMs = -1;   // 循环起播时间戳（vsync 时钟；-1 = 未就绪退回 Stopwatch）

    private bool _dragging;
    private Windows.Foundation.Point _lastPos;
    private long _lastSpinMs;         // spin 消息限流（~40msg/s）
    private int _liveCaptures;        // 本次拖拽已完成的实时抓帧数（0 = 仍需用旧循环帧兜底显示）
    private int _lastLoopIdx;         // 循环播放最近显示的帧序（拖拽开始时无缝接管显示）
    private int _livePhase;           // 拖拽实时模式的当前相位（与录制共用 0..35 相位空间）
    private bool _liveBusy;           // 拖拽实时步进互斥（上一步未完成不重入）

    private bool _walkEnabled = true;

    /// <summary>是否播放走路动画（设置 account_walk_animation）。</summary>
    public bool WalkEnabled
    {
        get => _walkEnabled;
        set
        {
            if (_walkEnabled == value) return;
            _walkEnabled = value;
            if (!_webReady) return;
            _ = PostAsync(new { type = "walk", on = value });
            if (value)
            {
                StartLoopRecording();      // 皮肤已就绪则重录循环，否则等 skin loaded 消息触发
            }
            else
            {
                ClearLoop();
                _staticFramesLeft = 2;
                _timer.Start();
            }
        }
    }

    public Skin3DView()
    {
        _panel = new Grid();
        Content = _panel;

        // 呈现层：Win2D 画布（透明组合层）；循环播放的换帧由 CompositionTarget.Rendering
        //（vsync）驱动，节奏与显示刷新率对齐，不受 UI 线程计时抖动影响
        _canvas = new CanvasControl { ClearColor = Microsoft.UI.Colors.Transparent };
        _canvas.Draw += OnDraw;
        _panel.Children.Add(_canvas);

        // 渲染层：WebView2 只当引擎宿主，完全不显示（Opacity=0 而非 Collapsed，
        // 避免 Chromium 判定页面隐藏而节流 rAF 停掉走路动画）
        _web = new WebView2 { Opacity = 0, IsHitTestVisible = false };
        _panel.Children.Add(_web);

        _panel.PointerPressed += (_, e) =>
        {
            _dragging = true;
            _liveCaptures = 0;                             // 拖拽首帧实时抓帧到位前，用旧循环帧兜底显示（防闪白）
            _lastPos = e.GetCurrentPoint(_panel).Position;
            _panel.CapturePointer(e.Pointer);
            if (_loopMode || _recording)
            {
                CancelLoopPlayback();                      // 退出循环/取消录制 → 实时模式（旧循环帧暂留作兜底）
            }
            _livePhase = _lastLoopIdx;                     // 从当前播放帧继续走，相位无缝衔接
            if (!_timer.IsEnabled) _timer.Start();
        };
        _panel.PointerMoved += (_, e) =>
        {
            if (!_dragging) return;
            var pos = e.GetCurrentPoint(_panel).Position;
            var dx = pos.X - _lastPos.X;
            var dy = pos.Y - _lastPos.Y;
            _lastPos = pos;
            if (Math.Abs(dx) > 0.5 || Math.Abs(dy) > 0.5)
            {
                var now = Environment.TickCount64;
                if (now - _lastSpinMs < 25) return;        // 限 ~40msg/s：高频指针事件直发会洪泛 WebView 消息队列
                _lastSpinMs = now;
                if (_webReady) _ = PostAsync(new { type = "spin", dx, dy });
            }
        };
        _panel.PointerReleased += (_, e) =>
        {
            _dragging = false;
            _panel.ReleasePointerCapture(e.Pointer);
            if (_walkEnabled)
            {
                StartLoopRecording();              // 拖拽结束：重录循环（模型已被转过）
            }
            else
            {
                _staticFramesLeft = 2;             // 走路关闭：再抓两帧静止姿态即停
            }
        };
        _panel.PointerWheelChanged += (_, _) => { }; // 滚轮不劫持页面滚动

        SizeChanged += (_, _) =>
        {
            if (_webReady && !_dragging)
            {
                _ = PostAsync(new { type = "size", w = (int)ActualWidth, h = (int)ActualHeight });
                if (_walkEnabled && _skinApplied) StartLoopRecording();   // 尺寸变化：重录（epoch 会安全作废在途录制）
            }
        };

        Loaded += (_, _) =>
        {
            if (_initStarted) return;
            _initStarted = true;
            // vsync 回调驱动循环换帧（Loaded/Unloaded 成对注册注销，避免静态事件泄漏）
            CompositionTarget.Rendering += OnLoopRender;
            _ = InitAsync();
        };
        Unloaded += (_, _) =>
        {
            // 离开账户页：停表并关闭 WebView2，不留后台渲染消耗
            CompositionTarget.Rendering -= OnLoopRender;
            _timer.Stop();
            _recording = false;
            if (_webReady) { try { _web.Close(); } catch { } }
            _webReady = false;
            _initStarted = false;
            _skinApplied = false;
            ClearLoop();
            _bitmap?.Dispose();
            _bitmap = null;
        };
        _timer.Tick += (_, _) =>
        {
            if (_loopMode) { _canvas.Invalidate(); return; }   // 循环模式：只请求重绘（vsync 链的兜底节奏）
            if (_dragging) _ = LiveDragStepAsync();            // 拖拽实时：tick→等回执→抓帧，与录制同一确定性管线
            else _ = CaptureFrameAsync(false, -1);             // 静止模式补帧（走路关闭/皮肤就绪前）
        };
    }

    private async Task InitAsync()
    {
        try
        {
            // WinUI3 的 WebView2 不支持透明（官方限制 #2992），但浏览器侧渲染仍可带
            // alpha：环境变量强制背景全透明（0 = #00000000），仅影响捕获像素而非屏幕。
            // 隐藏的 WebView2 会被 Chromium 节流，须禁用后台节流保证动画照常渲染。
            var options = new CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
                    "--disable-background-timer-throttling --disable-features=msSmartScreenProtection",
            };
            Environment.SetEnvironmentVariable("WEBVIEW2_DEFAULT_BACKGROUND_COLOR", "0");
            var env = await CoreWebView2Environment.CreateWithOptionsAsync(null, null, options);
            await _web.EnsureCoreWebView2Async(env);
            // 控制器背景透明决定 CapturePreview 输出的 PNG 是否带 alpha 通道
            //（屏幕合成层仍不支持透明，但本 WebView2 已隐藏，无所谓）
            _web.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
            var core = _web.CoreWebView2;
            core.WebMessageReceived += (_, msg) =>
            {
                string? text;
                try { text = msg.TryGetWebMessageAsString(); } catch { text = null; }
                if (text == "skin loaded")
                {
                    // 皮肤纹理就绪：录循环（走路开）或抓静止帧（走路关）
                    _skinApplied = true;
                    if (_walkEnabled) StartLoopRecording();
                    else { _staticFramesLeft = 2; _timer.Start(); }
                }
                else if (text is not null && text.StartsWith("[tick done] "))
                {
                    // 帧号匹配才放行：上一帧迟到的回执提前放行当前帧会抓到旧相位（播放抽搐）
                    if (int.TryParse(text.AsSpan("[tick done] ".Length), out var k) && k == _tickAckFrame)
                        _tickAck?.TrySetResult(true);
                }
            };
            core.SetVirtualHostNameToFolderMapping("app3d.local",
                Path.Combine(AppContext.BaseDirectory, "Assets", "3d"),
                CoreWebView2HostResourceAccessKind.Allow);
            core.NavigationCompleted += async (_, _) =>
            {
                _webReady = true;
                await PostAsync(new { type = "size", w = (int)ActualWidth, h = (int)ActualHeight });
                await PostAsync(new { type = "walk", on = _walkEnabled });
                if (_lastPng != null) await PostSkinAsync(_lastPng, _lastSlim);
            };
            core.Navigate("https://app3d.local/player.html");
        }
        catch (Exception ex)
        {
            Starcloud.Core.Services.LauncherLog.Error("player3d", "WebView2 初始化失败", ex);
        }
    }

    // ── 逐帧确定性录制：逐帧发 tick 指令渲染精确相位 → 抓帧，循环绝对无缝 ──
    // 用世代号（epoch）管理生命周期：拖拽/重录会 ClearLoop 并开新世代，
    // 旧世代尚在 await 中的录制循环醒来后发现 epoch 不匹配即自行退出——
    // 否则旧循环会因 _recording 被新录制重新置 true 而"复活"，两套 tick
    // 交错抓帧，循环帧相位错乱（表现为播放抽搐、帧数异常如 24/7 帧完成）

    private void StartLoopRecording()
    {
        if (!_webReady || !_walkEnabled || !_skinApplied || _dragging) return;
        _timer.Stop();
        ClearLoop();               // 先清旧循环（epoch 自增 → 所有在途录制作废）
        _recording = true;         // 再置录制标志，随后录制循环据此写入 _loopFrames
        _ = RecordLoopAsync(_recordEpoch);
    }

    private async Task RecordLoopAsync(int epoch)
    {
        for (int k = 0; k < LoopFrames; k++)
        {
            if (!_webReady || !_walkEnabled || !_skinApplied || _dragging || epoch != _recordEpoch) return;
            await PostAsync(new { type = "tick", k });
            await WaitTickAckAsync(k);                          // 等帧号匹配的 [tick done] 再抓帧，超时兜底继续
            if (epoch != _recordEpoch) return;                  // 等待期间被新录制/拖拽取代
            await CaptureFrameAsync(true, epoch);
            if (epoch != _recordEpoch) return;
        }
        _recording = false;
        _loopFrameClock.Restart();                             // 循环播放从帧 0 起
        _playStartMs = _lastVsyncMs;                           // vsync 时钟起播（-1 则 OnDraw 退回 Stopwatch）
        _loopMode = true;                                      // 录制完成 → 循环播放，零抓帧
        _timer.Start();                                        // 33ms 兜底换帧节奏（vsync 链的主节奏之外）
        _canvas.Invalidate();                                  // 立即起播，不等下一拍
        Starcloud.Core.Services.LauncherLog.Write("player3d", $"走路循环录制完成：{_loopFrames.Count} 帧");
    }

    /// <summary>JS 端回执（setTimeout(0)，隐藏页面 rAF 挂起不可用；正常 ~1-5ms，150ms 超时兜底）。</summary>
    private TaskCompletionSource<bool>? _tickAck;
    private int _tickAckFrame = -1;

    private async Task WaitTickAckAsync(int k)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _tickAck = tcs;
        _tickAckFrame = k;
        await Task.WhenAny(tcs.Task, Task.Delay(150));
        if (_tickAck == tcs) { _tickAck = null; _tickAckFrame = -1; }
    }

    private void ClearLoop()
    {
        _recordEpoch++;          // 旧世代在途录制就此作废（醒来后 epoch 不匹配即退出）
        _loopMode = false;
        _recording = false;
        foreach (var f in _loopFrames) f.Dispose();
        _loopFrames.Clear();
    }

    /// <summary>
    /// 停止循环播放/录制但暂不释放帧：拖拽开始时调用。旧循环帧保留在
    /// _loopFrames 里作为显示兜底（首次实时抓帧到达前画布不至于闪白），
    /// 直到下一次 StartLoopRecording → ClearLoop 才真正释放。
    /// </summary>
    private void CancelLoopPlayback()
    {
        _recordEpoch++;
        _loopMode = false;
        _recording = false;
    }

    /// <summary>
    /// 拖拽实时步进：发 tick 渲染下一相位 → 等帧号匹配的回执 → 抓帧。
    /// 与录制走同一条确定性管线——若发完消息不等于回执就抓帧，CapturePreview
    /// 可能抓到上一相位（消息尚未被 JS 处理），新旧帧交替即表现为抽搐。
    /// </summary>
    private async Task LiveDragStepAsync()
    {
        if (_liveBusy || !_dragging || !_webReady) return;
        _liveBusy = true;
        try
        {
            _livePhase = (_livePhase + 1) % LoopFrames;
            await PostAsync(new { type = "tick", k = _livePhase });
            await WaitTickAckAsync(_livePhase);
            await CaptureFrameAsync(false, -1);
        }
        finally
        {
            _liveBusy = false;
        }
    }

    // ── 单帧捕获：WebView2 → PNG → CanvasBitmap ──

    /// <param name="asRecordFrame">true = 写入循环帧列表；false = 更新实时显示帧 _bitmap</param>
    /// <param name="epoch">录制世代号；await 期间世代更替则废弃本帧——
    /// 若按 _recording 现值判断归属，旧世代/拖拽的在途抓帧会在新录制 ClearLoop
    /// 之后醒来，把一帧错误相位永久混进新循环（表现为每轮循环固定位置抽一下）</param>
    private async Task CaptureFrameAsync(bool asRecordFrame, int epoch)
    {
        if (_capturing || !_webReady || _web.CoreWebView2 is null || _loopMode) return;
        if (!asRecordFrame && !_walkEnabled && !_dragging)
        {
            if (_staticFramesLeft <= 0)
            {
                _timer.Stop();           // 静止姿态已抓到且不在拖拽，停止循环
                return;
            }
            _staticFramesLeft--;
        }
        _capturing = true;
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            await _web.CoreWebView2.CapturePreviewAsync(CoreWebView2CapturePreviewImageFormat.Png, stream);
            stream.Seek(0);

            // 直接在控件设备上解码建图
            var bitmap = await CanvasBitmap.LoadAsync(_canvas, stream, 96);
            if (asRecordFrame)
            {
                if (epoch != _recordEpoch)
                {
                    bitmap.Dispose();                // 世代已更替：本帧作废，绝不混入新循环
                    return;
                }
                _loopFrames.Add(bitmap);             // 循环帧由 ClearLoop 统一释放
            }
            else
            {
                _bitmap?.Dispose();
                _bitmap = bitmap;
                if (_dragging) _liveCaptures++;      // 拖拽实时帧已就绪，OnDraw 可切换出兜底帧
            }
            _canvas.Invalidate();                    // CanvasControl 按需重绘：新帧就绪，主动请求重画
        }
        catch (Exception ex)
        {
            Starcloud.Core.Services.LauncherLog.Error("player3d", "帧捕获失败", ex);
            _timer.Stop();
        }
        finally
        {
            _capturing = false;
        }
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        var n = _loopFrames.Count;
        CanvasBitmap? bmp;
        if (n > 0 && (_loopMode || _recording))
        {
            // 录制中：显示最新抓到的帧（渐进出画）；循环播放：按 vsync 时钟取当前相位帧。
            // 帧长用精确 1000/30 ms：60Hz 下恰为 2 个整 vsync、120Hz 下 4 个，
            // 换帧点与刷新节拍严格对齐；vsync 时间戳取自 Rendering 事件（量化到帧），
            // 消除 Stopwatch 连续时钟在 vsync 边界附近抖动导致的周期性微跳
            if (_recording && !_loopMode)
            {
                bmp = _loopFrames[n - 1];
            }
            else
            {
                double elapsed = _playStartMs >= 0 && _lastVsyncMs >= _playStartMs
                    ? _lastVsyncMs - _playStartMs
                    : _loopFrameClock.Elapsed.TotalMilliseconds;
                var idx = (int)(elapsed / (1000.0 / 30.0)) % n;
                _lastLoopIdx = idx;                        // 记录当前帧，拖拽开始时无缝接管
                bmp = _loopFrames[idx];
            }
        }
        else if (_dragging && _liveCaptures == 0 && n > 0)
        {
            bmp = _loopFrames[Math.Min(_lastLoopIdx, n - 1)];   // 拖拽首帧实时抓帧前的兜底（防闪白）
        }
        else
        {
            bmp = _bitmap;
        }
        if (bmp is null) return;
        var rect = new Windows.Foundation.Rect(0, 0, (float)sender.ActualWidth, (float)sender.ActualHeight);
        args.DrawingSession.DrawImage(bmp, rect);
    }

    private readonly Stopwatch _loopFrameClock = Stopwatch.StartNew();

    // vsync 驱动循环播放。注意：WinUI3 桌面版 CompositionTarget.Rendering 只在
    // 视觉树发生渲染时触发（无持续渲染循环），因此循环模式下必须每帧无条件
    // Invalidate 维持「渲染 → Rendering → Invalidate → 渲染」自持链——一旦跳过
    // Invalidate，链条即断，动画只在鼠标移动等其他 UI 活动时蹦帧。
    // 顺带记录 RenderingTime（vsync 时间戳）作为播放时钟（见 OnDraw）
    private void OnLoopRender(object? sender, object? e)
    {
        if (e is RenderingEventArgs rea) _lastVsyncMs = rea.RenderingTime.TotalMilliseconds;
        if (_loopMode && _loopFrames.Count > 0) _canvas.Invalidate();
    }

    // ── 公共 API ──

    /// <summary>按账户切换皮肤（微软=UUID、第三方=认证服务器会话皮肤）；空账户渲染内置 Steve。</summary>
    public void SetAccount(string? type, string? uuid, string? username, string? externalBase = null)
        => _ = LoadSkinAsync(type, uuid, username, externalBase);

    // ── 皮肤：磁盘缓存 → 皮肤源（minotar / 认证服务器） → 内置 Steve ──

    private async Task LoadSkinAsync(string? type, string? uuid, string? username, string? externalBase = null)
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
        if (_skinKey == key && _skinApplied && _webReady) return;   // 同账户已在显示：跳过，避免整轮重录
        _skinKey = key;

        byte[]? png = null;
        if (key != "steve")
        {
            var file = Path.Combine(CacheDir, $"{Sanitize(key)}.png");
            if (!File.Exists(file))
            {
                // 皮肤地址：微软/旧数据走 minotar（玩家名）；第三方由认证服务器的会话接口解析
                var url = key.StartsWith("e_", StringComparison.Ordinal)
                    ? await ExternalSkinUrlAsync(externalBase!, uuid!)
                    : $"https://minotar.net/skin/{Uri.EscapeDataString((type == "microsoft" ? uuid : username)!)}";
                if (url is not null)
                {
                    try
                    {
                        using var resp = await Http.GetAsync(url);
                        resp.EnsureSuccessStatusCode();
                        Directory.CreateDirectory(CacheDir);
                        await using var fs = File.Create(file);
                        await resp.Content.CopyToAsync(fs);
                    }
                    catch (Exception ex)
                    {
                        Starcloud.Core.Services.LauncherLog.Error("player3d", $"皮肤下载失败 url={url}", ex);
                        try { File.Delete(file); } catch { }
                    }
                }
            }
            if (File.Exists(file))
            {
                try { png = await File.ReadAllBytesAsync(file); }
                catch (Exception ex) { Starcloud.Core.Services.LauncherLog.Error("player3d", "缓存皮肤读取失败", ex); }
            }
        }
        if (png == null)
        {
            var steve = Path.Combine(AppContext.BaseDirectory, "Assets", "steve.png");
            if (!File.Exists(steve)) return;
            png = await File.ReadAllBytesAsync(steve);
        }
        if (_skinKey != key) return;   // 期间已切换账户

        _lastPng = png;
        _lastSlim = await DetectSlimAsync(png);
        if (_webReady) await PostSkinAsync(png, _lastSlim);
    }

    /// <summary>第三方皮肤地址：认证服务器 sessionserver → textures.SKIN.url；失败返回 null（回退 Steve）。</summary>
    private async Task<string?> ExternalSkinUrlAsync(string externalBase, string uuid)
    {
        try
        {
            return await AppServices.Instance.Yggdrasil.GetSkinUrlAsync(externalBase, uuid);
        }
        catch (Exception ex)
        {
            Starcloud.Core.Services.LauncherLog.Error("player3d", "第三方皮肤地址解析失败", ex);
            return null;
        }
    }

    private static string Sha1Prefix8(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(s)), 0, 4).ToLowerInvariant();

    private async Task PostSkinAsync(byte[] png, bool slim)
    {
        var b64 = Convert.ToBase64String(png);
        await PostAsync(new { type = "skin", data = "data:image/png;base64," + b64, slim });
        // skin loaded 消息回来后由 WebMessageReceived 触发录循环 / 抓静止帧
    }

    // slim 版型检测：64x64 皮肤在 (54,20) 处 slim 手臂为透明、宽臂不透明；
    // 旧版 64x32 皮肤无 slim，按比例映射后同一点即为手臂区 → 不误判
    private static async Task<bool> DetectSlimAsync(byte[] png)
    {
        try
        {
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            var decoder = await BitmapDecoder.CreateAsync(stream);
            int w = (int)decoder.PixelWidth, h = (int)decoder.PixelHeight;
            var pixelData = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
                new BitmapTransform(), ExifOrientationMode.IgnoreExifOrientation,
                ColorManagementMode.DoNotColorManage);
            var bytes = pixelData.DetachPixelData();
            int stride = w * 4;
            int sx = 54 * w / 64, sy = 20 * h / 64;
            return sy * stride + sx * 4 + 3 < bytes.Length && bytes[sy * stride + sx * 4 + 3] == 0;
        }
        catch
        {
            return false;
        }
    }

    private async Task PostAsync(object msg)
    {
        try
        {
            _web.CoreWebView2?.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(msg));
        }
        catch (Exception ex)
        {
            Starcloud.Core.Services.LauncherLog.Error("player3d", "消息发送失败", ex);
        }
        await Task.CompletedTask;
    }

    private static string Sanitize(string key)
    {
        var sb = new System.Text.StringBuilder(key.Length);
        foreach (var c in key) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
