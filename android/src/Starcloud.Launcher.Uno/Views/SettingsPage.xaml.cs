// 设置页（移植自桌面 Starcloud.App.Views.SettingsPage，仅做安卓侧适配）：
// - 设置保存统一走 AppServices.Instance.SaveSettings()（PORTING 公约）
// - 主题 / 背景改动后调 ShellRootView.RefreshAppearance()（安卓无窗口背景三层，仅重放主题）
// - 桌面 FileOpenPicker（手动添加 Java / 选择背景图片）在触屏端无对应交互：
//   改为 UiDialog 输入框填写路径，功能语义保留
// - 内置壁纸缩略图：安卓端壁纸经 resizetizer 走 ms-appx 分发（AppearanceService 注释），
//   用 Ui.Asset 取图，不再读文件系统路径

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Controls;
using Starcloud.Launcher.Uno.Views.Dialogs;
using System.Text.RegularExpressions;

namespace Starcloud.Launcher.Uno.Views;

public sealed partial class SettingsPage : Page, IShellPage
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
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_loaded) return; // 缓存实例再次挂树：回填由 OnShown 负责，避免双份回填
        await LoadSettingsAsync();
    }

    /// <summary>页面被导航显示（实例已缓存复用）：首次进入交给 Loaded，之后的每次进入按当前设置重新回填。</summary>
    public void OnShown()
    {
        if (!_loaded) return; // 首次进入交给 Loaded（此刻页面尚未挂树）
        _ = LoadSettingsAsync();
    }

    /// <summary>按当前设置回填全部控件（首次 Loaded 与缓存后每次进入共用），末尾扫一遍 Java 运行时。</summary>
    private async Task LoadSettingsAsync()
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
        MemoryAutoHint.Text = Loc.T("1 ~ {0} GB（物理内存 {1} GB，自动上限 {2} GB）；最小内存固定 512 MB。",
            (int)MaxMemorySlider.Maximum, physicalGb, autoGb);

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

        // Java（下拉列表在执行下面的 ScanJavaAsync 时自动填充）
        _javaOverride = s.JavaExecutable;

        // 下载与资源
        ConcurrencyBox.Value = s.DownloadConcurrency;
        RetriesBox.Value = s.DownloadMaxRetries;
        ResourceCacheSwitch.IsOn = s.ResourceCacheEnabled;
        // 未知 ID 回落「自动（多源聚合）」
        SourceCombo.SelectedIndex = s.DownloadSource?.Trim().ToLowerInvariant() switch
        {
            "official" => 1,
            "bmclapi" => 2,
            "fastmirror" => 3,
            "rainyun" => 4,
            _ => 0,
        };
        CustomMirrorBox.Text = s.DownloadCustomMirrorUrl ?? "";
        CurseForgeKeyBox.Text = s.CurseForgeApiKey ?? "";

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
        LanguageCombo.SelectedIndex =
            string.Equals(s.UiLanguage, "en-US", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
        OverlaySwitch.IsOn = s.NovaOverlayEnabled;
        AutoUpdateSwitch.IsOn = s.AutoUpdateEnabled;
        WalkAnimSwitch.IsOn = s.AccountWalkAnimation;
        EmbeddedLoginSwitch.IsOn = s.MicrosoftLoginEmbedded;
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
        await ScanJavaAsync();
    }

    private void Category_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        // XAML 中首个条目 IsSelected="True" 会在 InitializeComponent 解析期间同步触发本事件，
        // 此时右侧 SectionGame 等控件尚未创建，直接跳过（XAML 已自带正确的初始可见性）
        if (SectionGame is null) return;
        var tag = (CategoryList.SelectedItem as FrameworkElement)?.Tag as string ?? "game";

        // 选中态落到各行控件自己的 IsActive 上：选中底、跟手光斑、圆角都由 CategoryNavItem
        // 的模板提供，三者同源同形，不再出现「阴影框与按钮框错位」
        CatGame.IsActive = tag == "game";
        CatJava.IsActive = tag == "java";
        CatDownload.IsActive = tag == "download";
        CatGeneral.IsActive = tag == "general";

        var target = tag switch
        {
            "java" => SectionJava,
            "download" => SectionDownload,
            "general" => SectionGeneral,
            _ => SectionGame,
        };

        foreach (var section in new[] { SectionGame, SectionJava, SectionDownload, SectionGeneral })
            section.Visibility = ReferenceEquals(section, target) ? Visibility.Visible : Visibility.Collapsed;

        // 进入 Java 页自动重新搜索（本机可能刚装了新 Java）
        if (_loaded && tag == "java") _ = ScanJavaAsync();

        // 各区块高度不同，切页时把滚动位置归零；用动画回滚（框架 ChangeView 的动画参数），比瞬移顺
        if (SectionScroll.VerticalOffset > 0) SectionScroll.ChangeView(null, 0, null, disableAnimation: false);

        // 二级子导航转场：显隐瞬时切换（不做布局动画），只让进入的区块淡入 + 轻微上移
        AnimateSectionIn(target);
    }

    /// <summary>进场时区块的起始下移量（像素）。</summary>
    private const double SectionSlideOffset = 18d;

    /// <summary>
    /// 二级子导航进场动效：只对进入的区块做「淡入 + 轻微上移」。
    /// <para>统一走 MotionFx 的合成层独立动画（与下载页、列表入场同一条曲线与时长），
    /// 不触发布局；动效开关关闭时直接到位。</para>
    /// </summary>
    private void AnimateSectionIn(FrameworkElement section)
    {
        MotionFx.Reset(section);
        MotionFx.FadeSlideIn(section, (float)SectionSlideOffset);
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
        try { _s.SaveSettings(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    private void ShowGameLogSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.ShowGameLogOnLaunch = ShowGameLogSwitch.IsOn;
        try { _s.SaveSettings(); } catch { /* 落盘失败不阻断 */ }
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
            _s.SaveSettings();
            SavedBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    // ── Java：自动搜索 + 下拉选择（下拉第一项 = 自动按版本推荐） ──

    /// <summary>已搜索到的本机运行时。</summary>
    private List<JavaRuntime> _javaRuntimes = [];

    /// <summary>用户选定的 java 可执行文件；null = 自动（按版本 JSON 推荐的主版本匹配）。</summary>
    private string? _javaOverride;

    private void JavaCombo_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        _javaOverride = (JavaCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        // 删除按钮只对启动器自己下载的运行时开放（系统/官方 Java 不能动）
        JavaDeleteBtn.IsEnabled = _javaRuntimes.Any(rt =>
            MatchesOverride(rt.Executable) && rt.Source == "launcher");
    }

    /// <summary>删除启动器下载的运行时（java/&lt;major&gt;/），系统与官方运行时不给删。</summary>
    private async void DeleteJava_Click(object s, RoutedEventArgs e)
    {
        if (JavaCombo.SelectedItem is not ComboBoxItem item || item.Tag is not string exe) return;
        var rt = _javaRuntimes.FirstOrDefault(r =>
            string.Equals(r.Executable, exe, StringComparison.OrdinalIgnoreCase));
        if (rt is null || rt.Source != "launcher") return;

        var dlg = new ThemedDialog
        {
            Title = Loc.T("删除 Java"),
            Content = Loc.T("将从启动器目录删除 Java {0}（{1}）。此操作不可撤销。", rt.MajorVersion, rt.Executable),
            PrimaryButtonText = Loc.T("删除"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Ui.Root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        JavaDeleteBtn.IsEnabled = false;
        try
        {
            JavaService.RemoveRuntime(rt.MajorVersion);
            if (MatchesOverride(rt.Executable)) _javaOverride = null;
            _javaRuntimes.RemoveAll(r =>
                string.Equals(r.Executable, rt.Executable, StringComparison.OrdinalIgnoreCase));
            PopulateJavaCombo();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("删除失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            JavaDeleteBtn.IsEnabled = _javaRuntimes.Any(x =>
                MatchesOverride(x.Executable) && x.Source == "launcher");
        }
    }

    /// <summary>重建下拉：自动项 + 已搜索运行时 +（必要时）手动添加的自定义项，并恢复当前选择。</summary>
    private void PopulateJavaCombo()
    {
        JavaCombo.Items.Clear();
        JavaCombo.Items.Add(new ComboBoxItem { Content = Loc.T("自动选择（按版本推荐）"), Tag = null });
        foreach (var rt in _javaRuntimes)
            JavaCombo.Items.Add(new ComboBoxItem
            {
                Content = $"Java {rt.MajorVersion} · {rt.VersionString} · {rt.Executable}",
                Tag = rt.Executable,
            });
        var overridePath = _javaOverride;
        if (!string.IsNullOrWhiteSpace(overridePath) && !_javaRuntimes.Any(rt => MatchesOverride(rt.Executable)))
            JavaCombo.Items.Add(new ComboBoxItem
            {
                Content = Loc.T("手动添加 · {0}", overridePath),
                Tag = overridePath,
            });

        var index = 0;
        for (var i = 1; i < JavaCombo.Items.Count; i++)
        {
            if (JavaCombo.Items[i] is ComboBoxItem item && MatchesOverride(item.Tag as string))
            {
                index = i;
                break;
            }
        }
        JavaCombo.SelectedIndex = index;
    }

    private bool MatchesOverride(string? exe) =>
        !string.IsNullOrWhiteSpace(_javaOverride) &&
        string.Equals(exe, _javaOverride, StringComparison.OrdinalIgnoreCase);

    // 桌面用 FileOpenPicker 选 .exe 再 ProbeAsync 校验；触屏端无选择器交互（公约），
    // 改为输入框填写路径，校验与下拉回落逻辑与桌面一致
    private async void BrowseJava_Click(object s, RoutedEventArgs e)
    {
        var path = await UiDialog.PromptAsync(
            Loc.T("手动添加 Java"),
            Loc.T("输入 Java 可执行文件（java）的完整路径："),
            Loc.T("确定"), placeholder: "/usr/bin/java");
        if (string.IsNullOrWhiteSpace(path)) return;

        JavaBrowseBtn.IsEnabled = false;
        try
        {
            var rt = await JavaService.ProbeAsync(path.Trim());
            if (rt is null)
            {
                ErrorBar.Message = Loc.T("未能从该文件解析出 Java 版本：\n{0}", path.Trim());
                ErrorBar.IsOpen = true;
                return;
            }
            _javaOverride = rt.Executable;
            if (!_javaRuntimes.Any(x => string.Equals(x.Executable, rt.Executable, StringComparison.OrdinalIgnoreCase)))
                _javaRuntimes.Insert(0, rt);
            PopulateJavaCombo();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("Java 校验失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            JavaBrowseBtn.IsEnabled = true;
        }
    }

    // ── 游戏：全局环境变量 ──

    private void GlobalEnvAdd_Click(object s, RoutedEventArgs e) => AddGlobalEnvRow("", "", focus: true);

    private void AddGlobalEnvRow(string name, string value, bool focus)
    {
        var nameBox = new TextBox
        {
            Text = name,
            PlaceholderText = Loc.T("名称"),
            Width = 200,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var valueBox = new TextBox
        {
            Text = value,
            PlaceholderText = Loc.T("值"),
            Width = 360,
            FontSize = 13,
            VerticalAlignment = VerticalAlignment.Center,
        };
        var del = new GlowButton
        {
            Content = new FontIcon { FontFamily = IconFont.Family,  Glyph = "\uEAA5", FontSize = 12 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nameBox, Loc.T("全局环境变量名称"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(valueBox, Loc.T("全局环境变量值"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(del, Loc.T("删除全局环境变量"));

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
            _s.IconCache.ClearAll();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
            return;
        }
        SavedBar.Title = Loc.T("缓存已清空");
        SavedBar.IsOpen = true;
    }

    // ── Java 自动搜索 ──

    /// <summary>「搜索 Java」按钮：重新扫一遍本机（可能刚装了新 Java）。</summary>
    private async void ScanJava_Click(object s, RoutedEventArgs e) => await ScanJavaAsync(interactive: true);

    private async Task ScanJavaAsync(bool interactive = false)
    {
        if (interactive)
        {
            JavaScanBtn.IsEnabled = false;
            JavaScanBtn.Content = Loc.T("正在搜索…");
        }
        try
        {
            _javaRuntimes = await _s.Java.ListRuntimesAsync();
            PopulateJavaCombo();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("搜索 Java 失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            if (interactive)
            {
                JavaScanBtn.Content = Loc.T("搜索 Java");
                JavaScanBtn.IsEnabled = true;
            }
        }
    }

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
            _javaOverride = exe;
            await ScanJavaAsync();
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
        ShellRootView.RefreshAppearance();
    }

    // 界面动效：切换即落盘（转场在下次导航生效，悬浮反馈在下次进页生效）
    private void Motion_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.UiAnimations =
            (MotionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        try { _s.SaveSettings(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    // 界面语言：切换即时生效（词典热替换 + 已本地化的 XAML 文案重放）
    private void Language_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.UiLanguage =
            (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-CN";
        try { _s.SaveSettings(); } catch { /* 落盘失败不阻断 */ }
        Loc.SetLanguage(_s.Settings.UiLanguage);
        SavedBar.IsOpen = true;
    }

    // Nova 游戏内浮窗：切换即落盘（下局游戏启动生效，不追溯当前会话——克制）
    private void OverlaySwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.NovaOverlayEnabled = OverlaySwitch.IsOn;
        try { _s.SaveSettings(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    // 账户页玩家走路动画：切换即落盘（下次进入账户页生效）
    private void WalkAnimSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.AccountWalkAnimation = WalkAnimSwitch.IsOn;
        try { _s.SaveSettings(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    // 微软登录方式：切换即落盘（下次点「微软登录」时生效，不打断正在进行的登录）
    private void EmbeddedLoginSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.MicrosoftLoginEmbedded = EmbeddedLoginSwitch.IsOn;
        try { _s.SaveSettings(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    // ── 通用：软件更新（设计：.trae/documents/auto-update-feature.md） ──

    // 自动检查更新：切换即落盘（下次启动生效）
    private void AutoUpdateSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.AutoUpdateEnabled = AutoUpdateSwitch.IsOn;
        try { _s.SaveSettings(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    // 手动检查更新：状态写进行内文本，有新版弹 UpdateDialog（弹窗内部自理下载与收尾）
    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        CheckUpdateBtn.IsEnabled = false;
        UpdateStatusText.Text = Loc.T("正在检查更新…");
        try
        {
            var m = await _s.SelfUpdate.FetchManifestAsync();
            if (!SelfUpdateService.IsNewerVersion(m.Version, SelfUpdateService.CurrentVersion()))
            {
                UpdateStatusText.Text = Loc.T("已是最新版本（{0}）", SelfUpdateService.CurrentVersion());
                return;
            }
            UpdateStatusText.Text = "";
            var xamlRoot = Ui.Root;
            if (xamlRoot is not null)
                await UpdateDialog.ShowAsync(xamlRoot, m);
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "";
            ErrorBar.Message = Loc.T("检查失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    // ── 通用：背景与外观（D1，设计 §8） ──

    /// <summary>按 AppearanceService.Wallpapers 生成壁纸缩略图（代码构建，避免 DataTemplate 类型解析）。
    /// 安卓端壁纸随 APK 走 ms-appx 分发（resizetizer），用 Ui.Asset 取图，不读文件系统路径。</summary>
    private void BuildWallpaperList()
    {
        BuiltinWallpaperGrid.Items.Clear();
        foreach (var w in AppearanceService.Wallpapers)
        {
            var thumb = new Image
            {
                Width = 128,
                Height = 72,
                Stretch = Stretch.UniformToFill,
                Source = Ui.Asset($"Assets/Wallpapers/{w.FileName}"),
            };
            var caption = new TextBlock
            {
                Text = w.Name,
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            var cell = new StackPanel { Spacing = 4, Children = { thumb, caption } };
            cell.Tag = w.Id;
            Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(cell, Loc.T("内置壁纸：{0}", w.Name));
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

        // 桌面按 ResolveBackgroundPath（文件系统存在性）判断；安卓端内置壁纸随 APK 分发必然可用，
        // 本地图片模式的文件校验已在 Loaded 的回落逻辑里做过，这里按「模式非关闭」判定。
        var hasWallpaper = mode != "none";
        BgBlurSlider.IsEnabled = hasWallpaper;
        BgOpacitySlider.IsEnabled = hasWallpaper;
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

    // 桌面用 FileOpenPicker 选图片；触屏端无选择器交互（公约），改为输入框填写图片路径
    private async void PickImage_Click(object s, RoutedEventArgs e)
    {
        var path = (await UiDialog.PromptAsync(
            Loc.T("选择图片"),
            Loc.T("输入背景图片的完整路径（jpg / png / bmp / webp）："),
            Loc.T("确定")))?.Trim();
        if (string.IsNullOrWhiteSpace(path)) return;
        if (!File.Exists(path))
        {
            ErrorBar.Message = Loc.T("文件不存在：{0}", path);
            ErrorBar.IsOpen = true;
            return;
        }
        var st = _s.Settings;
        st.BackgroundImagePath = path;
        st.BackgroundMode = "file";
        if (BackgroundModeCombo.SelectedIndex != 2) BackgroundModeCombo.SelectedIndex = 2;
        ImagePathText.Text = path;
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
        ShellRootView.RefreshAppearance();
        QueueBackgroundSave();
    }

    private void MaskFollowTheme_Toggled(object s, RoutedEventArgs e)
    {
        MaskOpacitySlider.IsEnabled = !MaskFollowThemeCheck.IsOn;
        if (!_loaded) return;
        var st = _s.Settings;
        st.BackgroundFollowTheme = MaskFollowThemeCheck.IsOn;
        st.BackgroundMaskOpacity = MaskFollowThemeCheck.IsOn ? null : MaskOpacitySlider.Value;
        ShellRootView.RefreshAppearance();
        PersistBackground();
    }

    /// <summary>背景与外观项即改即存。</summary>
    private void PersistBackground()
    {
        try { _s.SaveSettings(); } catch { /* 落盘失败不阻断 */ }
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

        // Java：下拉里选了具体运行时才写路径，选「自动选择」则留空（null = 自动）
        s0.JavaExecutable = string.IsNullOrWhiteSpace(_javaOverride) ? null : _javaOverride;

        // 启动参数
        s0.ExtraJvmArgs = SplitLines(ExtraJvmArgsBox.Text);
        s0.UseBuiltinJvmArgs = UseBuiltinJvmSwitch.IsOn;

        // 下载与资源
        s0.DownloadConcurrency = Math.Max(1, IntOf(ConcurrencyBox, s0.DownloadConcurrency));
        s0.DownloadMaxRetries = Math.Max(0, IntOf(RetriesBox, s0.DownloadMaxRetries));
        s0.ResourceCacheEnabled = ResourceCacheSwitch.IsOn;
        s0.DownloadSource = (SourceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        s0.DownloadCustomMirrorUrl = CustomMirrorBox.Text.Trim();
        s0.CurseForgeApiKey = CurseForgeKeyBox.Text.Trim();

        // 全局环境变量
        s0.GlobalEnvVars = ReadGlobalEnv();

        // 通用
        s0.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        s0.UiAnimations = (MotionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        s0.UiLanguage = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-CN";
        s0.VersionIsolated = IsolationDefaultRadio.SelectedIndex == 0;
        s0.MicrosoftLoginEmbedded = EmbeddedLoginSwitch.IsOn;

        _s.SaveSettings();
        ShellRootView.RefreshAppearance();
        SavedBar.Title = Loc.T("已保存");
        SavedBar.IsOpen = true;
    }
}
