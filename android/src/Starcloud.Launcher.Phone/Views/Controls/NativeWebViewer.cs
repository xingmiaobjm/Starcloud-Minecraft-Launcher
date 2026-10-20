// 原生网页承载控件（Uno Skia 渲染器专用）：
// Skia 渲染器下 Microsoft.UI.Xaml.Controls.WebView 与 Microsoft.Web.WebView2 门面全部
// 未实现（Uno0001），参照 GameSurfaceHost 的官方契约（using-skia-hosting-native-controls）
// 把 global::Android.Webkit.WebView 直接设为 ContentControl.Content 嵌进视觉树。
//
// 消息桥（对齐桌面网页控件的 WebMessage 语义）：
//   JS→C#：AddJavascriptInterface 注入原生桥对象 __scHostBridge；垫片脚本把
//     window.chrome.webview.postMessage 重定向到该桥，桥回调经 DispatcherQueue 回 UI
//     线程后抛 WebMessageReceived（消息以字符串形态送达）。
//   C#→JS：PostWebMessageAsJson 经 evaluateJavascript 调垫片安装的 window.__scHostDispatch，
//     由它 JSON.parse 后分发给 chrome.webview 的 message 监听者（e.data = 解析后的对象）。
//   垫片必须在页面脚本之前生效（如 3d/player.html 顶层就调用 chrome.webview.addEventListener，
//   垫片晚到会让页面脚本直接崩掉），而 OnPageStarted 里注入存在竞态，所以 file:// 页面不走
//   LoadUrl：读出内容、把垫片插进 <head> 开标签后 LoadDataWithBaseURL；http(s) 页面
//   （登录页等）不需要消息桥，直接 LoadUrl。
//
// 线程说明：Android WebView 的方法必须在主（UI）线程调用；使用方都在 UI 线程操作本控件，
// 不做额外封送。唯一的例外是 JS 桥回调天然跑在 WebView 的 JS 线程，已在桥内回投 UI 线程。
// 命名空间避坑：裸写 Android.* 会解析到本项目 Starcloud.Android.*，一律 global:: 限定。

using System.Text;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.UI;

namespace Starcloud.Launcher.Phone.Views.Controls;

public class NativeWebViewer : ContentControl
{
    /// <summary>JS 桥对象在页面里的名字（双下划线开头，避免与页面脚本撞名）。</summary>
    private const string BridgeName = "__scHostBridge";

    private global::Android.Webkit.WebView? _native;
    private Windows.UI.Color _background = Microsoft.UI.Colors.White;   // 与桌面网页控件默认白底一致

    /// <summary>页面背景色（对应桌面 DefaultBackgroundColor；皮肤 3D 预览要设透明）。</summary>
    public Windows.UI.Color DefaultBackgroundColor
    {
        get => _background;
        set { _background = value; if (_native is not null) ApplyBackground(_native); }
    }

    /// <summary>JS→C# 消息（chrome.webview.postMessage 经原生桥送达，等价桌面 WebMessageReceived）。</summary>
    public event EventHandler<string>? WebMessageReceived;

    /// <summary>页面加载完成（Android OnPageFinished，等价桌面 NavigationCompleted）。</summary>
    public event EventHandler? NavigationCompleted;

    public NativeWebViewer()
    {
        // ContentPresenter 默认贴左上，拉伸对齐才能铺满外层容器（同 GameSurfaceHost）
        HorizontalContentAlignment = HorizontalAlignment.Stretch;
        VerticalContentAlignment = VerticalAlignment.Stretch;
        Loaded += (_, _) => EnsureCreated();
        Unloaded += (_, _) => DestroyNative();
    }

