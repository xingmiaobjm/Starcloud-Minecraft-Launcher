// 账户详情（二级页）：左侧「形象与皮肤 / 账户信息」分区，右侧对应内容。
//
// 皮肤区的版式照主流启动器的"皮肤选择器"来：
//   左：3D 形象（可拖拽转视角） + 当前皮肤信息 + 导入 / 应用 / 取消
//   右：「我的皮肤」卡片网格（首格是「添加皮肤」占位卡）
//       +「默认皮肤」卡片网格（启动器自带的预置皮肤，可折叠，点一下即加入衣柜并应用）
// 衣柜（皮肤库）在所有账户之间共用一份，每个账户只记自己选了哪张 ——
// 所以同一张皮肤在这里换个账户照样能选，而"当前账户在用哪张"各行其是。
// 卡片缩略图不用原始纹理，而是走 Core 的软件光栅化器渲染成小立绘（见 SkinThumbnails）。
//
// 皮肤库原先是个弹窗（SkinDialogs），能力已整块搬到本页：
//   · 库列表要滚动、3D 预览要拖拽、导入要选文件 —— 页面空间才够；
//   · 不再出现「弹窗里再弹窗」（ContentDialog 不许嵌套）；
//   · 应用 / 取消 / 删除皮肤都就地刷新。
// 删除账户这类不可逆操作仍然二次确认（确认框是必要的，不是弹窗式流程）。
//
// 移植自桌面 Starcloud.App.Views.AccountDetailPage.xaml.cs，适配点：
// - 导航：OnNavigatedTo 参数 → 静态 PendingUuid + IShellPage.OnShown（对齐 VersionsPage 范式）；
// - 整页拖放导入（Skin_DragOver / Skin_Drop）在触屏端无对应交互，已移除；
//   本地文件导入（FileOpenPicker）在安卓无可用封装，入口保留并提示改用「从链接导入」；
// - 预置皮肤资产：桌面按磁盘路径读 Assets/Skins/default/*.png，安卓改为经 AppAssets
//   读 APK 资产字节（入库走 ImportBytes，缩略图走 SkinThumbnails.FromBytes）；
// - 箭头旋转：桌面页面内私有实现 → Services/MotionFx.RotateChevron（同一套合成动画）；
// - 弹窗宿主 Content.XamlRoot → Services/Ui.Root；UUID 等宽字体 Consolas → monospace。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Rendering;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Behaviors;
using Starcloud.Launcher.Uno.Views.Dialogs;
using Starcloud.Launcher.Uno.Views.Rows;
using Starcloud.Launcher.Uno.Views.Shared;

namespace Starcloud.Launcher.Uno.Views;

public sealed partial class AccountDetailPage : Page, IShellPage
{
    private readonly AppServices _s = AppServices.Instance;
    private string _uuid = "";
    private string? _selectedSkinId;

    /// <summary>
    /// 账户列表页进详情页时交接的账户标识（对齐 VersionDetailPage.PendingVersionId 的范式）。
    /// </summary>
    internal static string? PendingUuid;

    /// <summary>当前网格里的皮肤卡（选中描边要逐张改，不能重建整个列表——重建会让卡片闪一下）</summary>
    private List<SkinCardRow> _savedRows = [];

    /// <summary>「默认皮肤」是否展开（标题行点击切换）</summary>
    private bool _presetsExpanded = true;

    /// <summary>代码回填型号下拉时抑制 SelectionChanged：否则回填本身会被当成"用户改了型号"，
    /// 白白写一次库、还把皮肤网格重建一遍（网格重建会让卡片闪一下）。</summary>
    private bool _fillingModel;

    public AccountDetailPage()
    {
        InitializeComponent();
        // 型号选项用代码建：三档固定值，放 XAML 里反而要写三遍一模一样的结构
        ModelBox.Items.Add(new ComboBoxItem { Content = Loc.T("自动（按纹理判断）"), Tag = SkinModels.Auto });
        ModelBox.Items.Add(new ComboBoxItem { Content = Loc.T("经典（粗手臂）"), Tag = SkinModels.Classic });
        ModelBox.Items.Add(new ComboBoxItem { Content = Loc.T("纤细（细手臂）"), Tag = SkinModels.Slim });
    }

    private StoredAccount? Account => _s.Accounts.Accounts.FirstOrDefault(a => a.Uuid == _uuid);

    private bool IsOffline => Account?.Type == AccountStore.OfflineType;

    private bool IsMicrosoft => Account?.Type == "microsoft";

    private bool IsExternal => Account?.Type == "external";

    /// <summary>本地皮肤库服务哪几类账户：离线 + 正版（第三方走皮肤站衣柜，见 <see cref="ApplySkinMode"/>）</summary>
    private bool SupportsLocalSkins => IsOffline || IsMicrosoft;

    /// <summary>衣柜一次给多少张皮肤渲染小立绘（下载 + 渲染都要钱，先给一屏够看的量）</summary>
    private const int WardrobeThumbLimit = 24;

    private bool _wardrobeBusy;
    private bool _wardrobeLoaded;

