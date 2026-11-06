// 联机房间的服务层回归：6 位码规则、Mock 后端的房间生命周期、HTTP 契约字段映射。
//
// 这三块是「大厅/房间」UI 的地基：码解析错 → 用户进不去；Mock 语义与云端不一致 →
// UI 在演示环境能跑、接真接口就崩；字段映射错 → 房间名/人数/密码标记全空。

using System.Net;
using System.Text;
using Starcloud.Core.Services.Rooms;
using Xunit;

namespace Starcloud.Core.Tests;

public class RoomsServiceTests
{
    // ── 6 位联机码 ──

    [Theory]
    [InlineData("248135", true)]
    [InlineData(" 248135 ", true)]
    [InlineData("248-135", true)]
    [InlineData("２４８１３５", true)]     // 全角数字（中文输入法常见）
    [InlineData("24813", false)]
    [InlineData("2481357", false)]
    [InlineData("24813a", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void 联机码只接受六位数字(string? code, bool valid)
    {
        Assert.Equal(valid, RoomCodeRules.IsValid(code));
    }

    [Fact]
    public void 联机码规范化去掉空白连字符并转换全角()
    {
        Assert.Equal("248135", RoomCodeRules.Normalize(" 24-8135 "));
        Assert.Equal("248135", RoomCodeRules.Normalize("２４８１３５"));
        Assert.Equal("", RoomCodeRules.Normalize(null));
    }

    // ── Mock 后端：房间生命周期 ──

    private static RoomService Mock(string me = "测试玩家") =>
        new(() => null, mock: true, me: me);

    [Fact]
    public async Task 大厅预置房间且密码房间不暴露密码()
    {
        var rooms = Mock();
        var list = await rooms.ListAsync();

        Assert.NotEmpty(list);
        Assert.Contains(list, r => r.HasPassword);
        Assert.All(list, r => Assert.True(RoomCodeRules.IsValid(r.Code)));
    }

    [Fact]
    public async Task 密码错误进不去且能识别为密码问题()
    {
        var rooms = Mock();
        var locked = (await rooms.ListAsync()).First(r => r.HasPassword);

        var ex = await Assert.ThrowsAsync<RoomApiException>(
            () => rooms.JoinAsync(locked.Code, "wrong"));
        Assert.True(ex.IsBadPassword);
    }

    [Fact]
    public async Task 正确密码进房后拿到陶瓦码()
    {
        var rooms = Mock();
        var locked = (await rooms.ListAsync()).First(r => r.HasPassword);

        var result = await rooms.JoinAsync(locked.Code, "123456");
        Assert.True(result.Room.Joined);
        Assert.NotNull(result.TerracottaCode);
        Assert.Contains(result.Room.Members, m => m.UserId == "me");
    }

    [Fact]
    public async Task 建房生成六位联机码并成为房主()
    {
        var rooms = Mock();
        var created = await rooms.CreateAsync(new CreateRoomRequest("我的房间", 6, "pw", null));

        Assert.True(RoomCodeRules.IsValid(created.Code));
        Assert.Equal(6, created.MaxPlayers);
        Assert.True(created.HasPassword);

        var joined = await rooms.JoinAsync(created.Code, "pw");
        Assert.True(joined.Room.IsOwner);
    }

    [Fact]
    public async Task 房间内消息按序追加且可发可读()
    {
        var rooms = Mock();
        var room = await rooms.CreateAsync(new CreateRoomRequest("聊天房", 4, null, null));

        await rooms.SendMessageAsync(room.Code, "你好");
        await rooms.SendMessageAsync(room.Code, "在吗");

        var messages = await rooms.GetMessagesAsync(room.Code, null, 50);
        Assert.Contains(messages, m => m.Content == "你好");
        Assert.Contains(messages, m => m.Content == "在吗");
        // Id 严格递增（UI 靠它去重 SSE 回显）
        for (var i = 1; i < messages.Count; i++)
            Assert.True(messages[i].Id > messages[i - 1].Id);
    }

    [Fact]
    public async Task 非房主不能踢人()
    {
        var rooms = Mock();
        var locked = (await rooms.ListAsync()).First(r => r.HasPassword);
        await rooms.JoinAsync(locked.Code, "123456");

        var ex = await Assert.ThrowsAsync<RoomApiException>(
            () => rooms.KickAsync(locked.Code, "someone"));
        Assert.False(ex.IsGone);
        Assert.Equal(403, ex.Status);
    }

    [Fact]
    public async Task 解散后房间从大厅消失且进不去()
    {
        var rooms = Mock();
        var room = await rooms.CreateAsync(new CreateRoomRequest("临时房", 4, null, null));
        await rooms.DissolveAsync(room.Code);

        Assert.DoesNotContain(await rooms.ListAsync(), r => r.Code == room.Code);
        var ex = await Assert.ThrowsAsync<RoomApiException>(
            () => rooms.JoinAsync(room.Code, null));
        Assert.True(ex.IsGone);
    }

    [Fact]
    public async Task 我的房间包含创建过的房间()
    {
        var rooms = Mock();
        var room = await rooms.CreateAsync(new CreateRoomRequest("历史房", 4, null, null));

        var mine = await rooms.MineAsync();
        Assert.Contains(mine, r => r.Code == room.Code);
    }

    [Fact]
    public async Task 非法联机码在请求前就被拦下()
    {
        var rooms = Mock();
        var ex = await Assert.ThrowsAsync<RoomApiException>(() => rooms.JoinAsync("abc", null));
        Assert.Equal("bad_code", ex.Code);
    }

    // ── HTTP 契约：字段映射与错误体解析 ──

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond)
        : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(respond(request));
    }

