package cn.starcloud.launcher.web

import android.annotation.SuppressLint
import android.app.Activity
import android.os.Bundle
import android.view.ViewGroup
import android.webkit.WebView
import android.webkit.WebViewClient

/**
 * Web UI 壳：一个全屏 WebView 加载 `assets/web/index.html`。
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
