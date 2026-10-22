// 手机端 Agent（跨设备代理）：让同一局域网下的电脑端发现并调用本机。
//
// 与桌面端 DeviceLink 完全同协议：
//   · UDP 47777 每 3 秒广播 beacon {v,uid,name,port,app,iid}（uid = sha256(Starcloud ID uid) 前 16 hex）
//   · TCP 47778 服务端：hello 挑战 → HMAC-SHA256(apiKey) 双向签名 → JSON 行命令循环
//   · 命令：ping / device.info / saves.list / saves.check / saves.get / saves.put
//
// 凭证在设置页填写（Starcloud ID 的 uid 与 API Key），只落本机 SharedPreferences，UI 不回显完整 Key。
package cn.starcloud.launcher.compose.game

import android.content.Context
import android.os.Build
import cn.starcloud.launcher.compose.data.LauncherPrefs
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.ServerSocket
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.util.zip.ZipEntry
import java.util.zip.ZipOutputStream
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec

object AgentService {

    private const val BEACON_PORT = 47777
    private const val LINK_PORT = 47778
    private const val APP_ID = "starcloud"
    private const val BEACON_INTERVAL_MS = 3000L

    @Volatile
    var running = false
        private set

    private var beaconThread: Thread? = null
    private var serverThread: Thread? = null
    private var serverSocket: ServerSocket? = null
    private var beaconSocket: DatagramSocket? = null

    fun uidHash(uid: String): String {
        val digest = MessageDigest.getInstance("SHA-256").digest(uid.toByteArray(Charsets.UTF_8))
        return digest.joinToString("") { "%02x".format(it) }.take(16)
    }

    private fun sign(apiKey: String, message: String): String {
        val mac = Mac.getInstance("HMAC-SHA256")
        mac.init(SecretKeySpec(apiKey.toByteArray(Charsets.UTF_8), "HMAC-SHA256"))
        return mac.doFinal(message.toByteArray(Charsets.UTF_8))
            .joinToString("") { "%02x".format(it) }
    }

    private fun newIid(): String =
        java.util.UUID.randomUUID().toString().replace("-", "").take(12)

    fun start(context: Context) {
        if (running) return
        val uid = LauncherPrefs.starcloudUid(context)
        val apiKey = LauncherPrefs.starcloudKey(context)
        if (uid.isBlank() || apiKey.isBlank()) return
        running = true

        val iid = newIid()
        val deviceName = LauncherPrefs.agentDeviceName(context)
            .ifBlank { "${Build.MANUFACTURER} ${Build.MODEL}".trim() }

        beaconThread = Thread {
            val socket = DatagramSocket().apply {
                broadcast = true
                beaconSocket = this
            }
            val target = InetSocketAddress(InetAddress.getByName("255.255.255.255"), BEACON_PORT)
            while (running) {
                try {
                    val payload = JSONObject()
                        .put("v", 1)
                        .put("uid", uidHash(uid))
                        .put("name", deviceName)
                        .put("port", serverSocket?.localPort ?: LINK_PORT)
                        .put("app", APP_ID)
                        .put("iid", iid)
                    val bytes = payload.toString().toByteArray(Charsets.UTF_8)
                    socket.send(DatagramPacket(bytes, bytes.size, target))
                } catch (_: Exception) {
                }
                try {
                    Thread.sleep(BEACON_INTERVAL_MS)
                } catch (_: InterruptedException) {
                    break
                }
            }
            socket.close()
        }.apply { name = "starcloud-agent-beacon" }.also { it.start() }

        serverThread = Thread {
            var server: ServerSocket? = null
            try {
                val any = InetAddress.getByName("0.0.0.0")
                server = try {
                    ServerSocket(LINK_PORT, 50, any)
                } catch (_: Exception) {
                    ServerSocket(0, 50, any) // 端口被占退让临时端口，beacon 广播实际端口
                }
                serverSocket = server
                while (running) {
                    val client = server.accept()
                    Thread { serveClient(client, uid, apiKey, context, deviceName) }
                        .apply { name = "starcloud-agent-session" }.start()
                }
            } catch (_: Exception) {
            } finally {
                try {
                    server?.close()
                } catch (_: Exception) {
                }
            }
        }.apply { name = "starcloud-agent-server" }.also { it.start() }
    }

