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

    // ── 扩展能力（v1.2 起；未声明对应权限时成员为 null / undefined） ──

    /// <summary>官方版本清单（launcher:instances_read）</summary>
    public PluginVersionsApi? Versions { get; init; }

    /// <summary>已安装实例与维护（launcher:instances_read / launcher:instances_write）</summary>
    public PluginInstancesApi? Instances { get; init; }

    /// <summary>游戏进程（launcher:game_launch）：启动 / 状态 / 结束</summary>
    public PluginGameApi? Game { get; init; }

    /// <summary>社区资源（launcher:content_read / launcher:content_write）</summary>
    public PluginContentApi? Content { get; init; }

    /// <summary>下载到插件 data 目录（launcher:download）</summary>
    public PluginDownloadApi? Download { get; init; }

    /// <summary>插件自己的 HTTP 请求（launcher:network）</summary>
    public PluginHttpApi? Http { get; init; }

    /// <summary>启动器全局设置（launcher:settings_read / launcher:settings_write）</summary>
    public PluginSettingsApi? Settings { get; init; }

    /// <summary>账户身份只读列表（launcher:accounts_read；永不暴露令牌）</summary>
    public PluginAccountsApi? Accounts { get; init; }

    /// <summary>启动器 / 游戏 / 插件日志（launcher:logs_read）</summary>
    public PluginLogsApi? Logs { get; init; }

    /// <summary>任务中心（launcher:tasks）</summary>
    public PluginTasksApi? Tasks { get; init; }

    /// <summary>存档与备份（launcher:saves_read / launcher:saves_write）</summary>
    public PluginSavesApi? Saves { get; init; }

    /// <summary>联机（launcher:multiplayer）</summary>
    public PluginMultiplayerApi? Multiplayer { get; init; }

    /// <summary>外观（launcher:appearance）</summary>
    public PluginAppearanceApi? Appearance { get; init; }

    /// <summary>启动器更新查询（launcher:updates）</summary>
    public PluginUpdatesApi? Updates { get; init; }

    /// <summary>插件私有键值存储（launcher:storage）</summary>
    public PluginStorageApi? Storage { get; init; }

    /// <summary>剪贴板 / 打开链接与目录 / 确认框 / 开页（launcher:clipboard / shell / ui_panel）</summary>
    public PluginSystemApi? System { get; init; }

    /// <summary>事件订阅（launcher:events）</summary>
    public PluginEventsApi? Events { get; init; }

    /// <summary>本次事件载荷（仅 onLauncherEvent 钩子非 null）</summary>
    public PluginEventContext? Event { get; init; }
}
