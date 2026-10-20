// LaunchSpec：进程内 JVM 启动所需的全部运行时要素（与桌面命令行字符串解耦），
// 由 M3 的 InProcessJvmLauncher 直接消费。

namespace Starcloud.Android.Core.Launch;

public sealed record LaunchSpec(
    string MainClass,
    IReadOnlyList<string> JvmArgs,
    IReadOnlyList<string> Classpath,
    IReadOnlyList<string> ProgramArgs,
    IReadOnlyList<string> NativeDirs,
    IReadOnlyDictionary<string, string> Env,
    int JreMajor,
    string WorkingDir,
    RenderBackend Backend);
