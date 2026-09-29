// 插件能力层（plugin-spec.md §7）：ctx 上每个成员一个类型，公开方法即开放的能力。
//
// 三条硬规则（改这个文件时必须守住）：
//   1. 权限门控：成员由 PluginHost 按 manifest 声明装配；即使有人绕过装配直接调用，
//      每个方法入口还会做一次 Require(权限)，两道门。
//   2. 身份不可触碰：任何方法都不返回、不接受 accessToken / refreshToken / 客户端令牌；
//      ctx.accounts 只有身份元数据（id / 名字 / 类型 / 是否默认）。
//   3. 复杂动作不重写：装版本、补全、装资源、整合包、备份、联机这些一律转交与 Nova
//      共用的 NovaToolRegistry，保证「AI 能做的插件也能做，且两条路径行为一致」。
//
// 异步收敛：插件钩子是同步的（JS 引擎同步执行、Python 走行式协议），因此所有宿主异步
// 调用都在 PluginSync 里收敛成同步返回；工作体放到线程池上跑，避免占着 UI 线程时死锁。

using System.Text.Json;
using System.Text.Json.Nodes;
using Starcloud.Core.Services;

namespace Starcloud.Core.Plugins;

/// <summary>同步等待宿主异步操作（插件钩子上下文里没有 await）</summary>
internal static class PluginSync
{
    internal static T Run<T>(Func<Task<T>> work) => Task.Run(work).GetAwaiter().GetResult();

    internal static void Run(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();
}

/// <summary>launcher:events 的本次事件载荷（onLauncherEvent 钩子专用）</summary>
public sealed class PluginEventContext
{
    /// <summary>事件名，如 game.launched</summary>
    public string Name { get; init; } = "";

