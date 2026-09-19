// 插件宿主（Core 编排层）：插件生命周期、钩子分发、UI 注册表、通知事件。
// 合规不变量（规范 §4）：本体不暴露任何账号身份校验钩子；
// beforeGameLaunch 仅透出 GameArgsManager 增量参数（append/removeKey，无覆写）。

using Starcloud.Core.Services;

namespace Starcloud.Core.Plugins;

/// <summary>游戏启动前钩子桥（GameLauncher → PluginHost；App 层把 PluginHost 实例透传进 LaunchOptions）</summary>
public interface IPluginLaunchHook
{
    /// <summary>收集所有已启用插件追加的启动参数增量（身份参数已在管理器内拦截）</summary>
    (IReadOnlyList<string> Jvm, IReadOnlyList<string> Game) CollectExtraArgs(
        string versionId, string playerName);
}

public sealed class PluginHost : IPluginLaunchHook, IDisposable
{
    public const string HookBeforeGameLaunch = "beforeGameLaunch";

    /// <summary>插件注册的 UI 面板（launcher:ui_panel）</summary>
    public sealed record PanelRegistration(
        string PluginId, string PanelId, string Title, string EntryPath);

    /// <summary>插件注册的页面挂载区（launcher:ui_panel）：宿主在固定 slot 位置嵌入插件 HTML</summary>
    public sealed record SlotRegistration(
        string PluginId, string SlotId, string EntryPath, int Height);

    /// <summary>插件启动失败信息</summary>
    public sealed record PluginStartError(string PluginId, string Message);

    private sealed record Loaded(
        PluginManifest Manifest, string Dir, IPluginRuntime Runtime, PluginLogger Logger);

    private readonly object _gate = new();
    private readonly List<Loaded> _loaded = [];
    private readonly List<PanelRegistration> _panels = [];
    private readonly List<SlotRegistration> _slots = [];
    private readonly string _logDir;
    private readonly string _hostSdkPath;

    /// <summary>启动器自身版本（minLauncherVersion 校验与 ctx 透出）</summary>
    public string LauncherVersion { get; }

    public PluginHost(string launcherVersion, string pluginLogDir, string hostSdkPath)
    {
        LauncherVersion = launcherVersion;
        _logDir = pluginLogDir;
        _hostSdkPath = hostSdkPath;
    }

    // ── UI 注册表与通知（App 层订阅） ──

    public IReadOnlyList<PanelRegistration> Panels
    {
        get { lock (_gate) return [.. _panels]; }
    }

    /// <summary>(pluginId, panelId) 移除面板</summary>
    public event Action<PanelRegistration>? PanelRegistered;
    public event Action<string, string>? PanelUnregistered;
    /// <summary>(pluginId, title, message)</summary>
    public event Action<string, string, string>? NotifyRequested;

    internal void RegisterPanel(string pluginId, string panelId, string title, string entry)
    {
        // entry 限制为插件解压目录内相对路径，拒绝绝对路径与穿越
        if (Path.IsPathRooted(entry) || entry.Split('/', '\\').Contains(".."))
            throw new UnauthorizedAccessException("面板入口必须是插件目录内的相对路径");
        var dir = GetPluginDir(pluginId)
            ?? throw new InvalidOperationException("插件尚未加载，无法注册面板");
        var reg = new PanelRegistration(pluginId, panelId, title,
            Path.Combine(dir, entry.Replace('/', Path.DirectorySeparatorChar)));
        lock (_gate)
        {
            _panels.RemoveAll(p => p.PluginId == pluginId && p.PanelId == panelId);
            _panels.Add(reg);
        }
        PanelRegistered?.Invoke(reg);
        GetLogger(pluginId)?.Info($"已注册面板：{panelId}（{title}）");
    }

    internal void RegisterSlot(string pluginId, string slotId, string entry, int height)
    {
        // 与面板相同：entry 限制为插件解压目录内相对路径，拒绝绝对路径与穿越
        if (Path.IsPathRooted(entry) || entry.Split('/', '\\').Contains(".."))
            throw new UnauthorizedAccessException("挂载区入口必须是插件目录内的相对路径");
        var dir = GetPluginDir(pluginId)
            ?? throw new InvalidOperationException("插件尚未加载，无法注册挂载区");
        var reg = new SlotRegistration(pluginId, slotId,
            Path.Combine(dir, entry.Replace('/', Path.DirectorySeparatorChar)),
            Math.Clamp(height, 48, 320));
        lock (_gate)
        {
            _slots.RemoveAll(s => s.PluginId == pluginId && s.SlotId == slotId);
            _slots.Add(reg);
        }
        GetLogger(pluginId)?.Info($"已注册挂载区：{slotId}（{entry}）");
    }

    /// <summary>查询某挂载点上所有插件注册的内容（页面加载时调用）</summary>
    public IReadOnlyList<SlotRegistration> GetSlots(string slotId)
    {
        lock (_gate) return [.. _slots.Where(s => s.SlotId == slotId)];
    }

    internal void RaiseNotify(string pluginId, string title, string message)
    {
        GetLogger(pluginId)?.Info($"通知：{title} - {message}");
        NotifyRequested?.Invoke(pluginId, title, message);
    }

    // ── 生命周期 ──

