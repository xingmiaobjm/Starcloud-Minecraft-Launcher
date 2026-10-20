// 声明式视图渲染器（规范 2.0 §7）：把插件提供的视图模型画成原生控件。
//
// 三条不变量：
//   1. 只画白名单组件（解析阶段已把关）——这里没有 WebView、没有 HTML 注入点；
//   2. 数据与动作都经宿主：storage 读写走 PluginHost.InvokeAction，权限、网络白名单、
//      存储配额与插件自己调用时是同一套校验，声明式 UI 不是旁路；
//   3. 画不出来就明说：未接入的组件（list 的 itemView）渲染成一行说明，而不是空白或假控件。
//
// 移植自桌面 Starcloud.App.Views.Plugins.PluginViewRenderer，签名与桌面一致
// （PluginContributionPanel / 首页卡片带 / 插件面板页共用同一入口）。

using System.Text.Json;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.Launcher.Phone.Services;
using Starcloud.Core.Plugins;
using Windows.UI;

namespace Starcloud.Launcher.Phone.Views.Plugins;

/// <summary>渲染上下文：绑定求值、动作执行、结果提示都回落到宿主</summary>
public sealed class PluginViewContext
{
    /// <summary>插件 id</summary>
    public required string PluginId { get; init; }

    /// <summary>插件清单（host.* 绑定与展示用）</summary>
    public required PluginManifest Manifest { get; init; }

    /// <summary>插件宿主（能力调用入口）</summary>
    public required PluginHost Host { get; init; }

    /// <summary>插件解压目录（包内相对路径以此为根）</summary>
    public required string PluginDir { get; init; }

    /// <summary>结果提示：(消息, 是否成功)</summary>
    public Action<string, bool>? Notify { get; init; }

    /// <summary>请求整份视图重绘（写回数据后调用）</summary>
    public Action? RequestRefresh { get; init; }

    /// <summary>视图内本地状态（bind 的 state.&lt;key&gt;）</summary>
    public Dictionary<string, string> LocalState { get; } = new(StringComparer.Ordinal);

    /// <summary>list 逐项渲染：当前项（原始 JSON）栈，模板里再套 list 时逐层压入</summary>
    internal List<string> ItemStack { get; } = [];

    /// <summary>已展开的 list 层数（防模板自引用无限展开）</summary>
    internal int ItemDepth { get; set; }
}

public static class PluginViewRenderer
{
    private const int MaxTableRows = 50;
    private const int MaxTableColumns = 8;
    private const int MaxImageHeight = 320;
    private const int MaxListItems = 40;

    /// <summary>渲染一棵视图；任何异常都由调用方兜住（页面会显示错误面板）</summary>
    public static FrameworkElement Render(PluginViewNode node, PluginViewContext ctx) =>
        Build(node, ctx);

    /// <summary>
    /// 把插件的一份声明式视图文件渲染成控件树 —— 挂载区（PluginContributionPanel）与
    /// 首页卡片带共用同一个入口，两处不各写一套。
    /// 插件不在已安装列表 / 解析失败 / 渲染异常都不抛：返回一行说明文字，宿主照常显示其它内容。
    /// </summary>
    public static FrameworkElement RenderFile(string pluginId, string entryPath,
        Action<string, bool>? notify = null, Action? refresh = null)
    {
        var services = AppServices.Instance;
        var installed = services.Plugins.Scan().FirstOrDefault(p => p.Manifest.Id == pluginId);
        if (installed is null) return Hint("插件已不在已安装列表中");
        if (!PluginViewParser.TryParseFile(entryPath, out var root, out var errors) || root is null)
            return Hint($"视图无法渲染：{string.Join("；", errors)}");

        var ctx = new PluginViewContext
        {
            PluginId = pluginId,
            Manifest = installed.Manifest,
            Host = services.PluginHost,
            PluginDir = installed.Dir,
            Notify = notify,
            RequestRefresh = refresh,
        };
        try
        {
            return Render(root, ctx);
        }
        catch (Exception ex)
        {
            return Hint($"视图渲染失败：{ex.Message}");
        }
    }

