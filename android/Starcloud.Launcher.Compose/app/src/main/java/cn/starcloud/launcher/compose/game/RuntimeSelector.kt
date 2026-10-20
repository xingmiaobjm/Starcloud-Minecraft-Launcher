// 版本 → JRE 运行时 / 渲染链选择（对齐 C# RuntimeSelector 与 RenderChainSelector）
package cn.starcloud.launcher.compose.game

object RuntimeSelector {

    /** JRE 主版本：javaVersion 声明优先（≤8→8，≤17→17，≤21→21，更高→25）；否则按版本号推断 */
    fun selectJreMajor(mcVersionId: String, javaMajorVersion: Int?): Int {
        if (javaMajorVersion != null && javaMajorVersion > 0) {
            return when {
                javaMajorVersion <= 8 -> 8
                javaMajorVersion <= 17 -> 17
                javaMajorVersion <= 21 -> 21
                else -> 25
            }
        }
        return when (val minor = parseMinor(mcVersionId)) {
            null -> 25          // 快照 / 新命名（26.x）/ 特殊版本：按最新运行时
            in 0..16 -> 8
            17 -> 17
            in 18..21 -> 21
            else -> 25
        }
    }

    fun componentNameFor(jreMajor: Int): String = when {
        jreMajor <= 8 -> "jre8"
        jreMajor <= 17 -> "jre17"
        jreMajor <= 21 -> "jre21"
        else -> "jre25"
    }

    /** 提取「1.x」中的 minor；非 release 命名（快照、26.x 新命名等）返回 null */
    fun parseMinor(versionId: String): Int? {
        val m = Regex("""^1\.(\d+)""").find(versionId) ?: return null
        return m.groupValues[1].toIntOrNull()
    }
}

enum class RenderBackend { Zink, Gl4es }

object RenderChainSelector {

    /** 自动：1.12.2 及以前走 gl4es，1.13+ 与快照走 Zink；强制 gl4es 仅在 ≤1.16 可行 */
    fun select(mcVersionId: String, preferZink: Boolean): RenderBackend {
        val minor = RuntimeSelector.parseMinor(mcVersionId)
        if (preferZink) return RenderBackend.Zink
        return if (minor != null && minor <= 12) RenderBackend.Gl4es else RenderBackend.Zink
    }

    fun componentNameOf(backend: RenderBackend) =
        if (backend == RenderBackend.Zink) "mesa-zink" else "gl4es"

    /**
     * LWJGL -Dorg.lwjgl.opengl.libname 指定的库名。Zink 指回 libOSMesa_81.so（无 egl* 导出）
     * 让 SDL_GL_LoadLibrary 干净失败，MC 随即回退自己的 Vulkan(Turnip) 后端（26.x 唯一可行链）
     */
    fun openGLLibraryName(backend: RenderBackend) =
        if (backend == RenderBackend.Zink) "libOSMesa_81.so" else "libgl4es_114.so"
}
