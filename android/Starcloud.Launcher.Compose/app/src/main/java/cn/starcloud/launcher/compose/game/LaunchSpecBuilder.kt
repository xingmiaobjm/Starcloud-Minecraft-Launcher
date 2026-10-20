// LaunchSpec 构造（对齐 C# LaunchSpecBuilder + GameLauncher.BuildArgsCore 的移植）：
// 复用桌面同一套变量替换 / 新旧两代参数 / 规则过滤语义，平台差异处覆盖——
// OS 规则按 linux 过滤、类路径与 natives 目录由组件目录组装。
package cn.starcloud.launcher.compose.game

import cn.starcloud.launcher.compose.data.ArchiveKind
import cn.starcloud.launcher.compose.data.ComponentEntry
import cn.starcloud.launcher.compose.data.ComponentManifest
import org.json.JSONArray
import org.json.JSONObject
import java.io.File

data class LaunchSpec(
    val mainClass: String,
    val jvmArgs: List<String>,
    val classpath: List<String>,
    val programArgs: List<String>,
    val nativeDirs: List<String>,
    val env: Map<String, String>,
    val jreMajor: Int,
    val workingDir: File,
    val backend: RenderBackend,
    val lwjglName: String,
) {
    /** MC 26.x（LWJGL ≥3.4）走 SDL 窗口/输入后端，窗口面归 SdlGameActivity */
    val isSdlMode: Boolean get() = lwjglName == "lwjgl341"
}

data class LaunchOptions(
    val versionId: String,
    val playerName: String,
    val gameDir: File,       // 实例/游戏工作目录（安卓 = .minecraft）
    val sharedDir: File,     // 共享根 .minecraft：libraries/assets/jar
    val minMemoryMb: Int,
    val maxMemoryMb: Int,
    val uuid: String,
    val accessToken: String,
    val userType: String,
    val tmpDir: File,
)

/** 组件清单里实际存在的 JRE 大版本，用于缺 JRE 时的提示 */
private fun describeAvailableJre(manifest: ComponentManifest): String {
    val majors = manifest.components
        .map { it.name }
        .filter { it.startsWith("jre") }
        .mapNotNull { Regex("(\\d+)").find(it)?.groupValues?.get(1)?.toIntOrNull() }
        .distinct()
        .sorted()
    return if (majors.isEmpty()) "当前内置组件里没有任何 JRE"
    else "当前只提供 Java " + majors.joinToString(" / ")
}

