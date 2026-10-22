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

    /** 最大内存（MB），0 = 未设置（使用自动算法） */
    fun maxMemoryMb(context: Context): Int = prefs(context).getInt(KEY_MAX_MEM_MB, 0)

    fun setMaxMemoryMb(context: Context, mb: Int) {
        prefs(context).edit().putInt(KEY_MAX_MEM_MB, mb.coerceAtLeast(0)).apply()
    }

    /**
     * 自动堆内存（按设备物理内存分档，夹在 512–4096 MB）：
     *   ≥11 GB → 4096；≥7 GB → 3072；≥5 GB → 2048；≥3.5 GB → 1536；其余 1024。
     * 低端机保底 1024 避免 OOM，高端机封顶 4096 避免游戏内 GC 停顿恶化。
     */
    fun autoMaxMemoryMb(context: Context): Int {
        val totalMb = try {
            val am = context.getSystemService(Context.ACTIVITY_SERVICE) as android.app.ActivityManager
            val mi = android.app.ActivityManager.MemoryInfo()
            am.getMemoryInfo(mi)
            (mi.totalMem / (1024 * 1024)).toInt()
        } catch (e: Exception) {
            4096
        }
        return when {
            totalMb >= 11264 -> 4096
            totalMb >= 7168 -> 3072
            totalMb >= 5120 -> 2048
            totalMb >= 3584 -> 1536
            else -> 1024
        }
    }

    /** 实际生效的堆内存：手动设置优先，否则走自动算法 */
    fun effectiveMaxMemoryMb(context: Context): Int =
        maxMemoryMb(context).takeIf { it > 0 } ?: autoMaxMemoryMb(context)

    // ── Starcloud ID（桌面同账号；Agent 与跨设备调用共用） ──
    const val KEY_SC_UID = "starcloud_uid"
    const val KEY_SC_KEY = "starcloud_key"

    fun starcloudUid(context: Context): String =
        prefs(context).getString(KEY_SC_UID, "") ?: ""

    fun setStarcloudUid(context: Context, value: String) {
        prefs(context).edit().putString(KEY_SC_UID, value.trim()).apply()
    }

    fun starcloudKey(context: Context): String =
        prefs(context).getString(KEY_SC_KEY, "") ?: ""

    fun setStarcloudKey(context: Context, value: String) {
        prefs(context).edit().putString(KEY_SC_KEY, value.trim()).apply()
    }

    fun starcloudLoggedIn(context: Context): Boolean =
        starcloudUid(context).isNotBlank() && starcloudKey(context).isNotBlank()

    fun starcloudLogout(context: Context) {
        prefs(context).edit().remove(KEY_SC_UID).remove(KEY_SC_KEY).apply()
    }

    // ── 跨设备 Agent（供电脑端经局域网调用） ──
    const val KEY_AGENT_ENABLED = "agent_enabled"
    const val KEY_AGENT_UID = "agent_uid"
    const val KEY_AGENT_KEY = "agent_key"
    const val KEY_AGENT_NAME = "agent_name"

    fun agentEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_AGENT_ENABLED, false)

    fun setAgentEnabled(context: Context, value: Boolean) {
        prefs(context).edit().putBoolean(KEY_AGENT_ENABLED, value).apply()
    }

    fun agentUid(context: Context): String =
        prefs(context).getString(KEY_AGENT_UID, "") ?: ""

    fun setAgentUid(context: Context, value: String) {
        prefs(context).edit().putString(KEY_AGENT_UID, value.trim()).apply()
    }

    fun agentApiKey(context: Context): String =
        prefs(context).getString(KEY_AGENT_KEY, "") ?: ""

    fun setAgentApiKey(context: Context, value: String) {
        prefs(context).edit().putString(KEY_AGENT_KEY, value.trim()).apply()
    }

    fun agentDeviceName(context: Context): String =
        prefs(context).getString(KEY_AGENT_NAME, "") ?: ""

    fun setAgentDeviceName(context: Context, value: String) {
        prefs(context).edit().putString(KEY_AGENT_NAME, value.trim()).apply()
    }

    /** 默认（上次启动）版本 id：versions.setDefault 写它，启动页读它 */
    fun lastVersion(context: Context): String =
        prefs(context).getString(KEY_LAST_VERSION, "") ?: ""

    fun setLastVersion(context: Context, id: String) {
        prefs(context).edit().putString(KEY_LAST_VERSION, id.trim()).apply()
    }

    // ── 联机（陶瓦同形控制面：本机 HTTP /state；游戏开局域网后自动建房） ──
    const val KEY_MP_AUTO = "multiplayer_auto"
    const val KEY_MP_PORT = "multiplayer_port"
    const val KEY_MP_OVERLAY = "multiplayer_overlay"

    /** 游戏开局域网后自动开启联机（自动建房） */
    fun multiplayerAuto(context: Context): Boolean =
        prefs(context).getBoolean(KEY_MP_AUTO, false)

    fun setMultiplayerAuto(context: Context, value: Boolean) {
        prefs(context).edit().putBoolean(KEY_MP_AUTO, value).apply()
    }

    /** 本地联机核心（陶瓦）HTTP 控制面端口；0 = 未配置 */
    fun multiplayerPort(context: Context): Int =
        prefs(context).getInt(KEY_MP_PORT, 26000)

    fun setMultiplayerPort(context: Context, port: Int) {
        prefs(context).edit().putInt(KEY_MP_PORT, port.coerceIn(0, 65535)).apply()
    }

    /** 游戏内悬浮窗（联机控制）开关 */
    fun multiplayerOverlay(context: Context): Boolean =
        prefs(context).getBoolean(KEY_MP_OVERLAY, false)

    fun setMultiplayerOverlay(context: Context, value: Boolean) {
        prefs(context).edit().putBoolean(KEY_MP_OVERLAY, value).apply()
    }

    // ── 触摸控件（游戏内键位层） ──
    const val KEY_TOUCH_LAYOUT = "touch_layout"
    const val KEY_TOUCH_ENABLED = "touch_enabled"

    /** 当前触摸布局名（空 = 默认） */
    fun touchLayout(context: Context): String =
        prefs(context).getString(KEY_TOUCH_LAYOUT, "") ?: ""

    fun setTouchLayout(context: Context, name: String) {
        prefs(context).edit().putString(KEY_TOUCH_LAYOUT, name.trim()).apply()
    }

    /** 游戏内触摸控件层开关（默认开） */
    fun touchControlsEnabled(context: Context): Boolean =
        prefs(context).getBoolean(KEY_TOUCH_ENABLED, true)

    fun setTouchControlsEnabled(context: Context, value: Boolean) {
        prefs(context).edit().putBoolean(KEY_TOUCH_ENABLED, value).apply()
    }

    /** 一次性导出全部设置，供 settings.get 返回 */
    fun snapshot(context: Context): Map<String, Any> = linkedMapOf(
        KEY_PLAYER_NAME to playerName(context),
        KEY_THEME to theme(context),
        KEY_MAX_MEM_MB to maxMemoryMb(context),
    )
}