    // ── 节点分发 ──

    private static FrameworkElement Build(PluginViewNode node, PluginViewContext ctx) => node.Type switch
    {
        "stack" => BuildStack(node, ctx),
        "text" => BuildText(node, ctx),
        "code" => BuildCode(node, ctx),
        "divider" => BuildDivider(),
        "badge" => BuildBadge(node, ctx),
        "progress" => BuildProgress(node, ctx),
        "image" => BuildImage(node, ctx),
        "link" => BuildLink(node, ctx),
        "button" => BuildButton(node, ctx),
        "toggle" => BuildToggle(node, ctx),
        "input" => BuildInput(node, ctx),
        "select" => BuildSelect(node, ctx),
        "table" => BuildTable(node, ctx),
        "list" => BuildList(node, ctx),
        _ => Hint($"未支持的组件：{node.Type}"),
    };

    private static FrameworkElement BuildStack(PluginViewNode node, PluginViewContext ctx)
    {
        var panel = new StackPanel
        {
            Orientation = node.Direction == "row" ? Orientation.Horizontal : Orientation.Vertical,
            Spacing = node.Spacing > 0 ? node.Spacing : 8,
        };
        foreach (var child in node.Children) panel.Children.Add(Build(child, ctx));
        return panel;
    }

    private static FrameworkElement BuildText(PluginViewNode node, PluginViewContext ctx)
    {
        var text = new TextBlock
        {
            Text = Resolve(node.Value, ctx),
            TextWrapping = node.Wrap ? TextWrapping.Wrap : TextWrapping.NoWrap,
        };
        switch (node.Style)
        {
            case "title":
                text.FontSize = 20;
                text.FontWeight = FontWeights.Medium;
                break;
            case "caption":
                text.FontSize = 12;
                text.Opacity = 0.7;
                break;
            case "code":
                text.FontFamily = MonoFont;
                text.FontSize = 13;
                break;
            default:
                text.FontSize = 14;
                break;
        }
        return text;
    }