    /// <summary>
    /// 加载页面。http(s) 直接加载；file:// 与 file:///android_asset/ 读出内容注入垫片后按原 URL
    /// 为基加载（垫片要先于页面脚本生效）。须在 UI 线程调用。
    /// </summary>
    public void Load(Uri uri)
    {
        EnsureCreated();
        var url = uri.ToString();
        if (uri.Scheme is "http" or "https" || TryReadLocalText(uri) is not { } html)
        {
            // http(s) 不需要消息桥；file 读不到（异常/编码问题）退回原样加载，只损失桥功能
            _native!.LoadUrl(url);
            return;
        }
        var data = InjectShim(html);
        // 原生 HTML + utf-8 直载（LoadDataWithBaseURL 无 LoadData 的 # 截断坑）。
        // 不用 base64：部分厂商 WebView（实测华为 com.huawei.webview）会把 base64
        // 载荷当纯文本整段显示，页面根本不解析。
        _native!.LoadDataWithBaseURL(url, data, "text/html", "utf-8", url);
    }

    /// <summary>执行一段 JS（返回值忽略）。须在 UI 线程调用。</summary>
    public void EvaluateJavaScript(string script)
    {
        if (_native is null) return;
        _native.EvaluateJavascript(script, null);
    }

    /// <summary>向页面发 JSON 消息：垫片把它还原成对象分发给 chrome.webview 的 message 监听者。须在 UI 线程调用。</summary>
    public void PostWebMessageAsJson(string json)
    {
        if (_native is null || string.IsNullOrEmpty(json)) return;
        // JSON 文本作为 JS 字符串字面量传给垫片函数，由它 JSON.parse 还原成消息对象
        EvaluateJavaScript("window.__scHostDispatch && window.__scHostDispatch(" + JsStringLiteral(json) + ");");
    }

    /// <summary>停止当前加载。须在 UI 线程调用。</summary>
    public void Stop() => _native?.StopLoading();

    // ── 原生视图生命周期 ──

    /// <summary>按需创建原生 WebView（Loaded 或首次使用时；Unloaded 销毁后再次使用会重建）。</summary>
    private void EnsureCreated()
    {
        if (_native is not null) return;
        // 视图经 Uno 原生宿主挂进窗口，用 Application.Context 即可（同 GameSurfaceHost）
        var web = new global::Android.Webkit.WebView(global::Android.App.Application.Context);
        var s = web.Settings;
        s.JavaScriptEnabled = true;
        s.DomStorageEnabled = true;
        // 插件 1.x 面板是 file:// 页面，要能 fetch 同目录本地资源；Android API 30+ 起
        // AllowFileAccess 默认 false，必须显式打开（含 fromFileURLs / universal 两条跨域读）
        s.AllowFileAccess = true;
        s.AllowFileAccessFromFileURLs = true;
        s.AllowUniversalAccessFromFileURLs = true;
        s.MediaPlaybackRequiresUserGesture = false;
        // 桌面网页控件默认接受第三方 Cookie（微软登录页依赖）；安卓 WebView 默认拒绝，显式对齐
        // （新版 Mono.Android 绑定把 CookieManager.Instance 标为可空，单例语义下不会为 null）
        global::Android.Webkit.CookieManager.Instance!.SetAcceptThirdPartyCookies(web, true);
        ApplyBackground(web);
        web.SetWebViewClient(new PageClient(this));
        web.AddJavascriptInterface(new JsBridge(this), BridgeName);
        _native = web;
        Content = web;
    }

    private void ApplyBackground(global::Android.Webkit.WebView web)
    {
        var c = _background;
        web.SetBackgroundColor(global::Android.Graphics.Color.Argb(c.A, c.R, c.G, c.B));
    }

    /// <summary>Unloaded 时销毁原生 WebView 防泄漏；重新 Loaded / 再次使用时 EnsureCreated 重建。</summary>
    private void DestroyNative()
    {
        if (_native is null) return;
        // 先从 Uno 原生宿主摘除再 Destroy（Destroy 要求视图已脱离视图树）；全程在 UI 线程
        Content = null;
        _native.StopLoading();
        _native.Destroy();
        _native = null;
    }

    /// <summary>页面加载回调（WebViewClient 回调都在 UI 线程）。</summary>
    private sealed class PageClient : global::Android.Webkit.WebViewClient
    {
        private readonly NativeWebViewer _owner;
        public PageClient(NativeWebViewer owner) => _owner = owner;

