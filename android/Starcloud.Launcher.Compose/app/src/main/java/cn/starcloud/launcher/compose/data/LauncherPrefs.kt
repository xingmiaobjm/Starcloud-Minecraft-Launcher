package cn.starcloud.launcher.compose.data

import android.content.Context

/**
 * 启动器的轻量偏好存储（SharedPreferences）。
 *
 * 只放「跨进程重启要保留、又不值得建数据库」的几个值：离线用户名、主题、内存上限。
 * Compose 壳与 Web 壳共用同一份，键名与 Web 桥的 `settings.*` 一一对应。
 */
object LauncherPrefs {

    private const val FILE = "starcloud_prefs"

    const val KEY_PLAYER_NAME = "player_name"
    const val KEY_THEME = "theme"
    const val KEY_MAX_MEM_MB = "max_mem_mb"
    const val KEY_LAST_VERSION = "last_version"

    const val DEFAULT_PLAYER_NAME = "Player"

    private fun prefs(context: Context) =
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    /** 离线用户名：启动参数里的 --username，也是离线 UUID 的来源 */
    fun playerName(context: Context): String =
        prefs(context).getString(KEY_PLAYER_NAME, DEFAULT_PLAYER_NAME)
            ?.takeIf { it.isNotBlank() } ?: DEFAULT_PLAYER_NAME

    fun setPlayerName(context: Context, name: String) {
        val clean = name.trim().ifEmpty { DEFAULT_PLAYER_NAME }
        prefs(context).edit().putString(KEY_PLAYER_NAME, clean).apply()
    }

    /** 主题：system / light / dark（Web 壳与 Compose 壳共用） */
    fun theme(context: Context): String =
        prefs(context).getString(KEY_THEME, "system") ?: "system"

    fun setTheme(context: Context, mode: String) {
        val clean = if (mode in setOf("system", "light", "dark")) mode else "system"
        prefs(context).edit().putString(KEY_THEME, clean).apply()
    }

    /** 最大内存（MB），0 = 未设置 */
    fun maxMemoryMb(context: Context): Int = prefs(context).getInt(KEY_MAX_MEM_MB, 0)

    fun setMaxMemoryMb(context: Context, mb: Int) {
        prefs(context).edit().putInt(KEY_MAX_MEM_MB, mb.coerceAtLeast(0)).apply()
    }

    /** 默认（上次启动）版本 id：versions.setDefault 写它，启动页读它 */
    fun lastVersion(context: Context): String =
        prefs(context).getString(KEY_LAST_VERSION, "") ?: ""

    fun setLastVersion(context: Context, id: String) {
        prefs(context).edit().putString(KEY_LAST_VERSION, id.trim()).apply()
    }

    /** 一次性导出全部设置，供 settings.get 返回 */
    fun snapshot(context: Context): Map<String, Any> = linkedMapOf(
        KEY_PLAYER_NAME to playerName(context),
        KEY_THEME to theme(context),
        KEY_MAX_MEM_MB to maxMemoryMb(context),
    )
}