    private static FrameworkElement BuildCode(PluginViewNode node, PluginViewContext ctx)
    {
        var text = new TextBlock
        {
            Text = Resolve(node.Value, ctx),
            FontFamily = MonoFont,
            FontSize = 13,
            TextWrapping = TextWrapping.NoWrap,
            IsTextSelectionEnabled = true,
        };
        var scroller = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            Content = text,
        };
        var box = new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(12, 10, 12, 10),
            Background = Brush("LayerFillColorDefaultBrush", Color.FromArgb(0x18, 0x80, 0x80, 0x80)),
            Child = scroller,
        };
        if (!string.IsNullOrEmpty(node.Language)) ToolTipService.SetToolTip(box, node.Language);
        return box;
    }

    private static FrameworkElement BuildDivider() => new Border
    {
        Height = 1,
        Margin = new Thickness(0, 4, 0, 4),
        Background = Brush("DividerStrokeColorDefaultBrush", Color.FromArgb(0x33, 0x80, 0x80, 0x80)),
    };

    private static FrameworkElement BuildBadge(PluginViewNode node, PluginViewContext ctx)
    {
        var chip = new Border
        {
            Style = Res<Style>("ScChip"),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Style = Res<Style>("ScChipText"),
                Text = Resolve(node.Value, ctx),
            },
        };
        return new StackPanel { Orientation = Orientation.Horizontal, Children = { chip } };
    }

    private static FrameworkElement BuildProgress(PluginViewNode node, PluginViewContext ctx)
    {
        var stack = new StackPanel { Spacing = 6 };
        var label = node.Value.Bind is not null || node.Value.Text.Length > 0
            ? Resolve(node.Value, ctx)
            : "";
        if (label.Length > 0)
            stack.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.75 });
        stack.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = Math.Clamp(node.Progress, 0, 1) * 100,
        });
        return stack;
    }

    private static FrameworkElement BuildImage(PluginViewNode node, PluginViewContext ctx)
    {
        var src = node.Source;
        Uri? uri = null;
        if (src.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            uri = Uri.TryCreate(src, UriKind.Absolute, out var remote) ? remote : null;
        }
        else
        {
            // 包内相对路径：以插件解压目录为根（清单校验已拒绝绝对路径与 .. 穿越）
            var full = Path.GetFullPath(Path.Combine(ctx.PluginDir,
                src.Replace('/', Path.DirectorySeparatorChar)));
            if (File.Exists(full))
                uri = new Uri("file:///" + full.Replace('\\', '/'));
        }
        if (uri is null) return Hint($"图片不可用：{src}");

        var height = node.Height > 0 ? Math.Min(node.Height, MaxImageHeight) : double.NaN;
        var image = new Image
        {
            Source = new BitmapImage(uri),
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        if (!double.IsNaN(height)) image.Height = height;
        return new Border { CornerRadius = new CornerRadius(8), Child = image };
    }

    private static FrameworkElement BuildLink(PluginViewNode node, PluginViewContext ctx)
    {
        var link = new HyperlinkButton
        {
            Content = Resolve(node.Value, ctx) is { Length: > 0 } text ? text : node.Href,
            Padding = new Thickness(0, 2, 0, 2),
        };
        var href = node.Href;
        if (href.Length > 0)
        {
            link.Click += async (_, _) =>
            {
                try { await Windows.System.Launcher.LaunchUriAsync(new Uri(href)); }
                catch { /* 打开失败不打断面板 */ }
            };
        }
        return link;
    }

    private static FrameworkElement BuildButton(PluginViewNode node, PluginViewContext ctx)
    {
        var button = new Button
        {
            Content = Resolve(node.Value, ctx) is { Length: > 0 } label ? label : node.Action?.Api ?? "按钮",
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(14, 6, 14, 6),
            MinHeight = 32,
        };
        if (node.Kind == "accent") button.Style = Res<Style>("AccentButtonStyle");
        if (node.Kind == "danger")
            button.Foreground = Brush("SystemFillColorCriticalBrush", Color.FromArgb(0xFF, 0xC4, 0x2B, 0x1C));

        var action = node.Action;
        if (action is null)
        {
            button.IsEnabled = false;
            ToolTipService.SetToolTip(button, "该按钮没有声明 action");
            return button;
        }
        button.Click += (_, _) => RunAction(action, ctx);
        return button;
    }

    private static FrameworkElement BuildToggle(PluginViewNode node, PluginViewContext ctx)
    {
        var bind = node.Bind ?? node.Value.Bind;
        var toggle = new ToggleSwitch
        {
            Header = node.Value.Text,
            OnContent = "",
            OffContent = "",
            IsOn = ParseBool(ReadBound(bind, ctx)),
        };
        if (bind?.Scope != "storage")
        {
            toggle.IsEnabled = false;
            ToolTipService.SetToolTip(toggle, "未绑定 storage 键，暂不支持写回");
            return toggle;
        }
        toggle.Toggled += (sender, _) =>
        {
            if (!ReferenceEquals(sender, toggle)) return;
            WriteStorage(bind.Key, toggle.IsOn, ctx);
        };
        return toggle;
    }

    private static FrameworkElement BuildInput(PluginViewNode node, PluginViewContext ctx)
    {
        var bind = node.Bind ?? node.Value.Bind;
        var input = new TextBox
        {
            Header = node.Value.Text,
            PlaceholderText = node.Placeholder,
            Text = ReadBound(bind, ctx),
        };
        if (node.Multiline)
        {
            input.AcceptsReturn = true;
            input.TextWrapping = TextWrapping.Wrap;
            input.MinHeight = 72;
        }
        if (bind?.Scope != "storage")
        {
            input.IsReadOnly = true;
            ToolTipService.SetToolTip(input, "未绑定 storage 键，暂不支持写回");
            return input;
        }
        input.LostFocus += (_, _) => WriteStorage(bind.Key, input.Text, ctx);
        return input;
    }

    private static FrameworkElement BuildSelect(PluginViewNode node, PluginViewContext ctx)
    {
        var bind = node.Bind ?? node.Value.Bind;
        var combo = new ComboBox
        {
            Header = node.Value.Text,
            MinWidth = 160,
        };
        foreach (var option in node.Options) combo.Items.Add(option);
        var index = Array.IndexOf(node.Options, ReadBound(bind, ctx));
        if (index >= 0) combo.SelectedIndex = index;
        if (bind?.Scope != "storage" || node.Options.Length == 0)
        {
            combo.IsEnabled = false;
            ToolTipService.SetToolTip(combo,
                node.Options.Length == 0 ? "select 没有 options" : "未绑定 storage 键，暂不支持写回");
            return combo;
        }
        combo.SelectionChanged += (_, _) =>
        {
            if (combo.SelectedItem is string picked) WriteStorage(bind.Key, picked, ctx);
        };
        return combo;
    }

    /// <summary>表格：columns 为表头，rows/items 绑定一个数组（对象按列名取值，数组按位置取值）</summary>
    private static FrameworkElement BuildTable(PluginViewNode node, PluginViewContext ctx)
    {
        var columns = node.Columns.Take(MaxTableColumns).ToArray();
        if (columns.Length == 0) return Hint("table 缺少 columns");
        if (node.ItemsBind is null) return Hint("table 缺少 rows/items 绑定");

        var rows = ReadRows(node, ctx);
        var grid = new Grid { ColumnSpacing = 16, RowSpacing = 6 };
        for (var c = 0; c < columns.Length; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        for (var c = 0; c < columns.Length; c++)
        {
            var header = new TextBlock
            {
                Text = columns[c],
                FontSize = 12,
                FontWeight = FontWeights.Medium,
                Opacity = 0.7,
            };
            Grid.SetColumn(header, c);
            grid.Children.Add(header);
        }

        var shown = rows.Take(MaxTableRows).ToArray();
        for (var r = 0; r < shown.Length; r++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var c = 0; c < columns.Length; c++)
            {
                var cell = new TextBlock
                {
                    Text = c < shown[r].Length ? shown[r][c] : "",
                    FontSize = 13,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                };
                Grid.SetRow(cell, r + 1);
                Grid.SetColumn(cell, c);
                grid.Children.Add(cell);
            }
        }

        var wrapped = new StackPanel { Spacing = 8, Children = { grid } };
        if (rows.Length == 0)
            wrapped.Children.Add(new TextBlock { Text = "（暂无数据）", FontSize = 12, Opacity = 0.6 });
        else if (rows.Length > shown.Length)
            wrapped.Children.Add(new TextBlock
            {
                Text = $"（共 {rows.Length} 行，仅显示前 {shown.Length} 行）",
                FontSize = 12,
                Opacity = 0.6,
            });
        return wrapped;
    }

    private static FrameworkElement Hint(string message) => new TextBlock
    {
        Text = message,
        FontSize = 12,
        Opacity = 0.6,
        TextWrapping = TextWrapping.Wrap,
    };

    /// <summary>
    /// list：items 绑定一个数组，逐项按 itemView 模板渲染；模板里用 <c>item.*</c> 取字段，
    /// 裸 <c>item</c> 取整项。模板可以再套 list（受 MaxItemDepth 限制）。
    /// </summary>
    private static FrameworkElement BuildList(PluginViewNode node, PluginViewContext ctx)
    {
        var items = ReadItemJson(node, ctx);
        if (items.Length == 0) return Hint("（暂无数据）");
        if (ctx.ItemDepth >= PluginViewParser.MaxItemDepth)
            return Hint($"列表嵌套超过 {PluginViewParser.MaxItemDepth} 层，已停止展开");

        var templatePath = ResolveRelative(node.ItemView, ctx);
        if (!PluginViewParser.TryParseFile(templatePath, out var template, out var errors) || template is null)
            return Hint($"逐项模板无法渲染：{string.Join("；", errors)}");

        var shown = items.Take(MaxListItems).ToArray();
        var panel = new StackPanel { Spacing = 6 };
        ctx.ItemDepth++;
        try
        {
            foreach (var item in shown)
            {
                ctx.ItemStack.Add(item);
                try { panel.Children.Add(Build(template, ctx)); }
                finally { ctx.ItemStack.RemoveAt(ctx.ItemStack.Count - 1); }
            }
        }
        finally
        {
            ctx.ItemDepth--;
        }

        if (items.Length <= shown.Length) return panel;
        var wrapped = new StackPanel { Spacing = 6, Children = { panel } };
        wrapped.Children.Add(Hint($"（共 {items.Length} 项，仅显示前 {shown.Length} 项）"));
        return wrapped;
    }

    /// <summary>包内相对路径 → 绝对路径（解析阶段已拒绝绝对路径与 .. 穿越，这里再夹一次）</summary>
    private static string ResolveRelative(string relative, PluginViewContext ctx)
    {
        var root = Path.GetFullPath(ctx.PluginDir);
        var full = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
        return full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(full, root, StringComparison.OrdinalIgnoreCase)
            ? full
            : root; // 越界就回落成目录路径（File.Exists 为假 → 页面显示「模板无法渲染」）
    }

    // ── 绑定与动作 ──

    private static string Resolve(PluginViewValue value, PluginViewContext? ctx)
    {
        if (value.Bind is null) return value.Text;
        if (ctx is null) return value.Text;
        return value.Bind.Scope switch
        {
            "host" => value.Bind.Key switch
            {
                "version" => ctx.Host.LauncherVersion,
                "plugin" => ctx.PluginId,
                "api_version" => ctx.Manifest.ApiVersion,
                _ => "",
            },
            "state" => ctx.LocalState.GetValueOrDefault(value.Bind.Key, ""),
            "item" => ResolveItem(value.Bind.Key, ctx),
            _ => ReadStorage(value.Bind.Key, ctx),
        };
    }

    /// <summary>当前列表项取值：空键取整项，<c>a.b</c> / <c>arr.0</c> 逐层下钻</summary>
    private static string ResolveItem(string key, PluginViewContext ctx)
    {
        if (ctx.ItemStack.Count == 0) return "";
        var json = ctx.ItemStack[^1];
        if (key.Length == 0) return JsonToText(json);
        try
        {
            using var doc = JsonDocument.Parse(json);
            var current = doc.RootElement;
            foreach (var part in key.Split('.'))
            {
                if (current.ValueKind == JsonValueKind.Object &&
                    current.TryGetProperty(part, out var next))
                {
                    current = next;
                }
                else if (current.ValueKind == JsonValueKind.Array &&
                         int.TryParse(part, out var index) && index >= 0 &&
                         index < current.GetArrayLength())
                {
                    current = current[index];
                }
                else
                {
                    return "";
                }
            }
            return JsonToText(current.GetRawText());
        }
        catch
        {
            return "";
        }
    }

    /// <summary>按绑定读值（表单控件用；无绑定则空串）</summary>
    private static string ReadBound(PluginViewBinding? bind, PluginViewContext ctx) =>
        bind is null ? "" : Resolve(new PluginViewValue("", bind), ctx);

    private static string ReadStorage(string key, PluginViewContext ctx)
    {
        if (!PluginViewParser.IsSafeBindingKey(key)) return "";
        var result = ctx.Host.InvokeAction(ctx.PluginId, "storage.get", Args(key));
        if (!result.Ok || result.ResultJson is null) return "";
        return JsonToText(result.ResultJson);
    }

    private static void WriteStorage(string key, object value, PluginViewContext ctx)
    {
        if (!PluginViewParser.IsSafeBindingKey(key)) return;
        var result = ctx.Host.InvokeAction(ctx.PluginId, "storage.set", Args(key, value));
        if (result.Ok)
        {
            ctx.Notify?.Invoke($"已保存 {key}", true);
            ctx.RequestRefresh?.Invoke();
        }
        else
        {
            ctx.Notify?.Invoke(result.Error ?? "写入失败", false);
        }
    }

    /// <summary>能力参数是"位置参数数组"（与 Python host_call 协议一致），不是对象</summary>
    private static string Args(params object?[] values) => JsonSerializer.Serialize(values);

    private static void RunAction(PluginViewAction action, PluginViewContext ctx)
    {
        if (action.IsHandler)
        {
            ctx.Notify?.Invoke($"处理器回调 {action.Handler} 将在运行时增量中接入", false);
            return;
        }
        var result = ctx.Host.InvokeAction(ctx.PluginId, action.Api, action.ArgsJson);
        if (result.Ok)
        {
            ctx.Notify?.Invoke($"已执行 {action.Api}", true);
            ctx.RequestRefresh?.Invoke();
        }
        else
        {
            ctx.Notify?.Invoke(result.Error ?? "执行失败", false);
        }
    }

    /// <summary>JSON 值 → 展示文本（对象/数组做紧凑 JSON，字符串去掉引号）</summary>
    private static string JsonToText(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind switch
            {
                JsonValueKind.String => doc.RootElement.GetString() ?? "",
                JsonValueKind.Null or JsonValueKind.Undefined => "",
                JsonValueKind.Object or JsonValueKind.Array => doc.RootElement.GetRawText(),
                _ => doc.RootElement.ToString(),
            };
        }
        catch
        {
            return json;
        }
    }

    /// <summary>表格数据：只接受数组；元素是对象时按列名取值，是数组时按位置取值</summary>
    private static string[][] ReadRows(PluginViewNode node, PluginViewContext ctx)
    {
        var bind = node.ItemsBind!;
        var raw = bind.Scope == "storage"
            ? StorageJson(bind.Key, ctx)
            : "";
        if (raw.Length == 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var columns = node.Columns;
            var rows = new List<string[]>();
            foreach (var element in doc.RootElement.EnumerateArray())
            {
                if (rows.Count >= MaxTableRows) break;
                var cells = new string[columns.Length];
                for (var c = 0; c < columns.Length; c++)
                {
                    cells[c] = element.ValueKind switch
                    {
                        JsonValueKind.Object when element.TryGetProperty(columns[c], out var v) => JsonToText(v.GetRawText()),
                        JsonValueKind.Array when element.GetArrayLength() > c => JsonToText(element[c].GetRawText()),
                        _ => "",
                    };
                }
                rows.Add(cells);
            }
            return [.. rows];
        }
        catch
        {
            return [];
        }
    }

    /// <summary>list 的数据：数组元素各自的原始 JSON（只支持数组，元素可以是对象/数组/标量）</summary>
    private static string[] ReadItemJson(PluginViewNode node, PluginViewContext ctx)
    {
        var bind = node.ItemsBind!;
        if (bind.Scope != "storage") return [];
        var raw = StorageJson(bind.Key, ctx);
        if (raw.Length == 0) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            return [.. doc.RootElement.EnumerateArray().Select(e => e.GetRawText())];
        }
        catch
        {
            return [];
        }
    }

    private static string StorageJson(string key, PluginViewContext ctx)
    {
        if (!PluginViewParser.IsSafeBindingKey(key)) return "";
        var result = ctx.Host.InvokeAction(ctx.PluginId, "storage.get", Args(key));
        return result.Ok ? result.ResultJson ?? "" : "";
    }

    private static bool ParseBool(string text) =>
        text.Equals("true", StringComparison.OrdinalIgnoreCase) || text == "1";

    // ── 资源与画笔 ──

    private static readonly FontFamily MonoFont = new("Consolas");

    private static T? Res<T>(string key) where T : class =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as T : null;

    private static Brush Brush(string key, Color fallback) =>
        Res<Brush>(key) ?? new SolidColorBrush(fallback);
}
