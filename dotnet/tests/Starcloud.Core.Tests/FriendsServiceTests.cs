// 好友服务契约测试：用桩 HttpMessageHandler 钉住 /v1/friends/* 的请求形状与响应解析，
// 不依赖线上服务（线上由后端仓库的 friends.ts 保证）。SSE 也在这条桩连接上跑一遍。

using System.Net;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class FriendsServiceTests
{
    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            if (request.Content is not null)
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            return respond(request);
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static FriendsService Service(StubHandler handler, string? key = "sk-test") =>
        new(() => key, handler: handler);

    [Fact]
    public async Task 好友列表带鉴权并解析未读数()
    {
        var handler = new StubHandler(_ => Json("""
            {"friends":[{"user_id":"sc-1","id":"Alpha","display_name":"Alpha",
              "avatar_url":null,"since":"2026-10-21T00:00:00.000Z","unread":2}],"total":1}
            """));
        var friends = await Service(handler).GetFriendsAsync();

        Assert.Single(friends);
        Assert.Equal("sc-1", friends[0].UserId);
        Assert.Equal("Alpha", friends[0].DisplayName);
        Assert.Equal(2, friends[0].Unread);
        Assert.Equal("Bearer", handler.LastRequest!.Headers.Authorization!.Scheme);
        Assert.Equal("sk-test", handler.LastRequest.Headers.Authorization.Parameter);
        Assert.Equal("/v1/friends", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task 搜索参数做Url编码并解析关系()
    {
        var handler = new StubHandler(_ => Json("""
            {"users":[{"user_id":"sc-2","id":"Beta","display_name":"Beta",
              "avatar_url":null,"relation":"outgoing"}]}
            """));
        var users = await Service(handler).SearchAsync("be ta");

        Assert.Single(users);
        Assert.Equal("outgoing", users[0].Relation);
        Assert.Contains("q=be%20ta", handler.LastRequest!.RequestUri!.Query);
    }

    [Fact]
    public async Task 创建邀请发送连接信息并解析房间码优先()
    {
        var handler = new StubHandler(_ => Json("""
            {"invite":{"invite_id":"i-1","from_user_id":"sc-1","to_user_id":"sc-2",
              "address":"mc.example.com","port":25565,"room_code":"U/1234",
              "message":"来","status":"pending",
              "created_at":"2026-10-21T00:00:00.000Z",
              "expires_at":"2026-10-21T00:10:00.000Z","responded_at":null,
              "user":{"user_id":"sc-2","id":"Beta","display_name":"Beta","avatar_url":null}}}
            """, HttpStatusCode.Created));

        var invite = await Service(handler).CreateInviteAsync(
            "sc-2", "mc.example.com", 25565, "U/1234", "来");

        Assert.Equal("i-1", invite.InviteId);
        Assert.Equal("U/1234", invite.ConnectionText);
        Assert.Contains("\"room_code\":\"U/1234\"", handler.LastBody);
        Assert.Contains("\"port\":25565", handler.LastBody);
        Assert.Equal("/v1/friends/sc-2/invites", handler.LastRequest!.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task 地址端口作为连接信息兜底()
    {
        var handler = new StubHandler(_ => Json("""
            {"invite":{"invite_id":"i-2","from_user_id":"sc-1","to_user_id":"sc-2",
              "address":"1.2.3.4","port":25570,"room_code":null,"message":null,
              "status":"pending","created_at":"2026-10-21T00:00:00.000Z",
              "expires_at":"2026-10-21T00:10:00.000Z","responded_at":null,
              "user":{"user_id":"sc-2","id":"Beta","display_name":"Beta","avatar_url":null}}}
            """, HttpStatusCode.Created));

        var invite = await Service(handler).CreateInviteAsync("sc-2", "1.2.3.4", 25570, null, null);
        Assert.Equal("1.2.3.4:25570", invite.ConnectionText);
    }

    [Fact]
    public async Task 后端错误体转成带code的异常()
    {
        var handler = new StubHandler(_ => Json("""
            {"error":{"code":"not_friends","message":"只能邀请好友联机"}}
            """, HttpStatusCode.Forbidden));

        var ex = await Assert.ThrowsAsync<FriendApiException>(
            () => Service(handler).CreateInviteAsync("sc-2", "1.2.3.4", null, null, null));
        Assert.Equal("not_friends", ex.Code);
        Assert.Equal("只能邀请好友联机", ex.Message);
        Assert.Equal(HttpStatusCode.Forbidden, ex.Status);
    }

    [Fact]
    public async Task 未登录直接报错且不发请求()
    {
        var handler = new StubHandler(_ => Json("{}"));
        var ex = await Assert.ThrowsAsync<FriendApiException>(
            () => Service(handler, key: null).GetFriendsAsync());
        Assert.Equal("unauthorized", ex.Code);
        Assert.Null(handler.LastRequest);
    }

    [Fact]
    public async Task SSE解析事件并忽略ready心跳注释()
    {
        var sse = """
            event: ready
            data: {"ok":true}

            : ping

            event: invite
            data: {"invite_id":"i-9","user":{"display_name":"Beta"}}

            """;
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(sse, Encoding.UTF8, "text/event-stream"),
        });

        var events = new List<FriendEvent>();
        await Service(handler).StreamEventsAsync(evt =>
        {
            events.Add(evt);
            return Task.CompletedTask;
        });

        var invite = Assert.Single(events);
        Assert.Equal("invite", invite.Type);
        Assert.Equal("i-9", invite.Data.GetProperty("invite_id").GetString());
        Assert.Equal("text/event-stream", handler.LastRequest!.Headers.Accept.ToString());
        Assert.Equal("/v1/friends/events", handler.LastRequest.RequestUri!.AbsolutePath);
    }

    [Fact]
    public async Task 聊天记录发送与已读按后端结构解析()
    {
        var handler = new StubHandler(request =>
        {
            var path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get && path.EndsWith("/messages"))
                return Json("""
                    {"items":[{"id":3,"from_user_id":"sc-2","to_user_id":"sc-1",
                      "content":"在吗","created_at":"2026-10-21T06:00:00.000Z","read_at":null}],
                     "has_more":true,"next_before_id":3}
                    """);
            if (request.Method == HttpMethod.Post && path.EndsWith("/messages"))
                return Json("""
                    {"message":{"id":4,"from_user_id":"sc-1","to_user_id":"sc-2",
                      "content":"在的","created_at":"2026-10-21T06:01:00.000Z","read_at":null}}
                    """, HttpStatusCode.Created);
            return Json("""{"updated":2,"unread":{"total":0,"items":[]}}""");
        });
        var service = Service(handler);

        var page = await service.GetConversationAsync("sc-2", limit: 100);
        Assert.True(page.HasMore);
        Assert.Equal(3L, page.NextBeforeId);
        Assert.Equal("在吗", Assert.Single(page.Items).Content);
        Assert.Contains("limit=100", handler.LastRequest!.RequestUri!.Query);

        var sent = await service.SendMessageAsync("sc-2", "在的");
        Assert.Equal(4L, sent.Id);
        Assert.Equal("sc-1", sent.FromUserId);
        using (var body = System.Text.Json.JsonDocument.Parse(handler.LastBody!))
            Assert.Equal("在的", body.RootElement.GetProperty("content").GetString());

        Assert.Equal(2, await service.MarkReadAsync("sc-2"));
    }
}
