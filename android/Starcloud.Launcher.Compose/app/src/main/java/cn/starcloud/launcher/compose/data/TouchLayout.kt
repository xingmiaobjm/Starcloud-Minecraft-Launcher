// 触摸控件布局（对齐 FCL / Pojav 的 TouchControls 思路）：
//   一套布局 = 若干键位（button / toggle / joystick / dpad / mouse 区域），
//   位置为相对屏幕比例（0-1，键位中心）+ dp 尺寸，键位用 Android KeyCode（注入经 LwjglKeycodeMap）。
//   布局存 <files>/touch-layouts/<名称>.json，可导出/导入（分享键位 = 分享这段 JSON）。
package cn.starcloud.launcher.compose.data

import android.content.Context
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

object TouchLayout {

    data class Button(
        val type: String,              // button / toggle / joystick / mouse / mouse_left / mouse_right
        val key: Int,                  // Android KeyEvent keycode；joystick/mouse* 为 -1
        val label: String,
        val x: Float,                  // 中心点相对位置 0-1
        val y: Float,
        val w: Int = 48,               // dp
        val h: Int = 48,
        val id: String = java.util.UUID.randomUUID().toString().take(8),
    )

    data class Layout(val name: String, val buttons: List<Button>)

    private fun dir(context: Context): File =
        File(context.filesDir, "touch-layouts").apply { mkdirs() }

    /** 内置默认布局：左摇杆 + 常用按键（以手机横屏 100% 比例为基准） */
    fun defaultLayout(): Layout = Layout(
        name = "默认",
        buttons = listOf(
            Button("joystick", -1, "", 0.10f, 0.72f, 120, 120, "stick"),
            Button("button", android.view.KeyEvent.KEYCODE_SHIFT_LEFT, "Shift", 0.055f, 0.40f, 46, 46, "sneak"),
            Button("button", android.view.KeyEvent.KEYCODE_SPACE, "空格", 0.82f, 0.42f, 56, 56, "jump"),
            Button("mouse_left", -1, "左键", 0.86f, 0.72f, 60, 60, "attack"),
            Button("mouse_right", -1, "右键", 0.72f, 0.82f, 56, 56, "use"),
            Button("button", android.view.KeyEvent.KEYCODE_E, "E", 0.60f, 0.86f, 44, 44, "inv"),
            Button("button", android.view.KeyEvent.KEYCODE_Q, "Q", 0.48f, 0.88f, 40, 40, "drop"),
            Button("mouse", -1, "", 0.45f, 0.50f, 200, 200, "look"),
        ),
    )

    fun list(context: Context): List<String> =
        dir(context).listFiles { f -> f.isFile && f.extension == "json" }
            ?.map { it.nameWithoutExtension }?.sorted() ?: emptyList()

    fun load(context: Context, name: String): Layout? = try {
        val file = File(dir(context), "$name.json")
        if (!file.isFile) null else parse(file.readText(), name)
    } catch (e: Exception) {
        null
    }

    fun save(context: Context, layout: Layout) {
        File(dir(context), "${layout.name}.json").writeText(toJson(layout).toString(2))
    }

    fun delete(context: Context, name: String) {
        File(dir(context), "$name.json").delete()
    }

    /** 当前选中布局（未设置或文件缺失时回落到默认，并落盘默认布局） */
    fun current(context: Context): Layout {
        val name = LauncherPrefs.touchLayout(context)
        val loaded = if (name.isNotBlank()) load(context, name) else null
        if (loaded != null) return loaded
        val builtin = defaultLayout()
        if (load(context, builtin.name) == null) save(context, builtin)
        LauncherPrefs.setTouchLayout(context, builtin.name)
        return builtin
    }

    fun setCurrent(context: Context, name: String) {
        LauncherPrefs.setTouchLayout(context, name)
    }

    // ── 序列化（也是分享格式） ──

    fun toJson(layout: Layout): JSONObject = JSONObject().apply {
        put("name", layout.name)
        put("buttons", JSONArray().apply {
            layout.buttons.forEach { b ->
                put(JSONObject().apply {
                    put("type", b.type)
                    put("key", b.key)
                    put("label", b.label)
                    put("x", b.x.toDouble())
                    put("y", b.y.toDouble())
                    put("w", b.w)
                    put("h", b.h)
                    put("id", b.id)
                })
            }
        })
    }

    fun parse(text: String, fallbackName: String = "导入"): Layout {
        val root = JSONObject(text.trim().removePrefix("\uFEFF"))
        val name = root.optString("name").ifBlank { fallbackName }
        val arr = root.optJSONArray("buttons") ?: JSONArray()
        val buttons = (0 until arr.length()).mapNotNull { i ->
            arr.optJSONObject(i)?.let { o ->
                Button(
                    type = o.optString("type", "button"),
                    key = o.optInt("key", -1),
                    label = o.optString("label"),
                    x = o.optDouble("x", 0.5).toFloat(),
                    y = o.optDouble("y", 0.5).toFloat(),
                    w = o.optInt("w", 48),
                    h = o.optInt("h", 48),
                    id = o.optString("id").ifBlank { java.util.UUID.randomUUID().toString().take(8) },
                )
            }
        }
        return Layout(name, buttons)
    }
}
