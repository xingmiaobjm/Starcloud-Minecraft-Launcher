// 插件能力层（续）：设置 / 账户 / 日志 / 任务 / 存档 / 联机 / 外观 / 更新 / 存储 / 系统 / 事件。
// 与 PluginApis.cs 同一套规则：权限两道门、身份不可触碰、复杂动作转交共用执行层。

using System.Text.Json;
using System.Text.Json.Nodes;
using Starcloud.Core.Services;

namespace Starcloud.Core.Plugins;

// ══════════════════════════════════════════════════════════════════════
//  ctx.settings / ctx.accounts / ctx.logs / ctx.tasks
// ══════════════════════════════════════════════════════════════════════

/// <summary>ctx.settings（launcher:settings_read / settings_write）：启动器全局设置</summary>
public sealed class PluginSettingsApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>
    /// 读取设置。主机私有 / 敏感项（模型 Key、模型地址、CurseForge Key、更新源地址）
    /// 一律不下发，其键名在返回值的 filtered 字段里明示，插件不必猜。
    /// </summary>
    public Dictionary<string, object?> Get()
    {
        _c.Require(PluginPermissions.SettingsRead, "读取启动器设置");
        var node = JsonNode.Parse(JsonSerializer.Serialize(_c.Settings, JsonOpts)) as JsonObject
                   ?? [];
        var result = _c.Obj(("filtered", PluginApiCore.HostOnlySettingKeys));
        foreach (var key in PluginApiCore.HostOnlySettingKeys) node.Remove(key);
        foreach (var prop in node) result[prop.Key] = PluginApiCore.Plain(prop.Value);
        return result;
    }

    /// <summary>
    /// 修改设置：settingsJson 是 JSON 对象，只接受白名单键（与 Nova 的 update_settings 同一批），
    /// 主机私有键一律拒绝——插件改不了模型密钥，也改不了更新源地址。
    /// </summary>
    public Dictionary<string, object?> Set(string settingsJson)
    {
        _c.Require(PluginPermissions.SettingsWrite, "修改启动器设置");
        var patch = PluginApiCore.ParseArgs(settingsJson);
        if (patch.ValueKind != JsonValueKind.Object)
            throw new ArgumentException("settingsJson 必须是 JSON 对象");

        var unknown = patch.EnumerateObject()
            .Select(p => p.Name)
            .Where(k => !AllowedKeys.Contains(k))
            .ToArray();
        if (unknown.Length > 0)
            throw new UnauthorizedAccessException($"字段不允许由插件修改：{string.Join("、", unknown)}");

        var node = JsonNode.Parse(patch.GetRawText())!;
        return _c.ToolOk("update_settings", node);
    }

    /// <summary>允许插件写的设置键（与 Nova update_settings 的白名单一致）</summary>
    internal static readonly string[] AllowedKeys =
    [
        "download_source", "download_concurrency", "download_max_retries",
        "download_custom_mirror_url",
        "default_min_memory_mb", "default_max_memory_mb", "last_version",
        "version_isolated", "java_executable", "use_builtin_jvm_args",
        "theme", "language", "ui_language", "ui_animations", "ui_motion_level", "account_walk_animation",
        "device_link_enabled", "device_name", "p2p_download_enabled",
        "auto_update_enabled", "launch_auto_retry", "show_game_log_on_launch",
        "resource_cache_enabled", "microsoft_login_embedded",
        "background_mode", "background_builtin_id", "background_image_path",
        "background_image_opacity", "background_blur_radius", "background_mask_opacity",
        "background_follow_theme",
        "default_window_width", "default_window_height", "default_fullscreen",
        "extra_jvm_args", "global_env_vars",
    ];

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };
}

