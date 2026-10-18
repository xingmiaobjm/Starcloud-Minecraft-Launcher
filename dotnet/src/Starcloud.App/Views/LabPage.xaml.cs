// SC 实验室：内置工具箱。四个分区共用一页，左侧子导航切换。
//   投影工坊   —— 投影文件解析 / 元数据编辑 / 格式转换 / 方块统计（Core: ProjectionService）
//   彩色文字   —— § 格式代码文本生成（Core: McText）
//   世吞生成器 —— 分层 TNT 阵列投影生成（Core: WorldEater）
//   指令助手   —— 命令模板 + 参数装配（Core: CommandHelper，同一份模板也注册给 Nova）

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Controls;
using Starcloud.App.Views.Dialogs;
using Starcloud.Core.Services;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Projection;
using System.IO;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Pickers;

namespace Starcloud.App.Views;

public sealed partial class LabPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private ProjectionInfo? _currentProjection;
    private CommandTemplate? _currentTemplate;
    private readonly Dictionary<string, TextBox> _cmdParamBoxes = new(StringComparer.Ordinal);
    private bool _devNoticeShown;

    public LabPage()
    {
        InitializeComponent();
        // 空间模式下预览高度跟着内容区视口走
        ToolScroll.SizeChanged += (_, _) => UpdatePreviewHeight();
        // 页面被丢弃/窗口关闭时务必把系统光标还回来，否则退出后鼠标会一直是隐藏的
        Unloaded += (_, _) => SetPreviewSpaceLayout(false);
        if (!_cursorRestoreHooked && App.MainWindow is { } mainWindow)
        {
            _cursorRestoreHooked = true;
            mainWindow.Closed += (_, _) => SetSystemCursorHidden(false);
            // 兜底：进程退出时也要还原，别把用户的鼠标指针留在隐藏状态
            System.AppDomain.CurrentDomain.ProcessExit += (_, _) => SetSystemCursorHidden(false);
        }
        Loaded += (_, _) =>
        {
            RefreshProjectionList();
            BuildCommandPickers();
            UpdateEaterEstimate();
            InitRichEditor();
            // 子导航的初始选中在 XAML 解析期就触发了（那时各 Section 还不存在，处理器直接返回），
            // 这里补一次分区初始化——否则首次进入实验室时 3D 预览的 WebView2 永远不导航，预览区一片空白
            ApplySection(CurrentToolTag());
            _ = ShowDevNoticeAsync();
        };
    }

    /// <summary>开发版提示：实验室尚未完工，进入页面时弹一次，避免用户把半成品当成正式功能</summary>
    private async Task ShowDevNoticeAsync()
    {
        if (_devNoticeShown || XamlRoot is null) return;
        _devNoticeShown = true;
        try
        {
            var dialog = new ThemedDialog
            {
                Title = Loc.T("实验室（开发中）"),
                Content = Loc.T("实验室功能受限，未完成开发。当前版本仅供内部测试，功能可能不完整或随时调整。"),
                CloseButtonText = Loc.T("知道了"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            await dialog.ShowAsync();
        }
        catch
        {
            // 页面已离开或已有其他弹层：提示失败不影响使用
        }
    }

    private string CurrentToolTag() =>
        (ToolList.SelectedItem as FrameworkElement)?.Tag as string ?? "projection";

    // ── 子导航 ──

    private void Tool_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        // XAML 首个条目 IsSelected 会在解析期同步触发本事件，此时各 Section 尚未创建
        if (SectionProjection is null) return;
        ApplySection(CurrentToolTag());
    }

    /// <summary>切到某个分区：可见性 + 动效 + 该分区的延迟初始化</summary>
    private void ApplySection(string tag)
    {
        ToolProjection.IsActive = tag == "projection";
        ToolText.IsActive = tag == "text";
        ToolCommunity.IsActive = tag == "community";
        ToolCommands.IsActive = tag == "commands";

        var target = tag switch
        {
            "text" => SectionText,
            "community" => SectionCommunity,
            "commands" => SectionCommands,
            _ => SectionProjection,
        };
        foreach (var section in new[] { SectionProjection, SectionText, SectionCommunity, SectionCommands })
            section.Visibility = ReferenceEquals(section, target) ? Visibility.Visible : Visibility.Collapsed;

        if (ToolScroll.VerticalOffset > 0) ToolScroll.ChangeView(null, 0, null, disableAnimation: false);
        MotionFx.Reset(target);
        MotionFx.FadeSlideIn(target, 18f);

        // 投影社区：回填源设置并首次自动拉一次列表（有 10 分钟缓存，重复进入不重复打 API）
        if (tag == "community")
        {
            if (CommunityApiBaseBox.Text.Length == 0) CommunityApiBaseBox.Text = _s.Settings.ProjectionGithubApi ?? "";
            if (CommunityCustomSourcesBox.Text.Length == 0) CommunityCustomSourcesBox.Text = _s.Settings.ProjectionCustomSources ?? "";
            if (CommunityTokenBox.Password.Length == 0) CommunityTokenBox.Password = _s.Settings.ProjectionGithubToken ?? "";
            CommunityBaseLabel.Text = Loc.T("当前 GitHub 通道：{0}", _s.ProjectionCommunity.GitHub.CurrentBaseLabel);
            if (CommunityGrid.Items.Count == 0) _ = SearchCommunityAsync(forceRefresh: false);
        }
        // 彩色文字：分区可见后再初始化示例文本（折叠状态下 RichEditBox 的格式操作不可靠）
        if (tag == "text") InitRichEditor();
        // 投影工坊的 3D 预览同理，延迟到进入该分区
        if (tag == "projection") _ = InitPreviewWebAsync();
        else if (_previewSpaceLayout) ExitPreviewSpaceMode();   // 切走时收起空间模式的满屏布局
    }

    // ── 投影工坊 · 3D 预览（WebView2 + 体素渲染） ──

    private bool _previewReady;
    private string? _previewPendingPath;
    /// <summary>渲染进程已挂掉（白屏）：此时再 PostWebMessage 会把 UI 线程卡住，必须先重建预览页</summary>
    private bool _previewDead;
    /// <summary>预览请求序号：快速切换投影时只认最后一次，避免旧结果覆盖新结果</summary>
    private int _previewSeq;
    /// <summary>首次预览的方块预算：超过就抽样出图，再弹窗问是否全量</summary>
    private const int PreviewSampleBudget = 20_000;
    /// <summary>本次会话里已确认过「完整加载」的投影（切回去不再重复问）</summary>
    private readonly HashSet<string> _fullPreviewApproved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>空间模式布局是否已展开（预览铺满内容区）</summary>
    private bool _previewSpaceLayout;

    // 空间模式要改的样式值，进模式前先记下来，退出时原样还原
    private Thickness _cardPadding;
    private CornerRadius _cardCorner;
    private Thickness _scrollPadding;
    private bool _spaceMetricsCaptured;

    /// <summary>
    /// 空间模式：把预览放大到整个内容区（右侧），列表卡与元数据/统计收起，
    /// 左侧导航（主导航 + 本页子导航）保持原样不动。
    /// </summary>
    private void SetPreviewSpaceLayout(bool on)
    {
        if (_previewSpaceLayout == on) return;
        _previewSpaceLayout = on;

        if (!_spaceMetricsCaptured)
        {
            _cardPadding = ProjectionDetailCard.Padding;
            _cardCorner = ProjectionDetailCard.CornerRadius;
            _scrollPadding = ToolScroll.Padding;
            _spaceMetricsCaptured = true;
        }

        ProjectionListCard.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ProjectionMetaBlock.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        PreviewLabel.Visibility = on ? Visibility.Collapsed : Visibility.Visible;
        ProjectionStatBlock.Visibility = on ? Visibility.Collapsed : Visibility.Visible;

        // 铺满内容区：页面解除限宽、卡片去内边距与圆角、收起滚动
        LabRoot.MaxWidth = on ? double.PositiveInfinity : 1280;
        ToolScroll.Padding = on ? new Thickness(0) : _scrollPadding;
        ToolScroll.VerticalScrollMode = on ? ScrollMode.Disabled : ScrollMode.Enabled;
        ToolScroll.VerticalScrollBarVisibility = on ? ScrollBarVisibility.Hidden : ScrollBarVisibility.Auto;
        SectionProjection.Spacing = on ? 0 : 16;
        ProjectionDetailCard.Padding = on ? new Thickness(0) : _cardPadding;
        ProjectionDetailCard.CornerRadius = on ? new CornerRadius(0) : _cardCorner;
        ProjectionCardStack.Spacing = on ? 0 : 12;

        SetSystemCursorHidden(on);   // 空间模式藏起系统鼠标（WebView2 的指针锁定在本机不藏）
        UpdatePreviewHeight();
    }

    /// <summary>空间模式下预览高度跟随内容区视口（平时固定 380）</summary>
    private void UpdatePreviewHeight()
    {
        if (!_previewSpaceLayout)
        {
            PreviewHost.Height = 380;
            return;
        }
        var viewport = ToolScroll.ViewportHeight;
        if (double.IsNaN(viewport) || viewport <= 0) viewport = ActualHeight;
        // 留 2px 余量，免得正好等于视口时闪出滚动条
        PreviewHost.Height = Math.Max(240, viewport - 2);
    }

    /// <summary>切走投影工坊分区时，让网页端退出空间模式（画面会被收起）</summary>
    private void ExitPreviewSpaceMode()
    {
        SetPreviewSpaceLayout(false);
        try { PreviewWeb.CoreWebView2?.PostWebMessageAsJson("""{"type":"exitSpace"}"""); }
        catch { /* 网页端已销毁 */ }
    }

    /// <summary>空间模式键位提示是否已展示过（static：每次启动器运行只提示一次，与页面重建无关）</summary>
    private static bool _spaceHintShown;

    /// <summary>系统鼠标指针是否被我们隐藏着（空间模式）</summary>
    private static bool _systemCursorHidden;

    /// <summary>窗口关闭时恢复光标的事件是否已挂（只挂一次）</summary>
    private static bool _cursorRestoreHooked;

    // ── 隐藏系统光标（空间模式）──
    // WebView2 的指针锁定在本机**不会**隐藏系统光标（GetCursorInfo 仍报 CURSOR_SHOWING）：
    // 指针所在窗口属于 msedgewebview2 的渲染进程，它每次 WM_SETCURSOR 都会重设光标形状，
    // 而 ShowCursor 是按线程计的、WinUI 的 ProtectedCursor 又没有"无光标"形状——
    // 所以只能把系统光标整体换成透明光标，退出时用 SPI_SETCURSORS 让系统恢复默认。
    //
    // 注意光标必须是 **32 位带 alpha 的现代光标**（CreateIconIndirect + 32bpp DIB）：
    // 最早用 CreateCursor 造的单色光标会走传统软件光标路径，指针移动要应用线程重绘，
    // 叠加连续出帧的 WebGL 会明显发卡（实测踩过）。
    private const uint OCR_NORMAL = 32512, OCR_IBEAM = 32513, OCR_HAND = 32649;
    private const uint SPI_SETCURSORS = 0x0057;

    /// <summary>
    /// 要替换的光标 id：只挑预览区真会出现的三种（每次 SetSystemCursor 都会触发一次系统级
    /// 光标刷新，全量 14 种换下来进入空间模式会顿一下）
    /// </summary>
    private static readonly uint[] AllCursorIds = [OCR_NORMAL, OCR_IBEAM, OCR_HAND];

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct IconInfo
    {
        public bool IsIcon;
        public int HotspotX;
        public int HotspotY;
        public IntPtr MaskBitmap;
        public IntPtr ColorBitmap;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CieXyz
    {
        public int X, Y, Z;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct CieXyzTriple
    {
        public CieXyz Red, Green, Blue;
    }

    /// <summary>BITMAPV5HEADER（必须完整，缺字段 CreateDIBSection 会失败）</summary>
    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct BitmapV5Header
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
        public uint RedMask;
        public uint GreenMask;
        public uint BlueMask;
        public uint AlphaMask;
        public uint CSType;
        public CieXyzTriple Endpoints;
        public uint GammaRed;
        public uint GammaGreen;
        public uint GammaBlue;
        public uint Intent;
        public uint ProfileData;
        public uint ProfileSize;
        public uint Reserved;
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr CreateIconIndirect(ref IconInfo iconInfo);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetSystemCursor(IntPtr hcur, uint id);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, IntPtr pvParam, uint fWinIni);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BitmapV5Header header, uint usage,
        out IntPtr bits, IntPtr section, uint offset);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitCount, byte[]? bits);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [System.Runtime.InteropServices.DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr handle);

    /// <summary>32×32 全透明光标（32 位带 alpha，颜色位图 alpha 全 0、掩码全 1）</summary>
    private static IntPtr CreateBlankCursor()
    {
        const int size = 32;
        var hdc = CreateCompatibleDC(IntPtr.Zero);
        try
        {
            var header = new BitmapV5Header
            {
                Size = (uint)System.Runtime.InteropServices.Marshal.SizeOf<BitmapV5Header>(),
                Width = size,
                Height = size,
                Planes = 1,
                BitCount = 32,
                Compression = 0,             // BI_RGB
                CSType = 0x73524742,         // 'sRGB'
            };
            // 颜色位图：32bpp DIB，CreateDIBSection 会清零 → alpha 全 0（全透明）
            var color = CreateDIBSection(hdc, ref header, 0, out _, IntPtr.Zero, 0);
            if (color == IntPtr.Zero) return IntPtr.Zero;

            // AND 掩码必须全 1：掩码语义是"保留屏幕内容"，
            // 全 0 会被当成纯黑块画出来（实测踩过），全 1 才是"什么都不画"
            var maskBits = new byte[size * size / 8];
            Array.Fill(maskBits, (byte)0xFF);
            var mask = CreateBitmap(size, size, 1, 1, maskBits);
            if (mask == IntPtr.Zero)
            {
                DeleteObject(color);
                return IntPtr.Zero;
            }

            var info = new IconInfo { IsIcon = false, MaskBitmap = mask, ColorBitmap = color };
            var cursor = CreateIconIndirect(ref info);
            DeleteObject(color);
            DeleteObject(mask);
            return cursor;
        }
        finally
        {
            DeleteDC(hdc);
        }
    }

    /// <summary>
    /// 隐藏/恢复系统鼠标指针。SetSystemCursor 会把句柄交给系统（由系统销毁），
    /// 所以每个光标 id 都要一份独立句柄。
    /// </summary>
    private static void SetSystemCursorHidden(bool hidden)
    {
        if (_systemCursorHidden == hidden) return;
        _systemCursorHidden = hidden;
        try
        {
            if (hidden)
            {
                foreach (var id in AllCursorIds)
                {
                    var blank = CreateBlankCursor();
                    if (blank != IntPtr.Zero) SetSystemCursor(blank, id);
                }
            }
            else
            {
                SystemParametersInfo(SPI_SETCURSORS, 0, IntPtr.Zero, 0);
            }
        }
        catch
        {
            // 藏/还原光标失败不影响功能
        }
    }

    private async Task InitPreviewWebAsync()
    {
        if (_previewReady) return;
        try
        {
            // 空间模式要连续出帧：隐藏/被遮挡的 WebView2 会被 Chromium 节流到几乎不出帧
            //（与皮肤预览 Skin3DView 同一个坑），这里关掉后台节流
            var options = new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions
            {
                AdditionalBrowserArguments =
                    "--disable-backgrounding-occluded-windows --disable-renderer-backgrounding " +
                    "--disable-background-timer-throttling",
            };
            var environment = await Microsoft.Web.WebView2.Core.CoreWebView2Environment
                .CreateWithOptionsAsync(null, null, options);
            await PreviewWeb.EnsureCoreWebView2Async(environment);
            if (PreviewWeb.CoreWebView2 is not { } core) return;
            _previewDead = false;
            // 渲染进程可能被 Chromium 判为无响应而回收（实测 8 万方块的投影切换时出现过白屏）：
            // 收到通知就重建预览页，别让后续 PostWebMessage 卡在死进程上
            core.ProcessFailed += (_, e) => OnPreviewProcessFailed(e);
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            var html = Path.Combine(AppContext.BaseDirectory, "Assets", "Lab", "projection-viewer.html");
            if (!File.Exists(html)) throw new FileNotFoundException("找不到预览页面", html);
            // 页面加载完成前推的消息会丢：网页端加载好后回一条 ready，这里再补推一次
            core.WebMessageReceived += PreviewWeb_Message;
            core.Navigate(new Uri(html).AbsoluteUri);
            _previewReady = true;
            LauncherLog.Write("lab", "预览页已就绪");
            // 初始化期间用户已经选中过投影：补推一次
            if (_previewPendingPath is { } pending) _ = ShowVoxelsAsync(pending);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("lab", $"3D 预览不可用：{ex.Message}");
        }
    }

    /// <summary>渲染进程挂掉后的恢复：优先 reload（浏览器进程通常还活着），否则重建预览页</summary>
    private void OnPreviewProcessFailed(Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedEventArgs e)
    {
        _previewDead = true;
        LauncherLog.Write("lab", $"预览页进程失败：{e.ProcessFailedKind}");
        DispatcherQueue.TryEnqueue(async () =>
        {
            if (e.ProcessFailedKind != Microsoft.Web.WebView2.Core.CoreWebView2ProcessFailedKind.BrowserProcessExited)
            {
                try
                {
                    PreviewWeb.CoreWebView2?.Reload();
                    _previewDead = false;
                    return;
                }
                catch { /* 落到重建 */ }
            }
            _previewReady = false;
            _previewDead = false;
            await InitPreviewWebAsync();
        });
    }

    private void PreviewWeb_Message(Microsoft.Web.WebView2.Core.CoreWebView2 sender,
        Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(e.WebMessageAsJson);
            if (!doc.RootElement.TryGetProperty("type", out var type)) return;
            var kind = type.GetString();
            if (kind == "ready")
            {
                // 页面加载完成前推的消息会丢：收到 ready 后补推一次
                if (_previewPendingPath is { } pending) _ = ShowVoxelsAsync(pending);
                PostInit();
            }
            else if (kind == "space")
            {
                // 网页端进出空间模式 → 预览放大 / 还原到内容区
                var on = doc.RootElement.TryGetProperty("on", out var flag) && flag.GetBoolean();
                SetPreviewSpaceLayout(on);
            }
            else if (kind == "hintShown")
            {
                _spaceHintShown = true;
            }
        }
        catch
        {
            // 网页端消息格式不认识：忽略
        }
    }

    /// <summary>告知网页端：本次运行是否还没弹过空间模式键位提示</summary>
    private void PostInit()
    {
        try
        {
            PreviewWeb.CoreWebView2?.PostWebMessageAsJson(
                System.Text.Json.JsonSerializer.Serialize(new { type = "init", firstRun = !_spaceHintShown }));
        }
        catch
        {
            // 网页端已销毁
        }
    }

    /// <summary>把字典按条数切块（WebView2 单条消息不宜过大）</summary>
    private static IEnumerable<Dictionary<string, T>> Chunk<T>(IReadOnlyDictionary<string, T> source, int size)
    {
        var buffer = new Dictionary<string, T>(size, StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            buffer[key] = value;
            if (buffer.Count < size) continue;
            yield return buffer;
            buffer = new Dictionary<string, T>(size, StringComparer.Ordinal);
        }
        if (buffer.Count > 0) yield return buffer;
    }

    /// <summary>把选中投影的体素推给预览页（大投影的解析放后台线程）</summary>
    private async Task ShowVoxelsAsync(string path, bool full = false)
    {
        _previewPendingPath = path;
        var seq = ++_previewSeq;
        LauncherLog.Write("lab", $"预览请求：{Path.GetFileName(path)}（就绪={_previewReady}，全量={full}）");
        if (_previewDead) return;   // 渲染进程已挂：等 ProcessFailed 的恢复流程重建后再推
        if (!_previewReady || PreviewWeb.CoreWebView2 is not { } core) return;
        try
        {
            // 大投影先按预算抽样出图（秒开），再问用户要不要全量；全量可能卡顿
            var budget = full || _fullPreviewApproved.Contains(path) ? int.MaxValue : PreviewSampleBudget;
            var assets = _s.MinecraftAssets;
            var (voxels, pack) = await Task.Run(() =>
            {
                var v = VoxelExporter.Read(path, budget, assets);
                var p = ProjectionAssetCollector.Collect(v.BlockIds, assets.Read);
                return (v, p);
            });

            // 快速切换投影时旧请求可能后完成：只认最后一次
            if (seq != _previewSeq || _previewDead) return;

            if (voxels.Unsupported is { } reason)
            {
                core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new { type = "notice", text = reason }));
                return;
            }

            void Post(object payload) =>
                core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(payload));

            Post(new
            {
                type = "voxelBegin",
                sizeX = voxels.SizeX,
                sizeY = voxels.SizeY,
                sizeZ = voxels.SizeZ,
                truncated = voxels.Truncated,
                assetSource = assets.SourceLabel,
            });

            // blockstates / models 分块（每块 200 条），避免单条消息过大
            foreach (var chunk in Chunk(pack.BlockStates, 200))
                Post(new { type = "voxelStates", items = chunk });
            foreach (var chunk in Chunk(pack.Models, 200))
                Post(new { type = "voxelModels", items = chunk });

            // 纹理（base64 data URL）分块，每块 60 张
            var textureItems = pack.Textures.ToDictionary(
                kv => kv.Key,
                kv => "data:image/png;base64," + Convert.ToBase64String(kv.Value),
                StringComparer.Ordinal);
            foreach (var chunk in Chunk(textureItems, 60))
                Post(new { type = "voxelTextures", items = chunk });

            // 方块分块发（一次两万个）
            const int blockChunk = 20000;
            var blocks = voxels.Blocks;
            for (var start = 0; start < blocks.Count; start += blockChunk)
            {
                var end = Math.Min(start + blockChunk, blocks.Count);
                var items = new object[end - start];
                for (var i = start; i < end; i++)
                {
                    var block = blocks[i];
                    items[i - start] = new object[] { block.X, block.Y, block.Z, block.State };
                }
                Post(new { type = "voxelBlocks", items });
            }
            LauncherLog.Write("lab",
                $"预览已推送：方块 {blocks.Count}，blockstates {pack.BlockStates.Count}，models {pack.Models.Count}，纹理 {pack.Textures.Count}");
            core.PostWebMessageAsJson("""{"type":"voxelEnd"}""");

            // 抽样预览 → 问用户是否加载全量（全量可能卡顿；同一文件确认过就不再问）
            if (voxels.Truncated && !full && !_fullPreviewApproved.Contains(path))
            {
                LauncherLog.Write("lab", $"抽样预览：{Path.GetFileName(path)}（{blocks.Count} 块），询问是否全量");
                var total = _currentProjection is { } info &&
                            string.Equals(info.FilePath, path, StringComparison.OrdinalIgnoreCase)
                    ? info.TotalBlocks
                    : blocks.Count;
                await PromptFullPreviewAsync(path, blocks.Count, total);
            }
        }
        catch (Exception ex)
        {
            try
            {
                core.PostWebMessageAsJson(System.Text.Json.JsonSerializer.Serialize(new
                {
                    type = "notice",
                    text = Loc.T("预览失败：{0}", ex.Message),
                }));
            }
            catch { /* 网页端已销毁 */ }
        }
    }

    /// <summary>抽样预览后询问是否加载全量（全量可能卡顿）</summary>
    private async Task PromptFullPreviewAsync(string path, int sampled, int total)
    {
        try
        {
            var dialog = new ThemedDialog
            {
                Title = Loc.T("投影较大"),
                Content = Loc.T("当前为抽样预览（{0:N0} 个方块），完整投影有 {1:N0} 个方块。加载完整投影可能需要一些时间，转动视角也可能卡顿。",
                    sampled, total),
                PrimaryButtonText = Loc.T("加载完整投影"),
                CloseButtonText = Loc.T("保持抽样预览"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            _fullPreviewApproved.Add(path);
            await ShowVoxelsAsync(path, full: true);
        }
        catch (Exception ex)
        {
            // 页面已离开 / 已有其他弹层：保持抽样即可
            LauncherLog.Write("lab", $"全量询问失败：{ex.Message}");
        }
    }

    // ── 投影工坊 ──

    private void RefreshProjectionList() => _ = RefreshProjectionListAsync();

    private async Task RefreshProjectionListAsync()
    {
        try
        {
            var dirs = _s.Projections.Directories();
            ProjectionDirText.Text = dirs[0];
            // 列目录要解析每个文件的元数据：超大投影几百毫秒，别压在 UI 线程上
            var files = await Task.Run(() => _s.Projections.List());
            ProjectionList.ItemsSource = files.Select(f => new ProjectionRow(f)).ToList();
            ProjectionEmpty.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            OpenProjectionDirBtn.IsEnabled = Directory.Exists(dirs[0]);
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("读取投影目录失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
    }

    private void RefreshProjections_Click(object s, RoutedEventArgs e) => RefreshProjectionList();

    private void OpenProjectionDir_Click(object s, RoutedEventArgs e)
    {
        var dir = _s.Projections.Directories()[0];
        try
        {
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    private async void OpenProjection_Click(object s, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        foreach (var ext in new[] { ".litematic", ".nbt", ".schematic", ".schem" })
            picker.FileTypeFilter.Add(ext);
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;
        _ = LoadProjectionAsync(file.Path);
    }

    private void ProjectionList_SelectionChanged(object s, SelectionChangedEventArgs e)
    {
        if (ProjectionList.SelectedItem is ProjectionRow row) _ = LoadProjectionAsync(row.Path);
    }

    /// <summary>读元数据要扫位数组（超大投影几百毫秒），放后台线程，别卡住选中那一下</summary>
    private async Task LoadProjectionAsync(string path)
    {
        try
        {
            var info = await Task.Run(() => _s.Projections.Read(path));
            _currentProjection = info;

            ProjectionDetailCard.Visibility = Visibility.Visible;
            ProjectionTitle.Text = info.Name;
            ProjNameBox.Text = info.Name;
            ProjAuthorBox.Text = info.Author;
            ProjDescBox.Text = info.Description;
            ProjectionMeta.Text = Loc.T("{0} · 尺寸 {1}×{2}×{3} · 区域 {4} · 非空气方块 {5:N0} · 调色板 {6} 项 · {7} · 修改于 {8:yyyy-MM-dd HH:mm}",
                info.FormatName, info.Size.X, info.Size.Y, info.Size.Z, info.RegionCount,
                info.TotalBlocks, info.PaletteSize,
                info.FileSizeBytes >= 1024 * 1024 ? $"{info.FileSizeBytes / 1048576.0:F1} MB" : $"{Math.Max(1, info.FileSizeBytes / 1024)} KB",
                info.ModifiedAt);

            var editable = info.Format == ProjectionFormat.Litematic;
            ProjNameBox.IsEnabled = editable;
            ProjAuthorBox.IsEnabled = editable;
            ProjDescBox.IsEnabled = editable;
            SaveMetaBtn.IsEnabled = editable;
            ConvertNbtBtn.IsEnabled = editable;
            ConvertLitematicBtn.IsEnabled = info.Format == ProjectionFormat.StructureNbt;

            var total = Math.Max(1, info.TotalBlocks);
            BlockStatList.ItemsSource = info.TopBlocks.Select(b => Loc.T("{0}　{1:N0}（{2:P1}）",
                b.BlockId, b.Count, b.Count / (double)total)).ToList();
            BlockStatList.Visibility = info.TopBlocks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            // 3D 预览：把体素推给预览页（未初始化时记下路径，初始化完补推）
            _ = ShowVoxelsAsync(path);
        }
        catch (Exception ex)
        {
            _currentProjection = null;
            ProjectionDetailCard.Visibility = Visibility.Collapsed;
            _previewPendingPath = null;
            try { PreviewWeb.CoreWebView2?.PostWebMessageAsJson("""{"type":"clear"}"""); }
            catch { /* 预览页未就绪：无需清理 */ }
            ErrorBar.Message = Loc.T("解析失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
    }

    private void SaveProjectionMeta_Click(object s, RoutedEventArgs e)
    {
        if (_currentProjection is not { } info) return;
        try
        {
            _s.Projections.UpdateMetadata(info.FilePath, ProjNameBox.Text.Trim(),
                ProjAuthorBox.Text.Trim(), ProjDescBox.Text.Trim());
            OkBar.Message = Loc.T("元数据已写入 {0}", Path.GetFileName(info.FilePath));
            OkBar.IsOpen = true;
            _ = LoadProjectionAsync(info.FilePath);
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("保存失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
    }

    private async void ConvertToNbt_Click(object s, RoutedEventArgs e) =>
        await ConvertAsync(toLitematic: false);

    private async void ConvertToLitematic_Click(object s, RoutedEventArgs e) =>
        await ConvertAsync(toLitematic: true);

    private async Task ConvertAsync(bool toLitematic)
    {
        if (_currentProjection is not { } info) return;
        var picker = new FileSavePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        picker.SuggestedFileName = info.Name;
        picker.DefaultFileExtension = toLitematic ? ".litematic" : ".nbt";
        picker.FileTypeChoices.Add(toLitematic ? Loc.T("Litematica 投影") : Loc.T("结构文件"),
            new List<string> { toLitematic ? ".litematic" : ".nbt" });
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        try
        {
            await Task.Run(() =>
            {
                if (toLitematic) _s.Projections.ConvertToLitematic(info.FilePath, file.Path);
                else _s.Projections.ConvertToStructureNbt(info.FilePath, file.Path);
            });
            OkBar.Message = Loc.T("已转换并保存：{0}", file.Path);
            OkBar.IsOpen = true;
            NotificationCenter.Instance.Info(Loc.T("投影已转换：{0}", Path.GetFileName(file.Path)));
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("转换失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
    }

    private void RevealProjection_Click(object s, RoutedEventArgs e)
    {
        if (_currentProjection is not { } info) return;
        try
        {
            System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{info.FilePath}\"");
        }
        catch (Exception ex)
        {
            ErrorBar.Message = ex.Message;
            ErrorBar.IsOpen = true;
        }
    }

    /// <summary>投影列表行（ToString 直接当行文案）</summary>
    private sealed record ProjectionRow(ProjectionFile File)
    {
        public string Path => File.Path;
        public override string ToString() => $"{File.Name}　·　{File.SizeText}　·　{File.ModifiedAt:MM-dd HH:mm}";
    }

    // ── 投影社区（多源聚合：GitHub 经镜像加速 + 卡片列表 + 一键下载到投影目录） ──

    private bool _communityBusy;
    private readonly Dictionary<string, CommunityCardRefs> _communityCards = new(StringComparer.OrdinalIgnoreCase);
    private List<CommunityProjection> _communityItems = [];

    /// <summary>卡片上的可更新部件（探测到文件后就地更新，不重建整卡）</summary>
    private sealed class CommunityCardRefs
    {
        public required Border Root { get; init; }
        public required TextBlock FileLine { get; init; }
        public required GlowButton Download { get; init; }
        public required CommunityProjection Item { get; init; }
        public List<CommunityFile>? Files { get; set; }
        /// <summary>已确认 Release 里没有投影文件（工具/模组仓库）——只有这种才参与"过滤"</summary>
        public bool ConfirmedEmpty { get; set; }
    }

    private void CommunitySearch_Click(object s, RoutedEventArgs e) =>
        _ = SearchCommunityAsync(forceRefresh: true);

    private void CommunityRefresh_Click(object s, RoutedEventArgs e) =>
        _ = SearchCommunityAsync(forceRefresh: true);

    private void CommunitySearch_Submitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args) =>
        _ = SearchCommunityAsync(forceRefresh: true);

    private async Task SearchCommunityAsync(bool forceRefresh)
    {
        if (_communityBusy) return;
        _communityBusy = true;
        CommunityLoading.IsActive = true;
        CommunityStatus.Text = Loc.T("正在从多个源获取…");
        try
        {
            var rawQuery = CommunitySearch.Text ?? "";
            // 中文关键词先转英文再检索（源生态是英文），三级兜底：
            //   ① 内置游戏术语词典（「世吞」→ world eater；通用翻译会把它音译成人名，实测踩过）
            //   ② 通用翻译（词典/MC 百科/机翻）
            //   ③ 原词
            var effective = rawQuery;
            if (rawQuery.Trim().Length > 0)
            {
                if (CommunityGlossary.ToSearchTerm(rawQuery) is { } glossaryHit)
                {
                    effective = glossaryHit;
                }
                else
                {
                    try
                    {
                        var translatedQuery = await _s.SearchTranslation.TranslateAsync(rawQuery);
                        if (translatedQuery.Translated) effective = translatedQuery.Effective;
                    }
                    catch
                    {
                        // 翻译失败：用原词直接搜
                    }
                }
            }

            var (items, failed) = await _s.ProjectionCommunity.SearchAsync(effective, forceRefresh);
            _communityItems = items;

            CommunityGrid.Items.Clear();
            _communityCards.Clear();
            foreach (var item in items)
                CommunityGrid.Items.Add(BuildCommunityCard(item));

            var queryNote = effective != rawQuery ? Loc.T("（已按「{0}」检索）", effective) : "";
            CommunityStatus.Text = (items.Count == 0
                ? Loc.T("没有找到投影（换个关键词试试，例如 世吞 / world eater / farm）")
                : failed.Count > 0
                    ? Loc.T("共 {0} 个候选 · {1} 源暂不可用", items.Count, string.Join("/", failed))
                    : Loc.T("共 {0} 个候选", items.Count)) + queryNote;
            CommunityBaseLabel.Text = Loc.T("当前 GitHub 通道：{0}", _s.ProjectionCommunity.GitHub.CurrentBaseLabel);

            // 探测 Release：只显示含投影文件的仓库（默认开）——图省额度，最多探 24 个且带缓存
            await ProbeAndFilterAsync(queryNote);
        }
        catch (Exception ex)
        {
            CommunityStatus.Text = Loc.T("获取失败：{0}", ex.Message);
        }
        finally
        {
            _communityBusy = false;
            CommunityLoading.IsActive = false;
        }
    }

    /// <summary>
    /// Release 探测 → 有投影文件的仓库置顶并显示文件名与大小；无文件的按开关过滤掉。
    /// 每次探测最多 24 个仓库、并发 4、结果缓存 1 小时（匿名 API 每小时只有 60 次额度）。
    /// </summary>
    private async Task ProbeAndFilterAsync(string queryNote)
    {
        if (_communityItems.Count == 0) return;
        var (probed, failed) = await _s.ProjectionCommunity.ProbeFilesAsync(
            _communityItems, maxProbe: 12, concurrency: 4,
            progress: (done, count) => DispatcherQueue.TryEnqueue(() =>
            {
                CommunityStatus.Text = Loc.T("正在探测投影文件…（{0}/{1}）", done, count) + queryNote;
            }));

        var filterOn = CommunityFilterCheck.IsChecked == true;
        var withFiles = new List<CommunityProjection>();
        var unprobed = new List<CommunityProjection>();      // 探测失败或超出本轮上限：保留显示，绝不当成"无文件"
        var confirmedEmpty = new List<CommunityProjection>(); // 确认过 Release 里没有投影文件（工具/模组仓库）
        foreach (var item in _communityItems)
        {
            var has = probed.TryGetValue(item.FullName, out var files) && files.Count > 0;
            var isFailed = failed.Contains(item.FullName);
            var wasProbed = probed.ContainsKey(item.FullName);

            if (_communityCards.TryGetValue(item.FullName, out var refs))
            {
                refs.Files = has ? files : null;
                refs.ConfirmedEmpty = wasProbed && !isFailed && !has;
                refs.FileLine.Text = has
                    ? Loc.T("📦 {0}{1}", files![0].Name,
                        files.Count > 1 ? Loc.T(" 等 {0} 个文件", files.Count) : $"（{files[0].SizeText}）")
                    : isFailed
                        ? Loc.T("未能探测（GitHub 额度或网络受限，可稍后重试）")
                        : wasProbed
                            ? Loc.T("该仓库 Release 里没有投影文件")
                            : Loc.T("未探测（本轮只探测前 12 个候选）");
            }

            if (has) withFiles.Add(item);
            else if (isFailed || !wasProbed) unprobed.Add(item);
            else confirmedEmpty.Add(item);
        }

        // 排序：有投影文件的置顶；未探测的保留（不过滤）；确认无文件的按开关显示/隐藏
        CommunityGrid.Items.Clear();
        foreach (var item in withFiles.Concat(unprobed).Concat(filterOn ? [] : confirmedEmpty))
            if (_communityCards.TryGetValue(item.FullName, out var refs))
                CommunityGrid.Items.Add(refs.Root);

        var failedNote = failed.Count > 0 ? Loc.T(" · {0} 个未能探测", failed.Count) : "";
        CommunityStatus.Text = filterOn
            ? Loc.T("共 {0} 个含投影文件的资源（已滤掉 {1} 个无投影文件的仓库{2}）",
                withFiles.Count, confirmedEmpty.Count, failedNote) + queryNote
            : Loc.T("共 {0} 个候选 · {1} 个含投影文件（未过滤{2}）",
                _communityItems.Count, withFiles.Count, failedNote) + queryNote;
    }

    /// <summary>过滤开关切换：就地重排网格（探测结果已缓存，不重新联网）</summary>
    private void CommunityFilter_Changed(object s, RoutedEventArgs e)
    {
        if (_communityCards.Count == 0) return;
        var filterOn = CommunityFilterCheck.IsChecked == true;
        var withFiles = new List<CommunityProjection>();
        var unprobed = new List<CommunityProjection>();
        var confirmedEmpty = new List<CommunityProjection>();
        foreach (var item in _communityItems)
        {
            if (!_communityCards.TryGetValue(item.FullName, out var r)) continue;
            if (r.Files is { Count: > 0 }) withFiles.Add(item);
            else if (r.ConfirmedEmpty) confirmedEmpty.Add(item);
            else unprobed.Add(item); // 未探测 / 探测失败：不参与过滤
        }
        CommunityGrid.Items.Clear();
        foreach (var item in withFiles.Concat(unprobed).Concat(filterOn ? [] : confirmedEmpty))
            if (_communityCards.TryGetValue(item.FullName, out var refs))
                CommunityGrid.Items.Add(refs.Root);
        CommunityStatus.Text = filterOn
            ? Loc.T("共 {0} 个含投影文件的资源（已滤掉 {1} 个无投影文件的仓库）", withFiles.Count, confirmedEmpty.Count)
            : Loc.T("共 {0} 个候选 · {1} 个含投影文件（未过滤）", _communityItems.Count, withFiles.Count);
    }

    /// <summary>源设置保存并强制刷新（改基址/加自定义源后立即生效）</summary>
    private void CommunitySaveSources_Click(object s, RoutedEventArgs e)
    {
        var st = _s.Settings;
        st.ProjectionGithubApi = CommunityApiBaseBox.Text.Trim();
        st.ProjectionCustomSources = CommunityCustomSourcesBox.Text.Trim();
        st.ProjectionGithubToken = CommunityTokenBox.Password.Trim();
        try { st.Save(); } catch { /* 落盘失败不阻断 */ }
        OkBar.Message = Loc.T("投影源已保存，正在按新配置刷新…");
        OkBar.IsOpen = true;
        _ = SearchCommunityAsync(forceRefresh: true);
    }

    /// <summary>
    /// 社区卡片（代码构建，无 DataTemplate/绑定坑）：<b>上半图片、下半文字</b>——
    /// 封面用仓库社交图（经当前 GitHub 通道加载，失败自动收起图区）；描述先英文上屏、
    /// 译文回来后就地替换（项目既有模式）。
    /// </summary>
    private Border BuildCommunityCard(CommunityProjection item)
    {
        var title = new TextBlock
        {
            Text = item.Name,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            FontSize = 14,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxWidth = 210,
        };
        var sourceChip = new Border
        {
            Background = (Brush)Application.Current.Resources["SubtleFillColorTertiaryBrush"],
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 1, 6, 1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new TextBlock
            {
                Text = item.Source,
                FontSize = 11,
                Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
            },
        };
        var head = new Grid();
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(title, 0);
        Grid.SetColumn(sourceChip, 1);
        head.Children.Add(title);
        head.Children.Add(sourceChip);

        var meta = new TextBlock
        {
            Text = Loc.T("{0} · ⭐ {1} · {2:MM-dd} 更新", item.Author, item.Stars, item.UpdatedAt),
            FontSize = 11,
            Margin = new Thickness(0, 4, 0, 0),
            Foreground = ThemedBrush("TextFillColorTertiaryBrush"),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        var desc = new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(item.Description) ? Loc.T("（无描述）") : item.Description,
            FontSize = 12,
            Margin = new Thickness(0, 6, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
        };

        // 探测结果行：默认"探测中"，检测到投影文件后替换为文件名与大小
        var fileLine = new TextBlock
        {
            Text = Loc.T("探测投影文件…"),
            FontSize = 11,
            Margin = new Thickness(0, 6, 0, 0),
            TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
        };

        var download = new GlowButton
        {
            Content = Loc.T("下载投影"),
            FontSize = 12,
            Padding = new Thickness(10, 4, 10, 4),
            Margin = new Thickness(0, 8, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        download.Click += async (_, _) => await DownloadCommunityAsync(item, download);

        var body = new StackPanel();
        body.Children.Add(head);
        body.Children.Add(meta);
        body.Children.Add(desc);
        body.Children.Add(fileLine);
        body.Children.Add(download);

        // 上半图片：统一高 120、圆角上沿；加载失败（无社交图/网络）就整块收起，退回纯文字卡片
        var image = new Image
        {
            Height = 120,
            Stretch = Stretch.UniformToFill,
        };
        var imageHost = new Border
        {
            Height = 120,
            CornerRadius = new CornerRadius(12, 12, 0, 0),
            Background = (Brush)Application.Current.Resources["LayerFillColorDefaultBrush"],
            Child = image,
            Visibility = string.IsNullOrEmpty(item.ImageUrl) ? Visibility.Collapsed : Visibility.Visible,
        };
        if (!string.IsNullOrEmpty(item.ImageUrl))
        {
            try { image.Source = new BitmapImage(new Uri(item.ImageUrl)); }
            catch { imageHost.Visibility = Visibility.Collapsed; }
            image.ImageFailed += (_, _) => imageHost.Visibility = Visibility.Collapsed;
        }

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var textHost = new Border { Padding = new Thickness(14, 10, 14, 12), Child = body };
        Grid.SetRow(imageHost, 0);
        Grid.SetRow(textHost, 1);
        root.Children.Add(imageHost);
        root.Children.Add(textHost);

        // 描述中文化：先原文上屏，译文回来替换（失败保留原文）
        if (!string.IsNullOrEmpty(item.Description) &&
            !SearchTranslationService.ContainsCjk(item.Description))
        {
            _ = TranslateCardDescriptionAsync(desc, item.Description);
        }

        var card = new Border
        {
            Width = 300,
            CornerRadius = new CornerRadius(12),
            Background = (Brush)Application.Current.Resources["GlassCardBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            Child = root,
        };
        _communityCards[item.FullName] = new CommunityCardRefs
        {
            Root = card,
            FileLine = fileLine,
            Download = download,
            Item = item,
        };
        return card;
    }

    /// <summary>卡片描述译文回填（译文 = 中文文本；失败静默保留英文）</summary>
    private async Task TranslateCardDescriptionAsync(TextBlock desc, string original)
    {
        var translated = await _s.CommunityTranslation.TranslateToChineseAsync(original);
        if (!string.IsNullOrEmpty(translated)) desc.Text = translated!;
    }

    /// <summary>下载社区投影：列 Release 里的投影文件 → 单文件直下 / 多文件弹选择 → 落投影目录</summary>
    private async Task DownloadCommunityAsync(CommunityProjection item, GlowButton button)
    {
        button.IsEnabled = false;
        var original = button.Content;
        button.Content = Loc.T("读取文件…");
        try
        {
            // 已经探测过就直接用（探测结果 1 小时缓存）：没探测过或来自自定义源时才现拉
            var files = _communityCards.TryGetValue(item.FullName, out var cached) && cached.Files is { Count: > 0 }
                ? cached.Files
                : await _s.ProjectionCommunity.ListFilesAsync(item);
            if (files.Count == 0)
            {
                ErrorBar.Message = Loc.T("「{0}」的 Release 里没有投影文件（.litematic / .nbt 等）", item.Name);
                ErrorBar.IsOpen = true;
                return;
            }

            CommunityFile file;
            if (files.Count == 1)
            {
                file = files[0];
            }
            else
            {
                var list = new ListView { SelectionMode = ListViewSelectionMode.Single, MaxHeight = 200 };
                foreach (var f in files)
                    list.Items.Add(new ListViewItem
                    {
                        Content = $"{f.Name}（{f.SizeText}）",
                        Tag = f,
                        Padding = new Thickness(8, 6, 8, 6),
                    });
                list.SelectedIndex = 0;
                var dlg = new ThemedDialog
                {
                    Title = Loc.T("选择要下载的投影文件"),
                    Content = list,
                    PrimaryButtonText = Loc.T("下载"),
                    CloseButtonText = Loc.T("取消"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = Content.XamlRoot,
                };
                if (await dlg.ShowAsync() != ContentDialogResult.Primary ||
                    list.SelectedItem is not ListViewItem chosen || chosen.Tag is not CommunityFile picked)
                    return;
                file = picked;
            }

            button.Content = Loc.T("下载中…");
            var dir = _s.Projections.Directories()[0];
            var dest = await _s.ProjectionCommunity.DownloadAsync(file, dir);
            OkBar.Message = Loc.T("已下载 {0} —— 可在「投影工坊」里预览与编辑", Path.GetFileName(dest));
            OkBar.IsOpen = true;
            NotificationCenter.Instance.Info(Loc.T("投影已下载：{0}", Path.GetFileName(dest)));
            RefreshProjectionList();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("下载失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
        finally
        {
            button.Content = original;
            button.IsEnabled = true;
        }
    }

    // ── 彩色文字（Markdown 手感的编辑界面，格式只保留颜色） ──

    // ── 彩色文字：Word 式富文本编辑（选中一段再上色，点色块立即生效），输出 § 代码 ──

    private bool _richTextInit;
    private int _selStart;
    private int _selEnd;

    private static string GetDocumentText(Microsoft.UI.Text.RichEditTextDocument doc)
    {
        doc.GetText(Microsoft.UI.Text.TextGetOptions.None, out var text);
        return text ?? "";
    }

    /// <summary>首次进入时放一段示例并上渐变，之后不再覆盖用户内容</summary>
    private void InitRichEditor()
    {
        if (_richTextInit || RichEditor?.Document is not { } doc) return;
        _richTextInit = true;
        BuildEditorContextMenu();
        try
        {
            doc.SetText(Microsoft.UI.Text.TextSetOptions.None, Loc.T("欢迎来到 Starcloud 服务器"));
            var length = GetDocumentText(doc).Length;
            if (length > 1) ApplyGradient(doc, 0, length - 1, "#55FFFF", "#FF55FF");
        }
        catch
        {
            // 富文本初始化失败不影响手动输入
        }
        UpdateTextOutput();
    }

    private bool _editorMenuBuilt;

    /// <summary>右键菜单：字形 + 文字颜色 + 渐变/彩虹 + 清除 + 剪切/复制/粘贴/撤销/全选</summary>
    private void BuildEditorContextMenu()
    {
        if (_editorMenuBuilt || RichEditor is null) return;
        _editorMenuBuilt = true;

        var menu = new MenuFlyout();

        void AddItem(string text, RoutedEventHandler click)
        {
            var item = new MenuFlyoutItem { Text = text };
            item.Click += click;
            menu.Items.Add(item);
        }

        AddItem(Loc.T("粗体"), ToggleBold_Click);
        AddItem(Loc.T("斜体"), ToggleItalic_Click);
        AddItem(Loc.T("下划线"), ToggleUnderline_Click);
        AddItem(Loc.T("删除线"), ToggleStrike_Click);

        var colors = new MenuFlyoutSubItem { Text = Loc.T("文字颜色") };
        foreach (var hex in new[]
                 {
                     "#FF5555", "#FFAA00", "#FFFF55", "#55FF55", "#55FFFF",
                     "#5555FF", "#FF55FF", "#FFFFFF", "#AAAAAA", "#333333",
                 })
        {
            var (r, g, b) = McText.ParseHex(hex);
            var item = new MenuFlyoutItem
            {
                Text = hex,
                Tag = hex,
                Icon = new FontIcon
                {
                    Glyph = "",
                    Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, r, g, b)),
                },
            };
            item.Click += PaletteColor_Click;
            colors.Items.Add(item);
        }
        menu.Items.Add(colors);

        var gradient = new MenuFlyoutSubItem { Text = Loc.T("渐变") };
        var applyGradient = new MenuFlyoutItem { Text = Loc.T("应用渐变（当前起止色）") };
        applyGradient.Click += ApplyGradient_Click;
        gradient.Items.Add(applyGradient);
        var applyRainbow = new MenuFlyoutItem { Text = Loc.T("应用彩虹") };
        applyRainbow.Click += ApplyRainbow_Click;
        gradient.Items.Add(applyRainbow);
        menu.Items.Add(gradient);

        AddItem(Loc.T("清除格式"), ClearFormat_Click);
        menu.Items.Add(new MenuFlyoutSeparator());

        var cut = new MenuFlyoutItem { Text = Loc.T("剪切"), KeyboardAcceleratorTextOverride = "Ctrl+X" };
        cut.Click += (_, _) => EditorCommand(d => d.Selection.Cut());
        menu.Items.Add(cut);

        var copy = new MenuFlyoutItem { Text = Loc.T("复制"), KeyboardAcceleratorTextOverride = "Ctrl+C" };
        copy.Click += (_, _) => EditorCommand(d => d.Selection.Copy());
        menu.Items.Add(copy);

        var paste = new MenuFlyoutItem { Text = Loc.T("粘贴"), KeyboardAcceleratorTextOverride = "Ctrl+V" };
        paste.Click += (_, _) => EditorCommand(d => d.Selection.Paste(0));
        menu.Items.Add(paste);

        var undo = new MenuFlyoutItem { Text = Loc.T("撤销"), KeyboardAcceleratorTextOverride = "Ctrl+Z" };
        undo.Click += (_, _) => EditorCommand(d => d.Undo());
        menu.Items.Add(undo);

        menu.Items.Add(new MenuFlyoutSeparator());

        var selectAll = new MenuFlyoutItem { Text = Loc.T("全选"), KeyboardAcceleratorTextOverride = "Ctrl+A" };
        selectAll.Click += (_, _) =>
        {
            if (RichEditor?.Document is not { } doc) return;
            var length = GetDocumentText(doc).Length;
            doc.Selection.SetRange(0, Math.Max(0, length - 1));
            UpdateTextOutput();
        };
        menu.Items.Add(selectAll);

        RichEditor.ContextFlyout = menu;
    }

    private void EditorCommand(Action<Microsoft.UI.Text.RichEditTextDocument> action)
    {
        if (RichEditor?.Document is not { } doc) return;
        action(doc);
        UpdateTextOutput();
    }

    private void RichEditor_TextChanged(object s, RoutedEventArgs e) => UpdateTextOutput();

    private void RichEditor_SelectionChanged(object s, RoutedEventArgs e)
    {
        if (RichEditor?.Document is not { } doc) return;
        _selStart = doc.Selection.StartPosition;
        _selEnd = doc.Selection.EndPosition;
    }

    /// <summary>当前选区（点工具栏按钮会抢焦点，所以用最后一次记录的范围）</summary>
    private (int Start, int End) SelectedRange(Microsoft.UI.Text.RichEditTextDocument doc)
    {
        // 优先读文档当前选区（右键/拖选后是最新的）；点工具栏丢了选区时退回最后记录的范围
        var selection = doc.Selection;
        if (selection.EndPosition > selection.StartPosition)
            return (selection.StartPosition, selection.EndPosition);
        return (Math.Max(0, _selStart), Math.Max(_selStart, _selEnd));
    }

    private void PaletteColor_Click(object s, RoutedEventArgs e)
    {
        if (s is not FrameworkElement { Tag: string hex } || RichEditor?.Document is not { } doc) return;
        var (start, end) = SelectedRange(doc);
        if (end <= start)
        {
            TextHint.Text = Loc.T("先选中一段文字（Ctrl+A 全选），再点色块。");
            return;
        }
        ApplySolid(doc, start, end, hex);
        UpdateTextOutput();
        SetRangeHint(start, end);
    }

    private void ApplyGradient_Click(object s, RoutedEventArgs e)
    {
        if (RichEditor?.Document is not { } doc) return;
        var (start, end) = SelectedRange(doc);
        if (end <= start)
        {
            TextHint.Text = Loc.T("先选中一段文字（Ctrl+A 全选），再点「应用渐变」。");
            return;
        }
        ApplyGradient(doc, start, end, PaintFromBox.Text, PaintToBox.Text);
        UpdateTextOutput();
        SetRangeHint(start, end);
    }

    private void ApplyRainbow_Click(object s, RoutedEventArgs e)
    {
        if (RichEditor?.Document is not { } doc) return;
        var (start, end) = SelectedRange(doc);
        if (end <= start)
        {
            TextHint.Text = Loc.T("先选中一段文字（Ctrl+A 全选），再点「应用彩虹」。");
            return;
        }
        ApplyRainbow(doc, start, end);
        UpdateTextOutput();
        SetRangeHint(start, end);
    }

    private void SetRangeHint(int start, int end) =>
        TextHint.Text = Loc.T("已应用到选中的 {0} 个字符", end - start);

    private static void ApplySolid(Microsoft.UI.Text.RichEditTextDocument doc, int start, int end, string hex)
    {
        doc.GetRange(start, end).CharacterFormat.ForegroundColor = HexToColor(hex);
    }

    private static void ApplyGradient(Microsoft.UI.Text.RichEditTextDocument doc, int start, int end, string fromHex, string toHex)
    {
        var (r1, g1, b1) = McText.ParseHex(fromHex);
        var (r2, g2, b2) = McText.ParseHex(toHex);
        var count = end - start;
        for (var i = 0; i < count; i++)
        {
            var t = count == 1 ? 0 : (double)i / (count - 1);
            var color = Windows.UI.Color.FromArgb(255,
                (byte)Math.Round(r1 + (r2 - r1) * t),
                (byte)Math.Round(g1 + (g2 - g1) * t),
                (byte)Math.Round(b1 + (b2 - b1) * t));
            doc.GetRange(start + i, start + i + 1).CharacterFormat.ForegroundColor = color;
        }
    }

    private static void ApplyRainbow(Microsoft.UI.Text.RichEditTextDocument doc, int start, int end)
    {
        var count = end - start;
        for (var i = 0; i < count; i++)
        {
            var color = HsvToColor(360.0 * i / Math.Max(1, count));
            doc.GetRange(start + i, start + i + 1).CharacterFormat.ForegroundColor = color;
        }
    }

    private void ToggleBold_Click(object s, RoutedEventArgs e)
    {
        if (RichEditor?.Document is not { } doc) return;
        var (start, end) = SelectedRange(doc);
        if (end <= start) return;
        var format = doc.GetRange(start, end).CharacterFormat;
        format.Bold = format.Bold == Microsoft.UI.Text.FormatEffect.On
            ? Microsoft.UI.Text.FormatEffect.Off
            : Microsoft.UI.Text.FormatEffect.On;
        UpdateTextOutput();
    }

    private void ToggleItalic_Click(object s, RoutedEventArgs e)
    {
        if (RichEditor?.Document is not { } doc) return;
        var (start, end) = SelectedRange(doc);
        if (end <= start) return;
        var format = doc.GetRange(start, end).CharacterFormat;
        format.Italic = format.Italic == Microsoft.UI.Text.FormatEffect.On
            ? Microsoft.UI.Text.FormatEffect.Off
            : Microsoft.UI.Text.FormatEffect.On;
        UpdateTextOutput();
    }

    private void ToggleUnderline_Click(object s, RoutedEventArgs e)
    {
        if (RichEditor?.Document is not { } doc) return;
        var (start, end) = SelectedRange(doc);
        if (end <= start) return;
        var format = doc.GetRange(start, end).CharacterFormat;
        format.Underline = format.Underline == Microsoft.UI.Text.UnderlineType.None
            ? Microsoft.UI.Text.UnderlineType.Single
            : Microsoft.UI.Text.UnderlineType.None;
        UpdateTextOutput();
    }

    private void ToggleStrike_Click(object s, RoutedEventArgs e)
    {
        if (RichEditor?.Document is not { } doc) return;
        var (start, end) = SelectedRange(doc);
        if (end <= start) return;
        var format = doc.GetRange(start, end).CharacterFormat;
        format.Strikethrough = format.Strikethrough == Microsoft.UI.Text.FormatEffect.On
            ? Microsoft.UI.Text.FormatEffect.Off
            : Microsoft.UI.Text.FormatEffect.On;
        UpdateTextOutput();
    }

    private void ClearFormat_Click(object s, RoutedEventArgs e)
    {
        if (RichEditor?.Document is not { } doc) return;
        var (start, end) = SelectedRange(doc);
        if (end <= start) return;
        var format = doc.GetRange(start, end).CharacterFormat;
        format.ForegroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        format.Bold = Microsoft.UI.Text.FormatEffect.Off;
        format.Italic = Microsoft.UI.Text.FormatEffect.Off;
        format.Underline = Microsoft.UI.Text.UnderlineType.None;
        format.Strikethrough = Microsoft.UI.Text.FormatEffect.Off;
        UpdateTextOutput();
    }

    private void PaintColor_Changed(object s, RoutedEventArgs e) => UpdateTextOutput();

    private void PaintColor_Changed(object s, TextChangedEventArgs e) => UpdateTextOutput();

    private void PaintColor_Changed(object s, SelectionChangedEventArgs e) => UpdateTextOutput();

    /// <summary>把富文本逐字读成 § 代码（颜色/粗斜体/下划线/删除线都按选区还原）</summary>
    private void UpdateTextOutput()
    {
        if (TextOutput is null || RichEditor?.Document is not { } doc) return;
        var text = GetDocumentText(doc);
        var legacy = (TextPrecisionCombo?.SelectedItem as ComboBoxItem)?.Tag as string == "legacy";
        // 编辑框里没上色的字显示成主题文字色（浅色主题是黑），游戏里默认是白：这里统一映射
        var themeDefault = (RichEditor.Foreground as SolidColorBrush)?.Color;
        var white = Windows.UI.Color.FromArgb(255, 255, 255, 255);
        var sb = new System.Text.StringBuilder();
        string? lastStyle = null;
        Windows.UI.Color? lastColor = null;

        for (var i = 0; i < text.Length; i++)
        {
            var ch = text[i];
            if (ch == '\r') ch = '\n';

            var format = doc.GetRange(i, i + 1).CharacterFormat;
            var color = format.ForegroundColor;
            if (color.A == 0) color = white;
            if (themeDefault is { } dc && color.Equals(dc)) color = white;
            var bold = format.Bold == Microsoft.UI.Text.FormatEffect.On;
            var italic = format.Italic == Microsoft.UI.Text.FormatEffect.On;
            var underline = format.Underline != Microsoft.UI.Text.UnderlineType.None;
            var strike = format.Strikethrough == Microsoft.UI.Text.FormatEffect.On;
            var styleKey = $"{(bold ? 1 : 0)}{(italic ? 1 : 0)}{(underline ? 1 : 0)}{(strike ? 1 : 0)}";

            if (styleKey != lastStyle)
            {
                sb.Append(McText.Section).Append('r')
                    .Append(McText.StyleCodes(bold, italic, underline, strike))
                    .Append(ColorCode(color, legacy));
                lastStyle = styleKey;
                lastColor = color;
            }
            else if (lastColor is null || !lastColor.Value.Equals(color))
            {
                sb.Append(ColorCode(color, legacy));
                lastColor = color;
            }
            sb.Append(ch);
        }

        var output = sb.ToString().TrimEnd('\n');
        TextOutput.Text = output;
        var plain = McText.Strip(output);
        if (plain.Length == 0)
        {
            TextHint.Text = "";
        }
        else if (string.IsNullOrEmpty(TextHint.Text) || !TextHint.Text.StartsWith(Loc.T("已应用到")))
        {
            TextHint.Text = Loc.T("纯文本 {0} 字；带代码共 {1} 字", plain.Length, output.Length);
        }
    }

    private static string ColorCode(Windows.UI.Color color, bool legacy)
    {
        var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        return legacy
            ? $"{McText.Section}{McText.NearestLegacy(hex).Code}"
            : McText.HexPrefix(hex);
    }

    private static Windows.UI.Color HsvToColor(double hue)
    {
        var h = ((hue % 360) + 360) % 360;
        const double c = 1.0;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Windows.UI.Color.FromArgb(255,
            (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }

    private static Windows.UI.Color HexToColor(string hex)
    {
        var (r, g, b) = McText.ParseHex(hex);
        return Windows.UI.Color.FromArgb(255, r, g, b);
    }

    private void CopyText_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(TextOutput.Text)) return;
        var package = new DataPackage();
        package.SetText(TextOutput.Text);
        Clipboard.SetContent(package);
        TextHint.Text = Loc.T("已复制到剪贴板");
    }

    // ── 世吞生成器 ──

    private WorldEaterOptions ReadEaterOptions() => new(
        Length: (int)Math.Round(EaterLength.Value),
        Width: (int)Math.Round(EaterWidth.Value),
        Layers: (int)Math.Round(EaterLayers.Value),
        Spacing: (int)Math.Round(EaterSpacing.Value),
        LayerGap: (int)Math.Round(EaterLayerGap.Value),
        Offset: (int)Math.Round(EaterOffset.Value));

    private void EaterOptions_Changed(object s, NumberBoxValueChangedEventArgs e) => UpdateEaterEstimate();

    private void UpdateEaterEstimate()
    {
        if (EaterEstimate is null) return;
        var options = ReadEaterOptions();
        if (WorldEater.Validate(options) is { } error)
        {
            EaterEstimate.Text = Loc.T("参数有误：{0}", error);
            return;
        }
        EaterEstimate.Text = Loc.T("预计 TNT {0:N0} 个（体积 {1}×{2}×{3}）",
            WorldEater.EstimateTntCount(options),
            options.Length,
            options.Layers + options.LayerGap * (options.Layers - 1),
            options.Width + Math.Abs(options.Offset * (options.Layers - 1)));
    }

    private async void GenerateEater_Click(object s, RoutedEventArgs e)
    {
        var options = ReadEaterOptions();
        if (WorldEater.Validate(options) is { } error)
        {
            ErrorBar.Message = error;
            ErrorBar.IsOpen = true;
            return;
        }

        var dir = _s.Projections.Directories()[0];
        Directory.CreateDirectory(dir);
        var name = $"世吞阵列-{options.Length}x{options.Width}x{options.Layers}层-{DateTime.Now:yyyyMMdd-HHmmss}";
        var dest = Path.Combine(dir, name + ".litematic");
        try
        {
            await Task.Run(() => WorldEater.GenerateLitematic(options, dest));
            OkBar.Message = Loc.T("已生成 {0}（{1:N0} 个 TNT）", Path.GetFileName(dest), WorldEater.EstimateTntCount(options));
            OkBar.IsOpen = true;
            NotificationCenter.Instance.Info(Loc.T("世吞阵列已生成：{0}", Path.GetFileName(dest)));
            RefreshProjectionList();
            if (ToolList.SelectedItem is FrameworkElement { Tag: "eater" })
            {
                // 生成后切到投影工坊让用户直接看到
                foreach (var item in ToolList.Items.OfType<ListViewItem>())
                    if ((item.Tag as string) == "projection") { ToolList.SelectedItem = item; break; }
            }
        }
        catch (Exception ex)
        {
            ErrorBar.Message = Loc.T("生成失败：{0}", ex.Message);
            ErrorBar.IsOpen = true;
        }
    }

    // ── 指令助手 ──

    private void BuildCommandPickers()
    {
        CmdCategoryCombo.ItemsSource = CommandHelper.Categories.ToList();
        if (CmdCategoryCombo.Items.Count > 0) CmdCategoryCombo.SelectedIndex = 0;
    }

    private void CmdCategory_Changed(object s, SelectionChangedEventArgs e)
    {
        if (CmdTemplateCombo is null) return;
        var category = CmdCategoryCombo.SelectedItem as string;
        CmdTemplateCombo.ItemsSource = CommandHelper.Templates
            .Where(t => t.Category == category)
            .Select(t => t.Name)
            .ToList();
        if (CmdTemplateCombo.Items.Count > 0) CmdTemplateCombo.SelectedIndex = 0;
    }

    private void CmdTemplate_Changed(object s, SelectionChangedEventArgs e)
    {
        if (CmdParamPanel is null) return;
        var category = CmdCategoryCombo.SelectedItem as string;
        var name = CmdTemplateCombo.SelectedItem as string;
        _currentTemplate = CommandHelper.Templates
            .FirstOrDefault(t => t.Category == category && t.Name == name);
        _cmdParamBoxes.Clear();
        CmdParamPanel.Children.Clear();
        if (_currentTemplate is not { } template) return;

        foreach (var param in template.Params)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock
            {
                Text = param.Label,
                Width = 86,
                FontSize = 13,
                VerticalAlignment = VerticalAlignment.Center,
            });
            var box = new TextBox
            {
                Text = param.Default,
                Width = 320,
                FontSize = 13,
                PlaceholderText = param.Suggestions,
            };
            box.TextChanged += (_, _) => UpdateCommandOutput();
            _cmdParamBoxes[param.Key] = box;
            row.Children.Add(box);
            if (param.Suggestions.Length > 0)
            {
                row.Children.Add(new TextBlock
                {
                    Text = Loc.T("常用：{0}", param.Suggestions),
                    FontSize = 11,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = ThemedBrush("TextFillColorTertiaryBrush"),
                });
            }
            CmdParamPanel.Children.Add(row);
        }
        if (template.Note.Length > 0)
        {
            CmdParamPanel.Children.Add(new TextBlock
            {
                Text = template.Note,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemedBrush("TextFillColorSecondaryBrush"),
            });
        }
        UpdateCommandOutput();
    }

    private void UpdateCommandOutput()
    {
        if (CmdOutput is null || _currentTemplate is not { } template) return;
        var values = _cmdParamBoxes.ToDictionary(kv => kv.Key, kv => kv.Value.Text, StringComparer.Ordinal);
        var command = CommandHelper.Build(template, values);
        CmdOutput.Text = "/" + command;

        if (CommandHelper.HasUnfilled(command))
        {
            CmdHint.Text = Loc.T("还有没填的参数（显示为 {占位符}）");
            return;
        }
        CmdHint.Text = CommandHelper.Validate(command) is { } problem
            ? Loc.T("体检提示：{0}", problem)
            : Loc.T("看起来没问题");
    }

    private void CopyCommand_Click(object s, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(CmdOutput.Text)) return;
        var package = new DataPackage();
        package.SetText(CmdOutput.Text.TrimStart('/'));
        Clipboard.SetContent(package);
        CmdHint.Text = Loc.T("已复制（不含开头的 /）");
    }

    /// <summary>按当前主题查画刷：沿 ActualTheme 在合并字典的 ThemeDictionaries 中查找
    /// （直接索引 Application.Resources 可能解析到别的主题档，项目既有坑）。</summary>
    private Brush ThemedBrush(string key)
    {
        var want = ActualTheme.ToString();
        foreach (var d in Application.Current.Resources.MergedDictionaries)
            if (d.ThemeDictionaries.TryGetValue(want, out var v) &&
                v is ResourceDictionary rd && rd.TryGetValue(key, out var b) &&
                b is Brush brush)
                return brush;
        return Application.Current.Resources.TryGetValue(key, out var f) && f is Brush fb
            ? fb
            : new SolidColorBrush(Microsoft.UI.Colors.Gray);
    }
}
