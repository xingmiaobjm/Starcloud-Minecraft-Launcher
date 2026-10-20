// M2 下载引擎：version json → client jar → libraries → assets 的编排与落盘。
// 设计对齐 Core 下载源口径：piston-meta/resources 官方源优先尝试、BMCLAPI 镜像回落
// （国内网络实测官方源直连不稳）；所有网络请求必须带超时（无超时会在不通源上无限挂起）。
package cn.starcloud.launcher.compose.data

import android.os.Environment
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.runtime.getValue
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.launch
import kotlinx.coroutines.runBlocking
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest
import java.util.concurrent.atomic.AtomicBoolean
import java.util.concurrent.atomic.AtomicInteger

/** 应用级安装状态（Compose 可观察）：下载进行中跨页面存活 */
object InstallState {
    var version by mutableStateOf<String?>(null)   // 正在安装的版本（null = 空闲）
    var stage by mutableStateOf("")
    var file by mutableStateOf("")
    var done by mutableIntStateOf(0)
    var total by mutableIntStateOf(0)
    var message by mutableStateOf<String?>(null)   // 完成/错误信息
    val cancelled = AtomicBoolean(false)

    fun cancel() { cancelled.set(true) }
}

/** 应用级协程域：下载不随页面销毁取消 */
object AppScope {
    val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
}

class VersionDownloader(private val filesRoot: File) {

    private val mcRoot get() = File(filesRoot, ".minecraft")
    private val versionsDir get() = File(mcRoot, "versions")
    private val librariesDir get() = File(mcRoot, "libraries")
    private val assetsDir get() = File(mcRoot, "assets")

    private val cancelled get() = InstallState.cancelled

    private fun checkCancel() {
        if (cancelled.get()) throw kotlinx.coroutines.CancellationException("已取消")
    }

    /** 官方 URL 的候选镜像：**BMCLAPI 镜像优先**（国内直连官方源不通，实测 65/66 失败）、官方回落 */
    /** 官方 URL 的候选镜像：**镜像优先、官方回落**。已实测 libraries.minecraft.net 与
         piston-* 官方直连在当前网络被 DNS 污染（下载到错误内容 → sha1 必失败），
         BMCLAPI maven/assets 镜像内容经电脑侧比对与官方 sha1 一致。 */
    private fun candidates(url: String): List<String> {
        val mirrored = mutableListOf<String>()
        if (url.startsWith("https://piston-meta.mojang.com/"))
            mirrored += url.replace("https://piston-meta.mojang.com/", "https://bmclapi2.bangbang93.com/")
        if (url.startsWith("https://resources.download.minecraft.net/"))
            mirrored += url.replace("https://resources.download.minecraft.net/", "https://bmclapi2.bangbang93.com/assets/")
        if (url.startsWith("https://piston-data.mojang.com/maven/"))
            mirrored += url.replace("https://piston-data.mojang.com/maven/", "https://bmclapi2.bangbang93.com/maven/")
        if (url.startsWith("https://libraries.minecraft.net/"))
            mirrored += url.replace("https://libraries.minecraft.net/", "https://bmclapi2.bangbang93.com/maven/")
        return (if (mirrored.isEmpty()) listOf(url) else mirrored + url).map { proxied(it) }.distinct()
    }

    // 代理模式（调试）：adb reverse tcp:8888 + 电脑 tools/forward_proxy.py——设备网络对
    // Mojang/BMCLAPI 域名响应被替换时，下载改走电脑网络（HTTP 明文，manifest 已开 cleartext）。
    // BMCLAPI 全路径可透传（versions/maven/assets 均有镜像）。
    private val USE_LOCAL_PROXY = true
    private val LOCAL_PROXY = "http://192.168.10.251:8889"

    private fun proxied(url: String): String {
        if (!USE_LOCAL_PROXY) return url
        val path = when {
            url.startsWith("https://piston-meta.mojang.com/") -> url.substringAfter("piston-meta.mojang.com/")
            url.startsWith("https://piston-data.mojang.com/maven/") -> url.substringAfter("piston-data.mojang.com/")
            url.startsWith("https://libraries.minecraft.net/") -> url.substringAfter("libraries.minecraft.net/")
            url.startsWith("https://resources.download.minecraft.net/") ->
                "assets/" + url.substringAfter("resources.download.minecraft.net/")
            url.startsWith("https://bmclapi2.bangbang93.com/") -> url.substringAfter("bmclapi2.bangbang93.com/")
            else -> return url
        }
        return "$LOCAL_PROXY/$path"
    }

