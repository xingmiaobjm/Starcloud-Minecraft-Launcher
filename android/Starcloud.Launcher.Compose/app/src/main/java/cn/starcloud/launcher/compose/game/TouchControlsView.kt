// 游戏内触摸控件层（FCL / Pojav 风格）：全屏自定义 View，完全接管触摸——
//   · 按钮 → CallbackBridge 注入键盘/鼠标按下抬起；toggle 点击切换
//   · 摇杆 → WASD 组合（阈值 0.45）
//   · mouse 区域与空白 → 视角拖动（sendCursorDelta）
//   · 悬浮球（可拖动）：点击切换控制面板 / 编辑模式
// 编辑模式：拖动键位改位置、点击选中（编辑器面板据此改尺寸/删键位/改键）。
package cn.starcloud.launcher.compose.game

import android.annotation.SuppressLint
import android.content.Context
import android.graphics.Canvas
import android.graphics.Color
import android.graphics.Paint
import android.graphics.Typeface
import android.view.KeyEvent
import android.view.MotionEvent
import android.view.View
import cn.starcloud.launcher.compose.data.TouchLayout
import org.lwjgl.glfw.CallbackBridge
import kotlin.math.abs
import kotlin.math.hypot
import kotlin.math.roundToInt

class TouchControlsView(context: Context) : View(context) {

    var layout: TouchLayout.Layout = TouchLayout.defaultLayout()
        set(value) {
            field = value
            selectedId = null
            invalidate()
        }

    /** 编辑模式：显示虚线框、可拖动键位 */
    var editMode: Boolean = false
        set(value) {
            field = value
            if (!value) selectedId = null
            invalidate()
        }

    /** 当前选中的键位 id（编辑模式） */
    var selectedId: String? = null
        private set

    /** 悬浮球点击（非编辑模式）→ 打开控制面板 */
    var onOrbClick: (() -> Unit)? = null

    /** Java 侧（游戏活动）设置悬浮球动作 */
    fun setOrbClickAction(action: Runnable?) {
        onOrbClick = action?.let { r -> { r.run() } }
    }

    /** 编辑模式下键位变化回调（保存用） */
    var onLayoutChanged: (() -> Unit)? = null

    /** 编辑模式下选中变化回调（编辑器刷新底栏用） */
    var onSelectionChanged: (() -> Unit)? = null

