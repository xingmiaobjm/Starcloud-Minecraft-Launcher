// 版本详情子页：概览 / 内容 / 操作卡片（设置卡片由 T6 填充）
// 计划：docs/superpowers/plans/2026-09-09-version-management-plan.md T5

using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.Storage.Pickers;

namespace Starcloud.App.Views;

public sealed partial class VersionDetailPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private string _versionId = "";
    private InstanceSettingsStore _store = null!;

    /// <summary>RefreshAll 批量赋值期间抑制控件事件触发保存。</summary>
    private bool _loading;

    /// <summary>全字段防抖保存（500ms 单发）。</summary>
    private readonly DispatcherTimer _saveTimer;

    /// <summary>环境变量行（名称 / 值 / 容器），顺序即写入顺序。</summary>
    private readonly List<EnvRow> _envRows = [];

    /// <summary>已扫描 Java 运行时（ComboBox 数据源，不含置顶的「跟随全局」）。</summary>
    private List<JavaRuntime> _runtimes = [];

    /// <summary>隔离 / Java / 全屏 / 语言：三态与覆盖值的运行态缓存（null = 跟随全局）。</summary>
    private string? _isolationOverride;
    private string? _javaOverride;
    private bool? _fullscreenOverride;
    private string? _langOverride;

    /// <summary>内存 / 窗口：控件无法表达「未覆盖」，用脏标记区分是否写入版本档案。</summary>
    private bool _memDirty;
    private bool _winDirty;

    private static readonly Regex ServerRegex = new(@"^[^\s:]+(:\d{1,5})?$", RegexOptions.Compiled);
    private static readonly Regex EnvNameRegex = new(@"^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.Compiled);
    private static readonly string[] LangCodes = ["", "zh_CN", "zh_TW", "en_US", "ja_JP"];

    private sealed record EnvRow(StackPanel Container, TextBox Name, TextBox Value);

    public VersionDetailPage()
    {
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => FlushSave();
    }

    protected override async void OnNavigatedTo(NavigationEventArgs e)
    {
        _versionId = e.Parameter as string ?? "";
        RefreshAll();
        await LoadJavaRuntimesAsync();
    }

    protected override void OnNavigatedFrom(NavigationEventArgs e) => FlushSave();

    private static Brush ErrorBrush() =>
        (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];

    // ── 刷新 ──

    private void RefreshAll()
    {
        _loading = true;
        var dotMc = _s.Settings.DotMinecraft;
        _store = new InstanceSettingsStore(dotMc);
        var profile = _store.Get(_versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _s.Settings);
        var gameRoot = InstanceLayout.GameRoot(dotMc, _versionId, isolated);

        // 头部
        TitleText.Text = _versionId;
        KindText.Text = VersionRowHelpers.DetectKind(dotMc, _versionId);
        var category = profile.Category ?? "";
        CategoryBadgeText.Text = category;
        CategoryBadge.Visibility = category.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        IsolatedBadge.Visibility = isolated ? Visibility.Visible : Visibility.Collapsed;
        ApplyIcon(VersionRowHelpers.ResolveIconPath(dotMc, profile.IconPath));

        var running = App.RunningVersionId == _versionId;
        RunningBanner.IsOpen = running;
        RenameBtn.IsEnabled = !running;
        CopyBtn.IsEnabled = !running;

        // 概览
        GameRootText.Text = gameRoot;
        LastPlayedText.Text = VersionRowHelpers.FormatLastPlayed(profile.LastPlayedAt);
        CategoryBox.Text = category;
        NoteBox.Text = profile.Note ?? "";

        // 内容：5 个内容目录行
        ContentPanel.Children.Clear();
        foreach (var dir in InstanceLayout.ContentDirs)
            ContentPanel.Children.Add(BuildContentRow(gameRoot, dir));

        LoadSettingsCard(profile, running);

        _loading = false;
        RefreshHints();
    }

    /// <summary>游戏设置卡片回显：控件值 = 版本覆盖值 ?? 全局生效值。</summary>
    private void LoadSettingsCard(VersionProfile profile, bool running)
    {
        var settings = _s.Settings;

        // 隔离三态
        _isolationOverride = profile.Isolation;
        IsolationRadio.SelectedIndex = profile.Isolation switch { "shared" => 1, "isolated" => 2, _ => 0 };
        IsolationReset.Visibility = profile.Isolation is null ? Visibility.Collapsed : Visibility.Visible;
        IsolationRadio.IsEnabled = !running;
        UpdateGameRootPreview();

        // 内存档位（显式档位优先；旧数据有覆盖值按「自定义」推断）
        _memDirty = false;
        var memMode = profile.MemoryMode ?? (profile.MemoryMaxMb is not null ? "manual" : "global");
        MemoryModeRadio.SelectedIndex = memMode switch { "auto" => 1, "manual" => 2, _ => 0 };
        MaxMemBox.Value = profile.MemoryMaxMb ?? settings.DefaultMaxMemoryMb;

        // Java 档位
        _javaOverride = profile.JavaPath;
        var javaMode = profile.JavaMode ?? (profile.JavaPath is not null ? "manual" : "global");
        JavaModeRadio.SelectedIndex = javaMode switch { "auto" => 1, "manual" => 2, _ => 0 };
        PopulateJavaCombo();

        // 窗口档位与全屏
        _winDirty = false;
        _fullscreenOverride = profile.Fullscreen;
        var winMode = profile.WindowMode ?? (profile.WindowWidth is not null ? "manual" : "global");
        WindowModeRadio.SelectedIndex = winMode switch { "auto" => 1, "manual" => 2, _ => 0 };
        WidthBox.Value = profile.WindowWidth ?? settings.DefaultWindowWidth ?? 1280;
        HeightBox.Value = profile.WindowHeight ?? settings.DefaultWindowHeight ?? 720;
        FullscreenRadio.SelectedIndex = profile.Fullscreen switch { true => 1, false => 2, _ => 0 };

        UpdateModeVisibility();
        RefreshMemoryBar();

        // 连服 / 命令 / 额外参数
        ServerBox.Text = profile.ServerAddress ?? "";
        PreCmdBox.Text = profile.PreLaunchCommand ?? "";
        PostCmdBox.Text = profile.PostLaunchCommand ?? "";
        ExtraJvmBox.Text = string.Join("\n", profile.ExtraJvmArgs);
        ExtraGameBox.Text = string.Join("\n", profile.ExtraGameArgs);

        // 语言
        _langOverride = profile.LanguageOverride;
        var langIndex = Array.IndexOf(LangCodes, profile.LanguageOverride ?? "");
        LangCombo.SelectedIndex = langIndex < 0 ? 0 : langIndex;
        LangReset.Visibility = profile.LanguageOverride is null ? Visibility.Collapsed : Visibility.Visible;

        // 环境变量
        EnvPanel.Children.Clear();
        _envRows.Clear();
        foreach (var (k, v) in profile.EnvVars)
            AddEnvRow(k, v, focus: false);

        SettingsRunningBanner.IsOpen = running;
    }

    /// <summary>即时校验与提示文案（不写入数据）。</summary>
    private void RefreshHints()
    {
        ValidateMemory();
        ValidateWindow();
        TryReadServer(out _);
        ReadEnv();
        UpdateLangHint();
        UpdateJavaHint();
    }

    private StackPanel BuildContentRow(string gameRoot, string dir)
    {
        var path = Path.Combine(gameRoot, dir);
        var exists = Directory.Exists(path);
        var openBtn = new Button
        {
            Content = "打开目录",
            Tag = path,
            FontSize = 12,
            Padding = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(openBtn, $"打开 {dir} 目录");
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Padding = new Thickness(0, 4, 0, 4),
            Children =
            {
                new FontIcon { Glyph = "\uE8B7", FontSize = 16 },
                new TextBlock { Text = dir, FontSize = 13, VerticalAlignment = VerticalAlignment.Center },
                new TextBlock
                {
                    Text = exists ? "已存在" : "尚未创建",
                    FontSize = 12,
                    Opacity = 0.55,
                    VerticalAlignment = VerticalAlignment.Center,
                },
                openBtn,
            },
        };
    }

    private void OpenContentDir_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private void ApplyIcon(string? iconPath)
    {
        if (iconPath is null)
        {
            IconImage.Source = null;
            IconImage.Visibility = Visibility.Collapsed;
            IconFallback.Visibility = Visibility.Visible;
        }
        else
        {
            IconImage.Source = new BitmapImage(new Uri(iconPath));
            IconImage.Visibility = Visibility.Visible;
            IconFallback.Visibility = Visibility.Collapsed;
        }
    }

    // ── 头部 ──

    private void Back_Click(object sender, RoutedEventArgs e) => Frame.GoBack();

    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        App.PendingLaunchVersionId = _versionId;
        (App.MainWindow as MainWindow)?.NavigateTo("home");
    }

    private async void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.FileTypeFilter.Add(".png");
        picker.FileTypeFilter.Add(".jpg");
        picker.FileTypeFilter.Add(".jpeg");
        picker.FileTypeFilter.Add(".bmp");
        picker.FileTypeFilter.Add(".ico");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        try
        {
            var iconsDir = Path.Combine(_s.Settings.DotMinecraft, "starcloud", "icons");
            Directory.CreateDirectory(iconsDir);
            var fileName = _versionId + file.FileType; // <版本名>.<原扩展名>
            File.Copy(file.Path, Path.Combine(iconsDir, fileName), overwrite: true);
            var profile = _store.Get(_versionId);
            profile.IconPath = fileName;
            _store.Save();
            ApplyIcon(VersionRowHelpers.ResolveIconPath(_s.Settings.DotMinecraft, profile.IconPath));
        }
        catch (Exception ex)
        {
            await InstanceActions.ShowErrorAsync(Content.XamlRoot, "更换图标失败", ex);
        }
    }

    // ── 操作卡片 ──

    private async void Verify_Click(object sender, RoutedEventArgs e)
    {
        var dotMc = _s.Settings.DotMinecraft;
        VerifyBtn.IsEnabled = false;
        try
        {
            var report = await _s.Preflight.CheckAsync(dotMc, _versionId);
            if (!report.NeedsRepair)
            {
                await new ContentDialog
                {
                    Title = "验证文件",
                    Content = "一切正常，未发现缺失或损坏的文件。",
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot,
                }.ShowAsync();
                return;
            }
            if (report.VersionJsonBroken && !report.CanAutoRestoreJson)
            {
                await new ContentDialog
                {
                    Title = "验证文件",
                    Content = "版本 JSON 缺失或损坏且无法自动恢复，请到下载页重新安装。",
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot,
                }.ShowAsync();
                return;
            }

            var count = report.Issues.Count + (report.VersionJsonBroken ? 1 : 0);
            var confirm = new ContentDialog
            {
                Title = "验证文件",
                Content = $"发现 {count} 个缺失或损坏的文件，立即补全吗？",
                PrimaryButtonText = "立即补全",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Content.XamlRoot,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            // 进度对话框（无按钮，完成后 Hide）
            var bar = new Microsoft.UI.Xaml.Controls.ProgressBar { Minimum = 0, Maximum = 100 };
            var step = new TextBlock { FontSize = 12, Opacity = 0.7 };
            var progressDlg = new ContentDialog
            {
                Title = "正在补全缺失文件",
                Content = new StackPanel { Spacing = 8, Children = { bar, step } },
                PrimaryButtonText = string.Empty,
                CloseButtonText = string.Empty,
                XamlRoot = Content.XamlRoot,
            };
            var progress = new Progress<BatchProgress>(p =>
            {
                bar.Value = p.Percentage;
                step.Text = $"{p.DoneFiles}/{p.TotalFiles}（{p.Percentage}%）";
            });
            _ = progressDlg.ShowAsync();
            try
            {
                await _s.Preflight.EnsureReadyAsync(dotMc, _versionId, report, _s.Settings, progress);
                progressDlg.Hide();
                await new ContentDialog
                {
                    Title = "验证文件",
                    Content = "补全完成，文件已恢复完整。",
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot,
                }.ShowAsync();
            }
            catch (PreflightException ex)
            {
                progressDlg.Hide();
                var listed = string.Join("\n", ex.FailedFiles.Take(5).Select(f => "• " + f.Path));
                await new ContentDialog
                {
                    Title = "补全失败",
                    Content = $"以下文件补全失败：\n{listed}",
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot,
                }.ShowAsync();
            }
            catch (Exception ex)
            {
                progressDlg.Hide();
                await InstanceActions.ShowErrorAsync(Content.XamlRoot, "补全失败", ex);
            }
        }
        catch (Exception ex)
        {
            await InstanceActions.ShowErrorAsync(Content.XamlRoot, "验证文件失败", ex);
        }
        finally
        {
            VerifyBtn.IsEnabled = true;
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var result = await InstanceActions.RenameAsync(Content.XamlRoot,
            _s.Settings.DotMinecraft, _versionId);
        if (result is null) return;
        if (result.Error is not null)
        {
            await InstanceActions.ShowErrorAsync(Content.XamlRoot, "重命名失败", result.Error);
            return;
        }
        _versionId = result.Id; // 详情页内改名：更新 ID 与头部后留在本页
        RefreshAll();
    }

    private async void Copy_Click(object sender, RoutedEventArgs e)
    {
        var dotMc = _s.Settings.DotMinecraft;
        var profile = _store.Get(_versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _s.Settings);
        var result = await InstanceActions.CopyAsync(Content.XamlRoot, dotMc, _versionId, isolated);
        if (result is null) return;
        if (result.Error is not null)
        {
            await InstanceActions.ShowErrorAsync(Content.XamlRoot, "复制失败", result.Error);
        }
    }

    private async void ViewJson_Click(object sender, RoutedEventArgs e)
    {
        await InstanceActions.ShowJsonAsync(Content.XamlRoot,
            _s.Settings.DotMinecraft, _versionId);
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var running = App.RunningVersionId == _versionId;
        var profile = _store.Get(_versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _s.Settings);
        var message = running
            ? $"版本 {_versionId} 正在运行，请先停止游戏再删除。"
            : $"确定要删除版本 {_versionId} 吗？\n将删除其版本文件夹，此操作不可恢复。"
              + (isolated ? "\n注意：独立目录下的 mods / 存档等也将一并删除。" : "");
        var dlg = new ContentDialog
        {
            Title = "删除版本",
            Content = message,
            PrimaryButtonText = running ? string.Empty : "删除",
            CloseButtonText = "关闭",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (running || await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            InstalledVersionManager.Remove(_s.Settings.DotMinecraft, _versionId);
            _store.Remove(_versionId);
        }
        catch (Exception ex)
        {
            await InstanceActions.ShowErrorAsync(Content.XamlRoot, "删除失败", ex);
            return;
        }
        Frame.GoBack(); // 回到来源页（VersionsPage Loaded 会重新加载列表）
    }

    // ── 隔离模式（三态 + 切换迁移） ──

    private async void IsolationRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;

        var target = IsolationRadio.SelectedIndex switch
        {
            1 => "shared",
            2 => "isolated",
            _ => (string?)null,
        };
        var settings = _s.Settings;
        var dotMc = settings.DotMinecraft;
        var targetIsolated = target switch
        {
            "shared" => false,
            "isolated" => true,
            _ => settings.VersionIsolated,
        };
        var curIsolated = InstanceLayout.IsIsolated(_store.Get(_versionId), settings);
        var fromRoot = InstanceLayout.GameRoot(dotMc, _versionId, curIsolated);
        var toRoot = InstanceLayout.GameRoot(dotMc, _versionId, targetIsolated);
        UpdateGameRootPreview();

        if (string.Equals(fromRoot, toRoot, StringComparison.OrdinalIgnoreCase))
        {
            ApplyIsolation(target);
            return;
        }

        var movable = InstanceLayout.ContentDirs
            .Where(d => Directory.Exists(Path.Combine(fromRoot, d))
                        && Directory.EnumerateFileSystemEntries(Path.Combine(fromRoot, d)).Any())
            .ToList();
        var body = movable.Count == 0
            ? $"隔离方式将切换为「{IsolationName(target)}」，当前没有需要迁移的内容目录。"
            : $"隔离方式将切换为「{IsolationName(target)}」。\n\n以下目录将从\n{fromRoot}\n移动到\n{toRoot}\n\n"
              + string.Join("\n", movable.Select(d => "• " + d));
        var dlg = new ContentDialog
        {
            Title = "切换版本隔离",
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = movable.Count == 0 ? string.Empty : "立即移动",
            SecondaryButtonText = "仅切换不移动",
            CloseButtonText = "取消",
            DefaultButton = movable.Count == 0 ? ContentDialogButton.Secondary : ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        var result = await dlg.ShowAsync();
        if (result == ContentDialogResult.None)
        {
            RevertIsolationRadio();
            return;
        }
        if (result == ContentDialogResult.Secondary)
        {
            ApplyIsolation(target);
            return;
        }

        // 立即移动：操作期间禁用隔离与目录相关操作
        IsolationRadio.IsEnabled = false;
        RenameBtn.IsEnabled = false;
        CopyBtn.IsEnabled = false;
        try
        {
            await InstanceLayout.MoveContentDirsAsync(fromRoot, toRoot);
        }
        catch (Exception ex)
        {
            RevertIsolationRadio();
            await InstanceActions.ShowErrorAsync(Content.XamlRoot, "迁移内容目录失败", ex);
            return;
        }
        finally
        {
            RenameBtn.IsEnabled = App.RunningVersionId != _versionId;
            CopyBtn.IsEnabled = App.RunningVersionId != _versionId;
        }
        ApplyIsolation(target);
    }

    private void IsolationReset_Click(object sender, RoutedEventArgs e) =>
        IsolationRadio.SelectedIndex = 0; // 仅覆盖时可见，置 0 必然触发 SelectionChanged（含迁移询问）

    private static string IsolationName(string? isolation) => isolation switch
    {
        "shared" => "共享目录",
        "isolated" => "独立目录",
        _ => "跟随全局默认",
    };

    /// <summary>写入隔离三态并整体刷新（GameRoot / 内容行 / 徽章随之更新）。</summary>
    private void ApplyIsolation(string? target)
    {
        FlushSave();
        _isolationOverride = target;
        var profile = _store.Get(_versionId);
        profile.Isolation = target;
        _store.Set(_versionId, profile);
        _store.Save();
        RefreshAll();
    }

    private void RevertIsolationRadio()
    {
        _loading = true;
        IsolationRadio.SelectedIndex = _isolationOverride switch { "shared" => 1, "isolated" => 2, _ => 0 };
        _loading = false;
        UpdateGameRootPreview();
    }

    private void UpdateGameRootPreview()
    {
        var isolated = IsolationRadio.SelectedIndex switch
        {
            1 => false,
            2 => true,
            _ => _s.Settings.VersionIsolated,
        };
        GameRootPreview.Text = "游戏目录：" +
            InstanceLayout.GameRoot(_s.Settings.DotMinecraft, _versionId, isolated);
    }

    // ── 档位可见性 ──

    /// <summary>按三个档位单选控制子控件可见性（「恢复跟随」仅在非全局档位出现）。</summary>
    private void UpdateModeVisibility()
    {
        var memMode = MemoryModeRadio.SelectedIndex;
        MaxMemBox.Visibility = memMode == 2 ? Visibility.Visible : Visibility.Collapsed;
        AutoMemText.Visibility = memMode == 1 ? Visibility.Visible : Visibility.Collapsed;
        MemoryReset.Visibility = memMode == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (memMode == 1)
            AutoMemText.Text = $"自动：按物理内存分配约 {Gb(SystemMemory.AutoMaxMb())}。";

        var javaMode = JavaModeRadio.SelectedIndex;
        JavaManualPanel.Visibility = javaMode == 2 ? Visibility.Visible : Visibility.Collapsed;
        JavaReset.Visibility = javaMode == 0 ? Visibility.Collapsed : Visibility.Visible;

        var winMode = WindowModeRadio.SelectedIndex;
        WindowManualPanel.Visibility = winMode == 2 ? Visibility.Visible : Visibility.Collapsed;
        WindowReset.Visibility = winMode == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>内存占用条：物理内存快照一次（不轮询），画出「已使用 / 总量」与「本版本分配」。</summary>
    private void RefreshMemoryBar()
    {
        const double barWidth = 260;
        var (totalMb, usedMb) = SystemMemory.Snapshot();
        var allocMb = MemoryModeRadio.SelectedIndex switch
        {
            1 => SystemMemory.AutoMaxMb(),
            2 => double.IsNaN(MaxMemBox.Value) ? _s.Settings.DefaultMaxMemoryMb : (int)MaxMemBox.Value,
            _ => _s.Settings.DefaultMaxMemoryMb,
        };
        var scale = totalMb > 0 ? barWidth / totalMb : 0;
        MemoryUsedBar.Width = Math.Clamp(usedMb * scale, 0, barWidth);
        MemoryAllocBar.Width = Math.Clamp(allocMb * scale, 0, barWidth);
        MemoryUsageText.Text = $"已使用 {Gb(usedMb)} / 总 {Gb(totalMb)}　·　本版本分配 {Gb(allocMb)}";
    }

    private static string Gb(long mb) => $"{mb / 1024.0:0.#} GB";

    // ── 内存 ──

    private void MemoryMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (MemoryModeRadio.SelectedIndex == 2) _memDirty = true;
        UpdateModeVisibility();
        RefreshMemoryBar();
        ValidateMemory();
        ScheduleSave();
    }

    private void Memory_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        _memDirty = true;
        ValidateMemory();
        RefreshMemoryBar();
        ScheduleSave();
    }

    private void MemoryReset_Click(object sender, RoutedEventArgs e) => MemoryModeRadio.SelectedIndex = 0;

    private bool ValidateMemory()
    {
        var max = MaxMemBox.Value;
        var bad = MemoryModeRadio.SelectedIndex == 2
            && (double.IsNaN(max) || max < 512 || max > 131072);
        MaxMemBox.BorderBrush = bad ? ErrorBrush() : null;
        MemErrorText.Visibility = bad ? Visibility.Visible : Visibility.Collapsed;
        if (bad) MemErrorText.Text = "请填写 512–131072 之间的数值。";
        return !bad;
    }

    // ── Java ──

    private async Task LoadJavaRuntimesAsync()
    {
        try
        {
            _runtimes = await _s.Java.ListRuntimesAsync();
        }
        catch
        {
            _runtimes = [];
        }
        if (_versionId.Length == 0) return;
        _loading = true;
        PopulateJavaCombo();
        _loading = false;
        UpdateJavaHint();
    }

    private void JavaMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        UpdateModeVisibility();
        UpdateJavaHint();
        ScheduleSave();
    }

    /// <summary>重建 Java 下拉：已扫描运行时 +（必要时）自定义项。</summary>
    private void PopulateJavaCombo()
    {
        JavaCombo.Items.Clear();
        foreach (var rt in _runtimes)
            JavaCombo.Items.Add(new ComboBoxItem
            {
                Content = $"Java {rt.MajorVersion} · {rt.VersionString} · {rt.Executable}",
                Tag = rt.Executable,
            });
        if (_javaOverride is not null
            && !_runtimes.Any(r => string.Equals(r.Executable, _javaOverride, StringComparison.OrdinalIgnoreCase)))
            JavaCombo.Items.Add(new ComboBoxItem { Content = $"自定义 · {_javaOverride}", Tag = _javaOverride });

        JavaCombo.SelectedIndex = -1;
        if (_javaOverride is null) return;
        for (var i = 0; i < JavaCombo.Items.Count; i++)
        {
            if (JavaCombo.Items[i] is ComboBoxItem item
                && string.Equals(item.Tag as string, _javaOverride, StringComparison.OrdinalIgnoreCase))
            {
                JavaCombo.SelectedIndex = i;
                return;
            }
        }
    }

    private void UpdateJavaHint() => JavaHintText.Text = JavaModeRadio.SelectedIndex switch
    {
        1 => "自动：按该版本 JSON 推荐的主版本匹配本机 Java。",
        2 => _javaOverride is null
            ? "自定义：尚未选择，将按版本推荐自动匹配。"
            : $"已为这个版本指定 Java：{_javaOverride}",
        _ => $"当前跟随全局：{_s.Settings.JavaExecutable ?? "自动匹配可用 Java"}",
    };

    private void JavaCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _javaOverride = (JavaCombo.SelectedItem as ComboBoxItem)?.Tag as string;
        UpdateModeVisibility();
        UpdateJavaHint();
        ScheduleSave();
    }

    private void JavaReset_Click(object sender, RoutedEventArgs e) => JavaModeRadio.SelectedIndex = 0;

    private async void JavaBrowse_Click(object sender, RoutedEventArgs e)
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
                await new ContentDialog
                {
                    Title = "无法识别的 Java",
                    Content = $"未能从该文件解析出 Java 版本：\n{file.Path}",
                    CloseButtonText = "确定",
                    XamlRoot = Content.XamlRoot,
                }.ShowAsync();
                return;
            }
            _javaOverride = file.Path;
            if (!_runtimes.Any(r => string.Equals(r.Executable, file.Path, StringComparison.OrdinalIgnoreCase)))
                _runtimes.Add(rt);
            _loading = true;
            PopulateJavaCombo();
            _loading = false;
            JavaModeRadio.SelectedIndex = 2;   // 浏览选择即视为「自定义」档位
            UpdateModeVisibility();
            UpdateJavaHint();
            FlushSave();
        }
        catch (Exception ex)
        {
            await InstanceActions.ShowErrorAsync(Content.XamlRoot, "Java 校验失败", ex);
        }
        finally
        {
            JavaBrowseBtn.IsEnabled = true;
        }
    }

    // ── 窗口 ──

    private void WindowMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (WindowModeRadio.SelectedIndex == 2) _winDirty = true;
        UpdateModeVisibility();
        ValidateWindow();
        ScheduleSave();
    }

    private void Window_ValueChanged(NumberBox sender, NumberBoxValueChangedEventArgs args)
    {
        if (_loading) return;
        _winDirty = true;
        ValidateWindow();
        ScheduleSave();
    }

    private void FullscreenRadio_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        _fullscreenOverride = FullscreenRadio.SelectedIndex switch
        {
            1 => true,
            2 => false,
            _ => (bool?)null,
        };
        ScheduleSave();
    }

    private void WindowReset_Click(object sender, RoutedEventArgs e) => WindowModeRadio.SelectedIndex = 0;

    private bool ValidateWindow()
    {
        var w = WidthBox.Value;
        var h = HeightBox.Value;
        var oneMissing = double.IsNaN(w) ^ double.IsNaN(h);
        var outOfRange = (!double.IsNaN(w) && (w < 200 || w > 32767))
                      || (!double.IsNaN(h) && (h < 200 || h > 32767));
        var bad = WindowModeRadio.SelectedIndex == 2 && (oneMissing || outOfRange);
        WidthBox.BorderBrush = bad ? ErrorBrush() : null;
        HeightBox.BorderBrush = bad ? ErrorBrush() : null;
        WindowHintText.Text = bad
            ? "宽高需同时填写 200–32767 之间的数值。"
            : WindowModeRadio.SelectedIndex switch
            {
                1 => "自动：854 × 480 窗口化启动。",
                _ => GameLauncher.ModernWindowArgs(_versionId)
                    ? "窗口尺寸通过 --width/--height 传入；未改动时使用游戏默认大小。"
                    : "此版本可能不支持窗口参数（需要 1.13 及以上）。",
            };
        return !bad;
    }

    // ── 自动连服 ──

    private void ServerBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) ScheduleSave();
    }

    /// <summary>校验连服地址；合法时经 value 返回（空串 → null），非法返回 false 且不写入。</summary>
    private bool TryReadServer(out string? value)
    {
        value = null;
        var text = ServerBox.Text.Trim();
        var m = Regex.Match(_versionId, @"^1\.(\d+)");
        var supportsQuickPlay = !m.Success
            || (int.TryParse(m.Groups[1].Value, out var minor) && minor >= 20);

        if (text.Length == 0)
        {
            ServerBox.BorderBrush = null;
            ServerHintText.Foreground = SecondaryBrush();
            ServerHintText.Text = supportsQuickPlay
                ? "留空表示不自动连接；该版本支持 quickPlayMultiplayer。"
                : "留空表示不自动连接。";
            return true;
        }

        var ok = ServerRegex.IsMatch(text) && PortInRange(text);
        if (!ok)
        {
            ServerBox.BorderBrush = ErrorBrush();
            ServerHintText.Foreground = ErrorBrush();
            ServerHintText.Text = "格式不正确：应为 host 或 host:port，端口 1–65535。";
            return false;
        }

        ServerBox.BorderBrush = null;
        if (supportsQuickPlay)
        {
            ServerHintText.Foreground = SecondaryBrush();
            ServerHintText.Text = "启动时将自动连接该服务器（quickPlayMultiplayer）。";
        }
        else
        {
            ServerHintText.Foreground = CautionBrush();
            ServerHintText.Text = "此版本不支持自动连服，该设置不会生效。";
        }
        value = text;
        return true;
    }

    private static bool PortInRange(string text)
    {
        var colon = text.LastIndexOf(':');
        if (colon < 0) return true;
        return int.TryParse(text.AsSpan(colon + 1), out var port) && port is >= 1 and <= 65535;
    }

    // ── 环境变量 ──

    private void AddEnvRow(string name, string value, bool focus)
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
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nameBox, "环境变量名称");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(valueBox, "环境变量值");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(del, "删除环境变量");

        var container = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            Children = { nameBox, valueBox, del },
        };
        del.Tag = container;
        del.Click += RemoveEnvRow_Click;
        nameBox.TextChanged += Env_TextChanged;
        valueBox.TextChanged += Env_TextChanged;

        _envRows.Add(new EnvRow(container, nameBox, valueBox));
        EnvPanel.Children.Add(container);
        if (focus) nameBox.Focus(FocusState.Programmatic);
    }

    private void EnvAdd_Click(object sender, RoutedEventArgs e)
    {
        AddEnvRow("", "", focus: true);
        ScheduleSave();
    }

    private void RemoveEnvRow_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not StackPanel container) return;
        var row = _envRows.FirstOrDefault(r => r.Container == container);
        if (row is null) return;
        _envRows.Remove(row);
        EnvPanel.Children.Remove(container);
        if (!_loading) ScheduleSave();
    }

    private void Env_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        ReadEnv();
        ScheduleSave();
    }

    /// <summary>读取环境变量行 → 字典（后者覆盖前者）；同时刷新非法 / 重名黄字提示。</summary>
    private Dictionary<string, string> ReadEnv()
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        var invalid = new List<string>();
        var dupes = new List<string>();
        foreach (var row in _envRows)
        {
            var name = row.Name.Text.Trim();
            var val = row.Value.Text;
            if (name.Length == 0)
            {
                if (val.Trim().Length > 0) invalid.Add("（名称不能为空）");
                continue;
            }
            if (!EnvNameRegex.IsMatch(name))
            {
                invalid.Add(name);
                continue;
            }
            if (dict.ContainsKey(name) && !dupes.Contains(name)) dupes.Add(name);
            dict[name] = val;
        }

        var msgs = new List<string>();
        if (invalid.Count > 0)
            msgs.Add("以下名称不合法（仅字母、数字、下划线，且不以数字开头），已忽略："
                     + string.Join("、", invalid));
        if (dupes.Count > 0)
            msgs.Add("名称重复（后者覆盖前者）：" + string.Join("、", dupes));
        EnvWarnText.Text = string.Join("\n", msgs);
        EnvWarnText.Visibility = msgs.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        return dict;
    }

    // ── 命令 / 语言 / 额外参数 ──

    private void Cmd_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) ScheduleSave();
    }

    private void LangCombo_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        var idx = LangCombo.SelectedIndex;
        _langOverride = idx >= 1 && idx < LangCodes.Length ? LangCodes[idx] : null;
        LangReset.Visibility = _langOverride is null ? Visibility.Collapsed : Visibility.Visible;
        UpdateLangHint();
        ScheduleSave();
    }

    private void LangReset_Click(object sender, RoutedEventArgs e)
    {
        _langOverride = null;
        _loading = true;
        LangCombo.SelectedIndex = 0;
        _loading = false;
        LangReset.Visibility = Visibility.Collapsed;
        UpdateLangHint();
        ScheduleSave();
    }

    private void UpdateLangHint() =>
        LangHintText.Text = _langOverride is null
            ? $"当前跟随全局：{_s.Settings.Language}"
            : "已覆盖全局语言；启动前会写入该版本的 options.txt。";

    private void ExtraArgs_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) ScheduleSave();
    }

    // ── 概览字段 ──

    private void CategoryBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) ScheduleSave();
    }

    private void NoteBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_loading) ScheduleSave();
    }

    // ── 保存（防抖 500ms + 离开页面强制 flush） ──

    private void ScheduleSave()
    {
        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void FlushSave()
    {
        _saveTimer.Stop();
        if (_versionId.Length == 0 || _store is null) return;

        var profile = _store.Get(_versionId);
        var before = System.Text.Json.JsonSerializer.Serialize(profile);

        // 概览
        profile.Category = string.IsNullOrWhiteSpace(CategoryBox.Text) ? null : CategoryBox.Text.Trim();
        profile.Note = string.IsNullOrWhiteSpace(NoteBox.Text) ? null : NoteBox.Text;

        // 三态字段（null = 跟随全局）
        profile.Isolation = _isolationOverride;
        profile.LanguageOverride = _langOverride;

        // 内存 / Java / 窗口档位（null = 跟随全局设置）
        profile.MemoryMode = MemoryModeRadio.SelectedIndex switch { 1 => "auto", 2 => "manual", _ => null };
        profile.JavaMode = JavaModeRadio.SelectedIndex switch { 1 => "auto", 2 => "manual", _ => null };
        profile.WindowMode = WindowModeRadio.SelectedIndex switch { 1 => "auto", 2 => "manual", _ => null };

        // 内存：仅「自定义」写覆盖值（最小固定 512）
        if (profile.MemoryMode == "manual")
        {
            if (_memDirty && ValidateMemory())
            {
                profile.MemoryMinMb = 512;
                profile.MemoryMaxMb = (int)MaxMemBox.Value;
            }
        }
        else
        {
            profile.MemoryMinMb = null;
            profile.MemoryMaxMb = null;
        }

        // Java：仅「自定义」写指定路径（自动档由 Resolve 置空 → 走版本推荐）
        profile.JavaPath = profile.JavaMode == "manual" ? _javaOverride : null;

        // 窗口：仅「自定义」写覆盖值
        if (profile.WindowMode == "manual")
        {
            profile.Fullscreen = _fullscreenOverride;
            if (_winDirty && ValidateWindow())
            {
                profile.WindowWidth = (int)WidthBox.Value;
                profile.WindowHeight = (int)HeightBox.Value;
            }
        }
        else
        {
            profile.WindowWidth = null;
            profile.WindowHeight = null;
            profile.Fullscreen = null;
        }

        // 连服（非法不写入）
        if (TryReadServer(out var server)) profile.ServerAddress = server;

        // 环境变量 / 命令 / 额外参数
        profile.EnvVars = ReadEnv();
        profile.PreLaunchCommand = NullIfEmpty(PreCmdBox.Text);
        profile.PostLaunchCommand = NullIfEmpty(PostCmdBox.Text);
        profile.ExtraJvmArgs = SplitLines(ExtraJvmBox.Text);
        profile.ExtraGameArgs = SplitLines(ExtraGameBox.Text);

        if (System.Text.Json.JsonSerializer.Serialize(profile) == before) return;
        _store.Set(_versionId, profile);
        _store.Save();
    }

    private static string? NullIfEmpty(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static List<string> SplitLines(string text) =>
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

    private static Brush SecondaryBrush() =>
        (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private static Brush CautionBrush() =>
        (Brush)Application.Current.Resources["SystemFillColorCautionBrush"];
}

