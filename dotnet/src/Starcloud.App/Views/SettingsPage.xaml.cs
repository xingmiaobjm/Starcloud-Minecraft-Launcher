// 设置页

using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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

    private DispatcherQueueTimer? _bgSaveTimer;

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

            // 启动可靠性：自动重试 / 实时日志开关
            LaunchAutoRetrySwitch.IsOn = s.LaunchAutoRetry;
            ShowGameLogSwitch.IsOn = s.ShowGameLogOnLaunch;
            RefreshMemoryRecommend();

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
            MotionCombo.SelectedIndex = s.UiAnimations switch
            {
                "on" => 1,
                "off" => 2,
                _ => 0,
            };
            OverlaySwitch.IsOn = s.NovaOverlayEnabled;
            IsolationDefaultRadio.SelectedIndex = s.VersionIsolated ? 0 : 1;

            // 背景与外观（D1）
            BackgroundModeCombo.SelectedIndex = s.BackgroundMode switch
            {
                "builtin" => 1,
                "file" => 2,
                _ => 0,
            };
            BuildWallpaperList();
            SelectBuiltinWallpaper(s.BackgroundBuiltinId);
            BgBlurSlider.Value = Math.Clamp(s.BackgroundBlurRadius, BgBlurSlider.Minimum, BgBlurSlider.Maximum);
            BgOpacitySlider.Value = Math.Clamp(s.BackgroundImageOpacity, BgOpacitySlider.Minimum, BgOpacitySlider.Maximum);
            BgBlurText.Text = $"{(int)Math.Round(BgBlurSlider.Value)} px";
            BgOpacityText.Text = $"{BgOpacitySlider.Value:P0}";
            MaskFollowThemeCheck.IsOn = s.BackgroundFollowTheme;
            MaskOpacitySlider.Value = Math.Clamp(AppearanceService.ResolveMaskOpacity(s, ActualTheme), 0, 1);
            MaskOpacitySlider.IsEnabled = !s.BackgroundFollowTheme;
            ImagePathText.Text = s.BackgroundImagePath ?? "";
            // 本地图片已失效（文件被删/移动）→ 静默回落「关闭」，不弹错误
            if (s.BackgroundMode == "file" && s.ResolvedBackgroundPath() is null)
            {
                s.BackgroundMode = "none";
                BackgroundModeCombo.SelectedIndex = 0;
                ImagePathText.Text = "";
            }
            UpdateBackgroundPanels();

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

    private int _suggestMaxMemoryMb;

    private void MaxMemorySlider_ValueChanged(object s, RangeBaseValueChangedEventArgs e)
    {
        if (MaxMemoryText is null) return;
        MaxMemoryText.Text = $"{(int)e.NewValue} GB";
    }

    // ── 游戏：启动可靠性 ──

    private void LaunchAutoRetrySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.LaunchAutoRetry = LaunchAutoRetrySwitch.IsOn;
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    private void ShowGameLogSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.ShowGameLogOnLaunch = ShowGameLogSwitch.IsOn;
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    /// <summary>按物理内存 + 全局 mods 探测刷新智能内存推荐文案（spec §8.2）。</summary>
    private void RefreshMemoryRecommend()
    {
        var dotMc = _s.Settings.DotMinecraft;
        var (modCount, hasShaders) = MemoryAdvisor.Probe(dotMc);
        var totalMb = SystemMemory.Snapshot().TotalMb;
        _suggestMaxMemoryMb = MemoryAdvisor.SuggestMaxMb(totalMb, modCount, hasShaders);
        MemoryRecommendText.Text = MemoryAdvisor.Explain(totalMb, modCount, hasShaders);
    }

    /// <summary>应用推荐值到全局默认最大内存（同步滑块显示并落盘）。</summary>
    private void ApplyMemoryRecommend_Click(object s, RoutedEventArgs e)
    {
        try
        {
            var suggestGb = Math.Max(1, (int)Math.Round(_suggestMaxMemoryMb / 1024.0));
            MaxMemorySlider.Maximum = Math.Max(MaxMemorySlider.Maximum, suggestGb);
            MaxMemorySlider.Value = Math.Clamp(suggestGb, 1, MaxMemorySlider.Maximum);
            _s.Settings.DefaultMaxMemoryMb = (int)MaxMemorySlider.Value * 1024;
            _s.Settings.DefaultMinMemoryMb = Math.Min(_s.Settings.DefaultMinMemoryMb, _s.Settings.DefaultMaxMemoryMb);
            _s.Settings.Save();
            SavedBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
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
        _s.Settings.Theme = tag;   // 内存生效，落盘仍由「保存设置」负责（不改现有行为）
        MainWindow.RefreshAppearance();
    }

    // 界面动效：切换即落盘（转场在下次导航生效，悬浮反馈在下次进页生效）
    private void Motion_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.UiAnimations =
            (MotionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    // Nova 游戏内浮窗：切换即落盘（下局游戏启动生效，不追溯当前会话——克制）
    private void OverlaySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.NovaOverlayEnabled = OverlaySwitch.IsOn;
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    // ── 通用：背景与外观（D1，设计 §8） ──

    /// <summary>按 AppearanceService.Wallpapers 生成壁纸缩略图（代码构建，避免 DataTemplate 类型解析）。</summary>
    private void BuildWallpaperList()
    {
        BuiltinWallpaperGrid.Items.Clear();
        foreach (var w in AppearanceService.Wallpapers)
        {
            var path = Path.Combine(AppearanceService.WallpaperDirectory, w.FileName);
            var thumb = new Image
            {
                Width = 128,
                Height = 72,
                Stretch = Stretch.UniformToFill,
                Source = File.Exists(path) ? new BitmapImage(new Uri(path)) : null,
            };
            var caption = new TextBlock
            {
                Text = w.Name,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var cell = new StackPanel { Spacing = 4, Children = { thumb, caption } };
            cell.Tag = w.Id;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, $"内置壁纸：{w.Name}");
            BuiltinWallpaperGrid.Items.Add(cell);
        }
    }

    /// <summary>选中指定内置壁纸（id 非法时回落到第一张）。</summary>
    private void SelectBuiltinWallpaper(string? id)
    {
        var effective = AppearanceService.FindWallpaper(id)?.Id ?? AppearanceService.Wallpapers[0].Id;
        foreach (var item in BuiltinWallpaperGrid.Items)
        {
            if (item is FrameworkElement fe && (fe.Tag as string) == effective)
            {
                BuiltinWallpaperGrid.SelectedItem = item;
                return;
            }
        }
    }

    /// <summary>按当前模式联动两张子面板与「清除」按钮可用性。</summary>
    private void UpdateBackgroundPanels()
    {
        var mode = _s.Settings.BackgroundMode;
        BuiltinWallpaperPanel.Visibility = mode == "builtin" ? Visibility.Visible : Visibility.Collapsed;
        LocalImagePanel.Visibility = mode == "file" ? Visibility.Visible : Visibility.Collapsed;
        ClearImageBtn.IsEnabled = !string.IsNullOrWhiteSpace(_s.Settings.BackgroundImagePath);
    }

    private void BackgroundMode_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        var tag = (BackgroundModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "none";
        var st = _s.Settings;
        st.BackgroundMode = tag;
        // 首次切到内置壁纸但尚未选过 → 默认第一张，避免「选中内置却没有背景」
        if (tag == "builtin" && AppearanceService.FindWallpaper(st.BackgroundBuiltinId) is null)
        {
            st.BackgroundBuiltinId = AppearanceService.Wallpapers[0].Id;
            SelectBuiltinWallpaper(st.BackgroundBuiltinId);
        }
        UpdateBackgroundPanels();
        PersistBackground();
    }

    private void BuiltinWallpaper_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        if (BuiltinWallpaperGrid.SelectedItem is not FrameworkElement fe || fe.Tag is not string id) return;
        var st = _s.Settings;
        if (st.BackgroundMode == "builtin" && st.BackgroundBuiltinId == id)
        {
            UpdateBackgroundPanels();
            return;
        }
        st.BackgroundBuiltinId = id;
        st.BackgroundMode = "builtin";
        if (BackgroundModeCombo.SelectedIndex != 1) BackgroundModeCombo.SelectedIndex = 1;
        UpdateBackgroundPanels();
        PersistBackground();
    }

    private async void PickImage_Click(object s, RoutedEventArgs e)
    {
        if (App.MainWindow is null) return;
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in new[] { ".jpg", ".jpeg", ".png", ".bmp", ".webp" })
            picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        var st = _s.Settings;
        st.BackgroundImagePath = file.Path;
        st.BackgroundMode = "file";
        if (BackgroundModeCombo.SelectedIndex != 2) BackgroundModeCombo.SelectedIndex = 2;
        ImagePathText.Text = file.Path;
        UpdateBackgroundPanels();
        PersistBackground();
    }

    private void ClearImage_Click(object s, RoutedEventArgs e)
    {
        var st = _s.Settings;
        st.BackgroundImagePath = null;
        st.BackgroundMode = "none";
        BackgroundModeCombo.SelectedIndex = 0;
        ImagePathText.Text = "";
        UpdateBackgroundPanels();
        PersistBackground();
    }

    private void BackgroundSlider_Changed(object s, RangeBaseValueChangedEventArgs e)
    {
        if (BgBlurText is null || BgOpacityText is null) return;
        BgBlurText.Text = $"{(int)Math.Round(BgBlurSlider.Value)} px";
        BgOpacityText.Text = $"{BgOpacitySlider.Value:P0}";
        if (!_loaded) return;
        var st = _s.Settings;
        st.BackgroundBlurRadius = BgBlurSlider.Value;
        st.BackgroundImageOpacity = BgOpacitySlider.Value;
        st.BackgroundMaskOpacity = MaskFollowThemeCheck.IsOn ? null : MaskOpacitySlider.Value;
        MainWindow.RefreshAppearance();
        QueueBackgroundSave();
    }

    private void MaskFollowTheme_Toggled(object s, RoutedEventArgs e)
    {
        MaskOpacitySlider.IsEnabled = !MaskFollowThemeCheck.IsOn;
        if (!_loaded) return;
        var st = _s.Settings;
        st.BackgroundFollowTheme = MaskFollowThemeCheck.IsOn;
        st.BackgroundMaskOpacity = MaskFollowThemeCheck.IsOn ? null : MaskOpacitySlider.Value;
        MainWindow.RefreshAppearance();
        PersistBackground();
    }

    /// <summary>背景与外观项即改即存。</summary>
    private void PersistBackground()
    {
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    /// <summary>滑块拖动期间节流落盘（400ms 无变更后写一次），预览仍逐帧生效。</summary>
    private void QueueBackgroundSave()
    {
        if (_bgSaveTimer is null)
        {
            _bgSaveTimer = _dq.CreateTimer();
            _bgSaveTimer.Interval = TimeSpan.FromMilliseconds(400);
            _bgSaveTimer.IsRepeating = false;
            _bgSaveTimer.Tick += (_, _) => PersistBackground();
        }
        _bgSaveTimer.Stop();
        _bgSaveTimer.Start();
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
        s0.UiAnimations = (MotionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        s0.VersionIsolated = IsolationDefaultRadio.SelectedIndex == 0;

        s0.Save();
        MainWindow.RefreshAppearance();
        SavedBar.Title = "已保存";
        SavedBar.IsOpen = true;
    }
}