/// <summary>ctx.accounts（launcher:accounts_read）：只读身份列表，永不返回任何令牌</summary>
public sealed class PluginAccountsApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>
    /// 已保存账户：id（离线账户 = 名字派生 UUID）/ 名字 / 类型（microsoft / external / offline）/
    /// 是否默认 / 第三方服务器名。**没有令牌字段**——凭证只活在启动器内存里。
    /// </summary>
    public object?[] List()
    {
        _c.Require(PluginPermissions.AccountsRead, "读取账户列表");
        var store = _c.Services.Accounts;
        if (store is null) return [];
        return store.Accounts
            .Select(a => (object?)_c.Obj(
                ("id", a.Uuid),
                ("name", a.Username),
                ("type", a.Type),
                ("isDefault", a.IsDefault),
                ("server", a.ServerName ?? a.ServerBase ?? "")))
            .ToArray();
    }

    /// <summary>
    /// 当前启动会用的身份（名字 / 类型 / 是否就绪）。会话内没登录时 ready=false 并给出原因，
    /// 插件可以据此提示用户去账户页，但代替不了登录。
    /// </summary>
    public Dictionary<string, object?> Current()
    {
        _c.Require(PluginPermissions.AccountsRead, "读取当前启动身份");
        var svc = _c.Services.LaunchAccount;
        var mode = svc?.Mode ?? "microsoft";
        if (svc is null)
            return _c.Obj(("mode", mode), ("name", null), ("type", null), ("ready", false));
        try
        {
            var player = PluginSync.Run(() => svc.ResolveAsync());
            return _c.Obj(
                ("mode", mode),
                ("name", player.Name),
                ("type", player.ExternalBase is not null ? "external"
                    : player.Credentials?.UserType == "legacy" ? "offline" : "microsoft"),
                ("ready", true));
        }
        catch (Exception ex)
        {
            return _c.Obj(("mode", mode), ("name", null), ("type", null),
                ("ready", false), ("reason", ex.Message));
        }
    }
}

/// <summary>ctx.logs（launcher:logs_read）：读启动器日志、游戏日志、插件自身日志尾部</summary>
public sealed class PluginLogsApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>启动器日志尾部（当天文件；跨天时连前一天一起取，够看一次启动）</summary>
    public string Launcher(int lines = 100)
    {
        _c.Require(PluginPermissions.LogsRead, "读取启动器日志");
        var wanted = Math.Clamp(lines, 1, 2000);
        var all = new List<string>();
        foreach (var f in new[]
                 {
                     LauncherLog.CurrentFile,
                     Path.Combine(LauncherPaths.LogsDir,
                         $"launcher-{DateTime.Today.AddDays(-1):yyyy-MM-dd}.log"),
                 })
        {
            all.AddRange(TailLines(f, wanted));
        }
        return string.Join("\n", all.TakeLast(wanted));
    }

    /// <summary>启动器保存的游戏日志（最新一局）</summary>
    public string Game(int lines = 100)
    {
        _c.Require(PluginPermissions.LogsRead, "读取游戏日志");
        try
        {
            var dir = LauncherPaths.GameLogsDir;
            if (!Directory.Exists(dir)) return "";
            var latest = new DirectoryInfo(dir).GetFiles("*.log")
                .OrderByDescending(f => f.LastWriteTimeUtc)
                .FirstOrDefault();
            return latest is null
                ? ""
                : string.Join("\n", TailLines(latest.FullName, Math.Clamp(lines, 1, 2000)));
        }
        catch
        {
            return "";
        }
    }

    /// <summary>插件自身的运行日志（plugins/logs/&lt;id&gt;.log）</summary>
    public string Plugin(int lines = 100)
    {
        var file = Path.Combine(LauncherPaths.PluginLogsDir, _c.PluginId + ".log");
        return string.Join("\n", TailLines(file, Math.Clamp(lines, 1, 2000)));
    }

    private static IEnumerable<string> TailLines(string file, int lines)
    {
        try
        {
            if (!File.Exists(file)) return [];
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(fs);
            var queue = new Queue<string>(lines);
            while (reader.ReadLine() is { } line)
            {
                if (queue.Count == lines) queue.Dequeue();
                queue.Enqueue(line);
            }
            return queue;
        }
        catch
        {
            return [];
        }
    }
}

/// <summary>ctx.tasks（launcher:tasks）：任务中心（下载 / 安装进度）</summary>
public sealed class PluginTasksApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>任务快照：标题、状态、进度、详情（含下载速度）</summary>
    public object?[] List()
    {
        _c.Require(PluginPermissions.Tasks, "读取任务中心");
        return TaskCenterService.Instance.Snapshot()
            .Select(t => (object?)_c.Obj(
                ("id", t.Id), ("title", t.Title), ("state", t.State.ToString()),
                ("progress", t.Progress), ("detail", t.Detail)))
            .ToArray();
    }

    /// <summary>取消一个进行中的任务；不传 id 时取消全部进行中的任务</summary>
    public Dictionary<string, object?> Cancel(string? taskId = null)
    {
        _c.Require(PluginPermissions.Tasks, "取消任务");
        return _c.ToolOk("cancel_task", new { task_id = taskId, all = taskId is null });
    }

    /// <summary>清空任务记录（先取消进行中的任务）</summary>
    public Dictionary<string, object?> Clear()
    {
        _c.Require(PluginPermissions.Tasks, "清空任务记录");
        return _c.ToolOk("clear_tasks", new { });
    }
}

