package cn.starcloud.launcher.web

import android.app.Activity
import android.os.Bundle
import android.util.Log

/**
 * debug 专用：直接调用 Web 桥的方法并把结果写进 logcat（tag = ScSelfTest）。
 *
 * 真机/模拟器走查时，UI 上的点击坐标在不同分辨率下不稳；这个活动把
 * `versions.install` / `launch.start` 这两条链路直接跑一遍，结果可 grep：
 *
 *   adb shell am start -n cn.starcloud.launcher.compose/cn.starcloud.launcher.web.BridgeSelfTestActivity \
 *       --es method versions.install --es params '{"id":"1.20.4"}'
 *
 * 只存在于 debug 构建（app/src/debug），release 包里没有这个类。
 */
class BridgeSelfTestActivity : Activity() {

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val method = intent?.getStringExtra("method") ?: "catalog.installed"
        // --es proxy http://127.0.0.1:8889 ：下载走 forward_proxy（配 adb reverse）
        intent?.getStringExtra("proxy")?.let { cn.starcloud.launcher.compose.data.DownloadDebug.proxyBase = it }
        // 参数用单独的 extra 拼，避免 adb shell 把 JSON 引号吃掉（踩过）
        val params = intent?.getStringExtra("params") ?: buildString {
            append("{")
            var first = true
            for (key in listOf("id", "player", "versionId", "kind", "fileName", "folderName")) {
                val v = intent?.getStringExtra(key) ?: continue
                if (!first) append(',')
                first = false
                append('"').append(key).append('"').append(':').append('"').append(v).append('"')
            }
            append("}")
        }

        val bridge = StarcloudBridge(this) { js ->
            // 桥的回调是一行 JS：window.__sc.reply("<id>","<json>")
            Log.i(TAG, "reply-js: ${js.take(400)}")
        }
        Log.i(TAG, "call $method params=$params")

        Thread {
            try {
                bridge.call("selftest", method, params)
            } catch (t: Throwable) {
                Log.e(TAG, "call failed", t)
            }
            Thread.sleep(1500)
            runOnUiThread { finish() }
        }.start()
    }

    private companion object {
        const val TAG = "ScSelfTest"
    }
}
