// 完整启动编排（对齐 C# GameLauncherService.LaunchAsync）：
// 部署组件 → Java 桥就绪 → 构建参数 → 环境预置 → SDL 游戏活动 → JVM 线程 → 退出放行。
package cn.starcloud.launcher.compose.game

import android.content.Context
import android.content.Intent
import android.util.Log
import cn.starcloud.launcher.compose.data.ComponentDeployer
import cn.starcloud.launcher.compose.data.ComponentManifest
import org.json.JSONObject
import java.io.File
import java.util.UUID

object GameLauncher {


    private const val TAG = "StarcloudFlow"

    /** 启动状态回传：phase 为人类可读进度行；exitCode 非空表示游戏已退出 */
    interface Listener {
        fun onLog(line: String)
        fun onProgress(phase: String)
        fun onFinished(exitCode: Int, error: String?)
    }

    /** 内部 files 目录的规范路径（/data/data/<pkg>/files 形式）：
        linker namespace permitted_paths 按路径字符串匹配，/data/user 视图在 Release 下不命中 */
    fun normalizedFilesDir(context: Context): String {
        val path = context.filesDir.absolutePath
        val prefix = "/data/user/"
        if (!path.startsWith(prefix)) return path
        val rest = path.removePrefix(prefix)               // "<uid>/<pkg>/files"
        val uidEnd = rest.indexOf('/')
        return if (uidEnd > 0) "/data/data/" + rest.substring(uidEnd + 1) else path
    }

