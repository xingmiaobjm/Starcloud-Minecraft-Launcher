// Nova 动手许可层（设计文档第 4 节）：手动开关默认关闭，
// 开启 = 显式授权（开启命令进确认清单）；越界即停的边界写入每档开关描述。
// 可审计流水（nova_actions）：谁动了、动了什么、何时，全部记录，保证「能动手且每次都能被看到干了啥」。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>单档许可开关（Key: recording）</summary>
public sealed record NovaPermission(
    [property: JsonPropertyName("key")] string Key,
    [property: JsonPropertyName("on")] bool Enabled,
    [property: JsonPropertyName("granted_at")] DateTime? GrantedAt = null);

/// <summary>许可层持久化（%APPDATA%/starcloud-launcher/nova.permissions.json）</summary>
public sealed class NovaPermissionStore
{
    /// <summary>录屏：游戏运行时从显卡缓冲区采集画面并硬件编码落盘，不碰非游戏窗口</summary>
    public const string Recording = "recording";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    private Dictionary<string, NovaPermission> _state = [];

    public NovaPermissionStore() => Load();

    /// <summary>查询某档开关状态（默认关闭）</summary>
    public bool IsOn(string key) => _state.TryGetValue(key, out var p) && p.Enabled;

    /// <summary>设置开关状态（UI 侧必须先弹确认清单再调用）；写一条许可记录</summary>
    public async Task SetAsync(string key, bool on, NovaActionLog log, CancellationToken ct = default)
    {
        _state[key] = new NovaPermission(key, on, on ? DateTime.Now : null);
        await Task.Run(() =>
        {
            LauncherPaths.EnsureDataDir();
            File.WriteAllText(LauncherPaths.NovaPermissionsFile,
                JsonSerializer.Serialize(_state.Values.OrderBy(p => p.Key), JsonOpts));
        }, ct);
        var (title, _) = Describe(key);
        log.Append(new NovaActionEntry(
            Guid.NewGuid().ToString("N")[..12], DateTime.Now, "permission",
            $"{title} 开关「{(on ? "开启" : "关闭")}」",
            on ? "已授权" : "已撤销", "许可层状态变更"));
    }

    /// <summary>开关的展示名与边界说明（越界即停）</summary>
    public static (string Title, string Boundary) Describe(string key) => key switch
    {
        Recording => ("录屏", "只在游戏运行时从显卡缓冲区采集画面并硬件编码，录到本地磁盘；不碰非游戏窗口"),
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
