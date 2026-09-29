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

    /// <summary>
    /// 插件注册的 UI 面板（launcher:ui_panel / 2.0 contributes.panels）。
    /// EntryPath：1.x = 插件目录内 HTML；2.0（Declarative=true）= 声明式视图 JSON（规范 2.0 §7）。
    /// </summary>
    public sealed record PanelRegistration(
        string PluginId, string PanelId, string Title, string EntryPath, bool Declarative = false);

    /// <summary>
    /// 插件注册的页面挂载区（launcher:ui_panel / 2.0 contributes.slots、versionSections）。
    /// EntryPath：1.x = 插件目录内 HTML；声明式（Declarative=true）= 视图 JSON 的绝对路径。
    /// Title 仅声明式用（分区/卡片要抬头），Height ≤0 表示按内容自适应。
    /// </summary>
    public sealed record SlotRegistration(
        string PluginId, string SlotId, string EntryPath, int Height,
        string Title = "", bool Declarative = false);

    /// <summary>
    /// 插件在首页放的一张卡片（2.0 contributes.homeCards，规范 2.0 §6.2）。
    /// 与挂载区不同：同一插件可以声明多张，卡片带按插件加载顺序排列。
    /// </summary>
    public sealed record HomeCardRegistration(
        string PluginId, string CardId, string Title, string EntryPath);

    /// <summary>
    /// 插件的一级导航项（2.0 contributes.navItems，规范 2.0 §6.4）。
    /// 跨插件上限（最多 3 个）由宿主导航栏按加载顺序截断，注册表全量收下。
    /// </summary>
    public sealed record NavItemRegistration(
        string PluginId, string NavId, string Title, string IconGlyph, string EntryPath,
        bool IconFallback);

    /// <summary>插件启动失败信息</summary>
    public sealed record PluginStartError(string PluginId, string Message);

    private sealed record Loaded(
        PluginManifest Manifest, string Dir, IPluginRuntime Runtime, PluginLogger Logger);

    private readonly object _gate = new();
    private readonly List<Loaded> _loaded = [];
    private readonly List<PanelRegistration> _panels = [];
    private readonly List<SlotRegistration> _slots = [];
    private readonly List<HomeCardRegistration> _homeCards = [];
    private readonly List<NavItemRegistration> _navItems = [];
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

    /// <summary>
    /// 面板 / 挂载区集合发生任何变化（插件加载完注册、停用摘除）。
    /// 供宿主页面上的挂载区控件重建内容——插件是后台加载的，
    /// 首页这类启动即渲染的页面在首次 Loaded 时可能还看不到贡献。
    /// </summary>
    public event Action? ContributionsChanged;

    private void RaiseContributionsChanged() => ContributionsChanged?.Invoke();

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
        RaiseContributionsChanged();
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
        RaiseContributionsChanged();
    }

    /// <summary>查询某挂载点上所有插件注册的内容（页面加载时调用）</summary>
    public IReadOnlyList<SlotRegistration> GetSlots(string slotId)
    {
        lock (_gate) return [.. _slots.Where(s => s.SlotId == slotId)];
    }

    /// <summary>首页卡片（按插件加载顺序、插件内的声明顺序排列）</summary>
    public IReadOnlyList<HomeCardRegistration> GetHomeCards()
    {
        lock (_gate) return [.. _homeCards];
    }

    /// <summary>一级导航项（按插件加载顺序、插件内的声明顺序）</summary>
    public IReadOnlyList<NavItemRegistration> GetNavItems()
    {
        lock (_gate) return [.. _navItems];
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
        // 未来版本（如 3.0）明确拒绝，避免「装上了却静默不工作」
        if (!PluginEnvironment.CanRunApi(manifest.ApiVersion))
            return new PluginStartError(manifest.Id,
                $"本版本启动器暂不支持 API {manifest.ApiVersion} 插件，插件保持停用");
        var logger = new PluginLogger(manifest.Id, _logDir);
        // 2.0：细粒度能力经桥接后下发；未强制/未实现的能力一律不给（fail-closed），逐条记日志
        if (manifest.IsApi2)
        {
            var grant = PluginPermissions.BridgeApi2(manifest.Permissions);
            logger.Info(grant.Granted.Count > 0
                ? $"2.0 能力已生效：{string.Join(" · ", grant.Granted)}"
                : "2.0 能力：无（未声明可用能力）");
            if (grant.Deferred.Count > 0)
                logger.Warn($"以下 2.0 能力本次未开放（fail-closed）：{string.Join("；", grant.Deferred)}");
        }
        IPluginRuntime runtime;
        try
        {
            runtime = CreateRuntime(manifest, dir);
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
            RegisterContributedPanels(manifest, dir, logger);
            RegisterContributedSlots(manifest, dir, logger);
            RegisterContributedHomeCards(manifest, dir, logger);
            RegisterContributedNavItems(manifest, dir, logger);
            logger.Info($"已加载（v{manifest.Version}，权限 {string.Join(", ", manifest.Permissions)}）");
            return null;
        }
        catch (Exception ex)
        {
            Stop(manifest.Id);
            return new PluginStartError(manifest.Id, $"onLoad 执行失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 2.0 声明式扩展点（规范 2.0 §6.2）：清单里写了就注册，不需要插件代码执行。
    /// 面板入口是包内相对路径（manifest.Validate 已校验过安全），在目录内解析成绝对路径。
    /// </summary>
    private void RegisterContributedPanels(PluginManifest manifest, string dir, PluginLogger logger)
    {
        var panels = manifest.Contributes?.Panels;
        if (panels is null || panels.Length == 0) return;
        // 声明式面板同样是 UI 能力：没拿到 ui:panel 就不注册（与 ctx.ui 的装配口径一致）
        if (manifest.IsApi2 &&
            !PluginPermissions.BridgeApi2(manifest.Permissions)
                .Capabilities.Contains(PluginPermissions.UiPanel))
        {
            logger.Warn($"声明了 contributes.panels，但 ui:panel 能力未生效，面板不会注册");
            return;
        }
        foreach (var panel in panels)
        {
            if (string.IsNullOrWhiteSpace(panel.Id) || string.IsNullOrWhiteSpace(panel.View)) continue;
            var title = string.IsNullOrWhiteSpace(panel.Title) ? panel.Id : panel.Title;
            var reg = new PanelRegistration(manifest.Id, panel.Id, title,
                Path.Combine(dir, panel.View.Replace('/', Path.DirectorySeparatorChar)),
                Declarative: true);
            lock (_gate)
            {
                _panels.RemoveAll(p => p.PluginId == manifest.Id && p.PanelId == panel.Id);
                _panels.Add(reg);
            }
            PanelRegistered?.Invoke(reg);
            logger.Info($"已注册声明式面板：{panel.Id}（{title}）");
        }
        RaiseContributionsChanged();
    }

    /// <summary>宿主实际支持的挂载点（写别的点会被拒绝并记日志，避免「声明了却静默不生效」）</summary>
    public static readonly string[] SupportedSlotPoints =
    [
        "download.top", "home.top", "versions.top", "version.detail.top",
        "version.detail.sections",
    ];

    /// <summary>contributes.versionSections 的落点（版本详情页的分区区）</summary>
    public const string VersionSectionsPoint = "version.detail.sections";

    /// <summary>
    /// 2.0 声明式挂载区（规范 2.0 §6.2）：contributes.slots / versionSections 里写了就注册。
    /// 需要 <c>ui:panel</c> 能力；点不在 <see cref="SupportedSlotPoints"/> 里的一律不注册并记日志。
    /// </summary>
    private void RegisterContributedSlots(PluginManifest manifest, string dir, PluginLogger logger)
    {
        var contributes = manifest.Contributes;
        if (contributes is null) return;
        var hasSlots = contributes.Slots.Length > 0 || contributes.VersionSections.Length > 0;
        if (!hasSlots) return;
        if (manifest.IsApi2 &&
            !PluginPermissions.BridgeApi2(manifest.Permissions)
                .Capabilities.Contains(PluginPermissions.UiPanel))
        {
            logger.Warn("声明了 contributes.slots / versionSections，但 ui:panel 能力未生效，挂载区不会注册");
            return;
        }

        foreach (var slot in contributes.Slots)
        {
            if (string.IsNullOrWhiteSpace(slot.Point) || string.IsNullOrWhiteSpace(slot.View)) continue;
            if (!SupportedSlotPoints.Contains(slot.Point, StringComparer.Ordinal))
            {
                logger.Warn($"挂载点 {slot.Point} 宿主暂不支持，已跳过（支持：{string.Join(" / ", SupportedSlotPoints)}）");
                continue;
            }
            AddSlot(manifest, dir, logger, slot.Point, slot.View, slot.Height, title: "");
        }

        foreach (var section in contributes.VersionSections)
        {
            if (string.IsNullOrWhiteSpace(section.Id) || string.IsNullOrWhiteSpace(section.View)) continue;
            AddSlot(manifest, dir, logger, VersionSectionsPoint, section.View, 0,
                string.IsNullOrWhiteSpace(section.Title) ? section.Id : section.Title);
        }
        RaiseContributionsChanged();
    }

    private void AddSlot(PluginManifest manifest, string dir, PluginLogger logger,
        string point, string view, int height, string title)
    {
        var reg = new SlotRegistration(manifest.Id, point,
            Path.Combine(dir, view.Replace('/', Path.DirectorySeparatorChar)),
            height > 0 ? Math.Clamp(height, 48, 320) : 0,
            title, Declarative: true);
        lock (_gate)
        {
            // 同一插件同一点只保留一条（清单本身不会重复，这里防手改清单）
            _slots.RemoveAll(s => s.PluginId == manifest.Id && s.SlotId == point);
            _slots.Add(reg);
        }
        logger.Info($"已注册声明式挂载区：{point}（{Path.GetFileName(view)}）");
    }

    /// <summary>
    /// 2.0 首页卡片（规范 2.0 §6.2）：contributes.homeCards 里写了就注册。
    /// 与挂载区同为 UI 能力，需要 <c>ui:panel</c>；同一插件可声明多张，
    /// 「最多显示几张」由宿主首页卡片带决定（这里全量收下，不截断）。
    /// </summary>
    private void RegisterContributedHomeCards(PluginManifest manifest, string dir, PluginLogger logger)
    {
        var cards = manifest.Contributes?.HomeCards;
        if (cards is null || cards.Length == 0) return;
        if (manifest.IsApi2 &&
            !PluginPermissions.BridgeApi2(manifest.Permissions)
                .Capabilities.Contains(PluginPermissions.UiPanel))
        {
            logger.Warn("声明了 contributes.homeCards，但 ui:panel 能力未生效，首页卡片不会注册");
            return;
        }

        foreach (var card in cards)
        {
            if (string.IsNullOrWhiteSpace(card.Id) || string.IsNullOrWhiteSpace(card.View)) continue;
            var reg = new HomeCardRegistration(manifest.Id, card.Id,
                string.IsNullOrWhiteSpace(card.Title) ? card.Id : card.Title,
                Path.Combine(dir, card.View.Replace('/', Path.DirectorySeparatorChar)));
            lock (_gate)
            {
                // 同一插件同一 id 只保留一条（清单本身不会重复，这里防手改清单）
                _homeCards.RemoveAll(c => c.PluginId == manifest.Id && c.CardId == card.Id);
                _homeCards.Add(reg);
            }
            logger.Info($"已注册首页卡片：{card.Id}（{reg.Title}）");
        }
        RaiseContributionsChanged();
    }

    /// <summary>2.0 一级导航项：contributes.navItems 里写了就注册；同样是 UI 能力，需要 ui:panel。</summary>
    private void RegisterContributedNavItems(PluginManifest manifest, string dir, PluginLogger logger)
    {
        var items = manifest.Contributes?.NavItems;
        if (items is null || items.Length == 0) return;
        if (manifest.IsApi2 &&
            !PluginPermissions.BridgeApi2(manifest.Permissions).Capabilities.Contains(PluginPermissions.UiPanel))
        {
            logger.Warn("声明了 contributes.navItems，但 ui:panel 能力未生效，导航项不会注册");
            return;
        }

        // 清单校验已限制 ≤1；这里仍防御性只取第一条（防手改清单）
        var item = items[0];
        if (string.IsNullOrWhiteSpace(item.Id) || string.IsNullOrWhiteSpace(item.View)) return;
        var iconOk = PluginExtensionPoints.TryResolveNavIcon(item.Icon, out var glyph);
        if (!iconOk) logger.Warn($"导航项 {item.Id} 的图标名 {item.Icon} 不在宿主白名单里，已回落默认图标");

        var reg = new NavItemRegistration(manifest.Id, item.Id,
            string.IsNullOrWhiteSpace(item.Title) ? item.Id : item.Title,
            glyph,
            Path.Combine(dir, item.View.Replace('/', Path.DirectorySeparatorChar)),
            IconFallback: !iconOk);
        lock (_gate)
        {
            _navItems.RemoveAll(n => n.PluginId == manifest.Id && n.NavId == item.Id);
            _navItems.Add(reg);
        }
        logger.Info($"已注册导航项：{item.Id}（{reg.Title}）");
        RaiseContributionsChanged();
    }

    /// <summary>停止插件：回调 onUnload、清理面板、释放运行时</summary>
    public void Stop(string pluginId)
    {
        Loaded? plugin;
        var removedAny = false;
        lock (_gate)
        {
            plugin = _loaded.FirstOrDefault(p => p.Manifest.Id == pluginId);
            if (plugin is null) return;
            _loaded.Remove(plugin);
            _subscriptions.Remove(pluginId);
            var removed = _panels.Where(p => p.PluginId == pluginId).ToList();
            _panels.RemoveAll(p => p.PluginId == pluginId);
            var removedSlots = _slots.RemoveAll(s => s.PluginId == pluginId) > 0;
            var removedCards = _homeCards.RemoveAll(c => c.PluginId == pluginId) > 0;
            var removedNavs = _navItems.RemoveAll(n => n.PluginId == pluginId) > 0;
            removedAny = removed.Count > 0 || removedSlots || removedCards || removedNavs;
            foreach (var r in removed) PanelUnregistered?.Invoke(pluginId, r.PanelId);
        }
        if (removedAny) RaiseContributionsChanged();
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

    private IPluginRuntime CreateRuntime(PluginManifest manifest, string dir)
    {
        if (File.Exists(Path.Combine(dir, "main.js")))
            return new JsPluginRuntime();
        if (File.Exists(Path.Combine(dir, "main.py")))
            return new PyPluginRuntime(dir, _hostSdkPath, manifest.Id, HostCall);
        // 纯声明式 2.0 插件（只有 contributes、没有入口脚本）也允许加载：无可执行代码 → 空运行时
        if (manifest.IsApi2 && manifest.Contributes is not null)
            return new NoopPluginRuntime();
        throw new InvalidOperationException("插件缺少入口脚本（main.js / main.py）");
    }

    /// <summary>无入口脚本的声明式插件运行时：全部钩子为空操作</summary>
    private sealed class NoopPluginRuntime : IPluginRuntime
    {
        public string EntryName => "";
        public string? Load(string entryPath, PluginContext ctx) => null;
        public void InvokeHook(string hookName, PluginContext ctx) { }
        public void Dispose() { }
    }

    private PluginContext BuildContext(PluginManifest manifest, string dir,
        PluginLogger logger, GameArgsManager? args, PluginEventContext? evt = null)
    {
        // 2.0：装配用的能力集合是"桥接后"的（只含不放大权限的能力），
        // 主机白名单与存储配额随之一起下发（规范 2.0 §5）
        var grant = manifest.IsApi2 ? PluginPermissions.BridgeApi2(manifest.Permissions) : null;
        string[] perms = grant is null ? manifest.Permissions : [.. grant.Capabilities];
        var api = _services is null ? null : new PluginApiCore(
            _services, manifest.Id, Path.Combine(dir, "data"), logger, perms, this, evt,
            grant?.AllowedHosts, grant?.StorageQuotaKb);
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
                ApiVersion = manifest.ApiVersion.Length > 0
                    ? manifest.ApiVersion
                    : PluginEnvironment.ApiVersion,
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

    // ── 声明式视图动作（规范 2.0 §7.2） ──

    /// <summary>动作执行结果：ResultJson = 能力返回值的 JSON；Error 非空即失败</summary>
    public sealed record PluginActionResult(string? ResultJson, string? Error)
    {
        public bool Ok => Error is null;
    }

    /// <summary>
    /// 执行声明式视图里的 <c>{api,args}</c> 动作（宿主代插件调用能力）。
    /// 走的是与插件自己调用时同一套能力分发，因此权限校验、网络主机白名单、
    /// 存储配额等限制完全一致——声明式 UI 不是绕过权限的旁路。
    /// </summary>
    public PluginActionResult InvokeAction(string pluginId, string api, string argsJson)
    {
        Loaded? plugin;
        lock (_gate) plugin = _loaded.FirstOrDefault(p => p.Manifest.Id == pluginId);
        if (plugin is null)
            return new PluginActionResult(null, "插件未启用：面板动作需要插件处于启用状态");
        if (string.IsNullOrWhiteSpace(api))
            return new PluginActionResult(null, "动作缺少 api");
        try
        {
            var ctx = BuildContext(plugin.Manifest, plugin.Dir, plugin.Logger, args: null);
            var result = InvokeCapability(ctx, api, string.IsNullOrWhiteSpace(argsJson) ? "{}" : argsJson);
            return new PluginActionResult(PluginApiCore.Json(result), null);
        }
        catch (Exception ex)
        {
            return new PluginActionResult(null, ex.Message);
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
