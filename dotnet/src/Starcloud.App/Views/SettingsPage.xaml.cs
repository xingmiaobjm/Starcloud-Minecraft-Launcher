// 设置页

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Behaviors;
using Starcloud.App.Views.Controls;
using Starcloud.App.Views.Dialogs;
using Starcloud.Core.Services;
using Starcloud.Core.Services.P2P;
using System.Text.RegularExpressions;
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
        // 动效说明是按当前选择动态拼的（不走 loc:Loc 附加属性），切语言时手动重放一次
        Loc.LanguageChanged += UpdateMotionHint;
        Unloaded += (_, _) => Loc.LanguageChanged -= UpdateMotionHint;
        Loaded += async (_, _) =>
        {
            var s = _s.Settings;

            // 内存：单滑块（整数 GB）——上界 = 本机物理内存，下界 1 GB
            var physicalGb = Math.Max(1, (int)(SystemMemory.Snapshot().TotalMb / 1024));
            var autoGb = Math.Clamp(SystemMemory.AutoMaxMb() / 1024, 1, physicalGb);
            var existingGb = Math.Clamp(s.DefaultMaxMemoryMb / 1024, 1, physicalGb);
            MaxMemorySlider.Maximum = physicalGb;
            MaxMemorySlider.Value = existingGb;
            MaxMemoryText.Text = $"{(int)MaxMemorySlider.Value} GB";
            MemoryAutoHint.Text = Loc.T("1 ~ {0} GB（本机物理内存 {1} GB，推荐约 {2} GB）。",
                physicalGb, physicalGb, autoGb);

            // 启动可靠性：自动重试 / 实时日志开关
            LaunchAutoRetrySwitch.IsOn = s.LaunchAutoRetry;
            ShowGameLogSwitch.IsOn = s.ShowGameLogOnLaunch;
            RefreshMemoryRecommend();
            _ = InitJavaVendorsAsync();

            // 正版账户：登录令牌加密落盘开关（默认开）
            MsTokenPersistSwitch.IsOn = s.MicrosoftTokenPersist;

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
            AutoModDepsSwitch.IsOn = s.AutoInstallModDependencies;
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
            MotionCombo.SelectedIndex = s.UiMotionLevel switch
            {
                "minimal" => 0,
                "lite" => 1,
                "full" => 3,
                _ => 2,
            };
            UpdateMotionHint();
            LanguageCombo.SelectedIndex =
                string.Equals(s.UiLanguage, "en-US", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            OverlaySwitch.IsOn = s.NovaOverlayEnabled;
            AutoUpdateSwitch.IsOn = s.AutoUpdateEnabled;
            WalkAnimSwitch.IsOn = s.AccountWalkAnimation;
            EmbeddedLoginSwitch.IsOn = s.MicrosoftLoginEmbedded;
            IsolationDefaultRadio.SelectedIndex = s.VersionIsolated ? 0 : 1;

            // 跨设备与 P2P
            DeviceLinkSwitch.IsOn = s.DeviceLinkEnabled;
            P2pSwitch.IsOn = s.P2pDownloadEnabled;
            P2pCrossSwitch.IsOn = s.DeviceP2PCrossAccount;
            P2pInternetSwitch.IsOn = s.P2pInternetEnabled;
            P2pUploadBox.SelectedIndex = s.P2pInternetUploadMbps switch
            {
                <= 1 => 0,
                <= 2 => 1,
                <= 4 => 2,
                <= 8 => 3,
                _ => 4,
            };
            DeviceNameBox.Text = s.DeviceName ?? "";
            UpdateSyncLoginHint();
            RefreshLinkStatus();
            StartPeerTimer();
            RefreshPeers();

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
        };
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
        CatCross.IsActive = tag == "crossdev";

        var target = tag switch
        {
            "java" => SectionJava,
            "download" => SectionDownload,
            "general" => SectionGeneral,
            "crossdev" => SectionCross,
            _ => SectionGame,
        };

        foreach (var section in new[] { SectionGame, SectionJava, SectionDownload, SectionGeneral, SectionCross })
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

    // ── Java：自动搜索 + 下拉选择（下拉第一项 = 自动按版本推荐） ──

    /// <summary>已搜索到的本机运行时。</summary>
    private List<JavaRuntime> _javaRuntimes = [];

    /// <summary>用户选定的 java.exe；null = 自动（按版本 JSON 推荐的主版本匹配）。</summary>
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
            XamlRoot = Content.XamlRoot,
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

    private async void BrowseJava_Click(object s, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".exe");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        JavaBrowseBtn.IsEnabled = false;
        try
        {
            var rt = await JavaService.ProbeAsync(file.Path);
            if (rt is null)
            {
                ErrorBar.Message = Loc.T("未能从该文件解析出 Java 版本：\n{0}", file.Path);
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

    // ── Java 运行时下载：发行版（Temurin / 龙井）+ 版本列表 ──

    /// <summary>初始化发行版下拉并加载版本列表（页面加载时调用一次）</summary>
    private async Task InitJavaVendorsAsync()
    {
        if (JavaVendorCombo.Items.Count > 0) return;
        JavaVendorCombo.Items.Add(new ComboBoxItem
        {
            Content = JavaDistributionsService.Name(JavaVendor.Temurin), Tag = JavaVendor.Temurin,
        });
        JavaVendorCombo.Items.Add(new ComboBoxItem
        {
            Content = JavaDistributionsService.Name(JavaVendor.Dragonwell), Tag = JavaVendor.Dragonwell,
        });
        JavaVendorCombo.SelectedIndex = 0;
        await LoadJavaVersionsAsync();
    }

    private async void JavaVendor_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        await LoadJavaVersionsAsync();
    }

    /// <summary>按当前发行版拉可用版本；失败时把原因说清楚（连不上 / 404 都能看出来）</summary>
    private async Task LoadJavaVersionsAsync()
    {
        JavaVersionCombo.Items.Clear();
        JavaVendorStatus.Text = Loc.T("正在获取可用版本…");
        JavaDownloadBtn.IsEnabled = false;
        try
        {
            var vendor = (JavaVendorCombo.SelectedItem as ComboBoxItem)?.Tag as JavaVendor? ?? JavaVendor.Temurin;
            var releases = await _s.JavaDistros.ListAsync(vendor);
            foreach (var r in releases)
                JavaVersionCombo.Items.Add(new ComboBoxItem { Content = r.Display, Tag = r });
            if (JavaVersionCombo.Items.Count > 0) JavaVersionCombo.SelectedIndex = 0;
            JavaVendorStatus.Text = releases.Count == 0
                ? Loc.T("没有取到可用版本：可切换另一个发行版再试。")
                : Loc.T("共 {0} 个版本可选。", releases.Count);
            JavaDownloadBtn.IsEnabled = releases.Count > 0;
        }
        catch (Exception ex)
        {
            // 「显示无连接」这类问题多半是发行版域名在该网络下不可达：直接建议换发行版
            JavaVendorStatus.Text = Loc.T("获取版本列表失败：{0}\n可切换另一个发行版，或检查网络/代理后重试。", ex.Message);
            JavaDownloadBtn.IsEnabled = false;
        }
    }

    private async void DownloadJavaVendor_Click(object s, RoutedEventArgs e)
    {
        if ((JavaVersionCombo.SelectedItem as ComboBoxItem)?.Tag is not JavaRelease release) return;
        JavaDownloadBtn.IsEnabled = false;
        JavaProgress.Visibility = Visibility.Visible;
        JavaProgress.IsIndeterminate = true;
        try
        {
            var status = new Progress<string>(t => _dq.TryEnqueue(() => JavaVendorStatus.Text = t));
            var exe = await _s.JavaDistros.InstallAsync(release, status);
            _javaOverride = exe;
            await ScanJavaAsync();
            JavaVendorStatus.Text = Loc.T("已安装 {0}。", release.Display);
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("下载失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
            JavaVendorStatus.Text = Loc.T("下载失败：{0}\n若提示无法连接，请换一个发行版（龙井走 GitHub，Temurin 走 Adoptium）。", ex.Message);
        }
        finally
        {
            JavaProgress.IsIndeterminate = false;
            JavaProgress.Visibility = Visibility.Collapsed;
            JavaDownloadBtn.IsEnabled = true;
        }
    }

    private void Theme_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        var tag = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        _s.Settings.Theme = tag;   // 内存生效，落盘仍由「保存设置」负责（不改现有行为）
        MainWindow.RefreshAppearance();
    }

    // 界面动效四档：切换即落盘，毛玻璃材质即时生效；悬浮/光照/形变等接了线的动效
    // 对已加载页面不追溯，下次进页生效（与旧开关同一约定）。
    private void Motion_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        ApplyMotionLevel((MotionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "balanced");
        SavedBar.IsOpen = true;
    }

    /// <summary>把下拉框当前选择的档位落到设置并让全部效果当场生效。</summary>
    private void ApplyMotionLevel(string level)
    {
        var s = _s.Settings;
        s.UiMotionLevel = level;
        // 旧开关（插件 API 的 ui_animations）与等级保持同义：极少 = off，其余 = on
        s.UiAnimations = level == "minimal" ? "off" : "on";
        UpdateMotionHint();
        MotionMaterials.Apply(s);      // 卡片毛玻璃的收起/恢复是改画刷属性，当场可见
        HoverShadowBehavior.RefreshAll();    // 光斑：极少当场摘掉，切回当场接上
        PointerLightBehavior.RefreshAll();   // 玻璃面/玻璃缘/阴影/形变：同上
        try { s.Save(); } catch { /* 落盘失败不阻断 */ }
    }

    /// <summary>按当前选择刷新动效档位说明。</summary>
    private void UpdateMotionHint()
    {
        if (MotionHint is null) return;
        MotionHint.Text = Loc.T((MotionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "minimal" => "只保留最基本的页面切换动画。",
            "lite" => "在极少的基础上，加入毛玻璃卡片与悬浮微反馈。",
            "full" => "拥有全部动效（更明显的按压回弹与更亮的边缘光），建议高性能独立显卡机型使用。",
            _ => "在较少的基础上，加入按钮边缘光、阴影跟随与按压回弹（推荐）。",
        });
    }

    // ── 跨设备与 P2P（设计：2026-10-14-cross-device-sync-design.md） ──

    private void DeviceLinkSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.DeviceLinkEnabled = DeviceLinkSwitch.IsOn;
        ApplyDeviceLink();
    }

    private void P2pCrossSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        _s.Settings.DeviceP2PCrossAccount = P2pCrossSwitch.IsOn;
        _s.Settings.Save();
    }

    private void P2pSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.P2pDownloadEnabled = P2pSwitch.IsOn;
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        // P2P 开关即时生效（PeerCount 按 settings 实时判定），无需重启链路
        SavedBar.IsOpen = true;
    }

    /// <summary>
    /// 高级传递优化开关：落盘 + 当场启停。STUN 探测与 announce 都在后台线程，
    /// 启停只 await 一个 Task.Run，UI 不冻结。
    /// </summary>
    private async void P2pInternetSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        var enable = P2pInternetSwitch.IsOn;

        if (enable && !_s.StarcloudId.IsLoggedIn)
        {
            // 开关拨回并落盘同步：留着 ON 会让设置与实际状态脱节（下次启动也不会跑）
            P2pInternetSwitch.IsOn = false;
            _s.Settings.P2pInternetEnabled = false;
            try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
            ErrorBar.Message = Loc.T("高级传递优化需要登录 Starcloud ID");
            ErrorBar.IsOpen = true;
            return;
        }

        _s.Settings.P2pInternetEnabled = enable;
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }

        SavedBar.IsOpen = true;
        await Task.Run(() =>
        {
            if (enable) _s.InternetP2P.Start();
            else _s.InternetP2P.Stop();
        });
    }

    private void P2pUploadBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.P2pInternetUploadMbps = P2pUploadBox.SelectedIndex switch
        {
            0 => 1,
            1 => 2,
            2 => 4,
            3 => 8,
            _ => 16,
        };
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
    }

    /// <summary>设备链路开关：落盘 + 当场启停（beacon/服务随开关起，不需要重启启动器）。</summary>
    /// <summary>
    /// 设备链路开关：落盘 + 当场启停。
    /// <para><b>启停必须离开 UI 线程</b>：首次绑定监听端口时 Windows 防火墙可能同步阻塞（规则协商/弹窗），
    /// Stop 里还有等待后台循环退出的同步 Wait——直接在 UI 线程做就是"开一下卡几秒"。
    /// 这里只 await 一个后台任务，UI 期间不冻结。</para>
    /// </summary>
    private async void ApplyDeviceLink()
    {
        var link = _s.DeviceLink;
        var enable = _s.Settings.DeviceLinkEnabled;
        UpdateSyncLoginHint();
        SavedBar.IsOpen = true;

        if (enable && link.CanStart)
        {
            var ok = await Task.Run(() => link.Start());
            if (!ok)
            {
                ErrorBar.Message = Loc.T("设备链路启动失败（端口被占用或防火墙拦截）");
                ErrorBar.IsOpen = true;
            }
        }
        else if (!enable)
        {
            await Task.Run(() => link.Stop());
        }
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        RefreshLinkStatus();
        RefreshPeers();
    }

    /// <summary>高级传递优化的实时状态：探测中 / 已参与（N 个公网端点）/ 网络不可用。</summary>
    private void RefreshInternetP2pStatus()
    {
        var settings = _s.Settings;
        var text = !settings.P2pInternetEnabled
            ? Loc.T("未开启：不会向其他用户分享文件")
            : !_s.StarcloudId.IsLoggedIn
                ? Loc.T("需要登录 Starcloud ID")
                : _s.InternetP2P.State switch
                {
                    InternetP2PState.Ready =>
                        Loc.T("已参与：{0} 个公网端点，可与在线用户直连", _s.InternetP2P.CandidateCount),
                    InternetP2PState.Probing => Loc.T("正在探测公网端点…"),
                    InternetP2PState.Unavailable =>
                        Loc.T("当前网络无法打洞（UDP 或 STUN 不通），不会参与"),
                    _ => Loc.T("未参与"),
                };
        var fetched = _s.InternetP2P.BytesFetched;
        if (settings.P2pInternetEnabled && fetched > 0)
            text += " · " + Loc.T("已从对端取到 {0} MB", (fetched / 1024.0 / 1024.0).ToString("0.0"));
        P2pInternetStatus.Text = text;
    }

    private void UpdateSyncLoginHint()
    {
        SyncLoginHint.Visibility = _s.StarcloudId.IsLoggedIn ? Visibility.Collapsed : Visibility.Visible;
    }

    private void GoLogin_Click(object s, RoutedEventArgs e)
    {
        if (App.MainWindow is MainWindow win) win.NavigateTo("accounts");
    }

    /// <summary>跨设备链路状态一行（在线设备卡顶部）。</summary>
    private void RefreshLinkStatus()
    {
        try
        {
            var peers = _s.DeviceLink.Peers.Count;
            var link = _s.DeviceLink.Running
                ? Loc.T("链路运行中 · 本机名称 {0} · 在线设备 {1} 台", _s.DeviceLink.DeviceName, peers)
                : Loc.T("链路未运行：开启开关后，同一 Starcloud ID 的设备即可互相发现");
            LinkStatusText.Text = link;
        }
        catch
        {
            // 状态拿不到就保持上一帧
        }
    }

    // ── 跨设备：在线设备列表（2 秒自刷，只在「跨设备」分区可见时工作） ──

    private DispatcherQueueTimer? _peerTimer;

    private void StartPeerTimer()
    {
        if (_peerTimer is not null) return;
        _peerTimer = _dq.CreateTimer();
        _peerTimer.Interval = TimeSpan.FromSeconds(2); // 必须显式设：默认 0 会自旋
        _peerTimer.IsRepeating = true;
        _peerTimer.Tick += (_, _) =>
        {
            if (SectionCross.Visibility != Visibility.Visible) return;
            RefreshPeers();
            RefreshInternetP2pStatus();
        };
        _peerTimer.Start();
    }

    private void RefreshPeers()
    {
        var peers = _s.DeviceLink.Peers;
        var selected = (PeerList.SelectedItem as PeerRow)?.Key;

        PeerList.ItemsSource = peers.Select(p => new PeerRow(
            $"{p.Address}:{p.Port}",
            p.Name,
            p.Address,
            Loc.T("在线 {0} 秒前", (int)(DateTime.Now - p.LastSeen).TotalSeconds))).ToList();
        MotionFx.StaggerListItems(PeerList);

        PeerCountText.Text = Loc.T("{0} 台", peers.Count);
        if (selected is not null)
        {
            var keep = PeerList.Items.OfType<PeerRow>().FirstOrDefault(r => r.Key == selected);
            if (keep is not null) PeerList.SelectedItem = keep;
        }
        EmptyHint.Visibility = peers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdatePeerActions();
    }

    private void PeerList_SelectionChanged(object s, SelectionChangedEventArgs e) => UpdatePeerActions();

    private void RefreshCross_Click(object s, RoutedEventArgs e) => RefreshPeers();

    private void UpdatePeerActions()
    {
        PullBtn.IsEnabled = PeerList.SelectedItem is PeerRow && !_peerBusy && _s.StarcloudId.IsLoggedIn;
    }

    private bool _peerBusy;

    /// <summary>从选中的对端列出世界 → 选一个拉回本机（落到共享根）。</summary>
    private async void Pull_Click(object s, RoutedEventArgs e)
    {
        if (PeerList.SelectedItem is not PeerRow row || _peerBusy) return;
        var peer = _s.DeviceLink.Peers.FirstOrDefault(p => $"{p.Address}:{p.Port}" == row.Key);
        if (peer is null) return;

        _peerBusy = true;
        UpdatePeerActions();
        try
        {
            var remotes = await _s.SaveSync.ListRemoteAsync(peer);
            if (remotes is null || remotes.Count == 0)
            {
                ErrorBar.Message = Loc.T("没能从 {0} 读到存档清单（设备离线或对端没有世界）", peer.Name);
                ErrorBar.IsOpen = true;
                return;
            }

            var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 220 };
            foreach (var r in remotes)
            {
                list.Items.Add(new ListViewItem
                {
                    Content = new TextBlock
                    {
                        Text = Loc.T("{0}（{1}· 上次游玩 {2:yyyy-MM-dd HH:mm}）",
                            r.LevelName, r.Isolated ? r.VersionId : Loc.T("共享"), r.LastPlayed ?? DateTime.MinValue),
                        TextWrapping = TextWrapping.Wrap,
                    },
                    Tag = r,
                    Padding = new Thickness(8, 6, 8, 6),
                });
            }
            list.SelectedIndex = 0;
            var dlg = new ThemedDialog
            {
                Title = Loc.T("从 {0} 拉取存档", peer.Name),
                Content = new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = Loc.T("选择要拉回的世界；本机同名世界更新时会被拒收，较旧时自动改名保留。"),
                            TextWrapping = TextWrapping.Wrap,
                        },
                        list,
                    },
                },
                PrimaryButtonText = Loc.T("拉取"),
                CloseButtonText = Loc.T("取消"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary ||
                list.SelectedItem is not ListViewItem item || item.Tag is not RemoteSaveInfo remote) return;

            var localIsolated = remote.Isolated &&
                                Directory.Exists(Path.Combine(_s.Settings.DotMinecraft, "versions", remote.VersionId));
            var result = await _s.SaveSync.PullAsync(peer, remote, remote.VersionId, localIsolated);
            if (result.Ok)
            {
                SavedBar.Title = Loc.T("已完成");
                SavedBar.Message = result.Message;
                SavedBar.IsOpen = true;
                NotificationCenter.Instance.Info(result.Message);
            }
            else
            {
                ErrorBar.Message = result.Message;
                ErrorBar.IsOpen = true;
            }
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("拉取失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _peerBusy = false;
            UpdatePeerActions();
        }
    }

    /// <summary>设备列表行（Key 用于自刷时保持选中）；ToString 直接给 ListView 当行文案</summary>
    public sealed record PeerRow(string Key, string Name, string Address, string Seen)
    {
        public override string ToString() => Loc.T("{0} · {1} · {2}", Name, Address, Seen);
    }

    // 界面语言：切换即时生效（词典热替换 + 已本地化的 XAML 文案重放）
    private void Language_Changed(object s, SelectionChangedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.UiLanguage =
            (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-CN";
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        Loc.SetLanguage(_s.Settings.UiLanguage);
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

    // 账户页玩家走路动画：切换即落盘（下次进入账户页生效）
    private void WalkAnimSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.AccountWalkAnimation = WalkAnimSwitch.IsOn;
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    // 微软登录方式：切换即落盘（下次点「微软登录」时生效，不打断正在进行的登录）
    private void EmbeddedLoginSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.MicrosoftLoginEmbedded = EmbeddedLoginSwitch.IsOn;
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    // ── 通用：软件更新（设计：.trae/documents/auto-update-feature.md） ──

    // 自动检查更新：切换即落盘（下次启动生效）
    private void AutoUpdateSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded) return;
        _s.Settings.AutoUpdateEnabled = AutoUpdateSwitch.IsOn;
        try { _s.Settings.Save(); } catch { /* 落盘失败不阻断 */ }
        SavedBar.IsOpen = true;
    }

    // 手动检查更新：状态写进行内文本，有新版弹 UpdateDialog（弹窗内部自理下载与重启）
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
            var xamlRoot = Content.XamlRoot;
            if (xamlRoot is not null)
                UpdateDialog.Show(xamlRoot, m);
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

        // 「模糊强度 / 不透明度」描述的是壁纸自己：背景关闭（纯毛玻璃）时它们没有作用对象，
        // 置灰比让人拖半天没反应更清楚。毛玻璃强度两层都吃（壁纸遮罩 + 窗口亚克力），始终可用。
        var hasWallpaper = AppearanceService.ResolveBackgroundPath(_s.Settings) is not null;
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


    private static async Task<Windows.Storage.StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker
        {
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add("*");
        return await picker.PickSingleFolderAsync();
    }

    private void ShowSettingsError(string message)
    {
        ErrorBar.Message = message;
        ErrorBar.IsOpen = true;
        SavedBar.IsOpen = false;
    }

    private void ShowSettingsNotice(string message)
    {
        SavedBar.Title = message;
        SavedBar.IsOpen = true;
        ErrorBar.IsOpen = false;
    }

    private void Save_Click(object s, RoutedEventArgs e)
    {
        var s0 = _s.Settings;
        // 游戏目录已移到版本页顶部的下拉管理，这里不再写 DotMinecraft

        // 内存：最小固定 512 MB，最大取滑块（整数 GB）
        s0.MicrosoftTokenPersist = MsTokenPersistSwitch.IsOn;
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
        s0.AutoInstallModDependencies = AutoModDepsSwitch.IsOn;
        s0.DownloadSource = (SourceCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "auto";
        s0.DownloadCustomMirrorUrl = CustomMirrorBox.Text.Trim();
        s0.CurseForgeApiKey = CurseForgeKeyBox.Text.Trim();

        // 全局环境变量
        s0.GlobalEnvVars = ReadGlobalEnv();

        // 通用
        s0.Theme = (ThemeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "system";
        s0.UiMotionLevel = (MotionCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "balanced";
        s0.UiAnimations = s0.UiMotionLevel == "minimal" ? "off" : "on";
        MotionMaterials.Apply(s0);
        HoverShadowBehavior.RefreshAll();
        PointerLightBehavior.RefreshAll();
        // 跨设备：设备名改动即时生效（beacon 每次广播实时取）
        s0.DeviceName = DeviceNameBox.Text.Trim();
        s0.UiLanguage = (LanguageCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "zh-CN";
        s0.VersionIsolated = IsolationDefaultRadio.SelectedIndex == 0;
        s0.MicrosoftLoginEmbedded = EmbeddedLoginSwitch.IsOn;

        s0.Save();
        MainWindow.RefreshAppearance();
        SavedBar.Title = Loc.T("已保存");
        SavedBar.IsOpen = true;
    }
}
