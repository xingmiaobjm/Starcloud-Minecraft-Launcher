// 插件上下文（规范 §3.2）：传给生命周期钩子的能力对象集。
// 能力按 manifest 权限装配——未声明对应权限的成员为 null（JS 侧 undefined），物理上不可调用。

namespace Starcloud.Core.Plugins;

/// <summary>启动器信息（只读暴露给插件）</summary>
public sealed class PluginLauncherInfo
{
    public string Version { get; init; } = "";
    public string ApiVersion { get; init; } = "";
    public bool OfficialBuild { get; init; }
}

/// <summary>
/// 受限文件访问（launcher:file_access）：root 锁定在插件自身 data 目录，路径穿越直接拒绝（规范 §4 沙箱）。
/// </summary>
public sealed class PluginFiles
{
    private readonly string _root;
    private readonly PluginLogger _log;

    public PluginFiles(string pluginDataDir, PluginLogger log)
    {
        _root = Path.GetFullPath(pluginDataDir);
        _log = log;
    }

    private string SafePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("文件名不能为空");
        var full = Path.GetFullPath(Path.Combine(_root, name));
        if (!full.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"路径越界：插件只能访问自身 data 目录（{name}）");
        return full;
    }

    /// <summary>读文本；文件不存在返回 null</summary>
    public string? Read(string name)
    {
        var p = SafePath(name);
        return File.Exists(p) ? File.ReadAllText(p) : null;
    }

    /// <summary>写文本（自动建父目录）</summary>
    public void Write(string name, string content)
    {
        var p = SafePath(name);
        var dir = Path.GetDirectoryName(p);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(p, content);
    }

    public bool Exists(string name) => File.Exists(SafePath(name));

    /// <summary>列出 data 目录下文件名（不含子目录递归）</summary>
    public string[] List()
    {
        Directory.CreateDirectory(_root);
        return Directory.GetFiles(_root).Select(Path.GetFileName).OfType<string>().ToArray();
    }
}

/// <summary>UI 扩展 API（按权限装配）</summary>
public sealed class PluginUiApi
{
    private readonly string _pluginId;
    private readonly PluginHost _host;
    private readonly PluginLogger _log;
    private readonly bool _canNotify;

    internal PluginUiApi(string pluginId, PluginHost host, PluginLogger log, bool canNotify)
    {
        _pluginId = pluginId;
        _host = host;
        _log = log;
        _canNotify = canNotify;
    }

    /// <summary>
    /// 注册面板（launcher:ui_panel）：ctx.ui.registerPanel("main", "我的面板", "assets/panel.html")。
    /// entry 为插件目录内相对路径（HTML）。重复 id 覆盖。
    /// </summary>
    public void RegisterPanel(string panelId, string title, string entry)
    {
        if (string.IsNullOrWhiteSpace(panelId) || string.IsNullOrWhiteSpace(title) ||
            string.IsNullOrWhiteSpace(entry))
            throw new ArgumentException("registerPanel 参数不完整：panelId / title / entry 必填");
        _host.RegisterPanel(_pluginId, panelId, title, entry);
    }

    /// <summary>
    /// 注册页面挂载区（launcher:ui_panel）：ctx.ui.registerSlot("download.top", "assets/banner.html", 120)。
    /// slotId 为宿主支持的挂载点（当前：download.top = 下载页顶部），entry 为插件目录内相对路径（HTML），
    /// height 为像素高度（48-320）。重复 slotId 覆盖。
    /// </summary>
    public void RegisterSlot(string slotId, string entry, int height)
    {
        if (string.IsNullOrWhiteSpace(slotId) || string.IsNullOrWhiteSpace(entry))
            throw new ArgumentException("registerSlot 参数不完整：slotId / entry 必填");
        _host.RegisterSlot(_pluginId, slotId, entry, height);
    }

    /// <summary>弹窗通知（launcher:notification）：ctx.ui.notify("标题", "内容")</summary>
    public void Notify(string title, string message)
    {
        if (!_canNotify)
            throw new UnauthorizedAccessException("未声明 launcher:notification 权限，无法发送通知");
        _host.RaiseNotify(_pluginId, title ?? "", message ?? "");
    }
}

/// <summary>传给插件钩子的上下文对象</summary>
public sealed class PluginContext
{
    /// <summary>manifest 声明的权限（Python 运行时握手用）</summary>
    public string[] Permissions { get; init; } = [];

    /// <summary>启动器信息</summary>
    public PluginLauncherInfo Launcher { get; init; } = new();

    /// <summary>
    /// 启动参数管理器（launcher:game_arguments；仅 beforeGameLaunch 钩子提供）。
    /// 只有 append / removeKey / appendJvm，无全量覆写。
    /// </summary>
    public GameArgsManager? Args { get; init; }

    /// <summary>受限文件访问（launcher:file_access；root = 插件 data 目录）</summary>
    public PluginFiles? Files { get; init; }

    /// <summary>UI 扩展（launcher:ui_panel / launcher:notification）</summary>
    public PluginUiApi? Ui { get; init; }

    /// <summary>日志输出：ctx.logger.info / warn / error</summary>
    public PluginLogger Logger { get; init; } = null!;
}
