// 内置外壳插件：把 12 个页面与左侧导航登记成贡献点。
//
// 以前外壳里写死两处：MainWindow.xaml 的 NavigationViewItem 列表、
// MainWindow.xaml.cs 里 tag → Page 类型的 switch。两处都不认识插件，
// 加一页就要改两处；插件想挂页面还得另走一条路。
//
// 现在这里一条条登记：
//   Kind.Page    —— 页面（Payload = 页面类型），外壳懒创建
//   Kind.NavItem —— 导航项（PageId 指向页面，Order 排序，Target="footer" 进底部区）
// 外壳只认贡献点，不再认识具体页面类型。停用本插件 = 导航清空（外核对这一点有单测）。

using Starcloud.App.Views;
using Starcloud.Core.Kernel;

namespace Starcloud.App.Shell;

/// <summary>内置外壳插件：12 个页面的导航与页面登记</summary>
public sealed class BuiltinShellPlugin : PluginBase
{
    public const string PluginId = "starcloud.shell";

    public static PluginDescriptor Descriptor { get; } = new()
    {
        Id = PluginId,
        Name = "外壳导航",
        Version = "1.0.0",
        Description = "登记启动器自带的页面与左侧导航项",
        Kind = PluginKind.Ui,
        Activation = PluginActivation.Eager,
        Builtin = true,
        Provides = [],
        Permissions = [],
    };

    /// <summary>一条页面 + 一条导航项；图标要么是字体字形，要么是图片 URI</summary>
    private sealed record Entry(
        string Tag, string Title, Type Page, int Order,
        string? Glyph = null, string? IconUri = null, bool Footer = false, bool Separator = false);

    // 顺序与图标沿用改造前 MainWindow.xaml 里的排布，一个不少
    private static readonly Entry[] Entries =
    [
        new("home",        "首页",     typeof(HomePage),               10, "\uEC77"),
        new("versions",    "版本",     typeof(VersionsPage),           20, "\uEA94"),
        new("download",    "下载",     typeof(DownloadPage),           30, "\uE90C"),
        new("saves",       "存档",     typeof(SavesPage),              40, "\uEA90"),
        new("multiplayer", "联机",     typeof(MultiplayerPage),        50, "\uEB91"),
        new("lab",         "实验室",   typeof(LabPage),                60,
            IconUri: "ms-appx:///Assets/Brand/lab-nav.svg"),
        new("projections", "投影管理", typeof(ProjectionManagerPage),  70,
            IconUri: "ms-appx:///Assets/Brand/projection-nav.svg"),
        new("running",     "运行状态", typeof(RunningStatusPage),      80, "\uEC3B"),
        new("",            "",         typeof(ProjectionManagerPage),  90, Separator: true),
        new("accounts",    "账户",     typeof(AccountsPage),          100, "\uEB8F"),
        new("plugins",     "插件",     typeof(PluginsPage),           110, "\uEABC"),
        new("ai",          "Nova",     typeof(NovaPage),              120,
            IconUri: "ms-appx:///Assets/Brand/starcloud-mark-small.svg"),
        new("logs",        "启动器日志", typeof(LogsPage),             200, "\uEC64", Footer: true),
        new("settings",    "设置",     typeof(SettingsPage),          210, "\uEC04", Footer: true),
    ];

    public override Task InitializeAsync(IPluginContext context, CancellationToken ct = default)
    {
        base.InitializeAsync(context, ct);

        foreach (var e in Entries)
        {
            if (e.Separator)
            {
                context.Contributions.Add(new Contribution(
                    ContributionKind.NavItem, PluginId, $"shell.sep.{e.Order}", Order: e.Order,
                    Target: "separator"));
                continue;
            }

            // 页面：Payload 是类型，外壳按需创建（内核不引用任何 UI 类型）
            context.Contributions.Add(new Contribution(
                ContributionKind.Page, PluginId, $"shell.page.{e.Tag}",
                Title: e.Title, PageId: e.Tag, Order: e.Order, Payload: e.Page));

            context.Contributions.Add(new Contribution(
                ContributionKind.NavItem, PluginId, $"shell.nav.{e.Tag}",
                Title: e.Title, PageId: e.Tag, Order: e.Order,
                Glyph: e.Glyph, IconUri: e.IconUri,
                Target: e.Footer ? "footer" : null));
        }
        return Task.CompletedTask;
    }
}