    fun stop() {
        running = false
        beaconSocket?.close()
        try {
            serverSocket?.close()
        } catch (_: Exception) {
        }
        beaconThread?.interrupt()
        serverThread?.interrupt()
        beaconThread = null
        serverThread = null
        serverSocket = null
    }

    // ── 单客户端会话：hello → 挑战应答 → 命令循环 ──

    private fun serveClient(
        client: Socket, uid: String, apiKey: String, context: Context, deviceName: String,
    ) {
        try {
            client.tcpNoDelay = true
            val input = client.getInputStream()
            val out = client.getOutputStream()

            val hello = JSONObject(readLine(input) ?: return)
            if (hello.optString("cmd") != "hello") return
            if (hello.optString("uid") != uidHash(uid)) return
            val c1 = hello.optString("challenge")
            if (c1.length !in 8..128) return

            val c2 = newIid()
            writeLine(out, JSONObject().put("challenge", c2))

            val their = JSONObject(readLine(input) ?: return)
            if (their.optString("sig") != sign(apiKey, c1 + c2)) return
            writeLine(out, JSONObject().put("sig", sign(apiKey, c2 + c1)))

            while (running) {
                val line = readLine(input) ?: break
                val req = JSONObject(line)
                val bodyFile = if (req.optBoolean("has_body")) {
                    val temp = File(context.cacheDir, "agent-body/${java.util.UUID.randomUUID()}.bin")
                    temp.parentFile?.mkdirs()
                    readBlock(input, temp)
                    temp
                } else null

                try {
                    val reply = handle(context, req, bodyFile, deviceName)
                    val json = JSONObject()
                        .put("ok", reply.optBoolean("ok"))
                        .put("err", if (reply.has("err")) reply.optString("err") else JSONObject.NULL)
                        .put("payload", reply.opt("payload") ?: JSONObject.NULL)
                    writeLine(out, json)
                    val source = reply.optString("source_file")
                    if (source.isNotEmpty()) {
                        writeFileBlock(out, source)
                        File(source).delete()
                    }
                } finally {
                    bodyFile?.delete()
                }
            }
        } catch (_: Exception) {
            // 单个会话异常只断开该连接
        } finally {
            try {
                client.close()
            } catch (_: Exception) {
            }
        }
    }

    // ── 命令分派（与桌面端 DeviceLinkService.HandleAsync 同名同义） ──

    private fun handle(
        context: Context, req: JSONObject, bodyFile: File?, deviceName: String,
    ): JSONObject {
        return when (req.optString("cmd")) {
            "ping" -> ok(JSONObject().put("pong", true).put("name", deviceName))
            "device.info" -> ok(
                JSONObject()
                    .put("name", deviceName)
                    .put("model", "${Build.MANUFACTURER} ${Build.MODEL}")
                    .put("os", "Android ${Build.VERSION.RELEASE}")
                    .put("app", APP_ID)
                    .put("agent", 1),
            )
            "saves.list" -> ok(JSONObject().put("saves", savesArray(context)))
            "saves.check" -> ok(
                JSONObject()
                    .put("action", checkAction(context, req))
                    .put("localLastPlayedMs", lastPlayedOf(context, req)),
            )
            "saves.put" -> receiveSave(context, req, bodyFile)
            "saves.get" -> sendSave(context, req)
            else -> fail("未知命令 ${req.optString("cmd")}")
        }
    }

    // ── 存档命令实现（游戏根 = getExternalFilesDir，兼容 versions/<id> 隔离） ──

    private fun filesRoot(context: Context): File =
        context.getExternalFilesDir(null) ?: context.filesDir

