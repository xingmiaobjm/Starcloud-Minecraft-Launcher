// 账户页右侧的 Minecraft 玩家 3D 形象（安卓版）。
//
// 桌面架构是「隐藏网页控件 + CapturePreview 抓帧 + Win2D 呈现」——为绕开桌面网页控件
// 无法透明的官方限制，并把走路循环录成帧序列零抓帧回放。安卓侧没有 Win2D（csproj 不引用
// Microsoft.Graphics.Canvas），且 Uno 的网页控件门面在 Skia 下全部未实现（Uno0001），
// 因此不照搬抓帧管线，改为：**自建 NativeWebViewer（原生 Android WebView）直接呈现引擎页**
// （skinview3d，Assets/3d/player.html，经 file:///android_asset/ 加载，透明背景由控件
// DefaultBackgroundColor 与页面自身 CSS 共同保证；chrome.webview 消息桥由控件内垫片提供）。
//
// 与桌面一致的确定性动画协议（player.html 消息驱动，引擎时钟全程冻结）：
//   走路动画按相位确定性渲染（sin(8p) 周期 = π/4 progress，36 帧/周期）。
//   皮肤就绪后由 33ms DispatcherTimer 逐帧发 {tick k}（0..35 循环）推进相位——
//   相位完全由 tick 指令设置，JS 侧不开任何 rAF/interval，杜绝双时钟打架导致的快走/抽搐；
//   36 帧 × 33ms ≈ 1.2 秒/周期，与桌面 30fps 节奏一致。桌面用 vsync 换帧规避 DispatcherTimer
//   节拍抖动；安卓侧 WebView 页面合成独立于 XAML 渲染树，tick 指令只改相位不抢帧，
//   33ms 定时抖动最多让相位步进略不均匀，不会造成丢帧/回绕跳变。
//
// 皮肤来源：微软账户用 UUID、旧数据用玩家名走 minotar.net/skin（与头像同源）；
// 第三方账户由认证服务器 sessionserver 解析皮肤地址 → 磁盘缓存 skin_cache →
// 全部失败用内置 Assets/steve.png（APK 资产，经 AppAssets 解包）；以 data URI 传给引擎
// （绕过跨域限制）；slim 版型在 C# 侧检测（Core PngDecoder + SkinTexture.DetectSlim）后
// 传 loadSkin 的 {model} 选项。
//
// 交互：拖拽旋转（XAML 层捕获指针位移 → {spin} 消息驱动模型自转，拖拽期间 tick 暂停、
// 相位冻结，与桌面语义一致；松手恢复走路循环）。触屏没有滚轮，缩放不做。

using System.Net.Http;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Starcloud.Core.Rendering;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Shared;

namespace Starcloud.Launcher.Uno.Views.Controls;

