// 版本详情页：概览 / Mod 管理 / 游戏设置 / 启动参数 四分区，设置三态跟随与覆盖、
// 内容四分类（Mod / 资源包 / 光影包 / 存档）管理。
// 移植自桌面 Starcloud.App.Views.VersionDetailPage（仅做安卓侧适配）：
// - 页面参数：壳无导航参数，用静态 PendingVersionId（列表页设置后导航）或 VersionId 属性装配
// - 生命周期：OnNavigatedTo/From → IShellPage.OnShown / Unloaded（含 FlushSave）
// - 启动入口：GameLauncherService.SelectedVersion + PushOverlay(GamePage(autoLaunch:true))
// - 打开目录：Process.Start → UiDialog 展示路径；文件选择器 / 拖拽安装：触屏端无对应能力，移除
// - 内容下载弹窗的安装目标 = 首页当前选中版本（Settings.LastVersion），见项目记忆「资源管理最终方案」

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Controls;
using Starcloud.Launcher.Uno.Views.Dialogs;
using Starcloud.Launcher.Uno.Views.Rows;
using Starcloud.Launcher.Uno.Views.Shared;
using System.Text.RegularExpressions;

namespace Starcloud.Launcher.Uno.Views;

public sealed partial class VersionDetailPage : Page, IShellPage
{
    private readonly AppServices _s = AppServices.Instance;
    private string _versionId = "";
    private InstanceSettingsStore _store = null!;

    /// <summary>壳路由装配入口：列表页跳转前设置，页面构造 / 显示时消费（对齐桌面导航参数语义）。</summary>
    public static string? PendingVersionId { get; set; }

    /// <summary>对象初始化装配入口（ShellRootView 路由工厂可用 new VersionDetailPage { VersionId = ... }）。</summary>
    public string VersionId { get; set; } = "";

    /// <summary>RefreshAll 批量赋值期间抑制控件事件触发保存。</summary>
    private bool _loading;

    /// <summary>全字段防抖保存（500ms 单发）。</summary>
    private readonly DispatcherTimer _saveTimer;

    /// <summary>内容分区行模型缓存（检查更新时遍历）。</summary>
    private readonly List<ContentFileRow> _contentRows = [];

