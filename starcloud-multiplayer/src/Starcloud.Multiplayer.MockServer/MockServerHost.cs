using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Starcloud.Multiplayer.Core.Protocol;

namespace Starcloud.Multiplayer.MockServer;

/// <summary>跑起来的 mock 服务端：测试与 CLI 都通过它拿到实际监听地址。</summary>
public sealed record MockServerHandle(WebApplication App, string BaseAddress) : IAsyncDisposable
{
    public async ValueTask DisposeAsync()
    {
        await App.StopAsync();
        await App.DisposeAsync();
    }
}

/// <summary>
/// 本地模拟云端。**契约与将来真云端完全一致**（路径、字段、错误码），
/// 所以客户端只需要换一个 base url 就能切到真服务端。
/// </summary>
public static class MockServerHost
{
    /// <param name="port">0 表示让系统分配一个空闲端口。</param>
    public static async Task<MockServerHandle> StartAsync(
        string seedFile, string statePath, int port = 0, CancellationToken ct = default)
    {
        var store = new MockStore(seedFile, statePath);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, port));
        // 默认安静：mock 是拿来验证客户端的，逐条打印请求会把自检输出淹掉。
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        // 契约字段是 snake_case，自动绑定必须跟着它走，否则请求体全部落空。
        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.PropertyNamingPolicy = MpJson.Options.PropertyNamingPolicy;
            options.SerializerOptions.PropertyNameCaseInsensitive = true;
        });

        var app = builder.Build();
        var group = app.MapGroup(MpProtocol.BasePath);

        group.MapGet("/friends", (HttpContext ctx) =>
            WithAuth(ctx, store, userId => From(store.Friends(userId))));

        group.MapPost("/presence", async (HttpContext ctx) =>
        {
            if (!TryAuth(ctx, store, out var userId)) return Unauthorized();
            var request = await ReadJsonAsync<PresenceRequest>(ctx) ?? new PresenceRequest(null);
            return From(store.TouchPresence(userId, request.RoomId));
        });

        group.MapGet("/rooms", (HttpContext ctx) =>
            WithAuth(ctx, store, _ => From(store.PublicRooms())));

        group.MapPost("/rooms", async (HttpContext ctx) =>
        {
            if (!TryAuth(ctx, store, out var userId)) return Unauthorized();
            var request = await ReadJsonAsync<CreateRoomRequest>(ctx);
            if (request is null) return Err(MpProtocol.ErrInvalidRequest, "请求体为空");
            return From(store.CreateRoom(userId, request));
        });

        group.MapDelete("/rooms/{roomId}", (HttpContext ctx, string roomId) =>
            WithAuth(ctx, store, userId => From(store.CloseRoom(userId, roomId))));

        group.MapPost("/rooms/{roomId}/join", (HttpContext ctx, string roomId) =>
            WithAuth(ctx, store, userId => From(store.JoinRoom(userId, roomId))));

        group.MapGet("/invites", (HttpContext ctx) =>
            WithAuth(ctx, store, userId => From(store.PendingInvites(userId))));

        group.MapPost("/invites", async (HttpContext ctx) =>
        {
            if (!TryAuth(ctx, store, out var userId)) return Unauthorized();
            var request = await ReadJsonAsync<InviteRequest>(ctx);
            if (request is null) return Err(MpProtocol.ErrInvalidRequest, "请求体为空");
            return From(store.CreateInvite(userId, request));
        });

        group.MapPost("/invites/{inviteId}/accept", (HttpContext ctx, string inviteId) =>
            WithAuth(ctx, store, userId => From(store.AcceptInvite(userId, inviteId))));

        group.MapPost("/invites/{inviteId}/reject", (HttpContext ctx, string inviteId) =>
            WithAuth(ctx, store, userId => From(store.RejectInvite(userId, inviteId))));

        // 仅 mock 存在：云端通道下客户端登录后把 api_key → user_id 登记进来。
        // 真云端用 ApiKey 自身的权威解析，不存在这个端点。
        app.MapPost("/v1/multiplayer/_mock/keys", async (HttpContext ctx) =>
        {
            var request = await ReadJsonAsync<MockKeyRequest>(ctx);
            if (request is null) return Err(MpProtocol.ErrInvalidRequest, "请求体为空");
            return store.RegisterKey(request.ApiKey, request.UserId)
                ? Json(new SimpleOk(true))
                : Err(MpProtocol.ErrNotFound, "未知用户");
        });

        await app.StartAsync(ct);
        var address = ResolveAddress(app, port);
        return new MockServerHandle(app, address);
    }

    private static string ResolveAddress(WebApplication app, int port)
    {
        var feature = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>();
        var address = feature?.Addresses.FirstOrDefault();
        return address ?? $"http://127.0.0.1:{port}";
    }

    private static async Task<T?> ReadJsonAsync<T>(HttpContext ctx) where T : class
    {
        try
        {
            return await ctx.Request.ReadFromJsonAsync<T>();
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static IResult WithAuth(HttpContext ctx, MockStore store, Func<string, IResult> handler) =>
        TryAuth(ctx, store, out var userId) ? handler(userId) : Unauthorized();

    private static IResult Unauthorized() => Err(MpProtocol.ErrUnauthorized, "缺少或无效的 ApiKey");

    private static bool TryAuth(HttpContext ctx, MockStore store, out string userId)
    {
        userId = string.Empty;
        var header = ctx.Request.Headers.Authorization.ToString();
        if (!header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)) return false;

        var resolved = store.ResolveUser(header["Bearer ".Length..].Trim());
        if (resolved is null) return false;

        userId = resolved;
        return true;
    }

    private static IResult From<T>(StoreResult<T> result) =>
        result.Ok ? Json(result.Value!) : Err(result.ErrorCode!, result.Message!);

    private static IResult Json<T>(T data) =>
        Results.Json(new MpEnvelope<T>(true, data, null, null), MpJson.Options);

    private static IResult Err(string code, string message) =>
        Results.Json(new MpEnvelope<SimpleOk>(false, null, code, message), MpJson.Options, statusCode: StatusOf(code));

    private static int StatusOf(string code) => code switch
    {
        MpProtocol.ErrUnauthorized => StatusCodes.Status401Unauthorized,
        MpProtocol.ErrForbidden => StatusCodes.Status403Forbidden,
        MpProtocol.ErrNotFound => StatusCodes.Status404NotFound,
        MpProtocol.ErrRoomClosed => StatusCodes.Status404NotFound,
        MpProtocol.ErrRoomExists => StatusCodes.Status409Conflict,
        MpProtocol.ErrRoomFull => StatusCodes.Status409Conflict,
        MpProtocol.ErrNotHost => StatusCodes.Status403Forbidden,
        _ => StatusCodes.Status400BadRequest,
    };

    /// <summary>仅 mock 的请求体。</summary>
    public sealed record MockKeyRequest(string ApiKey, string UserId);
}
