// 离线账户存储（M4 账户域 · 离线先行）：
//   · 账户列表持久化在 SharedPreferences（JSON 数组）
//   · 离线 UUID 与桌面端 Core 同算法（MD5 派生的 v3 UUID），换启动器后存档/白名单能对上
//   · 正版（微软 OAuth）与第三方（authlib）账户待后续里程碑接入同一列表
package cn.starcloud.launcher.compose.data

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.security.MessageDigest

data class OfflineAccount(val name: String, val uuid: String)

object AccountsStore {

    private const val FILE = "starcloud_accounts"
    private const val KEY_LIST = "accounts"
    private const val KEY_SELECTED = "selected"

    /** 官方离线用户名规则：3–16 位字母 / 数字 / 下划线 */
    fun isValidName(name: String): Boolean =
        name.length in 3..16 && name.all { it.isLetterOrDigit() || it == '_' }

    /** 等价 Java UUID.nameUUIDFromBytes("OfflinePlayer:<name>")：MD5 派生 v3 UUID */
    fun offlineUuid(name: String): String {
        val bytes = MessageDigest.getInstance("MD5")
            .digest("OfflinePlayer:${name}".toByteArray(Charsets.UTF_8))
        bytes[6] = ((bytes[6].toInt() and 0x0F) or 0x30).toByte()
        bytes[8] = ((bytes[8].toInt() and 0x3F) or 0x80).toByte()
        return String.format(
            "%02x%02x%02x%02x-%02x%02x-%02x%02x-%02x%02x-%02x%02x%02x%02x%02x%02x",
            bytes[0], bytes[1], bytes[2], bytes[3], bytes[4], bytes[5],
            bytes[6], bytes[7], bytes[8], bytes[9], bytes[10],
            bytes[11], bytes[12], bytes[13], bytes[14], bytes[15],
        )
    }

    private fun prefs(context: Context) =
        context.getSharedPreferences(FILE, Context.MODE_PRIVATE)

    fun list(context: Context): List<OfflineAccount> {
        val raw = prefs(context).getString(KEY_LIST, null) ?: return emptyList()
        return runCatching {
            val arr = JSONArray(raw)
            (0 until arr.length()).map { i ->
                val o = arr.getJSONObject(i)
                OfflineAccount(o.getString("name"), o.getString("uuid"))
            }
        }.getOrDefault(emptyList())
    }

    /** 添加离线账户；重名返回 false。选中态自动指向新账户。 */
    fun add(context: Context, name: String): Boolean {
        val clean = name.trim()
        if (!isValidName(clean)) return false
        val all = list(context).toMutableList()
        if (all.any { it.name.equals(clean, ignoreCase = true) }) return false
        all += OfflineAccount(clean, offlineUuid(clean))
        persist(context, all)
        setSelected(context, clean)
        return true
    }

    fun remove(context: Context, name: String) {
        val all = list(context).toMutableList()
        all.removeAll { it.name == name }
        persist(context, all)
        if (selectedName(context) == name) {
            prefs(context).edit().putString(KEY_SELECTED, all.firstOrNull()?.name ?: "").apply()
        }
    }

    fun setSelected(context: Context, name: String) {
        prefs(context).edit().putString(KEY_SELECTED, name).apply()
    }

    fun selectedName(context: Context): String =
        prefs(context).getString(KEY_SELECTED, "") ?: ""

    /** 启动使用的账户：选中优先，回落到列表第一个；空列表返回 null */
    fun selected(context: Context): OfflineAccount? {
        val all = list(context)
        if (all.isEmpty()) return null
        return all.firstOrNull { it.name == selectedName(context) } ?: all.first()
    }

    private fun persist(context: Context, accounts: List<OfflineAccount>) {
        val arr = JSONArray()
        accounts.forEach { a ->
            arr.put(JSONObject().put("name", a.name).put("uuid", a.uuid))
        }
        prefs(context).edit().putString(KEY_LIST, arr.toString()).apply()
    }
}
