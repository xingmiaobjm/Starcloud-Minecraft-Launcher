// 组件清单（对齐 C# ComponentManifest 的数据结构与校验语义）
package cn.starcloud.launcher.compose.data

import org.json.JSONObject

enum class ArchiveKind { TarXz, Zip }

data class ComponentEntry(
    val name: String,
    val version: String,
    val url: String,
    val sha256: String,
    val archive: ArchiveKind,
) {
    /** assets 内的包文件名（url 形如 asset://components/jre21.tar.xz） */
    val assetFile: String get() = url.substringAfterLast('/')
}

class ComponentManifest(val components: List<ComponentEntry>) {

    fun find(name: String): ComponentEntry? = components.firstOrNull { it.name == name }

    fun require(name: String): ComponentEntry =
        find(name) ?: throw IllegalStateException("组件清单缺少 $name：当前内置组件不完整")

    companion object {
        fun parse(jsonText: String): ComponentManifest {
            val root = JSONObject(jsonText)
            val arr = root.optJSONArray("components")
                ?: throw IllegalStateException("组件清单解析失败：缺 components 数组")
            val list = mutableListOf<ComponentEntry>()
            for (i in 0 until arr.length()) {
                val o = arr.getJSONObject(i)
                list += ComponentEntry(
                    name = o.getString("name"),
                    version = o.getString("version"),
                    url = o.getString("url"),
                    sha256 = o.optString("sha256", ""),
                    archive = if (o.optString("archive") == "TarXz") ArchiveKind.TarXz else ArchiveKind.Zip,
                )
            }
            if (list.isEmpty()) throw IllegalStateException("组件清单为空：至少需要登记一个组件")
            val names = list.map { it.name }
            if (names.size != names.distinct().size)
                throw IllegalStateException("组件清单存在同名重复条目")
            return ComponentManifest(list)
        }
    }
}
