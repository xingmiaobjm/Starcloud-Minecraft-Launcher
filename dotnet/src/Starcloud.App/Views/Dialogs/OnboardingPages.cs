// 首次向导的各页面：每一步只放一个设置项（顶部 Tab 只是大类，不随步数增加）。
// 与 OnboardingWindow 主体拆成 partial，避免单文件过长；页面里只做「控件 + 即时写设置」。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.App.Localization;
using Starcloud.Core.Services;

namespace Starcloud.App.Views.Dialogs;

public sealed partial class OnboardingWindow
{
    // ── 欢迎 · 语言 ──

    private UIElement PageLanguage()
    {
        var lang = new ComboBox { Width = 320 };
        lang.Items.Add(new ComboBoxItem { Content = Loc.T("简体中文") });
        lang.Items.Add(new ComboBoxItem { Content = "English" });
        lang.SelectedIndex = (_s.Settings.UiLanguage ?? "").StartsWith("en", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        lang.SelectionChanged += (_, _) =>
        {
            _s.Settings.UiLanguage = lang.SelectedIndex == 1 ? "en-US" : "zh-CN";
            Save();
        };
        return lang;
    }

    /// <summary>欢迎页右侧的品牌图（随主题切深浅两版）</summary>
    private Image BrandImage()
    {
        var brand = new Image { Width = 210, Stretch = Stretch.Uniform };
        try
        {
            var dark = _root.ActualTheme == ElementTheme.Dark;
            brand.Source = new SvgImageSource(new Uri(
                $"ms-appx:///Assets/Brand/{(dark ? "starcloud-mark-dark" : "starcloud-mark-light")}.svg"));
        }
        catch
        {
            // 图加载失败不影响向导
        }
        return brand;
    }

    // ── 外观 · 主题 ──

    private UIElement PageTheme()
    {
        var theme = new RadioButtons { MaxColumns = 1 };
        theme.Items.Add(new RadioButton { Content = Loc.T("跟随系统"), Tag = "system" });
        theme.Items.Add(new RadioButton { Content = Loc.T("浅色"), Tag = "light" });
        theme.Items.Add(new RadioButton { Content = Loc.T("深色"), Tag = "dark" });
        theme.SelectedIndex = (_s.Settings.Theme ?? "system") switch
        {
            "light" => 1,
            "dark" => 2,
            _ => 0,
        };
        theme.SelectionChanged += (_, _) =>
        {
            if (theme.SelectedItem is RadioButton { Tag: string id })
            {
                _s.Settings.Theme = id;
                Save();
                ApplyTheme();
                _root.Background = PageBrush();
            }
        };
        return theme;
    }

    // ── 外观 · 动效 ──

    private UIElement PageMotion()
    {
        var motion = new ComboBox { Width = 320 };
        motion.Items.Add(new ComboBoxItem { Content = Loc.T("极少（仅页面切换）"), Tag = "minimal" });
        motion.Items.Add(new ComboBoxItem { Content = Loc.T("较少"), Tag = "lite" });
        motion.Items.Add(new ComboBoxItem { Content = Loc.T("均衡（推荐）"), Tag = "balanced" });
        motion.Items.Add(new ComboBoxItem { Content = Loc.T("全部"), Tag = "full" });
        motion.SelectedIndex = _s.Settings.UiMotionLevel switch
        {
            "minimal" => 0,
            "lite" => 1,
            "full" => 3,
            _ => 2,
        };
        motion.SelectionChanged += (_, _) =>
        {
            if (motion.SelectedItem is ComboBoxItem { Tag: string id })
            {
                _s.Settings.UiMotionLevel = id;
                Save();
            }
        };
        return motion;
    }

    // ── 创建新账户 · 游戏账户 ──

    private UIElement PageGameAccount()
    {
        var panel = new StackPanel { Spacing = 10 };
        var status = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };

        var kinds = new RadioButtons { MaxColumns = 1 };
        // 正版排第一且默认选中（用户点名：首选项必须是正版）
        kinds.Items.Add(new RadioButton { Content = Loc.T("正版账户（Microsoft）"), Tag = "microsoft" });
        kinds.Items.Add(new RadioButton { Content = Loc.T("离线账户（最快）"), Tag = "offline" });
        kinds.Items.Add(new RadioButton { Content = Loc.T("外置账户（皮肤站）"), Tag = "external" });
        kinds.SelectedIndex = 0;
        panel.Children.Add(kinds);

        var offline = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        var name = new TextBox { PlaceholderText = Loc.T("游戏内昵称（英文 / 数字）"), Width = 300 };
        var create = new Button { Content = Loc.T("创建离线账户"), HorizontalAlignment = HorizontalAlignment.Left };
        create.Click += (_, _) =>
        {
            var value = name.Text.Trim();
            if (!AccountStore.IsValidOfflineName(value))
            {
                status.Text = Loc.T("昵称只能用英文字母、数字或下划线，长度 3–16。");
                return;
            }
            _s.Accounts.AddOrUpdateOffline(value);
            _s.Accounts.Save();
            status.Text = Loc.T("已创建离线账户：{0}", value);
        };
        offline.Children.Add(name);
        offline.Children.Add(create);

        var microsoft = new StackPanel { Spacing = 8 };
        var ms = new Button { Content = Loc.T("使用 Microsoft 账户登录"), HorizontalAlignment = HorizontalAlignment.Left };
        ms.Click += async (_, _) =>
        {
            var result = await AuthDialogs.MicrosoftDeviceCodeAsync(
                Content.XamlRoot, _s.MicrosoftAuth, _s.Accounts, t => status.Text = t);
            if (result is not null) status.Text = Loc.T("已登录正版账户：{0}", result.Username);
        };
        microsoft.Children.Add(ms);

        var external = new StackPanel { Spacing = 8, Visibility = Visibility.Collapsed };
        external.Children.Add(new TextBlock
        {
            Text = Loc.T("外置登录的表单在「账户」页：填皮肤站地址与账号即可。向导里不展开，避免输错。"),
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 460,
        });

        panel.Children.Add(offline);
        panel.Children.Add(microsoft);
        panel.Children.Add(external);
        panel.Children.Add(status);

        kinds.SelectionChanged += (_, _) =>
        {
            var tag = (kinds.SelectedItem as RadioButton)?.Tag as string ?? "microsoft";
            offline.Visibility = tag == "offline" ? Visibility.Visible : Visibility.Collapsed;
            microsoft.Visibility = tag == "microsoft" ? Visibility.Visible : Visibility.Collapsed;
            external.Visibility = tag == "external" ? Visibility.Visible : Visibility.Collapsed;
        };
        return panel;
    }

