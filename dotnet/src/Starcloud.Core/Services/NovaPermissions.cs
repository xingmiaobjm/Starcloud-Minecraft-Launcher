// Nova 动手许可层（设计文档第 4 节）：手动开关默认关闭，
// 开启 = 显式授权（开启命令进确认清单）；越界即停的边界写入每档开关描述。
// 可审计流水（nova_actions）：谁动了、动了什么、何时，全部记录，保证「能动手且每次都能被看到干了啥」。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>
/// 许可级别：从关到完全放开四档。
/// <para>Off = 关；Confirm = 变更前确认（每次动手都先列清单）；Auto = 自主模式（直接执行，
/// 但仍守代码里的硬边界）；Full = 完全访问（连路径白名单也放开）。</para>
/// </summary>
public enum NovaPermissionLevel
{
    Off = 0,
    Confirm = 1,
    Auto = 2,
    Full = 3,
}

/// <summary>单档许可（Key: recording / computer / game），带级别</summary>
public sealed record NovaPermission(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("on")] bool Enabled,
    [property: JsonPropertyName("granted_at")] DateTime? GrantedAt = null,
    /// <summary>0=关 1=确认 2=自主 3=完全；旧文件没有这个字段 → 用 on 推断</summary>
    [property: JsonPropertyName("level")] int Level = -1)
{
    public NovaPermissionLevel Resolve() => Level >= 0
        ? (NovaPermissionLevel)Math.Clamp(Level, 0, 3)
        : Enabled ? NovaPermissionLevel.Confirm : NovaPermissionLevel.Off;
}

/// <summary>许可层持久化（%APPDATA%/starcloud-launcher/nova.permissions.json）</summary>
public sealed class NovaPermissionStore
{
    /// <summary>录屏：游戏运行时从显卡缓冲区采集画面并硬件编码落盘，不碰非游戏窗口</summary>
    public const string Recording = "recording";

    /// <summary>操控电脑：移动/点击鼠标、打字、按键、打开网址或本地路径、切窗口</summary>
    public const string Computer = "computer";

    /// <summary>临时接管游戏：聚焦 Minecraft 窗口后在它的客户区里点击/按键</summary>
    public const string GameControl = "game";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private Dictionary<string, NovaPermission> _state = [];

    public NovaPermissionStore() => Load();

    /// <summary>查询某档级别（默认关）</summary>
    public NovaPermissionLevel Level(string key) =>
        _state.TryGetValue(key, out var p) ? p.Resolve() : NovaPermissionLevel.Off;

    /// <summary>是否已授权（确认及以上）</summary>
    public bool IsOn(string key) => Level(key) >= NovaPermissionLevel.Confirm;

    /// <summary>该档是否允许不经确认直接执行（自主及以上）</summary>
    public bool AllowsAutoRun(string key) => Level(key) >= NovaPermissionLevel.Auto;

    /// <summary>该档是否完全访问（放开路径等硬边界）</summary>
    public bool IsFullAccess(string key) => Level(key) == NovaPermissionLevel.Full;

    public static string LevelText(NovaPermissionLevel level) => level switch
    {
        NovaPermissionLevel.Off => "关闭",
        NovaPermissionLevel.Confirm => "变更前确认",
        NovaPermissionLevel.Auto => "自主模式",
        NovaPermissionLevel.Full => "完全访问",
        _ => "关闭",
    };

    /// <summary>设置档位（UI 侧必须先弹确认清单再调用）；写一条许可记录。</summary>
    public Task SetLevelAsync(string key, NovaPermissionLevel level, NovaActionLog log,
        CancellationToken ct = default) =>
        SaveAsync(key, level >= NovaPermissionLevel.Confirm, level, log, ct);

    /// <summary>设置开关状态（UI 侧必须先弹确认清单再调用）；写一条许可记录</summary>
    public Task SetAsync(string key, bool on, NovaActionLog log, CancellationToken ct = default) =>
        SaveAsync(key, on, on ? NovaPermissionLevel.Confirm : NovaPermissionLevel.Off, log, ct);

    private async Task SaveAsync(string key, bool on, NovaPermissionLevel level, NovaActionLog log,
        CancellationToken ct)
    {
        _state[key] = new NovaPermission(key, on, on ? DateTime.Now : null, (int)level);
        await Task.Run(() =>
        {
            LauncherPaths.EnsureDataDir();
            File.WriteAllText(LauncherPaths.NovaPermissionsFile,
                JsonSerializer.Serialize(_state.Values.OrderBy(p => p.Key), JsonOpts));
        }, ct);
        var (title, _) = Describe(key);
        log.Append(new NovaActionEntry(
            Guid.NewGuid().ToString("N")[..12], DateTime.Now, "permission",
            $"{title}「{LevelText(level)}」",
            on ? "已授权" : "已撤销", "许可层状态变更"));
    }