    /// <summary>事件载荷（JSON 字符串；JS 侧 ctx.event.data，Python 侧 ctx.event["data"]）</summary>
    public string Data { get; init; } = "{}";
}

/// <summary>
/// 能力层的公共运行时：服务包 + 权限集 + 插件身份 + 通用工具（JSON、宿主调用、权限校验）。
/// 每个 ctx 成员持有同一个实例。
/// <para>
/// 公开是因为各 ctx 成员类型（JS 沙箱需要 public 才能包装）要用它构造；
/// 成员全部是 internal，插件侧看到的只会是 ctx.versions / ctx.instances 这些成员，
/// 不需要、也不应该直接使用本类型。
/// </para>
/// </summary>
public sealed class PluginApiCore
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(30) };

    internal PluginHostServices Services { get; }
    internal string PluginId { get; }
    internal string DataDir { get; }
    internal PluginLogger Log { get; }
    internal IReadOnlyCollection<string> Permissions { get; }
    internal PluginHost Host { get; }
    internal PluginEventContext? Event { get; }

    /// <summary>设置键黑名单：主机私有 / 敏感字段，读写都不经插件（规范 §7.9）</summary>
    internal static readonly string[] HostOnlySettingKeys =
    [
        "nova_llm_api_key", "nova_llm_base_url", "nova_llm_model", "nova_llm_profiles",
        "nova_llm_active_id", "nova_gateway_enabled", "nova_quota_limit", "nova_quota_used",
        "nova_quota_reset_at", "nova_quota_checked_at", "curseforge_api_key",
        "update_manifest_url",
    ];

    internal PluginApiCore(PluginHostServices services, string pluginId, string dataDir,
        PluginLogger log, IReadOnlyCollection<string> permissions, PluginHost host,
        PluginEventContext? evt, string[]? allowedHosts = null, int? storageQuotaKb = null)
    {
        Services = services;
        PluginId = pluginId;
        DataDir = dataDir;
        Log = log;
        Permissions = permissions;
        Host = host;
        Event = evt;
        AllowedHosts = allowedHosts;
        StorageQuotaKb = storageQuotaKb;
    }

    /// <summary>
    /// 2.0 <c>network:host=</c> 允许的主机（支持 *.example.com 通配）；null = 不受限（1.x 语义）。
    /// 未声明 network 的插件不会走到这里（Require 先生效）。
    /// </summary>
    internal string[]? AllowedHosts { get; }

    /// <summary>2.0 <c>storage:kv(quota=)</c> 声明的私有存储配额（KB）；null = 不限（1.x 语义）</summary>
    internal int? StorageQuotaKb { get; }

    /// <summary>主机白名单校验（规范 2.0 §5.1：network 必须显式声明主机）</summary>
    private void EnsureHostAllowed(string host, string url)
    {
        if (AllowedHosts is null) return;
        if (IsHostAllowed(AllowedHosts, host)) return;
        throw new UnauthorizedAccessException(
            $"插件只声明了 {string.Join("、", AllowedHosts)} 的网络访问权限，已拒绝访问 {host}（{url}）");
    }

    /// <summary>
    /// 主机是否落在白名单内：<c>*</c> 全放行；<c>*.example.com</c> 匹配子域但**不**匹配裸域本身
    /// （避免 evil-example.com 这类后缀相近的域名被误放）。
    /// </summary>
    internal static bool IsHostAllowed(IReadOnlyList<string> patterns, string host)
    {
        foreach (var pattern in patterns)
        {
            if (pattern == "*") return true;
            if (pattern.StartsWith("*.", StringComparison.Ordinal))
            {
                if (host.EndsWith(pattern[1..], StringComparison.OrdinalIgnoreCase)) return true;
            }
            else if (string.Equals(host, pattern, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    internal bool Has(string permission) => Permissions.Contains(permission);

    /// <summary>第二道权限门：未声明即拒绝（异常由宿主捕获记录，不会拖垮启动器）</summary>
    internal void Require(string permission, string what)
    {
        if (!Has(permission))
            throw new UnauthorizedAccessException($"插件未声明 {permission} 权限，无法{what}");
    }

    internal LauncherSettings Settings => Services.Settings();

    internal string DotMc => Settings.DotMinecraft;

    /// <summary>结构化返回值的统一构造（JS 侧可直接 .field 访问，Python 侧是 JSON 对象）</summary>
    internal static Dictionary<string, object?> O(params (string Key, object? Value)[] pairs)
    {
        var d = new Dictionary<string, object?>(pairs.Length, StringComparer.Ordinal);
        foreach (var (k, v) in pairs) d[k] = v;
        return d;
    }

    /// <summary>同上，实例写法（各 ctx 成员里 _c.Obj(...) 用）</summary>
    internal Dictionary<string, object?> Obj(params (string Key, object? Value)[] pairs) => O(pairs);

    /// <summary>写操作统一返回：{ ok, message }（message 是启动器产生的结果说明）</summary>
    internal static Dictionary<string, object?> Ok(string message) =>
        O(("ok", true), ("message", message));

    /// <summary>转交与 Nova 共用的执行层（复杂动作只有这一份实现）</summary>
    internal string Tool(string name, object args)
    {
        var tools = Services.Tools
            ?? throw new InvalidOperationException("当前宿主没有提供该能力");
        var json = JsonSerializer.Serialize(args);
        return PluginSync.Run(() => tools.ExecuteAsync(name, json));
    }

    internal Dictionary<string, object?> ToolOk(string name, object args) => Ok(Tool(name, args));

    /// <summary>把 JSON 参数解析成可按名取值的 JsonElement（结构参数统一走这里）</summary>
    internal static JsonElement ParseArgs(string? argsJson)
    {
        var text = (argsJson ?? "").Trim();
        if (text.Length == 0) return JsonDocument.Parse("{}").RootElement.Clone();
        try
        {
            return JsonDocument.Parse(text).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ArgumentException("参数不是合法 JSON");
        }
    }

    internal static string ArgString(JsonElement args, string name, string fallback = "")
    {
        if (args.ValueKind != JsonValueKind.Object) return fallback;
        if (!args.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString() ?? fallback,
            JsonValueKind.Null or JsonValueKind.Undefined => fallback,
            _ => v.ToString(),
        };
    }

    internal static int? ArgInt(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return null;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n)) return n;
        return int.TryParse(v.ToString(), out var parsed) ? parsed : null;
    }

    internal static bool ArgBool(JsonElement args, string name, bool fallback = false)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind is JsonValueKind.True) return true;
        if (v.ValueKind is JsonValueKind.False) return false;
        return bool.TryParse(v.ToString(), out var b) ? b : fallback;
    }

    /// <summary>需要宿主界面的能力（确认框 / 开页 / 剪贴板 / 打开链接）</summary>
    internal IPluginHostBridge Bridge =>
        Services.Bridge ?? throw new InvalidOperationException("当前宿主没有提供界面能力");

    /// <summary>插件私有目录内的安全路径（与 ctx.files 同一套越界拒绝规则）</summary>
    internal string SafePath(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("文件名不能为空");
        var root = Path.GetFullPath(DataDir);
        var full = Path.GetFullPath(Path.Combine(root, name));
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new UnauthorizedAccessException($"路径越界：插件只能访问自身 data 目录（{name}）");
        return full;
    }

    // ── 宿主 HTTP（launcher:network）：插件自己的网络请求走这里，统一超时与错误语义 ──

    internal Dictionary<string, object?> HttpRequest(string method, string url, string? body,
        string? contentType)
    {
        Require(PluginPermissions.Network, "发起网络请求");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArgumentException($"只允许 http / https 绝对地址：{url}");
        EnsureHostAllowed(uri.Host, url);

        return PluginSync.Run(async () =>
        {
            using var req = new HttpRequestMessage(new HttpMethod(method.ToUpperInvariant()), uri);
            if (body is { Length: > 0 })
                req.Content = new StringContent(body,
                    System.Text.Encoding.UTF8, contentType ?? "application/json");
            using var resp = await Http.SendAsync(req).ConfigureAwait(false);
            var content = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            return O(
                ("status", (int)resp.StatusCode),
                ("ok", resp.IsSuccessStatusCode),
                ("contentType", resp.Content.Headers.ContentType?.ToString() ?? ""),
                ("body", content));
        });
    }

    internal static string Json(object? value) =>
        JsonSerializer.Serialize(value, new JsonSerializerOptions
        {
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });

    /// <summary>JSON 节点 → 插件侧可直接使用的普通对象（bool / int / double / string / 数组 / 字典）</summary>
    internal static object? Plain(JsonNode? node) => node switch
    {
        null => null,
        JsonArray arr => arr.Select(x => Plain(x)).ToArray(),
        JsonObject obj => obj.ToDictionary(p => p.Key, p => Plain(p.Value)),
        JsonValue v => v.TryGetValue<bool>(out var b) ? b
            : v.TryGetValue<int>(out var i) ? i
            : v.TryGetValue<double>(out var d) ? d
            : v.TryGetValue<string>(out var s) ? s
            : v.ToJsonString(),
        _ => node.ToJsonString(),
    };
}

