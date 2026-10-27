// 首次启动的使用引导（PCL 风格）：顶部横向分页 + 大留白内容区 + 底部纯文字步进。
//
// 时机：主窗口还没创建时先弹这个，用户点「完成」或右上角关闭后才创建并显示主窗口——
// 首次使用的所有决定（语言 / 外观 / 账户 / 游戏目录 / 下载源）都在这里一次做完。
//
// 分页：欢迎 / 外观 / 创建新账户 / 全局游戏设置 / 下载 / 完成。
// 每一步都直接读写 LauncherSettings，不需要额外的「确定」；中途关闭视为跳过剩余步骤（同样算完成）。

using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.Graphics;

namespace Starcloud.App.Views.Dialogs;

public sealed partial class OnboardingWindow : Window
{
    private const int WindowW = 1000;
    private const int WindowH = 640;
    private const double BrandPaneWidth = 280;

    private static readonly string[] TabTitles =
    [
        "欢迎", "外观", "创建新账户", "全局游戏设置", "下载", "完成",
    ];

    /// <summary>一步 = 一个设置项：(所属大类 Tab 下标, 页面标题, 页面说明, 内容构建)</summary>
    private readonly List<(int Tab, string Title, string Desc, Func<UIElement> Build)> _steps = [];

    private readonly AppServices _s = AppServices.Instance;
    private readonly Action _onFinished;
    private readonly List<(TextBlock Text, Border Underline)> _tabs = [];
    private readonly Grid _body = new();
    private readonly Grid _brandPane = new();

    /// <summary>右列（品牌图位）：宽度跟随品牌图可见性，隐藏时收 0 让内容铺满</summary>
    private ColumnDefinition _brandColumn = new() { Width = new GridLength(BrandPaneWidth) };
    private int _index;
    private int _lastIndex;
    private bool _finished;

    private Grid _root = null!;
    private Button _prevBtn = null!;
    private Button _nextBtn = null!;

    public OnboardingWindow(Action onFinished)
    {
        _onFinished = onFinished;
        Title = Loc.T("Starcloud 使用引导");

        // 无边框：内容自绘到标题栏区域（与主窗口同一套做法），顶部整条作为拖动区，
        // 右上角的 × 就是关闭按钮——不再显示系统标题栏。
        ExtendsContentIntoTitleBar = true;
        // 只延伸内容不够：系统标题栏按钮还会画在右上角，会和自绘的 × 叠在一起。
        // 与主窗口一致：保留边框（可拖拽缩放），藏掉系统标题栏本身。
        if (AppWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.SetBorderAndTitleBar(true, false);
            presenter.IsResizable = true;
        }

        var root = new Grid();
        Content = root;
        _root = root;
        ApplyTheme();
        root.Background = PageBrush();

        RegisterSteps();
        Build(root);
        ResizeAndCenter();
        try
        {
            // 与主窗口同一个应用图标（否则任务栏上是通用空白图标）
            AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        }
        catch
        {
            // 图标设不上不影响向导
        }
        ShowPage(0);
    }

    // ── 骨架 ──

