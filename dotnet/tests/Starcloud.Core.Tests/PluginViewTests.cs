// 插件 2.0：声明式视图解析、能力桥接与网络白名单的单元测试

using Starcloud.Core.Plugins;
using Xunit;

namespace Starcloud.Core.Tests;

// ── 声明式视图解析（规范 2.0 §7） ─────────────────────────────

public class PluginViewParserTests
{
    private static PluginViewNode Parse(string json)
    {
        var ok = PluginViewParser.TryParse(json, out var root, out var errors);
        Assert.True(ok, string.Join(" / ", errors));
        Assert.NotNull(root);
        return root!;
    }

    private static List<string> Errors(string json)
    {
        PluginViewParser.TryParse(json, out _, out var errors);
        return errors;
    }

    [Fact]
    public void 解析基本组件树()
    {
        var root = Parse("""
        {
          "type": "stack", "direction": "column", "spacing": 12,
          "children": [
            { "type": "text", "value": "实例报告", "style": "title" },
            { "type": "divider" },
            { "type": "badge", "value": "已加载" },
            { "type": "button", "label": "刷新", "kind": "accent", "action": { "api": "instances.list", "args": [] } }
          ]
        }
        """);
        Assert.Equal("stack", root.Type);
        Assert.Equal(4, root.Children.Length);
        Assert.Equal("实例报告", root.Children[0].Value.Text);
        Assert.Equal("title", root.Children[0].Style);
        Assert.Equal("accent", root.Children[3].Kind);
        Assert.Equal("instances.list", root.Children[3].Action!.Api);
        Assert.False(root.Children[3].Action!.IsHandler);
    }

    [Theory]
    [InlineData("""{ "type": "html", "value": "<b>x</b>" }""")]
    [InlineData("""{ "type": "iframe", "src": "https://example.com" }""")]
    [InlineData("""{ "type": "script", "value": "alert(1)" }""")]
    [InlineData("""{ "type": "svg" }""")]
    public void 禁止的组件被拒绝(string json)
    {
        Assert.Contains("禁止", string.Join(" / ", Errors(json)));
    }

    [Fact]
    public void 未知组件被拒绝()
    {
        Assert.Contains("未知视图组件", string.Join(" / ", Errors("""{ "type": "carousel" }""")));
    }

    [Fact]
    public void 缺少type被拒绝()
    {
        Assert.Contains("缺少 type", string.Join(" / ", Errors("""{ "value": "x" }""")));
    }

    [Fact]
    public void 文本样式非法被拒绝()
    {
        Assert.Contains("样式非法", string.Join(" / ", Errors("""{ "type": "text", "value": "x", "style": "huge" }""")));
    }

    [Fact]
    public void 非容器组件带children被拒绝()
    {
        Assert.Contains("不支持 children",
            string.Join(" / ", Errors("""{ "type": "text", "value": "x", "children": [] }""")));
    }

    [Fact]
    public void 嵌套超限被拒绝()
    {
        var json = string.Concat(Enumerable.Repeat("""{"type":"stack","children":[""", 20))
                   + """{"type":"text","value":"x"}"""
                   + string.Concat(Enumerable.Repeat("]}", 20));
        Assert.Contains("嵌套超过", string.Join(" / ", Errors(json)));
    }

    [Fact]
    public void 节点总数超限被拒绝()
    {
        var children = string.Join(",", Enumerable.Repeat("""{"type":"text","value":"x"}""", 600));
        var json = $$"""{ "type": "stack", "children": [{{children}}] }""";
        Assert.Contains("节点总数超过", string.Join(" / ", Errors(json)));
    }

    [Theory]
    [InlineData("""{ "type": "text", "value": { "bind": "storage.launches" } }""", "storage", "launches")]
    [InlineData("""{ "type": "text", "value": { "bind": "state.tab" } }""", "state", "tab")]
    [InlineData("""{ "type": "text", "value": { "bind": "host.version" } }""", "host", "version")]
    public void 绑定被解析(string json, string scope, string key)
    {
        var root = Parse(json);
        Assert.Equal(scope, root.Value.Bind!.Scope);
        Assert.Equal(key, root.Value.Bind!.Key);
    }

    [Theory]
    [InlineData("""{ "type": "text", "value": { "bind": "cookies.abc" } }""")]
    [InlineData("""{ "type": "text", "value": { "bind": "noseparator" } }""")]
    [InlineData("""{ "type": "text", "value": { "bind": "storage." } }""")]
    public void 非法绑定被拒绝(string json)
    {
        Assert.NotEmpty(Errors(json));
    }

