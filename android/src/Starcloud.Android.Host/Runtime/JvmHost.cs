// JVM 宿主：专用线程上拉起 HotSpot 并调用版本主类 main。
// 流程（spike S1 实证形态）：so 加载序 -> stdout/stderr 重定向 -> 工作目录与环境 ->
// JNI_CreateJavaVM -> main(String[]) -> DestroyJavaVM。
// so 不打进 APK：位于组件部署目录（glfw-stub / jre / 转译层），按依赖优先级 dlopen。

using System.Runtime.InteropServices;
using Starcloud.Android.Core.Launch;
using Starcloud.Android.Host.Interop;

namespace Starcloud.Android.Host.Runtime;

internal static unsafe class JvmHost
{
    // dlopen 依赖优先级：libc++_shared 先于 pojavexec，pojavexec 先于 awt，libjvm 随后；
    // 其余（转译层 / openal / jre 同伴库）无依赖顺序要求
    private const int PrioLibCxxShared = 0;
    private const int PrioPojavExec = 1;
    private const int PrioPojavExecAwt = 2;
    private const int PrioLibJvm = 3;
    private const int PrioOther = 10;

    private static Thread? _thread;
    private static readonly object StartLock = new();

    /// <summary>JVM 退出（正常或异常）回调：参数为进程退出码</summary>
    public static event Action<int>? Exited;

    public static bool IsRunning => _thread is { IsAlive: true };

    /// <summary>
    /// 环境预置：把 spec.Env 全量写入进程 bionic environ（同进程内 C# 与 JVM 共享）。
    /// SDL 模式下必须在 SdlGameActivity 启动之前调用——活动 onCreate 内的
    /// initializeHooks 装钩子时会读取环境变量（如 dlsym 代理读 POJAVEXEC_EGL），
    /// 晚于活动的 setenv 对钩子不可见；RunJvm 内的重放是幂等的（overwrite=1）。
    /// </summary>
    public static void ApplyEnvironment(LaunchSpec spec)
    {
        foreach (var (name, value) in spec.Env)
        {
            int rc;
            if (name == "PATH" && value.EndsWith(':'))
            {
                // PATH 尾冒号在此补全壳进程原 PATH
                string? processPath = Environment.GetEnvironmentVariable("PATH");
                rc = Libc.setenv(name, value + processPath, 1 /* overwrite */);
            }
            else
            {
                rc = Libc.setenv(name, value, 1 /* overwrite */);
            }
            if (rc != 0)
            {
                Trace($"[env] setenv 失败：{name}（rc={rc}）");
            }
        }
        EnsureDirectory(spec.Env, "TMPDIR");
        EnsureDirectory(spec.Env, "MESA_GLSL_CACHE_DIR");
        DumpGraphicsEnvironment();
    }

    /// <summary>
    /// 诊断转储：用 native getenv 回读图形链关键环境变量的实际生效值，
    /// 验证 setenv 已写入本进程 environ、路径指向的文件真实存在
    /// </summary>
    private static void DumpGraphicsEnvironment()
    {
        foreach (string name in new[] { "POJAVEXEC_EGL", "SDL_EGL_LIBRARY", "SDL_OPENGL_LIBRARY", "DRIVER_PATH", "LD_LIBRARY_PATH" })
        {
            string? value = Marshal.PtrToStringUTF8(Libc.getenv(name));
            if (value is null)
            {
                Trace($"[env] {name}=<未设置>");
                continue;
            }
            Trace(value.Length <= 300 ? $"[env] {name}={value}" : $"[env] {name}={value[..300]}…（截断，共 {value.Length} 字符）");
            if (value.StartsWith('/') && !value.Contains(':') && !File.Exists(value) && !Directory.Exists(value))
            {
                Trace($"[env] 警告：{name} 指向的路径不存在");
            }
        }
    }

    /// <summary>启动 JVM 线程；同一时刻只允许一个 JVM 存在</summary>
    public static void Start(LaunchSpec spec)
    {
        lock (StartLock)
        {
            if (IsRunning)
            {
                throw new InvalidOperationException("游戏进程已在运行中");
            }
            _thread = new Thread(() => RunCore(spec))
            {
                Name = "starcloud-jvm",
                // 非后台线程：JVM 存活期间壳进程不被回收；退出由 Exited 事件通知壳
                IsBackground = false,
            };
            _thread.Start();
        }
    }

    private static void RunCore(LaunchSpec spec)
    {
        Trace("[jvm] RunCore 进入");
        int exitCode;
        try
        {
            exitCode = RunJvm(spec);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[JvmHost] 致命错误：{ex}");
            exitCode = 1;
        }
        Console.Error.WriteLine($"[JvmHost] JVM 退出码 {exitCode}");
        DumpJvmLog(spec.WorkingDir);
        Exited?.Invoke(exitCode);
    }

