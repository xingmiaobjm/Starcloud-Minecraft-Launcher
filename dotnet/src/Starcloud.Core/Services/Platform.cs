// 平台差异收敛点（跨平台迁移设计 Phase 1）：规则判定 / natives / Java / 数据根 / Shell
// 统一从这里取值；业务代码禁止散落写死 OS 字符串。Windows 分支与历史行为逐字节一致。

using System.Runtime.InteropServices;

namespace Starcloud.Core.Services;

/// <summary>运行平台信息：Minecraft 规则 os.name、natives classifier、Adoptium 参数、路径与 Shell 语义</summary>
public static class PlatformInfo
{
    public static bool IsWindows { get; } = OperatingSystem.IsWindows();
    public static bool IsMacOS { get; } = OperatingSystem.IsMacOS();
    public static bool IsLinux { get; } = OperatingSystem.IsLinux();

    /// <summary>本进程是否 arm64（Apple Silicon / Linux ARM → Adoptium aarch64 与 natives-arm64）</summary>
    public static bool IsArm64Process { get; } =
        RuntimeInformation.ProcessArchitecture == Architecture.Arm64;

    /// <summary>Minecraft 规则系统的 os.name：windows / osx / linux（Mojang 对 macOS 用 "osx"）</summary>
    public static string MojangOsName => IsWindows ? "windows" : IsMacOS ? "osx" : "linux";

    /// <summary>Adoptium API 的 os 参数（macOS 为 "mac"）</summary>
    public static string AdoptiumOs => IsWindows ? "windows" : IsMacOS ? "mac" : "linux";

    /// <summary>Adoptium API 的 architecture 参数</summary>
    public static string AdoptiumArch => IsArm64Process ? "aarch64" : "x64";

    /// <summary>启动器数据根：Windows %APPDATA%（与历史版本共用）；macOS ~/Library/Application Support；Linux XDG 规范的 data 目录</summary>
    public static string DefaultAppDataRoot
    {
        get
        {
            if (IsWindows)
                return Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            if (IsMacOS)
                return Path.Combine(UserHome, "Library", "Application Support");
            var xdg = Environment.GetEnvironmentVariable("XDG_DATA_HOME");
            if (!string.IsNullOrWhiteSpace(xdg) && Path.IsPathRooted(xdg)) return xdg;
            return Path.Combine(UserHome, ".local", "share");
        }
    }

    /// <summary>默认游戏目录 .minecraft：三平台均为官方启动器约定位置</summary>
    public static string DefaultDotMinecraft
    {
        get
        {
            if (IsWindows)
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".minecraft");
            if (IsMacOS)
                return Path.Combine(UserHome, "Library", "Application Support", "minecraft");
            return Path.Combine(UserHome, ".minecraft");
        }
    }

    private static string UserHome => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>natives jar 内的本机库后缀：Windows .dll；Linux .so；macOS .dylib（LWJGL2 时代还有 .jnilib）</summary>
    public static bool IsNativeArtifact(string entryName)
    {
        if (IsWindows) return entryName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);
        if (IsMacOS)
            return entryName.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase)
                   || entryName.EndsWith(".jnilib", StringComparison.OrdinalIgnoreCase);
        return entryName.EndsWith(".so", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// 当前平台的 natives classifier 候选（按优先级，取第一个存在的键）。
    /// 新版清单用 natives-macos[-arm64] / natives-linux[-arm64]，老版本清单用 natives-osx。
    /// </summary>
    public static IReadOnlyList<string> NativesClassifierCandidates
    {
        get
        {
            if (IsWindows) return ["natives-windows"];
            if (IsMacOS)
                return IsArm64Process
                    ? ["natives-macos-arm64", "natives-macos", "natives-osx"]
                    : ["natives-macos", "natives-osx"];
            return IsArm64Process
                ? ["natives-linux-arm64", "natives-linux"]
                : ["natives-linux"];
        }
    }

    /// <summary>Java 可执行文件名：Windows java.exe，unix java</summary>
    public static string JavaExeName => IsWindows ? "java.exe" : "java";

    /// <summary>Adoptium 下载归档扩展名：Windows .zip，unix .tar.gz</summary>
    public static string JavaArchiveExtension => IsWindows ? ".zip" : ".tar.gz";

    /// <summary>classpath 与 PATH 的分隔符（Windows ";" / unix ":"）</summary>
    public static string ClasspathSeparator => Path.PathSeparator.ToString();

    /// <summary>路径去重比较器：Windows 沿用历史的不区分大小写，unix 区分大小写（文件系统语义）</summary>
    public static StringComparer PathComparer => IsWindows
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    /// <summary>pre/post 启动命令的 Shell 程序：Windows cmd.exe，unix /bin/sh</summary>
    public static string ShellFileName => IsWindows ? "cmd.exe" : "/bin/sh";

    /// <summary>Shell 执行标志：cmd 用 /c，sh 用 -c</summary>
    public static string ShellExecFlag => IsWindows ? "/c" : "-c";
}