// ══════════════════════════════════════════════════════════════════════
//  ctx.versions / ctx.instances —— 版本与实例
// ══════════════════════════════════════════════════════════════════════

/// <summary>ctx.versions（launcher:instances_read）：官方版本清单与已安装实例</summary>
public sealed class PluginVersionsApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>官方版本清单（含类型与发布时间；已安装的标记 installed=true）</summary>
    public object?[] List(bool includeSnapshots = false)
    {
        _c.Require(PluginPermissions.InstancesRead, "读取官方版本清单");
        var manifest = _c.Services.Manifest
            ?? throw new InvalidOperationException("当前宿主没有提供版本清单服务");
        var m = PluginSync.Run(() => manifest.GetManifestAsync(false));
        var dotMc = _c.DotMc;
        var installed = manifest.ListInstalled(dotMc).Select(i => i.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        return m.Versions
            .Where(v => includeSnapshots || v.Type == "release")
            .Select(v => (object?)_c.Obj(
                ("id", v.Id), ("type", v.Type), ("releaseTime", v.ReleaseTime),
                ("installed", installed.Contains(v.Id))))
            .ToArray();
    }

    /// <summary>官方最新正式版 / 快照版版本号</summary>
    public Dictionary<string, object?> Latest()
    {
        _c.Require(PluginPermissions.InstancesRead, "读取官方版本清单");
        var manifest = _c.Services.Manifest
            ?? throw new InvalidOperationException("当前宿主没有提供版本清单服务");
        var m = PluginSync.Run(() => manifest.GetManifestAsync(false));
        return _c.Obj(("release", m.Latest.Release), ("snapshot", m.Latest.Snapshot));
    }

    /// <summary>已安装实例 ID 与占用空间</summary>
    public object?[] Installed()
    {
        _c.Require(PluginPermissions.InstancesRead, "读取已安装实例");
        var manifest = _c.Services.Manifest
            ?? throw new InvalidOperationException("当前宿主没有提供版本清单服务");
        var dotMc = _c.DotMc;
        return manifest.ListInstalled(dotMc)
            .Select(i => (object?)_c.Obj(("id", i.Id), ("sizeBytes", i.SizeBytes)))
            .ToArray();
    }
}