    /// <summary>JVM 退出后把 stdout/stderr 日志尾部转发 logcat（重定向后 Console 流不再可见，logcat 是唯一现场）</summary>
    private static void DumpJvmLog(string workingDir)
    {
        try
        {
            string logPath = Path.Combine(workingDir, "jvm-stdout.log");
            if (!File.Exists(logPath))
            {
                Trace("[jvm-log] 日志文件不存在");
                return;
            }
            var lines = File.ReadAllLines(logPath);
            Trace($"[jvm-log] 共 {lines.Length} 行，输出末尾 80 行");
            foreach (var line in lines.TakeLast(80))
            {
                Trace("[jvm-log] " + line);
            }
        }
        catch (Exception ex)
        {
            Trace("[jvm-log] 读取失败：" + ex.Message);
        }
    }

    /// <summary>JVM 全生命周期；任何失败以异常上抛（线程体捕获置退出码 1）</summary>
    private static int RunJvm(LaunchSpec spec)
    {
        // [1/6] so 加载序：pojavexec（GLFW stub native）必须先于 libjvm——
        // 其注册的 JNI native 方法在游戏类首次触达时由 JVM 按需解析
        Trace("[jvm] 开始加载 native 库");
        LoadNativeLibraries(spec);
        Trace("[jvm] native 库加载完成");

        // [2/6] stdout/stderr 重定向：JVM 启动日志 / ExceptionDescribe / hs_err 摘要全走 stderr。
        // 0644：尽力让 shell 侧 adb 可直读；但华为等设备的 FUSE 挂载会按所有权模型把
        // /storage 上的新文件强制压成 0600（实测 open mode 与后续 chmod 都不生效），
        // 真机诊断主要靠 [jvm-log] 镜像进 logcat（tag=StarcloudFlow），文件权限不作为承诺。
        string logPath = Path.Combine(spec.WorkingDir, "jvm-stdout.log");
        int fd = Libc.open(logPath, Libc.O_WRONLY | Libc.O_CREAT | Libc.O_TRUNC, 0x1A4 /* 0644 */);
        if (fd < 0)
        {
            throw new InvalidOperationException($"open({logPath}) 失败 errno={Marshal.GetLastWin32Error()}");
        }
        Libc.dup2(fd, 1);
        Libc.dup2(fd, 2);
        if (fd > 2)
        {
            Libc.close(fd);
        }
        Trace($"[jvm] stdout/stderr 已重定向 {logPath}");
        StartLogMirror(logPath);

        // [3/6] 工作目录与环境（Minecraft 实例目录）；SDL 模式已在活动启动前
        // 应用过环境（ApplyEnvironment，钩子快照需要），此处幂等重放
        Libc.chdir(spec.WorkingDir);
        ApplyEnvironment(spec);

        // [4/6] 组装启动参数并创建 JVM
        var pojavExecPath = FindLibrary(spec.NativeDirs, "libpojavexec.so")
            ?? throw new InvalidOperationException("找不到 libpojavexec.so（glfw-stub 组件未部署？）");
        var optionStrings = new List<string>
        {
            "-Djava.home=" + spec.Env["JAVA_HOME"],
            "-Djava.class.path=" + string.Join(':', spec.Classpath),
            "-Dpojav.exec.path=" + pojavExecPath,
            "-XX:ErrorFile=" + spec.WorkingDir + "/hs_err_%p.log",
        };
        optionStrings.AddRange(spec.JvmArgs);

        // 诊断 dump：成功轮参数留档（对照 Compose 壳排 jre25 SEGV 差异，2026-10）
        foreach (var o in optionStrings) Trace("[jvm-opt] " + o);
        foreach (var (k, v) in spec.Env) Trace("[jvm-env] " + k + "=" + v);
        Trace("[jvm-main] " + spec.MainClass);
        foreach (var a in spec.ProgramArgs) Trace("[jvm-game] " + a);
        var optionPtrs = optionStrings.Select(Marshal.StringToHGlobalAnsi).ToArray();
        try
        {
            var options = (Jni.JavaVMOption*)Marshal.AllocHGlobal(
                sizeof(Jni.JavaVMOption) * optionPtrs.Length);
            try
            {
                for (int i = 0; i < optionPtrs.Length; i++)
                {
                    options[i] = new Jni.JavaVMOption { OptionString = optionPtrs[i], ExtraInfo = IntPtr.Zero };
                }
                var args = new Jni.JavaVMInitArgs
                {
                    Version = Jni.JniVersion18,
                    NOptions = optionPtrs.Length,
                    Options = (IntPtr)options,
                    IgnoreUnrecognized = 1, // JNI_TRUE：容忍组件自带 JVM 参数
                };

                var libJvmPath = FindLibrary(spec.NativeDirs, "libjvm.so")
                    ?? throw new InvalidOperationException("找不到 libjvm.so（JRE 组件未部署？）");
                var libJvm = Libc.dlopen(libJvmPath, Libc.RTLD_NOW | Libc.RTLD_GLOBAL);
                if (libJvm == IntPtr.Zero)
                {
                    throw new InvalidOperationException($"dlopen({libJvmPath}) 失败 errno={Marshal.GetLastWin32Error()}");
                }
                var createVm = (delegate* unmanaged<IntPtr*, IntPtr*, Jni.JavaVMInitArgs*, int>)
                    NativeLibrary.GetExport(libJvm, "JNI_CreateJavaVM");

                IntPtr vm = IntPtr.Zero;
                IntPtr env = IntPtr.Zero;
                Trace("[jvm] JNI_CreateJavaVM 前");
                int rc = createVm(&vm, &env, &args);
                Jni.Check(rc, "JNI_CreateJavaVM");
                Trace("[jvm] JNI_CreateJavaVM 完成");
                Console.Error.WriteLine(
                    $"[JvmHost] JVM 已创建 vm=0x{vm.ToInt64():X} env=0x{env.ToInt64():X}");

                // [5/6] 调版本主类 main(String[])
                Trace("[jvm] 调用 main 前");
                InvokeMain(env, spec);

                // [6/6] 等待 JVM 非守护线程结束后销毁
                var destroy = (delegate* unmanaged<IntPtr, int>)Jni.Fn(vm, Jni.VmDestroyJavaVm);
                rc = destroy(vm);
                Jni.Check(rc, "DestroyJavaVM");
                return 0;
            }
            finally
            {
                Marshal.FreeHGlobal((IntPtr)options);
            }
        }
        finally
        {
            foreach (var p in optionPtrs)
            {
                Marshal.FreeHGlobal(p);
            }
        }
    }

