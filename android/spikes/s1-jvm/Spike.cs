using System.Runtime.InteropServices;

namespace Starcloud.Spike.S1Jvm;

/// <summary>
/// M0-S1 JVM 拉起技术验证核心流程（设计稿 §6.1）：
/// dup2 重定向标准输出 -> 绝对路径加载 libjvm.so -> JNI_CreateJavaVM ->
/// 反射调用 HelloWorld.main 拿标准输出 -> 子线程 attach/detach -> DestroyJavaVM ->
/// 读回捕获文件断言标记串。
/// 所有 JNI 调用经 env / JavaVM 函数表按规范索引取函数指针完成，不依赖绑定生成。
/// </summary>
internal static unsafe class Spike
{
    // JNI_VERSION_1_8（HotSpot 支持的最高 JNI 版本常量）
    private const int JniVersion18 = 0x00010008;

    // JavaVM 函数表索引（JNIInvokeInterface_：[0-2] 保留共 3 项，[3] 起 DestroyJavaVM；
    // 注意与 JNIEnv 表区分：后者保留 4 项，[4] 起 GetVersion）
    private const int VmDestroyJavaVm = 3;
    private const int VmAttachCurrentThread = 4;
    private const int VmDetachCurrentThread = 5;
    private const int VmGetEnv = 6;
    private const int VmAttachCurrentThreadAsDaemon = 7;

    // JNIEnv 函数表索引
    private const int EnvGetVersion = 4;
    private const int EnvFindClass = 6;
    private const int EnvExceptionOccurred = 15;
    private const int EnvExceptionDescribe = 16;
    private const int EnvExceptionClear = 17;
    private const int EnvGetStaticMethodId = 113;
    private const int EnvCallStaticVoidMethod = 141;
    private const int EnvNewStringUtf = 167;
    private const int EnvNewObjectArray = 172;
    private const int EnvSetObjectArrayElement = 174;

    // 真机部署路径（s1d 步骤经 run-as 拷入 app 内部目录）。
    // SELinux 实证：/data/local/tmp 下文件为 shell_data_file 上下文，untrusted_app 域
    // 仅可读不可执行（dlopen mmap PROT_EXEC 被拒，avc denied { execute } permissive=0），
    // 因此 JRE 必须位于 app 私有目录（app_data_file 上下文允许 dlopen，即应用加载自身 so 的标准路径）。
    // 不依赖 LD_LIBRARY_PATH：HotSpot 按 sun.boot.library.path（= java.home/lib）绝对路径加载 libjava.so 等同伴库
    private const string JreRoot = "/data/data/cn.starcloud.spike.s1jvm/files/jre";
    private const string ClassPath = "/data/data/cn.starcloud.spike.s1jvm/files/classes";
    private const string PassMark = "Hello from Starcloud S1";

    [StructLayout(LayoutKind.Sequential)]
    private struct JavaVMOption
    {
        public IntPtr OptionString; // char*
        public IntPtr ExtraInfo;    // void*
    }

    // arm64 布局：version@0、nOptions@4、options@8（指针 8 字节对齐）、ignoreUnrecognized@16，总大小 24
    [StructLayout(LayoutKind.Sequential)]
    private struct JavaVMInitArgs
    {
        public int Version;
        public int NOptions;
        public IntPtr Options;
        public byte IgnoreUnrecognized;
    }

    // arm64 布局：version@0、name@8（char*）、group@16（jobject），总大小 24
    [StructLayout(LayoutKind.Sequential)]
    private struct JavaVMAttachArgs
    {
        public int Version;
        public IntPtr Name;  // char*
        public IntPtr Group; // jobject
    }

    // libc.so：stdout/stderr 重定向（Android 应用进程默认 stdout 进 /dev/null，必须 dup2 到文件）
    private const int O_WRONLY = 0x1;
    private const int O_CREAT = 0x40;
    private const int O_TRUNC = 0x200;