    private void Build(Grid root)
    {
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        // 顶部：分页居中 + 右侧关闭
        var header = new Grid { Padding = new Thickness(20, 14, 10, 0) };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var tabBar = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Spacing = 4,
        };
        for (var i = 0; i < TabTitles.Length; i++)
        {
            var index = i;
            var text = new TextBlock
            {
                Text = Loc.T(TabTitles[i]),
                FontSize = 14,
                Padding = new Thickness(14, 8, 14, 10),
            };
            var underline = new Border
            {
                Height = 2,
                Margin = new Thickness(12, 0, 12, 0),
                Background = AccentBrush(),
                Opacity = 0,
                CornerRadius = new CornerRadius(1),
            };
            var item = new StackPanel { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
            item.Children.Add(text);
            item.Children.Add(underline);
            item.PointerEntered += (_, _) => text.Opacity = 0.72;
            item.PointerExited += (_, _) => text.Opacity = 1;
            item.Tapped += (_, _) => JumpToTab(index);
            _tabs.Add((text, underline));
            tabBar.Children.Add(item);
        }
        Grid.SetColumn(tabBar, 1);
        header.Children.Add(tabBar);

        // 顶部这整条是可拖动区（Tab 与关闭按钮仍可点击：SetTitleBar 内的交互控件优先生效）
        SetTitleBar(header);

        var close = new Button
        {
            Content = new FontIcon { Glyph = "\uE711", FontSize = 12 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
        };
        ToolTipService.SetToolTip(close, Loc.T("跳过剩余步骤"));
        close.Click += (_, _) => Finish();
        Grid.SetColumn(close, 2);
        header.Children.Add(close);
        root.Children.Add(header);

        // 内容：可滚动（窗口太矮时不会把控件裁掉）+ 垂直居中（只有一个设置项时视觉重心居中）
        var content = new Grid { Padding = new Thickness(64, 4, 48, 4) };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        // 右列宽度跟随品牌图可见性：隐藏时收 0，内容铺满（此前固定 280 把控件挤裁）
        content.ColumnDefinitions.Add(_brandColumn);

        var scroller = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalContentAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Left,
        };
        _body.VerticalAlignment = VerticalAlignment.Center;
        scroller.Content = _body;
        content.Children.Add(scroller);

        _brandPane.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(_brandPane, 1);
        content.Children.Add(_brandPane);
        Grid.SetRow(content, 1);
        root.Children.Add(content);

        // 底部：‹ 上一步                下一步 ›
        var footer = new Grid { Padding = new Thickness(48, 0, 44, 26) };
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        footer.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _prevBtn = TextNavButton("‹  " + Loc.T("上一步"));
        _prevBtn.Click += (_, _) => ShowPage(_index - 1);
        footer.Children.Add(_prevBtn);

        _nextBtn = TextNavButton(Loc.T("下一步") + "  ›");
        _nextBtn.Click += (_, _) =>
        {
            if (_index >= _steps.Count - 1) Finish();
            else ShowPage(_index + 1);
        };
        Grid.SetColumn(_nextBtn, 2);
        footer.Children.Add(_nextBtn);

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
    }

    private static Button TextNavButton(string text) => new()
    {
        Content = text,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
        BorderThickness = new Thickness(0),
        Padding = new Thickness(10, 6, 10, 6),
        FontSize = 15,
    };

    private void ResizeAndCenter()
    {
        try
        {
            var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
            var w = Math.Min(WindowW, area.Width - 80);
            var h = Math.Min(WindowH, area.Height - 80);
            AppWindow.Resize(new SizeInt32(w, h));
            AppWindow.Move(new PointInt32(
                area.X + Math.Max(0, (area.Width - w) / 2),
                area.Y + Math.Max(0, (area.Height - h) / 2)));
        }
        catch
        {
            // 拿不到工作区就用系统默认位置
        }
    }

    // ── 分页切换 ──

    /// <summary>登记步骤：顶部只显示 6 个大类，每一步只放一个设置项</summary>
    private void RegisterSteps()
    {
        _steps.Add((0, Loc.T("欢迎"), Loc.T("选择您的首选语言，这将作用到后续的 Mod 翻译等功能。"), PageLanguage));
        _steps.Add((1, Loc.T("外观主题"), Loc.T("选择您的外观主题。"), PageTheme));
        _steps.Add((1, Loc.T("界面动效"), Loc.T("越往下动效越完整；低配设备建议「较少」或「极少」。"), PageMotion));
        _steps.Add((2, Loc.T("创建游戏账户"), Loc.T("三种账号任选一种，离线账户最快；也可以先跳过，之后在「账户」页添加。"), PageGameAccount));
        _steps.Add((2, Loc.T("Starcloud ID"), Loc.T("登录 StarcloudID，解锁更多功能。"), PageStarcloudId));
        _steps.Add((3, Loc.T("游戏目录"), Loc.T("用 PCL / HMCL / 官方启动器玩过的话，把那个 .minecraft 接进来，版本和存档都能直接用。"), PageGameDir));
        _steps.Add((4, Loc.T("下载源"), Loc.T("国内网络建议保持「自动」，或直接选 BMCLAPI；官方源在海外。"), PageDownloadSource));
        _steps.Add((4, Loc.T("同时下载数"), Loc.T("网络好可以调高；不稳就保持默认。"), PageConcurrency));
        _steps.Add((5, Loc.T("准备就绪"), Loc.T("下面是这次的选择，之后都能在设置页里修改。点「完成」进入启动器。"), PageFinish));
    }

