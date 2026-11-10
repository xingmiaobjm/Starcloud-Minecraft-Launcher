using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core;
using Starcloud.Core.Models;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Multiplayer;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Bridge;


public static partial class Program
{

    private static async Task<object?> HandleFriendsListAsync(TextWriter stdout, JsonElement p)
    {
            return new
            {
                signedIn = _friends.IsSignedIn,
                friends = await _friends.GetFriendsAsync(),
            };
    }

    private static async Task<object?> HandleFriendsRequestsAsync(TextWriter stdout, JsonElement p)
    {
            return new
            {
                signedIn = _friends.IsSignedIn,
                requests = await _friends.GetRequestsAsync(),
            };
    }

    private static async Task<object?> HandleFriendsSearchAsync(TextWriter stdout, JsonElement p)
    {
            var results = await _friends.SearchAsync(p.GetProperty("query").GetString() ?? "");
            return new { results };
    }

    private static async Task<object?> HandleFriendsSendRequestAsync(TextWriter stdout, JsonElement p)
    {
            await _friends.SendRequestAsync(
                p.GetProperty("target").GetString() ?? "",
                p.TryGetProperty("message", out var reqMsg) ? reqMsg.GetString() : null);
            return new { ok = true };
    }

    private static async Task<object?> HandleFriendsAcceptRequestAsync(TextWriter stdout, JsonElement p)
    {
            await _friends.AcceptRequestAsync(p.GetProperty("requestId").GetString()!);
            return new { ok = true };
    }

    private static async Task<object?> HandleFriendsRejectRequestAsync(TextWriter stdout, JsonElement p)
    {
            await _friends.RejectRequestAsync(p.GetProperty("requestId").GetString()!);
            return new { ok = true };
    }

    private static async Task<object?> HandleFriendsCancelRequestAsync(TextWriter stdout, JsonElement p)
    {
            await _friends.CancelRequestAsync(p.GetProperty("requestId").GetString()!);
            return new { ok = true };
    }

    private static async Task<object?> HandleFriendsRemoveAsync(TextWriter stdout, JsonElement p)
    {
            await _friends.RemoveFriendAsync(p.GetProperty("userId").GetString()!);
            return new { ok = true };
    }

    private static async Task<object?> HandleFriendsInvitesAsync(TextWriter stdout, JsonElement p)
    {
            return new
            {
                signedIn = _friends.IsSignedIn,
                invites = await _friends.GetInvitesAsync(),
            };
    }

    private static async Task<object?> HandleFriendsCreateInviteAsync(TextWriter stdout, JsonElement p)
    {
            var invite = await _friends.CreateInviteAsync(
                p.GetProperty("userId").GetString() ?? "",
                p.TryGetProperty("address", out var invAddr) ? invAddr.GetString() : null,
                p.TryGetProperty("port", out var invPort) && invPort.ValueKind == JsonValueKind.Number
                    ? invPort.GetInt32()
                    : null,
                p.TryGetProperty("roomCode", out var invRoom) ? invRoom.GetString() : null,
                p.TryGetProperty("message", out var invMsg) ? invMsg.GetString() : null);
            return new { invite };
    }

    private static async Task<object?> HandleFriendsAcceptInviteAsync(TextWriter stdout, JsonElement p)
    {
            await _friends.AcceptInviteAsync(p.GetProperty("inviteId").GetString()!);
            return new { ok = true };
    }

    private static async Task<object?> HandleFriendsDeclineInviteAsync(TextWriter stdout, JsonElement p)
    {
            await _friends.DeclineInviteAsync(p.GetProperty("inviteId").GetString()!);
            return new { ok = true };
    }

    private static async Task<object?> HandleFriendsCancelInviteAsync(TextWriter stdout, JsonElement p)
    {
            await _friends.CancelInviteAsync(p.GetProperty("inviteId").GetString()!);
            return new { ok = true };
    }

    private static async Task<object?> HandleFriendsConversationAsync(TextWriter stdout, JsonElement p)
    {
            long? beforeId = p.TryGetProperty("beforeId", out var convBefore) &&
                            convBefore.ValueKind == JsonValueKind.Number
                ? convBefore.GetInt64()
                : null;
            var page = await _friends.GetConversationAsync(
                p.GetProperty("userId").GetString()!, beforeId);
            return new { page };
    }

    private static async Task<object?> HandleFriendsSendAsync(TextWriter stdout, JsonElement p)
    {
            var message = await _friends.SendMessageAsync(
                p.GetProperty("userId").GetString()!,
                p.GetProperty("content").GetString() ?? "");
            return new { message };
    }

    private static async Task<object?> HandleFriendsMarkReadAsync(TextWriter stdout, JsonElement p)
    {
        {
            var updated = await _friends.MarkReadAsync(p.GetProperty("userId").GetString()!);
            return new { updated };
        }

        // ── 群聊（Win 0.4.0 同口径；附件协议与好友消息一致） ──
    }

