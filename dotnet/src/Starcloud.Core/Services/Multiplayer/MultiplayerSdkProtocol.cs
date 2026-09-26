// 联机 SDK 协议描述：一份数据结构描述「怎么装核心 / 怎么起会话 / 怎么问状态」。
//
// 之所以做成描述而不是接口实现，是因为主流联机方案（陶瓦联机、以及各家第三方
// 联机 SDK）的本地控制面高度同形：**写一个端口文件 + 本地 HTTP 查状态 / 改状态**。
// 于是：
//   · 内置陶瓦联机 = 下面 <see cref="Terracotta"/> 这份常量描述；
//   · 第三方联机 SDK = 用户/厂商提供的一份 JSON 清单（字段同名），放进
//     `<数据目录>/multiplayer/providers/*.json` 即被识别，无需改启动器代码。
//
// 协议参考：Terracotta | 陶瓦联机 的 `--hmcl <portFile>` 模式与 HMCL 的集成实现。

namespace Starcloud.Core.Services.Multiplayer;

/// <summary>状态查询放不下时（核心还没有端口文件）抛出的异常，界面按「核心未就绪」处理</summary>
public sealed class MultiplayerCoreNotReadyException(string message) : Exception(message);

/// <summary>第三方联机 SDK 清单字段非法（缺少必填项等）</summary>
public sealed class MultiplayerSdkManifestException(string message) : Exception(message);

public sealed class MultiplayerSdkProtocol
{
    // ── 身份 ──────────────────────────────────────────────────────────────

    /// <summary>稳定标识（设置里持久化的是它；改了等于换了一个提供方）</summary>
    public string Id { get; init; } = "";

    public string DisplayName { get; init; } = "";

    /// <summary>署名字符串，界面底部「联机由 X 提供技术支持」用</summary>
    public string Vendor { get; init; } = "";

    public string? VendorUrl { get; init; }

    public string? Description { get; init; }

    // ── 安装 ──────────────────────────────────────────────────────────────

    /// <summary>版本号；写进核心目录的版本文件，也是 <c>{version}</c> 占位符的取值</summary>
    public string Version { get; init; } = "";

    /// <summary>平台包下载地址（按顺序尝试，支持 <c>{version}</c> / <c>{platform}</c> / <c>{arch}</c>）</summary>
    public List<string> DownloadUrls { get; init; } = [];

    /// <summary>压缩包类型：<c>tar.gz</c> / <c>zip</c> / <c>raw</c>（直接落一个可执行文件）</summary>
    public string ArchiveKind { get; init; } = "tar.gz";

    /// <summary>解压后统一重命名成的固定文件名（raw 时为下载落盘名）</summary>
    public string ExecutableName { get; init; } = "";

    /// <summary>版本标识文件名（写在核心目录里；内容等于 <see cref="Version"/> 才算已安装）</summary>
    public string VersionFile { get; init; } = "version.txt";

    /// <summary>
    /// 旧版（Tauri 架构）留下的核心目录模板（可空）。版本一致时直接复用，
    /// 从老版本升上来的用户不必为重装核心再下一次 8 MB。支持 <c>{data_dir}</c>。
    /// </summary>
    public string? LegacyCoreDir { get; init; }

    // ── 会话 ──────────────────────────────────────────────────────────────

    /// <summary>
    /// 启动参数模板，占位符：<c>{port_file}</c>（端口文件绝对路径）。
    /// 陶瓦联机即 <c>--hmcl {port_file}</c>：核心把本地 HTTP 端口写进该文件。
    /// </summary>
    public List<string> LaunchArguments { get; init; } = [];

    /// <summary>端口文件相对运行目录的路径</summary>
    public string PortFile { get; init; } = "http.json";

    /// <summary>端口文件里端口字段名（陶瓦联机为 <c>port</c>）</summary>
    public string PortJsonKey { get; init; } = "port";

    /// <summary>等端口文件的最长秒数</summary>
    public int StartTimeoutSeconds { get; init; } = 30;

    // ── 本地 HTTP 控制面 ─────────────────────────────────────────────────

    /// <summary>接口根地址模板，占位符 <c>{port}</c></summary>
    public string ApiBase { get; init; } = "http://127.0.0.1:{port}";

    public string StatePath { get; init; } = "/state";
    public string IdlePath { get; init; } = "/state/ide";
    public string HostPath { get; init; } = "/state/scanning";
    public string JoinPath { get; init; } = "/state/guesting";
    public string LogPath { get; init; } = "/log?fetch=true";

    /// <summary>关闭核心进程的接口（无则空串：停止联机只回到空闲，不结束进程）</summary>
    public string ShutdownPath { get; init; } = "/panic?peaceful=true";

    /// <summary>创建/加入房间时玩家名的查询参数名</summary>
    public string PlayerQueryKey { get; init; } = "player";

    /// <summary>加入房间时房间号的查询参数名</summary>
    public string RoomQueryKey { get; init; } = "room";

    /// <summary>公共节点查询参数名（空串 = 该 SDK 不接受外部节点）</summary>
    public string NodesQueryKey { get; init; } = "public_nodes";

    /// <summary>节点列表地址（返回 <c>[{"url":..,"region":..}]</c>；空串 = 不拉取）</summary>
    public string NodeListUrl { get; init; } = "";

    // ── 状态映射 ─────────────────────────────────────────────────────────