class LaunchSpecBuilder(
    private val manifest: ComponentManifest,
    private val componentsRoot: File,
) {

    /** LWJGL 组件选择：版本声明 ≥3.4 用 3.4.1（SDL 后端），否则 3.3.3；未声明时 26.x 新命名默认 3.4.1 */
    fun lwjglComponentNameOf(version: JSONObject): String {
        val declared = libraryNames(version)
            .filter { it.startsWith("org.lwjgl:lwjgl:") }
            .map { it.removePrefix("org.lwjgl:lwjgl:") }
            .firstOrNull()
        if (declared != null) {
            val parts = declared.split('.').map { it.toIntOrNull() ?: 0 }
            val isAtLeast34 = parts.size >= 2 && (parts[0] > 3 || (parts[0] == 3 && parts[1] >= 4))
            return if (isAtLeast34) "lwjgl341" else "lwjgl3"
        }
        return if (RuntimeSelector.parseMinor(version.optString("id")) == null) "lwjgl341" else "lwjgl3"
    }

    /** 给定版本与启动选项，构建进程内 JVM 可直接使用的 LaunchSpec */
    fun build(version: JSONObject, options: LaunchOptions): LaunchSpec {
        val versionId = version.optString("id")
        val javaMajor = version.optJSONObject("javaVersion")?.optInt("majorVersion")?.takeIf { it > 0 }
        val jreMajor = RuntimeSelector.selectJreMajor(versionId, javaMajor)
        val backend = RenderChainSelector.select(versionId, preferZink = true)

        val jreEntry = manifest.find(RuntimeSelector.componentNameFor(jreMajor))
            ?: throw IllegalStateException(
                // 别写死"已提供 8/17/21/25"：内置组件缺失时那句话会自相矛盾（走查时踩到）
                "此版本需要 Java $jreMajor 运行时，${describeAvailableJre(manifest)}。"
            )
        val lwjglEntry = manifest.require(lwjglComponentNameOf(version))
        val glfwEntry = manifest.require("glfw-stub")
        val openalEntry = manifest.require("openal")
        val glEntry = manifest.require(RenderChainSelector.componentNameOf(backend))

        // natives 搜索路径：各必需组件目录中含 .so 的父目录集合
        val nativeDirs: java.util.ArrayList<String> = java.util.ArrayList(nativeDirsOf(jreEntry, lwjglEntry, glfwEntry, openalEntry, glEntry))

        // JNA（MC 引导期 NativeLibrariesBootstrap 依赖）：版本自带 jna.jar 的 jnidispatch 是
        // 桌面 glibc 构建，Android dlopen 报 libc.so.6 缺失；组件 jna 提供 bionic 版按需注入
        val jnaNativeDir = resolveJnaNativeDir(version)
        if (jnaNativeDir != null) nativeDirs.add(jnaNativeDir)

        val jreDir = componentDir(jreEntry)
        val glfwDir = componentDir(glfwEntry)

        // 类路径次序即类覆盖优先级：glfw-stub 胶水 jar 在 LWJGL 自带 glfw binding 之前
        val classpath = mutableListOf<String>()
        classpath += componentJars(glfwEntry)
        classpath += componentJars(lwjglEntry)
        val instanceId = options.versionId.ifBlank { versionId }
        val ownJar = File(File(options.sharedDir, "versions/$instanceId"), "$instanceId.jar")
        require(ownJar.isFile) { "实例 $instanceId 缺少客户端 jar：请先在版本页完成下载" }
        classpath += desktopLibraryJars(version, options.sharedDir)
        classpath += ownJar.absolutePath
        val distinct = classpath.distinct()

        // 通用参数构建（osName=linux，natives/classpath 由上面组装注入变量表）
        val built = buildArgsCore(version, options, distinct, nativeDirs.joinToString(":"))

        val jvmArgs = mutableListOf<String>().apply {
            // JDK 统一日志：JVM 内部初始化全记录落文件，main 静默快退时以此定位退出点
            add("-Xlog:class+load=info:stdout:utctime,tags")
            // x0 高位垃圾的 narrowOop 解压损坏（hs_err 实证 compressed oops 开启）：
            // MobiPort HotSpot 在 ART 宿主进程的堆基地址对齐不满足压缩条件，关闭规避
            add("-XX:-UseCompressedOops")
            add("-XX:-UseCompressedClassPointers")
            add("-Djava.class.path=${classpath.joinToString(":")}")
            // 安卓移植 HotSpot 靠 java.home 定位 modules 镜像；缺失会在类加载期 SIGSEGV
            add("-Djava.home=${jreDir.absolutePath}")
            // libpojavexec（GLFW stub）路径：游戏 JVM 内的钩子按此属性定位 so
            add("-Dpojav.exec.path=${glfwDir.resolve("libpojavexec.so").absolutePath}")
            add("-XX:ErrorFile=${options.gameDir.absolutePath}/hs_err_%p.log")
            add("-Dorg.lwjgl.opengl.libname=${RenderChainSelector.openGLLibraryName(backend)}")
            add("-Dorg.lwjgl.glfw.libname=libpojavexec.so")
            add("-Dorg.lwjgl.openal.libname=libopenal.so")
            addAll(built.jvmArgs)
            if (jnaNativeDir != null) add("-Djna.boot.library.path=$jnaNativeDir")
            // LWJGL 用 System.loadLibrary 定位核心库，只认 java.library.path / org.lwjgl.librarypath
            val lwjglNativesDir = componentDir(lwjglEntry).resolve("natives/arm64-v8a")
            add("-Dorg.lwjgl.librarypath=$lwjglNativesDir")
            add("-Djava.library.path=${nativeDirs.joinToString(":")}")
            add("-Dorg.lwjgl.util.Debug=true")
        }

        val glDir = componentDir(glEntry)
        val env = sortedMapOf(
            "HOME" to options.gameDir.absolutePath,
            "JAVA_HOME" to jreDir.absolutePath,
            // PATH 尾冒号：Jvm 启动时在尾部追加进程原 PATH
            "PATH" to "${jreDir.absolutePath}/bin:",
            "LD_LIBRARY_PATH" to nativeDirs.joinToString(":"),
            "FORCE_VSYNC" to "false",
            "ALSOFT_DRIVERS" to "opensl",
            "TMPDIR" to options.tmpDir.absolutePath,
            // 窗口 buffer 变换修正（libstarcloudwindow.so 读取）：横屏预旋转链的合成器补偿
            "STARCLOUD_WINDOW_TRANSFORM" to "inverse",
        )
        when (backend) {
            RenderBackend.Zink -> {
                env["POJAV_RENDERER"] = "opengles3_desktopgl_zink_kopper"
                env["LIBGL_ES"] = "3"
                // EGL 实现统一指向组件目录的 mesa libEGL（SDL 主路径与 FCL 钩子代理读同一份）
                val eglPath = glDir.resolve("libEGL_mesa.so").absolutePath
                env["POJAVEXEC_EGL"] = eglPath
                env["SDL_EGL_LIBRARY"] = eglPath
                env["SDL_OPENGL_LIBRARY"] = eglPath
                // Vulkan 驱动查找路径（不设 VULKAN_DRIVER_SYSTEM，让 zink 用随包 Turnip）
                env["DRIVER_PATH"] = glDir.absolutePath
                env["MESA_GLSL_CACHE_DIR"] = options.gameDir.resolve(".mesa_parser_cache").absolutePath
                env["MESA_GL_VERSION_OVERRIDE"] = "4.6"
                env["MESA_GLSL_VERSION_OVERRIDE"] = "460"
                env["force_glsl_extensions_warn"] = "true"
                env["allow_higher_compat_version"] = "true"
                env["allow_glsl_extension_directive_midshader"] = "true"
                // 刻意不提供 DRI 驱动定位：保持 GL 侧干净失败，让 MC 走 Vulkan(Turnip)
            }
            RenderBackend.Gl4es -> {
                env["POJAV_RENDERER"] = "opengles2"
                env["LIBGL_ES"] = "2"
                env["LIBGL_MIPMAP"] = "3"
                env["LIBGL_NORMALIZE"] = "1"
                env["LIBGL_NOINTOVLHACK"] = "1"
                env["LIBGL_NOERROR"] = "1"
            }
        }

        return LaunchSpec(
            mainClass = built.mainClass,
            jvmArgs = jvmArgs,
            classpath = distinct,
            programArgs = built.gameArgs,
            nativeDirs = nativeDirs,
            env = env,
            jreMajor = jreMajor,
            workingDir = options.gameDir,
            backend = backend,
            lwjglName = lwjglEntry.name,
        )
    }

    // ---- 参数构建核心（C# GameLauncher.BuildArgsCore 的移植） ----

    private data class BuiltArgs(val jvmArgs: List<String>, val gameArgs: List<String>, val mainClass: String)

    private fun buildArgsCore(
        version: JSONObject,
        options: LaunchOptions,
        classpath: List<String>,
        nativesDir: String,
    ): BuiltArgs {
        val versionId = version.optString("id")
        val classpathStr = classpath.joinToString(":")

        val assets = version.optString("assets").ifEmpty {
            version.optJSONObject("assetIndex")?.optString("id")?.ifEmpty { null } ?: "legacy"
        }
        val vars = mapOf(
            "auth_player_name" to options.playerName,
            "version_name" to versionId,
            "game_directory" to options.gameDir.absolutePath,
            "assets_root" to options.sharedDir.resolve("assets").absolutePath,
            "game_assets" to options.sharedDir.resolve("assets/virtual/$assets").absolutePath,
            "assets_index_name" to (version.optJSONObject("assetIndex")?.optString("id")?.ifEmpty { null } ?: assets),
            "auth_uuid" to options.uuid,
            "auth_access_token" to options.accessToken,
            "auth_session" to "token:${options.accessToken}:${options.uuid}",
            "auth_xuid" to "0",
            "clientid" to "starcloud",
            "user_type" to options.userType,
            "user_properties" to "{}",
            "version_type" to version.optString("type", "release"),
            "launcher_name" to "Starcloud",
            "launcher_version" to "1.0.0",
            "natives_directory" to nativesDir,
            "library_directory" to options.sharedDir.resolve("libraries").absolutePath,
            "classpath_separator" to ":",
            "classpath" to classpathStr,
            "resolution_width" to "1280",
            "resolution_height" to "720",
            "quickPlayPath" to "",
            "quickPlaySingleplayer" to "",
            "quickPlayMultiplayer" to "",
            "quickPlayRealms" to "",
            "quickPlayBroadBand" to "",
        )

        fun substitute(s: String): String = Regex("""\$\{([a-zA-Z0-9_]+)\}""").replace(s) { m ->
            vars[m.groupValues[1]] ?: m.value
        }

        val jvmArgs = mutableListOf(
            "-Xms${options.minMemoryMb}M",
            "-Xmx${options.maxMemoryMb}M",
            "-Djava.library.path=$nativesDir",
            "-Dorg.lwjgl.opengl.Display.allowSoftwareOpenGL=true",
            // 内置优化参数（对齐桌面 use_builtin_jvm_args）；
            // GC 暂用 SerialGC 对照：G1 在 ART 宿主进程类加载期 SEGV（hs_err_17744/18715/20203），
            // 若 SerialGC 通过则逐项回测 G1 参数定位
            "-XX:+UseSerialGC",
            "-Dsun.rmi.dgc.server.gcInterval=2147483646",
            "-Dfile.encoding=UTF-8",
        )

        val gameArgs = mutableListOf<String>()

        // 新代参数（arguments.jvm / arguments.game）
        val args = version.optJSONObject("arguments")
        if (args != null) {
            args.optJSONArray("jvm")?.let { appendValues(it, jvmArgs, ::substitute) }
            args.optJSONArray("game")?.let { appendValues(it, gameArgs, ::substitute) }
        }

        // 旧代参数字符串
        val mcArgs = version.optString("minecraftArguments").ifEmpty { null }
        if (mcArgs != null) {
            gameArgs += mcArgs.split(' ').filter { it.isNotEmpty() }.map { substitute(it) }
        } else if (args != null) {
            // 现代版本没有 minecraftArguments 时补全必需 game 参数
            if (gameArgs.none { it == "--username" }) {
                gameArgs += listOf(
                    "--username", options.playerName,
                    "--version", versionId,
                    "--gameDir", options.gameDir.absolutePath,
                    "--assetsDir", options.sharedDir.resolve("assets").absolutePath,
                    "--assetIndex", vars["assets_index_name"]!!,
                    "--uuid", options.uuid,
                    "--accessToken", options.accessToken,
                    "--userType", options.userType,
                    "--versionType", version.optString("type", "release"),
                )
            }
        }

        return BuiltArgs(jvmArgs, gameArgs, version.optString("mainClass"))
    }

    /** arguments.jvm/game 的条目：{rules?, value: string|string[]}，按 linux 规则过滤后展开 */
    private fun appendValues(arr: JSONArray, list: MutableList<String>, substitute: (String) -> String) {
        // Pojav 系版本 json 的数组是混合形态：官方对象 {rules?, value} + 裸字符串参数
        // （如 "-XX:StackShadowPages=32"——缺失它会让 MobiPort HotSpot 在 ART 宿主进程
        // 类加载期 SIGSEGV，Uno 壳带此参数跑通；真机二分实证 2026-10-10）
        for (i in 0 until arr.length()) {
            when (val raw = arr.get(i)) {
                is String -> if (raw.isNotEmpty()) list.add(substitute(raw))
                is JSONObject -> {
                    if (!rulesAllow(raw.optJSONArray("rules"))) continue
                    val value = raw.opt("value") ?: continue
                    when (value) {
                        is String -> if (value.isNotEmpty()) list.add(substitute(value))
                        is JSONArray -> for (j in 0 until value.length()) {
                            val v = value.optString(j)
                            if (v.isNotEmpty()) list.add(substitute(v))
                        }
                    }
                }
            }
        }
    }

    /** Mojang 规则判定：无规则 = 允许；os 按 linux；features 我们从不提供（不匹配即跳过）；
        按最后一个匹配项的 action 生效 */
    private fun rulesAllow(rules: JSONArray?): Boolean {
        if (rules == null || rules.length() == 0) return true
        var allowed = false
        for (i in 0 until rules.length()) {
            val r = rules.getJSONObject(i)
            val os = r.optJSONObject("os")
            val features = r.optJSONObject("features")
            val osApplies = os == null || os.optString("name") == "linux"
            val featApplies = if (features != null && features.length() > 0) {
                // 我们不提供任何 feature：仅当规则要求 feature 为 false 时才匹配
                var allFalse = true
                for (k in 0 until features.length()) {
                    if (features.getBoolean(features.names().getString(k))) allFalse = false
                }
                allFalse
            } else true
            if (osApplies && featApplies) allowed = r.getString("action") == "allow"
        }
        return allowed
    }

    // ---- 组件与库路径 ----

    private fun componentDir(entry: ComponentEntry) =
        cn.starcloud.launcher.compose.data.ComponentDeployer.componentDir(componentsRoot, entry.name, entry.version)



    private fun libraryNames(version: JSONObject): List<String> =
        version.optJSONArray("libraries")?.let { arr ->
            (0 until arr.length()).mapNotNull { arr.optJSONObject(it)?.optString("name") }
        } ?: emptyList()

    /** 组件目录内全部 jar（递归，按路径排序保证确定性）；未部署直接抛错 */
    private fun componentJars(entry: ComponentEntry): List<String> {
        val dir = componentDir(entry)
        require(dir.isDirectory) { "组件 ${entry.name} 尚未部署：请先完成组件部署" }
        return dir.walkTopDown()
            .filter { it.isFile && it.extension == "jar" }
            .map { it.absolutePath }
            .toList()
            .sorted()
    }

    /** 版本 JSON 声明的桌面库：按 linux 规则过滤、剔除 LWJGL 坐标（由组件提供） */
    private fun desktopLibraryJars(version: JSONObject, dotMc: File): List<String> {
        val libs = version.optJSONArray("libraries") ?: return emptyList()
        val result = mutableListOf<String>()
        for (i in 0 until libs.length()) {
            val lib = libs.optJSONObject(i) ?: continue
            if (!rulesAllow(lib.optJSONArray("rules"))) continue
            if (lib.optString("name").startsWith("org.lwjgl")) continue
            val artifact = lib.optJSONObject("downloads")?.optJSONObject("artifact") ?: continue
            val path = artifact.optString("path")
            if (path.isEmpty() || artifact.optString("url").isEmpty()) continue
            val f = dotMc.resolve("libraries/$path")
            if (f.isFile) result += f.absolutePath
        }
        return result
    }

    /** 版本声明 JNA 对应的 bionic libjnidispatch 目录：精确 → ≤声明最高 → 最高；缺失容忍。
        目录名为多段数字版本（如 5.13.0），逐段比较（对齐 C# Version.TryParse 语义） */
    private fun resolveJnaNativeDir(version: JSONObject): String? {
        val declared = libraryNames(version)
            .filter { it.startsWith("net.java.dev.jna:jna:") }
            .map { it.removePrefix("net.java.dev.jna:jna:") }
            .firstOrNull() ?: return null
        val jnaEntry = manifest.find("jna") ?: return null
        val root = componentDir(jnaEntry)
        if (!root.isDirectory) return null
        val want = parseVersionSegments(declared)
        val candidates = root.listFiles { f -> f.isDirectory }
            ?.mapNotNull { d -> parseVersionSegments(d.name)?.let { Triple(d, it, it) } }
            ?.sortedWith { x, y -> compareVersion(x.third, y.third) }
            ?: return null
        if (candidates.isEmpty()) return null
        if (want != null) {
            candidates.firstOrNull { it.second == want }?.let { return it.first.absolutePath }
            candidates.lastOrNull { isVersionAtMost(it.third, want) }?.let { return it.first.absolutePath }
        }
        return candidates.last().first.absolutePath
    }

    private fun parseVersionSegments(name: String): List<Int>? {
        if (name.isEmpty()) return null
        val parts = name.split('.')
        if (parts.any { it.toIntOrNull() == null }) return null
        return parts.map { it.toInt() }
    }

    /** 逐段版本比较：负数=前小，正数=前大，0=相等（短的按补 0 对齐） */
    private fun compareVersion(a: List<Int>, b: List<Int>): Int {
        val n = maxOf(a.size, b.size)
        for (i in 0 until n) {
            val x = a.getOrElse(i) { 0 }
            val y = b.getOrElse(i) { 0 }
            if (x != y) return x - y
        }
        return 0
    }

    /** 逐段比较：a 的每一段不大于 b（短的按补 0 对齐） */
    private fun isVersionAtMost(a: List<Int>, b: List<Int>): Boolean {
        val n = maxOf(a.size, b.size)
        for (i in 0 until n) {
            val x = a.getOrElse(i) { 0 }
            val y = b.getOrElse(i) { 0 }
            if (x != y) return x < y
        }
        return true
    }

    /** 递归枚举必需组件中含 .so 的目录集合（排序去重；JRE 的 lib/server 自然覆盖） */
    private fun nativeDirsOf(vararg entries: ComponentEntry): java.util.SortedSet<String> {
        val dirs = sortedSetOf<String>()
        for (entry in entries) {
            val dir = componentDir(entry)
            if (!dir.isDirectory) continue
            dir.walkTopDown()
                .filter { it.isFile && it.name.endsWith(".so") }
                .forEach { it.parentFile?.absolutePath?.let { p -> dirs.add(p) } }
        }
        return dirs
    }
}
