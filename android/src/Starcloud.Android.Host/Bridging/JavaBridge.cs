// ART 侧 Java 桥：经 JNIEnv 调宿主 dex 中的 org.lwjgl.glfw.CallbackBridge，
// 把 C# 侧输入事件（键鼠/触摸/窗口）转发给游戏进程（HotSpot 侧 LWJGL）。
// Initialize 必须在任何投递前完成：先写 pojav.exec.path 系统属性（触发静态块加载 so），
// 再缓存类全局引用与全部方法 ID。

using Android.Runtime;
using Android.Views;
using JValue = Android.Runtime.JValue;

namespace Starcloud.Android.Host.Bridging;

internal static class JavaBridge
{
    private const string ClassName = "org/lwjgl/glfw/CallbackBridge";

    // 类全局引用与 15 个方法 ID 缓存（全部 static void 成员）
    private static IntPtr _clazz;
    private static IntPtr _ensureLoaded;
    private static IntPtr _loadNativeLibrary;
    private static IntPtr _setupBridgeWindow;
    private static IntPtr _sendUpdateWindowSize;
    private static IntPtr _sendKeycode;
    private static IntPtr _sendMouseButton;
    private static IntPtr _sendMouseKeycode;
    private static IntPtr _sendScroll;
    private static IntPtr _sendCursorPos;
    private static IntPtr _sendCursorDelta;
    private static IntPtr _resetInputState;
    private static IntPtr _setModifiers;
    private static IntPtr _putMouseEventWithCoords;
    private static IntPtr _setGrabStateCallbacks;
    private static IntPtr _setAppContext;

    private static volatile bool _initialized;

    /// <summary>grab 回调 Runnable 的 C# 保活引用：peer 的全局引用随 C# 对象回收而释放，
    /// Java 侧回调仍可能被触发，故与回调生命周期对齐（注册后存活整个进程）</summary>
    private static ActionRunnable? _grabbedRunnable;
    private static ActionRunnable? _releasedRunnable;

    /// <summary>当前鼠标/键盘抓取状态（由 grab 回调翻转；触摸投递按此分流 delta/绝对坐标）</summary>
    public static volatile bool IsGrabbing;

    /// <summary>Initialize 是否已完成（输入投递与 Surface 注册的前置条件）</summary>
    public static bool IsInitialized => _initialized;

