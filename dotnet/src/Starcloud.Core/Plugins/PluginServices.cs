// 插件宿主服务包与宿主桥（plugin-spec.md §7）：把「启动器能做的事」一次性交给插件能力层。
//
// 设计要点：
// · 必需项 = 任何宿主都有的服务；可选项 = 该宿主没提供时对应 ctx 成员整体缺席，
//   宁可不给插件这个能力，也不给一个必然失败的接口。
// · 复杂动作（装版本 / 补全 / 装资源 / 备份 / 联机……）不在这里重写一遍，而是走与
//   Nova 共用的执行层（NovaToolRegistry）——两条入口必须行为完全一致，否则用户会遇到
//   「AI 能装好，插件装不好」这类只在某条路径复现的 bug。
// · 仅启动器 UI 才有的能力（对话框确认 / 开页 / 剪贴板 / 打开链接）走 IPluginHostBridge，
//   Core 单元测试里不实现桥 = 这些接口自动缺席。

using Starcloud.Core.Services;

namespace Starcloud.Core.Plugins;

/// <summary>
/// 只有 App（WinUI）宿主才有的能力。Core 侧只留接口，便于单元测试无 UI 运行。
/// 实现方必须自己把调用切回 UI 线程（插件钩子可能在任何线程上跑）。
/// </summary>
public interface IPluginHostBridge
{
    /// <summary>
    /// 弹窗让用户确认；返回 true = 用户点了确认，false = 取消，null = 当前宿主没有 UI。
    /// </summary>
    bool? Confirm(string title, string message);

    /// <summary>提示性弹窗（只有一个「知道了」按钮）</summary>
    bool? ShowInfo(string title, string message);

    /// <summary>切到启动器的某个页面（home / versions / download / accounts / settings / nova / multiplayer…）</summary>
    void OpenPage(string page);

    /// <summary>用系统默认浏览器打开链接，返回一句结果说明</summary>
    string OpenUrl(string url);

    /// <summary>在文件资源管理器里打开目录 / 定位文件，返回一句结果说明</summary>
    string OpenPath(string path);

    /// <summary>读剪贴板文本；空返回 null</summary>
    string? ClipboardText();

    /// <summary>写剪贴板文本</summary>
    void SetClipboardText(string text);
}

/// <summary>
/// 插件能力层的服务包。除 Settings 外全部可空：宿主没提供的服务 → 对应 ctx 成员缺席。
/// </summary>
public sealed record PluginHostServices
{
    /// <summary>当前设置快照（App 传 () =&gt; Settings）</summary>
    public required Func<LauncherSettings> Settings { get; init; }

    /// <summary>写设置后落盘（App 传 Settings.Save）</summary>
    public Action? SaveSettings { get; init; }

    /// <summary>已安装版本扫描（ctx.instances / ctx.versions）</summary>
    public ManifestService? Manifest { get; init; }

    /// <summary>版本安装与文件布局（ctx.instances 的完整性判断）</summary>
    public InstallerService? Installer { get; init; }

    /// <summary>多源下载引擎（ctx.download）</summary>
    public DownloadService? Download { get; init; }

    /// <summary>账户存储（ctx.accounts，只读身份）</summary>
    public AccountStore? Accounts { get; init; }

    /// <summary>启动账户解析（ctx.accounts.current）</summary>
    public LaunchAccountService? LaunchAccount { get; init; }

    /// <summary>任务中心（ctx.tasks）</summary>
    public TaskCenterService? Tasks { get; init; }

    /// <summary>与 Nova 共用的写操作执行层（复杂动作的执行体）</summary>
    public NovaToolRegistry? Tools { get; init; }

    /// <summary>App 侧能力（对话框 / 开页 / 剪贴板 / 打开链接）</summary>
    public IPluginHostBridge? Bridge { get; init; }
}