    /// <summary>
    /// 后台线程轮询 jvm-stdout.log 追加行并镜像 logcat。
    /// 存在理由：游戏退出（exit 253）会令整个进程瞬死，DumpJvmLog 的退出后镜像没有机会执行；
    /// FUSE 又把文件压成 0600 让 adb 直读不可行，实时轮询是唯一的现场留存手段。
    /// </summary>
    private static void StartLogMirror(string logPath)
    {
        var mirror = new Thread(() =>
        {
            try
            {
                using var fs = new FileStream(logPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                using var reader = new StreamReader(fs);
                while (true)
                {
                    string? line;
                    while ((line = reader.ReadLine()) is not null)
                    {
                        if (line.Length > 0)
                        {
                            Trace("[jvm-out] " + line);
                        }
                    }
                    Thread.Sleep(400);
                }
            }
            catch
            {
                // 进程退出时文件句柄失效属正常路径
            }
        })
        {
            IsBackground = true,
            Name = "starcloud-jvm-log",
        };
        mirror.Start();
    }

    /// <summary>FindClass('.')→'/' + GetStaticMethodID(main) + 变参按固定 4 参签名调用（arm64 实证）</summary>
    private static void InvokeMain(IntPtr env, LaunchSpec spec)
    {
        var findClass = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Jni.Fn(env, Jni.EnvFindClass);
        IntPtr mainClassUtf = Marshal.StringToHGlobalAnsi(spec.MainClass.Replace('.', '/'));
        try
        {
            IntPtr clazz = findClass(env, mainClassUtf);
            Jni.CheckRef(env, clazz, $"FindClass({spec.MainClass})");

            var getStaticMethodId =
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)Jni.Fn(env, Jni.EnvGetStaticMethodId);
            IntPtr name = Marshal.StringToHGlobalAnsi("main");
            try
            {
                IntPtr sig = Marshal.StringToHGlobalAnsi("([Ljava/lang/String;)V");
                try
                {
                    IntPtr mid = getStaticMethodId(env, clazz, name, sig);
                    Jni.CheckRef(env, mid, "GetStaticMethodID(main)");

                    var newStringUtf = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Jni.Fn(env, Jni.EnvNewStringUtf);
                    var newObjectArray =
                        (delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, IntPtr>)Jni.Fn(env, Jni.EnvNewObjectArray);
                    var setElement =
                        (delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, void>)Jni.Fn(env, Jni.EnvSetObjectArrayElement);

                    var findStringClass = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Jni.Fn(env, Jni.EnvFindClass);
                    IntPtr jls = Marshal.StringToHGlobalAnsi("java/lang/String");
                    try
                    {
                        IntPtr stringClass = findStringClass(env, jls);
                        Jni.CheckRef(env, stringClass, "FindClass(java/lang/String)");

                        IntPtr argsArray = newObjectArray(env, spec.ProgramArgs.Count, stringClass, IntPtr.Zero);
                        Jni.CheckRef(env, argsArray, "NewObjectArray");
                        for (int i = 0; i < spec.ProgramArgs.Count; i++)
                        {
                            IntPtr argUtf = Marshal.StringToHGlobalAnsi(spec.ProgramArgs[i]);
                            try
                            {
                                IntPtr jstr = newStringUtf(env, argUtf);
                                Jni.CheckRef(env, jstr, "NewStringUTF");
                                setElement(env, argsArray, i, jstr);
                            }
                            finally
                            {
                                Marshal.FreeHGlobal(argUtf);
                            }
                        }

                        var callStaticVoid =
                            (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)Jni.Fn(env, Jni.EnvCallStaticVoidMethod);
                        // 变参函数按固定 4 参签名调用：arm64 下前几个参数全走寄存器，与变参布局一致
                        callStaticVoid(env, clazz, mid, argsArray);
                        Jni.CheckPendingException(env, "CallStaticVoidMethod(main)");
                        Console.Error.WriteLine("[JvmHost] main 执行完成，无挂起异常");
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(jls);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(sig);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(name);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(mainClassUtf);
        }
    }

    /// <summary>
    /// 按依赖优先级加载 NativeDirs 下全部 so；核心 so（pojavexec/libjvm）失败即中止，
    /// 其余（转译层等）失败记日志跳过——渲染链可由 LWJGL 运行时显式加载兜底。
    /// </summary>
    private static void LoadNativeLibraries(LaunchSpec spec)
    {
        var libs = new List<(int Priority, string Path)>();
        foreach (var dir in spec.NativeDirs)
        {
            if (!Directory.Exists(dir))
            {
                continue;
            }
            foreach (var so in Directory.EnumerateFiles(dir, "*.so", SearchOption.TopDirectoryOnly))
            {
                libs.Add((PriorityOf(Path.GetFileName(so)), so));
            }
        }
        // 同优先级按路径排序保证加载顺序确定。
        // 关键：必须用 dlopen(RTLD_GLOBAL) 而非 .NET NativeLibrary.Load（RTLD_LOCAL）——
        // libpojavexec 对 JVM 子进程孵化（ProcessImpl.forkAndExec）的 JNI 钩子通过
        // dlsym(RTLD_DEFAULT) 解析 libjava.so 里的原函数；RTLD_LOCAL 加载的库不在
        // 全局作用域，dlsym 拿到 NULL 也会照样注册钩子，游戏首次创建子进程即 SIGSEGV
        foreach (var (priority, path) in libs
                     .OrderBy(l => l.Priority)
                     .ThenBy(l => l.Path, StringComparer.Ordinal))
        {
            var handle = Libc.dlopen(path, Libc.RTLD_NOW | Libc.RTLD_GLOBAL);
            if (handle == IntPtr.Zero && priority < PrioOther)
            {
                throw new InvalidOperationException(
                    $"dlopen 核心库失败：{path}（dlerror 见 logcat）");
            }
            if (handle == IntPtr.Zero)
            {
                Console.Error.WriteLine($"[JvmHost] 跳过 {path}：dlopen 失败 errno={Marshal.GetLastWin32Error()}");
            }
        }
    }

    private static int PriorityOf(string fileName) => fileName switch
    {
        "libc++_shared.so" => PrioLibCxxShared,
        "libpojavexec.so" => PrioPojavExec,
        "libpojavexec_awt.so" => PrioPojavExecAwt,
        "libjvm.so" => PrioLibJvm,
        _ => PrioOther,
    };

    /// <summary>宿主内诊断标记：直达 logcat（重定向只影响 Console 流，不影响 Log.Info）</summary>
    private static void Trace(string message) =>
        global::Android.Util.Log.Info("StarcloudFlow", message);

    private static string? FindLibrary(IReadOnlyList<string> dirs, string fileName)
    {
        foreach (var dir in dirs)
        {
            var p = Path.Combine(dir, fileName);
            if (File.Exists(p))
            {
                return p;
            }
        }
        return null;
    }

    private static void EnsureDirectory(IReadOnlyDictionary<string, string> env, string key)
    {
        if (!env.TryGetValue(key, out var path) || string.IsNullOrEmpty(path))
        {
            return;
        }
        Directory.CreateDirectory(path);
    }
}