/// <summary>ctx.instances（launcher:instances_read / instances_write）：实例详情与安装维护</summary>
public sealed class PluginInstancesApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>实例清单：ID、占用空间、是否隔离、分类、上次游玩、是否正在运行</summary>
    public object?[] List()
    {
        _c.Require(PluginPermissions.InstancesRead, "读取实例列表");
        var manifest = _c.Services.Manifest
            ?? throw new InvalidOperationException("当前宿主没有提供版本清单服务");
        var dotMc = _c.DotMc;
        var store = new InstanceSettingsStore(dotMc);
        return manifest.ListInstalled(dotMc)
            .Select(i =>
            {
                var p = store.Get(i.Id);
                return (object?)_c.Obj(
                    ("id", i.Id),
                    ("sizeBytes", i.SizeBytes),
                    ("isolated", InstanceLayout.IsIsolated(p, _c.Settings)),
                    ("category", p.Category ?? ""),
                    ("note", p.Note ?? ""),
                    ("lastPlayedAt", p.LastPlayedAt?.ToString("o")),
                    ("running", GameSessionGuard.IsVersionAlive(dotMc, i.Id)));
            })
            .ToArray();
    }

    /// <summary>实例是否已安装（版本 JSON 与 client jar 都在）</summary>
    public bool Exists(string versionId)
    {
        _c.Require(PluginPermissions.InstancesRead, "查询实例");
        var installer = _c.Services.Installer
            ?? throw new InvalidOperationException("当前宿主没有提供安装服务");
        return !string.IsNullOrWhiteSpace(versionId) && installer.IsInstalled(_c.DotMc, versionId);
    }

    /// <summary>实例详情：生效内存 / Java / 窗口 / 隔离 / 自动连服，以及该实例的内容目录文件</summary>
    public Dictionary<string, object?> Get(string versionId)
    {
        _c.Require(PluginPermissions.InstancesRead, "读取实例详情");
        if (string.IsNullOrWhiteSpace(versionId)) throw new ArgumentException("versionId 不能为空");
        var dotMc = _c.DotMc;
        var profile = new InstanceSettingsStore(dotMc).Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _c.Settings);
        var eff = InstanceLayout.Resolve(dotMc, versionId, profile, _c.Settings,
            MemoryAdvisor.Probe(InstanceLayout.GameRoot(dotMc, versionId, isolated)));
        return _c.Obj(
            ("id", versionId),
            ("installed", _c.Services.Installer?.IsInstalled(dotMc, versionId) ?? false),
            ("gameRoot", eff.GameRoot),
            ("isolated", isolated),
            ("memoryMinMb", eff.MinMem),
            ("memoryMaxMb", eff.MaxMem),
            ("javaPath", eff.Java ?? ""),
            ("windowWidth", eff.WindowW),
            ("windowHeight", eff.WindowH),
            ("fullscreen", eff.Fullscreen),
            ("serverAddress", eff.ServerAddress ?? ""),
            ("language", eff.Language),
            ("note", profile.Note ?? ""),
            ("mods", Files(versionId, "mods")));
    }

    /// <summary>实例内某个内容目录的文件名与大小（mods / resourcepacks / shaderpacks / saves / datapacks）</summary>
    public object?[] Files(string versionId, string kind = "mods")
    {
        _c.Require(PluginPermissions.InstancesRead, "读取实例文件");
        if (string.IsNullOrWhiteSpace(versionId)) throw new ArgumentException("versionId 不能为空");
        return ListFiles(GameRootOf(versionId, kind));
    }

    // ── 写操作：交给与 Nova 共用的执行层 ──

    /// <summary>安装一个官方原版版本</summary>
    public Dictionary<string, object?> Install(string versionId)
    {
        _c.Require(PluginPermissions.InstancesWrite, "安装版本");
        return _c.ToolOk("install_minecraft", new { version_id = versionId });
    }

    /// <summary>给某个 MC 版本安装加载器（forge / neoforge / fabric / quilt）</summary>
    public Dictionary<string, object?> InstallLoader(string loader, string mcVersion,
        string? build = null)
    {
        _c.Require(PluginPermissions.InstancesWrite, "安装加载器");
        return _c.ToolOk("install_loader", new { loader, mc_version = mcVersion, build });
    }

    /// <summary>补全实例缺失 / 损坏的文件</summary>
    public Dictionary<string, object?> Repair(string versionId)
    {
        _c.Require(PluginPermissions.InstancesWrite, "补全实例文件");
        return _c.ToolOk("repair_instance", new { version_id = versionId });
    }

    /// <summary>重命名实例（目录与实例档案一并迁移）</summary>
    public Dictionary<string, object?> Rename(string versionId, string newId)
    {
        _c.Require(PluginPermissions.InstancesWrite, "重命名实例");
        return _c.ToolOk("rename_version", new { version_id = versionId, new_id = newId });
    }

    /// <summary>删除实例（不可撤销，删前建议先 ctx.saves.backupInstance）</summary>
    public Dictionary<string, object?> Delete(string versionId)
    {
        _c.Require(PluginPermissions.InstancesWrite, "删除实例");
        return _c.ToolOk("delete_version", new { version_id = versionId });
    }

    /// <summary>修改实例覆盖设置（settingsJson 为 JSON 对象，字段见规范 §7.3）</summary>
    public Dictionary<string, object?> UpdateSettings(string versionId, string settingsJson)
    {
        _c.Require(PluginPermissions.InstancesWrite, "修改实例设置");
        var patch = PluginApiCore.ParseArgs(settingsJson);
        if (patch.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("settingsJson 必须是 JSON 对象");
        var node = new JsonObject { ["version_id"] = versionId };
        foreach (var prop in patch.EnumerateObject())
            node[prop.Name] = JsonNode.Parse(prop.Value.GetRawText());
        return _c.ToolOk("update_instance_settings", node);
    }

    private string GameRootOf(string versionId, string kind)
    {
        var dotMc = _c.DotMc;
        var profile = new InstanceSettingsStore(dotMc).Get(versionId);
        var root = InstanceLayout.GameRoot(dotMc, versionId,
            InstanceLayout.IsIsolated(profile, _c.Settings));
        return Path.Combine(root, kind switch
        {
            "resourcepacks" => "resourcepacks",
            "shaderpacks" => "shaderpacks",
            "saves" => "saves",
            "datapacks" => Path.Combine("world", "datapacks"),
            _ => "mods",
        });
    }

    private static object?[] ListFiles(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return [];
            return new DirectoryInfo(dir).GetFiles()
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => (object?)PluginApiCore.O(("name", f.Name), ("sizeBytes", f.Length)))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}

