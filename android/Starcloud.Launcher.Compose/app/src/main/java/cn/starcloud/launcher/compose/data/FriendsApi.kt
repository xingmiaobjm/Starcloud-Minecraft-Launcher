// Starcloud 好友网络层（对齐桌面 Starcloud.Core/FriendsService 协议）：
//   基址 https://starcloudfur.cn，鉴权 Authorization: Bearer <api_key>
//   错误体统一 {"error":{"code","message"}}，message 已中文化直接展示
//   SSE：GET /v1/friends/events（friend_request / friend_request_accepted /
//         friend_message / invite / invite_accepted / invite_declined / invite_canceled）
// 所有方法阻塞式，必须在 IO 协程调用。
package cn.starcloud.launcher.compose.data

import org.json.JSONArray
import org.json.JSONObject
import java.io.BufferedReader
import java.io.InputStreamReader
import java.net.HttpURLConnection
import java.net.URL
import java.net.URLEncoder

object FriendsApi {

    private const val BASE = "https://starcloudfur.cn"

    // ── 数据模型 ──

    data class Profile(
        val userId: String,
        val id: String,
        val displayName: String,
        val avatarUrl: String?,
    )

    data class Friend(
        val profile: Profile,
        val since: String,
        val unread: Int,
    )

    data class RequestView(
        val requestId: String,
        val message: String?,
        val createdAt: String,
        val user: Profile,
    )

    data class Requests(
        val incoming: List<RequestView>,
        val outgoing: List<RequestView>,
    )

    data class SearchResult(
        val profile: Profile,
        val relation: String,
    )

    data class Invite(
        val inviteId: String,
        val fromUserId: String,
        val toUserId: String,
        val address: String?,
        val port: Int?,
        val roomCode: String?,
        val message: String?,
        val status: String,
        val createdAt: String,
        val user: Profile,
    ) {
        /** 地址或房间码——邀请里至少有一个 */
        val connectionText: String
            get() = roomCode?.takeIf { it.isNotEmpty() }
                ?: port?.let { "${address ?: ""}:$it" }
                ?: address ?: ""
    }

    data class Invites(val incoming: List<Invite>, val outgoing: List<Invite>)

    data class Message(
        val id: Long,
        val fromUserId: String,
        val toUserId: String,
        val content: String,
        val createdAt: String,
    )

    data class Conversation(val items: List<Message>, val hasMore: Boolean, val nextBeforeId: Long?)

    data class Snapshot(
        val friends: List<Friend>,
        val requests: Requests,
        val invites: Invites,
    )

    // ── 接口 ──

    fun friends(key: String): List<Friend> {
        val root = JSONObject(send("GET", "/v1/friends", null, key))
        val arr = root.optJSONArray("friends") ?: JSONArray()
        return (0 until arr.length()).mapNotNull { i ->
            arr.optJSONObject(i)?.let { o ->
                Friend(
                    profile = o.profile(),
                    since = o.optString("since"),
                    unread = o.optInt("unread", 0),
                )
            }
        }
    }

    fun requests(key: String): Requests {
        val root = JSONObject(send("GET", "/v1/friends/requests", null, key))
        fun side(name: String): List<RequestView> {
            val arr = root.optJSONArray(name) ?: JSONArray()
            return (0 until arr.length()).mapNotNull { i ->
                arr.optJSONObject(i)?.let { o ->
                    RequestView(
                        requestId = o.optString("request_id"),
                        message = o.optString("message").ifBlank { null },
                        createdAt = o.optString("created_at"),
                        user = o.optJSONObject("user")?.profile() ?: return@mapNotNull null,
                    )
                }
            }
        }
        return Requests(side("incoming"), side("outgoing"))
    }

    fun search(key: String, query: String): List<SearchResult> {
        val root = JSONObject(
            send("GET", "/v1/friends/search?q=" + URLEncoder.encode(query, "UTF-8"), null, key),
        )
        val arr = root.optJSONArray("users") ?: JSONArray()
        return (0 until arr.length()).mapNotNull { i ->
            arr.optJSONObject(i)?.let { o ->
                SearchResult(
                    profile = o.profile(),
                    relation = o.optString("relation"),
                )
            }
        }
    }

    /** 发好友申请（target = 对方 StarcloudID 或用户 id） */
    fun sendRequest(key: String, target: String, message: String?) {
        val body = JSONObject().put("target", target)
        if (!message.isNullOrBlank()) body.put("message", message.trim())
        send("POST", "/v1/friends/requests", body, key)
    }

    fun acceptRequest(key: String, requestId: String) {
        send("POST", "/v1/friends/requests/$requestId/accept", JSONObject(), key)
    }

    fun rejectRequest(key: String, requestId: String) {
        send("POST", "/v1/friends/requests/$requestId/reject", JSONObject(), key)
    }

    fun withdrawRequest(key: String, requestId: String) {
        send("DELETE", "/v1/friends/requests/$requestId", null, key)
    }

    fun removeFriend(key: String, userId: String) {
        send("DELETE", "/v1/friends/$userId", null, key)
    }

    fun invites(key: String): Invites {
        val root = JSONObject(send("GET", "/v1/friends/invites", null, key))
        fun side(name: String): List<Invite> {
            val arr = root.optJSONArray(name) ?: JSONArray()
            return (0 until arr.length()).mapNotNull { i -> arr.optJSONObject(i)?.invite() }
        }
        return Invites(side("incoming"), side("outgoing"))
    }

