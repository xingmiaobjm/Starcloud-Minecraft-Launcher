// 触摸布局编辑器（横屏全屏，深色网格模拟游戏画面）：
//   顶部：布局切换 / 新建 / 保存 / 分享（复制 JSON）/ 导入（剪贴板）/ 关闭
//   中间：TouchControlsView（编辑模式）——拖键位、点角标删、点选中
//   底部：选中键位的改键 / 尺寸 ± / 删除 / 新增键位
// 与游戏内控件层共用同一份布局文件（<files>/touch-layouts/*.json）。
package cn.starcloud.launcher.compose.game

import android.app.Activity
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.graphics.Color
import android.os.Bundle
import android.text.InputType
import android.view.Gravity
import android.view.KeyEvent
import android.view.ViewGroup
import android.widget.ArrayAdapter
import android.widget.Button
import android.widget.EditText
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.Spinner
import android.widget.TextView
import android.widget.Toast
import cn.starcloud.launcher.compose.data.LauncherPrefs
import cn.starcloud.launcher.compose.data.TouchLayout

class TouchLayoutActivity : Activity() {

    private lateinit var controls: TouchControlsView
    private lateinit var layoutButton: TextView
    private lateinit var selectionLabel: TextView
    private lateinit var keyButton: TextView
    private lateinit var root: FrameLayout
    private var layoutNames: List<String> = emptyList()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.statusBarColor = Color.BLACK
        window.navigationBarColor = Color.BLACK

        root = FrameLayout(this).apply { setBackgroundColor(0xFF101010.toInt()) }
        controls = TouchControlsView(this).apply {
            editMode = true
            layout = TouchLayout.current(this@TouchLayoutActivity)
            onSelectionChanged = { refreshSelection() }
        }
        root.addView(controls, FrameLayout.LayoutParams(MATCH, MATCH))