    // ── 创建新账户 · Starcloud ID ──

    private UIElement PageStarcloudId()
    {
        var panel = new StackPanel { Spacing = 8 };
        var status = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 };

        if (_s.StarcloudId.IsLoggedIn)
            status.Text = Loc.T("已登录：{0}", _s.StarcloudId.Current?.DisplayName ?? "");

        var id = new TextBox { PlaceholderText = "Starcloud ID", Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        var pwd = new PasswordBox { PlaceholderText = Loc.T("密码"), Width = 300, HorizontalAlignment = HorizontalAlignment.Left };
        var login = new Button { Content = Loc.T("登录"), HorizontalAlignment = HorizontalAlignment.Left };
        var register = new Button { Content = Loc.T("注册 Starcloud ID"), HorizontalAlignment = HorizontalAlignment.Left };
        register.Click += async (_, _) =>
        {
            // 注册走官网（用户指定入口），注册完回到这里登录
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://starcloudfur.cn/login"));
            status.Text = Loc.T("已打开官网注册页，完成注册后回到这里登录。");
        };
        login.Click += async (_, _) =>
        {
            login.IsEnabled = false;
            status.Text = Loc.T("正在登录 Starcloud ID…");
            try
            {
                var session = await _s.StarcloudId.LoginAsync(id.Text.Trim(), pwd.Password);
                status.Text = Loc.T("已登录 Starcloud ID：{0}", session.DisplayName);
                pwd.Password = "";
            }
            catch (Exception ex)
            {
                status.Text = ex.Message;
            }
            finally
            {
                login.IsEnabled = true;
            }
        };
        panel.Children.Add(id);
        panel.Children.Add(pwd);
        panel.Children.Add(login);
        panel.Children.Add(register);
        panel.Children.Add(status);
        return panel;
    }

    // ── 全局游戏设置 · 游戏目录 ──

