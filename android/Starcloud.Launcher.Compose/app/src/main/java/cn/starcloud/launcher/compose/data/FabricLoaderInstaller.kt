// Fabric / Quilt 加载器安装（对齐 Win 端 InstallerService.InstallLoaderProfileAsync）：
//   · 拉 launcher-meta profile（fabric: meta.fabricmc.net/v2，quilt: meta.quiltmc.org/v3，结构相同）
//   · 与原版版本 JSON 合并（mainClass 覆盖 / libraries 追加 / arguments.jvm 前插 + game 追加）
//   · 保存到 versions/<targetId>/，并下载 profile 里全部 Maven 坐标库
//     （Fabric profile 只有 name + url + sha1/size，没有 downloads —— 必须按 Maven 路径解析）
package cn.starcloud.launcher.compose.data

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.net.HttpURLConnection
import java.net.URL
import java.security.MessageDigest

object FabricLoaderInstaller {

    private const val FABRIC_META = "https://meta.fabricmc.net/v2/versions/loader"
    private const val QUILT_META = "https://meta.quiltmc.org/v3/versions/loader"

    /**
     * 下载并合并保存；返回新版本 id。
     * targetId 为目标版本目录名（缺省 = fabric-loader-<loader>-<mc>）；
     * quilt = true 时改用 Quilt 的 meta 源（v3 响应结构与 Fabric 相同）。
     */
    fun install(
        context: Context,
        filesRoot: File,
        mcVersion: String,
        loaderVersion: String,
        quilt: Boolean = false,
        targetId: String? = null,
        onStage: (String) -> Unit = {},
    ): String {
        val versionId = targetId ?: "fabric-loader-$loaderVersion-$mcVersion"
        val versionsDir = File(filesRoot, ".minecraft/versions").resolve(versionId)
        versionsDir.mkdirs()

        // 1. 拉 profile
        val metaBase = if (quilt) QUILT_META else FABRIC_META
        onStage("获取 $loaderVersion 配置…")
        val profile = JSONObject(fetch("$metaBase/$mcVersion/$loaderVersion/profile/json"))

        // 2. 读取原版版本 JSON 并合并
        onStage("合并原版 $mcVersion 配置…")
        val vanillaPath = File(File(filesRoot, ".minecraft/versions"), "$mcVersion/$mcVersion.json")
        if (!vanillaPath.isFile)
            throw IllegalStateException("缺少原版 $mcVersion 的版本 JSON，请先下载原版")
        val vanilla = JSONObject(vanillaPath.readText())

        val merged = JSONObject()
        merged.put("id", versionId)
        merged.put("mainClass", profile.optString("mainClass"))
        merged.put("type", profile.optString("type", "release"))
        merged.put("releaseTime", profile.optString("releaseTime"))
        merged.put("inheritsFrom", mcVersion)
        for (key in listOf("downloads", "assetIndex", "assets", "javaVersion", "logging", "time")) {
            vanilla.opt(key)?.let { merged.put(key, it) }
        }

        val libs = JSONArray()
        vanilla.optJSONArray("libraries")?.let { vanillaLibs ->
            for (i in 0 until vanillaLibs.length()) libs.put(vanillaLibs.get(i))
        }
        profile.optJSONArray("libraries")?.let { profileLibs ->
            for (i in 0 until profileLibs.length()) libs.put(profileLibs.get(i))
        }
        merged.put("libraries", libs)

        val args = JSONObject()
        val parentArgs = vanilla.optJSONObject("arguments") ?: JSONObject()
        val jvm = JSONArray()
        profile.optJSONObject("arguments")?.optJSONArray("jvm")?.let { childJvm ->
            for (i in 0 until childJvm.length()) jvm.put(childJvm.get(i))
        }
        parentArgs.optJSONArray("jvm")?.let { parentJvm ->
            for (i in 0 until parentJvm.length()) jvm.put(parentJvm.get(i))
        }
        args.put("jvm", jvm)
        val game = JSONArray()
        profile.optJSONObject("arguments")?.optJSONArray("game")?.let { childGame ->
            for (i in 0 until childGame.length()) game.put(childGame.get(i))
        }
        parentArgs.optJSONArray("game")?.let { parentGame ->
            for (i in 0 until parentGame.length()) game.put(parentGame.get(i))
        }
        args.put("game", game)
        merged.put("arguments", args)

        File(versionsDir, "$versionId.json").writeText(merged.toString())

        // 3. 下载 profile 全部库（Maven 坐标解析 + sha1 校验，已有则跳过）
        val libsDir = File(filesRoot, ".minecraft/libraries")
        data class LibJob(val url: String, val dest: File, val sha1: String?)
        val jobs = mutableListOf<LibJob>()
        for (i in 0 until libs.length()) {
            val lib = libs.optJSONObject(i) ?: continue
            val name = lib.optString("name")
            if (name.isBlank()) continue
            val path = mavenPath(name) ?: continue
            val base = lib.optString("url").ifBlank { "https://libraries.minecraft.net/" }.trimEnd('/')
            jobs.add(LibJob("$base/$path", File(libsDir, path), lib.optString("sha1").ifBlank { null }))
        }
        var done = 0
        for (job in jobs) {
            onStage("下载 Fabric 库（${++done}/${jobs.size}）…")
            if (job.dest.isFile && job.dest.length() > 0) continue
            job.dest.parentFile?.mkdirs()
            fetchToFile(job.url, job.dest)
            if (job.sha1 != null) {
                val actual = sha1Of(job.dest)
                if (!actual.equals(job.sha1!!, ignoreCase = true))
                    throw IllegalStateException("库校验失败：${job.dest.name}")
            }
        }
        return versionId
    }

    /** group:artifact:version[:classifier][@ext] → group/artifact/version/artifact-version[-classifier].ext */
    fun mavenPath(name: String): String? {
        var raw = name.trim()
        if (raw.isEmpty()) return null
        var ext = "jar"
        val at = raw.indexOf('@')
        if (at >= 0) {
            if (at == raw.length - 1) return null
            ext = raw.substring(at + 1)
            raw = raw.substring(0, at)
        }
        val parts = raw.split(":")
        if (parts.size !in 3..4) return null
        val group = parts[0]
        val artifact = parts[1]
        val version = parts[2]
        val classifier = if (parts.size == 4) parts[3] else null
        if (group.isEmpty() || artifact.isEmpty() || version.isEmpty()) return null
        if (classifier != null && classifier.isEmpty()) return null
        val fileName = "$artifact-$version$classifier.$ext"
        return group.replace('.', '/') + "/" + artifact + "/" + version + "/" + fileName
    }

    private fun fetch(url: String): String =
        (URL(url).openConnection() as HttpURLConnection).run {
            connectTimeout = 10_000
            readTimeout = 30_000
            requestMethod = "GET"
            inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
        }

    private fun fetchToFile(url: String, dest: File) {
        (URL(url).openConnection() as HttpURLConnection).run {
            connectTimeout = 10_000
            readTimeout = 60_000
            instanceFollowRedirects = true
            dest.outputStream().use { out -> inputStream.copyTo(out) }
        }
    }

    private fun sha1Of(file: File): String {
        val digest = MessageDigest.getInstance("SHA-1")
        file.inputStream().use { input ->
            val buffer = ByteArray(1 shl 16)
            while (true) {
                val read = input.read(buffer)
                if (read <= 0) break
                digest.update(buffer, 0, read)
            }
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
}