    private fun fetchText(url: String): String {
        var lastError: Exception? = null
        for (u in candidates(url)) {
            try {
                return (URL(u).openConnection() as HttpURLConnection).run {
                    connectTimeout = 8_000
                    readTimeout = 20_000
                    instanceFollowRedirects = true
                    inputStream.bufferedReader(charset("UTF-8")).use { it.readText() }
                }
            } catch (e: Exception) {
                lastError = e
            }
        }
        throw lastError ?: IllegalStateException("拉取失败：$url")
    }

    /** 下载文件到 dest（已存在且大小一致则跳过）；sha1 非空时校验 */
    private fun downloadFile(urls: List<String>, dest: File, sha1: String?, size: Long) {
        checkCancel()
        // 跳过已下载：size 已知按精确匹配；未知（assets 对象）存在且非空即认
        if (dest.isFile && ((size > 0 && dest.length() == size) || (size == 0L && dest.length() > 0))) return
        dest.parentFile?.mkdirs()
        val part = File(dest.parentFile, dest.name + ".part")
        var lastError: Exception? = null
        for (u in urls) {
            try {
                checkCancel()
                val conn = URL(u).openConnection() as HttpURLConnection
                conn.connectTimeout = 8_000
                conn.readTimeout = 30_000
                conn.instanceFollowRedirects = true
                val digest = MessageDigest.getInstance("SHA-1")
                conn.inputStream.use { input ->
                    part.outputStream().use { output ->
                        val buf = ByteArray(64 * 1024)
                        while (true) {
                            checkCancel()
                            val n = input.read(buf)
                            if (n < 0) break
                            output.write(buf, 0, n)
                            digest.update(buf, 0, n)
                        }
                    }
                }
                if (sha1?.isNotEmpty() == true) {
                    val hex = digest.digest().joinToString("") { "%02x".format(it) }
                    if (!hex.equals(sha1, ignoreCase = true)) {
                        part.delete()
                        throw IllegalStateException("sha1 校验失败：${dest.name}")
                    }
                }
                if (size > 0 && part.length() != size) {
                    part.delete()
                    throw IllegalStateException("大小不符：${dest.name}")
                }
                if (dest.exists()) dest.delete()
                if (!part.renameTo(dest)) throw IllegalStateException("落盘失败：${dest.name}")
                return
            } catch (e: InterruptedException) {
                part.delete()
                throw e
            } catch (e: Exception) {
                part.delete()
                lastError = e
                // 候选源失败（限流/抖动）退避 1 秒再试下一个，避免连环快速失败
                Thread.sleep(800)
            }
        }
        throw lastError ?: IllegalStateException("下载失败：${dest.name}")
    }

    /** Mojang 规则判定：无规则 = 允许；有 os 规则时按最后一个匹配项（安卓 JVM 按 linux 处理） */
    private fun allowedByRules(rules: org.json.JSONArray?): Boolean {
        if (rules == null || rules.length() == 0) return true
        var allowed = false
        for (i in 0 until rules.length()) {
            val r = rules.getJSONObject(i)
            val os = r.optJSONObject("os")
            val applies = os == null || os.optString("name") == "linux"
            if (applies) allowed = r.getString("action") == "allow"
        }
        return allowed
    }