    [Fact]
    public void 处理器动作被解析()
    {
        var root = Parse("""{ "type": "button", "label": "跑", "action": { "handler": "onRefresh" } }""");
        Assert.True(root.Action!.IsHandler);
        Assert.Equal("onRefresh", root.Action!.Handler);
    }

    [Fact]
    public void 动作缺少api与handler被拒绝()
    {
        Assert.Contains("缺少 api", string.Join(" / ", Errors("""{ "type": "button", "label": "x", "action": {} }""")));
    }

    [Theory]
    [InlineData("assets/icon.png", true)]
    [InlineData("https://example.com/a.png", true)]
    [InlineData("file:///C:/Windows/a.png", false)]
    [InlineData("../../secret.png", false)]
    [InlineData("/etc/passwd", false)]
    public void 图片地址白名单(string src, bool allowed)
    {
        var json = $$"""{ "type": "image", "src": "{{src}}" }""";
        Assert.Equal(allowed, Errors(json).Count == 0);
    }

    [Fact]
    public void 链接只允许https()
    {
        Assert.Empty(Errors("""{ "type": "link", "value": "官网", "href": "https://example.com" }"""));
        Assert.NotEmpty(Errors("""{ "type": "link", "value": "官网", "href": "http://example.com" }"""));
    }

    [Fact]
    public void 进度值被裁剪到区间内()
    {
        Assert.Equal(1, Parse("""{ "type": "progress", "progress": 5 }""").Progress);
        Assert.Equal(0, Parse("""{ "type": "progress", "progress": -3 }""").Progress);
        Assert.Equal(0.5, Parse("""{ "type": "progress", "progress": 0.5 }""").Progress);
    }

    [Fact]
    public void 表格列与绑定被解析()
    {
        var root = Parse("""
        { "type": "table", "columns": ["实例", "大小"], "rows": { "bind": "storage.report" } }
        """);
        Assert.Equal(["实例", "大小"], root.Columns);
        Assert.Equal("report", root.ItemsBind!.Key);
    }

    [Fact]
    public void 列表需要模板与数据绑定()
    {
        Assert.Contains("itemView", string.Join(" / ",
            Errors("""{ "type": "list", "items": { "bind": "storage.x" } }""")));
        Assert.Contains("items", string.Join(" / ",
            Errors("""{ "type": "list", "itemView": "views/item.json" }""")));
    }

    [Fact]
    public void 列表模板必须是包内相对路径()
    {
        var json = """
        { "type": "list", "items": { "bind": "storage.x" }, "itemView": "../../secret.json" }
        """;
        Assert.Contains("包内相对路径", string.Join(" / ", Errors(json)));
    }

    [Fact]
    public void 非列表组件不允许itemView()
    {
        Assert.Contains("不支持 itemView", string.Join(" / ",
            Errors("""{ "type": "text", "value": "x", "itemView": "views/item.json" }""")));
    }

    [Fact]
    public void 列表与逐项绑定被解析()
    {
        var root = Parse("""
        { "type": "list", "items": { "bind": "storage.report" }, "itemView": "views/item.json" }
        """);
        Assert.Equal("views/item.json", root.ItemView);
        Assert.Equal("report", root.ItemsBind!.Key);

        var item = Parse("""{ "type": "text", "value": { "bind": "item.实例" } }""");
        Assert.Equal("item", item.Value.Bind!.Scope);
        Assert.Equal("实例", item.Value.Bind!.Key);

        var whole = Parse("""{ "type": "text", "value": { "bind": "item" } }""");
        Assert.Equal("", whole.Value.Bind!.Key);
    }

    [Fact]
    public void 非法json被拒绝()
    {
        Assert.Contains("JSON 非法", string.Join(" / ", Errors("{ not json")));
    }

    [Fact]
    public void 绑定键不允许路径分隔符()
    {
        Assert.True(PluginViewParser.IsSafeBindingKey("launches"));
        Assert.False(PluginViewParser.IsSafeBindingKey("../escape"));
        Assert.False(PluginViewParser.IsSafeBindingKey("a/b"));
        Assert.False(PluginViewParser.IsSafeBindingKey(""));
    }

