package cn.starcloud.launcher.web

import android.content.Context
import android.os.Build
import android.util.Log
import android.webkit.JavascriptInterface
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

/**
 * Web UI ↔ Kotlin 的桥。页面里通过 `window.starcloud.xxx()` 调用，
 * 返回值一律是 JSON 字符串（避免 WebView 把对象转成 "[object Object]"）。
 *
 * 这一层刻意保持"薄"：只做参数校验、调用既有仓储、序列化结果。
 * 真正的逻辑仍然留在 compose/data 与 compose/game 里，Compose 与 Web 两个壳共用。
 */
class StarcloudBridge(private val context: Context) {

    private val filesRoot: File
        get() = context.getExternalFilesDir(null) ?: context.filesDir

    private val versionsDir: File
        get() = File(File(filesRoot, ".minecraft"), "versions")

    /** 应用信息：版本号与平台，页面用来渲染标题栏 */
    @JavascriptInterface
    fun appInfo(): String = JSONObject().apply {
        put("name", "Starcloud")
        put("version", appVersion())
        put("platform", "android")
        put("sdk", Build.VERSION.SDK_INT)
        put("root", filesRoot.absolutePath)
    }.toString()

    /** 已安装版本列表：目录名即实例名，和 Windows 端 / 版本页同一套口径 */
    @JavascriptInterface
    fun versions(): String {
        val arr = JSONArray()
        val dir = versionsDir
        if (dir.isDirectory) {
            dir.listFiles()
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
        }
        return JSONObject().put("versions", arr).toString()
    }

    /** 页面日志落到 logcat，tag 固定，方便 adb logcat -s StarcloudWeb 过滤 */
    @JavascriptInterface
    fun log(message: String) {
        Log.i(TAG, message)
    }

    /** 版本号取自 PackageInfo：这个模块没开 BuildConfig 生成 */
    private fun appVersion(): String = try {
        context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "0.0.0"
    } catch (e: Exception) {
        "0.0.0"
    }

    private companion object {
        const val TAG = "StarcloudWeb"
    }
}
