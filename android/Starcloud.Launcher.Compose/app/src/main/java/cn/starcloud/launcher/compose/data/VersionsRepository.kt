// M1 版本域：已安装版本扫描/删除（对齐 Core ManifestService.ListInstalled 语义：
// versions/<id>/<id>.json 存在即已安装，大小取 <id>.jar）
package cn.starcloud.launcher.compose.data

import org.json.JSONObject
import java.io.File

data class InstalledVersion(
    val id: String,
    val type: String,      // release / snapshot / old_beta / old_alpha / 未知
    val sizeBytes: Long,
    val lastModified: Long,
)

class VersionsRepository(private val filesRoot: File) {

    val versionsDir: File get() = File(filesRoot, ".minecraft/versions")

    fun list(): List<InstalledVersion> {
        val result = mutableListOf<InstalledVersion>()
        versionsDir.listFiles { f -> f.isDirectory }?.forEach { sub ->
            val json = File(sub, "${sub.name}.json")
            if (!json.isFile) return@forEach
            val type = try {
                JSONObject(json.readText()).optString("type", "未知")
            } catch (_: Exception) { "未知" }
            val jar = File(sub, "${sub.name}.jar")
            result += InstalledVersion(
                id = sub.name,
                type = type,
                sizeBytes = if (jar.isFile) jar.length() else 0L,
                lastModified = json.lastModified(),
            )
        }
        return result.sortedByDescending { it.lastModified }
    }

    fun remove(id: String) {
        File(versionsDir, id).deleteRecursively()
    }
}