// ══════════════════════════════════════════════════════════════════════
//  ctx.game —— 游戏进程（launcher:game_launch）
// ══════════════════════════════════════════════════════════════════════

/// <summary>ctx.game（launcher:game_launch）：按当前账户启动实例、查状态、结束进程</summary>
public sealed class PluginGameApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>
    /// 当前游戏进程状态：{ running, versionId, pid, startedAt, uptimeSeconds }。
    /// 多开时取「运行状态页下拉选中的那一局」（跟随 SelectedPid），插件与界面看到的是同一局。
    /// </summary>
    public Dictionary<string, object?> Status()
    {
        _c.Require(PluginPermissions.GameLaunch, "查询游戏进程状态");
        var cur = GameRuntimeMonitor.Instance.Current;
        return cur is null
            ? _c.Obj(("running", false), ("versionId", null), ("pid", 0),
                ("startedAt", null), ("uptimeSeconds", 0))
            : _c.Obj(("running", true), ("versionId", cur.VersionId), ("pid", cur.Pid),
                ("startedAt", cur.StartedAt.ToString("o")),
                ("uptimeSeconds", (int)Math.Max(0, cur.Uptime.TotalSeconds)));
    }

    /// <summary>
    /// 启动一个实例。身份由启动器本体解析（插件拿不到、也传不了任何凭证）：
    /// 会话内缺凭证时启动器会提示需要重新登录，不会静默失败。
    /// </summary>
    public Dictionary<string, object?> Launch(string versionId)
    {
        _c.Require(PluginPermissions.GameLaunch, "启动游戏");
        return _c.ToolOk("launch_game", new { version_id = versionId });
    }

    /// <summary>
    /// 强制结束当前游戏进程（等同任务管理器结束 java 进程，未保存的进度会丢）。
    /// 多开时结束「运行状态页下拉选中的那一局」，并标记为用户主动结束（不算崩溃）。
    /// </summary>
    public Dictionary<string, object?> Kill()
    {
        _c.Require(PluginPermissions.GameLaunch, "结束游戏进程");
        var monitor = GameRuntimeMonitor.Instance;
        var cur = monitor.Current;
        if (cur is null) return _c.Obj(("ok", false), ("message", "当前没有正在运行的游戏"));
        monitor.Kill(cur.Pid);
        return _c.Obj(("ok", true), ("message", $"已结束 {cur.VersionId}（pid {cur.Pid}）"));
    }

    /// <summary>某实例最近的游戏日志尾部（先补齐行数上限，再由启动器读取）</summary>
    public string Log(string? versionId = null, int lines = 40)
    {
        _c.Require(PluginPermissions.LogsRead, "读取游戏日志");
        return _c.Tool("read_game_log",
            new { version_id = versionId, lines = Math.Clamp(lines, 1, 200) });
    }

    /// <summary>当前正在运行的实例是否有窗口（进程存活即 true）</summary>
    public bool IsRunning()
    {
        _c.Require(PluginPermissions.GameLaunch, "查询游戏进程状态");
        return GameSessionGuard.IsAnyGameAlive(_c.DotMc);
    }
}