    /**
     * 按指定版本完整启动。挂起在 IO 语义线程（调用方负责协程调度）；
     * JVM 在专门的非守护线程上运行，本函数在「SDL 活动就绪 + JVM 线程已拉起」后返回，
     * 游戏退出经 listener.onFinished 通知。
     */
    fun launch(context: Context, versionId: String, playerName: String, listener: Listener) {
        try {
            val componentsRoot = File(normalizedFilesDir(context), "components")
            val cacheRoot = File(context.getExternalFilesDir(null)!!, "cache")
            val dotMc = File(context.getExternalFilesDir(null)!!, ".minecraft")
            val tmpDir = File(context.cacheDir, "tmp").apply { mkdirs() }

            listener.onLog("[flow] 启动链开始 version=$versionId")
            val manifest: ComponentManifest = ComponentDeployer.ensureComponents(
                context, componentsRoot, cacheRoot,
            ) { phase, cur, total ->
                listener.onProgress("$phase（$cur/$total）")
            }
            listener.onProgress("组件就绪")

            // pojavexec：glfw-stub 组件目录下的 libpojavexec.so（Java 桥静态块的加载目标）
            val glfwEntry = manifest.require("glfw-stub")
            val glfwDir = ComponentDeployer.componentDir(componentsRoot, glfwEntry.name, glfwEntry.version)
            val pojavExec = File(glfwDir, "libpojavexec.so")
            check(pojavExec.isFile) { "组件包异常：$pojavExec 不存在" }

            // Java 桥须在 JVM 拉起前就绪（输入投递 / Surface 注册都依赖它）。
            // libpojavexec 的 DT_NEEDED 不在 classloader namespace 搜索路径内，
            // 必须按依赖序 System.load 先行注册：libc++_shared → bytehook → androidnsbypass → fcl
            System.setProperty("pojav.exec.path", pojavExec.absolutePath)
            for (name in listOf("libc++_shared.so", "libbytehook.so", "libandroidnsbypass.so", "libfcl.so")) {
                val lib = File(glfwDir, name)
                if (lib.isFile) {
                    System.load(lib.absolutePath)
                    listener.onLog("[flow] 预载 $name")
                }
            }
            // 组件目录里的 libjli.so 是 jre libinstrument 的依赖，预加载无害（去重）
            componentsRoot.walkTopDown()
                .filter { it.name == "libjli.so" }
                .toList()
                .sorted()
                .forEach { System.load(it.absolutePath) }
            // 触发 CallbackBridge 静态块（此时依赖已就绪）并完成桥接上下文注入。
            // 与 C# JavaBridge 同款按名字调用契约（native 侧按名字查找，类名方法名不可改动）
            val bridgeClass = Class.forName("org.lwjgl.glfw.CallbackBridge")
            bridgeClass.getMethod("setAppContext", Context::class.java)
                .invoke(null, context)
            bridgeClass.getMethod("setGrabStateCallbacks", Runnable::class.java, Runnable::class.java)
                .invoke(null, Runnable { }, Runnable { })
            listener.onProgress("Java 桥就绪")

            // 版本 json：设备上已有（versions/<id>/<id>.json）
            val versionFile = File(dotMc, "versions/$versionId/$versionId.json")
            check(versionFile.isFile) { "未找到版本 $versionId：请先在「下载」页安装" }
            val version = JSONObject(versionFile.readText())

            // 离线账户（M4 前的占位身份；离线 UUID 为 MC 标准的名字派生）
            val offlineUuid = UUID.nameUUIDFromBytes("OfflinePlayer:$playerName".toByteArray()).toString()
            val options = LaunchOptions(
                versionId = versionId,
                playerName = playerName,
                gameDir = dotMc,
                sharedDir = dotMc,
                minMemoryMb = 512,
                maxMemoryMb = 2048,
                uuid = offlineUuid,
                accessToken = "0",
                userType = "legacy",
                tmpDir = tmpDir,
            )
            // TEMP-MINARGS: 最小参数对照（判定 jopt 冻结是否参数集相关）
            val minArgsOverride = true

            val spec = LaunchSpecBuilder(manifest, componentsRoot).build(version, options)
            listener.onLog("[flow] 参数构建完成：主类 ${spec.mainClass}，渲染链 ${spec.backend}")

            // 环境预置必须在 SDL 活动启动前（onCreate 装钩子时对环境做快照）；PATH 尾冒号补全
            val processPath = System.getenv("PATH") ?: ""
            for ((name, value) in spec.env) {
                val v = if (name == "PATH" && value.endsWith(":")) value + processPath else value
                StarcloudJvm.starcloudSetEnv(name, v)
            }
            // POJAV_NATIVEDIR：APK lib 目录（pojavexec 侧查找宿主自带库用）
            StarcloudJvm.starcloudSetEnv(
                "POJAV_NATIVEDIR",
                context.applicationInfo.nativeLibraryDir,
            )

            if (spec.isSdlMode) {
                val lwjglEntry = manifest.require(spec.lwjglName)
                val sdlLib = ComponentDeployer
                    .componentDir(componentsRoot, lwjglEntry.name, lwjglEntry.version)
                    .resolve("natives/arm64-v8a/libSDL3.so")
                check(sdlLib.isFile) { "组件包异常：$sdlLib 不存在" }
                val glDir = if (spec.backend == RenderBackend.Zink) {
                    val glEntry = manifest.require("mesa-zink")
                    ComponentDeployer.componentDir(componentsRoot, glEntry.name, glEntry.version).absolutePath
                } else null
                // FCL 同款补系统目录（mesa 的 DT_NEEDED 在 /system/lib64）
                val fullLdPath = buildString {
                    append(spec.env["LD_LIBRARY_PATH"] ?: "")
                    append(":/system/lib64:/vendor/lib64:/vendor/lib64/hw:/system_ext/lib64:")
                    append(context.applicationInfo.nativeLibraryDir)
                }
                startSdlActivity(context, sdlLib.absolutePath, fullLdPath, glDir)
                listener.onLog("[flow] SDL 游戏活动已就绪")
            }

            // JVM 线程（非守护：游戏存活期间壳进程不回收）；退出后放行 SDLThread 并通知 UI
            val jvmBody = Runnable {
                startLogMirror(File(spec.workingDir, "jvm-stdout.log"))
                // argv+env 快照持久化（与 Uno 成功轮 [jvm-opt]/[jvm-env]/[jvm-game] 逐项 diff 用）
                java.io.File(dotMc, "launch_snapshot.txt").writeText(
                    buildString {
                        appendLine("== jvmArgs ==")
                        spec.jvmArgs.forEachIndexed { i, a -> appendLine("opt[$i]=$a") }
                        appendLine("== mainClass ==")
                        appendLine(spec.mainClass)
                        appendLine("== programArgs ==")
                        spec.programArgs.forEachIndexed { i, a -> appendLine("game[$i]=$a") }
                        appendLine("== env ==")
                        spec.env.forEach { (k, v) -> appendLine("$k=$v") }
                    }
                )
                // TEMP: targetSdk28 + CreateJavaVM 组合（JLI 在 targetSdk28 下 SEGV 复发）
                val exitCode = StarcloudJvm.starcloudRunJvm(
                    nativeDirs = spec.nativeDirs.toTypedArray(),
                    logPath = File(spec.workingDir, "jvm-stdout.log").absolutePath,
                    cwd = spec.workingDir.absolutePath,
                    envPairs = spec.env.map { (k, v) -> "$k=$v" }.toTypedArray(),
                    options = spec.jvmArgs.toTypedArray(),
                    mainClass = spec.mainClass,
                    programArgs = (if (minArgsOverride) listOf(
                        "--username", playerName,
                        "--version", versionId,
                        "--gameDir", dotMc.absolutePath,
                        "--assetIndex", "34",
                        "--assetsDir", dotMc.resolve("assets").absolutePath,
                    ) else spec.programArgs).toTypedArray(),
                )
                Log.i(TAG, "[jvm] 退出码 $exitCode")
                if (spec.isSdlMode) notifySdlExit()
                listener.onFinished(exitCode, null)
            }
            // HotSpot 初始化/类加载需要深调用栈：Android Java 线程默认栈 ~1MB 过小，
            // 显式给 64MB（对齐 Pojav 的 pthread 大栈实践）
            Thread(null, jvmBody, "starcloud-jvm", 64L * 1024 * 1024).apply {
                isDaemon = false
            }.start()
            listener.onProgress("JVM 已拉起")
        } catch (e: Throwable) {
            Log.e(TAG, "启动失败", e)
            listener.onFinished(-1, e.message ?: e.javaClass.simpleName)
        }
    }

