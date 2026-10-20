package cn.starcloud.launcher.web

import android.content.Context
import android.os.Build
import android.util.Log
import android.webkit.JavascriptInterface
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * Web UI ↔ Kotlin 的桥。
 *
 * 协议（与 Electron 的 preload 对齐，页面代码两边共用）：
 *   JS → 原生：starcloud.call(id, method, paramsJson)
 *   原生 → JS：window.__sc.reply(id, resultJson)
 *              window.__sc.event(eventName, paramsJson)   // 进度、日志等推送
 *
 * 所有 @JavascriptInterface 方法都必须快速返回：耗时操作放到线程池，
 * 结果通过 [invokeJs] 回推（Activity 里包一层 webView.post + evaluateJavascript）。
 */
class StarcloudBridge(
    private val context: Context,
    private val invokeJs: (String) -> Unit,
) {
    private val filesRoot: File
        get() = context.getExternalFilesDir(null) ?: context.filesDir

    private val versionsDir: File
        get() = File(File(filesRoot, ".minecraft"), "versions")

    /**
     * 统一入口。method 用点号分组的小写名，参数与返回值都是 JSON。
     * 目前只实现只读的几个；写操作（安装 / 启动）在 P2 接上。
     */
    @JavascriptInterface
    fun call(id: String, method: String, paramsJson: String) {
        val result = try {
            when (method) {
                "app.info" -> appInfo()
                "versions.list" -> versions()
                "ui.log" -> {
                    val msg = runCatching { JSONObject(paramsJson).optString("message") }.getOrNull()
                    if (!msg.isNullOrEmpty()) Log.i(TAG, msg)
                    "{}"
                }
                else -> errorJson("unsupported", "安卓端暂未实现 $method")
            }
        } catch (t: Throwable) {
            Log.w(TAG, "call $method failed", t)
            errorJson("internal", t.message ?: t.javaClass.simpleName)
        }
        invokeJs("window.__sc&&window.__sc.reply(${JSONObject.quote(id)},${JSONObject.quote(result)})")
    }

    /** 主动推送事件（下载进度等）；P2 会用到，先留出通道 */
    fun emit(event: String, payloadJson: String) {
        invokeJs("window.__sc&&window.__sc.event(${JSONObject.quote(event)},${JSONObject.quote(payloadJson)})")
    }

    private fun appInfo(): String = JSONObject().apply {
        put("name", "Starcloud")
        put("version", appVersion())
        put("platform", "android")
        put("sdk", Build.VERSION.SDK_INT)
        put("root", filesRoot.absolutePath)
    }.toString()

    /** 已安装版本：目录名即实例名，与桌面端版本页同一口径 */
    private fun versions(): String {
        val arr = JSONArray()
        versionsDir.takeIf { it.isDirectory }?.listFiles()
            ?.filter { it.isDirectory && File(it, "${it.name}.json").exists() }
            ?.sortedBy { it.name.lowercase() }
            ?.forEach { v ->
                val jar = File(v, "${v.name}.jar")
                arr.put(
                    JSONObject().apply {
                        put("id", v.name)
                        put("sizeBytes", if (jar.exists()) jar.length() else 0L)
                    }
                )
            }
        return JSONObject().put("versions", arr).toString()
    }

    /** 版本号取自 PackageInfo：这个模块没开 BuildConfig 生成 */
    private fun appVersion(): String = try {
        context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "0.0.0"
    } catch (e: Exception) {
        "0.0.0"
    }

    private fun errorJson(code: String, message: String): String = JSONObject().apply {
        put("error", JSONObject().apply {
            put("code", code)
            put("message", message)
        })
    }.toString()

    private companion object {
        const val TAG = "StarcloudWeb"
    }
}
