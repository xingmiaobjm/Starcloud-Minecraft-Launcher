// 插件 2.0 声明式视图：JSON → 视图模型（规范 2.0 §7）
// 白名单制：只接受宿主认得的组件与属性；html / iframe / script / style / object / embed / svg 一律拒绝。
// 本文件只回答「能不能画、画什么」，具体画法在 App 的 PluginViewRenderer（模型 → WinUI 控件树）。

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Starcloud.Core.Plugins;

/// <summary>一段文本或一个绑定（规范 2.0 §7.2：值可以写成 <c>{"bind":"storage.x"}</c>）</summary>
public sealed record PluginViewValue(string Text, PluginViewBinding? Bind)
{
    public bool IsBind => Bind is not null;
}

/// <summary>数据绑定：<c>storage.&lt;key&gt;</c> / <c>state.&lt;key&gt;</c> / <c>host.&lt;字段&gt;</c></summary>
public sealed record PluginViewBinding(string Scope, string Key)
{
    public override string ToString() => $"{Scope}.{Key}";
}

/// <summary>
/// 动作（规范 2.0 §7.2）：<c>{api,args}</c> 由宿主按权限校验后直接调用能力；<c>{handler}</c> 回调插件代码。
/// ArgsJson 为空对象时为 "{}"。
/// </summary>
public sealed record PluginViewAction(string Api, string ArgsJson, string Handler)
{
    public bool IsHandler => Handler.Length > 0;
}

/// <summary>一个视图节点（未列入白名单的字段一律忽略）</summary>
public sealed class PluginViewNode
{
    public string Type { get; init; } = "";
    public PluginViewValue Value { get; init; } = new("", null);
    public PluginViewBinding? ItemsBind { get; init; }

    /// <summary>表单控件（toggle / input / select）的读写绑定：<c>"storage.键"</c></summary>
    public PluginViewBinding? Bind { get; init; }

    /// <summary>list 的逐项模板（包内相对路径）：每一项按它渲染，模板内用 <c>item.*</c> 取字段</summary>
    public string ItemView { get; init; } = "";
    public string Style { get; init; } = "";
    public string Direction { get; init; } = "column";
    public string Kind { get; init; } = "normal";
    public string Placeholder { get; init; } = "";
    public string Source { get; init; } = "";
    public string Href { get; init; } = "";
    public string Language { get; init; } = "";
    public bool Wrap { get; init; } = true;
    public bool Multiline { get; init; }
    public double Spacing { get; init; }
    public double Height { get; init; }
    public double Progress { get; init; }
    public string[] Options { get; init; } = [];
    public string[] Columns { get; init; } = [];
    public PluginViewAction? Action { get; init; }
    public PluginViewNode[] Children { get; init; } = [];

    /// <summary>子节点总数（含自身），供宿主限制渲染开销</summary>
    public int CountNodes()
    {
        var n = 1;
        foreach (var child in Children) n += child.CountNodes();
        return n;
    }
}

/// <summary>声明式视图解析器（严格：任何一处越界都拒绝整份视图，并给出可操作的错误列表）</summary>
public static class PluginViewParser
{
    /// <summary>嵌套深度上限</summary>
    public const int MaxDepth = 12;

    /// <summary>节点总数上限</summary>
    public const int MaxNodes = 500;

    /// <summary>允许的组件类型（规范 2.0 §7.1）</summary>
    public static readonly string[] AllowedTypes =
    [
        "stack", "text", "button", "toggle", "input", "select",
        "list", "table", "image", "progress", "badge", "divider", "link", "code",
    ];

    /// <summary>明确禁止的组件类型：能引入任意 HTML/脚本/CSS，只在 ui:html 模式下才可能出现</summary>
    public static readonly string[] ForbiddenTypes =
    [
        "html", "iframe", "frame", "script", "style", "object", "embed", "svg", "canvas", "form",
    ];

    private static readonly string[] TextStyles = ["title", "body", "caption", "code"];
    private static readonly string[] ButtonKinds = ["normal", "accent", "danger"];
    private static readonly string[] BindScopes = ["storage", "state", "host", "item"];

    /// <summary>list 逐项渲染的递归上限（模板里再套 list 时防止无限展开）</summary>
    public const int MaxItemDepth = 3;

    /// <summary>解析一份视图 JSON；errors 非空即视为失败</summary>
    public static bool TryParse(string json, out PluginViewNode? root, out List<string> errors)
    {
        root = null;
        errors = [];
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException ex)
        {
            errors.Add($"视图 JSON 非法：{ex.Message}");
            return false;
        }