// ══════════════════════════════════════════════════════════════════════
//  ctx.saves / ctx.multiplayer / ctx.appearance / ctx.updates
// ══════════════════════════════════════════════════════════════════════

/// <summary>ctx.saves（launcher:saves_read / saves_write）：存档与实例备份</summary>
public sealed class PluginSavesApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>所有实例的存档清单（名称 / 所属实例 / 大小 / 上次游玩）</summary>
    public string List()
    {
        _c.Require(PluginPermissions.SavesRead, "读取存档列表");
        return _c.Tool("list_saves", new { });
    }

    /// <summary>备份记录清单（id 供 Restore / DeleteBackup 使用）</summary>
    public string Backups(string? versionId = null)
    {
        _c.Require(PluginPermissions.SavesRead, "读取备份列表");
        return _c.Tool("list_backups", new { version_id = versionId });
    }

    /// <summary>备份一个实例（可含存档）</summary>
    public Dictionary<string, object?> BackupInstance(string versionId, string? name = null,
        bool includeSaves = true)
    {
        _c.Require(PluginPermissions.SavesWrite, "备份实例");
        return _c.ToolOk("backup_version",
            new { version_id = versionId, name, include_saves = includeSaves });
    }

    /// <summary>把某条备份还原回它的实例（覆盖同名文件）</summary>
    public Dictionary<string, object?> Restore(string backupId)
    {
        _c.Require(PluginPermissions.SavesWrite, "还原备份");
        return _c.ToolOk("restore_backup", new { backup_id = backupId });
    }

    /// <summary>删除一条备份记录与它的 zip</summary>
    public Dictionary<string, object?> DeleteBackup(string backupId)
    {
        _c.Require(PluginPermissions.SavesWrite, "删除备份");
        return _c.ToolOk("delete_backup", new { backup_id = backupId });
    }

    /// <summary>备份单个存档目录</summary>
    public Dictionary<string, object?> Backup(string saveName, string? versionId = null)
    {
        _c.Require(PluginPermissions.SavesWrite, "备份存档");
        return _c.ToolOk("backup_save", new { save_name = saveName, version_id = versionId });
    }

    /// <summary>删除单个存档目录（建议先 Backup）</summary>
    public Dictionary<string, object?> Delete(string saveName, string? versionId = null)
    {
        _c.Require(PluginPermissions.SavesWrite, "删除存档");
        return _c.ToolOk("delete_save", new { save_name = saveName, version_id = versionId });
    }

    /// <summary>把实例导出成可分享的 .mrpack</summary>
    public Dictionary<string, object?> ExportModpack(string? versionId = null, string? name = null)
    {
        _c.Require(PluginPermissions.SavesWrite, "导出整合包");
        return _c.ToolOk("export_modpack", new { version_id = versionId, name });
    }
}

/// <summary>ctx.multiplayer（launcher:multiplayer）：内置陶瓦联机与第三方联机 SDK</summary>
public sealed class PluginMultiplayerApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>联机状态：提供方、房间号、玩家数、节点地区、可用提供方清单</summary>
    public string Status()
    {
        _c.Require(PluginPermissions.Multiplayer, "读取联机状态");
        return _c.Tool("multiplayer_status", new { });
    }

    /// <summary>安装当前提供方的联机核心（首次联机前需要）</summary>
    public Dictionary<string, object?> InstallCore()
    {
        _c.Require(PluginPermissions.Multiplayer, "安装联机核心");
        return _c.ToolOk("install_multiplayer_core", new { });
    }

    /// <summary>开房（房主）：先在游戏里「对局域网开放」，再开房拿邀请码</summary>
    public Dictionary<string, object?> Host()
    {
        _c.Require(PluginPermissions.Multiplayer, "创建联机房间");
        return _c.ToolOk("multiplayer_host", new { });
    }

    /// <summary>用邀请码加入房间</summary>
    public Dictionary<string, object?> Join(string room)
    {
        _c.Require(PluginPermissions.Multiplayer, "加入联机房间");
        return _c.ToolOk("multiplayer_join", new { room });
    }

    /// <summary>结束联机；idleOnly=true 只把核心切回空闲，不退出房间</summary>
    public Dictionary<string, object?> Stop(bool idleOnly = false)
    {
        _c.Require(PluginPermissions.Multiplayer, "结束联机");
        return _c.ToolOk("multiplayer_stop", new { idle_only = idleOnly });
    }
}

