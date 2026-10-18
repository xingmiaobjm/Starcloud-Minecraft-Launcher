// 内核 · 插件模型：描述清单 + 运行态。
//
// 内置插件与外部插件用**同一份描述形状**（字段对齐 docs/plugin-spec-v2.md §3），
// 区别只有"在哪跑、有多少权限"。这样插件页能一张表列出全部插件，
// 设置里也能把"换个实现"做成一件普通的事。

namespace Starcloud.Core.Kernel;

/// <summary>插件种类：决定激活次序（引擎 → 功能 → 助手 → UI）</summary>
public enum PluginKind
{
    /// <summary>引擎：被别的东西依赖的底层能力（下载引擎、投影引擎、LLM 网关、链路）</summary>
    Engine = 0,

    /// <summary>功能：面向用户的一整块功能（版本管理、存档库、账户、Java）</summary>
    Feature = 1,

    /// <summary>助手：Nova 对话、Nova 执行器、各个工具、指令助手</summary>
    Assistant = 2,

    /// <summary>界面：只贡献 UI（页面、卡片、分区），不提供契约</summary>
    Ui = 3,
}

/// <summary>激活时机</summary>
public enum PluginActivation
{
    /// <summary>内核启动时就激活</summary>
    Eager,

    /// <summary>第一次被用到（有人取它提供的契约 / 点开它的页面）才激活</summary>
    Lazy,
}

public enum PluginState
{
    /// <summary>已登记，未激活</summary>
    Registered,

    /// <summary>激活中</summary>
    Activating,

    /// <summary>已激活</summary>
    Active,

    /// <summary>激活失败（依赖缺失 / 初始化抛异常），不可用</summary>
    Failed,

    /// <summary>被用户或配置禁用</summary>
    Disabled,

    /// <summary>已停用（Shutdown 已跑完）</summary>
    Stopped,
}

/// <summary>插件描述清单（内置插件在代码里写死，外部插件来自 manifest.json）</summary>
public sealed record PluginDescriptor
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public string Version { get; init; } = "1.0.0";
    public string ApiVersion { get; init; } = KernelVersion.ApiVersion;
    public string? Description { get; init; }
    public string? Author { get; init; }
    public PluginKind Kind { get; init; } = PluginKind.Feature;
    public PluginActivation Activation { get; init; } = PluginActivation.Eager;

    /// <summary>内置插件（编译期登记、进程内、完全信任）</summary>
    public bool Builtin { get; init; } = true;

    /// <summary>本插件对外提供的契约类型（懒激活靠它找"谁能提供"）</summary>
    public Type[] Provides { get; init; } = [];

    /// <summary>依赖的插件 id → 版本范围（P0 只校验"在不在、能不能激活"）</summary>
    public IReadOnlyDictionary<string, string> Depends { get; init; } = new Dictionary<string, string>();

    /// <summary>声明需要的权限（外部插件由沙箱裁决；内置插件按全量信任但仍登记，便于审计）</summary>
    public string[] Permissions { get; init; } = [];
}

/// <summary>插件运行态（插件页展示 / 诊断用）</summary>
public sealed record PluginRuntime(PluginDescriptor Descriptor)
{
    public PluginState State { get; internal set; } = PluginState.Registered;
    public string? Error { get; internal set; }
    public double ActivationMs { get; internal set; }
}

/// <summary>内核版本与插件 API 版本</summary>
public static class KernelVersion
{
    /// <summary>插件 API 版本：契约有不兼容变更时递增（对齐 plugin-spec-v2 的 apiVersion）</summary>
    public const string ApiVersion = "3.0";

    /// <summary>内核自身版本</summary>
    public const string Kernel = "1.0.0";
}