    [DllImport("libc.so", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int open(string path, int flags, uint mode);

    [DllImport("libc.so")]
    private static extern int dup2(int oldFd, int newFd);

    [DllImport("libc.so")]
    private static extern int close(int fd);

    // libc.so：原生 pthread（attach 对照路径，绕开 Mono 托管线程栈）
    [DllImport("libc.so")]
    private static extern int pthread_create(
        out IntPtr tid, IntPtr attr, delegate* unmanaged<void*, void*> entry, void* arg);

    [DllImport("libc.so")]
    private static extern int pthread_join(IntPtr tid, IntPtr retval);

    // pthread 入口向主流程回传的 attach 结果（UnmanagedCallersOnly 静态方法无法用闭包，走静态字段）
    private static IntPtr _ptVm;
    private static IntPtr _ptName; // JavaVMAttachArgs.name，主线程预分配 ANSI 串
    private static bool _ptUseAsDaemon; // false = 普通 AttachCurrentThread，true = AttachCurrentThreadAsDaemon
    private static int _ptAttachRc = int.MinValue;
    private static int _ptGetEnvRc = int.MinValue;
    private static int _ptVer = int.MinValue;
    private static int _ptDetachRc = int.MinValue;

    [UnmanagedCallersOnly]
    private static void* PthreadAttachEntry(void* arg)
    {
        // 两种 attach 形态各跑一次原生 pthread 全周期：attach -> GetEnv -> GetVersion -> detach
        IntPtr vm = _ptVm;
        int attachIdx = _ptUseAsDaemon ? VmAttachCurrentThreadAsDaemon : VmAttachCurrentThread;
        var attach = (delegate* unmanaged<IntPtr, IntPtr*, JavaVMAttachArgs*, int>)Fn(vm, attachIdx);
        var getEnv = (delegate* unmanaged<IntPtr, IntPtr*, int, int>)Fn(vm, VmGetEnv);
        var detach = (delegate* unmanaged<IntPtr, int>)Fn(vm, VmDetachCurrentThread);

        IntPtr tenv = IntPtr.Zero;
        var jargs = new JavaVMAttachArgs { Version = JniVersion18, Name = _ptName, Group = IntPtr.Zero };
        _ptAttachRc = attach(vm, &tenv, &jargs);
        if (tenv != IntPtr.Zero)
        {
            _ptGetEnvRc = getEnv(vm, &tenv, JniVersion18);
            _ptVer = ((delegate* unmanaged<IntPtr, int>)Fn(tenv, EnvGetVersion))(tenv);
            _ptDetachRc = detach(vm);
        }
        return null;
    }

    public static void Run(Action<string> log, string cacheDir)
    {
        log("=== M0-S1 JVM 拉起验证 开始 ===");
        string capturePath = Path.Combine(cacheDir, "s1-stdout.txt");
        // 托管侧诊断文件的可靠通道：Console.* 在 .NET Android 走 logcat 不受 [1/9] 的 fd 重定向影响，
        // 只有托管文件 IO 直写落盘；AttachWorker 崩溃取证与最终结论均写这里
        string diagPath = capturePath + ".diag.txt";
        File.AppendAllText(diagPath, "=== 运行开始 " + DateTime.Now.ToString("HH:mm:ss") + " ===" + Environment.NewLine);

        try
        {
            // [1/9] stdout/stderr 重定向（JVM 启动日志与 ExceptionDescribe 都走 stderr）
            int fd = open(capturePath, O_WRONLY | O_CREAT | O_TRUNC, 0x180 /* 0600 */);
            if (fd < 0)
            {
                throw new InvalidOperationException(
                    $"open({capturePath}) 失败 errno={Marshal.GetLastWin32Error()}");
            }
            dup2(fd, 1);
            dup2(fd, 2);
            if (fd > 2)
            {
                close(fd);
            }
            log($"[1/9] stdout/stderr 已重定向 -> {capturePath}");

            // [2/9] 绝对路径加载 libjvm.so（设计稿 §6.3：不依赖 LD_LIBRARY_PATH）
            string libJvmPath = $"{JreRoot}/lib/server/libjvm.so";
            IntPtr libJvm = NativeLibrary.Load(libJvmPath);
            log($"[2/9] libjvm.so 已加载 @ 0x{libJvm.ToInt64():X}");
            // dlopen 返回值是 soinfo 句柄而非加载基址；真实基址看 maps（诊断 attach 崩溃 fault addr 用）
            foreach (string line in File.ReadLines("/proc/self/maps"))
            {
                if (line.Contains("libjvm.so", StringComparison.Ordinal))
                {
                    log("      maps: " + line.TrimEnd());
                }
            }

            // [3/9] 取导出符号，确认创建前无已存在 VM
            var createVm = (delegate* unmanaged<IntPtr*, IntPtr*, JavaVMInitArgs*, int>)
                NativeLibrary.GetExport(libJvm, "JNI_CreateJavaVM");
            var getCreatedVms = (delegate* unmanaged<IntPtr*, int, int*, int>)
                NativeLibrary.GetExport(libJvm, "JNI_GetCreatedJavaVMs");
            int createdBefore = -1;
            IntPtr* vmBuf = stackalloc IntPtr[4];
            int rc = getCreatedVms(vmBuf, 4, &createdBefore);
            Check(rc, "JNI_GetCreatedJavaVMs");
            log($"[3/9] 创建前已存在 VM 数 = {createdBefore}（期望 0）");

            // [4/9] 组装 JavaVMInitArgs 并创建 JVM
            IntPtr homeOpt = Marshal.StringToHGlobalAnsi("-Djava.home=" + JreRoot);
            try
            {
                IntPtr cpOpt = Marshal.StringToHGlobalAnsi("-Djava.class.path=" + ClassPath);
                try
                {
                    // HotSpot 致命错误时写 hs_err 到 cache 目录（含带符号 native 栈，诊断 attach 崩溃用）
                    IntPtr errOpt = Marshal.StringToHGlobalAnsi(
                        "-XX:ErrorFile=" + cacheDir + "/hs_err_%p.log");
                    try
                    {
                        JavaVMOption* options = stackalloc JavaVMOption[3];
                        options[0] = new JavaVMOption { OptionString = homeOpt };
                        options[1] = new JavaVMOption { OptionString = cpOpt };
                        options[2] = new JavaVMOption { OptionString = errOpt };
                        var args = new JavaVMInitArgs
                        {
                            Version = JniVersion18,
                            NOptions = 3,
                            Options = (IntPtr)options,
                            IgnoreUnrecognized = 1, // JNI_TRUE
                        };
                        IntPtr vm = IntPtr.Zero;
                        IntPtr env = IntPtr.Zero;
                        rc = createVm(&vm, &env, &args);
                        Check(rc, "JNI_CreateJavaVM");
                        log($"[4/9] JNI_CreateJavaVM rc=0 vm=0x{vm.ToInt64():X} env=0x{env.ToInt64():X}");

                        RunJvmChecks(log, vm, env, diagPath);
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(errOpt);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(cpOpt);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(homeOpt);
            }

            // 读回捕获文件并断言标记串
            string output = File.ReadAllText(capturePath);
            log("---- 捕获的 JVM 输出 ----");
            foreach (string line in output.Split('\n'))
            {
                if (line.Length > 0)
                {
                    log("  | " + line.TrimEnd('\r'));
                }
            }
            log("------------------------");
            bool pass = output.Contains(PassMark, StringComparison.Ordinal);
            string verdict = pass
                ? "=== S1 结论：通过（加载/创建/调用/attach/销毁/输出断言全部成功）==="
                : $"=== S1 结论：未通过（输出缺少标记「{PassMark}」）===";
            log(verdict);
            File.AppendAllText(diagPath, verdict + Environment.NewLine);
        }
        catch (Exception ex)
        {
            log("!! 中止：" + ex.Message);
            File.AppendAllText(diagPath, "!! 中止：" + ex.Message + Environment.NewLine);
            // 尽力读回捕获文件：ExceptionDescribe 与 JVM 启动日志都写进了重定向后的 stderr
            try
            {
                log("---- 捕获文件内容 ----");
                foreach (string line in File.ReadAllText(capturePath).Split('\n'))
                {
                    if (line.Length > 0)
                    {
                        log("  | " + line.TrimEnd('\r'));
                    }
                }
            }
            catch
            {
                // 捕获文件不存在（如步骤 1 之前就失败）则忽略
            }
            log("=== S1 结论：未通过 ===");
        }
    }

    /// <summary>创建成功后的 JNI 功能验证：GetVersion / FindClass / 调 main / attach / destroy。</summary>
    private static void RunJvmChecks(Action<string> log, IntPtr vm, IntPtr env, string diagPath)
    {
        // [5/9] env 基础校验
        var getVersion = (delegate* unmanaged<IntPtr, int>)Fn(env, EnvGetVersion);
        int jniVer = getVersion(env);
        log($"[5/9] GetVersion -> 0x{jniVer:X4}（期望 0x{JniVersion18:X4}）");

        // [6/9] FindClass + GetStaticMethodID
        var findClass = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Fn(env, EnvFindClass);
        IntPtr name = Marshal.StringToHGlobalAnsi("HelloWorld");
        try
        {
            IntPtr clazz = findClass(env, name);
            CheckRef(env, clazz, "FindClass(HelloWorld)");
            log($"[6/9] FindClass(\"HelloWorld\") -> 0x{clazz.ToInt64():X}");

            var getStaticMethodId =
                (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, IntPtr>)Fn(env, EnvGetStaticMethodId);
            IntPtr mName = Marshal.StringToHGlobalAnsi("main");
            try
            {
                IntPtr mSig = Marshal.StringToHGlobalAnsi("([Ljava/lang/String;)V");
                try
                {
                    IntPtr mid = getStaticMethodId(env, clazz, mName, mSig);
                    CheckRef(env, mid, "GetStaticMethodID(main)");
                    log($"      GetStaticMethodID(\"main\", \"([Ljava/lang/String;)V\") -> 0x{mid.ToInt64():X}");

                    // [7/9] 构造 String[] 参数并调用 main
                    InvokeMain(log, env, clazz, mid);
                }
                finally
                {
                    Marshal.FreeHGlobal(mSig);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(mName);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(name);
        }

        // [8/9] 子线程 attach/getEnv/detach（两种形态各一个原生 pthread 全周期）
        AttachWorker(log, vm, diagPath);

        // [9/9] 销毁 JVM
        var destroy = (delegate* unmanaged<IntPtr, int>)Fn(vm, VmDestroyJavaVm);
        int rc = destroy(vm);
        Check(rc, "DestroyJavaVM");
        log("[9/9] DestroyJavaVM rc=0");
        File.AppendAllText(diagPath, "[9/9] DestroyJavaVM rc=0" + Environment.NewLine);
    }

    private static void InvokeMain(Action<string> log, IntPtr env, IntPtr clazz, IntPtr mid)
    {
        var findClass = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Fn(env, EnvFindClass);
        var newStringUtf = (delegate* unmanaged<IntPtr, IntPtr, IntPtr>)Fn(env, EnvNewStringUtf);
        var newObjectArray =
            (delegate* unmanaged<IntPtr, int, IntPtr, IntPtr, IntPtr>)Fn(env, EnvNewObjectArray);
        var setElement =
            (delegate* unmanaged<IntPtr, IntPtr, int, IntPtr, void>)Fn(env, EnvSetObjectArrayElement);
        var callStaticVoid =
            (delegate* unmanaged<IntPtr, IntPtr, IntPtr, IntPtr, void>)Fn(env, EnvCallStaticVoidMethod);

        IntPtr jls = Marshal.StringToHGlobalAnsi("java/lang/String");
        try
        {
            IntPtr stringClass = findClass(env, jls);
            CheckRef(env, stringClass, "FindClass(java/lang/String)");

            IntPtr argStr = Marshal.StringToHGlobalAnsi("S1");
            try
            {
                IntPtr jstr = newStringUtf(env, argStr);
                CheckRef(env, jstr, "NewStringUTF");

                IntPtr argsArray = newObjectArray(env, 1, stringClass, IntPtr.Zero);
                CheckRef(env, argsArray, "NewObjectArray");
                setElement(env, argsArray, 0, jstr);

                // 变参函数按固定 4 参签名调用：arm64 下前几个参数全走寄存器，与变参布局一致
                callStaticVoid(env, clazz, mid, argsArray);
                CheckPendingException(env, "CallStaticVoidMethod(HelloWorld.main)");
                log("[7/9] HelloWorld.main 执行完成，无挂起异常");
            }
            finally
            {
                Marshal.FreeHGlobal(argStr);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(jls);
        }
    }

    private static void AttachWorker(Action<string> log, IntPtr vm, string diagPath)
    {
        // 诊断日志三写：log() 只进 UI；Console.Error 走 logcat；File.AppendAllText 是唯一可靠落盘通道
        Action<string> diag = msg =>
        {
            log(msg);
            Console.Error.WriteLine(msg);
            File.AppendAllText(diagPath, msg + Environment.NewLine);
        };

        diag("      maps 中 libjvm.so 映射（真实加载基址）：");
        LogJvmMappings(diag);

        // 路径 A：原生 pthread + 普通 AttachCurrentThread + 完整 JavaVMAttachArgs
        // （历史"普通 attach 确定性崩溃"系 JavaVM 表索引整体错位的幻象，本轮以正确索引重新验证）
        RunPthreadAttach(diag, vm, useAsDaemon: false, label: "a");
        // 路径 B：同形态换 AttachCurrentThreadAsDaemon（daemon 线程变体）
        RunPthreadAttach(diag, vm, useAsDaemon: true, label: "b");
    }

    private static void RunPthreadAttach(Action<string> diag, IntPtr vm, bool useAsDaemon, string label)
    {
        _ptVm = vm;
        _ptUseAsDaemon = useAsDaemon;
        _ptName = Marshal.StringToHGlobalAnsi("s1-pt");
        _ptAttachRc = int.MinValue;
        _ptGetEnvRc = int.MinValue;
        _ptVer = int.MinValue;
        _ptDetachRc = int.MinValue;
        int crc = pthread_create(out IntPtr tid, IntPtr.Zero,
            (delegate* unmanaged<void*, void*>)&PthreadAttachEntry, null);
        Check(crc, "pthread_create");
        pthread_join(tid, IntPtr.Zero);
        Marshal.FreeHGlobal(_ptName);
        string form = useAsDaemon ? "AsDaemon" : "普通 AttachCurrentThread";
        // GetVersion 返回 VM 实际支持的版本：本 JRE（HotSpot JDK10+）为 0xA0000（JNI_VERSION_10），
        // 并非 CreateJavaVM 请求的 0x00010008；请求版本在支持范围内时 Create/GetEnv 均按规范返回 JNI_OK
        diag($"[8/9{label}] 原生 pthread {form}+Args：Attach={_ptAttachRc} GetEnv={_ptGetEnvRc} " +
            $"GetVersion=0x{_ptVer:X} Detach={_ptDetachRc}（期望 Attach/GetEnv/Detach=0，GetVersion=0xA0000）");
    }

    /// <summary>打印 /proc/self/maps 中 libjvm.so 的映射行：真实加载基址（dlopen 返回值是 soinfo 句柄）。</summary>
    private static void LogJvmMappings(Action<string> log)
    {
        foreach (string line in File.ReadLines("/proc/self/maps"))
        {
            if (line.Contains("libjvm.so", StringComparison.Ordinal))
            {
                log("      " + line.TrimEnd());
            }
        }
    }

    /// <summary>取函数表第 index 项：table = *(void***)env。</summary>
    private static IntPtr Fn(IntPtr table, int index)
    {
        void** funcs = *(void***)table;
        return (IntPtr)funcs[index];
    }

    private static void Check(int rc, string step)
    {
        if (rc != 0)
        {
            throw new InvalidOperationException($"{step} 失败 rc={rc}");
        }
    }

    private static void CheckRef(IntPtr env, IntPtr handle, string step)
    {
        if (handle != IntPtr.Zero)
        {
            return;
        }
        throw PendingOr(env, step);
    }

    private static void CheckPendingException(IntPtr env, string step)
    {
        var occurred = (delegate* unmanaged<IntPtr, IntPtr>)Fn(env, EnvExceptionOccurred);
        if (occurred(env) == IntPtr.Zero)
        {
            return;
        }
        throw PendingOr(env, step);
    }

    private static Exception PendingOr(IntPtr env, string step)
    {
        var occurred = (delegate* unmanaged<IntPtr, IntPtr>)Fn(env, EnvExceptionOccurred);
        if (occurred(env) != IntPtr.Zero)
        {
            // 异常详情经 ExceptionDescribe 写入 stderr，随重定向进入捕获文件
            ((delegate* unmanaged<IntPtr, void>)Fn(env, EnvExceptionDescribe))(env);
            ((delegate* unmanaged<IntPtr, void>)Fn(env, EnvExceptionClear))(env);
            return new InvalidOperationException($"{step} 抛出 Java 异常（详情见捕获文件）");
        }
        return new InvalidOperationException($"{step} 返回 NULL 且无挂起异常");
    }
}