    /** 下载一个版本（json → client jar → libraries → asset 索引 → assets） */
    fun downloadVersion(entry: ManifestEntry) {
        InstallState.cancelled.set(false)
        InstallState.version = entry.id
        InstallState.message = null
        InstallState.done = 0
        InstallState.total = 0
        try {
            // ① 版本 json：优先复用设备上已有的（重装/补文件场景零网络即可完成；
            // 本机代理出口被加速器路由到坏边缘时 piston-meta 会回 BlobNotFound，实测复现）
            InstallState.stage = "版本 json"
            InstallState.file = entry.id + ".json"
            InstallState.done = 0
            InstallState.total = 1
            val localJsonFile = File(versionsDir, entry.id + "/" + entry.id + ".json")
            var json: org.json.JSONObject
            if (localJsonFile.isFile && localJsonFile.length() > 0) {
                json = JSONObject(localJsonFile.readText())
            } else {
                json = JSONObject(fetchText(entry.url))
            }
            InstallState.done = 1

            // inheritsFrom：取父版合并（子缺 downloads/libraries/assetIndex 时用父的）
            if (json.has("inheritsFrom")) {
                InstallState.file = json.getString("inheritsFrom") + ".json"
                val parentText = fetchText(parentUrl(json.getString("inheritsFrom"), entry.url))
                val parent = JSONObject(parentText)
                if (!json.has("downloads")) json.put("downloads", parent.optJSONObject("downloads"))
                if (!json.has("assetIndex")) json.put("assetIndex", parent.optJSONObject("assetIndex"))
                if (!json.has("mainClass")) json.put("mainClass", parent.optString("mainClass"))
                val merged = json.optJSONArray("libraries") ?: org.json.JSONArray()
                parent.optJSONArray("libraries")?.let { p ->
                    for (i in 0 until p.length()) merged.put(p.getJSONObject(i))
                }
                json.put("libraries", merged)
            }

            val dir = File(versionsDir, entry.id)
            dir.mkdirs()
            File(dir, entry.id + ".json").writeText(json.toString(2))

            // ② client jar
            InstallState.stage = "客户端 jar"
            val client = json.optJSONObject("downloads")?.optJSONObject("client")
            InstallState.file = entry.id + ".jar"
            InstallState.done = 0
            InstallState.total = 1
            if (client != null) {
                downloadFile(
                    candidates(client.getString("url")),
                    File(dir, entry.id + ".jar"),
                    client.optString("sha1", ""),
                    client.optLong("size", 0L),
                )
                InstallState.done = 1
            }

            // ③ libraries（linux 规则允许项）。sha1/size 在构建清单时即绑定：
            // libItems 是过滤子集，按其下标回查 libs 原数组会错位，size 全对不上
            // → 已下好的文件无法跳过 → 全量重下（代理不稳时整版失败，实测复现）。
            val libs = json.optJSONArray("libraries") ?: org.json.JSONArray()
            class LibDl(val url: String, val dest: File, val sha1: String?, val size: Long)
            val libItems = mutableListOf<LibDl>()
            for (i in 0 until libs.length()) {
                val lib = libs.getJSONObject(i)
                if (!allowedByRules(lib.optJSONArray("rules"))) continue
                val artifact = lib.optJSONObject("downloads")?.optJSONObject("artifact") ?: continue
                libItems += LibDl(
                    artifact.getString("url"),
                    File(librariesDir, artifact.getString("path")),
                    artifact.optString("sha1", "").ifEmpty { null },
                    artifact.optLong("size", 0L),
                )
            }
            InstallState.stage = "组件库"
            InstallState.file = ""
            InstallState.done = 0
            InstallState.total = libItems.size
            // done 计数走原子自增：并发下 InstallState.done = done + 1 非原子自增会丢帧
            val libDone = java.util.concurrent.atomic.AtomicInteger(0)
            val libFailures = runParallel(
                libItems,
                body = { index ->
                    val item = libItems[index]
                    downloadFile(candidates(item.url), item.dest, item.sha1, item.size)
                },
                onDone = { InstallState.done = libDone.incrementAndGet() },
            )
            if (libFailures > 0) throw IllegalStateException("组件库有 " + libFailures + " 个文件下载失败")

            // ④ asset 索引
            checkCancel()
            InstallState.stage = "资源索引"
            val assetIndex = json.optJSONObject("assetIndex")
            InstallState.done = 0
            InstallState.total = 1
            var objects = org.json.JSONObject()
            var virtual = false
            if (assetIndex != null) {
                val idxId = assetIndex.optString("id", "legacy")
                val localIdx = File(assetsDir, "indexes/" + idxId + ".json")
                val idxText = if (localIdx.isFile && localIdx.length() > 0) localIdx.readText()
                else fetchText(assetIndex.getString("url"))
                val idx = JSONObject(idxText)
                objects = idx.optJSONObject("objects") ?: org.json.JSONObject()
                virtual = idx.optBoolean("virtual", false)
                File(assetsDir, "indexes/${assetIndex.optString("id", "legacy")}.json").writeText(idxText)
                InstallState.done = 1
            }

            // ⑤ assets（对象哈希分目录存放；官方源 + BMCLAPI 镜像回落）
            val assetItems = mutableListOf<Pair<String, File>>()
            val keys = objects.keys()
            while (keys.hasNext()) {
                val name = keys.next()
                val o = objects.getJSONObject(name)
                val hash = o.getString("hash")
                assetItems += "https://resources.download.minecraft.net/${hash.substring(0, 2)}/$hash" to
                    File(assetsDir, "objects/${hash.substring(0, 2)}/$hash")
            }
            InstallState.stage = "游戏资源"
            InstallState.file = ""
            InstallState.done = 0
            InstallState.total = assetItems.size
            val assetDone = java.util.concurrent.atomic.AtomicInteger(0)
            val assetFailures = runParallel(
                assetItems,
                body = { index ->
                    val (url, dest) = assetItems[index]
                    downloadFile(candidates(url), dest, dest.name, 0L)
                },
                onDone = { InstallState.done = assetDone.incrementAndGet() },
            )
            if (assetFailures > 0) throw IllegalStateException("游戏资源有 " + assetFailures + " 个文件下载失败")
            if (virtual) File(assetsDir, "virtual/legacy").mkdirs()

            InstallState.message = "安装完成：${entry.id}"
        } catch (e: kotlinx.coroutines.CancellationException) {
            InstallState.message = "已取消"
        } catch (e: InterruptedException) {
            InstallState.message = "已取消"
        } catch (e: Exception) {
            InstallState.message = "安装失败：${e.message}"
        } finally {
            InstallState.version = null
        }
    }

