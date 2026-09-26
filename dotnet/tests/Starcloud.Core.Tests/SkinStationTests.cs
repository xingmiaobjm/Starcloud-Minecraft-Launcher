// 皮肤站衣柜（第三方换肤）测试。
//
// 皮肤站的开放 API 各家有差异，所以这里钉住的是"两种写法都试、失败给可读原因"这条契约：
//   · 列表：分页包裹 (`data.data`) 与直接数组 (`data`) 都要认；
//   · 换肤：先试 `PUT profile/skin {"tid":…}`；服务端不认就下载那张皮肤再走标准换肤上传；
//   · 凭证失效 / 不是皮肤站 / HTTP 200 但业务码非 0，都要翻译成人话，不许把状态码甩给用户。

using System.Net;
using System.Text;
using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class SkinStationTests
{
    private const string Base = "https://skin.example.com";

    [Fact]
    public async Task 列出衣柜_分页包裹与直接数组都认()
    {
        var http = new FakeHandler();
        http.Reply(HttpMethod.Get, $"{Base}/api/v1/user/skins?page=1&page_size=100", 200,
            """{"code":0,"data":{"total":2,"data":[{"tid":7,"name":"我的皮肤","model":"alex","url":"https://skin.example.com/a.png"},{"id":9,"name":"另一个","model":"steve"}]}}""");
        var list = await new SkinStationService(http).ListAsync(Base, "token");

        Assert.Equal(2, list.Count);
        Assert.Equal("7", list[0].Id);
        Assert.Equal("我的皮肤", list[0].Name);
        Assert.True(list[0].Slim);                 // alex = 纤细
        Assert.Equal("https://skin.example.com/a.png", list[0].Url);
        Assert.Equal("9", list[1].Id);             // id 字段也认
        Assert.False(list[1].Slim);
        Assert.Null(list[1].Url);
    }

    [Fact]
    public async Task 列出衣柜_凭证失效与不是皮肤站都给人话()
    {
        var expired = new FakeHandler();
        expired.Reply(HttpMethod.Get, $"{Base}/api/v1/user/skins?page=1&page_size=100", 401, "{}");
        var e1 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SkinStationService(expired).ListAsync(Base, "token"));
        Assert.Contains("重新登录", e1.Message);

        var notStation = new FakeHandler();
        notStation.Reply(HttpMethod.Get, $"{Base}/api/v1/user/skins?page=1&page_size=100", 404, "{}");
        var e2 = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SkinStationService(notStation).ListAsync(Base, "token"));
        Assert.Contains("没有开放皮肤站接口", e2.Message);
    }

    [Fact]
    public async Task 没有凭证时先说清楚要重新登录()
    {
        var e = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SkinStationService(new FakeHandler()).ListAsync(Base, ""));
        Assert.Contains("重新登录", e.Message);
    }

    [Fact]
    public async Task 换肤_支持直接选中时一步到位()
    {
        var http = new FakeHandler();
        http.Reply(HttpMethod.Put, $"{Base}/api/v1/user/profile/skin", 200, """{"code":0}""");
        var skin = new StationSkin("7", "我的皮肤", "alex", "https://skin.example.com/a.png");

        var message = await new SkinStationService(http).ApplyAsync(Base, "token", skin);

        Assert.Contains("我的皮肤", message);
        Assert.Single(http.Requests);                      // 没有走下载 + 回退上传
        Assert.Contains("\"tid\":\"7\"", http.Requests[0].Body);
    }

    [Fact]
    public async Task 换肤_服务端不认直接选中时下载后回退上传()
    {
        var http = new FakeHandler();
        http.ReplySequence(HttpMethod.Put, $"{Base}/api/v1/user/profile/skin",
            (405, "{}"), (200, """{"code":0}"""));
        http.ReplyBytes("https://skin.example.com/a.png", SkinPng());
        var skin = new StationSkin("7", "我的皮肤", "alex", "https://skin.example.com/a.png");

        var message = await new SkinStationService(http).ApplyAsync(Base, "token", skin);

        Assert.Contains("搬回", message);
        Assert.Equal(3, http.Requests.Count);              // 选中尝试 + 下载 + 上传
        var upload = http.Requests[^1].Body;
        Assert.Contains("\"model\":\"slim\"", upload);      // alex → slim
        Assert.Contains(Convert.ToBase64String(SkinPng()), upload);   // base64 原样，别被转义成 \u002B
    }

    [Fact]
    public async Task 换肤_HTTP200但业务码非0按失败处理()
    {
        var http = new FakeHandler();
        http.Reply(HttpMethod.Put, $"{Base}/api/v1/user/profile/skin", 200,
            """{"code":1,"message":"该皮肤不属于当前账号"}""");
        var skin = new StationSkin("7", "别人的皮肤", "steve", null);

        var e = await Assert.ThrowsAsync<InvalidOperationException>(
            () => new SkinStationService(http).ApplyAsync(Base, "token", skin));
        Assert.Contains("该皮肤不属于当前账号", e.Message);
    }

    private static byte[] SkinPng()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName,
                "src", "Starcloud.App", "Assets", "steve.png");
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到测试用皮肤 steve.png");
    }

    /// <summary>按 (方法, URL) 配 canned 响应；可按顺序配多个（回退路径要两次不同的答复）</summary>
    private sealed class FakeHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, Queue<(int Status, string Body)>> _json = [];
        private readonly Dictionary<string, byte[]> _bytes = [];

        public List<(string Method, string Url, string Body)> Requests { get; } = [];

        public void Reply(HttpMethod method, string url, int status, string body) =>
            ReplySequence(method, url, (status, body));

        public void ReplySequence(HttpMethod method, string url,
            params (int Status, string Body)[] replies)
        {
            var key = Key(method, url);
            if (!_json.TryGetValue(key, out var queue)) _json[key] = queue = [];
            foreach (var r in replies) queue.Enqueue(r);
        }

        public void ReplyBytes(string url, byte[] bytes) => _bytes[url] = bytes;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage req, CancellationToken ct)
        {
            var url = req.RequestUri!.ToString();
            var body = req.Content is null ? "" : await req.Content.ReadAsStringAsync(ct);
            Requests.Add((req.Method.Method, url, body));

            if (req.Method == HttpMethod.Get && _bytes.TryGetValue(url, out var file))
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(file),
                };

            var key = Key(req.Method, url);
            var (status, text) = _json.TryGetValue(key, out var queue) && queue.Count > 0
                ? queue.Dequeue()
                : (404, "{}");
            return new HttpResponseMessage((HttpStatusCode)status)
            {
                Content = new StringContent(text, Encoding.UTF8, "application/json"),
            };
        }

        private static string Key(HttpMethod method, string url) => $"{method.Method} {url}";
    }
}