/// <summary>ctx.appearance（launcher:appearance）：主题 / 背景 / 毛玻璃遮罩</summary>
public sealed class PluginAppearanceApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>
    /// 改外观，只传要改的项：theme（light / dark / system）、backgroundMode（none / builtin / file）、
    /// backgroundImagePath、opacity（0.2-1）、blurRadius（0-64）。
    /// </summary>
    public Dictionary<string, object?> Set(string? theme = null, string? backgroundMode = null,
        string? backgroundImagePath = null, double? opacity = null, int? blurRadius = null)
    {
        _c.Require(PluginPermissions.Appearance, "修改启动器外观");
        return _c.ToolOk("set_appearance", new
        {
            theme,
            background_mode = backgroundMode,
            background_image_path = backgroundImagePath,
            opacity,
            blur_radius = blurRadius,
        });
    }
}

/// <summary>ctx.updates（launcher:updates）：启动器自更新查询（只查不装）</summary>
public sealed class PluginUpdatesApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>检查启动器是否有新版本，返回远端版本号与更新说明</summary>
    public string Check()
    {
        _c.Require(PluginPermissions.Updates, "检查启动器更新");
        return _c.Tool("check_updates", new { });
    }
}

// ══════════════════════════════════════════════════════════════════════
//  ctx.storage / ctx.system / ctx.events
// ══════════════════════════════════════════════════════════════════════

/// <summary>ctx.storage（launcher:storage）：插件私有键值存储（data/storage.json，跨启动保留）</summary>
public sealed class PluginStorageApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;
    private readonly object _gate = new();

    /// <summary>读一个键；不存在返回 null</summary>
    public object? Get(string key)
    {
        _c.Require(PluginPermissions.Storage, "读取插件存储");
        lock (_gate) return PluginApiCore.Plain(ReadCore()[key]);
    }

    /// <summary>写一个键（值可以是任意可 JSON 化的内容）</summary>
    public void Set(string key, object? value)
    {
        _c.Require(PluginPermissions.Storage, "写入插件存储");
        if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key 不能为空");
        lock (_gate)
        {
            var obj = ReadCore();
            obj[key] = value is null ? null : JsonNode.Parse(PluginApiCore.Json(value));
            WriteCore(obj);
        }
    }

    /// <summary>删除一个键；返回此前是否存在</summary>
    public bool Delete(string key)
    {
        _c.Require(PluginPermissions.Storage, "写入插件存储");
        lock (_gate)
        {
            var obj = ReadCore();
            var removed = obj.Remove(key);
            if (removed) WriteCore(obj);
            return removed;
        }
    }

    /// <summary>全部键名</summary>
    public string[] Keys()
    {
        _c.Require(PluginPermissions.Storage, "读取插件存储");
        lock (_gate) return ReadCore().Select(p => p.Key).ToArray();
    }

    /// <summary>清空插件存储</summary>
    public void Clear()
    {
        _c.Require(PluginPermissions.Storage, "写入插件存储");
        lock (_gate) WriteCore([]);
    }

    private JsonObject ReadCore()
    {
        try
        {
            var file = _c.SafePath("storage.json");
            if (!File.Exists(file)) return [];
            return JsonNode.Parse(File.ReadAllText(file)) as JsonObject ?? [];
        }
        catch
        {
            return []; // 文件被手改坏时按空处理，插件不至于彻底起不来
        }
    }

    private void WriteCore(JsonObject obj)
    {
        var json = obj.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        // 2.0 storage:kv(quota=) 的配额在这里强制（1.x 未声明配额 → 不限，保持原行为）
        if (_c.StorageQuotaKb is { } quotaKb &&
            System.Text.Encoding.UTF8.GetByteCount(json) > (long)quotaKb * 1024)
            throw new InvalidOperationException(
                $"插件私有存储已超过声明配额 {quotaKb} KB，写入被拒绝（请清理旧键或提高 quota）");

        var file = _c.SafePath("storage.json");
        var dir = Path.GetDirectoryName(file);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(file, json);
    }
}