    private data class World(val folder: String, val dir: File, val versionId: String) {
        val size: Long get() = dir.walkBottomUp().filter { it.isFile }.sumOf { it.length() }
        val lastModified: Long get() = dir.lastModified()
    }

    private fun allWorlds(context: Context): List<World> {
        val root = filesRoot(context)
        val result = mutableListOf<World>()
        File(root, "saves").takeIf { it.isDirectory }?.let { saves ->
            saves.listFiles()?.filter { it.isDirectory }?.forEach {
                result += World(it.name, it, "")
            }
        }
        File(root, "versions").takeIf { it.isDirectory }?.listFiles()?.filter { it.isDirectory }?.forEach { ver ->
            File(ver, "saves").takeIf { it.isDirectory }?.listFiles()?.filter { it.isDirectory }?.forEach {
                result += World(it.name, it, ver.name)
            }
        }
        return result.distinctBy { it.folder + "@" + it.dir.path }
    }

    private fun findWorld(context: Context, versionId: String, folder: String): World? =
        allWorlds(context).firstOrNull { it.folder == folder && (versionId.isBlank() || it.versionId == versionId) }

    private fun savesArray(context: Context): JSONArray {
        val arr = JSONArray()
        allWorlds(context).forEach { w ->
            arr.put(
                JSONObject()
                    .put("folder", w.folder)
                    .put("name", w.folder)
                    .put("lastPlayedMs", w.lastModified)
                    .put("size", w.size)
                    .put("versionId", w.versionId)
                    .put("isolated", w.versionId.isNotBlank()),
            )
        }
        return arr
    }

    private fun lastPlayedOf(context: Context, req: JSONObject): Long =
        findWorld(context, req.optString("versionId"), req.optString("folder"))?.lastModified ?: 0

    private fun checkAction(context: Context, req: JSONObject): String {
        val world = findWorld(context, req.optString("versionId"), req.optString("folder"))
            ?: return "Accept"
        val incoming = req.optLong("lastPlayedMs")
        return if (incoming >= world.lastModified) "Accept" else "AcceptWithBackup"
    }

    private fun receiveSave(context: Context, req: JSONObject, bodyFile: File?): JSONObject {
        val folder = req.optString("folder").ifBlank { return fail("世界目录名非法") }
        if (bodyFile == null || !bodyFile.exists()) return fail("缺少存档数据")
        val mc = req.optString("versionId")
        val savesDir = if (mc.isBlank()) File(filesRoot(context), "saves")
        else File(File(filesRoot(context), "versions"), mc + "/saves")
        val destDir = File(savesDir, folder)

        // 冲突保护：本地同名世界较新时先改名保留
        if (destDir.exists() && destDir.lastModified() > req.optLong("lastPlayedMs")) {
            val stamp = java.text.SimpleDateFormat("yyyyMMdd-HHmmss", java.util.Locale.US)
                .format(java.util.Date())
            destDir.renameTo(File(savesDir, "$folder.conflict-$stamp"))
        }
        destDir.mkdirs()
        unzip(bodyFile, destDir)

        // 版本配置随存档走（与桌面端 pending-config 同路径约定）
        val cfgJson = req.optString("cfgJson")
        if (cfgJson.isNotEmpty()) {
            val pending = File(filesRoot(context), "gamestate/pending-config/$folder.json")
            pending.parentFile?.mkdirs()
            pending.writeText(cfgJson, Charsets.UTF_8)
        }
        return ok(JSONObject().put("received", folder))
    }