    /// <summary>开关的展示名与边界说明（越界即停）</summary>
    public static (string Title, string Boundary) Describe(string key) => key switch
    {
        Recording => ("录屏", "只在游戏运行时从显卡缓冲区采集画面并硬件编码，录到本地磁盘；不碰非游戏窗口"),
        Computer => ("操控电脑",
            "会移动鼠标并点击、模拟键盘打字与按键、打开 http/https 网址或本机已存在的文件/目录、切换/最小化/关闭窗口；"
            + "不注入进程、不装钩子、不读剪贴板或密码；每一步都会先列清单再执行"),
        GameControl => ("临时接管游戏",
            "会先把 Minecraft 窗口切到前台，然后在它的窗口内点击与按键（例如点菜单、开背包、按 F3 看坐标）；"
            + "不会修改游戏文件、不会代替你保存世界；每一步都会先列清单再执行"),
        _ => (key, ""),
    };

    private void Load()
    {
        try
        {
            if (!File.Exists(LauncherPaths.NovaPermissionsFile)) return;
            var list = JsonSerializer.Deserialize<List<NovaPermission>>(
                File.ReadAllText(LauncherPaths.NovaPermissionsFile), JsonOpts);
            if (list is null) return;
            _state = list.ToDictionary(p => p.Key);
        }
        catch
        {
            _state = [];
        }
    }
}

/// <summary>一条操作日志（可审计：何时/类别/做了什么/结果）</summary>
public sealed record NovaActionEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("time")] DateTime Time,
    [property: JsonPropertyName("kind")] string Kind,      // plan/permission/fallback
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("state")] string State,    // 已确认执行/已取消/失败/已授权/已撤销
    [property: JsonPropertyName("detail")] string Detail)
{
    public string TimeText => Time.ToString("MM-dd HH:mm");
}

/// <summary>操作日志（%APPDATA%/starcloud-launcher/nova.actions.json，保留最近 200 条）</summary>
public sealed class NovaActionLog
{
    private const int MaxEntries = 200;
    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private List<NovaActionEntry> _entries = [];

    public NovaActionLog() => Load();

    public IReadOnlyList<NovaActionEntry> Entries =>
        _entries.OrderByDescending(e => e.Time).ToList();

    /// <summary>记录一条日志并落盘（失败静默：审计不可用时不得阻断正常流程）</summary>
    public void Append(NovaActionEntry entry)
    {
        _entries.Add(entry);
        if (_entries.Count > MaxEntries) _entries.RemoveRange(0, _entries.Count - MaxEntries);
        try
        {
            LauncherPaths.EnsureDataDir();
            File.WriteAllText(LauncherPaths.NovaActionsFile, JsonSerializer.Serialize(_entries, JsonOpts));
        }
        catch { /* 审计落盘失败不阻断 */ }
    }

    /// <summary>确认清单确认 → 执行 → 记日志；返回给用户看的结果摘要。
    /// <para>执行期间在调度中心登记一条「Nova · &lt;计划名&gt;」的任务：以前 Nova 的动作只在聊天区可见，
    /// 长计划（压缩存档 / 装整合包）跑到一半用户切走页面就完全看不到进度。
    /// 粒度刻意是「一条计划一条任务」——逐步登记会让抽屉被 Nova 的琐碎步骤淹没。</para></summary>
    public async Task<string> ExecuteConfirmedAsync(
        NovaPlan plan, CancellationToken ct = default)
    {
        var task = TaskCenterService.Instance.Begin($"Nova · {plan.Title}");
        // 调度中心的「取消」得能真的掐掉这次执行 → 把任务自己的 Cts 与调用方的 ct 串起来
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, task.Cts.Token);
        try
        {
            var result = await plan.ExecuteAsync(linked.Token);
            Append(new NovaActionEntry(plan.Id, DateTime.Now, "plan",
                plan.Title, "已确认执行", result));
            TaskCenterService.Instance.Complete(task);
            return result;
        }
        catch (OperationCanceledException)
        {
            Append(new NovaActionEntry(plan.Id, DateTime.Now, "plan",
                plan.Title, "已取消", "用户在调度中心取消了这次执行"));
            TaskCenterService.Instance.Cancel(task);
            throw;
        }
        catch (Exception ex)
        {
            Append(new NovaActionEntry(plan.Id, DateTime.Now, "plan",
                plan.Title, "失败", ex.Message));
            TaskCenterService.Instance.Fail(task, ex.Message);
            throw;
        }
    }

    /// <summary>确认清单被取消 → 记日志</summary>
    public void LogCancelled(NovaPlan plan) =>
        Append(new NovaActionEntry(plan.Id, DateTime.Now, "plan",
            plan.Title, "已取消", "用户在确认清单上点了取消"));

    public void Clear()
    {
        _entries.Clear();
        try
        {
            if (File.Exists(LauncherPaths.NovaActionsFile)) File.Delete(LauncherPaths.NovaActionsFile);
        }
        catch { /* 清理失败不阻断 */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(LauncherPaths.NovaActionsFile)) return;
            _entries = JsonSerializer.Deserialize<List<NovaActionEntry>>(
                File.ReadAllText(LauncherPaths.NovaActionsFile), JsonOpts) ?? [];
        }
        catch
        {
            _entries = [];
        }
    }
}