    /// <summary>内容分区对应的游戏根目录。</summary>
    private string _contentGameRoot = "";

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
        // 两种装配方式都吃：静态 PendingVersionId 优先（列表页设置后导航），其次 VersionId 属性
        if (PendingVersionId is { Length: > 0 } pending)
        {
            PendingVersionId = null;
            _versionId = pending;
        }
        else
        {
            _versionId = VersionId;
        }
        InitializeComponent();
        _saveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveTimer.Tick += (_, _) => FlushSave();
        // 桌面 OnNavigatedFrom 的 FlushSave 时机：页面被换出（Unload）时落盘
        Unloaded += (_, _) => FlushSave();
    }

    /// <summary>壳导航显示时机（对齐桌面 OnNavigatedTo 的刷新时机）。</summary>
    public void OnShown()
    {
        if (PendingVersionId is { Length: > 0 } pending)
        {
            PendingVersionId = null;
            _versionId = pending;
        }
        RefreshAll();
        _ = LoadJavaRuntimesAsync();
    }

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
        var category = profile.Category ?? "";
        CategoryBadgeText.Text = category;
        CategoryBadge.Visibility = category.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        IsolatedBadge.Visibility = isolated ? Visibility.Visible : Visibility.Collapsed;
        ApplyIcon(VersionRowHelpers.DisplayIconPath(dotMc, _versionId, profile.IconPath));

        var running = _s.Runtime.Instances.Any(i => i.VersionId == _versionId);
        RunningBanner.IsOpen = running;
        RenameBtn.IsEnabled = !running;
        CopyBtn.IsEnabled = !running;

        // 概览
        GameRootText.Text = gameRoot;
        // 快捷方式卡片里的文件夹入口：Tag 即实际路径（点开即用，与「打开目录」同一处理）
        OpenVersionFolderBtn.Tag = Path.Combine(dotMc, "versions", _versionId);
        OpenSavesFolderBtn.Tag = Path.Combine(gameRoot, "saves");
        OpenModsFolderBtn.Tag = Path.Combine(gameRoot, "mods");
        OpenScreenshotsFolderBtn.Tag = Path.Combine(gameRoot, "screenshots");
        LastPlayedText.Text = VersionRowHelpers.FormatLastPlayed(profile.LastPlayedAt);
        CategoryBox.Text = category;
        NoteBox.Text = profile.Note ?? "";

        // NumberBox.Header 不在 Loc 附加属性支持范围内，改由代码赋值
        WidthBox.Header = Loc.T("宽");
        HeightBox.Header = Loc.T("高");

        // 内容：Mod / 资源包 / 光影包 / 存档 四分类（芯片切换独立界面）
        // 每次进入默认落在「概览」：先把左栏选中项归位（本身就是 0 号时不会触发事件，
        // 故随后再兜底调一次 SetSection，保证区块可见性与选中底一致；不播过渡）
        NavList.SelectedIndex = 0;
        SetSection(0, animate: false);
        SetCategory(0);
        ContentPanelFill(gameRoot);

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

        // 自定义内存：与「设置 → 内存分配」同款滑块（整数 GB），上界为「按物理内存自动分配」值
        var autoGb = Math.Max(1, SystemMemory.AutoMaxMb() / 1024);
        var memGb = Math.Max(1, (profile.MemoryMaxMb ?? settings.DefaultMaxMemoryMb) / 1024);
        MaxMemSlider.Maximum = Math.Max(autoGb, memGb);
        MaxMemSlider.Value = Math.Clamp(memGb, 1, MaxMemSlider.Maximum);
        MaxMemText.Text = $"{(int)MaxMemSlider.Value} GB";
        MemAutoHint.Text = Loc.T("1 ~ {0} GB（物理内存 {1} GB，自动上限 {2} GB）；最小内存固定 512 MB。",
            (int)MaxMemSlider.Maximum, SystemMemory.Snapshot().TotalMb / 1024, autoGb);

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
        ValidateWindow();
        TryReadServer(out _);
        ReadEnv();
        UpdateLangHint();
        UpdateJavaHint();
    }

    // ── 内容卡片：Mod / 资源包 / 光影包 / 存档 四分类 ──

    /// <summary>后台收集的四分类数据（目录枚举与文件大小统计不占用 UI 线程）。</summary>
    private sealed record ContentData(
        string ModsDir, List<ContentFileRow> ModRows,
        string PacksDir, List<ContentFileRow> PackRows,
        string ShadersDir, List<ContentFileRow> ShaderRows,
        string SavesDir, List<(string Save, List<ContentFileRow> Rows)> Saves);

    /// <summary>当前列表显示所依据的 broken 集合（null = 未标注任何「无法解析」）。</summary>
    private HashSet<string>? _displayedBroken;

    /// <summary>冲突扫描结果缓存（避免启停/删除 mod 后全量重扫 jar）。</summary>
    private ModConflictReport? _cachedReport;
    private string? _reportStamp;
    private DateTime _reportAt;

    /// <summary>填充三个内容分区（IO 后台收集；先渲染，再异步标注「无法解析」）。</summary>
    private async void ContentPanelFill(string gameRoot) =>
        await ContentPanelFillAsync(gameRoot, broken: null);

    private async Task ContentPanelFillAsync(string gameRoot, HashSet<string>? broken)
    {
        _contentGameRoot = gameRoot;
        _displayedBroken = broken;
        var data = await Task.Run(() => CollectContentData(gameRoot));
        if (!string.Equals(gameRoot, _contentGameRoot, StringComparison.OrdinalIgnoreCase)) return;
        BuildContentSections(data, broken);
        if (broken is null) _ = AnnotateBrokenModsAsync(gameRoot);
    }

    private static ContentData CollectContentData(string gameRoot)
    {
        var modsDir = Path.Combine(gameRoot, "mods");
        var packsDir = Path.Combine(gameRoot, "resourcepacks");
        var shadersDir = Path.Combine(gameRoot, "shaderpacks");
        var saves = new List<(string Save, List<ContentFileRow> Rows)>();
        var savesDir = Path.Combine(gameRoot, "saves");
        if (Directory.Exists(savesDir))
        {
            foreach (var save in Directory.EnumerateDirectories(savesDir))
                saves.Add((Path.GetFileName(save),
                    BuildFileRows(Path.Combine(save, "datapacks"), "datapack")));
        }
        return new ContentData(modsDir, BuildFileRows(modsDir, "mod"),
            packsDir, BuildFileRows(packsDir, "resourcepack"),
            shadersDir, BuildFileRows(shadersDir, "shader"),
            savesDir, saves);
    }

    /// <summary>重建四分类；broken 非空时把对应 Mod 行状态改为「无法解析」。</summary>
    private void BuildContentSections(ContentData d, HashSet<string>? broken)
    {
        ModSection.Children.Clear();
        PackSection.Children.Clear();
        ShaderSection.Children.Clear();
        SaveSection.Children.Clear();
        _contentRows.Clear();

        // Mod（含 .disabled）
        ModSection.Children.Add(SectionHeader(Loc.T("Mod 模组"), d.ModRows.Count));
        foreach (var row in d.ModRows)
        {
            var shown = broken is not null && broken.Contains(row.FileName)
                ? row with { StatusText = Loc.T("无法解析") }
                : row;
            _contentRows.Add(shown);
            ModSection.Children.Add(BuildFileRow(shown));
        }
        ModSection.Children.Add(CategoryTail(0, d.ModRows.Count, d.ModsDir));

        // 资源包
        PackSection.Children.Add(SectionHeader(Loc.T("资源包"), d.PackRows.Count));
        foreach (var row in d.PackRows)
        {
            _contentRows.Add(row);
            PackSection.Children.Add(BuildFileRow(row));
        }
        PackSection.Children.Add(CategoryTail(1, d.PackRows.Count, d.PacksDir));

        // 光影包
        ShaderSection.Children.Add(SectionHeader(Loc.T("光影包"), d.ShaderRows.Count));
        foreach (var row in d.ShaderRows)
        {
            _contentRows.Add(row);
            ShaderSection.Children.Add(BuildFileRow(row));
        }
        ShaderSection.Children.Add(CategoryTail(2, d.ShaderRows.Count, d.ShadersDir));

        // 存档（每个存档下的数据包）
        SaveSection.Children.Add(SectionHeader(Loc.T("存档"), d.Saves.Count));
        foreach (var (save, rows) in d.Saves)
        {
            SaveSection.Children.Add(new TextBlock
            {
                Text = save,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.Medium,
                Margin = new Thickness(0, 6, 0, 0),
            });
            foreach (var row in rows)
            {
                _contentRows.Add(row);
                SaveSection.Children.Add(BuildFileRow(row));
            }
        }
        SaveSection.Children.Add(CategoryTail(3, d.Saves.Count, d.SavesDir));

        // 计数落定后再决定说明行要不要露面（当前分类为空 → 交给空状态说话）
        _categoryCounts[0] = d.ModRows.Count;
        _categoryCounts[1] = d.PackRows.Count;
        _categoryCounts[2] = d.ShaderRows.Count;
        _categoryCounts[3] = d.Saves.Count;
        SyncContentHint();
    }

    /// <summary>四个分类当前的条目数（0~3 与分类下标同序），说明行与空状态按它决定谁露面。</summary>
    private readonly int[] _categoryCounts = new int[4];

    /// <summary>
    /// 分类收尾块：有内容时给「路径 + 打开目录」；空的分类换成统一的空状态
    /// （<see cref="EmptyStateView"/>，与主页「暂无已安装版本」同一个语汇）。
    /// 空列表下只摆一行灰色绝对路径 + 一颗小按钮，既看不出这里能干什么，也没有拖拽落点的暗示。
    /// </summary>
    private FrameworkElement CategoryTail(int category, int count, string dir)
    {
        if (count > 0) return OpenDirRow(dir);

        var empty = new EmptyStateView
        {
            Glyph = GlyphForKind(CategoryKind(category)),
            Title = Loc.T(CategoryEmptyTitle(category)),
            Subtitle = Loc.T(CategoryEmptySubtitle(category)),
            ActionText = Loc.T("打开目录"),
            Margin = new Thickness(0, 12, 0, 12),
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };
        empty.ActionClick += (_, _) => OpenPath(dir);
        return empty;
    }

    /// <summary>分类对应的内容类型（决定空状态的图标）。</summary>
    private static string CategoryKind(int category) => category switch
    {
        0 => "mod",
        1 => "resourcepack",
        2 => "shader",
        _ => "folder",
    };

    /// <summary>空分类的主文案：说清「这里该有什么」，而不是只报一个 0。</summary>
    private static string CategoryEmptyTitle(int category) => category switch
    {
        0 => "还没有安装 Mod",
        1 => "还没有资源包",
        2 => "还没有光影包",
        _ => "暂无存档",
    };

    /// <summary>空分类的引导文案：存档是文件夹，不走 .jar / .zip 那条路。</summary>
    private static string CategoryEmptySubtitle(int category) => category == 3
        ? "把存档文件夹放进 saves 目录即可。"
        : "可点击上方分类芯片搜索安装；停用仅改名为 .disabled，可随时还原。";

    /// <summary>说明行只在当前分类有内容时露面（空分类由空状态接管，同一件事不说两遍）。</summary>
    private void SyncContentHint() =>
        ContentHint.Visibility = _categoryCounts[_contentCategory] == 0
            ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>mods 目录指纹（最后写入时间 + 文件数），用于判断扫描缓存是否仍有效。</summary>
    private string ModsStamp()
    {
        var dir = Path.Combine(_s.Settings.DotMinecraft, "mods");
        if (!Directory.Exists(dir)) return "none";
        long count = 0;
        try { count = Directory.EnumerateFiles(dir).LongCount(); } catch { }
        return $"{Directory.GetLastWriteTimeUtc(dir):O}|{count}";
    }

    /// <summary>冲突报告：60 秒内且 mods 目录未变化时复用缓存，否则全量重扫。</summary>
    private async Task<ModConflictReport> GetReportAsync(bool force)
    {
        var stamp = ModsStamp();
        if (!force && _cachedReport is not null && _reportStamp == stamp &&
            DateTime.Now - _reportAt < TimeSpan.FromSeconds(60))
            return _cachedReport;
        var report = await _s.ModConflicts.ScanAsync(_s.Settings.DotMinecraft);
        _cachedReport = report;
        _reportStamp = stamp;
        _reportAt = DateTime.Now;
        return report;
    }

    /// <summary>用 mod 扫描结果标注无法解析的 jar（异步，不阻塞首屏；与已显示结果一致时不重建）。</summary>
    private async Task AnnotateBrokenModsAsync(string gameRoot)
    {
        ModConflictReport report;
        try
        {
            report = await GetReportAsync(force: false);
        }
        catch
        {
            return;
        }
        if (!string.Equals(gameRoot, _contentGameRoot, StringComparison.OrdinalIgnoreCase)) return;
        var broken = report.Mods
            .Where(m => m.Error is not null)
            .Select(m => m.FileName)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        // 与当前显示的标注状态一致则跳过，避免整列表闪烁重绘
        var current = _displayedBroken ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (current.SetEquals(broken)) return;
        await ContentPanelFillAsync(gameRoot, broken.Count == 0 ? null : broken);
    }

    /// <summary>分区标题（名称 + 条目数）。字号走本页 12/14/28 的字阶，不用离档的 13。</summary>
    private static TextBlock SectionHeader(string label, int count)
    {
        return new TextBlock
        {
            Text = Loc.T("{0}（{1}）", label, count),
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.Medium,
            Margin = new Thickness(0, 4, 0, 0),
        };
    }

    /// <summary>目录路径 + 「打开目录」按钮。</summary>
    private StackPanel OpenDirRow(string path)
    {
        var open = new GlowButton
        {
            Content = Loc.T("打开目录"),
            Tag = path,
            FontSize = 12,
            Padding = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(open, Loc.T("打开 {0} 目录", Path.GetFileName(path)));
        open.Click += OpenDir_Click;
        return new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            // 上方留一点缝，与列表行分开：它是收尾的目录入口，不是又一条内容
            Margin = new Thickness(0, 4, 0, 0),
            Children =
            {
                new TextBlock
                {
                    Text = path,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = SecondaryBrush(),
                    TextTrimming = TextTrimming.CharacterEllipsis,
                },
                open,
            },
        };
    }

    private void OpenDir_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not string path) return;
        OpenPath(path);
    }

    /// <summary>打开（必要时先建出）目录。列表底部的按钮与空状态的操作按钮都走这里。
    /// 安卓无「打开文件夹」语义：建出目录后展示路径，交由系统文件管理器接管。</summary>
    private void OpenPath(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            _ = UiDialog.AlertAsync(Loc.T("打开目录"), path);
        }
        catch (Exception ex)
        {
            ShowContentInfo(Loc.T("打开目录失败：{0}", ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>文件行图标按内容类型取字形（原先一律画文件夹字形，看不出是 Mod 还是资源包）。</summary>
    private static string GlyphForKind(string kind) => kind switch
    {
        "mod" => "\uEA93",           // Mod / 内容
        "resourcepack" => "\uEC49",  // 资源包
        "shader" => "\uEC0C",        // 光影包
        "datapack" => "\uEC24",      // 数据包（在存档里）
        _ => "\uEA90",               // 目录
    };

    /// <summary>枚举目录内文件（含 .disabled）生成行模型。</summary>
    private static List<ContentFileRow> BuildFileRows(string dir, string kind)
    {
        var rows = new List<ContentFileRow>();
        if (!Directory.Exists(dir)) return rows;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            var name = Path.GetFileName(file);
            var enabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            var display = enabled ? name : name[..^".disabled".Length];
            rows.Add(new ContentFileRow(
                display, FormatSize(new FileInfo(file).Length),
                enabled ? Loc.T("启用") : Loc.T("已停用"), enabled, kind, file));
        }
        return rows.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>单行：图标 + 文件名 + 状态 + 大小 + 启停 / 删除。</summary>
    private Grid BuildFileRow(ContentFileRow row)
    {
        var info = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new FontIcon { FontFamily = IconFont.Family, Glyph = GlyphForKind(row.Kind), FontSize = 14, Foreground = SecondaryBrush() },
                new TextBlock { Text = row.FileName, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis },
                new TextBlock { Text = row.StatusText, FontSize = 12, Foreground = SecondaryBrush() },
                new TextBlock { Text = row.SizeText, FontSize = 12, Foreground = SecondaryBrush() },
            },
        };

        var toggle = new GlowButton
        {
            Content = row.ToggleVerb,
            Tag = row,
            FontSize = 12,
            Padding = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(toggle, Loc.T("{0} {1}", row.ToggleVerb, row.FileName));
        toggle.Click += ToggleFile_Click;

        var del = new GlowButton
        {
            Content = Loc.T("删除"),
            Tag = row,
            FontSize = 12,
            Padding = new Thickness(10, 2, 10, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = ErrorBrush(),
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(del, Loc.T("删除 {0}", row.FileName));
        del.Click += DeleteFile_Click;

        var actions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 6,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { toggle, del },
        };

        var grid = new Grid { Padding = new Thickness(0, 2, 0, 2) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(info, 0);
        Grid.SetColumn(actions, 1);
        grid.Children.Add(info);
        grid.Children.Add(actions);
        return grid;
    }

    /// <summary>停用 / 启用（改名 .disabled，不删除文件）。</summary>
    private void ToggleFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ContentFileRow row) return;
        var verb = row.ToggleVerb;
        try
        {
            var target = row.Enabled
                ? row.FullPath + ".disabled"
                : row.FullPath[..^".disabled".Length];
            if (File.Exists(target)) File.Delete(target);
            File.Move(row.FullPath, target, overwrite: true);
            ShowContentInfo(Loc.T("{0} 已{1}。", row.FileName, verb), InfoBarSeverity.Success);
            ContentPanelFill(_contentGameRoot);
        }
        catch (Exception ex)
        {
            ShowContentInfo(Loc.T("{0}失败：{1}", verb, ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>删除单个内容文件（确认后执行）。</summary>
    private async void DeleteFile_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not ContentFileRow row) return;
        if (!await UiDialog.ConfirmAsync(Loc.T("删除内容文件"),
                Loc.T("确定要删除 {0} 吗？此操作不可恢复。", row.FileName), Loc.T("删除"), Loc.T("取消")))
            return;
        try
        {
            File.Delete(row.FullPath);
            ShowContentInfo(Loc.T("{0} 已删除。", row.FileName), InfoBarSeverity.Success);
            ContentPanelFill(_contentGameRoot);
        }
        catch (Exception ex)
        {
            ShowContentInfo(Loc.T("删除失败：{0}", ex.Message), InfoBarSeverity.Error);
        }
    }

    /// <summary>把消息显示在内容卡片 InfoBar。</summary>
    private void ShowContentInfo(string message, InfoBarSeverity severity)
    {
        ContentInfoBar.Severity = severity;
        ContentInfoBar.Message = message;
        ContentInfoBar.IsOpen = true;
    }

    // ── 分区导航（概览 / Mod 管理 / 游戏设置 / 启动参数） ──

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // XAML 中首个条目 IsSelected="True" 会在 InitializeComponent 解析期间同步触发本事件，
        // 此时右侧各区尚未创建，直接跳过（XAML 已自带正确的初始可见性）
        if (SecOverview is null) return;
        if (NavList.SelectedItem is FrameworkElement fe && fe.Tag is string t && int.TryParse(t, out var i))
            SetSection(i);
    }

    private void SetSection(int index, bool animate = true)
    {
        Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        SecOverview.Visibility = V(index == 0);
        SecContent.Visibility = V(index == 1);
        SecSettings.Visibility = V(index == 2);
        SecArgs.Visibility = V(index == 3);

        // 选中态落到各行控件自己的 IsActive 上：选中底、悬停底、跟手光斑与圆角都由
        // CategoryNavItem 的模板提供，与设置页 / 下载页左栏同一套颜色与阴影
        NavOverview.IsActive = index == 0;
        NavContent.IsActive = index == 1;
        NavSettings.IsActive = index == 2;
        NavArgs.IsActive = index == 3;

        // 切分区：刚露面的那一块淡入 + 轻微上移，与内容分类芯片（SetCategory）同一套过渡语言。
        // 初始化那一次不播（调用点传 animate: false）—— 进页面本身已有导航转场，再叠一层只是多余的一拍。
        if (animate)
            PlayFadeIn(index switch
            {
                0 => SecOverview,
                1 => SecContent,
                2 => SecSettings,
                _ => SecArgs,
            });
    }

    /// <summary>按页面实际主题取主题字典画刷：先查 App 覆盖（Light/Dark），再查默认主题字典。</summary>
    private Microsoft.UI.Xaml.Media.Brush ThemedBrush(string key)
    {
        var dictKeys = ActualTheme == ElementTheme.Dark ? new[] { "Dark", "Default" } : new[] { "Light" };
        var root = Application.Current.Resources;
        foreach (var dictKey in dictKeys)
        {
            if (root.ThemeDictionaries.TryGetValue(dictKey, out var d) && d is Microsoft.UI.Xaml.ResourceDictionary rd &&
                rd.TryGetValue(key, out var v) && v is Microsoft.UI.Xaml.Media.Brush br)
                return br;
            foreach (var md in root.MergedDictionaries)
                if (md.ThemeDictionaries.TryGetValue(dictKey, out var d2) && d2 is Microsoft.UI.Xaml.ResourceDictionary rd2 &&
                    rd2.TryGetValue(key, out var v2) && v2 is Microsoft.UI.Xaml.Media.Brush br2)
                    return br2;
        }
        return (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources[key];
    }

    // ── 内容分类芯片（Mod / 资源包 / 光影包 / 存档 四个独立界面） ──

    private int _contentCategory;

    private void Cat_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string t && int.TryParse(t, out var i))
        {
            SetCategory(i);
            if (i <= 2) _ = OpenDownloadDialogAsync(i); // Mod / 资源包 / 光影包 → 弹出下载窗口（PCL2 式）
        }
    }

    /// <summary>打开对应分类的下载弹窗（搜索 → 选择 → 安装）。
    /// 安装目标 = 首页当前选中版本（Settings.LastVersion，项目记忆「资源管理最终方案」）；
    /// 首页尚未选中时回落到当前详情版本。</summary>
    private async Task OpenDownloadDialogAsync(int category)
    {
        var kind = category switch { 1 => "resourcepack", 2 => "shader", _ => "mod" };
        var loader = ContentLibraryService.ResolveLoader(_versionId, out _);
        var loaderForSearch = category == 0 && loader != "vanilla" ? loader : null;
        var dialog = new ContentDownloadDialog(_s, kind, loaderForSearch,
            ContentLibraryService.ResolveMc(_versionId), _s.Settings.LastVersion ?? _versionId,
            () => ContentPanelFill(_contentGameRoot));
        dialog.XamlRoot = Ui.Root;
        await dialog.ShowAsync();
    }

    /// <summary>切换当前分类界面：四个分区互斥显示，芯片同步选中态。</summary>
    private void SetCategory(int index)
    {
        _contentCategory = index;
        Visibility V(bool on) => on ? Visibility.Visible : Visibility.Collapsed;
        ModSection.Visibility = V(index == 0);
        PackSection.Visibility = V(index == 1);
        ShaderSection.Visibility = V(index == 2);
        SaveSection.Visibility = V(index == 3);
        ModTools.Visibility = V(index == 0); // 检查更新 / 冲突扫描仅针对 Mod

        StyleChip(CatMods, index == 0);
        StyleChip(CatPacks, index == 1);
        StyleChip(CatShaders, index == 2);
        StyleChip(CatSaves, index == 3);

        // 换分类后，说明行与空状态的可见性跟着这个分类的条目数走（空分类由空状态说话）
        SyncContentHint();

        // 切换分区：内容淡入 + 轻微上移，一次性播放（Storyboard 不驻留 Composition 动画）
        var section = index switch
        {
            0 => (FrameworkElement)ModSection,
            1 => PackSection,
            2 => ShaderSection,
            _ => SaveSection,
        };
        PlayFadeIn(section);
    }

    /// <summary>对刚显示的分区播放 220ms 淡入上移动画（Apple 式轻过渡，30fps 无压力）。</summary>
    private static void PlayFadeIn(FrameworkElement section)
    {
        // 动效关闭时直接落定：不建 RenderTransform、也不跑 storyboard（否则关了动效这一层照样淡入上移）
        if (!AppServices.Instance.Motion.Enabled) return;

        var t = new TranslateTransform { Y = 10 };
        section.RenderTransform = t;
        var sb = new Storyboard();
        var opacity = new DoubleAnimation
        {
            From = 0, To = 1, Duration = new Duration(TimeSpan.FromMilliseconds(MotionFx.Scale(220))),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(opacity, section);
        Storyboard.SetTargetProperty(opacity, "Opacity");
        var rise = new DoubleAnimation
        {
            From = 10, To = 0, Duration = new Duration(TimeSpan.FromMilliseconds(MotionFx.Scale(220))),
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
        };
        Storyboard.SetTarget(rise, t);
        Storyboard.SetTargetProperty(rise, "Y");
        sb.Children.Add(opacity);
        sb.Children.Add(rise);
        sb.Begin();
    }

    /// <summary>芯片选中态：accent 实底 + 深字加粗；未选中交回 ChipStyle 默认配色。</summary>
    private void StyleChip(Button b, bool selected)
    {
        if (selected)
        {
            b.Background = ThemedBrush("AccentFillColorDefaultBrush");
            b.Foreground = ThemedBrush("TextOnAccentFillColorPrimaryBrush");
            b.FontWeight = Microsoft.UI.Text.FontWeights.Bold;
        }
        else
        {
            b.ClearValue(Button.BackgroundProperty);
            b.ClearValue(Button.ForegroundProperty);
            b.ClearValue(Button.FontWeightProperty);
        }
    }

    // ── 检查更新 / 冲突扫描 ──

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        var rows = _contentRows.Where(r => r.Enabled).ToList();
        if (rows.Count == 0)
        {
            ShowContentInfo(Loc.T("没有可检查的文件（已停用的内容不参与更新检查）。"), InfoBarSeverity.Informational);
            return;
        }

        var loader = ContentLibraryService.ResolveLoader(_versionId, out _);
        var mc = ContentLibraryService.ResolveMc(_versionId);
        CheckUpdateBtn.IsEnabled = false;
        try
        {
            // 并行检查（限流 4），N 个 Mod 时比串行快数倍
            var outdated = new System.Collections.Concurrent.ConcurrentBag<string>();
            await Parallel.ForEachAsync(rows, new ParallelOptions { MaxDegreeOfParallelism = 4 }, async (row, ct) =>
            {
                try
                {
                    var result = await _s.ContentLibrary.CheckUpdateAsync(
                        row.FullPath, mc, loader == "vanilla" ? null : loader);
                    if (result is not null) outdated.Add(row.FileName);
                }
                catch
                {
                    // 单个文件失败不影响其它文件
                }
            });
            var found = outdated.OrderBy(s => s, StringComparer.OrdinalIgnoreCase).ToList();
            ShowContentInfo(
                found.Count == 0
                    ? Loc.T("已检查 {0} 个文件，都是最新版本。", rows.Count)
                    : Loc.T("{0} 个文件可更新：{1}。可到下载页搜索同名资源安装新版本。", found.Count, string.Join("、", found)),
                found.Count == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
        }
        finally
        {
            CheckUpdateBtn.IsEnabled = true;
        }
    }

    private async void ScanConflict_Click(object sender, RoutedEventArgs e)
    {
        ScanConflictBtn.IsEnabled = false;
        try
        {
            var report = await GetReportAsync(force: true);
            if (report.Conflicts.Count == 0)
            {
                ShowContentInfo(Loc.T("已扫描 {0} 个模组，未发现冲突。", report.ScannedCount), InfoBarSeverity.Success);
            }
            else
            {
                var first = report.Conflicts[0];
                ShowContentInfo(
                    Loc.T("发现 {0} 个冲突。[{1}] {2}　建议：{3}",
                        report.Conflicts.Count, Loc.TCore(first.Kind), Loc.TCore(first.Detail), Loc.TCore(first.Suggestion)),
                    InfoBarSeverity.Warning);
            }
        }
        catch (Exception ex)
        {
            ShowContentInfo(Loc.T("冲突扫描失败：{0}", ex.Message), InfoBarSeverity.Error);
        }
        finally
        {
            ScanConflictBtn.IsEnabled = true;
        }
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "0 B",
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    private void ApplyIcon(string? iconPath)
    {
        // iconPath 已由 DisplayIconPath 解析好（自定义 > 加载器 > 版本类型），这里只负责加载
        var source = iconPath is not null ? VersionRowHelpers.ResolveIconSource(iconPath) : null;
        if (source is null)
        {
            IconImage.Source = null;
            IconImage.Visibility = Visibility.Collapsed;
            IconFallback.Visibility = Visibility.Visible;
        }
        else
        {
            IconImage.Source = source;
            IconImage.Visibility = Visibility.Visible;
            IconFallback.Visibility = Visibility.Collapsed;
        }
    }

    // ── 头部 ──

    /// <summary>返回版本列表（壳路由回退）。</summary>
    private void Back_Click(object sender, RoutedEventArgs e) =>
        AppNavigation.Navigate("versions");

    /// <summary>启动入口（对齐首页规则）：选定版本 → 游戏页覆盖层自动拉起启动链。</summary>
    private void Launch_Click(object sender, RoutedEventArgs e)
    {
        // 下载目标 = 当前选中版本（Settings.LastVersion），启动即视为选中
        _s.Settings.LastVersion = _versionId;
        GameLauncherService.Instance.SelectedVersion = _versionId;
        _s.SaveSettings();
        AppNavigation.PushOverlay(new GamePage(autoLaunch: true));
    }

    private async void ChangeIcon_Click(object sender, RoutedEventArgs e)
    {
        // 安卓无 FileOpenPicker 流程：自定义图标暂不可换
        await UiDialog.AlertAsync(Loc.T("更换图标"),
            Loc.T("安卓端暂不支持选择本地图片，实例图标按版本类型与加载器自动匹配。"));
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
                await new ThemedDialog
                {
                    Title = Loc.T("验证文件"),
                    Content = Loc.T("一切正常，未发现缺失或损坏的文件。"),
                    CloseButtonText = Loc.T("确定"),
                    XamlRoot = Ui.Root,
                }.ShowAsync();
                return;
            }
            if (report.VersionJsonBroken && !report.CanAutoRestoreJson)
            {
                await new ThemedDialog
                {
                    Title = Loc.T("验证文件"),
                    Content = Loc.T("版本 JSON 缺失或损坏且无法自动恢复，请到下载页重新安装。"),
                    CloseButtonText = Loc.T("确定"),
                    XamlRoot = Ui.Root,
                }.ShowAsync();
                return;
            }

            var count = report.Issues.Count + (report.VersionJsonBroken ? 1 : 0);
            var confirm = new ThemedDialog
            {
                Title = Loc.T("验证文件"),
                Content = Loc.T("发现 {0} 个缺失或损坏的文件，立即补全吗？", count),
                PrimaryButtonText = Loc.T("立即补全"),
                CloseButtonText = Loc.T("取消"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = Ui.Root,
            };
            if (await confirm.ShowAsync() != ContentDialogResult.Primary) return;

            // 进度对话框（无按钮，完成后 Hide）
            var bar = new ProgressBar { Minimum = 0, Maximum = 100 };
            var step = new TextBlock { FontSize = 12, Opacity = 0.7 };
            var progressDlg = new ThemedDialog
            {
                Title = Loc.T("正在补全缺失文件"),
                Content = new StackPanel { Spacing = 8, Children = { bar, step } },
                PrimaryButtonText = string.Empty,
                CloseButtonText = string.Empty,
                XamlRoot = Ui.Root,
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
                await new ThemedDialog
                {
                    Title = Loc.T("验证文件"),
                    Content = Loc.T("补全完成，文件已恢复完整。"),
                    CloseButtonText = Loc.T("确定"),
                    XamlRoot = Ui.Root,
                }.ShowAsync();
            }
            catch (PreflightException ex)
            {
                progressDlg.Hide();
                var listed = string.Join("\n", ex.FailedFiles.Take(5).Select(f => "• " + f.Path));
                await new ThemedDialog
                {
                    Title = Loc.T("补全失败"),
                    Content = Loc.T("以下文件补全失败：\n{0}", listed),
                    CloseButtonText = Loc.T("确定"),
                    XamlRoot = Ui.Root,
                }.ShowAsync();
            }
            catch (Exception ex)
            {
                progressDlg.Hide();
                await InstanceActions.ShowErrorAsync(Ui.Root!, Loc.T("补全失败"), ex);
            }
        }
        catch (Exception ex)
        {
            await InstanceActions.ShowErrorAsync(Ui.Root!, Loc.T("验证文件失败"), ex);
        }
        finally
        {
            VerifyBtn.IsEnabled = true;
        }
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        var result = await InstanceActions.RenameAsync(Ui.Root!,
            _s.Settings.DotMinecraft, _versionId);
        if (result is null) return;
        if (result.Error is not null)
        {
            await InstanceActions.ShowErrorAsync(Ui.Root!, Loc.T("重命名失败"), result.Error);
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
        var result = await InstanceActions.CopyAsync(Ui.Root!, dotMc, _versionId, isolated);
        if (result is null) return;
        if (result.Error is not null)
        {
            await InstanceActions.ShowErrorAsync(Ui.Root!, Loc.T("复制失败"), result.Error);
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var running = _s.Runtime.Instances.Any(i => i.VersionId == _versionId);
        var profile = _store.Get(_versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _s.Settings);
        var message = running
            ? Loc.T("版本 {0} 正在运行，请先停止游戏再删除。", _versionId)
            : Loc.T("确定要删除版本 {0} 吗？\n将删除其版本文件夹，此操作不可恢复。", _versionId)
              + (isolated ? Loc.T("\n注意：版本隔离下的 mods / 存档等也将一并删除。") : "");
        var dlg = new ThemedDialog
        {
            Title = Loc.T("删除版本"),
            Content = message,
            PrimaryButtonText = running ? string.Empty : Loc.T("删除"),
            CloseButtonText = Loc.T("关闭"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Ui.Root,
        };
        if (running || await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            InstalledVersionManager.Remove(_s.Settings.DotMinecraft, _versionId);
            _store.Remove(_versionId);
        }
        catch (Exception ex)
        {
            await InstanceActions.ShowErrorAsync(Ui.Root!, Loc.T("删除失败"), ex);
            return;
        }
        AppNavigation.Navigate("versions"); // 回到版本列表页（OnShown 会重新加载列表）
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
            ? Loc.T("隔离方式将切换为「{0}」，当前没有需要迁移的内容目录。", IsolationName(target))
            : Loc.T("隔离方式将切换为「{0}」。\n\n以下目录将从\n{1}\n移动到\n{2}\n\n",
                  IsolationName(target), fromRoot, toRoot)
              + string.Join("\n", movable.Select(d => "• " + d));
        var dlg = new ThemedDialog
        {
            Title = Loc.T("切换版本隔离"),
            Content = new TextBlock { Text = body, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = movable.Count == 0 ? string.Empty : Loc.T("立即移动"),
            SecondaryButtonText = Loc.T("仅切换不移动"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = movable.Count == 0 ? ContentDialogButton.Secondary : ContentDialogButton.Primary,
            XamlRoot = Ui.Root,
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
            await InstanceActions.ShowErrorAsync(Ui.Root!, Loc.T("迁移内容目录失败"), ex);
            return;
        }
        finally
        {
            var running = _s.Runtime.Instances.Any(i => i.VersionId == _versionId);
            RenameBtn.IsEnabled = !running;
            CopyBtn.IsEnabled = !running;
        }
        ApplyIsolation(target);
    }

    private void IsolationReset_Click(object sender, RoutedEventArgs e) =>
        IsolationRadio.SelectedIndex = 0; // 仅覆盖时可见，置 0 必然触发 SelectionChanged（含迁移询问）

    private static string IsolationName(string? isolation) => isolation switch
    {
        "shared" => Loc.T("共享目录"),
        "isolated" => Loc.T("版本隔离"),
        _ => Loc.T("跟随全局默认"),
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
        GameRootPreview.Text = Loc.T("游戏目录：{0}",
            InstanceLayout.GameRoot(_s.Settings.DotMinecraft, _versionId, isolated));
    }

    // ── 档位可见性 ──

    /// <summary>按三个档位单选控制子控件可见性（「恢复跟随」仅在非全局档位出现）。</summary>
    private void UpdateModeVisibility()
    {
        var memMode = MemoryModeRadio.SelectedIndex;
        MemManualPanel.Visibility = memMode == 2 ? Visibility.Visible : Visibility.Collapsed;
        AutoMemText.Visibility = memMode == 1 ? Visibility.Visible : Visibility.Collapsed;
        MemoryReset.Visibility = memMode == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (memMode == 1)
            AutoMemText.Text = Loc.T("自动：按物理内存分配约 {0}。", Gb(SystemMemory.AutoMaxMb()));

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
            2 => (int)MaxMemSlider.Value * 1024,
            _ => _s.Settings.DefaultMaxMemoryMb,
        };
        var scale = totalMb > 0 ? barWidth / totalMb : 0;
        MemoryUsedBar.Width = Math.Clamp(usedMb * scale, 0, barWidth);
        MemoryAllocBar.Width = Math.Clamp(allocMb * scale, 0, barWidth);
        MemoryUsageText.Text = Loc.T("已使用 {0} / 总 {1}　·　本版本分配 {2}", Gb(usedMb), Gb(totalMb), Gb(allocMb));
    }

    private static string Gb(long mb) => $"{mb / 1024.0:0.#} GB";

    // ── 内存 ──

    private void MemoryMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_loading) return;
        if (MemoryModeRadio.SelectedIndex == 2) _memDirty = true;
        UpdateModeVisibility();
        RefreshMemoryBar();
        ScheduleSave();
    }

    private void MaxMemSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (MaxMemText is null) return;
        MaxMemText.Text = $"{(int)e.NewValue} GB";
        if (_loading) return;
        _memDirty = true;
        RefreshMemoryBar();
        ScheduleSave();
    }

    private void MemoryReset_Click(object sender, RoutedEventArgs e) => MemoryModeRadio.SelectedIndex = 0;

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
            JavaCombo.Items.Add(new ComboBoxItem { Content = Loc.T("自定义 · {0}", _javaOverride), Tag = _javaOverride });

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
        1 => Loc.T("自动：按该版本 JSON 推荐的主版本匹配本机 Java。"),
        2 => _javaOverride is null
            ? Loc.T("自定义：尚未选择，将按版本推荐自动匹配。")
            : Loc.T("已为这个版本指定 Java：{0}", _javaOverride),
        _ => Loc.T("当前跟随全局：{0}", _s.Settings.JavaExecutable ?? Loc.T("自动匹配可用 Java")),
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
        // 安卓无文件选择流程（内置 JRE 由组件部署就位）：保持自动匹配
        await UiDialog.AlertAsync(Loc.T("浏览 Java"),
            Loc.T("安卓端使用内置 JRE 与自动匹配，暂不支持手动指定 Java 可执行文件。"));
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
            ? Loc.T("宽高需同时填写 200–32767 之间的数值。")
            : WindowModeRadio.SelectedIndex switch
            {
                1 => Loc.T("自动：854 × 480 窗口化启动。"),
                _ => GameLauncher.ModernWindowArgs(_versionId)
                    ? Loc.T("窗口尺寸通过 --width/--height 传入；未改动时使用游戏默认大小。")
                    : Loc.T("此版本可能不支持窗口参数（需要 1.13 及以上）。"),
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
                ? Loc.T("留空表示不自动连接；该版本支持 quickPlayMultiplayer。")
                : Loc.T("留空表示不自动连接。");
            return true;
        }

        var ok = ServerRegex.IsMatch(text) && PortInRange(text);
        if (!ok)
        {
            ServerBox.BorderBrush = ErrorBrush();
            ServerHintText.Foreground = ErrorBrush();
            ServerHintText.Text = Loc.T("格式不正确：应为 host 或 host:port，端口 1–65535。");
            return false;
        }

        ServerBox.BorderBrush = null;
        if (supportsQuickPlay)
        {
            ServerHintText.Foreground = SecondaryBrush();
            ServerHintText.Text = Loc.T("启动时将自动连接该服务器（quickPlayMultiplayer）。");
        }
        else
        {
            ServerHintText.Foreground = CautionBrush();
            ServerHintText.Text = Loc.T("此版本不支持自动连服，该设置不会生效。");
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
            Content = new FontIcon { FontFamily = IconFont.Family, Glyph = "\uEAA5", FontSize = 12 },
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(8, 4, 8, 4),
            VerticalAlignment = VerticalAlignment.Center,
        };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(nameBox, Loc.T("环境变量名称"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(valueBox, Loc.T("环境变量值"));
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(del, Loc.T("删除环境变量"));

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
                if (val.Trim().Length > 0) invalid.Add(Loc.T("（名称不能为空）"));
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
            msgs.Add(Loc.T("以下名称不合法（仅字母、数字、下划线，且不以数字开头），已忽略：")
                     + string.Join("、", invalid));
        if (dupes.Count > 0)
            msgs.Add(Loc.T("名称重复（后者覆盖前者）：") + string.Join("、", dupes));
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
            ? Loc.T("当前跟随全局：{0}", _s.Settings.Language)
            : Loc.T("已覆盖全局语言；启动前会写入该版本的 options.txt。");

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
            if (_memDirty)
            {
                profile.MemoryMinMb = 512;
                profile.MemoryMaxMb = (int)MaxMemSlider.Value * 1024;
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