        root.addView(buildTopBar(), FrameLayout.LayoutParams(MATCH, wrap).apply {
            gravity = Gravity.TOP
        })
        root.addView(buildBottomBar(), FrameLayout.LayoutParams(MATCH, wrap).apply {
            gravity = Gravity.BOTTOM
        })
        setContentView(root)
        refreshLayoutList(current = (controls.layout.name))
    }

    private val MATCH = ViewGroup.LayoutParams.MATCH_PARENT
    private val wrap = ViewGroup.LayoutParams.WRAP_CONTENT

    // ── 顶部工具栏 ──

    private fun buildTopBar(): LinearLayout {
        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(0xE0181818.toInt())
            setPadding(dp(8), dp(6), dp(8), dp(6))
            gravity = Gravity.CENTER_VERTICAL
        }
        bar.addView(TextView(this).apply {
            text = "布局"
            setTextColor(0xFFCCCCCC.toInt())
            textSize = 12f
            setPadding(0, 0, dp(6), 0)
        })
        layoutButton = pillButton("默认 ▾") { showLayoutMenu() }
        bar.addView(layoutButton)

        bar.addView(tool("新建") { newLayout() })
        bar.addView(tool("保存") { save() })
        bar.addView(tool("分享") { share() })
        bar.addView(tool("导入") { importLayout() })
        bar.addView(tool("默认键位") {
            controls.layout = TouchLayout.defaultLayout().copy(name = controls.layout.name)
            controls.invalidate()
            refreshSelection()
        })
        bar.addView(TextView(this).apply {
            text = "  拖动键位调整位置，点右上角 × 删除"
            setTextColor(0x80FFFFFF.toInt())
            textSize = 10f
        }, LinearLayout.LayoutParams(0, wrap, 1f))
        bar.addView(tool("关闭") { finish() })
        return bar
    }

    /** 胶囊下拉按钮：显示当前值 + ▾，点击弹系统列表（比原生 Spinner 清爽） */
    private fun pillButton(text: String, onClick: () -> Unit): TextView =
        TextView(this).apply {
            this.text = text
            textSize = 12f
            setTextColor(0xFF1A1A1A.toInt())
            maxLines = 1
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(0xFFFFD322.toInt())
                cornerRadius = dp(6).toFloat()
            }
            setPadding(dp(10), dp(5), dp(10), dp(5))
            setOnClickListener { onClick() }
            layoutParams = LinearLayout.LayoutParams(wrap, wrap).apply { marginEnd = dp(6) }
        }

    private fun tool(label: String, onClick: () -> Unit): Button =
        Button(this).apply {
            text = label
            textSize = 11f
            setTextColor(0xFF1A1A1A.toInt())
            background = android.graphics.drawable.GradientDrawable().apply {
                setColor(0xFFFFD322.toInt())
                cornerRadius = dp(6).toFloat()
            }
            isAllCaps = false
            minWidth = 0
            minimumWidth = 0
            setPadding(dp(10), dp(3), dp(10), dp(3))
            setOnClickListener { onClick() }
            layoutParams = LinearLayout.LayoutParams(wrap, wrap).apply { marginEnd = dp(4) }
        }

    // ── 底部键位条 ──

    private fun buildBottomBar(): LinearLayout {
        val bar = LinearLayout(this).apply {
            orientation = LinearLayout.HORIZONTAL
            setBackgroundColor(0xE0181818.toInt())
            setPadding(dp(8), dp(6), dp(8), dp(6))
            gravity = Gravity.CENTER_VERTICAL
        }
        selectionLabel = TextView(this).apply {
            text = "未选中键位"
            setTextColor(0xFFDDDDDD.toInt())
            textSize = 12f
        }
        bar.addView(selectionLabel, LinearLayout.LayoutParams(0, wrap, 1f))

        keyButton = pillButton("改键 ▾") { showKeyMenu() }
        bar.addView(keyButton)
        bar.addView(tool("＋") { controls.resizeSelected(6); refreshSelection() })
        bar.addView(tool("－") { controls.resizeSelected(-6); refreshSelection() })
        bar.addView(tool("改名") { renameSelected() })
        bar.addView(tool("删除") { controls.deleteSelected(); refreshSelection() })
        bar.addView(tool("新增键位") { addKey() })
        return bar
    }

    /** 布局下拉：切换存档里的布局 */
    private fun showLayoutMenu() {
        val names = TouchLayout.list(this).ifEmpty { listOf(controls.layout.name) }
        android.app.AlertDialog.Builder(this)
            .setTitle("选择布局")
            .setItems(names.toTypedArray()) { _, which ->
                val name = names[which]
                TouchLayout.load(this, name)?.let {
                    controls.layout = it
                    TouchLayout.setCurrent(this, name)
                    layoutButton.text = "$name ▾"
                    refreshSelection()
                }
            }
            .show()
    }

    /** 改键下拉：选中键位后点选即应用（键名同步成显示文字） */
    private fun showKeyMenu() {
        val b = controls.selectedButton()
        if (b == null) {
            toast("先在画面里点选一个键位")
            return
        }
        android.app.AlertDialog.Builder(this)
            .setTitle("为「${b.label.ifBlank { b.type }}」选择按键")
            .setItems(KEY_OPTIONS.map { it.label }.toTypedArray()) { _, which ->
                val opt = KEY_OPTIONS[which]
                if (opt.androidKey <= 0) {
                    // 摇杆 / 视角：换类型（位置尺寸保留）
                    controls.removeButton(b.id)
                    controls.addButton(opt.type, -1, opt.label, b.w)
                } else {
                    controls.rebindSelected(opt.androidKey)
                    controls.renameSelected(opt.label)
                }
                refreshSelection()
            }
            .show()
    }

    private data class KeyOption(val androidKey: Int, val label: String, val type: String)

    private val KEY_OPTIONS = listOf(
        KeyOption(KeyEvent.KEYCODE_W, "W", "button"),
        KeyOption(KeyEvent.KEYCODE_A, "A", "button"),
        KeyOption(KeyEvent.KEYCODE_S, "S", "button"),
        KeyOption(KeyEvent.KEYCODE_D, "D", "button"),
        KeyOption(KeyEvent.KEYCODE_SPACE, "空格", "button"),
        KeyOption(KeyEvent.KEYCODE_SHIFT_LEFT, "Shift", "button"),
        KeyOption(KeyEvent.KEYCODE_CTRL_LEFT, "Ctrl", "button"),
        KeyOption(KeyEvent.KEYCODE_E, "E", "button"),
        KeyOption(KeyEvent.KEYCODE_Q, "Q", "button"),
        KeyOption(KeyEvent.KEYCODE_F, "F", "button"),
        KeyOption(KeyEvent.KEYCODE_1, "1", "button"),
        KeyOption(KeyEvent.KEYCODE_2, "2", "button"),
        KeyOption(KeyEvent.KEYCODE_3, "3", "button"),
        KeyOption(KeyEvent.KEYCODE_TAB, "Tab/玩家列表", "button"),
        KeyOption(KeyEvent.KEYCODE_ESCAPE, "Esc/菜单", "button"),
        KeyOption(KeyEvent.KEYCODE_F3, "F3/调试", "button"),
        KeyOption(-1, "鼠标左键", "mouse_left"),
        KeyOption(-1, "鼠标右键", "mouse_right"),
        KeyOption(-1, "摇杆(WASD)", "joystick"),
        KeyOption(-1, "视角区域", "mouse"),
    )

    // ── 动作 ──

    private fun addKey() {
        val dialog = LinearLayout(this).apply {
            orientation = LinearLayout.VERTICAL
            setPadding(dp(16), dp(8), dp(16), dp(8))
        }
        val labelInput = EditText(this).apply {
            hint = "按键显示文字（如 攻击）"
            inputType = InputType.TYPE_CLASS_TEXT
        }
        val preview = TextView(this).apply {
            text = "键：W"
            setTextColor(0xFF999999.toInt())
            textSize = 12f
            setPadding(0, dp(6), 0, 0)
        }
        val typeSpinner = Spinner(this).apply {
            adapter = ArrayAdapter(
                this@TouchLayoutActivity,
                android.R.layout.simple_spinner_dropdown_item,
                KEY_OPTIONS.map { it.label },
            )
            setSelection(0)
        }
        typeSpinner.onItemSelectedListener = object : android.widget.AdapterView.OnItemSelectedListener {
            override fun onItemSelected(parent: android.widget.AdapterView<*>?, view: android.view.View?, position: Int, id: Long) {
                preview.text = "键：" + (KEY_OPTIONS.getOrNull(position)?.label ?: "")
            }
            override fun onNothingSelected(parent: android.widget.AdapterView<*>?) {}
        }
        dialog.addView(labelInput, LinearLayout.LayoutParams(MATCH, wrap))
        dialog.addView(typeSpinner, LinearLayout.LayoutParams(MATCH, wrap))
        dialog.addView(preview)

        android.app.AlertDialog.Builder(this)
            .setTitle("新增键位")
            .setView(dialog)
            .setPositiveButton("添加") { _, _ ->
                val opt = KEY_OPTIONS.getOrNull(typeSpinner.selectedItemPosition) ?: return@setPositiveButton
                val label = labelInput.text.toString().ifBlank { opt.label.take(1) }
                controls.addButton(opt.type, opt.androidKey, label)
                refreshSelection()
                toast("已添加，拖动到合适位置")
            }
            .setNegativeButton("取消", null)
            .show()
    }

    private fun renameSelected() {
        val b = controls.selectedButton()
        if (b == null) {
            toast("先点选一个键位")
            return
        }
        val input = EditText(this).apply {
            setText(b.label)
            hint = "显示文字"
        }
        android.app.AlertDialog.Builder(this)
            .setTitle("修改显示文字")
            .setView(input)
            .setPositiveButton("确定") { _, _ ->
                controls.renameSelected(input.text.toString())
                refreshSelection()
            }
            .setNegativeButton("取消", null)
            .show()
    }

    private fun newLayout() {
        val input = EditText(this).apply { hint = "新布局名称" }
        android.app.AlertDialog.Builder(this)
            .setTitle("新建布局（复制当前键位）")
            .setView(input)
            .setPositiveButton("创建") { _, _ ->
                val name = input.text.toString().trim().ifBlank { "布局${layoutNames.size + 1}" }
                val layout = controls.layout.copy(name = name)
                TouchLayout.save(this, layout)
                refreshLayoutList(current = name)
                TouchLayout.setCurrent(this, name)
                toast("已创建 $name")
            }
            .setNegativeButton("取消", null)
            .show()
    }

    private fun save() {
        TouchLayout.save(this, controls.layout)
        TouchLayout.setCurrent(this, controls.layout.name)
        LauncherPrefs.setTouchLayout(this, controls.layout.name)
        toast("已保存「${controls.layout.name}」")
    }

    private fun share() {
        val json = TouchLayout.toJson(controls.layout).toString(2)
        val cb = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        cb.setPrimaryClip(ClipData.newPlainText("Starcloud 触摸布局", json))
        toast("布局 JSON 已复制到剪贴板，发给好友即可分享")
    }

    private fun importLayout() {
        val cb = getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
        val text = cb.primaryClip?.takeIf { it.itemCount > 0 }?.getItemAt(0)?.text?.toString()
        if (text.isNullOrBlank()) {
            toast("剪贴板里没有布局 JSON")
            return
        }
        try {
            var layout = TouchLayout.parse(text)
            if (layout.buttons.isEmpty()) {
                toast("JSON 里没有键位")
                return
            }
            var name = layout.name
            var n = 2
            while (TouchLayout.list(this).contains(name)) {
                name = "${layout.name} (${n++})"
            }
            layout = layout.copy(name = name)
            TouchLayout.save(this, layout)
            controls.layout = layout
            refreshLayoutList(current = name)
            TouchLayout.setCurrent(this, name)
            toast("已导入「$name」（${layout.buttons.size} 个键位）")
        } catch (e: Exception) {
            toast("导入失败：${e.message}")
        }
    }

    private fun refreshLayoutList(current: String) {
        layoutNames = TouchLayout.list(this).ifEmpty { listOf(current) }
        if (!layoutNames.contains(current)) layoutNames = layoutNames + current
        layoutButton.text = "$current ▾"
    }

    private fun refreshSelection() {
        val b = controls.selectedButton()
        selectionLabel.text = if (b == null) "未选中键位"
        else "选中：${b.label.ifBlank { b.type }} · ${b.w}dp"
    }

    private fun toast(msg: String) {
        Toast.makeText(this, msg, Toast.LENGTH_SHORT).show()
    }

    private fun dp(v: Int): Int = (v * resources.displayMetrics.density).toInt()
}