    /// <summary>写入 pojav.exec.path、缓存 CallbackBridge 方法表、注册 Application Context</summary>
    public static void Initialize(string pojavExecPath,
        global::Android.Content.Context context, IReadOnlyList<string> dependencyLibs)
    {
        if (_initialized)
        {
            return;
        }

        Trace("[bridge] Initialize 开始：setProperty");
        // [1] java.lang.System.setProperty("pojav.exec.path", ...)：
        // CallbackBridge 静态块 ensureLoaded 优先按该属性 System.load
        // 注意：.NET 版 JNIEnv.FindClass 返回全局引用（内部 NewGlobalRef + 释放局部引用），
        // 必须用 DeleteGlobalRef 配对；DeleteLocalRef 会触发 CheckJNI abort（崩溃 #3 根因）
        IntPtr systemClass = JNIEnv.FindClass("java/lang/System");
        try
        {
            // System.setProperty(String,String) 返回前值（String），签名必须是 Ljava/lang/String;
            IntPtr setProperty = JNIEnv.GetStaticMethodID(systemClass, "setProperty",
                "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
            using var name = new JniLocalRef(JNIEnv.NewString("pojav.exec.path"));
            using var value = new JniLocalRef(JNIEnv.NewString(pojavExecPath));
            var previous = JNIEnv.CallStaticObjectMethod(systemClass, setProperty,
                new JValue(name.Handle), new JValue(value.Handle));
            if (previous != IntPtr.Zero)
            {
                JNIEnv.DeleteLocalRef(previous);
            }
            CheckNoException("System.setProperty(pojav.exec.path)");
        }
        finally
        {
            JNIEnv.DeleteGlobalRef(systemClass);
        }

        // [2] 缓存 CallbackBridge 类引用与方法 ID（首次 FindClass 触发静态块：加载 so）
        // FindClass 返回的即是全局引用，直接持有；不可再 NewGlobalRef（泄漏）或 DeleteLocalRef（abort）
        Trace("[bridge] FindClass CallbackBridge（触发静态块加载 so）");
        var globalClazz = JNIEnv.FindClass(ClassName);
        if (globalClazz == IntPtr.Zero)
        {
            throw new InvalidOperationException($"FindClass({ClassName}) 失败");
        }
        _clazz = globalClazz;
        Trace("[bridge] CallbackBridge 类引用就绪");

        _ensureLoaded = MethodId("ensureLoaded", "(Ljava/lang/String;)V");
        _loadNativeLibrary = MethodId("loadNativeLibrary", "(Ljava/lang/String;)V");
        _setupBridgeWindow = MethodId("setupBridgeWindow", "(Ljava/lang/Object;)V");
        _sendUpdateWindowSize = MethodId("sendUpdateWindowSize", "(II)V");
        _sendKeycode = MethodId("sendKeycode", "(ICIIZ)V");
        _sendMouseButton = MethodId("sendMouseButton", "(IZ)V");
        _sendMouseKeycode = MethodId("sendMouseKeycode", "(IIZ)V");
        _sendScroll = MethodId("sendScroll", "(DD)V");
        _sendCursorPos = MethodId("sendCursorPos", "(FF)V");
        _sendCursorDelta = MethodId("sendCursorDelta", "(FF)V");
        _resetInputState = MethodId("resetInputState", "()V");
        _setModifiers = MethodId("setModifiers", "(IZ)V");
        _putMouseEventWithCoords = MethodId("putMouseEventWithCoords", "(IFF)V");
        _setGrabStateCallbacks = MethodId("setGrabStateCallbacks",
            "(Ljava/lang/Runnable;Ljava/lang/Runnable;)V");
        _setAppContext = MethodId("setAppContext", "(Landroid/content/Context;)V");

        // [2.5] 显式补加载（顺序敏感）：静态块的 ensureLoaded 失败被吞，此处补载并 fail-fast。
        // ART 的 native 方法解析只认 System.load 注册进 ART 库表的 so（JvmHost 的
        // NativeLibrary.Load 走 Mono dlopen，ART 不可见）：
        // 1. 依赖 so：libpojavexec 的 DT_NEEDED（libjli.so 等）不在 classloader namespace
        //    搜索路径内，必须先按绝对路径加载（已加载的 so 再 load 被 linker 去重，视为成功）
        // 2. libc++_shared：C++ 运行时依赖，同理
        // 3. ensureLoaded(libpojavexec)：失败上抛中止启动链（sLoaded 防重）
        foreach (var lib in dependencyLibs)
        {
            EnsureJavaLibrary(lib);
        }
        EnsureJavaLibrary(Path.Combine(Path.GetDirectoryName(pojavExecPath)!, "libc++_shared.so"));
        EnsureLoaded(pojavExecPath);
        // [3] 注册 Application Context（剪贴板读取需要）
        CallVoid(_setAppContext, new JValue(context));
        _initialized = true;
        Trace("[bridge] Initialize 完成");
    }

    /// <summary>经 CallbackBridge.loadNativeLibrary 以 ART 视角加载 so（进 ART 库表）。
    /// System.load 必须在 Java 帧内调用（内部取调用者类作锚点，Mono 线程直调取到 null 即 NPE，
    /// 崩溃 #10 根因）。已加载的 so 再 load 抛 UnsatisfiedLinkError（linker 去重，视为成功）；
    /// 其余失败取 Throwable 消息上抛——华为设备 logcat 抓不到 app 自有日志，失败原因必须
    /// 经 UI 日志区可见</summary>
    private static void EnsureJavaLibrary(string path)
    {
        if (!File.Exists(path))
        {
            Trace($"[bridge] 跳过不存在的依赖库：{path}");
            return;
        }
        Trace($"[bridge] System.load：{Path.GetFileName(path)}");
        using var handle = new JniLocalRef(JNIEnv.NewString(path));
        JNIEnv.CallStaticVoidMethod(_clazz, _loadNativeLibrary, new JValue(handle.Handle));
        IntPtr ex = JNIEnv.ExceptionOccurred();
        if (ex == IntPtr.Zero)
        {
            Trace($"[bridge] 已加载 {Path.GetFileName(path)}");
            return;
        }
        JNIEnv.ExceptionClear();
        string detail = GetThrowableMessage(ex);
        JNIEnv.DeleteLocalRef(ex);
        if (detail.Contains("already loaded", StringComparison.OrdinalIgnoreCase))
        {
            Trace($"[bridge] 已加载（去重）{Path.GetFileName(path)}");
            return;
        }
        throw new InvalidOperationException(
            $"依赖库加载失败：{Path.GetFileName(path)}\n{detail}");
    }

    /// <summary>取 Throwable.getMessage()（用于把 Java 异常详情带上 C# 异常链）</summary>
    private static string GetThrowableMessage(IntPtr throwable)
    {
        IntPtr throwableClass = JNIEnv.FindClass("java/lang/Throwable");
        try
        {
            IntPtr getMessage = JNIEnv.GetMethodID(throwableClass, "getMessage",
                "()Ljava/lang/String;");
            IntPtr msg = JNIEnv.CallObjectMethod(throwable, getMessage);
            return msg != IntPtr.Zero
                ? JNIEnv.GetString(msg, JniHandleOwnership.TransferLocalRef) ?? "(空消息)"
                : "(无消息)";
        }
        finally
        {
            JNIEnv.DeleteGlobalRef(throwableClass);
        }
    }

    /// <summary>桥内诊断事件：宿主订阅后转发 UI 日志区——真机 logcat 抓不到 app 自有日志，
    /// UI 日志区是加载失败点唯一可靠取证通道</summary>
    public static event Action<string>? Traced;

    /// <summary>桥内诊断标记：直达 logcat 并转发 Traced 事件（消费方自行切主线程）</summary>
    private static void Trace(string message)
    {
        global::Android.Util.Log.Info("StarcloudFlow", message);
        Traced?.Invoke(message);
    }

    /// <summary>显式加载 libpojavexec（幂等；失败抛出由调用方决定是否致命）</summary>
    public static void EnsureLoaded(string pojavExecPath)
    {
        using var path = new JniLocalRef(JNIEnv.NewString(pojavExecPath));
        CallVoid(_ensureLoaded, new JValue(path.Handle));
    }

    /// <summary>注册抓取状态回调：捕获鼠标 -> 相对移动；释放 -> 绝对坐标。
    /// 直接传 peer 句柄（引用种类对参数传递无影响），不做任何 JNI 删除——
    /// ToJniHandle/NewGlobalRef 产生的是全局引用，误用 DeleteLocalRef 会触发 CheckJNI abort</summary>
    public static void SetGrabCallbacks(Action onGrabbed, Action onReleased)
    {
        _grabbedRunnable = new ActionRunnable(() => { IsGrabbing = true; onGrabbed(); });
        _releasedRunnable = new ActionRunnable(() => { IsGrabbing = false; onReleased(); });
        CallVoid(_setGrabStateCallbacks,
            new JValue(_grabbedRunnable.Handle), new JValue(_releasedRunnable.Handle));
    }

    public static void SetupBridgeWindow(object surface)
    {
        // Surface 由调用方（GamePage._surface）持有保活；此处只借用句柄，不增删引用
        var jSurface = (IJavaObject)surface;
        CallVoid(_setupBridgeWindow, new JValue(jSurface.Handle));
    }

    public static void SendUpdateWindowSize(int width, int height) =>
        CallVoid(_sendUpdateWindowSize, new JValue(width), new JValue(height));

    public static void SendKeycode(int androidKeycode, char keyChar, int scanCode, int mods, bool isDown) =>
        CallVoid(_sendKeycode,
            new JValue(androidKeycode), new JValue(keyChar), new JValue(scanCode),
            new JValue(mods), new JValue(isDown));

    public static void SendMouseButton(int button, bool isDown) =>
        CallVoid(_sendMouseButton, new JValue(button), new JValue(isDown));

    public static void SendMouseKeycode(int button, int modifiers, bool isDown) =>
        CallVoid(_sendMouseKeycode,
            new JValue(button), new JValue(modifiers), new JValue(isDown));

    public static void SendScroll(double dx, double dy) =>
        CallVoid(_sendScroll, new JValue(dx), new JValue(dy));

    public static void SendCursorPos(float x, float y) =>
        CallVoid(_sendCursorPos, new JValue(x), new JValue(y));

    public static void SendCursorDelta(float dx, float dy) =>
        CallVoid(_sendCursorDelta, new JValue(dx), new JValue(dy));

    public static void ResetInputState() => CallVoid(_resetInputState);

    public static void SetModifiers(int glfwKeycode, bool isDown) =>
        CallVoid(_setModifiers, new JValue(glfwKeycode), new JValue(isDown));

    public static void PutMouseEventWithCoords(int button, float x, float y) =>
        CallVoid(_putMouseEventWithCoords, new JValue(button), new JValue(x), new JValue(y));

    private static IntPtr MethodId(string name, string signature) =>
        JNIEnv.GetStaticMethodID(_clazz, name, signature);

    /// <summary>统一 CallStaticVoidMethod + 挂起异常检查（详情 dump 进 logcat）</summary>
    private static void CallVoid(IntPtr methodId, params JValue[] args)
    {
        JNIEnv.CallStaticVoidMethod(_clazz, methodId, args);
        CheckNoException("CallbackBridge." + methodId.ToString("X"));
    }

    private static void CheckNoException(string step)
    {
        IntPtr ex = JNIEnv.ExceptionOccurred();
        if (ex == IntPtr.Zero)
        {
            return;
        }
        try
        {
            JNIEnv.ExceptionDescribe();
        }
        finally
        {
            // ExceptionOccurred 返回局部引用，用完即还（原实现泄漏一处）
            JNIEnv.DeleteLocalRef(ex);
            JNIEnv.ExceptionClear();
        }
        throw new InvalidOperationException($"{step} 抛出 Java 异常（详情见 logcat）");
    }

    /// <summary>JNIEnv local ref 的 using 包装（线程局部引用表有上限，用完即还）</summary>
    private sealed class JniLocalRef(IntPtr handle) : IDisposable
    {
        public IntPtr Handle { get; } = handle;

        public void Dispose()
        {
            if (Handle != IntPtr.Zero)
            {
                JNIEnv.DeleteLocalRef(Handle);
            }
        }
    }

    /// <summary>grab 状态回调的 Runnable 适配（回调线程执行）</summary>
    private sealed class ActionRunnable : Java.Lang.Object, Java.Lang.IRunnable
    {
        private readonly Action _action;

        public ActionRunnable(Action action)
        {
            _action = action;
        }

        public void Run() => _action();
    }
}