    private void ShowPage(int index)
    {
        try
        {
            _index = Math.Clamp(index, 0, _steps.Count - 1);
            var current = _steps[_index];

            for (var i = 0; i < _tabs.Count; i++)
            {
                var active = i == current.Tab;
                _tabs[i].Text.FontWeight = active
                    ? Microsoft.UI.Text.FontWeights.SemiBold
                    : Microsoft.UI.Text.FontWeights.Normal;
                _tabs[i].Underline.Opacity = active ? 1 : 0;
            }

            _prevBtn.IsEnabled = _index > 0;
            _prevBtn.Opacity = _index > 0 ? 1 : 0.35;
            _nextBtn.Content = _index == _steps.Count - 1 ? Loc.T("完成") : Loc.T("下一步") + "  ›";

            _body.Children.Clear();
            _brandPane.Children.Clear();
            var showBrand = current.Tab == 0;
            _brandPane.Visibility = showBrand ? Visibility.Visible : Visibility.Collapsed;
            _brandColumn.Width = new GridLength(showBrand ? BrandPaneWidth : 0);

            var page = new StackPanel { Spacing = 12 };
            page.Children.Add(new TextBlock
            {
                Text = current.Title,
                FontSize = 26,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            });
            page.Children.Add(new TextBlock
            {
                Text = current.Desc,
                FontSize = 13,
                Opacity = 0.7,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8),
            });
            try
            {
                page.Children.Add(current.Build());
            }
            catch (Exception ex)
            {
                // 单页构造失败不能把整个向导变成空白页：记日志 + 原地给一句可读提示
                Starcloud.Core.Services.LauncherLog.Error("app", $"向导页面构建失败：{current.Title}", ex);
                page.Children.Add(new TextBlock
                {
                    Text = Loc.T("这个设置项加载失败：{0}", ex.Message),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Opacity = 0.8,
                });
            }
            _body.Children.Add(page);

            // 与主页切换账户方式同一套入场：淡入 + 轻微横向位移（前进从右来、后退从左来）。
            // 极少档位时 MotionFx 内部直接落位，不做动画。
            var forward = index >= _lastIndex;
            MotionFx.FadeSlideIn(page, fromY: 0, durationMs: MotionFx.EnterMs,
                delayMs: 0, fromX: forward ? 26f : -26f);
            _lastIndex = _index;

            // 品牌图只有欢迎类页面展示
            if (current.Tab == 0) _brandPane.Children.Add(BrandImage());
        }
        catch (Exception ex)
        {
            Starcloud.Core.Services.LauncherLog.Error("app", "向导切换页面失败", ex);
            _body.Children.Clear();
            _body.Children.Add(new TextBlock
            {
                Text = Loc.T("这个设置项加载失败：{0}", ex.Message),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Opacity = 0.8,
            });
        }
    }

    /// <summary>点顶部大类 = 跳到该大类的第一个设置项</summary>
    private void JumpToTab(int tab)
    {
        var first = _steps.FindIndex(s => s.Tab == tab);
        if (first >= 0) ShowPage(first);
    }

    // ── 收尾 ──

    /// <summary>完成 / 关闭都走这里（幂等）：写标记 → 关窗 → 通知宿主启动主窗口</summary>
    private void Finish()
    {
        if (_finished) return;
        _finished = true;
        try
        {
            _s.Settings.OnboardingDone = true;
            _s.Settings.Save();
        }
        catch
        {
            // 写不进去也不能把用户卡在向导里
        }
        // 顺序很关键：先让宿主把主窗口建起来，再关向导。
        // 反过来（先 Close 再建窗口）时，最后一个窗口关闭会让 WinUI 进入退役流程，
        // 紧接着创建的 MainWindow 起不来——表现就是「点完成，向导关了，启动器没出现」。
        try { _onFinished(); } catch { /* 宿主兜底 */ }
        try { Close(); } catch { }
    }

    private void Save()
    {
        try { _s.Settings.Save(); } catch { /* 存不下不影响向导继续 */ }
    }

    private void ApplyTheme()
    {
        try { _s.Appearance.ApplyTheme(_s.Settings.Theme, _root); } catch { }
    }

    private Brush PageBrush() => new SolidColorBrush(_root.ActualTheme == ElementTheme.Dark
        ? Windows.UI.Color.FromArgb(255, 32, 32, 32)
        : Windows.UI.Color.FromArgb(255, 250, 250, 250));

    private static Brush AccentBrush()
    {
        if (Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var v) &&
            v is Brush brush)
            return brush;
        return new SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 211, 34));
    }

    private static UIElement SectionTitle(string title, string desc)
    {
        var panel = new StackPanel { Spacing = 3 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
        });
        panel.Children.Add(new TextBlock
        {
            Text = desc,
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
        });
        return panel;
    }
}