        public override void OnPageFinished(global::Android.Webkit.WebView? view, string? url)
            => _owner.NavigationCompleted?.Invoke(_owner, EventArgs.Empty);
    }

    /// <summary>JS 桥：注入为 window.__scHostBridge，垫片把 chrome.webview.postMessage 转发进来。</summary>
    private sealed class JsBridge : Java.Lang.Object
    {
        private readonly NativeWebViewer _owner;
        public JsBridge(NativeWebViewer owner) => _owner = owner;

        [global::Android.Webkit.JavascriptInterface]
        [global::Java.Interop.Export("postMessage")]
        public void PostMessage(string? message)
        {
            // 桥回调在 WebView 的 JS 线程：回投 UI 线程再抛事件（使用方要摸 XAML 与定时器）
            var msg = message ?? "";
            _owner.DispatcherQueue.TryEnqueue(() => _owner.WebMessageReceived?.Invoke(_owner, msg));
        }
    }

    // ── file:// 读取与垫片注入 ──

    /// <summary>读本地页面文本：android_asset 走 AssetManager（APK 内，System.IO 读不到），其余按文件路径。</summary>
    private static string? TryReadLocalText(Uri uri)
    {
        try
        {
            const string assetPrefix = "file:///android_asset/";
            if (uri.AbsoluteUri.StartsWith(assetPrefix, StringComparison.OrdinalIgnoreCase))
            {
                using var stream = global::Android.App.Application.Context.Assets!.Open(uri.AbsoluteUri[assetPrefix.Length..]);
                using var reader = new StreamReader(stream);
                return reader.ReadToEnd();
            }
            return File.ReadAllText(uri.LocalPath);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把垫片插到 head 开标签之后（保住 DOCTYPE 的标准模式渲染）；没有 head 就拼在最前。</summary>
    private static string InjectShim(string html)
    {
        var idx = html.IndexOf("<head", StringComparison.OrdinalIgnoreCase);
        if (idx >= 0)
        {
            var gt = html.IndexOf('>', idx);
            if (gt >= 0) return html.Insert(gt + 1, ShimScript);
        }
        return ShimScript + html;
    }

    /// <summary>JS 字符串字面量（含引号）：转义引号/反斜杠/控制符与 U+2028/2029。</summary>
    private static string JsStringLiteral(string s)
    {
        var sb = new StringBuilder(s.Length + 16);
        sb.Append('"');
        foreach (var ch in s)
        {
            switch (ch)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < ' ' || ch == '\u2028' || ch == '\u2029')
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    /// <summary>垫片：在页面脚本之前把 chrome.webview 伪装出来并安装 C#→JS 分发入口。</summary>
    private const string ShimScript =
        """
        <script>(function () {
          if (window.__scHostDispatch) return;
          var bridge = window.__scHostBridge;   // AddJavascriptInterface 注入的原生桥（页面脚本前已就位）
          var listeners = [];
          var chrome = window.chrome || {};
          // 模拟桌面 chrome.webview：页面的 postMessage 与 message 监听语义原样保留
          chrome.webview = {
            addEventListener: function (t, cb) { if (t === "message") listeners.push(cb); },
            removeEventListener: function (t, cb) { var i = listeners.indexOf(cb); if (i >= 0) listeners.splice(i, 1); },
            postMessage: function (msg) {
              try { bridge.postMessage(typeof msg === "string" ? msg : JSON.stringify(msg)); } catch (e) { }
            },
          };
          window.chrome = chrome;
          // C#→JS：PostWebMessageAsJson 调本函数，把解析后的对象按桌面语义分发给监听者
          window.__scHostDispatch = function (json) {
            var data = json;
            try { data = JSON.parse(json); } catch (e) { }
            for (var i = 0; i < listeners.length; i++) { try { listeners[i]({ data: data }); } catch (e) { } }
          };
        })();</script>
        """;
}