    private UIElement PageGameDir()
    {
        var panel = new StackPanel { Spacing = 8 };
        var dirText = new TextBlock
        {
            Text = _s.Settings.DotMinecraft,
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8,
            MaxWidth = 480,
        };
        void Apply(string path)
        {
            try
            {
                var full = Path.GetFullPath(path);
                Directory.CreateDirectory(full);
                _s.Settings.DotMinecraft = full;
                var known = _s.Settings.KnownDotMinecraftDirs.Where(d => !string.IsNullOrWhiteSpace(d)).ToList();
                if (!known.Contains(full, StringComparer.OrdinalIgnoreCase)) known.Insert(0, full);
                _s.Settings.KnownDotMinecraftDirs = known;
                Save();
                dirText.Text = full;
            }
            catch (Exception ex)
            {
                dirText.Text = ex.Message;
            }
        }
        var scan = new Button { Content = Loc.T("扫描并导入 .minecraft（可多选）"), HorizontalAlignment = HorizontalAlignment.Left };
        scan.Click += async (_, _) =>
        {
            foreach (var picked in await MinecraftFinderDialog.ShowMultipleAsync(Content.XamlRoot))
                Apply(picked);
        };
        var useDefault = new Button { Content = Loc.T("使用默认目录"), HorizontalAlignment = HorizontalAlignment.Left };
        useDefault.Click += (_, _) => Apply(PlatformInfo.DefaultDotMinecraft);

        panel.Children.Add(dirText);
        panel.Children.Add(scan);
        panel.Children.Add(useDefault);
        return panel;
    }

    // ── 全局游戏设置 · 最大内存 ──

    // ── 下载 · 下载源 ──

    private UIElement PageDownloadSource()
    {
        var source = new ComboBox { Width = 320 };
        source.Items.Add(new ComboBoxItem { Content = Loc.T("自动（推荐）"), Tag = "auto" });
        source.Items.Add(new ComboBoxItem { Content = "BMCLAPI", Tag = "bmclapi" });
        source.Items.Add(new ComboBoxItem { Content = "FastMirror", Tag = "fastmirror" });
        source.Items.Add(new ComboBoxItem { Content = "Rainyun", Tag = "rainyun" });
        source.Items.Add(new ComboBoxItem { Content = Loc.T("官方源"), Tag = "official" });
        var ids = new[] { "auto", "bmclapi", "fastmirror", "rainyun", "official" };
        source.SelectedIndex = Math.Max(0, Array.IndexOf(ids, (_s.Settings.DownloadSource ?? "auto").ToLowerInvariant()));
        source.SelectionChanged += (_, _) =>
        {
            if (source.SelectedItem is ComboBoxItem { Tag: string id })
            {
                _s.Settings.DownloadSource = id;
                Save();
            }
        };
        return source;
    }

    // ── 下载 · 同时下载数 ──

    private UIElement PageConcurrency()
    {
        var concurrency = new ComboBox { Width = 160 };
        foreach (var n in new[] { 8, 16, 24, 32 })
            concurrency.Items.Add(new ComboBoxItem { Content = n.ToString(), Tag = n });
        concurrency.SelectedIndex = _s.Settings.DownloadConcurrency switch
        {
            <= 8 => 0,
            <= 16 => 1,
            <= 24 => 2,
            _ => 3,
        };
        concurrency.SelectionChanged += (_, _) =>
        {
            if (concurrency.SelectedItem is ComboBoxItem { Tag: int n })
            {
                _s.Settings.DownloadConcurrency = n;
                Save();
            }
        };
        return concurrency;
    }

    // ── 完成 · 摘要 ──

    private UIElement PageFinish()
    {
        var summary = new StackPanel { Spacing = 6 };
        void Line(string label, string value) =>
            summary.Children.Add(new TextBlock
            {
                Text = label + "：" + value,
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 480,
            });

        Line(Loc.T("语言"), _s.Settings.UiLanguage ?? "zh-CN");
        Line(Loc.T("主题"), _s.Settings.Theme switch
        {
            "light" => Loc.T("浅色"),
            "dark" => Loc.T("深色"),
            _ => Loc.T("跟随系统"),
        });
        Line(Loc.T("游戏账户"), _s.Accounts.Accounts.Count > 0
            ? string.Join("、", _s.Accounts.Accounts.Take(3).Select(a => a.Username))
            : Loc.T("尚未添加（之后可在「账户」页添加）"));
        Line("Starcloud ID", _s.StarcloudId.IsLoggedIn
            ? _s.StarcloudId.Current?.DisplayName ?? Loc.T("已登录")
            : Loc.T("未登录"));
        Line(Loc.T("游戏目录"), _s.Settings.DotMinecraft);
        Line(Loc.T("下载源"), _s.Settings.DownloadSource ?? "auto");
        return summary;
    }
}