    /// <summary>壳导航显示时机（对齐桌面 OnNavigatedTo 的绑定时机）。</summary>
    public void OnShown()
    {
        _uuid = PendingUuid ?? "";
        Bind();
    }

    private void Back_Click(object sender, RoutedEventArgs e) =>
        AppNavigation.Navigate("accounts");

    private void Bind()
    {
        if (Account is not { } account)
        {
            ErrorBar.Message = Loc.T("这个账户已经不存在了（可能在别处被删除）");
            ErrorBar.IsOpen = true;
            return;
        }

        _selectedSkinId = account.SkinId;
        PlayerView.WalkEnabled = _s.Settings.AccountWalkAnimation;
        FillHeader(account);
        RefreshSkinList();
        RefreshPresetList();
        ApplySkinMode(account);
        BuildInfo(account);
    }

    /// <summary>
    /// 皮肤区按账户类型分工：
    /// <list type="bullet">
    /// <item>离线：本地皮肤库 —— 导入 / 默认皮肤 / 应用 / 取消，靠启动器本机注入进游戏；</item>
    /// <item>正版：同一套本地皮肤库，但每次更换前强制警告
    /// （微软账号上的官方皮肤不会被改动，注入也不是所有版本都保证生效，见 ConfirmSkinChangeAsync）；</item>
    /// <item>第三方：皮肤站衣柜 —— 皮肤存在皮肤站上，在这里选一张就是换皮肤站的皮肤。</item>
    /// </list>
    /// </summary>
    private void ApplySkinMode(StoredAccount account)
    {
        var offline = account.Type == AccountStore.OfflineType;
        var external = account.Type == "external";
        var microsoft = account.Type == "microsoft";

        LocalSkinLibrary.Visibility = offline || microsoft ? Visibility.Visible : Visibility.Collapsed;
        SkinActions.Visibility = offline || microsoft ? Visibility.Visible : Visibility.Collapsed;
        StationSkinLibrary.Visibility = external ? Visibility.Visible : Visibility.Collapsed;

        SkinNote.Text = offline
            ? Loc.T("离线账户没有可上传的账号：皮肤通过启动器本机注入生效（单人 / 局域网可见，其他玩家通常看到默认皮肤）。")
            : microsoft
                ? Loc.T("正版账户的皮肤归微软账号所有：这里的换肤只在本机生效，不改账号上的官方皮肤；每次更换前都会先警告。")
                : Loc.T("第三方账户的皮肤在皮肤站上：从右边「皮肤站衣柜」选一张就换掉。");

        if (SupportsLocalSkins)
        {
            // 左侧预览跟着「当前选中」走：进页时就是账户在用的那张（没挂皮肤则显示账号形象）
            PlayerView.WalkEnabled = _s.Settings.AccountWalkAnimation;
            RefreshSkinSelection();
            return;
        }

        // 第三方：左侧直接显示账号形象（皮肤由皮肤站决定），本地皮肤库与这类账号无关
        _selectedSkinId = null;
        PlayerView.SetAccount(account.Type, account.Uuid, account.Username, account.ServerBase);
        PlayerView.WalkEnabled = _s.Settings.AccountWalkAnimation;
        SkinNameText.Text = Loc.T("账号形象");
        SkinMetaText.Text = Loc.T("皮肤由认证服务器（皮肤站）提供");
        SkinUsageText.Text = "";

        if (external && !_wardrobeLoaded) _ = LoadWardrobeAsync(interactive: false);
    }

    private void FillHeader(StoredAccount account)
    {
        HeaderInitial.Text = string.IsNullOrWhiteSpace(account.Username)
            ? "?" : account.Username[..1].ToUpperInvariant();
        HeaderAvatar.Source = AvatarHelper.Resolve(account.Type, account.Uuid, account.Username,
            account.ServerBase, AvatarHelper.LocalSkinPath(account));
        HeaderType.Text = TypeLabel(account);
        HeaderDefaultBadge.Visibility = account.IsDefault ? Visibility.Visible : Visibility.Collapsed;
        SetDefaultBtn.Visibility = account.IsDefault ? Visibility.Collapsed : Visibility.Visible;
        InfoSetDefaultBtn.Visibility = account.IsDefault ? Visibility.Collapsed : Visibility.Visible;
        // 措辞随账户类型走（XAML 里没有静态文案，避免出现与类型不符的「设为默认」）
        var action = DefaultActionTextOf(account);
        SetDefaultBtn.Content = action;
        InfoSetDefaultBtn.Content = action;
    }

    /// <summary>「设为…」按钮的措辞随账户类型走：首选是按类型各记一个的</summary>
    private static string DefaultActionTextOf(StoredAccount account) => account.Type switch
    {
        "microsoft" => Loc.T("设为首选微软账户"),
        "external" => Loc.T("设为首选第三方账户"),
        "offline" => Loc.T("设为首选离线账户"),
        _ => Loc.T("设为首选账户"),
    };

