// M2 下载域骨架：原版版本清单拉取（国内镜像优先，失败回落官方）
package cn.starcloud.launcher.compose.data

import org.json.JSONObject
import java.io.File
import java.net.URL

data class ManifestEntry(
    val id: String,
    val type: String,
    val url: String,
    val releaseTime: String,
)

class ManifestRepository(private val cacheFile: File) {

    /** 与 Core DownloadSource 同口径：镜像优先 */
    private val urls: List<String>
        get() = DownloadDebug.manifestUrl?.let { listOf(it) } ?: listOf(
            "https://bmclapi2.bangbang93.com/mc/game/version_manifest.json",
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json",
        )

    /** 读缓存清单（无缓存/损坏返回 null）——进页面先上屏缓存，网络刷新随后覆盖 */
    fun cached(): List<ManifestEntry>? = try {
        parse(cacheFile.readText())
    } catch (_: Exception) { null }

    /** 拉取（带回缓存）；失败抛出最后一个异常。必须带超时：无超时会在不通的源上无限挂起 */
    fun fetch(): List<ManifestEntry> {
        var lastError: Exception? = null
        for (u in urls) {
            try {
                val text = (URL(u).openConnection() as java.net.HttpURLConnection).run {
                    connectTimeout = 8_000
                    readTimeout = 15_000
                    instanceFollowRedirects = true
                    requestMethod = "GET"
                    inputStream.bufferedReader(charset("UTF-8")).use { it.readText() }
                }
                cacheFile.parentFile?.mkdirs()
                cacheFile.writeText(text)
                return parse(text)
            } catch (e: Exception) {
                lastError = e
            }
        }
        if (cacheFile.isFile) return parse(cacheFile.readText())
        throw lastError ?: IllegalStateException("清单拉取失败")
    }

    private fun parse(text: String): List<ManifestEntry> {
        val arr = JSONObject(text).getJSONArray("versions")
        return (0 until arr.length()).map { i ->
            val o = arr.getJSONObject(i)
            ManifestEntry(
                id = o.getString("id"),
                type = o.getString("type"),
                url = o.getString("url"),
                releaseTime = o.optString("releaseTime", ""),
            )
        }
    }
}
