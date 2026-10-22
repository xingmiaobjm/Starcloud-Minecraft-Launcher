// Starcloud ID 登录（对齐桌面 StarcloudIdService 协议）：
//   POST /v1/auth/login    {id,password} → {token,user_id,display_name,avatar_url}
//   POST /v1/auth/apikeys  Bearer token → {api_key}（幂等）
// token 一次性，api_key 才是长期凭证；方法阻塞式，须在 IO 协程调用。
package cn.starcloud.launcher.compose.data

import org.json.JSONObject
import java.net.HttpURLConnection
import java.net.URL

object AuthApi {

    private const val BASE = "https://starcloudfur.cn"

    data class Session(val userId: String, val apiKey: String, val displayName: String)

    fun login(starcloudId: String, password: String): Session {
        val loginRoot = JSONObject(
            send(
                "/v1/auth/login",
                JSONObject().put("id", starcloudId.trim()).put("password", password),
                null,
            ),
        )
        val token = loginRoot.optString("token")
        if (token.isBlank()) throw IllegalStateException("登录失败：服务端未返回令牌")
        val keyRoot = JSONObject(send("/v1/auth/apikeys", JSONObject(), token))
        val apiKey = keyRoot.optString("api_key")
        if (apiKey.isBlank()) throw IllegalStateException("登录失败：换取凭证为空")
        return Session(
            userId = loginRoot.optString("user_id"),
            apiKey = apiKey,
            displayName = loginRoot.optString("display_name"),
        )
    }

    private fun send(path: String, body: JSONObject, bearer: String?): String {
        val conn = (URL("$BASE$path").openConnection() as HttpURLConnection).apply {
            connectTimeout = 10_000
            readTimeout = 15_000
            requestMethod = "POST"
            if (bearer != null) setRequestProperty("Authorization", "Bearer $bearer")
            setRequestProperty("Content-Type", "application/json")
            doOutput = true
            outputStream.use { it.write(body.toString().toByteArray(Charsets.UTF_8)) }
        }
        try {
            val code = conn.responseCode
            val text = (if (code in 200..299) conn.inputStream else conn.errorStream)
                ?.bufferedReader(Charsets.UTF_8)?.use { it.readText() } ?: ""
            if (code !in 200..299) {
                val msg = runCatching {
                    JSONObject(text).optJSONObject("error")?.optString("message")
                }.getOrNull()?.takeIf { it.isNotBlank() }
                throw IllegalStateException(msg ?: "登录失败（HTTP $code）")
            }
            return text
        } finally {
            conn.disconnect()
        }
    }
}