        using (doc)
        {
            var budget = new Budget();
            var node = ParseNode(doc.RootElement, errors, budget, 1);
            if (errors.Count > 0) return false;
            root = node;
            return node is not null;
        }
    }

    /// <summary>从文件解析（视图文件必须位于插件包内，调用方负责路径校验）</summary>
    public static bool TryParseFile(string path, out PluginViewNode? root, out List<string> errors)
    {
        root = null;
        errors = [];
        if (!File.Exists(path))
        {
            errors.Add($"视图文件不存在：{Path.GetFileName(path)}");
            return false;
        }
        try
        {
            return TryParse(File.ReadAllText(path), out root, out errors);
        }
        catch (Exception ex)
        {
            errors.Add($"视图文件读取失败：{ex.Message}");
            return false;
        }
    }

    private sealed class Budget
    {
        public int Nodes;
    }

    private static PluginViewNode? ParseNode(
        JsonElement element, List<string> errors, Budget budget, int depth)
    {
        if (depth > MaxDepth)
        {
            errors.Add($"视图嵌套超过 {MaxDepth} 层");
            return null;
        }
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"视图节点必须是对象（当前 {element.ValueKind}）");
            return null;
        }
        if (++budget.Nodes > MaxNodes)
        {
            errors.Add($"视图节点总数超过 {MaxNodes}");
            return null;
        }

        var type = Str(element, "type");
        if (string.IsNullOrWhiteSpace(type))
        {
            errors.Add("视图节点缺少 type");
            return null;
        }
        if (ForbiddenTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"视图组件被禁止：{type}（能引入任意 HTML/脚本，只能在 ui:html 面板中使用）");
            return null;
        }
        if (!AllowedTypes.Contains(type, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"未知视图组件：{type}（可用：{string.Join(" / ", AllowedTypes)}）");
            return null;
        }
        type = type.ToLowerInvariant();

        var style = Str(element, "style");
        if (style.Length > 0 && !TextStyles.Contains(style, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"text 样式非法：{style}（可用：{string.Join(" / ", TextStyles)}）");
            return null;
        }

        var kind = Str(element, "kind");
        if (kind.Length > 0 && !ButtonKinds.Contains(kind, StringComparer.OrdinalIgnoreCase))
        {
            errors.Add($"button 种类非法：{kind}（可用：{string.Join(" / ", ButtonKinds)}）");
            return null;
        }

        var direction = Str(element, "direction");
        if (type == "stack" && direction.Length > 0 &&
            direction is not ("row" or "column"))
        {
            errors.Add($"stack 的 direction 只能是 row / column：{direction}");
            return null;
        }

        var source = Str(element, "src");
        if (type == "image")
        {
            if (source.Length == 0)
            {
                errors.Add("image 缺少 src");
                return null;
            }
            if (!IsAllowedImageSource(source))
            {
                errors.Add($"image 的 src 必须是 https 地址或包内相对路径：{source}");
                return null;
            }
        }

        var href = Str(element, "href");
        if (type == "link" && href.Length > 0 &&
            !href.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"link 的 href 只允许 https 地址：{href}");
            return null;
        }

        // 逐项模板（list）：包内相对路径
        var itemView = Str(element, "itemView");
        if (itemView.Length > 0 && !PluginManifest.IsSafeRelativePath(itemView))
        {
            errors.Add($"itemView 必须是包内相对路径：{itemView}");
            return null;
        }
        if (type != "list" && itemView.Length > 0)
        {
            errors.Add($"{type} 不支持 itemView（只有 list 逐项渲染）");
            return null;
        }

        var itemsBind = ParseBinding(element, "items", errors) ?? ParseBinding(element, "rows", errors);
        if (type == "list")
        {
            if (itemView.Length == 0)
            {
                errors.Add("list 需要 itemView（逐项模板的包内相对路径）");
                return null;
            }
            if (itemsBind is null)
            {
                errors.Add("list 需要 items 绑定（{\"items\":{\"bind\":\"storage.键\"}}）");
                return null;
            }
        }

        // 动作：{api, args} 或 {handler}
        PluginViewAction? action = null;
        if (element.TryGetProperty("action", out var actionElement))
        {
            action = ParseAction(actionElement, errors);
            if (action is null) return null;
        }

        // 子节点（只有 stack 需要 children；其它类型出现 children 视为写错）
        var children = new List<PluginViewNode>();
        if (element.TryGetProperty("children", out var childrenElement))
        {
            if (type != "stack")
            {
                errors.Add($"{type} 不支持 children（只有 stack 是容器）");
                return null;
            }
            if (childrenElement.ValueKind != JsonValueKind.Array)
            {
                errors.Add("stack 的 children 必须是数组");
                return null;
            }
            foreach (var child in childrenElement.EnumerateArray())
            {
                var parsed = ParseNode(child, errors, budget, depth + 1);
                if (parsed is null) return null;
                children.Add(parsed);
            }
        }

        return new PluginViewNode
        {
            Type = type,
            Value = ParseValue(element, type == "button" || type == "toggle" || type == "input" ||
                                       type == "select" || type == "progress" ? "label" : "value", errors),
            ItemsBind = itemsBind,
            Bind = ParseBinding(element, "bind", errors),
            ItemView = itemView,
            Style = style.ToLowerInvariant(),
            Direction = direction.Length == 0 ? "column" : direction.ToLowerInvariant(),
            Kind = kind.Length == 0 ? "normal" : kind.ToLowerInvariant(),
            Placeholder = Str(element, "placeholder"),
            Source = source,
            Href = href,
            Language = Str(element, "language"),
            Wrap = Bool(element, "wrap", fallback: true),
            Multiline = Bool(element, "multiline"),
            Spacing = Num(element, "spacing"),
            Height = Num(element, "height"),
            Progress = Math.Clamp(type == "progress"
                ? Num(element, "progress", Num(element, "value"))
                : 0, 0, 1),
            Options = Strings(element, "options"),
            Columns = Strings(element, "columns"),
            Action = action,
            Children = [.. children],
        };
    }

    /// <summary>值字段：字符串，或 { "bind": "scope.key" }</summary>
    private static PluginViewValue ParseValue(JsonElement element, string property, List<string> errors)
    {
        if (!element.TryGetProperty(property, out var v)) return new("", null);
        switch (v.ValueKind)
        {
            case JsonValueKind.Object:
                var binding = ParseBindingObject(v);
                if (binding is null)
                    errors.Add($"{property} 的绑定非法：应为 {{\"bind\":\"storage.键\"}}，"
                               + $"作用域只支持 {string.Join(" / ", BindScopes)}");
                return new("", binding);
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return new("", null);
            case JsonValueKind.True:
                return new("true", null);
            case JsonValueKind.False:
                return new("false", null);
            default:
                return new(v.ToString(), null);
        }
    }

    private static PluginViewBinding? ParseBinding(
        JsonElement element, string property, List<string>? errors = null)
    {
        if (!element.TryGetProperty(property, out var v)) return null;
        var binding = ParseBindingObject(v);
        if (binding is null && errors is not null)
            errors.Add($"{property} 必须是 {{\"bind\":\"storage.键\"}} 形式");
        return binding;
    }

    private static PluginViewBinding? ParseBindingObject(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String)
            return ParseBindingText(element.GetString());
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("bind", out var b) || b.ValueKind != JsonValueKind.String)
            return null;
        return ParseBindingText(b.GetString());
    }

    /// <summary>"storage.launches" → (storage, launches)；list 模板里的 "item.name" → (item, name)</summary>
    public static PluginViewBinding? ParseBindingText(string? text)
    {
        var t = (text ?? "").Trim();
        // 裸 item：整项的值（标量项常用）
        if (string.Equals(t, "item", StringComparison.OrdinalIgnoreCase))
            return new PluginViewBinding("item", "");
        var dot = t.IndexOf('.');
        if (dot <= 0 || dot == t.Length - 1) return null;
        var scope = t[..dot].ToLowerInvariant();
        if (!BindScopes.Contains(scope)) return null;
        return new PluginViewBinding(scope, t[(dot + 1)..]);
    }

    private static PluginViewAction? ParseAction(JsonElement element, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors.Add("action 必须是对象：{\"api\":…} 或 {\"handler\":…}");
            return null;
        }
        var handler = Str(element, "handler");
        if (handler.Length > 0) return new("", "{}", handler);

        var api = Str(element, "api");
        if (api.Length == 0)
        {
            errors.Add("action 缺少 api 或 handler");
            return null;
        }
        var argsJson = element.TryGetProperty("args", out var args) ? args.GetRawText() : "{}";
        return new(api, argsJson, "");
    }

    /// <summary>image 的 src：https 绝对地址，或包内相对路径（禁止协议头与路径穿越）</summary>
    public static bool IsAllowedImageSource(string src)
    {
        if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) return true;
        return PluginManifest.IsSafeRelativePath(src);
    }

    private static string Str(JsonElement element, string name) =>
        element.TryGetProperty(name, out var v)
            ? v.ValueKind switch
            {
                JsonValueKind.String => v.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                JsonValueKind.Object or JsonValueKind.Array => "",
                _ => v.ToString(),
            }
            : "";

    private static bool Bool(JsonElement element, string name, bool fallback = false)
    {
        if (!element.TryGetProperty(name, out var v)) return fallback;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback,
        };
    }

    private static double Num(JsonElement element, string name, double fallback = 0)
    {
        if (!element.TryGetProperty(name, out var v)) return fallback;
        if (v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)) return d;
        return double.TryParse(v.ToString(), out var parsed) ? parsed : fallback;
    }

    private static string[] Strings(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Array) return [];
        return [.. v.EnumerateArray()
            .Where(e => e.ValueKind == JsonValueKind.String)
            .Select(e => e.GetString() ?? "")];
    }

    /// <summary>绑定键合法性（storage 键不允许出现路径分隔符，避免落到插件 data 目录之外）</summary>
    public static bool IsSafeBindingKey(string key) =>
        key.Length > 0 && key.Length <= 128 && !Regex.IsMatch(key, @"[\\/\u0000-\u001f]");
}
