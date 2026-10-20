// 渲染链选择：安卓无桌面 GL，OpenGL 调用全靠转译层——
//   Zink（Mesa，GL 3.x+ 语义）→ 1.17+ 唯一可行
//   gl4es（GL 2.1 语义）→ 1.12.2 及以前足够
// 库名用 FCL 组件的原始产物名（libOSMesa_81.so / libgl4es_114.so），
// 与组件包内文件一一对应，LWJGL 经 -Dorg.lwjgl.opengl.libname 显式加载。

namespace Starcloud.Android.Core.Launch;

/// <summary>用户偏好：自动按版本选择 / 强制 Zink / 强制 gl4es</summary>
public enum RenderChainPreference
{
    Auto,
    Zink,
    Gl4es,
}

/// <summary>实际生效的渲染后端</summary>
public enum RenderBackend
{
    Zink,
    Gl4es,
}

public static class RenderChainSelector
{
    /// <summary>
    /// 选择渲染后端：强制 Zink 无条件生效；强制 gl4es 仅在 ≤1.16 可行
    /// （1.17+ 要求 GL 3.2，gl4es 兜不住，是必败配置，仍走 Zink）；
    /// 自动时 1.12.2 及以前走 gl4es，1.13+ 与快照走 Zink。
    /// </summary>
    public static RenderBackend Select(string mcVersionId, RenderChainPreference pref)
    {
        var minor = RuntimeSelector.ParseMinor(mcVersionId);
        if (pref == RenderChainPreference.Zink) return RenderBackend.Zink;
        if (pref == RenderChainPreference.Gl4es)
            return minor is not null && minor <= 16 ? RenderBackend.Gl4es : RenderBackend.Zink;
        return minor is not null && minor <= 12 ? RenderBackend.Gl4es : RenderBackend.Zink;
    }

    /// <summary>后端 → 组件清单名称</summary>
    public static string ComponentNameOf(RenderBackend backend) =>
        backend == RenderBackend.Zink ? "mesa-zink" : "gl4es";

    /// <summary>
    /// 后端 → LWJGL 通过 -Dorg.lwjgl.opengl.libname 指定的库名。
    /// 该库会被 MC 的 GlBackend.loadLibrary 交给 SDL_GL_LoadLibrary 当 EGL 库加载。
    /// 26.x 的 GlBackend 只接受桌面 GL（请求 CORE profile、编译桌面版 GLSL 3.30），
    /// 而 Android 上 SDL 绑定的是 EGL_OPENGL_ES_API、实际上下文是 GLES 3.2（实测），
    /// 桌面 GLSL 必被拒 → 此路对 26.x 不通。故这里指回 libOSMesa_81.so（无 egl* 导出），
    /// 让 SDL_GL_LoadLibrary 干净失败，MC 随即回退到自己的 Vulkan(Turnip) 后端。
    /// 1.16 及以下版本走 gl4es，不受影响
    /// </summary>
    public static string OpenGLLibraryName(RenderBackend backend) =>
        backend == RenderBackend.Zink ? "libOSMesa_81.so" : "libgl4es_114.so";
}