// ══════════════════════════════════════════════════════════════════════
//  ctx.content —— 社区资源（launcher:content_read / content_write）
// ══════════════════════════════════════════════════════════════════════

/// <summary>ctx.content（launcher:content_read / content_write）：Modrinth 与 CurseForge 资源</summary>
public sealed class PluginContentApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>搜索资源；kind = mod / resourcepack / shader / datapack / modpack</summary>
    public string Search(string query, string kind = "mod", string source = "modrinth",
        string? gameVersion = null, string? loader = null)
    {
        _c.Require(PluginPermissions.ContentRead, "搜索社区资源");
        return source.Equals("curseforge", StringComparison.OrdinalIgnoreCase)
            ? _c.Tool("search_curseforge", new { query, kind, game_version = gameVersion })
            : _c.Tool("search_modrinth", new { query, kind, game_version = gameVersion, loader });
    }

    /// <summary>把中文资源名翻成英文（两个平台都只有英文项目名）</summary>
    public string Translate(string query)
    {
        _c.Require(PluginPermissions.ContentRead, "翻译搜索词");
        return _c.Tool("translate_query", new { query });
    }

    /// <summary>本地资源库清单（可按类型 / 关键字过滤）</summary>
    public string Library(string? kind = null, string? query = null)
    {
        _c.Require(PluginPermissions.ContentRead, "读取资源库");
        return _c.Tool("list_content_library", new { kind, query });
    }

    /// <summary>扫描实例已装资源，列出可更新项</summary>
    public string CheckUpdates(string? versionId = null)
    {
        _c.Require(PluginPermissions.ContentRead, "检查资源更新");
        return _c.Tool("check_content_updates", new { version_id = versionId });
    }

    /// <summary>安装一个资源到实例（projectId 来自 Search 结果的 ID 或 slug）</summary>
    public Dictionary<string, object?> Install(string projectId, string kind = "mod",
        string source = "modrinth", string? versionNumber = null, string? targetVersion = null)
    {
        _c.Require(PluginPermissions.ContentWrite, "安装资源");
        return _c.ToolOk("install_content", new
        {
            project_id = projectId, kind, source,
            version_number = versionNumber, target_version = targetVersion,
        });
    }

    /// <summary>把资源库里已有的一份文件装载到实例（不重新下载）</summary>
    public Dictionary<string, object?> Enable(string fileName, string kind = "mod",
        string? targetVersion = null)
    {
        _c.Require(PluginPermissions.ContentWrite, "装载资源库文件");
        return _c.ToolOk("enable_content",
            new { file_name = fileName, kind, target_version = targetVersion });
    }

    /// <summary>从资源库删除一份文件（已装载到实例的副本不受影响）</summary>
    public Dictionary<string, object?> RemoveFromLibrary(string fileName, string kind = "mod")
    {
        _c.Require(PluginPermissions.ContentWrite, "删除资源库文件");
        return _c.ToolOk("remove_from_library", new { file_name = fileName, kind });
    }

    /// <summary>安装本地整合包文件（.mrpack / CurseForge 导出 zip）</summary>
    public Dictionary<string, object?> InstallModpack(string path, string? name = null,
        string? targetVersion = null)
    {
        _c.Require(PluginPermissions.ContentWrite, "安装整合包");
        return _c.ToolOk("install_modpack", new { path, name, target_version = targetVersion });
    }
}

