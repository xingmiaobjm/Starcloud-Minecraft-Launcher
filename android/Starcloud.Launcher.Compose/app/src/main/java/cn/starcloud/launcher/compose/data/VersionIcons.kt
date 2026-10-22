// 版本方块图标（对齐 Win 端 Assets/MC-Icons）：
//   release → 草方块 / snapshot → 紫水晶 / old_beta → 苔石 / old_alpha → 泥土
//   命中加载器关键字（neoforge → forge → fabric → quilt，顺序敏感）时改用加载器图标。
// 图标随 APK 资产发布（assets/mc-icons），按文件名缓存解码结果。
package cn.starcloud.launcher.compose.data

import android.content.Context
import android.graphics.Bitmap
import android.graphics.BitmapFactory

object VersionIcons {

    /** 顺序敏感：neoforge 必须先于 forge 匹配（id 中 "neoforge" 包含 "forge" 子串） */
    private val LoaderKeys = listOf("neoforge", "forge", "fabric", "quilt")

    fun fileFor(type: String, id: String, json: String? = null): String {
        for (key in LoaderKeys) {
            if (id.contains(key, ignoreCase = true) || json?.contains(key, ignoreCase = true) == true)
                return "$key.png"
        }
        return when (type) {
            "snapshot" -> "block_of_amethyst.png"
            "old_beta" -> "masonry_cobblestone.png"
            "old_alpha" -> "natural_dirt.png"
            else -> "natural_grass_block.png"
        }
    }

    private val cache = HashMap<String, Bitmap>()

    @Synchronized
    fun bitmap(context: Context, file: String): Bitmap? {
        cache[file]?.let { return it }
        return try {
            BitmapFactory.decodeStream(context.assets.open("mc-icons/$file"))?.also {
                cache[file] = it
            }
        } catch (_: Exception) {
            null
        }
    }
}
