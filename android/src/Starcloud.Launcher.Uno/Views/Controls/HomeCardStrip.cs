// 首页插件卡片带（规范 2.0 §6.2 contributes.homeCards）：
// 首页是固定视口布局（没有滚动条、卡片与窗口同高），所以卡片带的高度由宿主写死 ——
// 插件不能要求更高，否则一张卡就能把版本列表挤没。
//   · 有插件注册：一行 120 高的等宽卡片带（最多 3 张）；
//   · 没有：整块 Collapsed，高度 0 —— 没装插件的用户看到的首页与从前完全一致。
// 不做收起：注册了就摆着，不想看就在插件页把那个插件停掉（停用即摘除卡片）。
// 卡片内的动作结果经 Notify 交给宿主首页去弹通知条（卡片带自己没有位置显示结果）。
//
// Uno 移植说明：LauncherLog 暂走 Debug 输出（壳工程统一日志门面就位后可切回）；
// Grid 的 Row/ColumnDefinitions 在 Uno 上是只读集合属性，统一用集合初始化器形态。

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Plugins;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Plugins;
using Windows.Foundation;

namespace Starcloud.Launcher.Uno.Views.Controls;

public sealed class HomeCardStrip : Grid
{
    /// <summary>卡片带高度：固定视口下这个数字必须可预测</summary>
    private const double StripHeight = 120;

    /// <summary>卡片间距</summary>
    private const double Gutter = 12;

    /// <summary>一次最多摆几张卡，多的跳过（按插件加载顺序取前几张）</summary>
    private const int MaxCards = 3;

    private readonly Grid _cards = new() { Height = StripHeight, ColumnSpacing = Gutter };

    private PluginHost? _host;

    /// <summary>超限日志只在数量变化时写一行（Render 会被反复调用，不宜每次都写）</summary>
    private int _loggedOverflow = -1;

    /// <summary>卡片内动作的结果提示（宿主首页接通知条；null 则静默）</summary>
    public Action<string, bool>? Notify { get; set; }

    public HomeCardStrip()
    {
        Children.Add(_cards);
        Visibility = Visibility.Collapsed;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // 插件在后台加载：首页启动即渲染，首次 Loaded 时往往还没有贡献，
        // 订阅变化事件，等插件注册完再重建一次（与挂载区同一套做法）
        _host = AppServices.Instance.PluginHost;
        _host.ContributionsChanged += OnContributionsChanged;
        Render();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_host is not null) _host.ContributionsChanged -= OnContributionsChanged;
        _host = null;
    }

    /// <summary>贡献变化可能来自插件后台线程，必须回 UI 线程重建</summary>
    private void OnContributionsChanged() => DispatcherQueue.TryEnqueue(Render);

    /// <summary>重建卡片带（插件启停、卡片内动作之后都走这里）</summary>
    public void Render()
    {
        _cards.Children.Clear();
        _cards.ColumnDefinitions.Clear();

        var all = AppServices.Instance.PluginHost.GetHomeCards();
        // 桌面端写作 `[.. all]`（集合表达式展开）；本工程 LangVersion 固定 12，改等价的 ToList
        var shown = all.Count > MaxCards ? all.Take(MaxCards).ToList() : all.ToList();
        for (var i = 0; i < shown.Count; i++)
        {
            _cards.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var card = BuildCard(shown[i]);
            Grid.SetColumn(card, i);
            _cards.Children.Add(card);
        }

        // 没有插件注册卡片就整块不占位（首页布局与没装插件时一模一样）
        Visibility = shown.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        if (all.Count > MaxCards && all.Count != _loggedOverflow)
        {
            _loggedOverflow = all.Count;
            System.Diagnostics.Debug.WriteLine(
                $"[plugin] 首页卡片共 {all.Count} 张，超出上限，只展示前 {MaxCards} 张（按插件加载顺序）");
        }
        else if (all.Count <= MaxCards)
        {
            _loggedOverflow = -1;
        }
    }

    /// <summary>一张卡：卡头（插件声明的标题）+ 声明式视图内容</summary>
    private FrameworkElement BuildCard(PluginHost.HomeCardRegistration reg)
    {
        var grid = new Grid
        {
            RowSpacing = 8,
            RowDefinitions =
            {
                new RowDefinition { Height = GridLength.Auto },
                new RowDefinition { Height = new GridLength(1, GridUnitType.Star) },
            },
        };

        grid.Children.Add(new TextBlock
        {
            Text = reg.Title,
            FontSize = 15,
            FontWeight = FontWeights.Medium,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });

        // 视图内容高度不定：超出卡片带高度的部分裁掉（固定视口下卡片内不滚动）
        var content = PluginViewRenderer.RenderFile(reg.PluginId, reg.EntryPath, ShowStatus, Render);
        content.VerticalAlignment = VerticalAlignment.Top;
        var clipHost = new Grid();
        clipHost.Children.Add(content);
        clipHost.SizeChanged += (_, e) => clipHost.Clip =
            new RectangleGeometry { Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height) };
        Grid.SetRow(clipHost, 1);
        grid.Children.Add(clipHost);

        return new Border { Style = Res<Style>("ScCard"), Padding = new Thickness(12), Child = grid };
    }

    private void ShowStatus(string message, bool ok) => Notify?.Invoke(message, ok);

    private static T? Res<T>(string key) where T : class =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value as T : null;
}