    private val borderPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = dp(1.2f)
        color = 0x55FFFFFF
    }
    private val fillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
        color = 0x59000000
    }
    private val activeFillPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
        color = 0xD9FFD322.toInt()
    }
    private val textPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = 0xE6FFFFFF.toInt()
        textAlign = Paint.Align.CENTER
        typeface = Typeface.DEFAULT_BOLD
    }
    private val activeTextPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        color = 0xFF1A1A1A.toInt()
        textAlign = Paint.Align.CENTER
        typeface = Typeface.DEFAULT_BOLD
    }
    private val tickPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
        color = 0x66FFFFFF
    }
    private val deletePaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.FILL
        color = 0xFFD13438.toInt()
    }
    private val selectPaint = Paint(Paint.ANTI_ALIAS_FLAG).apply {
        style = Paint.Style.STROKE
        strokeWidth = dp(2f)
        color = 0xFFFFD322.toInt()
        pathEffect = android.graphics.DashPathEffect(floatArrayOf(dp(6f), dp(4f)), 0f)
    }

    // 触摸状态
    private val pointerTarget = HashMap<Int, String>()      // pointerId -> button id / "orb" / "look"
    private val activeKeys = HashSet<Int>()                  // 正在按下的按键（防重复 down）
    private val toggleStates = HashMap<String, Boolean>()
    private val stickPointers = HashMap<String, FloatArray>() // stick id -> [cx, cy, dx, dy]

    // 悬浮球位置（相对屏幕；右上角空档，避开默认为布局的"跳"键）
    private var orbX = 0.955f
    private var orbY = 0.115f
    private var orbDragging = false
    private var orbDownX = 0f
    private var orbDownY = 0f
    private var editDragId: String? = null

    fun setSelected(id: String?) {
        selectedId = id
        invalidate()
    }

    /** 当前布局快照（编辑保存用） */
    fun snapshot(): TouchLayout.Layout = layout

    // ── 绘制 ──

    override fun onDraw(canvas: Canvas) {
        super.onDraw(canvas)
        textPaint.textSize = dp(if (editMode) 13f else 12f)
        activeTextPaint.textSize = textPaint.textSize
        layout.buttons.forEach { b ->
            val cx = b.x * width
            val cy = b.y * height
            val w = dp(b.w.toFloat())
            val h = dp(b.h.toFloat())
            when (b.type) {
                "joystick" -> drawStick(canvas, b, cx, cy, w)
                "mouse" -> if (editMode) drawButton(canvas, b, cx, cy, w, h)
                else -> drawButton(canvas, b, cx, cy, w, h)
            }
        }
        // 悬浮球：操作态"控"，编辑态"编"
        val ox = orbX * width
        val oy = orbY * height
        canvas.drawCircle(ox, oy, dp(21f), fillPaint)
        canvas.drawCircle(ox, oy, dp(21f), borderPaint)
        textPaint.textSize = dp(14f)
        activeTextPaint.textSize = textPaint.textSize
        canvas.drawText(if (editMode) "编" else "控", ox, oy + dp(5f), textPaint)
    }

    private fun drawButton(canvas: Canvas, b: TouchLayout.Button, cx: Float, cy: Float, w: Float, h: Float) {
        val left = cx - w / 2
        val top = cy - h / 2
        val pressed = isPressed(b)
        // 编辑态未选中的键位降低存在感，选中项高亮
        val dim = editMode && b.id != selectedId
        val fillAlpha = if (dim) 0x33 else 0x59
        fillPaint.alpha = if (pressed) 0xFF else (fillAlpha * 255 / 0x100).coerceIn(0, 255)
        canvas.drawRoundRect(left, top, left + w, top + h, dp(10f), dp(10f),
            if (pressed) activeFillPaint else fillPaint)
        borderPaint.alpha = if (dim) 0x33 else 0x55
        canvas.drawRoundRect(left, top, left + w, top + h, dp(10f), dp(10f), borderPaint)
        borderPaint.alpha = 0x55
        fillPaint.alpha = 0x59
        if (b.label.isNotBlank()) {
            canvas.drawText(b.label, cx, cy + textPaint.textSize / 3,
                if (pressed) activeTextPaint else textPaint)
        }
        if (editMode) {
            canvas.drawRoundRect(left - dp(2f), top - dp(2f), left + w + dp(2f), top + h + dp(2f),
                dp(11f), dp(11f), if (b.id == selectedId) selectPaint else borderPaint)
            // 删除角标（红底白 ×）
            val dx = left + w - dp(1f)
            val dy = top + dp(1f)
            canvas.drawCircle(dx, dy, dp(9f), deletePaint)
            activeTextPaint.textSize = dp(11f)
            canvas.drawText("×", dx, dy + dp(4f), activeTextPaint)
            textPaint.textSize = dp(13f)
            activeTextPaint.textSize = textPaint.textSize
        }
    }

    private fun drawStick(canvas: Canvas, b: TouchLayout.Button, cx: Float, cy: Float, r: Float) {
        val outer = r / 2
        fillPaint.alpha = 0x40
        canvas.drawCircle(cx, cy, outer, fillPaint)
        fillPaint.alpha = 0x59
        canvas.drawCircle(cx, cy, outer, borderPaint)
        // 四向刻度
        for (i in 0 until 4) {
            val ang = Math.toRadians((i * 90).toDouble())
            val tx = cx + (Math.cos(ang) * (outer - dp(7f))).toFloat()
            val ty = cy + (Math.sin(ang) * (outer - dp(7f))).toFloat()
            canvas.drawCircle(tx, ty, dp(1.6f), tickPaint)
        }
        val st = stickPointers[b.id]
        val kx = cx + (st?.get(2) ?: 0f)
        val ky = cy + (st?.get(3) ?: 0f)
        canvas.drawCircle(kx, ky, outer * 0.46f, activeFillPaint)
        canvas.drawCircle(kx, ky, outer * 0.46f, borderPaint)
        if (editMode) {
            canvas.drawCircle(cx, cy, outer + dp(3f), if (b.id == selectedId) selectPaint else borderPaint)
            val dx = cx + outer - dp(1f)
            val dy = cy - outer + dp(1f)
            canvas.drawCircle(dx, dy, dp(9f), deletePaint)
            activeTextPaint.textSize = dp(11f)
            canvas.drawText("×", dx, dy + dp(4f), activeTextPaint)
            activeTextPaint.textSize = textPaint.textSize
        }
    }

    private fun isPressed(b: TouchLayout.Button): Boolean =
        pointerTarget.containsValue(b.id) || toggleStates[b.id] == true

    // ── 触摸 ──

    @SuppressLint("ClickableViewAccessibility")
    override fun onTouchEvent(ev: MotionEvent): Boolean {
        when (ev.actionMasked) {
            MotionEvent.ACTION_DOWN, MotionEvent.ACTION_POINTER_DOWN -> {
                val idx = ev.actionIndex
                val id = ev.getPointerId(idx)
                val x = ev.getX(idx)
                val y = ev.getY(idx)
                handleDown(id, x, y)
            }
            MotionEvent.ACTION_MOVE -> {
                for (i in 0 until ev.pointerCount) {
                    val id = ev.getPointerId(i)
                    handleMove(id, ev.getX(i), ev.getY(i))
                }
            }
            MotionEvent.ACTION_POINTER_UP, MotionEvent.ACTION_UP -> {
                val idx = ev.actionIndex
                handleUp(ev.getPointerId(idx))
            }
            MotionEvent.ACTION_CANCEL -> {
                pointerTarget.keys.toList().forEach { handleUp(it) }
            }
        }
        invalidate()
        return true
    }

    private fun handleDown(id: Int, x: Float, y: Float) {
        // 悬浮球优先
        if (hypot(x - orbX * width, y - orbY * height) <= dp(26f)) {
            pointerTarget[id] = "orb"
            orbDragging = false
            orbDownX = x
            orbDownY = y
            return
        }
        // 命中控件（倒序：后绘制的在上面）
        for (b in layout.buttons.asReversed()) {
            if (hit(b, x, y)) {
                if (editMode) {
                    // 编辑：点角标删、拖键位移动、点选
                    if (hitDelete(b, x, y)) {
                        removeButton(b.id)
                        return
                    }
                    selectedId = b.id
                    editDragId = b.id
                    pointerTarget[id] = b.id
                    onSelectionChanged?.invoke()
                    return
                }
                selectedId = b.id
                pointerTarget[id] = b.id
                pressDown(b, id, x, y)
                return
            }
        }
        // 空白 = 视角拖动
        pointerTarget[id] = "look"
    }

    private fun handleMove(id: Int, x: Float, y: Float) {
        val target = pointerTarget[id] ?: return
        when {
            target == "orb" -> {
                if (abs(x - orbDownX) > dp(8f) || abs(y - orbDownY) > dp(8f)) orbDragging = true
                if (orbDragging) {
                    orbX = (x / width).coerceIn(0.03f, 0.97f)
                    orbY = (y / height).coerceIn(0.05f, 0.95f)
                }
            }
            target == "look" -> {
                // 操作模式才发视角；编辑模式（含编辑器 Activity 预览）不注入
                if (!editMode) {
                    CallbackBridge.sendCursorDelta((x - CallbackBridge.mouseX) * 0.6f, 0f)
                }
            }
            editDragId == target -> {
                layout.buttons.firstOrNull { it.id == target }?.let { b ->
                    val nb = b.copy(x = (x / width).coerceIn(0f, 1f), y = (y / height).coerceIn(0f, 1f))
                    layout = layout.copy(buttons = layout.buttons.map { if (it.id == target) nb else it })
                }
            }
            else -> {
                val b = layout.buttons.firstOrNull { it.id == target } ?: return
                if (b.type == "joystick") {
                    val cx = b.x * width
                    val cy = b.y * height
                    val r = dp(b.w.toFloat()) / 2
                    var dx = x - cx
                    var dy = y - cy
                    val len = hypot(dx, dy)
                    if (len > r) { dx = dx / len * r; dy = dy / len * r }
                    stickPointers[b.id] = floatArrayOf(cx, cy, dx, dy)
                    val nx = dx / r
                    val ny = dy / r
                    applyStickKeys(nx, ny)
                } else if (b.type == "mouse" && !editMode) {
                    postLookDelta(x, y)
                }
            }
        }
    }

    private fun handleUp(id: Int) {
        val target = pointerTarget.remove(id) ?: return
        if (target == "orb") {
            if (!orbDragging) onOrbClick?.invoke()
            orbDragging = false
            return
        }
        if (target == "look") return
        editDragId = null
        val b = layout.buttons.firstOrNull { it.id == target } ?: return
        if (editMode) {
            onLayoutChanged?.invoke()
            return
        }
        pressUp(b)
        if (b.type == "joystick") stickPointers.remove(b.id)
    }

    // ── 输入注入 ──

    /** native 桥（libpojavexec/CallbackBridge）未就绪或处于编辑器预览时静默跳过 */
    private inline fun safeInject(block: () -> Unit) {
        try {
            block()
        } catch (_: Throwable) {
        }
    }

    private fun pressDown(b: TouchLayout.Button, pointerId: Int, x: Float, y: Float) {
        when (b.type) {
            "button" -> sendKey(b.key, true)
            "toggle" -> {
                val on = toggleStates[b.id] != true
                toggleStates[b.id] = on
                sendKey(b.key, on)
            }
            "mouse_left" -> safeInject { CallbackBridge.sendMouseButton(0, true) }
            "mouse_right" -> safeInject { CallbackBridge.sendMouseButton(1, true) }
            "joystick" -> {
                // 摇杆按下点决定初始方向
                handleMove(pointerId, x, y)
            }
            "mouse" -> postLookDelta(x, y)
        }
    }

    private fun pressUp(b: TouchLayout.Button) {
        when (b.type) {
            "button", "toggle" -> if (b.type == "button") sendKey(b.key, false)
            "mouse_left" -> safeInject { CallbackBridge.sendMouseButton(0, false) }
            "mouse_right" -> safeInject { CallbackBridge.sendMouseButton(1, false) }
            "joystick" -> {
                releaseStickKeys()
            }
        }
    }

    private val stickKeyCodes = intArrayOf(
        KeyEvent.KEYCODE_W, KeyEvent.KEYCODE_S, KeyEvent.KEYCODE_A, KeyEvent.KEYCODE_D,
    )

    private fun applyStickKeys(nx: Float, ny: Float) {
        val threshold = 0.45f
        val w = ny < -threshold
        val s = ny > threshold
        val a = nx < -threshold
        val d = nx > threshold
        val want = intArrayOf(
            if (w) stickKeyCodes[0] else 0,
            if (s) stickKeyCodes[1] else 0,
            if (a) stickKeyCodes[2] else 0,
            if (d) stickKeyCodes[3] else 0,
        ).filter { it != 0 }.toSet()
        // 释放不再需要的
        (activeKeys.filter { it in stickKeyCodes && it !in want }).forEach { code ->
            sendKey(code, false)
        }
        // 按下新增的
        (want - activeKeys).forEach { code -> sendKey(code, true) }
    }

    private fun releaseStickKeys() {
        stickKeyCodes.filter { it in activeKeys }.forEach { sendKey(it, false) }
    }

    private fun sendKey(androidKeyCode: Int, down: Boolean) {
        if (androidKeyCode <= 0) return
        if (down) {
            if (!activeKeys.add(androidKeyCode)) return
            safeInject {
                CallbackBridge.sendKeycode(androidKeyCode, '\u0000', 0, CallbackBridge.getCurrentMods(), true)
            }
        } else {
            if (!activeKeys.remove(androidKeyCode)) return
            safeInject {
                CallbackBridge.sendKeycode(androidKeyCode, '\u0000', 0, CallbackBridge.getCurrentMods(), false)
            }
        }
    }

    private fun postLookDelta(x: Float, y: Float) {
        if (editMode) return
        val b = buttonById("look")
        val centerX = b?.let { it.x * width } ?: (width / 2f)
        safeInject { CallbackBridge.sendCursorDelta((x - centerX) * 0.35f, 0f) }
    }

    // ── 编辑 ──

    /** 新增键位（编辑器调用）：放在屏幕中央偏移处 */
    fun addButton(type: String, key: Int, label: String, size: Int = 48) {
        val b = TouchLayout.Button(
            type = type, key = key, label = label,
            x = 0.5f, y = 0.5f, w = size, h = size,
        )
        layout = layout.copy(buttons = layout.buttons + b)
        selectedId = b.id
        onLayoutChanged?.invoke()
        invalidate()
    }

    fun removeButton(id: String) {
        layout = layout.copy(buttons = layout.buttons.filterNot { it.id == id })
        if (selectedId == id) selectedId = null
        onLayoutChanged?.invoke()
        invalidate()
    }

    fun deleteSelected() {
        selectedId?.let { removeButton(it) }
    }

    fun resizeSelected(deltaDp: Int) {
        val id = selectedId ?: return
        layout = layout.copy(buttons = layout.buttons.map {
            if (it.id == id) {
                val w = (it.w + deltaDp).coerceIn(28, 200)
                it.copy(w = w, h = w)
            } else it
        })
        onLayoutChanged?.invoke()
        invalidate()
    }

    fun renameSelected(label: String) {
        val id = selectedId ?: return
        layout = layout.copy(buttons = layout.buttons.map {
            if (it.id == id) it.copy(label = label) else it
        })
        onLayoutChanged?.invoke()
        invalidate()
    }

    fun rebindSelected(androidKeyCode: Int) {
        val id = selectedId ?: return
        layout = layout.copy(buttons = layout.buttons.map {
            if (it.id == id) it.copy(key = androidKeyCode) else it
        })
        onLayoutChanged?.invoke()
        invalidate()
    }

    fun selectedButton(): TouchLayout.Button? = layout.buttons.firstOrNull { it.id == selectedId }

    // ── 几何 ──

    private fun hit(b: TouchLayout.Button, x: Float, y: Float): Boolean {
        val cx = b.x * width
        val cy = b.y * height
        val half = if (b.type == "joystick") dp(b.w.toFloat()) / 2 else maxOf(dp(b.w.toFloat()), dp(b.h.toFloat())) / 2
        return abs(x - cx) <= half && abs(y - cy) <= half
    }

    private fun hitDelete(b: TouchLayout.Button, x: Float, y: Float): Boolean {
        val cx = b.x * width
        val cy = b.y * height
        val half = if (b.type == "joystick") dp(b.w.toFloat()) / 2 else maxOf(dp(b.w.toFloat()), dp(b.h.toFloat())) / 2
        val dx = cx + half - dp(4f)
        val dy = cy - half + dp(4f)
        return hypot(x - dx, y - dy) <= dp(14f)
    }

    private fun buttonById(id: String): TouchLayout.Button? =
        layout.buttons.firstOrNull { it.id == id }

    private fun dp(v: Float): Float = v * resources.displayMetrics.density
}
