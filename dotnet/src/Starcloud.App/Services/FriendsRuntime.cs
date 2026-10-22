// 好友实时通道：登录后常驻一条 /v1/friends/events SSE，断线指数退避重连。
//
// 事件在后台线程到达：这里只做两件事——转成 NotificationCenter 通知，
// 以及把原始事件抛给 UI 订阅者（订阅者自己 DispatcherQueue.TryEnqueue 回 UI 线程）。
// 退出登录 / 应用退出时取消循环。

using System.Text.Json;
using Starcloud.App.Localization;
using Starcloud.Core.Services;

namespace Starcloud.App.Services;

public sealed class FriendsRuntime
{
    private readonly FriendsService _friends;
    private readonly StarcloudIdService _identity;
    private CancellationTokenSource? _cts;
    private int _started;

    /// <summary>原始事件（后台线程）；invite / friend_request / friend_message 等</summary>
    public event Action<FriendEvent>? EventReceived;

    public FriendsRuntime(FriendsService friends, StarcloudIdService identity)
    {
        _friends = friends;
        _identity = identity;
    }

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _identity.SessionChanged += Restart;
        Restart();
    }

    private void Restart()
    {
        _cts?.Cancel();
        _cts?.Dispose();
        _cts = null;
        if (!_identity.IsLoggedIn) return;

        var cts = new CancellationTokenSource();
        _cts = cts;
        _ = Task.Run(() => LoopAsync(cts.Token));
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _friends.StreamEventsAsync(HandleAsync, ct);
                delay = TimeSpan.FromSeconds(3);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                LauncherLog.Write("friends", $"事件连接断开：{ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                await Task.Delay(delay, ct);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 60));
        }
    }

    private Task HandleAsync(FriendEvent ev)
    {
        Notify(ev);
        EventReceived?.Invoke(ev);
        return Task.CompletedTask;
    }

    private static void Notify(FriendEvent ev)
    {
        var name = DisplayNameOf(ev.Data);
        switch (ev.Type)
        {
            case "friend_request":
                NotificationCenter.Instance.Info(Loc.T("{0} 请求添加你为好友", name));
                break;
            case "friend_request_accepted":
                NotificationCenter.Instance.Info(Loc.T("{0} 接受了你的好友申请", name));
                break;
            case "invite":
                NotificationCenter.Instance.Info(Loc.T("{0} 邀请你联机", name));
                break;
            case "invite_accepted":
                NotificationCenter.Instance.Info(Loc.T("{0} 接受了你的联机邀请", name));
                break;
            case "invite_declined":
                NotificationCenter.Instance.Info(Loc.T("{0} 拒绝了你的联机邀请", name));
                break;
            case "invite_canceled":
                NotificationCenter.Instance.Info(Loc.T("{0} 撤回了联机邀请", name));
                break;
        }
    }

    /// <summary>事件数据里取对方昵称：邀请/申请带 user.display_name，接受类事件直接给资料对象。</summary>
    private static string DisplayNameOf(JsonElement data)
    {
        try
        {
            if (data.ValueKind == JsonValueKind.Object)
            {
                if (data.TryGetProperty("user", out var user) &&
                    user.ValueKind == JsonValueKind.Object &&
                    user.TryGetProperty("display_name", out var nested) &&
                    nested.ValueKind == JsonValueKind.String)
                    return nested.GetString() is { Length: > 0 } n ? n : Loc.T("好友");
                if (data.TryGetProperty("display_name", out var direct) &&
                    direct.ValueKind == JsonValueKind.String)
                    return direct.GetString() is { Length: > 0 } d ? d : Loc.T("好友");
            }
        }
        catch
        {
            // 事件结构异常：用通用称呼
        }
        return Loc.T("好友");
    }
}