    /** 固定 8 路并发跑同类文件下载（downloadVersion 本身在 IO 协程里同步执行，此处阻塞汇合）。
         单文件失败只计数不中断兄弟任务；返回失败数，由调用方决定是否整版失败。 */
    private fun <T> runParallel(items: List<T>, body: (Int) -> Unit, onDone: () -> Unit = {}): Int {
        if (items.isEmpty()) return 0
        val next = AtomicInteger(0)
        val failures = AtomicInteger(0)
        var firstError: String? = null
        // BMCLAPI 对高并发限流（限流响应内容错误 → sha1 失败）：并发降到 2
        val semaphore = kotlinx.coroutines.sync.Semaphore(2)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val jobs = (0 until minOf(2, items.size)).map {
            scope.launch {
                while (true) {
                    checkCancel()
                    val i = next.getAndIncrement()
                    if (i >= items.size) return@launch
                    semaphore.withPermit {
                        try {
                            body(i)
                            onDone()
                        } catch (e: kotlinx.coroutines.CancellationException) {
                            throw e
                        } catch (e: Exception) {
                            if (firstError == null) {
                                firstError = e.message ?: e.javaClass.simpleName
                                InstallState.file = "失败样例：" + firstError
                            }
                            failures.incrementAndGet()
                        }
                    }
                }
            }
        }
        jobs.forEach { runBlocking { it.join() } }
        return failures.get()
    }

    /** 父版本 json 的 URL：优先从清单缓存按 id 查，回落 piston-meta 规则路径 */
    private fun parentUrl(parentId: String, childUrl: String): String {
        // childUrl 形如 .../versions/<id>/<id>.json（镜像前缀同 child）
        val prefix = childUrl.substringBeforeLast("/versions/")
        return "$prefix/versions/$parentId/$parentId.json"
    }

    companion object {
        /** 数据根（与 Uno 壳同约定：external files/.minecraft） */
        fun filesRootOf(context: android.content.Context): File =
            context.getExternalFilesDir(null) ?: context.filesDir

        fun externalStorageReady(): Boolean =
            Environment.getExternalStorageState() == Environment.MEDIA_MOUNTED
    }
}