    public string StateField { get; init; } = "state";
    public string RoomField { get; init; } = "room";
    public string AddressField { get; init; } = "url";
    public string DifficultyField { get; init; } = "difficulty";
    public string PlayersField { get; init; } = "profiles";
    public string ErrorTypeField { get; init; } = "type";

    /// <summary>原始状态字符串 → 归一化阶段</summary>
    public Dictionary<string, MultiplayerPhase> PhaseMap { get; init; } = DefaultPhaseMap();

    /// <summary>异常类型 → 人类可读描述（中文原文即本地化键，界面走 Loc.T 翻译）</summary>
    public Dictionary<string, string> ErrorMap { get; init; } = [];

    /// <summary>官方核心的默认状态表（第三方 SDK 沿用同一套命名时可直接省略）</summary>
    public static Dictionary<string, MultiplayerPhase> DefaultPhaseMap() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["waiting"] = MultiplayerPhase.Idle,
        ["host-scanning"] = MultiplayerPhase.HostScanning,
        ["host-starting"] = MultiplayerPhase.HostStarting,
        ["host-ok"] = MultiplayerPhase.HostReady,
        ["guest-connecting"] = MultiplayerPhase.GuestConnecting,
        ["guest-starting"] = MultiplayerPhase.GuestEntering,
        ["guest-ok"] = MultiplayerPhase.GuestReady,
        ["exception"] = MultiplayerPhase.Failed,
    };

    /// <summary>陶瓦联机的默认异常表（对应核心 JSON 里 <c>type</c> 的整数值）</summary>
    public static Dictionary<string, string> DefaultErrorMap() => new(StringComparer.Ordinal)
    {
        ["0"] = "无法连接房主（P2P 打洞失败）",
        ["1"] = "与房主的连接被重置",
        ["2"] = "联机核心（客户端侧）异常退出",
        ["3"] = "联机核心（房主侧）异常退出",
        ["4"] = "与服务器的连接被重置",
        ["5"] = "联机服务端响应无效",
    };

    /// <summary>把模板里的占位符换成实际值（地址 / 版本 / 端口…）</summary>
    public string Expand(string template, int port = 0) => template
        .Replace("{version}", Version)
        .Replace("{platform}", Platforms.PlatformTag)
        .Replace("{arch}", Platforms.ArchTag)
        .Replace("{data_dir}", LauncherPaths.DataDir)
        .Replace("{port}", port.ToString());

    /// <summary>
    /// 内置的陶瓦联机描述（Terracotta | 陶瓦联机，AGPL-3.0-or-later，含官方「聚合例外条款」）。
    /// 启动器只下载并原样运行官方发布包、通过它自己的本地 HTTP 接口驱动，不修改、不重链接，
    /// 因此本启动器得以继续保持 MIT 许可。
    /// </summary>
    public static MultiplayerSdkProtocol Terracotta { get; } = new()
    {
        Id = "terracotta",
        DisplayName = "陶瓦联机",
        Vendor = "Terracotta | 陶瓦联机",
        VendorUrl = "https://github.com/burningtnt/Terracotta",
        Description = "基于 EasyTier 的 P2P 联机，无需公网 IP 或端口映射，为 Minecraft 优化。",
        Version = "0.4.2",
        // 国内镜像优先，官方 Releases 兜底（与旧版 Tauri 实现一致）
        DownloadUrls =
        [
            "https://gitee.com/burningtnt/Terracotta/releases/download/v{version}/terracotta-{version}-{platform}-pkg.tar.gz",
            "https://github.com/burningtnt/Terracotta/releases/download/v{version}/terracotta-{version}-{platform}-pkg.tar.gz",
        ],
        ArchiveKind = "tar.gz",
        ExecutableName = "terracotta.exe",
        LaunchArguments = ["--hmcl", "{port_file}"],
        NodeListUrl = "https://terracotta.glavo.site/nodes",
        LegacyCoreDir = "{data_dir}/terracotta/core",
        ErrorMap = DefaultErrorMap(),
    };

}

/// <summary>
/// 平台标识（平台包文件名里用的那套：<c>windows-x86_64</c> / <c>windows-arm64</c> …）。
/// 只覆盖官方发布包实际提供的组合，其余返回可读错误。
/// </summary>
public static class Platforms
{
    // 注意初始化顺序：PlatformTag 用到 ArchTag，属性初始化器是按声明顺序跑的，
    // 写成两个 get-only 自动属性会让 PlatformTag 拿到空字符串（"windows-"）。用 Lazy 隔离。
    private static readonly Lazy<string> ArchTagValue = new(ResolveArchTag);
    private static readonly Lazy<string> PlatformTagValue = new(ResolvePlatformTag);

    public static string ArchTag => ArchTagValue.Value;

    public static string PlatformTag => PlatformTagValue.Value;

    private static string ResolveArchTag() => System.Runtime.InteropServices.RuntimeInformation.OSArchitecture
        switch
        {
            System.Runtime.InteropServices.Architecture.X64 => "x86_64",
            System.Runtime.InteropServices.Architecture.Arm64 => "arm64",
            System.Runtime.InteropServices.Architecture.X86 => "i686",
            _ => "unknown",
        };

    private static string ResolvePlatformTag()
    {
        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "macos"
            : OperatingSystem.IsLinux() ? "linux"
            : "unknown";
        if (os == "unknown" || ArchTag == "unknown") return "unsupported";
        return $"{os}-{ArchTag}";
    }
}