    /** 发联机邀请（address/port/room_code 至少一项） */
    fun sendInvite(key: String, toUserId: String, address: String?, port: Int?, roomCode: String?, message: String?) {
        val body = JSONObject()
        if (!address.isNullOrBlank()) body.put("address", address.trim())
        if (port != null) body.put("port", port)
        if (!roomCode.isNullOrBlank()) body.put("room_code", roomCode.trim())
        if (!message.isNullOrBlank()) body.put("message", message.trim())
        send("POST", "/v1/friends/$toUserId/invites", body, key)
    }

    fun acceptInvite(key: String, inviteId: String): Invite =
        JSONObject(send("POST", "/v1/friends/invites/$inviteId/accept", JSONObject(), key)).invite()

    fun declineInvite(key: String, inviteId: String): Invite =
        JSONObject(send("POST", "/v1/friends/invites/$inviteId/decline", JSONObject(), key)).invite()

    fun cancelInvite(key: String, inviteId: String) {
        send("DELETE", "/v1/friends/invites/$inviteId", null, key)
    }

    fun conversation(key: String, peerUserId: String, beforeId: Long? = null, limit: Int = 50): Conversation {
        var path = "/v1/friends/$peerUserId/messages?limit=$limit"
        if (beforeId != null) path += "&before_id=$beforeId"
        val root = JSONObject(send("GET", path, null, key))
        val arr = root.optJSONArray("items") ?: JSONArray()
        return Conversation(
            items = (0 until arr.length()).mapNotNull { i ->
                arr.optJSONObject(i)?.let { o ->
                    Message(
                        id = o.optLong("id"),
                        fromUserId = o.optString("from_user_id"),
                        toUserId = o.optString("to_user_id"),
                        content = o.optString("content"),
                        createdAt = o.optString("created_at"),
                    )
                }
            },
            hasMore = root.optBoolean("has_more"),
            nextBeforeId = if (root.isNull("next_before_id")) null else root.optLong("next_before_id"),
        )
    }

    fun sendMessage(key: String, peerUserId: String, content: String): Message =
        JSONObject(
            send(
                "POST", "/v1/friends/$peerUserId/messages",
                JSONObject().put("content", content), key,
            ),
        ).let { o ->
            Message(
                id = o.optLong("id"),
                fromUserId = o.optString("from_user_id"),
                toUserId = o.optString("to_user_id"),
                content = o.optString("content"),
                createdAt = o.optString("created_at"),
            )
        }

    fun markRead(key: String, peerUserId: String) {
        send("POST", "/v1/friends/$peerUserId/messages/read", JSONObject(), key)
    }

    /**
     * SSE 长连接：阻塞读取事件直到连接断开，逐条回调 (type, dataJson)。
     * 断开由调用方取消协程实现；重连退避在调用方处理。
     */
    fun streamEvents(key: String, onEvent: (String, String) -> Unit) {
        val conn = (URL("$BASE/v1/friends/events").openConnection() as HttpURLConnection).apply {
            connectTimeout = 10_000
            readTimeout = 0 // 永不超时，靠调用方取消
            requestMethod = "GET"
            setRequestProperty("Authorization", "Bearer $key")
            setRequestProperty("Accept", "text/event-stream")
        }
        try {
            BufferedReader(InputStreamReader(conn.inputStream, Charsets.UTF_8)).use { reader ->
                var eventType = ""
                while (true) {
                    val line = reader.readLine() ?: break
                    when {
                        line.startsWith("event:") -> eventType = line.removePrefix("event:").trim()
                        line.startsWith("data:") -> {
                            val data = line.removePrefix("data:").trim()
                            if (eventType.isNotEmpty() && data.isNotEmpty()) onEvent(eventType, data)
                        }
                        line.isEmpty() -> eventType = ""
                    }
                }
            }
        } finally {
            conn.disconnect()
        }
    }

    // ── 内部 ──

    private fun JSONObject.profile() = Profile(
        userId = optString("user_id"),
        id = optString("id"),
        displayName = optString("display_name"),
        avatarUrl = optString("avatar_url").ifBlank { null },
    )

    private fun JSONObject.invite() = Invite(
        inviteId = optString("invite_id"),
        fromUserId = optString("from_user_id"),
        toUserId = optString("to_user_id"),
        address = optString("address").ifBlank { null },
        port = if (isNull("port")) null else optInt("port"),
        roomCode = optString("room_code").ifBlank { null },
        message = optString("message").ifBlank { null },
        status = optString("status"),
        createdAt = optString("created_at"),
        user = optJSONObject("user")?.profile() ?: Profile("", "", "未知用户", null),
    )

    private fun send(method: String, path: String, body: JSONObject?, key: String): String {
        val conn = (URL("$BASE$path").openConnection() as HttpURLConnection).apply {
            connectTimeout = 10_000
            readTimeout = 15_000
            requestMethod = method
            setRequestProperty("Authorization", "Bearer $key")
            if (body != null) {
                setRequestProperty("Content-Type", "application/json")
                doOutput = true
                outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
            }
        }
        try {
            val code = conn.responseCode
            val text = (if (code in 200..299) conn.inputStream else conn.errorStream)
                ?.bufferedReader(Charsets.UTF_8)?.use { it.readText() } ?: ""
            if (code !in 200..299) {
                val msg = runCatching {
                    JSONObject(text).optJSONObject("error")?.optString("message")
                }.getOrNull()?.takeIf { it.isNotBlank() }
                throw IllegalStateException(msg ?: "请求失败（HTTP $code）")
            }
            return text
        } finally {
            conn.disconnect()
        }
    }
}