    private static HttpResponseMessage Json(string json, HttpStatusCode status = HttpStatusCode.OK) =>
        new(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") };

    private const string RoomJson = """
        {
          "code": "248135",
          "name": "小明的建筑服",
          "owner": { "user_id": "u1", "display_name": "小明", "avatar_url": null },
          "members": 3, "max_players": 8, "has_password": true,
          "mc_version": "1.20.1", "loader": "fabric",
          "pack": { "file_id": "f1", "size": 4200, "sha256": "abc",
                    "format": "modrinth", "mc_version": "1.20.1", "loader": "fabric",
                    "name": "建筑整合包" },
          "has_terracotta": true, "created_at": "2026-10-29T10:00:00Z"
        }
        """;

    [Fact]
    public async Task 大厅响应按蛇形字段解析()
    {
        var rooms = new RoomService(() => "key", "http://localhost",
            new StubHandler(_ => Json($$"""{ "rooms": [{{RoomJson}}] }""")));

        var list = await rooms.ListAsync();
        var room = Assert.Single(list);
        Assert.Equal("248135", room.Code);
        Assert.Equal("小明", room.Owner.DisplayName);
        Assert.True(room.HasPassword);
        Assert.Equal("1.20.1 · Fabric", room.VersionText);
        Assert.Equal("3 / 8", room.MembersText);
        Assert.Equal("建筑整合包", room.Pack!.Name);
    }

    [Fact]
    public async Task 进房响应解析陶瓦码与房主标记()
    {
        var body = $$"""
            {
              "room": { "room": {{RoomJson}},
                        "members": [ { "user_id": "me", "display_name": "我",
                                       "avatar_url": null, "role": "owner", "online": true } ],
                        "joined": true, "is_owner": true },
              "terracotta_code": "U/ABCD-EFGH-IJKL-MNOP"
            }
            """;
        var rooms = new RoomService(() => "key", "http://localhost",
            new StubHandler(_ => Json(body)));

        var result = await rooms.JoinAsync("248135", "pw");
        Assert.Equal("U/ABCD-EFGH-IJKL-MNOP", result.TerracottaCode);
        Assert.True(result.Room.IsOwner);
        Assert.Equal("me", result.Room.OwnerMember!.UserId);
    }

    [Fact]
    public async Task 密码错误响应翻成带标记的异常()
    {
        var rooms = new RoomService(() => "key", "http://localhost", new StubHandler(_ =>
            Json("""{ "error": { "code": "bad_password", "message": "密码不正确" } }""",
                HttpStatusCode.Forbidden)));

        var ex = await Assert.ThrowsAsync<RoomApiException>(() => rooms.JoinAsync("248135", "x"));
        Assert.True(ex.IsBadPassword);
        Assert.Equal("密码不正确", ex.Message);
    }

    [Fact]
    public async Task 未登录直接调用会明确报错()
    {
        var rooms = new RoomService(() => null, "http://localhost",
            new StubHandler(_ => Json("{}")));

        var ex = await Assert.ThrowsAsync<RoomApiException>(() => rooms.ListAsync());
        Assert.Equal(401, ex.Status);
        Assert.Contains("登录", ex.Message);
    }
}