    private static string TypeLabel(StoredAccount account) => account.Type switch
    {
        "microsoft" => Loc.T("微软账户"),
        "external" => account.ServerName is { Length: > 0 }
            ? Loc.T("第三方账户 · {0}", account.ServerName)
            : Loc.T("第三方账户"),
        "offline" => Loc.T("离线账户"),
        _ => account.Type,
    };

    // ── 分区切换 ──

    private void Section_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (InfoPanel is null) return; // XAML 解析期间触发（首个条目 IsSelected="True"）
        var info = (SectionList.SelectedItem as ListViewItem)?.Tag as string == "info";
        SkinPanel.Visibility = info ? Visibility.Collapsed : Visibility.Visible;
        InfoPanel.Visibility = info ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── 皮肤库 ──

    private void RefreshSkinList()
    {
        var current = Account?.SkinId;
        var rows = new List<SkinCardRow>
        {
            // 首位是「添加皮肤 / 点此导入」占位卡：与皮肤卡共用一个模板，靠可见性切换
            new("", "", "", null, false, isAdd: true),
        };
        rows.AddRange(_s.Skins.List().Select(e => new SkinCardRow(
            e.Id, e.Name, SkinDetail(e),
            SkinThumbnails.FromFile(_s.Skins.PathOf(e), IsSlim(e.Id)),
            e.Id == current)
        { IsSelected = e.Id == _selectedSkinId }));

        _savedRows = rows;
        SavedList.ItemsSource = rows;
        SavedCountText.Text = Loc.T("{0} 张", rows.Count - 1);   // 占位卡不算皮肤
        MotionFx.StaggerListItems(SavedList);
    }

    /// <summary>
    /// 「默认皮肤」的折叠：标题行就是开关。收起时省下的高度留给「我的皮肤」，
    /// 展开/收起走 <see cref="ExpandBehavior"/>（高度平滑推开 + 内容淡入）。
    /// </summary>
    private void PresetToggle_Click(object sender, RoutedEventArgs e)
    {
        _presetsExpanded = !_presetsExpanded;
        ExpandBehavior.SetIsExpanded(PresetPane, _presetsExpanded);
        MotionFx.RotateChevron(PresetChevron, _presetsExpanded ? 90f : 0f, _s.Motion.Enabled);
        if (_presetsExpanded) LayoutCardGrids();
    }

