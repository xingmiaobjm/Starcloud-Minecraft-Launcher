// 加载器构建列表（与 Win 端同源）：
//   Fabric / Quilt —— launcher-meta JSON（[{loader:{version,stable}},…]）
//   Forge —— maven-metadata.xml 全量构建，过滤 "<mc>-" 前缀（新→旧）
//   NeoForge —— maven-metadata.xml 全量构建（版本号不含 MC 前缀，新→旧）
// 带进程内缓存：同一 MC 版本二次打开不重拉；慢源/坏源短超时快速失败。
package cn.starcloud.launcher.compose.screens

import org.json.JSONArray
import java.net.HttpURLConnection
import java.net.URL

internal object LoaderBuildsFetcher {

    private val cache = HashMap<String, List<String>>()

    /** 失败抛异常；缓存命中直接返回（不触网） */
    fun fetch(key: String, metaBase: String, mc: String): List<String> {
        val cacheKey = "$mc|$key"
        cache[cacheKey]?.let { return it }
        val result = fetchRemote(key, metaBase, mc)
        if (result.isNotEmpty()) cache[cacheKey] = result
        return result
    }

    private fun fetchRemote(key: String, metaBase: String, mc: String): List<String> = when (key) {
        "fabric", "quilt" -> {
            val arr = JSONArray(fetch("$metaBase/$mc"))
            (0 until arr.length()).mapNotNull { i ->
                arr.optJSONObject(i)?.optJSONObject("loader")?.optString("version")
                    ?.takeIf { it.isNotEmpty() }
            }
        }
        "forge" -> {
            val all = versionsFromXml(fetch("$metaBase/maven-metadata.xml"))
            all.filter { it.startsWith("$mc-") }
                .map { it.removePrefix("$mc-") }
                .reversed()
        }
        "neoforge" -> versionsFromXml(fetch("$metaBase/maven-metadata.xml")).reversed()
        else -> emptyList()
    }

    /** 404 = 该 MC 版本还没有任何构建，按空列表处理而非失败 */
    private fun fetch(url: String): String =
        (URL(url).openConnection() as HttpURLConnection).run {
            connectTimeout = 5_000
            readTimeout = 10_000
            requestMethod = "GET"
            instanceFollowRedirects = true
            val code = responseCode
            if (code == 404) return ""
            if (code !in 200..299) throw IllegalStateException("HTTP $code")
            inputStream.bufferedReader(Charsets.UTF_8).use { it.readText() }
        }

    private fun versionsFromXml(xml: String): List<String> =
        Regex("<version>(.*?)</version>").findAll(xml)
            .map { it.groupValues[1] }
            .filter { it.isNotBlank() }
            .toList()
}