    private const val SDL_ACTIVITY_CLASS = "com.starcloud.launcher.SdlGameActivity"

    private fun startSdlActivity(context: Context, sdlLibPath: String, ldPath: String, glDir: String?) {
        val activityClass = Class.forName(SDL_ACTIVITY_CLASS)
        // 同进程二次启动：先复位 SDLActivity 的会话静态位（manifest 的 SDL_ENV meta-data 已放行）
        activityClass.getMethod("prepareNewSession").invoke(null)
        val intent = Intent().setClassName(context, SDL_ACTIVITY_CLASS)
            .putExtra("sdl_lib_path", sdlLibPath)
            .putExtra("ld_library_path", ldPath)
            .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
        if (glDir != null) intent.putExtra("gl_dir", glDir)
        context.startActivity(intent)
        // 轮询 sReady：onCreate 里 libSDL3.so 加载 + setupJNI 完成后置位
        val readyField = activityClass.getField("sReady")
        val deadline = System.currentTimeMillis() + 20_000
        while (!readyField.getBoolean(null)) {
            if (System.currentTimeMillis() > deadline) {
                throw IllegalStateException("SDL 游戏活动 20s 未就绪（logcat 过滤 SDL / StarcloudSDL）")
            }
            Thread.sleep(50)
        }
    }

    private fun notifySdlExit() {
        try {
            Class.forName(SDL_ACTIVITY_CLASS).getMethod("notifyExit").invoke(null)
        } catch (e: Throwable) {
            Log.w(TAG, "notifyExit 失败：${e.message}")
        }
    }

    /** 后台线程轮询 jvm-stdout.log 追加行并镜像 logcat（游戏退出会令进程瞬死，实时轮询保现场） */
    private fun startLogMirror(logPath: File) {
        Thread {
            try {
                var offset = 0L
                val buf = ByteArray(8192)
                while (true) {
                    if (logPath.isFile) {
                        RandomAccess(logPath).use { ra ->
                            val len = ra.length()
                            if (len > offset) {
                                ra.seek(offset)
                                var read = ra.read(buf)
                                while (read > 0) {
                                    val text = String(buf, 0, read)
                                        .replace("\n", "\n[jvm-out] ")
                                    Log.i("StarcloudJvmOut", text)
                                    offset += read
                                    read = ra.read(buf)
                                }
                            }
                        }
                    }
                    Thread.sleep(400)
                }
            } catch (_: Throwable) {
                // 进程退出时文件句柄失效属正常路径
            }
        }.apply { isDaemon = true; name = "starcloud-jvm-log" }.start()
    }

    /** 简易 RandomAccessFile 包装（自动 close） */
    private class RandomAccess(f: File) : java.io.Closeable {
        private val raf = java.io.RandomAccessFile(f, "r")
        fun length(): Long = raf.length()
        fun seek(pos: Long) = raf.seek(pos)
        fun read(buf: ByteArray): Int = raf.read(buf)
        override fun close() = raf.close()
    }
}