    private static async Task<object?> HandleFriendsGroupsAsync(TextWriter stdout, JsonElement p)
    {
            return new
            {
                signedIn = _friends.IsSignedIn,
                groups = await _friends.GetGroupsAsync(),
            };
    }

    private static async Task<object?> HandleFriendsGroupDetailAsync(TextWriter stdout, JsonElement p)
    {
            return new
            {
                group = await _friends.GetGroupDetailAsync(
                    p.GetProperty("groupId").GetString()!),
            };
    }

    private static async Task<object?> HandleFriendsGroupMessagesAsync(TextWriter stdout, JsonElement p)
    {
            long? beforeId = p.TryGetProperty("beforeId", out var gmBefore) &&
                            gmBefore.ValueKind == JsonValueKind.Number
                ? gmBefore.GetInt64()
                : null;
            var items = await _friends.GetGroupMessagesAsync(
                p.GetProperty("groupId").GetString()!, beforeId);
            return new { items };
    }

    private static async Task<object?> HandleFriendsGroupSendAsync(TextWriter stdout, JsonElement p)
    {
            var message = await _friends.SendGroupMessageAsync(
                p.GetProperty("groupId").GetString()!,
                p.GetProperty("content").GetString() ?? "");
            return new { message };
    }

    private static async Task<object?> HandleFriendsGroupMarkReadAsync(TextWriter stdout, JsonElement p)
    {
            await _friends.MarkGroupReadAsync(p.GetProperty("groupId").GetString()!);
            return new { ok = true };
    }

    private static async Task<object?> HandleFriendsGroupCreateAsync(TextWriter stdout, JsonElement p)
    {
            var members = p.GetProperty("members").EnumerateArray()
                .Select(m => m.GetString() ?? "").Where(m => m.Length > 0).ToList();
            var group = await _friends.CreateGroupAsync(
                p.GetProperty("name").GetString() ?? "", members);
            return new { group };
    }

    private static async Task<object?> HandleFriendsGroupAddMembersAsync(TextWriter stdout, JsonElement p)
    {
            var members = p.GetProperty("members").EnumerateArray()
                .Select(m => m.GetString() ?? "").Where(m => m.Length > 0).ToList();
            await _friends.AddGroupMembersAsync(p.GetProperty("groupId").GetString()!, members);
            return new { ok = true };
    }

    private static async Task<object?> HandleFriendsGroupDissolveAsync(TextWriter stdout, JsonElement p)
    {
        {
            await _friends.DissolveGroupAsync(p.GetProperty("groupId").GetString()!);
            return new { ok = true };
        }

        // ── 文件传输（图片 ≤30MB / 视频·文件 ≤200MB；消息 = 前缀 + JSON，后端只管存取） ──
    }

    private static async Task<object?> HandleFriendsUploadFileAsync(TextWriter stdout, JsonElement p)
    {
            var filePath = p.GetProperty("path").GetString()!;
            // kind 可选：不给按扩展名判定（与 Win 端 FriendsPage 同口径）
            var kind = p.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String
                ? k.GetString()!
                : KindOfFile(filePath);
            var (id, size) = await _friends.UploadFileAsync(filePath);
            return new
            {
                id,
                size,
                kind,
                content = FriendsService.EncodeFileMessage(
                    Path.GetFileName(filePath), size, kind, id),
            };
    }

    private static async Task<object?> HandleFriendsDownloadFileAsync(TextWriter stdout, JsonElement p)
    {
            await _friends.DownloadFileAsync(
                p.GetProperty("fileId").GetString()!,
                p.GetProperty("destPath").GetString()!);
            return new { ok = true };
    }

    /// <summary>拉起好友 SSE 实时事件循环（转发为 friends.event 桥接事件）。</summary>
    private static void StartFriendsEvents(TextWriter writer)
    {
        StopFriendsEvents();
        var cts = new CancellationTokenSource();
        FriendsEventsCts = cts;
        _ = Task.Run(() => FriendsEventsLoopAsync(writer, cts.Token));
    }

    private static void StopFriendsEvents()
    {
        var old = Interlocked.Exchange(ref FriendsEventsCts, null);
        old?.Cancel();
        old?.Dispose();
    }

    /// <summary>SSE 长连接循环：断线指数退避（3s 起步，封顶 60s），与 Win FriendsRuntime 同策略。</summary>
    private static async Task FriendsEventsLoopAsync(TextWriter writer, CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(3);
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await _friends.StreamEventsAsync(ev =>
                {
                    Emit(writer, "friends.event", new { type = ev.Type, data = ev.Data });
                    return Task.CompletedTask;
                }, ct);
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

    /// <summary>按扩展名判定附件类型（与 Win 端 FriendsPage 同口径）。</summary>
    private static string KindOfFile(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ext switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => "image",
            ".mp4" or ".mkv" or ".mov" or ".avi" or ".webm" or ".flv" => "video",
            _ => "file",
        };
    }
}