/// <summary>ctx.system（launcher:clipboard / shell + ui_panel）：剪贴板、开链接、开目录、确认框、开页</summary>
public sealed class PluginSystemApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>读剪贴板文本（launcher:clipboard）</summary>
    public string? Clipboard()
    {
        _c.Require(PluginPermissions.Clipboard, "读取剪贴板");
        return _c.Bridge.ClipboardText();
    }

    /// <summary>写剪贴板文本（launcher:clipboard）</summary>
    public void Copy(string text)
    {
        _c.Require(PluginPermissions.Clipboard, "写入剪贴板");
        _c.Bridge.SetClipboardText(text ?? "");
    }

    /// <summary>用系统默认浏览器打开 http / https 链接（launcher:shell）</summary>
    public string OpenUrl(string url)
    {
        _c.Require(PluginPermissions.Shell, "打开链接");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            throw new ArgumentException("只允许打开 http / https 链接");
        return _c.Bridge.OpenUrl(uri.ToString());
    }

    /// <summary>在资源管理器里打开插件 data 目录（或其中子目录，launcher:shell）</summary>
    public string OpenFolder(string relative = "")
    {
        _c.Require(PluginPermissions.Shell, "打开目录");
        var rel = (relative ?? "").Trim();
        var path = rel.Length == 0 ? _c.DataDir : _c.SafePath(rel);
        if (!Directory.Exists(path)) Directory.CreateDirectory(path);
        return _c.Bridge.OpenPath(path);
    }

    /// <summary>弹确认框：用户点确认返回 true，取消返回 false（launcher:ui_panel）</summary>
    public bool Confirm(string title, string message)
    {
        _c.Require(PluginPermissions.UiPanel, "弹确认框");
        return _c.Bridge.Confirm(title ?? "", message ?? "") == true;
    }

    /// <summary>弹提示框（只有一个按钮，launcher:ui_panel）</summary>
    public void Alert(string title, string message)
    {
        _c.Require(PluginPermissions.UiPanel, "弹提示框");
        _c.Bridge.ShowInfo(title ?? "", message ?? "");
    }

    /// <summary>
    /// 切到启动器的某个页面（launcher:ui_panel）：
    /// home / versions / download / accounts / saves / logs / settings / nova / multiplayer / plugins
    /// </summary>
    public string OpenPage(string page)
    {
        _c.Require(PluginPermissions.UiPanel, "切换启动器页面");
        var allowed = new[]
        {
            "home", "versions", "download", "accounts", "saves", "logs",
            "settings", "nova", "multiplayer", "plugins",
        };
        var target = (page ?? "").Trim().ToLowerInvariant();
        if (!allowed.Contains(target))
            throw new ArgumentException($"未知页面：{page}（可用：{string.Join(" / ", allowed)}）");
        _c.Bridge.OpenPage(target);
        return $"已切换到 {target} 页面";
    }

    /// <summary>启动器版本号</summary>
    public string Version() => _c.Host.LauncherVersion;
}

/// <summary>ctx.events（launcher:events）：订阅启动器事件，由 onLauncherEvent 钩子接收</summary>
public sealed class PluginEventsApi(PluginApiCore c)
{
    private readonly PluginApiCore _c = c;

    /// <summary>本次事件载荷 { name, data }（仅 onLauncherEvent 钩子内非 null）</summary>
    public Dictionary<string, object?>? Current() =>
        _c.Event is null ? null : _c.Obj(("name", _c.Event.Name), ("data", _c.Event.Data));

    /// <summary>
    /// 订阅事件：game.launched（{versionId, pid}）/ game.exited（{versionId}）/ launcher.ready（{}）。
    /// 事件到达时宿主调用 onLauncherEvent(ctx)。
    /// </summary>
    public void On(string eventName)
    {
        _c.Require(PluginPermissions.Events, "订阅事件");
        if (string.IsNullOrWhiteSpace(eventName)) throw new ArgumentException("eventName 不能为空");
        _c.Host.SubscribeEvent(_c.PluginId, eventName.Trim());
    }

    /// <summary>取消订阅</summary>
    public void Off(string eventName)
    {
        _c.Require(PluginPermissions.Events, "退订事件");
        _c.Host.UnsubscribeEvent(_c.PluginId, (eventName ?? "").Trim());
    }

    /// <summary>当前已订阅的事件名</summary>
    public string[] Subscribed()
    {
        _c.Require(PluginPermissions.Events, "读取订阅列表");
        return _c.Host.Subscriptions(_c.PluginId);
    }
}