// ══════════════════════════════════════════════════════════════════════
//  ctx.download / ctx.http —— 下载与网络
// ══════════════════════════════════════════════════════════════════════

/// <summary>ctx.download（launcher:download）：走启动器多源下载引擎把文件拉进插件 data 目录</summary>
public sealed class PluginDownloadApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>
    /// 下载到插件自身 data 目录（沙箱内，写不到别处）。sha1 非空时校验，不匹配抛错。
    /// 返回落盘绝对路径。
    /// </summary>
    public string File(string url, string fileName, string? sha1 = null)
    {
        _c.Require(PluginPermissions.Download, "下载文件");
        var download = _c.Services.Download
            ?? throw new InvalidOperationException("当前宿主没有提供下载服务");
        var dest = _c.SafePath(fileName);
        PluginSync.Run(() => download.DownloadFileAsync(url, dest, sha1));
        return dest;
    }

    /// <summary>拉一段文本（http / https），返回字符串正文</summary>
    public string Text(string url)
    {
        var r = _c.HttpRequest("GET", url, null, null);
        return r["body"] as string ?? "";
    }
}

/// <summary>ctx.http（launcher:network）：插件自己的 HTTP 请求</summary>
public sealed class PluginHttpApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>GET；返回 { status, ok, contentType, body }</summary>
    public Dictionary<string, object?> Get(string url) => _c.HttpRequest("GET", url, null, null);

    /// <summary>POST JSON 正文；返回 { status, ok, contentType, body }</summary>
    public Dictionary<string, object?> PostJson(string url, string jsonBody) =>
        _c.HttpRequest("POST", url, jsonBody, "application/json");

    /// <summary>POST 任意正文（contentType 默认 text/plain）</summary>
    public Dictionary<string, object?> Post(string url, string body, string? contentType = null) =>
        _c.HttpRequest("POST", url, body, contentType ?? "text/plain");
}