    private fun sendSave(context: Context, req: JSONObject): JSONObject {
        val folder = req.optString("folder").ifBlank { return fail("世界目录名非法") }
        val world = findWorld(context, req.optString("versionId"), folder)
            ?: return fail("对端没有这个世界（可能已被删除）")
        val temp = File(context.cacheDir, "agent-out/${java.util.UUID.randomUUID()}.zip")
        temp.parentFile?.mkdirs()
        ZipOutputStream(FileOutputStream(temp)).use { zipOut ->
            world.dir.walkTopDown().filter { it.isFile }.forEach { file ->
                zipOut.putNextEntry(ZipEntry(file.relativeTo(world.dir).path))
                file.inputStream().use { it.copyTo(zipOut) }
                zipOut.closeEntry()
            }
        }
        val sha256 = MessageDigest.getInstance("SHA-256")
            .digest(temp.readBytes()).joinToString("") { "%02x".format(it) }
        return ok(
            JSONObject()
                .put("size", temp.length())
                .put("sha256", sha256),
        ).put("source_file", temp.path)
    }

    // ── 小工具 ──

    private fun ok(payload: org.json.JSONObject): JSONObject =
        JSONObject().put("ok", true).put("payload", payload)

    private fun fail(error: String): JSONObject =
        JSONObject().put("ok", false).put("err", error)

    private fun writeLine(out: java.io.OutputStream, payload: JSONObject) {
        out.write((payload.toString() + "\n").toByteArray(Charsets.UTF_8))
        out.flush()
    }

    private fun readLine(input: java.io.InputStream): String? {
        val buffer = StringBuilder()
        val one = ByteArray(1)
        while (true) {
            val read = input.read(one)
            if (read <= 0) return null
            if (one[0] == '\n'.code.toByte()) break
            buffer.append(one[0].toInt().toChar())
            if (buffer.length > 64 * 1024) throw java.io.IOException("链路行超长")
        }
        return buffer.toString()
    }

    private fun writeFileBlock(out: java.io.OutputStream, path: String) {
        val size = File(path).length()
        val header = ByteArray(8)
        for (i in 0 until 8) header[i] = ((size ushr (8 * i)) and 0xFF).toByte()
        out.write(header)
        File(path).inputStream().use { it.copyTo(out, 1 shl 16) }
        out.flush()
    }

    private fun readBlock(input: java.io.InputStream, dest: File) {
        val header = ByteArray(8)
        var off = 0
        while (off < 8) {
            val read = input.read(header, off, 8 - off)
            if (read <= 0) throw java.io.IOException("对端在传输中途断开")
            off += read
        }
        var remaining = 0L
        for (i in 0 until 8) remaining = remaining or ((header[i].toLong() and 0xFF) shl (8 * i))
        if (remaining < 0 || remaining > 8L * 1024 * 1024 * 1024) throw java.io.IOException("链路块尺寸非法")
        FileOutputStream(dest).use { fileOut ->
            val buffer = ByteArray(1 shl 16)
            var left = remaining
            while (left > 0) {
                val take = minOf(left, buffer.size.toLong()).toInt()
                val read = input.read(buffer, 0, take)
                if (read <= 0) throw java.io.IOException("对端在传输中途断开")
                fileOut.write(buffer, 0, read)
                left -= read
            }
        }
    }

    private fun unzip(zipFile: File, destDir: File) {
        destDir.mkdirs()
        java.util.zip.ZipInputStream(zipFile.inputStream()).use { zipIn ->
            while (true) {
                val entry: ZipEntry = zipIn.nextEntry ?: break
                if (entry.isDirectory) {
                    zipIn.closeEntry()
                    continue
                }
                val dest = safeResolve(destDir, entry.name)
                if (dest == null) {
                    zipIn.closeEntry()
                } else {
                    dest.parentFile?.mkdirs()
                    FileOutputStream(dest).use { zipIn.copyTo(it) }
                }
                FileOutputStream(dest).use { zipIn.copyTo(it) }
                zipIn.closeEntry()
            }
        }
    }

    private fun safeResolve(root: File, relative: String): File? {
        val parts = relative.replace('\\', '/').split('/')
            .filter { it.isNotEmpty() && it != "." && it != ".." }
        if (parts.isEmpty() || parts.any { it.contains(':') }) return null
        val full = File(root, parts.joinToString(File.separator)).canonicalFile
        return if (full.path.startsWith(root.canonicalFile.path)) full else null
    }
}