    /// <summary>启动（加载）插件：选择运行时、执行 onLoad；失败返回错误</summary>
    public PluginStartError? Start(PluginManifest manifest, string dir)
    {
        Stop(manifest.Id);
        var logger = new PluginLogger(manifest.Id, _logDir);
        IPluginRuntime runtime;
        try
        {
            runtime = CreateRuntime(dir);
        }
        catch (Exception ex)
        {
            return new PluginStartError(manifest.Id, ex.Message);
        }
        try
        {
            // 先入册再 Load：onLoad 内 registerPanel/Notify 需要解析插件目录
            lock (_gate) _loaded.Add(new Loaded(manifest, dir, runtime, logger));
            var err = runtime.Load(Path.Combine(dir, runtime.EntryName),
                BuildContext(manifest, dir, logger, args: null));
            if (err is not null)
            {
                Stop(manifest.Id); // 回滚登记，并清理 onLoad 阶段可能已注册的面板
                return new PluginStartError(manifest.Id, err);
            }
            logger.Info($"已加载（v{manifest.Version}，权限 {string.Join(", ", manifest.Permissions)}）");
            return null;
        }
        catch (Exception ex)
        {
            Stop(manifest.Id);
            return new PluginStartError(manifest.Id, $"onLoad 执行失败：{ex.Message}");
        }
    }

    /// <summary>停止插件：回调 onUnload、清理面板、释放运行时</summary>
    public void Stop(string pluginId)
    {
        Loaded? plugin;
        lock (_gate)
        {
            plugin = _loaded.FirstOrDefault(p => p.Manifest.Id == pluginId);
            if (plugin is null) return;
            _loaded.Remove(plugin);
            var removed = _panels.Where(p => p.PluginId == pluginId).ToList();
            _panels.RemoveAll(p => p.PluginId == pluginId);
            _slots.RemoveAll(s => s.PluginId == pluginId);
            foreach (var r in removed) PanelUnregistered?.Invoke(pluginId, r.PanelId);
        }
        try
        {
            plugin.Runtime.InvokeHook("onUnload", BuildContext(plugin.Manifest, plugin.Dir,
                plugin.Logger, args: null));
        }
        catch (Exception ex)
        {
            plugin.Logger.Warn($"onUnload 执行失败：{ex.Message}");
        }
        plugin.Runtime.Dispose();
        plugin.Logger.Info("已卸载");
    }

    public void StopAll()
    {
        string[] ids;
        lock (_gate) ids = [.. _loaded.Select(p => p.Manifest.Id)];
        foreach (var id in ids) Stop(id);
    }

    /// <summary>插件当前是否已加载</summary>
    public bool IsLoaded(string pluginId)
    {
        lock (_gate) return _loaded.Any(p => p.Manifest.Id == pluginId);
    }

    // ── 启动前钩子（IPluginLaunchHook） ──

    public (IReadOnlyList<string> Jvm, IReadOnlyList<string> Game) CollectExtraArgs(
        string versionId, string playerName)
    {
        var argsManager = new GameArgsManager(new PluginLogger("launch", _logDir));
        Loaded[] snapshot;
        lock (_gate)
            snapshot = [.. _loaded.Where(p =>
                p.Manifest.Permissions.Contains(PluginPermissions.GameArguments))];
        foreach (var plugin in snapshot)
        {
            try
            {
                plugin.Runtime.InvokeHook(HookBeforeGameLaunch,
                    BuildContext(plugin.Manifest, plugin.Dir, plugin.Logger, argsManager));
            }
            catch (Exception ex)
            {
                plugin.Logger.Error($"beforeGameLaunch 执行失败（已跳过该插件）：{ex.Message}");
            }
        }
        return (argsManager.Jvm, argsManager.Game);
    }

    // ── 内部 ──

    private IPluginRuntime CreateRuntime(string dir)
    {
        if (File.Exists(Path.Combine(dir, "main.js")))
            return new JsPluginRuntime();
        if (File.Exists(Path.Combine(dir, "main.py")))
            return new PyPluginRuntime(dir, _hostSdkPath);
        throw new InvalidOperationException("插件缺少入口脚本（main.js / main.py）");
    }

    private PluginContext BuildContext(PluginManifest manifest, string dir,
        PluginLogger logger, GameArgsManager? args)
    {
        var perms = manifest.Permissions;
        return new PluginContext
        {
            Permissions = perms,
            Launcher = new PluginLauncherInfo
            {
                Version = LauncherVersion,
                ApiVersion = PluginEnvironment.ApiVersion,
                OfficialBuild = PluginEnvironment.OfficialChinaBuild,
            },
            Args = perms.Contains(PluginPermissions.GameArguments) ? args : null,
            Files = perms.Contains(PluginPermissions.FileAccess)
                ? new PluginFiles(Path.Combine(dir, "data"), logger)
                : null,
            Ui = perms.Contains(PluginPermissions.UiPanel) || perms.Contains(PluginPermissions.Notification)
                ? new PluginUiApi(manifest.Id, this, logger,
                    canNotify: perms.Contains(PluginPermissions.Notification))
                : null,
            Logger = logger,
        };
    }

    private string? GetPluginDir(string pluginId)
    {
        lock (_gate) return _loaded.FirstOrDefault(p => p.Manifest.Id == pluginId)?.Dir;
    }

    private PluginLogger? GetLogger(string pluginId)
    {
        lock (_gate) return _loaded.FirstOrDefault(p => p.Manifest.Id == pluginId)?.Logger;
    }

    public void Dispose() => StopAll();
}
