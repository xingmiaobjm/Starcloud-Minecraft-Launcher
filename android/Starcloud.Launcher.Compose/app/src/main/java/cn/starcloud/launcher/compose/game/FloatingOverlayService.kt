// 游戏内悬浮窗（联机控制）：悬浮球可拖动，点开展开面板——
//   状态（阶段 + 邀请码/地址）、开房/关房、邀请好友（回启动器好友页·联机标签）。
// 悬浮窗用 TYPE_APPLICATION_OVERLAY，需用户授予「显示在其他应用上层」权限。
// 联机状态来自 MultiplayerController（陶瓦同形本地 HTTP 控制面）。
package cn.starcloud.launcher.compose.game

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.graphics.Color
import android.graphics.PixelFormat
import android.os.IBinder
import android.view.Gravity
import android.view.MotionEvent
import android.view.View
import android.view.WindowManager
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import cn.starcloud.launcher.compose.MainActivity
import cn.starcloud.launcher.compose.data.LauncherPrefs
import cn.starcloud.launcher.compose.data.MultiplayerController
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.launch

class FloatingOverlayService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main)
    private var wm: WindowManager? = null
    private var ball: TextView? = null
    private var panel: LinearLayout? = null
    private var statusText: TextView? = null
    private var toggleButton: Button? = null

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        startForegroundWithType()
        wm = getSystemService(WINDOW_SERVICE) as WindowManager
        addBall()
        MultiplayerController.startPolling(scope, this, owner = "overlay", intervalMs = 3_000)
        scope.launch {
            MultiplayerController.state.collect { updatePanel(it) }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int = START_STICKY

    override fun onDestroy() {
        MultiplayerController.stopPolling("overlay")
        scope.cancel()
        ball?.let { wm?.removeView(it) }
        panel?.let { wm?.removeView(it) }
        super.onDestroy()
    }

    // ── 悬浮球 ──

    private fun overlayFlags(): Int =
        WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE or WindowManager.LayoutParams.FLAG_LAYOUT_NO_LIMITS

    private fun ballParams(): WindowManager.LayoutParams = WindowManager.LayoutParams(
        dp(46), dp(46),
        WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
        overlayFlags(),
        PixelFormat.TRANSLUCENT,
    ).apply {
        gravity = Gravity.TOP or Gravity.START
        x = dp(8)
        y = dp(120)
    }

    private fun addBall() {
        val v = TextView(this).apply {
            text = "联"
            textSize = 16f
            setTextColor(Color.WHITE)
            gravity = Gravity.CENTER
            setBackgroundResource(android.R.drawable.dialog_frame)
            setBackgroundColor(0xCC1A1A1A.toInt())
        }
        var downX = 0f; var downY = 0f
        var startX = 0; var startY = 0
        var moved = false
        val params = ballParams()
        v.setOnTouchListener { _, ev ->
            when (ev.action) {
                MotionEvent.ACTION_DOWN -> {
                    downX = ev.rawX; downY = ev.rawY
                    startX = params.x; startY = params.y; moved = false; true
                }
                MotionEvent.ACTION_MOVE -> {
                    val dx = (ev.rawX - downX).toInt(); val dy = (ev.rawY - downY).toInt()
                    if (moved || dx * dx + dy * dy > 100) {
                        moved = true
                        params.x = startX + dx
                        params.y = startY + dy
                        wm?.updateViewLayout(v, params)
                    }
                    true
                }
                MotionEvent.ACTION_UP -> {
                    if (!moved) togglePanel()
                    true
                }
                else -> false
            }
        }
        ball = v
        wm?.addView(v, params)
    }

    // ── 展开面板 ──

    private fun togglePanel() {
        if (panel != null) hidePanel() else showPanel()
    }

    private fun panelParams(): WindowManager.LayoutParams = WindowManager.LayoutParams(
        dp(230), WindowManager.LayoutParams.WRAP_CONTENT,
        WindowManager.LayoutParams.TYPE_APPLICATION_OVERLAY,
        WindowManager.LayoutParams.FLAG_NOT_FOCUSABLE,
        PixelFormat.TRANSLUCENT,
    ).apply {
        gravity = Gravity.TOP or Gravity.START
        x = dp(60)
        y = dp(100)
    }

    private fun showPanel() {
        val ctx = this
        val box = LinearLayout(ctx).apply {
            orientation = LinearLayout.VERTICAL
            setBackgroundColor(0xF0202020.toInt())
            setPadding(dp(12), dp(10), dp(12), dp(10))
        }
        val title = TextView(ctx).apply {
            text = "联机"
            textSize = 14f
            setTextColor(Color.WHITE)
        }
        val status = TextView(ctx).apply {
            textSize = 12f
            setTextColor(0xFFCCCCCC.toInt())
            setPadding(0, dp(6), 0, dp(6))
        }
        statusText = status
        val row = LinearLayout(ctx).apply {
            orientation = LinearLayout.HORIZONTAL
        }
        val toggle = Button(ctx).apply {
            textSize = 12f
            setOnClickListener {
                val st = MultiplayerController.state.value
                scope.launch {
                    if (st.inRoom) MultiplayerController.idle(ctx)
                    else MultiplayerController.host(ctx, LauncherPrefs.playerName(ctx))
                }
            }
        }
        toggleButton = toggle
        val invite = Button(ctx).apply {
            text = "邀请好友"
            textSize = 12f
            setOnClickListener {
                val intent = Intent(ctx, MainActivity::class.java)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK or Intent.FLAG_ACTIVITY_SINGLE_TOP)
                    .putExtra(EXTRA_OPEN_TAB, "multiplayer")
                startActivity(intent)
                hidePanel()
            }
        }
        row.addView(toggle, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        row.addView(invite, LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f))
        box.addView(title)
        box.addView(status, LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
        box.addView(row, LinearLayout.LayoutParams.MATCH_PARENT, LinearLayout.LayoutParams.WRAP_CONTENT)
        panel = box
        wm?.addView(box, panelParams())
        updatePanel(MultiplayerController.state.value)
    }

    private fun hidePanel() {
        panel?.let { wm?.removeView(it) }
        panel = null
    }

    private fun updatePanel(st: MultiplayerController.State) {
        val phaseText = when (st.phase) {
            MultiplayerController.Phase.CoreMissing -> "未检测到联机核心"
            MultiplayerController.Phase.Idle -> "空闲（游戏先「对局域网开放」）"
            MultiplayerController.Phase.HostScanning -> "正在扫描局域网世界…"
            MultiplayerController.Phase.HostStarting -> "房间启动中…"
            MultiplayerController.Phase.HostReady -> "房间已开 · 邀请码 ${st.room ?: "-"}"
            MultiplayerController.Phase.GuestConnecting -> "连接房间中…"
            MultiplayerController.Phase.GuestEntering -> "进入虚拟局域网…"
            MultiplayerController.Phase.GuestReady -> "已联机 · ${st.address ?: ""} ${st.players}人"
            MultiplayerController.Phase.Failed -> "联机异常${st.error?.let { "（$it）" } ?: ""}"
        }
        statusText?.text = phaseText
        toggleButton?.text = if (st.inRoom) "关闭联机" else "开启联机"
    }

    // ── 前台服务 ──

    private fun startForegroundWithType() {
        val channelId = "multiplayer_overlay"
        val nm = getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        if (android.os.Build.VERSION.SDK_INT >= 26) {
            nm.createNotificationChannel(
                NotificationChannel(channelId, "联机悬浮窗", NotificationManager.IMPORTANCE_MIN),
            )
        }
        val notification: Notification =
            if (android.os.Build.VERSION.SDK_INT >= 26) {
                Notification.Builder(this, channelId)
                    .setSmallIcon(android.R.drawable.ic_menu_share)
                    .setContentText("联机控制悬浮窗运行中")
                    .build()
            } else {
                @Suppress("DEPRECATION")
                Notification.Builder(this)
                    .setSmallIcon(android.R.drawable.ic_menu_share)
                    .setContentText("联机控制悬浮窗运行中")
                    .build()
            }
        if (android.os.Build.VERSION.SDK_INT >= 34) {
            startForeground(NOTIFY_ID, notification,
                android.content.pm.ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE)
        } else {
            startForeground(NOTIFY_ID, notification)
        }
    }

    private fun dp(v: Int): Int = (v * resources.displayMetrics.density).toInt()

    companion object {
        private const val NOTIFY_ID = 42
        const val EXTRA_OPEN_TAB = "open_tab"

        fun start(context: Context) {
            context.startForegroundService(Intent(context, FloatingOverlayService::class.java))
        }

        fun stop(context: Context) {
            context.stopService(Intent(context, FloatingOverlayService::class.java))
        }

        /** 悬浮窗权限是否已授予 */
        fun canDraw(context: Context): Boolean =
            android.provider.Settings.canDrawOverlays(context)
    }
}
