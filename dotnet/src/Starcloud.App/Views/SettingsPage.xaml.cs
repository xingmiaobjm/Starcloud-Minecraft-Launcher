// 设置页

using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.Storage.Pickers;

namespace Starcloud.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherQueue _dq;
    private bool _loaded;

    private static readonly Regex EnvNameRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private sealed record EnvRow(StackPanel Container, TextBox Name, TextBox Value);
    private readonly List<EnvRow> _globalEnvRows = [];

    public SettingsPage()
    {
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        Loaded += async (_, _) =>
        {
            var s = _s.Settings;
            DotMcBox.Text = s.DotMinecraft;

            // 内存：单滑块（整数 GB），上界为「按物理内存自动分配」值
            var autoGb = Math.Max(1, SystemMemory.AutoMaxMb() / 1024);
            var existingGb = Math.Max(1, s.DefaultMaxMemoryMb / 1024);
            MaxMemorySlider.Maximum = Math.Max(autoGb, existingGb);
            MaxMemorySlider.Value = Math.Clamp(existingGb, 1, MaxMemorySlider.Maximum);
            MaxMemoryText.Text = $"{(int)MaxMemorySlider.Value} GB";
            var physicalGb = SystemMemory.Snapshot().TotalMb / 1024;
            MemoryAutoHint.Text =
                $"1 ~ {(int)MaxMemorySlider.Maximum} GB（物理内存 {physicalGb} GB，自动上限 {autoGb} GB）；最小内存固定 512 MB。";

            // 默认窗口
            DefaultWidthBox.Value = s.DefaultWindowWidth ?? 854;
            DefaultHeightBox.Value = s.DefaultWindowHeight ?? 480;
            DefaultFullscreenSwitch.IsOn = s.DefaultFullscreen;

            // 启动参数
            ExtraJvmArgsBox.Text = string.Join("\n", s.ExtraJvmArgs);
            UseBuiltinJvmSwitch.IsOn = s.UseBuiltinJvmArgs;

            // 全局环境变量
            GlobalEnvPanel.Children.Clear();
            _globalEnvRows.Clear();
            foreach (var (k, v) in s.GlobalEnvVars) AddGlobalEnvRow(k, v, focus: false);

            // Java
            JavaModeRadio.SelectedIndex = string.IsNullOrWhiteSpace(s.JavaExecutable) ? 0 : 1;
            JavaBox.Text = s.JavaExecutable ?? "";
            UpdateJavaModeUi();

            // 下载与资源
            ConcurrencyBox.Value = s.DownloadConcurrency;
            RetriesBox.Value = s.DownloadMaxRetries;
            ResourceCacheSwitch.IsOn = s.ResourceCacheEnabled;

            // 通用
            ThemeCombo.SelectedIndex = s.Theme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };
            OverlaySwitch.IsOn = s.NovaOverlayEnabled;
            IsolationDefaultRadio.SelectedIndex = s.VersionIsolated ? 0 : 1;

            _loaded = true;
            await DetectJavaAsync();
        };
    }

    private void Category_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        // XAML 中首个条目 IsSelected="True" 会在 InitializeComponent 解析期间同步触发本事件，
        // 此时右侧 SectionGame 等控件尚未创建，直接跳过（XAML 已自带正确的初始可见性）
        if (SectionGame is null) return;
        var tag = (CategoryList.SelectedItem as FrameworkElement)?.Tag as string ?? "game";
        SectionGame.Visibility = tag == "game" ? Visibility.Visible : Visibility.Collapsed;
        SectionJava.Visibility = tag == "java" ? Visibility.Visible : Visibility.Collapsed;
        SectionDownload.Visibility = tag == "download" ? Visibility.Visible : Visibility.Collapsed;
        SectionGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 游戏：内存滑块 ──

    private void MaxMemorySlider_ValueChanged(object s, RangeBaseValueChangedEventArgs e)
    {
        if (MaxMemoryText is null) return;
        MaxMemoryText.Text = $"{(int)e.NewValue} GB";
    }

    // ── Java：自动 / 手动 ──

    private void JavaMode_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (JavaWillUseText is null) return;
        UpdateJavaModeUi();
    }

    private void JavaBox_TextChanged(object s, TextChangedEventArgs e)
    {
        if (JavaWillUseText is null) return;
        UpdateJavaModeUi();
    }

    private void UpdateJavaModeUi()
    {
        var manual = JavaModeRadio.SelectedIndex == 1;
        JavaBox.IsEnabled = manual;
        JavaWillUseText.Text = manual
            ? (string.IsNullOrWhiteSpace(JavaBox.Text)
                ? "手动指定：留空时将回退到自动检测到的 Java。"
                : $"手动指定：将使用 {JavaBox.Text.Trim()}")
            : "自动选择：按游戏版本 JSON 推荐的 Java 主版本匹配已安装或已下载的运行时。";
    }

    private async void BrowseJava_Click(object s, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        JavaModeRadio.SelectedIndex = 1;
        JavaBox.Text = file.Path;
    }

    // ── 游戏：全局环境变量 ──

    private void GlobalEnvAdd_Click(object s, RoutedEventArgs e) => AddGlobalEnvRow("", "", focus: true);

    private void AddGlobalEnvRow(string name, string value, bool focus)
    {
        var nameBox = new TextBox
        {
            Text = name,
            PlaceholderText = "名称",
            Width = 200,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueBox = new TextBox
        {
            Text = value,
            PlaceholderText = "值",
            Width = 360,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var del = new Button
        {
            Content = new FontIcon { Glyph = "\uE74D", FontSize = 12 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nameBox, "全局环境变量名称");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(valueBox, "全局环境变量值");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(del, "删除全局环境变量");

        var container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { nameBox, valueBox, del },
        };
        del.Tag = container;
        del.Click += RemoveGlobalEnvRow_Click;

        _globalEnvRows.Add(new EnvRow(container, nameBox, valueBox));
        GlobalEnvPanel.Children.Add(container);
        if (focus) nameBox.Focus(FocusState.Programmatic);
    }

    private void RemoveGlobalEnvRow_Click(object s, RoutedEventArgs e)
    {
        if ((s as FrameworkElement)?.Tag is not StackPanel container) return;
        var row = _globalEnvRows.FirstOrDefault(r => r.Container == container);
        if (row is null) return;
        _globalEnvRows.Remove(row);
        GlobalEnvPanel.Children.Remove(container);
    }

    /// <summary>读取环境变量行 → 字典；跳过空名与非法名。</summary>
    private Dictionary<string, string> ReadGlobalEnv()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var row in _globalEnvRows)
        {
            var name = row.Name.Text.Trim();
            if (name.Length == 0 || !EnvNameRegex.IsMatch(name)) continue;
            dict[name] = row.Value.Text;
        }
        return dict;
    }

    private static List<string> SplitLines(string text) =>
        [.. text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];

    private static int IntOf(NumberBox box, int fallback) =>
        double.IsNaN(box.Value) ? fallback : (int)Math.Round(box.Value);

    // ── 下载与资源：缓存 ──

    private void ClearCache_Click(object s, RoutedEventArgs e)
    {
        try
        {
            _s.ResourceCache.ClearAll();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            return;
        }
        SavedBar.Title = "缓存已清空";
        SavedBar.IsOpen = true;
    }

    // ── Java 检测 ──

    private async Task DetectJavaAsync()
    {
        try
        {
            var runtimes = await _s.Java.ListRuntimesAsync();
            JavaListText.Text = runtimes.Count == 0
                ? "未检测到 Java，可点击上方按钮下载"
                : string.Join("\n", runtimes.Select(r => $"{r.Source} · Java {r.MajorVersion} · {r.VersionString}"));
        }
        catch (Exception ex)
        {
            JavaListText.Text = $"检测失败：{ex.Message}";
        }
    }

    private async void DetectJava_Click(object s, RoutedEventArgs e) => await DetectJavaAsync();

    private async void DownloadJava_Click(object s, RoutedEventArgs e) => await DownloadJavaAsync(21);
    private async void DownloadJava8_Click(object s, RoutedEventArgs e) => await DownloadJavaAsync(8);

    private async Task DownloadJavaAsync(int major)
    {
        JavaProgress.Visibility = Visibility.Visible;
        try
        {
            var progress = new Progress<BatchProgress>(p => _dq.TryEnqueue(() =>
                JavaProgress.Value = p.TotalFiles > 0 ? p.Percentage : 0));
            var exe = await _s.Java.DownloadRuntimeAsync(major, progress);
            JavaModeRadio.SelectedIndex = 1;
            JavaBox.Text = exe;
            await DetectJavaAsync();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
        finally
        {
            JavaProgress.Visibility = Visibility.Collapsed;
        }
    }

    private void Theme_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        var tag = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        ApplyTheme(tag);
    }

    // Nova 游戏内浮窗：切换即落盘（下局游戏启动生效，不追溯当前会话——克制）
    private void OverlaySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.NovaOverlayEnabled = OverlaySwitch.IsOn;
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    private static void ApplyTheme(string theme)
    {
        if (App.MainWindow?.Content is FrameworkElement root)
        {
            root.RequestedTheme = theme switch
            {
                "light" => ElementTheme.Light,
                "dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }

    private void Save_Click(object s, RoutedEventArgs e)
    {
        var s0 = _s.Settings;
        s0.DotMinecraft = string.IsNullOrWhiteSpace(DotMcBox.Text)
            ? s0.DotMinecraft : DotMcBox.Text.Trim();

        // 内存：最小固定 512 MB，最大取滑块（整数 GB）
        s0.DefaultMaxMemoryMb = (int)MaxMemorySlider.Value * 1024;
        s0.DefaultMinMemoryMb = Math.Min(s0.DefaultMinMemoryMb, s0.DefaultMaxMemoryMb);
        if (s0.DefaultMinMemoryMb >= s0.DefaultMaxMemoryMb)
            s0.DefaultMinMemoryMb = Math.Max(512, s0.DefaultMaxMemoryMb / 4);

        // 默认窗口
        s0.DefaultWindowWidth = IntOf(DefaultWidthBox, 854);
        s0.DefaultWindowHeight = IntOf(DefaultHeightBox, 480);
        s0.DefaultFullscreen = DefaultFullscreenSwitch.IsOn;

        // Java：仅手动档位写入路径
        s0.JavaExecutable = JavaModeRadio.SelectedIndex == 1 && !string.IsNullOrWhiteSpace(JavaBox.Text)
            ? JavaBox.Text.Trim() : null;

        // 启动参数
        s0.ExtraJvmArgs = SplitLines(ExtraJvmArgsBox.Text);
        s0.UseBuiltinJvmArgs = UseBuiltinJvmSwitch.IsOn;

        // 下载与资源
        s0.DownloadConcurrency = Math.Max(1, IntOf(ConcurrencyBox, s0.DownloadConcurrency));
        s0.DownloadMaxRetries = Math.Max(0, IntOf(RetriesBox, s0.DownloadMaxRetries));
        s0.ResourceCacheEnabled = ResourceCacheSwitch.IsOn;

        // 全局环境变量
        s0.GlobalEnvVars = ReadGlobalEnv();

        // 通用
        s0.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        s0.VersionIsolated = IsolationDefaultRadio.SelectedIndex == 0;

        s0.Save();
        SavedBar.Title = "已保存";
        SavedBar.IsOpen = true;
    }
}
