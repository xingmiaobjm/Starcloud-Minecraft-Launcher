// JNI 函数表封装（设计稿 §6，spike S1 实证形态）：
// 所有 HotSpot JNI 调用经 env / JavaVM 函数表按规范索引取函数指针完成，不依赖绑定生成。
// 索引依据 JNI 规范表：JavaVM 表 [0-2] 保留共 3 项，[3] 起 DestroyJavaVM；
// JNIEnv 表保留 4 项，[4] 起 GetVersion。

using System.Runtime.InteropServices;

namespace Starcloud.Android.Host.Interop;

internal static unsafe class Jni
{
    // JNI_VERSION_1_8（HotSpot 支持范围内请求；GetVersion 实际返回 VM 支持的最高版本）
    internal const int JniVersion18 = 0x00010008;

    // JavaVM 函数表索引（JNIInvokeInterface_）
    internal const int VmDestroyJavaVm = 3;
    internal const int VmAttachCurrentThread = 4;
    internal const int VmDetachCurrentThread = 5;
    internal const int VmGetEnv = 6;

    // JNIEnv 函数表索引（JNINativeInterface_）
    internal const int EnvGetVersion = 4;
    internal const int EnvFindClass = 6;
    internal const int EnvExceptionOccurred = 15;
    internal const int EnvExceptionDescribe = 16;
    internal const int EnvExceptionClear = 17;
    internal const int EnvNewGlobalRef = 21;
    internal const int EnvDeleteLocalRef = 26;
    internal const int EnvGetStaticMethodId = 113;
    internal const int EnvCallStaticObjectMethod = 114;
    internal const int EnvCallStaticVoidMethod = 141;
    internal const int EnvNewStringUtf = 167;
    internal const int EnvNewObjectArray = 172;
    internal const int EnvSetObjectArrayElement = 174;

    [StructLayout(LayoutKind.Sequential)]
    internal struct JavaVMOption
    {
        public IntPtr OptionString; // char*
        public IntPtr ExtraInfo;    // void*
    }

    // arm64 布局：version@0、nOptions@4、options@8（指针 8 字节对齐）、ignoreUnrecognized@16，总大小 24
    [StructLayout(LayoutKind.Sequential)]
    internal struct JavaVMInitArgs
    {
        public int Version;
        public int NOptions;
        public IntPtr Options;
        public byte IgnoreUnrecognized;
    }

    /// <summary>取函数表第 index 项：table = *(void***)env。</summary>
    internal static IntPtr Fn(IntPtr table, int index)
    {
        void** funcs = *(void***)table;
        return (IntPtr)funcs[index];
    }

    internal static void Check(int rc, string step)
    {
        if (rc != 0)
        {
            throw new InvalidOperationException($"{step} 失败 rc={rc}");
        }
    }

    internal static void CheckRef(IntPtr env, IntPtr handle, string step)
    {
        if (handle != IntPtr.Zero)
        {
            return;
        }
        throw PendingOr(env, step);
    }

    internal static void CheckPendingException(IntPtr env, string step)
    {
        var occurred = (delegate* unmanaged<IntPtr, IntPtr>)Fn(env, EnvExceptionOccurred);
        if (occurred(env) == IntPtr.Zero)
        {
            return;
        }
        throw PendingOr(env, step);
    }

    internal static Exception PendingOr(IntPtr env, string step)
    {
        var occurred = (delegate* unmanaged<IntPtr, IntPtr>)Fn(env, EnvExceptionOccurred);
        if (occurred(env) != IntPtr.Zero)
        {
            // 异常详情经 ExceptionDescribe 写入 stderr（已重定向到 jvm-stdout.log）
            ((delegate* unmanaged<IntPtr, void>)Fn(env, EnvExceptionDescribe))(env);
            ((delegate* unmanaged<IntPtr, void>)Fn(env, EnvExceptionClear))(env);
            return new InvalidOperationException($"{step} 抛出 Java 异常（详情见 jvm-stdout.log）");
        }
        return new InvalidOperationException($"{step} 返回 NULL 且无挂起异常");
    }
}
