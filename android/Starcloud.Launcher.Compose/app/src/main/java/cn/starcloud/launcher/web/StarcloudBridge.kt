package cn.starcloud.launcher.web

import android.content.Context
import android.os.Build
import android.util.Log
import android.webkit.JavascriptInterface
import cn.starcloud.launcher.compose.data.InstallState
import cn.starcloud.launcher.compose.data.LauncherPrefs
import cn.starcloud.launcher.compose.data.VersionsRepository
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
            "versions.install", "install.vanilla" -> { worker.execute { installVersion(id, params) }; return }
            "launch.start" -> { worker.execute { launchGame(id, params) }; return }
            "install.cancel" -> { InstallState.cancel(); reply(id, "{}"); return }
        }

        val result = try {
            when (method) {
                "app.info" -> appInfo()
                "versions.list", "catalog.installed" -> versions()
                "catalog.versions" -> remoteVersions()
                "versions.delete" -> deleteVersion(params)
                "saves.list" -> saves()
                "saves.delete" -> deleteSave(params)
                "saves.backup" -> backupSave(params)
                "version.detail" -> versionDetail(params)
                "version.toggleContent" -> toggleContent(params)
                "version.deleteContent" -> deleteContent(params)
                "versions.setDefault" -> setDefaultVersion(params)
                "java.list" -> javaList()
                "projections.scan" -> projectionsScan()
                "projections.delete" -> projectionsDelete(params)
                "accounts.list" -> accountsList()
                "accounts.addOffline" -> {
                    val name = params.optString("name").ifEmpty { params.optString("playerName") }
                    if (name.isNotBlank()) LauncherPrefs.setPlayerName(context, name)
                    "{}"
                }
                // 单账号模型：切换/删除都保持现状（页面随后会重新拉 accounts.list）
                "accounts.setCurrent", "accounts.remove" -> "{}"
                // 安卓端没有插件运行时：给空列表，页面显示"暂无插件"而不是报错
                "plugins.list" -> "{\"plugins\":[]}"
                "plugins.setEnabled" -> "{}"
                "settings.get" -> settingsGet()
                "settings.set", "settings.apply" -> settingsWrite(params)
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

    // ── settings：字段名与 electron 渲染层的 SettingsSnapshot 对齐（camelCase） ──
    private fun settingsGet(): String = JSONObject().apply {
        put("gameDir", File(filesRoot, ".minecraft").absolutePath)
        put("javaExecutable", "")            // 安卓的 JVM 由组件包拉起，没有独立可执行路径
        put("maxMemoryMb", LauncherPrefs.maxMemoryMb(context))
        put("downloadSource", "bmclapi")     // 与桌面端默认源一致
        // 安卓侧额外字段（桌面端没有，页面按需读取）
        put("playerName", LauncherPrefs.playerName(context))
        put("theme", LauncherPrefs.theme(context))
        put("platform", "android")
    }.toString()

    /** 写回：认识的键落盘，其余忽略；返回写入后的完整快照 */
    private fun settingsWrite(params: JSONObject): String {
        if (params.has("playerName")) {
            LauncherPrefs.setPlayerName(context, params.optString("playerName"))
        }
        if (params.has("theme")) {
            LauncherPrefs.setTheme(context, params.optString("theme"))
        }
        if (params.has("maxMemoryMb")) {
            LauncherPrefs.setMaxMemoryMb(context, params.optInt("maxMemoryMb"))
        }
        return settingsGet()
    }

    // ── catalog.installed：已安装版本（含类型与时间，渲染层按这些字段排序/标注） ──
    private fun installedVersions(): String {
        val repo = VersionsRepository(filesRoot)
        val arr = JSONArray()
        repo.list().forEach { v ->
            arr.put(JSONObject().apply {
                put("id", v.id)
                put("type", v.type)
                put("sizeBytes", v.sizeBytes)
                put("lastPlayedAt", java.time.Instant.ofEpochMilli(v.lastModified).toString())
            })
        }
        return JSONObject().put("installed", arr).toString()
    }

    // ── catalog.versions：官方清单（缓存优先，离线也能列出） ──
    private fun remoteVersions(): String {
        val repo = ManifestRepository(File(filesRoot, "cache/version_manifest.json"))
        val list = try { repo.cached() ?: repo.fetch() } catch (e: Exception) { emptyList() }
        val arr = JSONArray()
        list.forEach { e ->
            arr.put(JSONObject().apply {
                put("id", e.id)
                put("type", e.type)
                put("url", e.url)
                put("releaseTime", e.releaseTime)
            })
        }
        // 渲染层的 VersionCatalog 同时要 remote 与 installed（下载页会 installed.map 过滤已装）
        return JSONObject().apply {
            put("remote", arr)
            put("installed", JSONObject(installedVersions()).getJSONArray("installed"))
        }.toString()
    }

    // ── versions.delete：算一下删前大小，删完把释放的字节数回给页面 ──
    private fun deleteVersion(params: JSONObject): String {
        val id = params.optString("versionId").ifEmpty { params.optString("id") }
        if (id.isEmpty()) return errorJson("bad-request", "缺少版本 id")
        val repo = VersionsRepository(filesRoot)
        val before = repo.list().firstOrNull { it.id == id }?.sizeBytes ?: 0L
        repo.remove(id)
        return JSONObject().apply {
            put("deleted", !File(repo.versionsDir, id).exists())
            put("freedBytes", before)
            put("wasDefault", false)
        }.toString()
    }

    // ── saves.list：扫描 .minecraft/saves，字段对齐渲染层的 SaveEntry ──
    private fun saves(): String {
        val dir = File(File(filesRoot, ".minecraft"), "saves")
        val arr = JSONArray()
        dir.listFiles { f -> f.isDirectory }?.forEach { world ->
            val size = world.walkTopDown().filter { it.isFile }.sumOf { it.length() }
            arr.put(JSONObject().apply {
                put("folderName", world.name)
                put("levelName", world.name)
                put("gameRoot", File(filesRoot, ".minecraft").absolutePath)
                put("versionId", "")
                put("isolated", false)
                put("sizeBytes", size)
                put("lastPlayedAt", java.time.Instant.ofEpochMilli(world.lastModified()).toString())
            })
        }
        return JSONObject().put("saves", arr).toString()
    }

    // ── projections：schematics / config/litematica 两个约定目录 ──
    private val projectionExts = setOf(".litematic", ".nbt", ".schem", ".schematic")

    private fun projectionsScan(): String {
        val dotMc = File(filesRoot, ".minecraft")
        val dirs = listOf(File(dotMc, "schematics"), File(dotMc, "config/litematica"))
        val files = JSONArray()
        dirs.filter { it.isDirectory }.forEach { dir ->
            dir.listFiles { f -> f.isFile && projectionExts.any { f.name.endsWith(it, true) } }
                ?.sortedByDescending { it.lastModified() }
                ?.forEach { f ->
                    files.put(JSONObject().apply {
                        put("filePath", f.absolutePath)
                        put("fileName", f.nameWithoutExtension)
                        put("sizeBytes", f.length())
                        put("modifiedAt", java.time.Instant.ofEpochMilli(f.lastModified()).toString())
                        put("versionId", "")   // 安卓端不做版本隔离，投影放全局目录
                    })
                }
        }
        val versions = JSONArray()
        VersionsRepository(filesRoot).list().forEach { versions.put(it.id) }
        return JSONObject().put("files", files).put("versions", versions).toString()
    }

    private fun projectionsDelete(params: JSONObject): String {
        val path = params.optString("filePath").ifEmpty { params.optString("path") }
        if (path.isEmpty()) return errorJson("bad-request", "缺少文件路径")
        // 只允许删投影目录里的文件：防止页面传任意路径过来
        val file = File(path).canonicalFile
        val dotMc = File(filesRoot, ".minecraft").canonicalFile
        val allowed = listOf(File(dotMc, "schematics"), File(dotMc, "config/litematica"))
            .map { it.canonicalPath }
            .any { file.path.startsWith(it) }
        if (!allowed) return errorJson("forbidden", "只能删除投影目录内的文件")
        val ok = file.isFile && file.delete()
        return JSONObject().put("ok", ok).toString()
    }

    // ── version.detail：版本 JSON + 实例目录里的内容文件 ──
    private fun versionDetail(params: JSONObject): String {
        val id = params.optString("versionId").ifEmpty { params.optString("id") }
        if (id.isEmpty()) return errorJson("bad-request", "缺少版本 id")

        val versionDir = File(versionsDir, id)
        val jsonFile = File(versionDir, "$id.json")
        if (!jsonFile.isFile) return errorJson("not-found", "没有已安装的版本 $id")

        val root = JSONObject()
        var mc = id
        var loader = ""
        try {
            val json = JSONObject(jsonFile.readText())
            json.optString("inheritsFrom").takeIf { it.isNotEmpty() }?.let { mc = it }
            val probe = "$id ${json.optString("mainClass")}".lowercase()
            loader = when {
                "neoforge" in probe -> "neoforge"
                "forge" in probe -> "forge"
                "quilt" in probe -> "quilt"
                "fabric" in probe -> "fabric"
                else -> "vanilla"
            }
        } catch (_: Exception) {
            // JSON 坏了也照样把目录内容列出来，页面至少能看文件
        }

        return JSONObject().apply {
            put("versionId", id)
            put("gameRoot", versionDir.absolutePath)
            put("isolated", false)
            put("mcVersion", mc)
            put("loader", loader)
            put("mods", contentFiles(File(versionDir, "mods")))
            put("resourcepacks", contentFiles(File(versionDir, "resourcepacks")))
            put("shaderpacks", contentFiles(File(versionDir, "shaderpacks")))
        }.toString()
    }

    /** kind → 实例目录名 */
    private fun kindDir(kind: String): String? = when (kind) {
        "mod" -> "mods"
        "resourcepack" -> "resourcepacks"
        "shader" -> "shaderpacks"
        else -> null
    }

    private fun versionContentDir(params: JSONObject): File? {
        val id = params.optString("versionId").ifEmpty { params.optString("id") }
        val dir = kindDir(params.optString("kind")) ?: return null
        if (id.isEmpty() || id.contains("..")) return null
        return File(File(versionsDir, id), dir)
    }

    // 启用/停用 = 改名加/去 .disabled（与桌面端同一规则）
    private fun toggleContent(params: JSONObject): String {
        val dir = versionContentDir(params) ?: return errorJson("bad-request", "版本或类型不对")
        val fileName = params.optString("fileName")
        val wantEnabled = params.optBoolean("enabled", true)
        val current = File(dir, fileName)
        val target = if (wantEnabled) File(dir, fileName.removeSuffix(".disabled"))
        else File(dir, fileName.removeSuffix(".disabled") + ".disabled")
        if (!current.isFile) return errorJson("not-found", "找不到 $fileName")
        if (!current.renameTo(target)) return errorJson("io", "改名失败：$fileName")
        return JSONObject().put("files", contentFiles(dir)).toString()
    }

    private fun deleteContent(params: JSONObject): String {
        val dir = versionContentDir(params) ?: return errorJson("bad-request", "版本或类型不对")
        val fileName = params.optString("fileName")
        val file = File(dir, fileName)
        if (!file.isFile) return errorJson("not-found", "找不到 $fileName")
        if (!file.delete()) return errorJson("io", "删除失败：$fileName")
        return JSONObject().put("files", contentFiles(dir)).toString()
    }

    private fun setDefaultVersion(params: JSONObject): String {
        val id = params.optString("versionId").ifEmpty { params.optString("id") }
        if (id.isEmpty()) return errorJson("bad-request", "缺少版本 id")
        LauncherPrefs.setLastVersion(context, id)
        return JSONObject().put("ok", true).put("lastVersion", id).toString()
    }

    private fun contentFiles(dir: File): JSONArray {
        val arr = JSONArray()
        dir.listFiles { f -> f.isFile }?.sortedBy { it.name.lowercase() }?.forEach { f ->
            val enabled = !f.name.endsWith(".disabled", ignoreCase = true)
            arr.put(JSONObject().apply {
                put("fileName", f.name)
                put("displayName", if (enabled) f.name else f.name.removeSuffix(".disabled"))
                put("enabled", enabled)
                put("sizeBytes", f.length())
                put("path", f.absolutePath)
            })
        }
        return arr
    }

    // ── java.list：组件目录里的 JRE（组件包由 fetch-components 下载） ──
    private fun javaList(): String {
        val arr = JSONArray()
        File(filesRoot, "components").listFiles { f -> f.isDirectory && f.name.startsWith("jre") }
            ?.sortedBy { it.name }
            ?.forEach { jre ->
                val major = Regex("(\\d+)").find(jre.name)?.groupValues?.get(1)?.toIntOrNull() ?: 0
                arr.put(JSONObject().apply {
                    put("versionString", jre.name)
                    put("majorVersion", major)
                })
            }
        return JSONObject().put("runtimes", arr).toString()
    }

    // ── accounts.list：安卓端目前只有离线账号（设置里的用户名） ──
    private fun accountsList(): String {
        val name = LauncherPrefs.playerName(context)
        val uuid = java.util.UUID.nameUUIDFromBytes("OfflinePlayer:$name".toByteArray()).toString()
        val accounts = JSONArray().put(JSONObject().apply {
            put("uuid", uuid)
            put("name", name)
            put("type", "offline")
            put("isDefault", true)
            put("hasSession", true)
        })
        return JSONObject().put("accounts", accounts).put("current", uuid).toString()
    }

    // ── saves.delete / backups：直接对存档目录动手 ──
    private fun saveDir(params: JSONObject): File? {
        val folder = params.optString("folderName").ifEmpty { params.optString("folder") }
        if (folder.isEmpty() || folder.contains("..")) return null
        return File(File(File(filesRoot, ".minecraft"), "saves"), folder).takeIf { it.isDirectory }
    }

    private fun deleteSave(params: JSONObject): String {
        val dir = saveDir(params) ?: return errorJson("not-found", "找不到该存档")
        val freed = dir.walkTopDown().filter { it.isFile }.sumOf { it.length() }
        dir.deleteRecursively()
        return JSONObject().put("ok", !dir.exists()).put("freedBytes", freed).toString()
    }

    private fun backupSave(params: JSONObject): String {
        val dir = saveDir(params) ?: return errorJson("not-found", "找不到该存档")
        val out = File(File(context.cacheDir, "backup"), "${dir.name}-${System.currentTimeMillis()}.zip")
        out.parentFile?.mkdirs()
        java.util.zip.ZipOutputStream(out.outputStream().buffered()).use { zip ->
            dir.walkTopDown().filter { it.isFile }.forEach { f ->
                val rel = f.relativeTo(dir).path.replace('\\', '/')
                zip.putNextEntry(java.util.zip.ZipEntry(rel))
                f.inputStream().use { it.copyTo(zip) }
                zip.closeEntry()
            }
        }
        return JSONObject().put("ok", true).put("path", out.absolutePath).put("sizeBytes", out.length()).toString()
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

            // VersionDownloader 的失败文案是「安装失败：…」，不能用 startsWith 判断
            val msg = InstallState.message
            val failed = msg != null && msg.contains("失败")
            reply(callId, if (failed) errorJson("install-failed", msg!!)
            else JSONObject().put("ok", true).put("id", versionId).put("message", msg ?: "").toString())
        } catch (t: Throwable) {
            reply(callId, errorJson("install-failed", t.message ?: t.javaClass.simpleName))
        }
    }

    // ── launch.start：复用 GameLauncher（组件部署 + JVM 拉起 + SDL 活动） ──
    private fun launchGame(callId: String, params: JSONObject) {
        val versionId = params.optString("id")
        if (versionId.isEmpty()) { reply(callId, errorJson("bad-request", "缺少版本 id")); return }
        // 页面没传就用设置里的离线用户名（与 Compose 壳同一份偏好）
        val player = params.optString("player").trim()
            .ifEmpty { LauncherPrefs.playerName(context) }
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
        // catalog.installed 与 versions.list 共用：渲染层读 installed，老页面读 versions
        val json = JSONObject(installedVersions())
        val arr = json.getJSONArray("installed")
        return JSONObject().put("installed", arr).put("versions", arr).toString()
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