    [Fact]
    public void 节点计数包含自身与子树()
    {
        var root = Parse("""
        { "type": "stack", "children": [ { "type": "text", "value": "a" }, { "type": "divider" } ] }
        """);
        Assert.Equal(3, root.CountNodes());
    }
}

// ── 2.0 能力桥接（只放行不放大权限的能力） ─────────────────────

public class PluginPermissionsBridgeTests
{
    [Fact]
    public void 直通能力被桥接()
    {
        var grant = PluginPermissions.BridgeApi2(
            ["ui:panel", "ui:notify", "events:subscribe", "instances:read", "logs:read"]);
        Assert.Contains(PluginPermissions.UiPanel, grant.Capabilities);
        Assert.Contains(PluginPermissions.Notification, grant.Capabilities);
        Assert.Contains(PluginPermissions.Events, grant.Capabilities);
        Assert.Contains(PluginPermissions.InstancesRead, grant.Capabilities);
        Assert.Contains(PluginPermissions.LogsRead, grant.Capabilities);
        Assert.Empty(grant.Deferred);
    }

    [Fact]
    public void 需要删除闸门的能力不给()
    {
        var grant = PluginPermissions.BridgeApi2(["instances:write(allow_delete)", "saves:write"]);
        Assert.Empty(grant.Capabilities);
        Assert.Equal(2, grant.Deferred.Count);
        Assert.Contains("尚未实现", string.Join("；", grant.Deferred));
    }

    [Fact]
    public void 未实现的运行时能力不给()
    {
        var grant = PluginPermissions.BridgeApi2(["tasks:spawn", "ui:html"]);
        Assert.Empty(grant.Capabilities);
        Assert.Contains("tasks.spawn", string.Join("；", grant.Deferred));
        Assert.Contains("HTML 面板", string.Join("；", grant.Deferred));
    }

    [Fact]
    public void 网络主机白名单随能力下发()
    {
        var grant = PluginPermissions.BridgeApi2(
            ["network:host=api.example.com", "network:host=*.mirror.example.com"]);
        Assert.Contains(PluginPermissions.Network, grant.Capabilities);
        Assert.Equal(["api.example.com", "*.mirror.example.com"], grant.AllowedHosts!);
    }

    [Fact]
    public void 任意网络退化为全放行标记()
    {
        var grant = PluginPermissions.BridgeApi2(["network:any"]);
        Assert.Equal(["*"], grant.AllowedHosts!);
    }

    [Fact]
    public void 存储配额来自参数()
    {
        Assert.Equal(2048, PluginPermissions.BridgeApi2(["storage:kv(quota=2mb)"]).StorageQuotaKb);
        Assert.Equal(256, PluginPermissions.BridgeApi2(["storage:kv"]).StorageQuotaKb);
        Assert.Null(PluginPermissions.BridgeApi2(["ui:panel"]).StorageQuotaKb);
    }

    [Fact]
    public void 非法能力串计入未开放()
    {
        var grant = PluginPermissions.BridgeApi2(["evil:everything"]);
        Assert.Empty(grant.Capabilities);
        Assert.Single(grant.Deferred);
    }

    [Fact]
    public void 重复能力只授予一次()
    {
        var grant = PluginPermissions.BridgeApi2(["ui:panel", "ui:panel"]);
        Assert.Single(grant.Capabilities);
    }
}

// ── 网络主机白名单匹配 ───────────────────────────────────────

public class PluginHostAllowListTests
{
    [Theory]
    [InlineData("api.example.com", true)]
    [InlineData("API.EXAMPLE.COM", true)]
    [InlineData("cdn.example.com", false)]
    [InlineData("example.com", false)]
    public void 精确主机名匹配(string host, bool allowed) =>
        Assert.Equal(allowed, PluginApiCore.IsHostAllowed(["api.example.com"], host));

    [Theory]
    [InlineData("a.example.com", true)]
    [InlineData("deep.a.example.com", true)]
    [InlineData("example.com", false)]
    [InlineData("evil-example.com", false)]
    [InlineData("aexample.com", false)]
    [InlineData("example.com.evil.net", false)]
    public void 通配子域不匹配裸域与相似域(string host, bool allowed) =>
        Assert.Equal(allowed, PluginApiCore.IsHostAllowed(["*.example.com"], host));

    [Theory]
    [InlineData("anything.invalid")]
    [InlineData("127.0.0.1")]
    public void 任意主机放行(string host) =>
        Assert.True(PluginApiCore.IsHostAllowed(["*"], host));
}