    /// <summary>首次布局完成后把箭头摆到正确角度（此时才有尺寸，旋转中心才在正中）</summary>
    private void PresetChevron_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is FontIcon chevron)
            MotionFx.RotateChevron(chevron, _presetsExpanded ? 90f : 0f, motion: false);
    }

    private void RefreshPresetList()
    {
        var imported = _s.Skins.List()
            .Where(e => e.Source == PresetSource)
            .Select(e => e.SourceUrl ?? "")
            .ToHashSet(StringComparer.Ordinal);

        // 当前账户挂的是哪张预置皮肤（只有来源是预置才算）——用来在卡片上标「使用中」
        var current = Account?.SkinId is { Length: > 0 } skinId ? _s.Skins.Find(skinId) : null;
        var inUse = current?.Source == PresetSource ? current.SourceUrl ?? "" : "";

        // 预置皮肤在 APK 资产里（Assets/Skins/default）：读字节后走 FromBytes 渲染 + 磁盘缓存
        PresetList.ItemsSource = SkinPresets.All
            .Select(p =>
            {
                var png = AppAssets.Read(PresetAssetPath(p));
                return new SkinPresetRow(p.Id, p.Name,
                    png is null ? null : SkinThumbnails.FromBytes(png, p.Slim, $"preset|{p.Id}|{p.File}"),
                    imported.Contains(p.Id), InUse: p.Id == inUse);
            })
            .ToList();
        LayoutCardGrids();
    }

    private const string PresetSource = "preset";

    /// <summary>默认皮肤资产的 APK 内路径（csproj AndroidAsset 已全量打包 Assets/Skins/default）</summary>
    private static string PresetAssetPath(SkinPreset preset) =>
        $"Assets/Skins/default/{preset.File}";

    private void CardGrid_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutCardGrids();

    /// <summary>
    /// 卡片网格的高度要自己算：ItemsWrapGrid 只有在拿到"确定的高度"时才会换行，
    /// 高度无限时会把卡片排成一整行。行高 = 卡片高 + 上下外边距（我的皮肤 152+4、默认皮肤 146+4），
    /// 少算这 4px 就会把第二行切掉一条。
    /// </summary>
    private void LayoutCardGrids()
    {
        var width = SavedList.ActualWidth > 0 ? SavedList.ActualWidth : PresetList.ActualWidth;
        if (width <= 0) return;
        var columns = Math.Max(1, (int)Math.Floor(width / 104));

        // 我的皮肤：最多两行，多了在卡片区内自己滚
        var savedCount = Math.Max(_savedRows.Count, 1);
        var savedRows = Math.Min(2, Math.Max(1, (int)Math.Ceiling(savedCount / (double)columns)));
        var savedHeight = savedRows * 156 + 4;
        if (Math.Abs(SavedList.Height - savedHeight) > 0.5) SavedList.Height = savedHeight;

        // 默认皮肤：给足行数，一次看全
        var presetRows = Math.Max(1, (int)Math.Ceiling(SkinPresets.All.Count / (double)columns));
        var presetHeight = presetRows * 150 + 4;
        if (Math.Abs(PresetList.Height - presetHeight) > 0.5) PresetList.Height = presetHeight;
    }

    private static string SkinDetail(SkinEntry entry) =>
        $"{entry.SizeText} · {Loc.T("型号：{0}", ModelLabel(entry.Model))}" +
        (entry.Source switch
        {
            "url" => $" · {Loc.T("来自链接")}",
            PresetSource => $" · {Loc.T("启动器自带")}",
            _ => "",
        });

    /// <summary>皮肤库条目的胖瘦版型（缩略图渲染要按它决定手臂粗细）</summary>
    private bool IsSlim(string skinId) =>
        _s.Skins.ResolveModel(skinId, null) == SkinModels.Slim;

    /// <summary>型号显示名（三档固定文案集中在这里，便于本地化词典收录）</summary>
    internal static string ModelLabel(string? model) => SkinModels.Normalize(model) switch
    {
        SkinModels.Classic => Loc.T("经典（粗手臂）"),
        SkinModels.Slim => Loc.T("纤细（细手臂）"),
        _ => Loc.T("自动（按纹理判断）"),
    };

    private void Saved_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SkinCardRow row) return;
        if (row.IsAdd)
        {
            // 「添加皮肤」卡不是一张皮肤：点它走导入
            _ = ImportFromPickerAsync();
            return;
        }
        _selectedSkinId = row.Id;
        SkinStatus.Text = "";
        RefreshSkinSelection();
    }

    private void RefreshSkinSelection()
    {
        // 选中描边：逐张改状态，不重建列表（重建会让卡片闪一下、滚动也回顶）
        foreach (var row in _savedRows) row.IsSelected = row.Id == _selectedSkinId;

        if (!SupportsLocalSkins) return; // 第三方账户的左侧形象由 ApplySkinMode 负责
        var entry = _selectedSkinId is { Length: > 0 } id ? _s.Skins.Find(id) : null;
        if (entry is null)
        {
            if (IsMicrosoft && Account is { } msAccount)
            {
                // 正版账户还没选本地皮肤：先按账号形象显示（微软官方皮肤，minotar 取不到则内置 Steve），
                // 选了卡片再切成本地预览——"没选"这一态对正版不是空白，是官方那张。
                PlayerView.SetAccount(msAccount.Type, msAccount.Uuid, msAccount.Username,
                    msAccount.ServerBase);
                SkinNameText.Text = Loc.T("账号形象");
                SkinMetaText.Text = Loc.T("皮肤由微软账号提供");
                SkinUsageText.Text = "";
            }
            else
            {
                PlayerView.SetLocalSkin(null);
                SkinNameText.Text = Loc.T("还没有选中皮肤");
                SkinMetaText.Text = Loc.T("从右侧「我的皮肤」或「默认皮肤」里挑一张");
                SkinUsageText.Text = "";
            }
            ModelBox.IsEnabled = false;
            ApplyBtn.IsEnabled = false;
            UnbindBtn.Visibility = Account?.SkinId is { Length: > 0 }
                ? Visibility.Visible : Visibility.Collapsed;
            return;
        }

        PlayerView.SetLocalSkin(_s.Skins.PathOf(entry));
        SkinNameText.Text = entry.Name;
        SkinMetaText.Text = SkinDetail(entry);
        _fillingModel = true;
        try
        {
            ModelBox.SelectedIndex = entry.Model switch
            {
                SkinModels.Classic => 1,
                SkinModels.Slim => 2,
                _ => 0,
            };
        }
        finally { _fillingModel = false; }
        ModelBox.IsEnabled = true;
        ApplyBtn.IsEnabled = true;
        // 「取消使用」只在这个账户真的挂着皮肤时才有对象
        UnbindBtn.Visibility = Account?.SkinId is { Length: > 0 }
            ? Visibility.Visible : Visibility.Collapsed;
        SkinUsageText.Text = Account?.SkinId == entry.Id
            ? Loc.T("当前账户正在使用这张皮肤")
            : Loc.T("尚未应用到当前账户");
    }

    private void Model_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_fillingModel) return;   // 代码回填，不是用户改的
        if (_selectedSkinId is not { Length: > 0 } id) return;
        if (ModelBox.SelectedItem is not ComboBoxItem { Tag: string tag }) return;
        var entry = _s.Skins.Find(id);
        if (entry is null || entry.Model == tag) return;
        _s.Skins.SetModel(id, tag);
        RefreshSkinList(); // 卡片的型号与缩略图随之更新，选中态按 _selectedSkinId 还原
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportFromPickerAsync();

    /// <summary>
    /// 本地文件导入：安卓侧没有可直接用的文件选择器封装（桌面 WinRT FileOpenPicker
    /// 在 Uno 安卓上不可用），入口保留但引导走「从链接导入」——那是完整的替代路径。
    /// </summary>
    private async Task ImportFromPickerAsync()
    {
        await UiDialog.AlertAsync(Loc.T("导入皮肤"),
            Loc.T("触屏端暂不支持从本地文件导入，请点右上角「从链接导入」，粘贴皮肤 PNG 图片的链接。"));
    }

    /// <summary>导入一个皮肤文件并选中它；失败把原因写在状态行（保留：与桌面共用导入管线）</summary>
    private void ImportFile(string path)
    {
        var (entry, error) = _s.Skins.ImportPath(path);
        if (entry is null)
        {
            ShowSkinError(Loc.T("导入失败：{0}", Loc.TCore(error)));
            return;
        }
        _selectedSkinId = entry.Id;
        RefreshSkinList();
        RefreshSkinSelection();
        SkinStatus.Text = Loc.T("已导入「{0}」，点「应用到该账户」即可使用", entry.Name);
        ClearBars();
    }

    private void UrlImport_Click(object sender, RoutedEventArgs e) => _ = ImportFromUrlAsync();

    /// <summary>
    /// 从链接导入：链接先在一个对话框里问清楚（页面上常驻一个输入框会一直占着一行，
    /// 而这是偶尔用一次的动作）。
    /// </summary>
    private async Task ImportFromUrlAsync()
    {
        var box = new TextBox
        {
            PlaceholderText = Loc.T("皮肤图片链接（http/https）"),
            Width = 360,
        };
        var dlg = new ThemedDialog
        {
            Title = Loc.T("从链接导入皮肤"),
            Content = box,
            PrimaryButtonText = Loc.T("导入"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Ui.Root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var url = box.Text.Trim();
        if (url.Length == 0)
        {
            ShowSkinError(Loc.T("请先粘贴皮肤图片链接"));
            return;
        }
        SkinStatus.Text = Loc.T("正在下载…");
        var (entry, error) = await _s.Skins.ImportUrlAsync(url, null, _s.Download);
        if (entry is null)
        {
            ShowSkinError(Loc.T("导入失败：{0}", Loc.TCore(error)));
            return;
        }
        _selectedSkinId = entry.Id;
        RefreshSkinList();
        RefreshSkinSelection();
        SkinStatus.Text = Loc.T("已从链接导入「{0}」", entry.Name);
        ClearBars();
    }

    // ── 默认皮肤 ──

    private async void Preset_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not SkinPresetRow row) return;
        var preset = SkinPresets.All.FirstOrDefault(p => p.Id == row.Id);
        if (preset is null || Account is not { } account) return;

        // 点预置皮肤 = 加入衣柜并应用：应用这一步对正版账户同样要过强制警告。
        // 放在最前面（还没读文件、还没入库）：取消就是彻底取消，不留半截状态。
        if (!await ConfirmSkinChangeAsync()) return;

        // 同一张预置皮肤只入库一次：重复点就复用已有条目（按来源 + 配方 id 去重）
        var entry = _s.Skins.List()
            .FirstOrDefault(x => x.Source == PresetSource && x.SourceUrl == preset.Id);
        var added = false;
        if (entry is null)
        {
            byte[]? png;
            try
            {
                png = AppAssets.Read(PresetAssetPath(preset));
            }
            catch (Exception ex)
            {
                ShowSkinError(Loc.T("导入失败：{0}", Loc.TCore(ex.Message)));
                return;
            }
            if (png is null)
            {
                ShowSkinError(Loc.T("导入失败：{0}", Loc.TCore("内置皮肤资产缺失")));
                return;
            }
            var (created, error) = _s.Skins.ImportBytes(png, preset.Name,
                source: PresetSource, sourceUrl: preset.Id);
            if (created is null)
            {
                ShowSkinError(Loc.T("导入失败：{0}", Loc.TCore(error)));
                return;
            }
            entry = created;
            added = true;
        }

        var model = preset.Slim ? SkinModels.Slim : SkinModels.Classic;
        _s.Accounts.SetSkin(account.Uuid, entry.Id, model);
        account.SkinId = entry.Id;
        account.SkinModel = model;
        _selectedSkinId = entry.Id;
        RefreshSkinList();
        RefreshPresetList();
        RefreshSkinSelection();
        FillHeader(account);

        // 视线这时还停在下面那排默认皮肤上：把库里对应的卡片滚进视野。
        // 复用已有条目（预置皮肤只入库一次）时，皮肤库那边本来就不会多出新卡，
        // 不滚过去的话用户看到的仍是"点了没反应"。
        if (SavedList.ItemsSource is List<SkinCardRow> cards &&
            cards.FirstOrDefault(r => r.Id == entry.Id) is { } card)
            SavedList.ScrollIntoView(card);

        // 文案与账户类型无关：离线与正版都是"本机注入、下次启动生效"（正版另有强制警告）
        var applied = Loc.T("已应用「{0}」：下次启动游戏就在本机看到（单人 / 局域网）", preset.Name);
        SkinStatus.Text = added
            ? Loc.T("已加入皮肤库并选中「{0}」", preset.Name) + "　" + applied
            : applied;
        ClearBars();
    }

    // ── 皮肤库操作 ──

    /// <summary>
    /// 重命名（长按菜单里的「重命名」）：名字在一个对话框里改。
    /// 页面上不再常驻"新名字 + 重命名"那一行——它作用于"当前选中项"，
    /// 而长按菜单永远对准被点中的那张卡。
    /// </summary>
    private async Task RenameSkinAsync(string id)
    {
        var entry = _s.Skins.Find(id);
        if (entry is null) return;

        var box = new TextBox { Text = entry.Name, Width = 320 };
        var dlg = new ThemedDialog
        {
            Title = Loc.T("重命名皮肤"),
            Content = box,
            PrimaryButtonText = Loc.T("保存"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Ui.Root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var name = box.Text.Trim();
        if (name.Length == 0)
        {
            ShowSkinError(Loc.T("请先填新名字"));
            return;
        }
        var (ok, error) = _s.Skins.Rename(id, name);
        if (!ok)
        {
            ShowSkinError(Loc.T("重命名失败：{0}", Loc.TCore(error)));
            return;
        }
        RefreshSkinList();
        RefreshPresetList();
        RefreshSkinSelection();
        SkinStatus.Text = Loc.T("已重命名");
        ClearBars();
    }

    /// <summary>
    /// 皮肤卡长按菜单（重命名 / 删除）。做成卡片自己的上下文菜单，而不是页面上一排按钮：
    /// 按钮版要先"选中卡片、再点删除"，删掉的是当前选中项 —— 想删第二张却删了第一张太容易。
    /// 安卓上长按会触发 ContextRequested（与桌面右键同一事件），操作永远作用于被点中的那张卡。
    /// </summary>
    private void SavedList_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        var row = RowAt(e.OriginalSource);          // 命中元素可能是缩略图 / 名字，往上找卡片
        if (row is null || row.IsAdd) return;        // 「添加皮肤」占位卡没有可改的东西

        _selectedSkinId = row.Id;                    // 长按先选中：菜单与左侧预览都对准这张
        RefreshSkinSelection();
        var anchor = SavedList.ContainerFromItem(row) as FrameworkElement ?? SavedList;

        var menu = new MenuFlyout();
        var rename = new MenuFlyoutItem
        {
            Text = Loc.T("重命名"),
            Icon = new FontIcon { FontFamily = IconFont.Family, Glyph = "\uE8AC" },
        };
        rename.Click += (_, _) => _ = RenameSkinAsync(row.Id);
        menu.Items.Add(rename);

        var delete = new MenuFlyoutItem
        {
            Text = Loc.T("删除"),
            Icon = new FontIcon { FontFamily = IconFont.Family, Glyph = "\uEAA5" },
        };
        delete.Click += (_, _) => DeleteSkin(row.Id);
        menu.Items.Add(delete);

        if (e.TryGetPosition(anchor, out var point))
            menu.ShowAt(anchor, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(anchor);                     // 无指针位置：贴着卡片弹
        e.Handled = true;
    }

    /// <summary>命中的元素 → 它所属的皮肤卡行（不在卡片里返回 null）</summary>
    private static SkinCardRow? RowAt(object? source)
    {
        var node = source as DependencyObject;
        while (node is not null)
        {
            if (node is FrameworkElement { DataContext: SkinCardRow row }) return row;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    private void DeleteSkin(string id)
    {
        var entry = _s.Skins.Find(id);
        if (entry is null || !_s.Skins.Delete(id)) return;
        var cleared = _s.Accounts.ClearSkinReference(id); // 别留下指向空洞的引用
        if (_selectedSkinId == id) _selectedSkinId = null;
        RefreshSkinList();
        RefreshPresetList();
        RefreshSkinSelection();
        if (Account is { } account) FillHeader(account);
        SkinStatus.Text = cleared > 0
            ? Loc.T("已删除「{0}」，{1} 个账户已恢复默认形象", entry.Name, cleared)
            : Loc.T("已删除「{0}」", entry.Name);
        ClearBars();
    }

    /// <summary>
    /// 正版账户换肤前的强制确认（口径：正版每次更换前都要有强制性警告）。
    /// 内容必须诚实，不能只说"能换"：本机注入救不了官方皮肤，也不是所有版本都保证生效。
    /// 离线账户没有账号可影响，第三方走皮肤站，这两类直接放行。
    /// </summary>
    private async Task<bool> ConfirmSkinChangeAsync()
    {
        if (!IsMicrosoft) return true;

        var dlg = new ThemedDialog
        {
            Title = Loc.T("正版账户换肤警告"),
            Content = new TextBlock
            {
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 420,
                Text = Loc.T("这是微软正版账户，本次操作只影响本机：\n\n" +
                             "• 只改你本机看到的形象：启动器里的预览一定生效；游戏内 1.19.2 及更早版本直接生效，" +
                             "1.19.3 起客户端要求签名属性，本机注入不保证生效。\n" +
                             "• 不会修改、上传或删除微软账号上的官方皮肤。\n" +
                             "• 其他玩家看到的仍然是你微软账号上的官方皮肤。\n" +
                             "• 下次用启动器启动游戏时才生效。\n\n" +
                             "每次更换都会再确认一次。"),
            },
            PrimaryButtonText = Loc.T("我已了解，仍然更换"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Ui.Root,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (Account is not { } account || _selectedSkinId is not { Length: > 0 } id) return;
        if (!await ConfirmSkinChangeAsync()) return;
        var model = (ModelBox.SelectedItem as ComboBoxItem)?.Tag as string ?? SkinModels.Auto;
        _s.Accounts.SetSkin(account.Uuid, id, model);
        account.SkinId = id;
        account.SkinModel = SkinModels.Normalize(model);
        RefreshSkinList();
        RefreshSkinSelection();
        FillHeader(account);
        SkinStatus.Text = Loc.T("已应用：下次启动游戏就在本机看到这张皮肤（单人 / 局域网）");
        ClearBars();
    }

    private async void Unbind_Click(object sender, RoutedEventArgs e)
    {
        if (Account is not { } account) return;
        if (!await ConfirmSkinChangeAsync()) return;
        _s.Accounts.SetSkin(account.Uuid, null);
        account.SkinId = null;
        account.SkinModel = null;
        RefreshSkinList();
        RefreshSkinSelection();
        FillHeader(account);
        SkinStatus.Text = Loc.T("已取消使用：该账户恢复默认形象");
        ClearBars();
    }

    // ── 皮肤站衣柜（第三方账户） ──

    private void Station_Refresh_Click(object sender, RoutedEventArgs e) =>
        _ = LoadWardrobeAsync(interactive: true);

    private async void Station_Login_Click(object sender, RoutedEventArgs e)
    {
        if (Account is not { Type: "external" } account) return;
        // 令牌不落盘：重启启动器后要换肤就得重新登录一次（与启动游戏时的就地登录是同一个弹窗）
        var name = await AuthDialogs.ExternalPasswordAsync(Ui.Root!,
            account.ServerBase ?? account.ServerAlias, account.Username);
        if (name is null) return;                       // 取消或失败（失败已在弹窗里提示）
        _wardrobeLoaded = false;
        await LoadWardrobeAsync(interactive: true);
    }

    private async void Station_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not StationSkinRow row) return;
        if (Account is not { Type: "external" } account) return;
        var token = _s.Accounts.GetSession(account.Uuid)?.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            StationStatus.Text = Loc.T("需要先登录该账号：令牌不落盘，点「重新登录该账号」后再试。");
            return;
        }

        StationList.IsEnabled = false;
        StationBusy.IsActive = true;
        StationBusy.Visibility = Visibility.Visible;
        StationStatus.Text = Loc.T("正在换成「{0}」…", row.Name);
        try
        {
            var message = await _s.SkinStation.ApplyAsync(account.ServerBase ?? "", token, row.Skin);
            StationStatus.Text = Loc.TCore(message);
            // 换完重新拉一次账号形象：左侧 3D 预览要跟上（皮肤站已经换了）
            PlayerView.SetAccount(account.Type, account.Uuid, account.Username, account.ServerBase);
        }
        catch (Exception ex)
        {
            StationStatus.Text = Loc.TCore(ex.Message);
        }
        finally
        {
            StationBusy.IsActive = false;
            StationBusy.Visibility = Visibility.Collapsed;
            StationList.IsEnabled = true;
        }
    }

    /// <summary>
    /// 读皮肤站衣柜：列出站上属于该账号的皮肤，并把每张纹理渲染成卡片小立绘
    /// （与本地皮肤卡同一套渲染 + 磁盘缓存，卡片长得一致）。
    /// </summary>
    private async Task LoadWardrobeAsync(bool interactive)
    {
        if (Account is not { Type: "external" } account || _wardrobeBusy) return;
        var token = _s.Accounts.GetSession(account.Uuid)?.AccessToken;
        if (string.IsNullOrWhiteSpace(token))
        {
            StationStatus.Text = Loc.T("本次运行还没有该账号的登录凭证（令牌不落盘）：点「重新登录该账号」后就能读取皮肤站上的皮肤。");
            return;
        }

        _wardrobeBusy = true;
        StationBusy.IsActive = true;
        StationBusy.Visibility = Visibility.Visible;
        StationRefreshBtn.IsEnabled = false;
        if (interactive || StationList.ItemsSource is null)
            StationStatus.Text = Loc.T("正在读取皮肤站上的皮肤…");
        try
        {
            var skins = await _s.SkinStation.ListAsync(account.ServerBase ?? "", token);
            var rows = new List<StationSkinRow>();
            foreach (var skin in skins.Take(WardrobeThumbLimit))
                rows.Add(new StationSkinRow(skin,
                    ModelLabel(skin.Slim ? SkinModels.Slim : SkinModels.Classic),
                    await RenderStationThumbAsync(skin)));
            StationList.ItemsSource = rows;
            _wardrobeLoaded = true;
            LayoutStationGrid();
            StationStatus.Text = skins.Count == 0
                ? Loc.T("皮肤站上还没有皮肤：先到皮肤站网页上传或收藏几张，再回来刷新。")
                : Loc.T("共 {0} 张，点一张就换成它。", skins.Count);
        }
        catch (Exception ex)
        {
            StationStatus.Text = Loc.TCore(ex.Message);
        }
        finally
        {
            StationBusy.IsActive = false;
            StationBusy.Visibility = Visibility.Collapsed;
            StationRefreshBtn.IsEnabled = true;
            _wardrobeBusy = false;
        }
    }

    private void Station_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutStationGrid();

    /// <summary>
    /// 衣柜网格的高度要自己算：<see cref="ItemsWrapGrid"/> 只有拿到确定高度时才换行，
    /// 高度无限时会把几十张卡排成一整行（皮肤库那两个网格同一个坑）。
    /// </summary>
    private void LayoutStationGrid()
    {
        var width = StationList.ActualWidth;
        if (width <= 0) return;
        var columns = Math.Max(1, (int)Math.Floor(width / 104));
        var count = (StationList.ItemsSource as List<StationSkinRow>)?.Count ?? 0;
        var rows = Math.Max(1, (int)Math.Ceiling(Math.Max(count, 1) / (double)columns));
        var height = rows * 152 + 4;
        if (Math.Abs(StationList.Height - height) > 0.5) StationList.Height = height;
    }

    /// <summary>
    /// 站上那张纹理 → 卡片小立绘。渲染与落盘放后台线程（解码 + 光栅化 + PNG 编码都是 CPU 活），
    /// 但 <see cref="Microsoft.UI.Xaml.Media.Imaging.BitmapImage"/> 是 DependencyObject，只能在 UI 线程建，所以拆成"先出文件、再取图源"。
    /// </summary>
    private async Task<ImageSource?> RenderStationThumbAsync(StationSkin skin)
    {
        var png = await _s.SkinStation.TryFetchPngAsync(skin.Url);
        if (png is null) return null;
        var key = $"station|{skin.Id}|{skin.Url}";
        try
        {
            var file = await Task.Run(() => SkinThumbnails.EnsureThumbFile(png, skin.Slim, key));
            return SkinThumbnails.SourceFromFile(file);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("skin", $"皮肤站缩略图失败（{skin.Name}）：{ex.Message}");
            return null;
        }
    }

    // ── 账户信息 ──

    private void BuildInfo(StoredAccount account)
    {
        InfoRows.Children.Clear();
        AddInfoRow(Loc.T("类型"), TypeLabel(account));
        AddInfoRow(Loc.T("用户名"), account.Username);
        AddInfoRow(Loc.T("UUID"), account.Uuid, mono: true);
        if (account.Type == "external")
            AddInfoRow(Loc.T("认证服务器"), account.ServerBase ?? Loc.T("（未记录）"));
        AddInfoRow(Loc.T("会话状态"), SessionText(account));
    }

    private string SessionText(StoredAccount account)
    {
        if (account.Type == AccountStore.OfflineType)
            return Loc.T("离线身份不需要登录");
        return _s.Accounts.GetSession(account.Uuid) is { AccessToken.Length: > 0 }
            ? Loc.T("本次运行已登录，可直接启动游戏")
            : Loc.T("本次运行未登录，启动游戏时会要求就地登录（令牌不落盘，重启应用即失效）");
    }

    private void AddInfoRow(string label, string value, bool mono = false)
    {
        var grid = new Grid { ColumnSpacing = 12 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(112) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

        var labelText = new TextBlock
        {
            Text = label,
            FontSize = 13,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        };
        var valueText = new TextBlock
        {
            Text = value,
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        if (mono) valueText.FontFamily = new FontFamily("monospace"); // 安卓通用等宽族（桌面为 Consolas）
        Grid.SetColumn(labelText, 0);
        Grid.SetColumn(valueText, 1);
        grid.Children.Add(labelText);
        grid.Children.Add(valueText);
        InfoRows.Children.Add(grid);
    }

    // ── 账户操作 ──

    private void SetDefault_Click(object sender, RoutedEventArgs e)
    {
        if (Account is not { } account) return;
        _s.Accounts.SetDefault(account.Uuid);
        FillHeader(account);
        BuildInfo(account);
        StatusBar.Message = Loc.T("已将 {0} 设为首选账户", account.Username);
        StatusBar.IsOpen = true;
        ErrorBar.IsOpen = false;
    }

    private async void DeleteAccount_Click(object sender, RoutedEventArgs e)
    {
        if (Account is not { } account) return;
        var dlg = new ThemedDialog
        {
            Title = Loc.T("删除账户"),
            Content = Loc.T("确定要删除账户 {0} 吗？", account.Username),
            PrimaryButtonText = Loc.T("删除"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Ui.Root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        _s.Accounts.Remove(account.Uuid);
        AccountsPage.PendingStatus = Loc.T("已删除账户 {0}", account.Username);
        Back_Click(sender, e);
    }

    // ── 工具 ──

    private void ShowSkinError(string message)
    {
        SkinStatus.Text = message;
        ClearBars();
    }

    private void ClearBars()
    {
        ErrorBar.IsOpen = false;
        StatusBar.IsOpen = false;
    }
}
