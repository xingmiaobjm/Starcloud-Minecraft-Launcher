// libc.so 直调：stdout/stderr 重定向、工作目录、环境变量与目录创建。
// Android 应用进程默认 stdout 进 /dev/null，JVM 输出必须 dup2 到文件才能回收诊断。

using System.Runtime.InteropServices;

namespace Starcloud.Android.Host.Interop;

internal static class Libc
{
    internal const int O_WRONLY = 0x1;
    internal const int O_CREAT = 0x40;
    internal const int O_TRUNC = 0x200;

    [DllImport("libc.so", SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern int open(string path, int flags, uint mode);

    [DllImport("libc.so")]
    internal static extern int dup2(int oldFd, int newFd);

    [DllImport("libc.so")]
    internal static extern int close(int fd);

    [DllImport("libc.so", CharSet = CharSet.Ansi)]
    internal static extern int chdir(string path);

    [DllImport("libc.so", CharSet = CharSet.Ansi)]
    internal static extern int setenv(string name, string value, int overwrite);

    // 诊断用：回读本进程 bionic environ 的实际内容（验证 setenv 生效）
    [DllImport("libc.so", CharSet = CharSet.Ansi)]
    internal static extern IntPtr getenv(string name);

    [DllImport("libc.so", SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern int mkdir(string path, uint mode);

    [DllImport("libc.so", SetLastError = true, CharSet = CharSet.Ansi)]
    internal static extern IntPtr dlopen(string path, int flags);

    // bionic dlopen 标志（与 glibc 同值）：NOW=2，GLOBAL=0x100
    internal const int RTLD_NOW = 2;
    internal const int RTLD_GLOBAL = 0x100;
}
