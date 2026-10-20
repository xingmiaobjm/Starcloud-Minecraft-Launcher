package cn.starcloud.launcher.web

import android.content.Context
import android.os.Build
import android.util.Log
import android.webkit.JavascriptInterface
import cn.starcloud.launcher.compose.data.InstallState
import cn.starcloud.launcher.compose.data.ManifestRepository
import cn.starcloud.launcher.compose.data.VersionDownloader
import cn.starcloud.launcher.compose.game.GameLauncher
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.util.concurrent.Executors

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
    /** 长任务线程：安装与启动都不能占住 JS 调用线程 */
    private val worker = Executors.newSingleThreadExecutor { r ->
        Thread(r, "starcloud-bridge").apply { isDaemon = true }
    }
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
        val params = runCatching { JSONObject(paramsJson.ifEmpty { "{}" }) }.getOrElse { JSONObject() }

        // 长任务：先认领 id，再在线程池里跑，结束时（或进度中）自己回推
        when (method) {
            "versions.install" -> { worker.execute { installVersion(id, params) }; return }
            "launch.start" -> { worker.execute { launchGame(id, params) }; return }
            "install.cancel" -> { InstallState.cancel(); reply(id, "{}"); return }
        }

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

    // ── versions.install：复用 VersionDownloader，进度走 InstallState 轮询 ──
    private fun installVersion(callId: String, params: JSONObject) {
        val versionId = params.optString("id")
        if (versionId.isEmpty()) { reply(callId, errorJson("bad-request", "缺少版本 id")); return }
        try {
            val cache = File(filesRoot, "cache/version_manifest.json")
            val repo = ManifestRepository(cache)
            val entries = repo.cached() ?: repo.fetch()
            val entry = entries.firstOrNull { it.id == versionId }
                ?: run { reply(callId, errorJson("not-found", "清单里没有 $versionId")); return }

            // 安装期间每 200ms 推一次进度，页面不需要轮询
            val poller = Thread {
                var last = ""
                while (InstallState.version != null) {
                    val line = "${InstallState.stage}|${InstallState.done}|${InstallState.total}|${InstallState.file}"
                    if (line != last) {
                        last = line
                        emit("install.progress", JSONObject().apply {
                            put("version", InstallState.version ?: versionId)
                            put("stage", InstallState.stage)
                            put("file", InstallState.file)
                            put("done", InstallState.done)
                            put("total", InstallState.total)
                        }.toString())
                    }
                    try { Thread.sleep(200) } catch (e: InterruptedException) { break }
                }
            }.apply { isDaemon = true; start() }

            VersionDownloader(filesRoot).downloadVersion(entry)
            poller.join(2000)

            val msg = InstallState.message
            reply(callId, if (msg != null && msg.startsWith("失败")) errorJson("install-failed", msg)
            else JSONObject().put("ok", true).put("id", versionId).toString())
        } catch (t: Throwable) {
            reply(callId, errorJson("install-failed", t.message ?: t.javaClass.simpleName))
        }
    }

    // ── launch.start：复用 GameLauncher（组件部署 + JVM 拉起 + SDL 活动） ──
    private fun launchGame(callId: String, params: JSONObject) {
        val versionId = params.optString("id")
        if (versionId.isEmpty()) { reply(callId, errorJson("bad-request", "缺少版本 id")); return }
        val player = params.optString("player").ifEmpty { "Player" }
        try {
            GameLauncher.launch(context, versionId, player, object : GameLauncher.Listener {
                override fun onLog(line: String) = emit("game.log", JSONObject().put("line", line).toString())
                override fun onProgress(phase: String) = emit("game.progress", JSONObject().put("phase", phase).toString())
                override fun onFinished(exitCode: Int, error: String?) {
                    reply(callId, if (error != null) errorJson("launch-failed", error)
                    else JSONObject().put("ok", true).put("exitCode", exitCode).toString())
                }
            })
        } catch (t: Throwable) {
            reply(callId, errorJson("launch-failed", t.message ?: t.javaClass.simpleName))
        }
    }

    private fun reply(callId: String, resultJson: String) {
        invokeJs("window.__sc&&window.__sc.reply(${JSONObject.quote(callId)},${JSONObject.quote(resultJson)})")
    }

    /** 主动推送事件（下载进度等） */
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
