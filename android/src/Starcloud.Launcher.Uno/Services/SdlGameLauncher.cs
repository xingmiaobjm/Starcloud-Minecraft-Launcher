// SDL3 游戏活动编排（MC 26.x / LWJGL ≥3.4 SDL 后端）：
// 拉起 com.starcloud.launcher.SdlGameActivity——其 onCreate 完成 loadLibraries
// （组件目录 libSDL3.so）+ SDL.setupJNI（内部自动 SDL_SetMainReady），C# 侧轮询
// sReady 标志确认就绪后再拉起 JVM，保证 MC 首个 SDL_Init 必然通过；
// 游戏退出后 notifyExit 放行 SDLThread（其 main() 阻塞在退出闩上），由其 finish 活动。

using Android.Runtime;

namespace Starcloud.Launcher.Uno.Services;

internal static class SdlGameLauncher
{
    private const string ClassPath = "com/starcloud/launcher/SdlGameActivity";
    private const string ActivityClassName = "com.starcloud.launcher.SdlGameActivity";
    private const string SdlLibExtra = "sdl_lib_path";
    private const string LdPathExtra = "ld_library_path";
    private const string GlDirExtra = "gl_dir";
    private const int ReadyTimeoutMs = 20_000;

    /// <summary>
    /// 启动 SDL 游戏活动并等待其 onCreate 完成（sReady=true）。
    /// ldPath / glDir 供 loadLibraries 执行 FCL 加载链（setLdLibraryPath → DLOPEN → initializeHooks）
    /// </summary>
    public static async Task StartActivityAsync(string sdlLibPath, string? ldPath, string? glDir,
        CancellationToken ct = default)
    {
        var context = global::Android.App.Application.Context;
        // 同进程二次启动：先复位 SDLActivity 的会话静态位（manifest 的 SDL_ENV
        // meta-data 已放行 recreate，native 侧 run_count 检查不拦）
        InvokeStaticVoid("prepareNewSession");

        var intent = new global::Android.Content.Intent();
        intent.SetClassName(context.PackageName!, ActivityClassName);
        intent.PutExtra(SdlLibExtra, sdlLibPath);
        if (!string.IsNullOrEmpty(ldPath))
        {
            intent.PutExtra(LdPathExtra, ldPath);
        }
        if (!string.IsNullOrEmpty(glDir))
        {
            intent.PutExtra(GlDirExtra, glDir);
        }
        // 应用上下文 startActivity 必须带 NewTask
        intent.AddFlags(global::Android.Content.ActivityFlags.NewTask);
        context.StartActivity(intent);

        // 轮询 sReady：onCreate 里 libSDL3.so 加载 + setupJNI 完成后置位
        var deadline = Environment.TickCount64 + ReadyTimeoutMs;
        while (!ReadReady())
        {
            ct.ThrowIfCancellationRequested();
            if (Environment.TickCount64 > deadline)
            {
                throw new TimeoutException(
                    "SDL 游戏活动 20s 未就绪（onCreate 失败？logcat 过滤 SDL / StarcloudSDL）");
            }
            await Task.Delay(50, ct);
        }
    }

    /// <summary>游戏退出（JvmHost.Exited）后放行 SDLThread，由其 finish 游戏活动</summary>
    public static void NotifyExit() => InvokeStaticVoid("notifyExit");

    // ---- JNI：静态布尔读 / 静态 void 调（FindClass 返回全局引用，配对 DeleteGlobalRef）----

    private static bool ReadReady()
    {
        IntPtr clazz = JNIEnv.FindClass(ClassPath);
        try
        {
            IntPtr fid = JNIEnv.GetStaticFieldID(clazz, "sReady", "Z");
            return JNIEnv.GetStaticBooleanField(clazz, fid);
        }
        finally
        {
            JNIEnv.DeleteGlobalRef(clazz);
        }
    }

    private static void InvokeStaticVoid(string methodName)
    {
        IntPtr clazz = JNIEnv.FindClass(ClassPath);
        try
        {
            IntPtr mid = JNIEnv.GetStaticMethodID(clazz, methodName, "()V");
            JNIEnv.CallStaticVoidMethod(clazz, mid);
            CheckNoPending(methodName);
        }
        finally
        {
            JNIEnv.DeleteGlobalRef(clazz);
        }
    }

    private static void CheckNoPending(string what)
    {
        if (JNIEnv.ExceptionOccurred() != IntPtr.Zero)
        {
            // ExceptionDescribe 打印并清空挂起异常
            JNIEnv.ExceptionDescribe();
            throw new InvalidOperationException($"SdlGameActivity.{what} JNI 调用挂起异常（见 logcat）");
        }
    }
}