public class Skin3DView : UserControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(LauncherPaths.CacheDir, "skin_cache");

    private const int LoopFrames = 36;              // 1 个走路周期（36 帧，须与 player.html 的 PHASE_STEPS 一致）
    private const int LoopIntervalMs = 33;          // 播放步长：30fps（36 帧 ≈ 1.2 秒/周期）

    private readonly Grid _panel;
    private readonly NativeWebViewer _web;
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(LoopIntervalMs) };

    private bool _webReady;
    private string? _skinKey;
    private byte[]? _lastPng;        // 引擎就绪前已解析好的皮肤，就绪后补发
    private bool _lastSlim;
    private bool _skinApplied;       // JS 端皮肤纹理已完成加载（skin loaded 回执）

    private int _tickPhase;          // 当前走路相位（0..35 循环）

    private bool _dragging;
    private Windows.Foundation.Point _lastPos;
    private long _lastSpinMs;        // spin 消息限流（~40msg/s）

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
            SyncPlayback();
        }
    }

    public Skin3DView()
    {
        _panel = new Grid();
        Content = _panel;

        // 渲染层：NativeWebViewer（原生 WebView）直接可见（透明背景），不再走桌面那套隐藏+抓帧
        _web = new NativeWebViewer
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
        };
        _panel.Children.Add(_web);

        // 消息与导航事件在构造时挂一次（控件实例与视觉树同寿命，InitWeb 重入不会重复订阅）
        _web.WebMessageReceived += (_, msg) =>
        {
            // 垫片把 chrome.webview.postMessage 的字符串原样送达（等价桌面字符串形态的 WebMessage）
            if (msg == "skin loaded")
            {
                // 皮肤纹理就绪：开始走路相位播放（走路关则渲染单帧静止姿态）
                _skinApplied = true;
                SyncPlayback();
            }
            // [tick done] / [js] 日志回执：桌面抓帧管线才需要握手，安卓直显无需处理
        };
        _web.NavigationCompleted += (_, _) =>
        {
            _webReady = true;
            _ = PostAsync(new { type = "size", w = (int)ActualWidth, h = (int)ActualHeight });
            _ = PostAsync(new { type = "walk", on = _walkEnabled });
            if (_lastPng != null) _ = PostSkinAsync(_lastPng, _lastSlim);
        };

        _panel.PointerPressed += (_, e) =>
        {
            _dragging = true;
            _lastPos = e.GetCurrentPoint(_panel).Position;
            _panel.CapturePointer(e.Pointer);
            // 拖拽期间暂停走路相位（引擎仍可渲染 spin 帧）：与桌面「拖拽时退出循环」语义一致
            _timer.Stop();
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
            SyncPlayback();                                // 松手恢复走路/静止姿态
        };
        _panel.PointerCaptureLost += (_, _) =>
        {
            _dragging = false;
            SyncPlayback();
        };

        SizeChanged += (_, _) =>
        {
            if (_webReady && !_dragging)
                _ = PostAsync(new { type = "size", w = (int)ActualWidth, h = (int)ActualHeight });
        };

        Loaded += (_, _) => InitWeb();
        Unloaded += (_, _) =>
        {
            // 离开账户页：停表即可（NativeWebViewer 自身在 Unloaded 时 Destroy 原生 WebView，
            // 重新可见时 InitWeb 会重建并重载引擎页，状态由 NavigationCompleted 回执恢复）
            _timer.Stop();
            _webReady = false;
            _skinApplied = false;
        };
        _timer.Tick += (_, _) =>
        {
            if (!_webReady || !_skinApplied) return;
            // 逐帧推进走路相位：相位完全由 tick 指令驱动（与桌面录制管线同一确定性协议）
            _ = PostAsync(new { type = "tick", k = _tickPhase });
            _tickPhase = (_tickPhase + 1) % LoopFrames;
        };
    }

    /// <summary>按当前开关恢复播放节奏：走路开 → 起表；走路关 → 停表（静止姿态已由 walk 消息渲染）。</summary>
    private void SyncPlayback()
    {
        if (_walkEnabled && _skinApplied && !_dragging) _timer.Start();
        else _timer.Stop();
    }

    /// <summary>（重）加载引擎页：Unloaded 后 NativeWebViewer 会销毁原生 WebView，重新可见时再建。</summary>
    private void InitWeb()
    {
        try
        {
            // 透明背景：控件层透明 + player.html 自身背景透明，模型直接悬浮在页面底色上
            _web.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
            // APK 资产路径：Assets/3d 已随包打进 APK，file:///android_asset/ 直接可读
            //（虚拟域映射只对磁盘目录有意义，APK 内资产用不上）
            _web.Load(new Uri("file:///android_asset/Assets/3d/player.html"));
        }
        catch (Exception ex)
        {
            LauncherLog.Error("player3d", "WebView 初始化失败", ex);
        }
    }

    // ── 公共 API ──

    /// <summary>
    /// 按账户切换皮肤（微软=UUID、第三方=认证服务器会话皮肤）；空账户渲染内置 Steve。
    /// <paramref name="localSkinPath"/> 指向皮肤库文件时优先用它（桌面 SkinFigureView 的 5 参形态）。
    /// </summary>
    public void SetAccount(string? type, string? uuid, string? username,
        string? externalBase = null, string? localSkinPath = null)
        => _ = LoadSkinAsync(type, uuid, username, externalBase, localSkinPath);

    // ── 皮肤：磁盘缓存 → 皮肤源（minotar / 认证服务器） → 内置 Steve ──

    private async Task LoadSkinAsync(string? type, string? uuid, string? username,
        string? externalBase, string? localSkinPath)
    {
        // 皮肤库里指定了本机皮肤：直接用它（离线账户、以及"先看效果"的场景）
        if (!string.IsNullOrWhiteSpace(localSkinPath) && File.Exists(localSkinPath))
        {
            byte[]? localPng = null;
            try { localPng = await File.ReadAllBytesAsync(localSkinPath); }
            catch (Exception ex) { LauncherLog.Error("player3d", "本地皮肤读取失败", ex); }
            if (localPng is not null)
            {
                _skinKey = "local_" + localSkinPath;
                _lastPng = localPng;
                _lastSlim = DetectSlim(localPng);
                if (_webReady) await PostSkinAsync(localPng, _lastSlim);
                return;
            }
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
        if (_skinKey == key && _skinApplied && _webReady) return;   // 同账户已在显示：跳过，避免整轮重载
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
                        LauncherLog.Error("player3d", $"皮肤下载失败 url={url}", ex);
                        try { File.Delete(file); } catch { }
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
            // 内置兜底：APK 资产里的 steve.png，解包到数据目录后按常规文件读
            var steve = AppAssets.ExtractToDataDir("Assets/steve.png", "steve.png");
            if (steve is null) return;
            try { png = await File.ReadAllBytesAsync(steve); }
            catch (Exception ex) { LauncherLog.Error("player3d", "内置 Steve 读取失败", ex); return; }
        }
        if (_skinKey != key) return;   // 期间已切换账户

        _lastPng = png;
        _lastSlim = DetectSlim(png);
        if (_webReady) await PostSkinAsync(png, _lastSlim);
    }

    /// <summary>
    /// 直接下发一张本地皮肤文件（皮肤库预览用）；null / 文件缺失 = 回落到内置 Steve。
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
        _ = LoadSkinAsync(null, null, null, null, path);
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
            LauncherLog.Error("player3d", "第三方皮肤地址解析失败", ex);
            return null;
        }
    }

    private async Task PostSkinAsync(byte[] png, bool slim)
    {
        // 皮肤就绪信号重置：同引擎页换肤后等新的 skin loaded 再恢复播放
        _skinApplied = false;
        var b64 = Convert.ToBase64String(png);
        await PostAsync(new { type = "skin", data = "data:image/png;base64," + b64, slim });
        // skin loaded 消息回来后由 WebMessageReceived 触发相位播放
    }

    // slim 版型检测：Core 解码后按皮肤库同一判定——64×64 皮肤在 (54,20) 处
    // slim 手臂为透明、宽臂不透明；旧版 64x32 皮肤无 slim，按比例映射不误判
    private static bool DetectSlim(byte[] png)
    {
        try
        {
            return SkinTexture.DetectSlim(PngDecoder.Decode(png));
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
            _web.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(msg));
        }
        catch (Exception ex)
        {
            LauncherLog.Error("player3d", "消息发送失败", ex);
        }
        await Task.CompletedTask;
    }

    private static string Sha1Prefix8(string s) =>
        Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(
            System.Text.Encoding.UTF8.GetBytes(s)), 0, 4).ToLowerInvariant();

    private static string Sanitize(string key)
    {
        var sb = new System.Text.StringBuilder(key.Length);
        foreach (var c in key) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }
}
