// 插件宿主（Core 编排层）：插件生命周期、钩子分发、UI 注册表、通知事件。
// 合规不变量（规范 §4）：本体不暴露任何账号身份校验钩子；
// beforeGameLaunch 仅透出 GameArgsManager 增量参数（append/removeKey，无覆写）。

using Starcloud.Core.Services;
using System.Reflection;

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

    /// <summary>启动器事件钩子：宿主向插件投递事件时调用（ctx.events / ctx.event）</summary>
    public const string HookLauncherEvent = "onLauncherEvent";

    /// <summary>宿主可投递的事件名（规范 §7.15）</summary>
    public static readonly string[] KnownEvents =
        ["game.launched", "game.exited", "launcher.ready"];

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
    private readonly PluginHostServices? _services;

    /// <summary>插件 id → 已订阅的事件名</summary>
    private readonly Dictionary<string, HashSet<string>> _subscriptions = [];

    /// <summary>插件 id → 当前正在执行的钩子上下文（Python 侧 host_call 回调用它）</summary>
    private readonly Dictionary<string, PluginContext> _activeContexts = [];

    /// <summary>钩子调用闸门：事件可能从任意线程投递，Jint 引擎不可并发使用</summary>
    private readonly object _hookGate = new();

    /// <summary>启动器自身版本（minLauncherVersion 校验与 ctx 透出）</summary>
    public string LauncherVersion { get; }

    /// <summary>当前已加载（启用）的插件数</summary>
    public int LoadedCount
    {
        get { lock (_gate) return _loaded.Count; }
    }

    public PluginHost(string launcherVersion, string pluginLogDir, string hostSdkPath,
        PluginHostServices? services = null)
    {
        LauncherVersion = launcherVersion;
        _logDir = pluginLogDir;
        _hostSdkPath = hostSdkPath;
        _services = services;
    }

    /// <summary>宿主服务包（null = 没有扩展能力，ctx 上除基础成员外全部缺席）</summary>
    public PluginHostServices? Services => _services;

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
            runtime = CreateRuntime(manifest.Id, dir);
        }
        catch (Exception ex)
        {
            return new PluginStartError(manifest.Id, ex.Message);
        }
        try
        {
            // 先入册再 Load：onLoad 内 registerPanel/Notify 需要解析插件目录
            lock (_gate) _loaded.Add(new Loaded(manifest, dir, runtime, logger));
            // onLoad 期间也要能走 host_call（Python 侧 ctx 能力）：把 ctx 登记进去
            var ctx = BuildContext(manifest, dir, logger, args: null);
            lock (_gate) _activeContexts[manifest.Id] = ctx;
            string? err;
            try
            {
                err = runtime.Load(Path.Combine(dir, runtime.EntryName), ctx);
            }
            finally
            {
                lock (_gate) _activeContexts.Remove(manifest.Id);
            }
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
            _subscriptions.Remove(pluginId);
            var removed = _panels.Where(p => p.PluginId == pluginId).ToList();
            _panels.RemoveAll(p => p.PluginId == pluginId);
            _slots.RemoveAll(s => s.PluginId == pluginId);
            foreach (var r in removed) PanelUnregistered?.Invoke(pluginId, r.PanelId);
        }
        try
        {
            InvokeHookGuarded(plugin, BuildContext(plugin.Manifest, plugin.Dir,
                plugin.Logger, args: null), "onUnload");
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
                InvokeHookGuarded(plugin, BuildContext(plugin.Manifest, plugin.Dir,
                    plugin.Logger, argsManager), HookBeforeGameLaunch);
            }
            catch (Exception ex)
            {
                plugin.Logger.Error($"beforeGameLaunch 执行失败（已跳过该插件）：{ex.Message}");
            }
        }
        return (argsManager.Jvm, argsManager.Game);
    }

    // ── 内部 ──

    private IPluginRuntime CreateRuntime(string pluginId, string dir)
    {
        if (File.Exists(Path.Combine(dir, "main.js")))
            return new JsPluginRuntime();
        if (File.Exists(Path.Combine(dir, "main.py")))
            return new PyPluginRuntime(dir, _hostSdkPath, pluginId, HostCall);
        throw new InvalidOperationException("插件缺少入口脚本（main.js / main.py）");
    }

    private PluginContext BuildContext(PluginManifest manifest, string dir,
        PluginLogger logger, GameArgsManager? args, PluginEventContext? evt = null)
    {
        var perms = manifest.Permissions;
        var api = _services is null ? null : new PluginApiCore(
            _services, manifest.Id, Path.Combine(dir, "data"), logger, perms, this, evt);
        T? WhenAny<T>(Func<PluginApiCore, T> factory, params string[] anyOf) where T : class =>
            api is not null && anyOf.Any(perms.Contains) ? factory(api) : null;
        T? When<T>(string permission, Func<PluginApiCore, T> factory) where T : class =>
            WhenAny(factory, permission);

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

            // ── 扩展能力：按权限装配（未声明 = null = JS 侧 undefined） ──
            Versions = When(PluginPermissions.InstancesRead, a => new PluginVersionsApi(a)),
            Instances = WhenAny(a => new PluginInstancesApi(a),
                PluginPermissions.InstancesRead, PluginPermissions.InstancesWrite),
            Game = When(PluginPermissions.GameLaunch, a => new PluginGameApi(a)),
            Content = WhenAny(a => new PluginContentApi(a),
                PluginPermissions.ContentRead, PluginPermissions.ContentWrite),
            Download = When(PluginPermissions.Download, a => new PluginDownloadApi(a)),
            Http = When(PluginPermissions.Network, a => new PluginHttpApi(a)),
            Settings = WhenAny(a => new PluginSettingsApi(a),
                PluginPermissions.SettingsRead, PluginPermissions.SettingsWrite),
            Accounts = When(PluginPermissions.AccountsRead, a => new PluginAccountsApi(a)),
            Logs = When(PluginPermissions.LogsRead, a => new PluginLogsApi(a)),
            Tasks = When(PluginPermissions.Tasks, a => new PluginTasksApi(a)),
            Saves = WhenAny(a => new PluginSavesApi(a),
                PluginPermissions.SavesRead, PluginPermissions.SavesWrite),
            Multiplayer = When(PluginPermissions.Multiplayer, a => new PluginMultiplayerApi(a)),
            Appearance = When(PluginPermissions.Appearance, a => new PluginAppearanceApi(a)),
            Updates = When(PluginPermissions.Updates, a => new PluginUpdatesApi(a)),
            Storage = When(PluginPermissions.Storage, a => new PluginStorageApi(a)),
            System = WhenAny(a => new PluginSystemApi(a),
                PluginPermissions.Clipboard, PluginPermissions.Shell, PluginPermissions.UiPanel),
            Events = When(PluginPermissions.Events, a => new PluginEventsApi(a)),
            Event = evt,
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

    // ── 事件（launcher:events） ──

    /// <summary>订阅事件（插件侧 ctx.events.on 调用；重复订阅幂等）</summary>
    internal void SubscribeEvent(string pluginId, string eventName)
    {
        if (!KnownEvents.Contains(eventName))
            throw new ArgumentException(
                $"未知事件 {eventName}（可用：{string.Join(", ", KnownEvents)}）");
        lock (_gate)
        {
            if (!_subscriptions.TryGetValue(pluginId, out var set))
                _subscriptions[pluginId] = set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            set.Add(eventName);
        }
    }

    internal void UnsubscribeEvent(string pluginId, string eventName)
    {
        lock (_gate)
        {
            if (_subscriptions.TryGetValue(pluginId, out var set)) set.Remove(eventName);
        }
    }

    internal string[] Subscriptions(string pluginId)
    {
        lock (_gate)
            return _subscriptions.TryGetValue(pluginId, out var set) ? [.. set] : [];
    }

    /// <summary>
    /// 投递一个启动器事件给所有订阅了它的插件（宿主在游戏状态变化时调用）。
    /// 插件未定义 onLauncherEvent 时是空操作；单个插件出错只记日志，不影响其他插件。
    /// </summary>
    public void RaiseEvent(string eventName, object? payload = null)
    {
        if (!KnownEvents.Contains(eventName)) return;
        string json;
        try
        {
            json = payload is null ? "{}" : System.Text.Json.JsonSerializer.Serialize(payload);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("plugin", $"事件 {eventName} 载荷序列化失败：{ex.Message}");
            json = "{}";
        }

        Loaded[] targets;
        lock (_gate)
        {
            targets =
            [
                .. _loaded.Where(p =>
                    p.Manifest.Permissions.Contains(PluginPermissions.Events) &&
                    _subscriptions.TryGetValue(p.Manifest.Id, out var set) &&
                    set.Contains(eventName)),
            ];
        }
        if (targets.Length == 0) return;

        var evt = new PluginEventContext { Name = eventName, Data = json };
        foreach (var plugin in targets)
        {
            // 事件可能来自任意线程（计时器 / 进程退出回调），而 Jint 引擎不可并发使用 → 串行
            lock (_hookGate)
            {
                try
                {
                    var ctx = BuildContext(plugin.Manifest, plugin.Dir, plugin.Logger,
                        args: null, evt);
                    lock (_gate) _activeContexts[plugin.Manifest.Id] = ctx;
                    try
                    {
                        plugin.Runtime.InvokeHook(HookLauncherEvent, ctx);
                    }
                    finally
                    {
                        lock (_gate) _activeContexts.Remove(plugin.Manifest.Id);
                    }
                }
                catch (Exception ex)
                {
                    plugin.Logger.Error($"onLauncherEvent({eventName}) 执行失败：{ex.Message}");
                }
            }
        }
    }

    /// <summary>带上下文登记地调用钩子：Python 侧 host_call 靠登记找到当前 ctx</summary>
    private void InvokeHookGuarded(Loaded plugin, PluginContext ctx, string hookName)
    {
        lock (_gate) _activeContexts[plugin.Manifest.Id] = ctx;
        try
        {
            plugin.Runtime.InvokeHook(hookName, ctx);
        }
        finally
        {
            lock (_gate) _activeContexts.Remove(plugin.Manifest.Id);
        }
    }

    // ── Python 插件 host_call 分发（能力名 → ctx 成员方法） ──

    /// <summary>
    /// Python 子进程在钩子执行期间发起的宿主调用（行式 JSON 协议），形如
    /// {"op":"host_call","id":3,"api":"instances.list","args":[]}。
    /// 返回 {"ok":true,"result":…} 或 {"ok":false,"error":"…"}。
    /// </summary>
    internal string HostCall(string pluginId, string api, string argsJson)
    {
        string error;
        try
        {
            PluginContext? ctx;
            lock (_gate) _activeContexts.TryGetValue(pluginId, out ctx);
            if (ctx is null)
            {
                error = "宿主调用只能发生在插件钩子执行期间";
            }
            else
            {
                var result = InvokeCapability(ctx, api, argsJson);
                return "{\"ok\":true,\"result\":" + PluginApiCore.Json(result) + "}";
            }
        }
        catch (Exception ex)
        {
            error = ex.Message;
        }
        return "{\"ok\":false,\"error\":" + PluginApiCore.Json(error) + "}";
    }

    /// <summary>能力名 → (ctx 成员, 方法) 的映射表（由 ctx 类型反射生成，避免两处维护）</summary>
    private static readonly Dictionary<string, (string Member, string Method)> Capabilities =
        BuildCapabilityMap();

    private static Dictionary<string, (string, string)> BuildCapabilityMap()
    {
        var map = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
        foreach (var prop in typeof(PluginContext).GetProperties(
                     BindingFlags.Public | BindingFlags.Instance))
        {
            if (prop.PropertyType == typeof(string[]) ||
                prop.PropertyType == typeof(PluginLauncherInfo) ||
                prop.PropertyType == typeof(PluginEventContext)) continue;
            var domain = Camel(prop.Name);
            foreach (var m in prop.PropertyType.GetMethods(
                         BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                if (m.IsSpecialName) continue; // 跳过属性访问器
                map[$"{domain}.{Camel(m.Name)}"] = (prop.Name, m.Name);
            }
        }
        return map;

        static string Camel(string name) =>
            name.Length == 0 ? name : char.ToLowerInvariant(name[0]) + name[1..];
    }

    /// <summary>按能力名调用 ctx 成员方法（参数为 JSON 数组，逐位绑定）</summary>
    internal static object? InvokeCapability(PluginContext ctx, string api, string argsJson)
    {
        if (string.IsNullOrWhiteSpace(api))
            throw new ArgumentException("能力名不能为空");
        if (!Capabilities.TryGetValue(api.Trim(), out var target))
            throw new ArgumentException($"未知能力：{api}");

        var member = typeof(PluginContext).GetProperty(target.Member)?.GetValue(ctx)
            ?? throw new UnauthorizedAccessException(
                $"该插件没有启用 {target.Member} 能力（对应权限未声明）");
        var method = member.GetType().GetMethod(target.Method)
            ?? throw new InvalidOperationException($"能力方法不存在：{api}");

        var argsNode = PluginApiCore.ParseArgs(argsJson);
        var provided = argsNode.ValueKind == System.Text.Json.JsonValueKind.Array
            ? argsNode.EnumerateArray().ToArray()
            : [];

        var parameters = method.GetParameters();
        var call = new object?[parameters.Length];
        for (var i = 0; i < parameters.Length; i++)
        {
            var p = parameters[i];
            if (i < provided.Length)
            {
                call[i] = Coerce(provided[i], p.ParameterType, api, p.Name ?? "");
            }
            else if (p.HasDefaultValue)
            {
                call[i] = p.DefaultValue;
            }
            else if (p.ParameterType == typeof(string))
            {
                call[i] = "";
            }
            else if (!p.ParameterType.IsValueType)
            {
                call[i] = null;
            }
            else
            {
                call[i] = Activator.CreateInstance(p.ParameterType);
            }
        }

        try
        {
            return method.Invoke(member, call);
        }
        catch (TargetInvocationException ex) when (ex.InnerException is not null)
        {
            throw ex.InnerException;
        }
    }

    private static object? Coerce(System.Text.Json.JsonElement value, Type type,
        string api, string paramName)
    {
        var nullable = Nullable.GetUnderlyingType(type);
        if (value.ValueKind is System.Text.Json.JsonValueKind.Null or
            System.Text.Json.JsonValueKind.Undefined)
            return nullable is not null || !type.IsValueType ? null : Activator.CreateInstance(type);
        var t = nullable ?? type;
        if (t == typeof(string))
            return value.ValueKind == System.Text.Json.JsonValueKind.String
                ? value.GetString()
                : value.ToString();
        if (t == typeof(bool))
            return value.ValueKind switch
            {
                System.Text.Json.JsonValueKind.True => true,
                System.Text.Json.JsonValueKind.False => false,
                _ => bool.TryParse(value.ToString(), out var b) && b,
            };
        if (t == typeof(int))
            return value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                   value.TryGetInt32(out var n)
                ? n
                : int.TryParse(value.ToString(), out var parsed) ? parsed : 0;
        if (t == typeof(long))
            return value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                   value.TryGetInt64(out var l)
                ? l
                : long.TryParse(value.ToString(), out var lp) ? lp : 0L;
        if (t == typeof(double))
            return value.ValueKind == System.Text.Json.JsonValueKind.Number &&
                   value.TryGetDouble(out var d)
                ? d
                : double.TryParse(value.ToString(), out var dp) ? dp : 0d;
        if (t == typeof(string[]))
            return value.ValueKind == System.Text.Json.JsonValueKind.Array
                ? value.EnumerateArray().Select(e => e.ToString()).ToArray()
                : [];
        // object 形参（如 storage.set 的值）：原样转成普通对象图，JSON 数字/布尔/字符串/数组/对象都保留
        if (t == typeof(object))
            return PluginApiCore.Plain(System.Text.Json.Nodes.JsonNode.Parse(value.GetRawText()));
        throw new ArgumentException($"{api} 的参数 {paramName} 类型不支持（{type.Name}）");
    }

    public void Dispose() => StopAll();
}
