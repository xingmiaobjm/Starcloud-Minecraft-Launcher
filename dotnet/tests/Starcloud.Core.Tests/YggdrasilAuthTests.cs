// 第三方（Yggdrasil）登录的请求形状与错误透出。
//
// 真踩过的坑：`agent.name` 写成了自家的 "MinecraftLauncher"，LittleSkin 直接回
// "agent.name must be equal to Minecraft" —— 规范要求它必须**正好**是 "Minecraft"。
// 这里把请求体钉住，顺带钉住"服务端的 errorMessage 要原样透给用户"。

using System.Net;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class YggdrasilAuthTests
{
    private const string Base = "https://skin.example.com/api/yggdrasil";

    [Fact]
    public async Task 登录_请求体按规范发agent与clientToken()
    {
        var http = new FakeHandler(200, """
            {"accessToken":"tok","clientToken":"11111111-2222-3333-4444-555555555555",
             "selectedProfile":{"id":"pid","name":"Steve"}}
            """);
        var token = "11111111-2222-3333-4444-555555555555";

        var result = await new YggdrasilService(http)
            .AuthenticateAsync(Base, "user@example.com", "pw", token);

        Assert.Equal("tok", result.AccessToken);
        Assert.Equal("pid", result.ProfileId);
        Assert.Equal("Steve", result.ProfileName);

        var body = http.LastBody!;
        Assert.Contains("\"agent\":{\"name\":\"Minecraft\",\"version\":1}", body);
        Assert.Contains($"\"clientToken\":\"{token}\"", body);
        Assert.Contains("\"requestUser\":false", body);
        Assert.Equal($"{Base}/authserver/authenticate", http.LastUrl);
    }

    [Fact]
    public async Task 登录_没有selectedProfile时取可用角色里的第一个()
    {
        var http = new FakeHandler(200, """
            {"accessToken":"tok","clientToken":"c",
             "availableProfiles":[{"id":"p1","name":"First"},{"id":"p2","name":"Second"}]}
            """);

        var result = await new YggdrasilService(http).AuthenticateAsync(Base, "u", "p", "c");

        Assert.Equal("p1", result.ProfileId);
        Assert.Equal("First", result.ProfileName);
    }

    [Fact]
    public async Task 登录失败_把服务端的原因原样给用户()
    {
        var http = new FakeHandler(403, """
            {"error":"ForbiddenOperationException","errorMessage":"agent.name must be equal to Minecraft"}
            """);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new YggdrasilService(http).AuthenticateAsync(Base, "u", "p", "c"));

        Assert.Contains("agent.name must be equal to Minecraft", e.Message);
    }

    [Fact]
    public async Task 登录_账号没有角色时给出可读原因()
    {
        var http = new FakeHandler(200, """{"accessToken":"tok","clientToken":"c"}""");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new YggdrasilService(http).AuthenticateAsync(Base, "u", "p", "c"));

        Assert.Contains("没有 Minecraft 角色", e.Message);
    }

    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly int _status;
        private readonly string _body;

        public FakeHandler(int status, string body)
        {
            _status = status;
            _body = body;
        }

        public string? LastBody { get; private set; }
        public string? LastUrl { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            LastUrl = req.RequestUri!.ToString();
            LastBody = req.Content is null ? null : await req.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage((HttpStatusCode)_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }
}
