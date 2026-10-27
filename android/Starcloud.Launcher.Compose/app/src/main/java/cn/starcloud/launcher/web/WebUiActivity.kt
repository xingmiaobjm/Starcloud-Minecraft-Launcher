package cn.starcloud.launcher.web

import android.annotation.SuppressLint
import android.app.Activity
import android.os.Bundle
import android.view.ViewGroup
import android.webkit.WebChromeClient
import android.webkit.WebView
import android.webkit.WebViewClient

/**
 * Web UI 壳：一个全屏 WebView 加载 `assets/web/app/index.html`。
 *
 * 为什么不是 Electron：Electron 只能跑在桌面（它自带 Chromium + Node 主进程），
 * 安卓上跑不了。要做到「桌面 Electron 与安卓用同一套界面」，可行的做法是
 * **同一份 Web 前端 + 两个容器**：桌面上是 Electron，安卓上是这个 WebView 壳，
 * 两边都通过同一组桥接方法（`window.starcloud.*`）调用原生能力。
 *
 * 现阶段是 Phase 0：只验证 WebView ↔ Kotlin 通道，页面本身还是占位页；
 * Compose 界面保持原样，这个 Activity 不设 launcher 入口，用
 *   adb shell am start -n cn.starcloud.launcher.compose/cn.starcloud.launcher.web.WebUiActivity
 * 进入。
 */
class WebUiActivity : Activity() {

    private var webView: WebView? = null

    /** Web 侧路由是否还能后退（页面内部有路由，返回键应该先退页面而不是退出 Activity）。 */
    private val canGoBack: Boolean
        get() = webView?.canGoBack() == true

    @SuppressLint("SetJavaScriptEnabled")
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val view = WebView(this).apply {
            layoutParams = ViewGroup.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT
            )
            settings.javaScriptEnabled = true
            settings.allowFileAccess = true
            settings.domStorageEnabled = true
            // 资源从 assets 读，不需要跨域
            settings.allowFileAccessFromFileURLs = true

            // ── 视口与缩放：手机窄屏适配的前提 ──
            // 不设 useWideViewPort 时，WebView 按 980px 的默认视口渲染整页，
            // 桌面比例的界面会被整体缩小成「看不清的小字」；
            // 设了之后页面按设备宽度（CSS 像素）渲染，我们写的 @media 断点才会生效。
            settings.useWideViewPort = true
            settings.loadWithOverviewMode = true
            settings.setSupportZoom(false)
            settings.builtInZoomControls = false
            settings.displayZoomControls = false
            // 深色底：避免 Web 内容加载前闪白
            settings.forceDark = android.webkit.WebSettings.FORCE_DARK_ALLOW

            webChromeClient = WebChromeClient()
            webViewClient = WebViewClient()
            // 回调统一走主线程 + evaluateJavascript：@JavascriptInterface 的执行线程不是 UI 线程
            val self = this
            addJavascriptInterface(
                StarcloudBridge(this@WebUiActivity) { js ->
                    post { self.evaluateJavascript(js, null) }
                },
                "starcloud",
            )
            loadUrl("file:///android_asset/web/app/index.html")
        }
        webView = view
        setContentView(view)
    }

    /**
     * 返回键：页面内部还有历史时先退页面，没有了才退出 Activity。
     * 原来返回键直接退出 —— Web 前端是有内部路由的，一按就回到桌面，体验上像「应用崩了」。
     */
    @Suppress("DEPRECATION")
    override fun onBackPressed() {
        val wv = webView
        if (wv != null && wv.canGoBack()) {
            wv.goBack()
            return
        }
        @Suppress("DEPRECATION")
        super.onBackPressed()
    }

    /**
     * 切到后台时暂停 WebView 的定时器与动画：否则 Web 里的轮询会在游戏运行时继续跑，
     * 和游戏抢 CPU 与网络（设计稿里列的「WebView 与游戏抢资源」风险）。
     */
    override fun onPause() {
        webView?.apply {
            onPause()
            pauseTimers()
        }
        super.onPause()
    }

    override fun onResume() {
        super.onResume()
        webView?.apply {
            resumeTimers()
            onResume()
        }
    }

    override fun onDestroy() {
        // WebView 持有桥对象，销毁前先解绑，避免泄漏 Activity
        webView?.apply {
            removeJavascriptInterface("starcloud")
            destroy()
        }
        webView = null
        super.onDestroy()
    }
}
