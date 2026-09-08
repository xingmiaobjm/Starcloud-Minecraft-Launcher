// 设置页

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class SettingsPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly DispatcherQueue _dq;
    private bool _loaded;

    public SettingsPage()
    {
        InitializeComponent();
        _dq = DispatcherQueue.GetForCurrentThread();
        Loaded += async (_, _) =>
        {
            var s = _s.Settings;
            DotMcBox.Text = s.DotMinecraft;
            MinMemBox.Value = s.DefaultMinMemoryMb;
            MaxMemBox.Value = s.DefaultMaxMemoryMb;
            JavaBox.Text = s.JavaExecutable ?? "";
            ConcurrencyBox.Value = s.DownloadConcurrency;
            ThemeCombo.SelectedIndex = s.Theme switch
            {
                "light" => 1,
                "dark" => 2,
                _ => 0,
            };
            OverlaySwitch.IsOn = s.NovaOverlayEnabled;
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
        SectionGeneral.Visibility = tag == "general" ? Visibility.Visible : Visibility.Collapsed;
    }

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
        s0.DefaultMinMemoryMb = (int)MinMemBox.Value;
        s0.DefaultMaxMemoryMb = (int)MaxMemBox.Value;
        s0.JavaExecutable = string.IsNullOrWhiteSpace(JavaBox.Text) ? null : JavaBox.Text.Trim();
        s0.DownloadConcurrency = (int)ConcurrencyBox.Value;
        s0.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        if (s0.DefaultMinMemoryMb >= s0.DefaultMaxMemoryMb)
            s0.DefaultMinMemoryMb = s0.DefaultMaxMemoryMb / 4;
        s0.Save();
        SavedBar.IsOpen = true;
    }
}
