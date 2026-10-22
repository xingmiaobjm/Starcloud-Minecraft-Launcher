// 联机控制面（对齐桌面 ProtocolMultiplayerProvider 的陶瓦同形协议）：
//   核心是本地 HTTP：GET /state 探活与查状态；建房 /state/scanning?player=X&public_nodes=…；
//   进房 /state/guesting?room=X&player=X；回空闲 /state/ide。
//   状态字符串映射到归一阶段（waiting→空闲 / host-ok→房间已开（room=邀请码）/ …）。
// 手机端核心不随包分发：探活 <prefs 端口> 的 /state，通则可用（例如 Termux 里跑的核心）。
package cn.starcloud.launcher.compose.data

import android.content.Context
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import kotlinx.coroutines.Dispatchers
import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

object MultiplayerController {

    /** 归一阶段（对齐桌面 MultiplayerPhase） */
    enum class Phase { Idle, HostScanning, HostStarting, HostReady, GuestConnecting, GuestEntering, GuestReady, Failed, CoreMissing }

    data class State(
        val phase: Phase = Phase.CoreMissing,
        val room: String? = null,
        val address: String? = null,
        val players: Int = 0,
        val error: String? = null,
    ) {
        val inRoom: Boolean get() = phase in setOf(Phase.HostReady, Phase.GuestReady, Phase.GuestEntering)
    }

    private val _state = MutableStateFlow(State())
    val state: StateFlow<State> = _state

    private val pollJobs = HashMap<String, Job>()

    /** 手机端说明：核心进程不随启动器分发，探活本机端口（Termux/未来安卓核心均可） */
    fun configuredPort(context: Context): Int = LauncherPrefs.multiplayerPort(context)

    /** 立即探活一次并更新状态（IO 调用） */
    suspend fun refresh(context: Context) {
        val p = configuredPort(context)
        if (p <= 0) {
            _state.value = State(Phase.CoreMissing)
            return
        }
        _state.value = try {
            parse(withContext(Dispatchers.IO) { get(p, "/state") })
        } catch (e: Exception) {
            State(Phase.CoreMissing)
        }
    }

    /** 开房间（游戏需先「对局域网开放」） */
    suspend fun host(context: Context, playerName: String, publicNodes: List<String> = emptyList()): Result<Unit> {
        val p = configuredPort(context)
        if (p <= 0) return Result.failure(IllegalStateException("未配置联机核心端口"))
        return runCatching {
            val q = buildString {
                append("/state/scanning?player=")
                append(URLEncoder.encode(playerName, "UTF-8"))
                publicNodes.forEach { n ->
                    append("&public_nodes=")
                    append(URLEncoder.encode(n, "UTF-8"))
                }
            }
            withContext(Dispatchers.IO) { get(p, q) }
            refresh(context)
        }
    }

    /** 进房（room = 好友邀请里的邀请码） */
    suspend fun join(context: Context, room: String, playerName: String, publicNodes: List<String> = emptyList()): Result<Unit> {
        val p = configuredPort(context)
        if (p <= 0) return Result.failure(IllegalStateException("未配置联机核心端口"))
        return runCatching {
            val q = buildString {
                append("/state/guesting?room=")
                append(URLEncoder.encode(room.trim(), "UTF-8"))
                append("&player=")
                append(URLEncoder.encode(playerName, "UTF-8"))
                publicNodes.forEach { n ->
                    append("&public_nodes=")
                    append(URLEncoder.encode(n, "UTF-8"))
                }
            }
            withContext(Dispatchers.IO) { get(p, q) }
            refresh(context)
        }
    }

    /** 回到空闲（关房/退出） */
    suspend fun idle(context: Context): Result<Unit> {
        val p = configuredPort(context)
        if (p <= 0) return Result.failure(IllegalStateException("未配置联机核心端口"))
        return runCatching {
            withContext(Dispatchers.IO) { get(p, "/state/ide") }
            refresh(context)
        }
    }

    /** 周期轮询（联机面板 / 悬浮窗显示期间）；owner 区分持有者，互不干扰 */
    fun startPolling(scope: CoroutineScope, context: Context, owner: String, intervalMs: Long = 3_000) {
        stopPolling(owner)
        pollJobs[owner] = scope.launch {
            while (isActive) {
                refresh(context)
                delay(intervalMs)
            }
        }
    }

    fun stopPolling(owner: String) {
        pollJobs.remove(owner)?.cancel()
    }

    /** 游戏启动成功后挂钩：开关开着就自动建房（失败不影响游戏） */
    fun onGameLaunched(context: Context, playerName: String) {
        if (!LauncherPrefs.multiplayerAuto(context)) return
        CoroutineScope(SupervisorJob() + Dispatchers.Default).launch {
            runCatching { host(context, playerName) }
        }
    }

    // ── 内部 ──

    private fun parse(raw: String): State {
        val o = JSONObject(raw)
        val rawState = o.optString("state", "waiting")
        val phase = when (rawState) {
            "waiting" -> Phase.Idle
            "host-scanning" -> Phase.HostScanning
            "host-starting" -> Phase.HostStarting
            "host-ok" -> Phase.HostReady
            "guest-connecting" -> Phase.GuestConnecting
            "guest-starting" -> Phase.GuestEntering
            "guest-ok" -> Phase.GuestReady
            "exception" -> Phase.Failed
            else -> Phase.Idle
        }
        return State(
            phase = phase,
            room = o.optString("room").ifBlank { null },
            address = o.optString("address").ifBlank { null },
            players = o.optInt("players", 0),
            error = o.optString("error_type").ifBlank { null },
        )
    }

    private fun get(port: Int, path: String): String =
        (URL("http://127.0.0.1:$port$path").openConnection() as HttpURLConnection).run {
            connectTimeout = 2_000
            readTimeout = 4_000
            requestMethod = "GET"
            val code = responseCode
            val text = (if (code in 200..299) inputStream else errorStream)
                ?.bufferedReader(Charsets.UTF_8)?.use { it.readText() } ?: ""
            if (code !in 200..299) throw IllegalStateException("核心响应 HTTP $code")
            text
        }
}
