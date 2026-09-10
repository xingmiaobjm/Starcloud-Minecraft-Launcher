# 存档跨版本管理实现计划

日期：2026-09-10
设计文档：docs/superpowers/specs/2026-09-10-saves-cross-version-design.md（commit e9fe241）

构建命令：先 `Stop-Process -Name Starcloud.App -Force -ErrorAction SilentlyContinue`，
再 `dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo`
（每个任务完成后构建验证，目标 0 警告 0 错误）

计划期微修（相对 spec 的实现决定，不改变设计与验收）：

- 基础启动**不执行**版本级启动前 / 后命令（`PreLaunchCommand` / `PostLaunchCommand`）：
  保持「基础模式 + 极简」语义，避免再引入两个确认弹窗。`LaunchOptions` 该两参数传 null。
- `SaveLibraryService` 注册进 `AppServices.Instance`（与 `Manifest` 同源）；
  `PendingWorldStore` 按 `InstanceSettingsStore` 的既有惯例**按需 new**（`new PendingWorldStore(dotMc)`），
  不进 `AppServices`。
- `SaveService` 改签名后 `NovaService` 无需改动（该服务注入的 `_saves` 从未被调用，
  仅构造传参，不参与编译期检查）。

前置确认（已核实，实现时不再重复探索）：

- `SaveInfo`（SaveService.cs:9）现为 `(FolderName, LevelName, LastPlayed, SizeBytes, IconPath)`；
  `NbtReader` / `NbtCompound` 等 NBT 解析代码（SaveService.cs:102–231）**不动**
- `SaveService` 调用点只有 `SavesPage.xaml.cs`：L29 `ListSaves`、L110 `Backup`、L143 `Delete`；
  `NovaService.cs:54/82` 仅注入未调用
- `SavesPage.xaml.cs`：`_rows`（L15）、`_busy`（L16）、`Loaded += (_, _) => LoadSaves()`（L21）、
  `UpdateRowActions`（L46–52）、`FormatSize`（L58）、`OpenInExplorer`（L66）；
  **L79 与 L85 把 `_s.Settings.DotMinecraft` 硬编码为路径基准，必须改**
- `SavesPage.xaml` 根 `Grid MaxWidth="1280" HorizontalAlignment="Stretch" RowSpacing="16"`（L9），
  4 行：页头 / InfoBar(OkBar+ErrorBar) / 工具条 / 列表卡（L50–116）
- `ManifestService.ListInstalled(string dotMinecraft)` → `IReadOnlyList<InstalledVersionInfo(Id, SizeBytes)>`
  （ManifestService.cs:50/69）；`InstanceLayout.IsIsolated(profile, settings)` /
  `GameRoot(dotMc, versionId, isolated)` / `NextAvailablePath(path)` 均为既有静态方法
- `InstanceSettingsStore` 惯例是**按需 new**：`new InstanceSettingsStore(dotMc)` → `Get(id)` / `Save()` /
  `TouchPlayed(id)`（HomePage.xaml.cs:513/629）
- `LaunchOptions`（GameLauncher.cs:11–30）字段顺序见下；`LaunchCredentials`（L33）=
  `(Uuid, AccessToken, UserType, Xuid?)`；`GameLauncher.LaunchVersionAsync(dotMc, versionId, options)`
  → `Task<Process>`（L276）
- `AccountStore`：`Accounts` / `Default` / `SetDefault(uuid)` / `AddOrUpdateMicrosoft(...)`；
  `MicrosoftAuthService.RefreshAsync(refreshToken)` → `LoginResult?(Uuid, Username, AccessToken, RefreshToken, Xuid)`
- `LauncherSettings`（LauncherPaths.cs:66 起）：`DotMinecraft`、`VersionIsolated`(默认 true)、
  `LastUsername`、`LastVersion`、`DefaultMinMemoryMb`、`DefaultMaxMemoryMb`、`JavaExecutable`、
  `ExtraJvmArgs`、`UseBuiltinJvmArgs`
- `StarcloudSession`（StarcloudIdService.cs）= `(UserId, DisplayName, ApiKey, Channel, LoginAt)`，
  **无头像字段**；`StarcloudIdService.Current` / `IsLoggedIn`；`CloudChannel = "cloud"`
- `MainWindow.xaml`：`NovaOrb`（L28–64，含 Dark/Light 两套 `NovaOrbAcrylicBrush`）、
  `Nav` 菜单项（L77–107）、`NovaBar`（L122–167）、状态栏（L169–187，含 `NovaStateDot`/`NovaStateText`）、
  `NovaPopup`（L190 起）
- `MainWindow.xaml.cs`：`_toastResetTimer`（L138）、`OnOverlayToast`（L141）、
  `ApplyNovaState`（L150–166）、`NovaOrb_Click`（L84–92）、`NovaBarToggle_Click`（L95–100）、
  `OpenNovaBar`（L103–107）、`HandleJoinUrlAsync`（L110–114）、`NavigateTo(tag)`（L68–75）
- `HomePage.xaml.cs`：`_mode`（L22）、`SetMode`（L52 用 `LastUsername` 的 `ms:` 前缀）、
  `ResolvePlayerName`（L656–666）、`ResolveMicrosoftAccountAsync`（L671–693）、
  `AskNova_Click` 调 `OpenNovaBar()`；`StartLaunchAsync`（L479–654）是启动链路范本
- `AccountsPage` 已有 Starcloud ID 区块（对应导航 Tag `accounts`），个人中心**不新建页**

## T1：Core · SaveService 改认实例根

文件：`dotnet/src/Starcloud.Core/Services/SaveService.cs`

1. `SaveInfo` 增三个归属字段：

```csharp
public sealed record SaveInfo(
    string FolderName, string LevelName, DateTime? LastPlayed, long SizeBytes, string? IconPath,
    string VersionId,   // 所属版本 Id；"" 表示共享根
    bool Isolated,      // 所属版本是否隔离
    string GameRoot);   // 存档所在实例根
```

2. 三个方法签名改由调用方给基准：

```csharp
public List<SaveInfo> ListSaves(string gameRoot, string versionId, bool isolated)
public string Backup(string gameRoot, string backupsDir, string folderName)
public void Delete(string gameRoot, string folderName)
```

- `ListSaves` 扫 `Path.Combine(gameRoot, "saves")`，构造 `SaveInfo` 时填入
  `versionId` / `isolated` / `gameRoot`；排序逻辑不变。
- `Backup` 用传入的 `backupsDir`（不再自行拼 `dotMinecraft/backups`），
  `Directory.CreateDirectory(backupsDir)` 保留。
- `Delete` 用 `Path.Combine(gameRoot, "saves", folderName)`。
- 删掉私有 `SavesDir(string)`（不再有单一基准）。
3. 验证：构建（此时 `SavesPage` 会编译失败，属预期，T6 修复）。

## T2：Core · SaveLibraryService（跨版本聚合）

新文件：`dotnet/src/Starcloud.Core/Services/SaveLibraryService.cs`

```csharp
public sealed class SaveLibraryService(ManifestService manifest)
{
    /// <summary>已装版本 Id 列表（供存档页版本筛选下拉）</summary>
    public List<string> ListVersionIds(string dotMc);

    /// <summary>共享根 + 各隔离版本根下的全部存档，按 LastPlayed 倒序</summary>
    public List<SaveInfo> ListAll(string dotMc, LauncherSettings settings);
}
```

1. `ListVersionIds` = `manifest.ListInstalled(dotMc).Select(v => v.Id).ToList()`。
2. `ListAll`：
   - 共享根：`_saves.ListSaves(dotMc, "", false)`。
   - 逐版本：`new InstanceSettingsStore(dotMc).Get(id)` 拿 `VersionProfile` →
     `InstanceLayout.IsIsolated(profile, settings)`；
     - `true` → `InstanceLayout.GameRoot(dotMc, id, true)` 作根，
       `ListSaves(root, id, true)`，结果并入；
     - `false` → **跳过**（根就是共享根，已在第 1 步扫过，避免重复）。
   - 两个 `InstanceSettingsStore` 实例在同一方法内复用同一个（构造一次）。
   - 合并后整体按 `LastPlayed` 倒序（`SaveService` 内部已各自排序，合并后需再排一次）。
3. `AppServices`：新增 `public SaveLibraryService SaveLibrary { get; }`，
   构造 `SaveLibrary = new SaveLibraryService(Manifest);`（放在 `Manifest` 之后）。
4. 验证：构建（仍有 T6 的编译错误，或先临时改 `SavesPage` 编译通过——按 T1→T6 顺序做即可，
   T1–T5 结束时构建报错可接受，T6 后必须 0 错误）。

> 实现提示：若希望 T1–T5 每步都能构建，可把 T6 的「改用 `row.GameRoot` +
> `ListAll`」放到 T1 之后立刻做。计划按依赖顺序列出，执行时可按此调整。

## T3：Core · LaunchAccountService + ResolvedPlayer

新文件：`dotnet/src/Starcloud.Core/Services/LaunchAccountService.cs`

```csharp
public sealed record ResolvedPlayer(string Name, LaunchCredentials? Credentials);

public sealed class LaunchAccountService(
    AccountStore accounts,
    MicrosoftAuthService auth,
    Func<LauncherSettings> settings)
{
    private string? _mode;
    public string Mode
    {
        get => _mode ??= settings().LastUsername is { } u && u.StartsWith("ms:") ? "microsoft" : "offline";
        set => _mode = value;
    }

    public string OfflineName { get; set; } = "";

    /// <summary>解析当前应使用的玩家身份（正版含凭据续期）。失败抛异常，由调用方展示。</summary>
    public async Task<ResolvedPlayer> ResolveAsync();
}
```

1. `ResolveAsync` 逻辑（照搬 HomePage.xaml.cs:656–693 语义）：
   - `Mode == "microsoft"`：取 `accounts.Default`（`Type == "microsoft"`）；为空抛
     `InvalidOperationException("尚无微软账户，请先到账户页登录")`；有 `RefreshToken` 时
     `await auth.RefreshAsync(...)`，成功则 `accounts.AddOrUpdateMicrosoft(...)` 并返回带凭据的
     `ResolvedPlayer`；异常吞掉降级；否则用存量 `AccessToken`；都没有则抛
     `InvalidOperationException($"{acc.Username} 的 Microsoft 登录已过期，请到账户页重新登录")`。
   - 离线：名字取 `accounts.Default?.Username` → `OfflineName` → `settings().LastUsername`（去掉 `ms:` 前缀）
     的首个非空值；`Credentials = null`。全空抛 `InvalidOperationException("请输入离线玩家名或选择账户")`。
2. `AppServices`：新增 `public LaunchAccountService LaunchAccount { get; }`，
   构造 `LaunchAccount = new LaunchAccountService(Accounts, MicrosoftAuth, () => Settings);`
   （放在 `MicrosoftAuth` 之后；闭包读 `Settings` 属性，`ReloadSettings` 后自动跟随）。
3. 验证：构建。

## T4：Core · PendingWorldStore（未决记录）

新文件：`dotnet/src/Starcloud.Core/Services/PendingWorldStore.cs`

```csharp
public sealed class PendingWorldRecord
{
    [JsonPropertyName("world_folder")]   public string WorldFolder { get; set; } = "";
    [JsonPropertyName("world_name")]     public string WorldName { get; set; } = "";
    [JsonPropertyName("source_version")] public string SourceVersion { get; set; } = "";
    [JsonPropertyName("target_version")] public string TargetVersion { get; set; } = "";
    [JsonPropertyName("source_game_root")] public string SourceGameRoot { get; set; } = "";
    [JsonPropertyName("target_game_root")] public string TargetGameRoot { get; set; } = "";
    [JsonPropertyName("copied")]         public bool Copied { get; set; }
    [JsonPropertyName("started_at")]     public DateTime StartedAt { get; set; }
    [JsonPropertyName("process_id")]     public int ProcessId { get; set; }
}

public sealed class PendingWorldStore(string dotMc)
{
    private string File => Path.Combine(dotMc, "starcloud", "pending-world.json");
    public PendingWorldRecord? Load();
    public void Save(PendingWorldRecord record);
    public void Clear();
}
```

- `Save`：`Directory.CreateDirectory(<dotMc>/starcloud)` + 原子写（temp + `File.Move(overwrite: true)`，
  照 `InstanceSettingsStore` 的写法）；`Load` 文件不存在或反序列化失败 → `null`。
- `Clear`：`File.Exists` 时删除。
3. 验证：构建。

## T5：App · HomePage 账户解析改调模块

文件：`dotnet/src/Starcloud.App/Views/HomePage.xaml.cs`

1. `Initialize()`（L50–57）：`SetMode(...)` 改为 `_s.LaunchAccount.Mode = _s.Settings.LastUsername is { } u && u.StartsWith("ms:") ? "microsoft" : "offline";`，
   `_mode` 字段删除，全部引用点改用 `_s.LaunchAccount.Mode`（`SetMode` 内的控件显示逻辑保留，
   只把「设置模式」这一步换成写模块）。
2. 账户下拉框切换时（`AccountCombo_SelectionChanged`）：`_s.Accounts.SetDefault(uuid)` 保留，
   同时把 `_s.LaunchAccount.OfflineName` / `Mode` 与控件状态保持同步（离线手输名写入 `OfflineName`）。
3. 删除 `ResolvePlayerName`（L656–666）与 `ResolveMicrosoftAccountAsync`（L671–693）。
4. `StartLaunchAsync`（L479–654）：
   - 前置校验段（L482–492）改为：`var player = await _s.LaunchAccount.ResolveAsync()` 的失败由
     既有 `try/catch` 承接；离线无名的快速提示保留（在校验前先查
     `_mode == "offline" && string.IsNullOrWhiteSpace(name)`）。
   - L499–509 的 `playerName` / `cred` 改由 `ResolvedPlayer` 提供。
   - 其余（覆盖层、预检、PreLaunch、Exited 回调、`App.RunningVersionId`、
     `store.TouchPlayed`、`LastUsername`/`LastVersion` 写回）**原样不动**。
5. 验证：构建 + 手工回归：主页正版 / 离线两条路径均可正常启动，账户下拉与离线名输入行为不变。

## T6：App · 存档页数据层与列表改造

文件：`dotnet/src/Starcloud.App/Views/PageRows.cs`、`SavesPage.xaml`、`SavesPage.xaml.cs`

1. `PageRows.cs` 中 `SaveRow`（L45–50）扩为：

```csharp
public sealed record SaveRow(string FolderName, string Name, string? IconPath,
    string LastPlayedText, string SizeText,
    string VersionLabel, bool Isolated, string GameRoot, string SearchKey)
{
    public bool HasIcon => IconPath is not null;
    public ImageSource? Icon => VersionRowHelpers.ResolveIconSource(IconPath);
    public Visibility IsolatedVisibility => Isolated ? Visibility.Visible : Visibility.Collapsed;
}
```

2. `SavesPage.xaml`：
   - 工具条左侧 `StackPanel` 前插两个按钮：
     `<Button x:Name="LaunchOtherBtn" Style="{StaticResource AccentButtonStyle}" Content="用其他版本启动" IsEnabled="False" Click="LaunchOther_Click" />`
     与 `<Button x:Name="CopyOtherBtn" Content="复制到其他版本" IsEnabled="False" Click="CopyOther_Click" />`；
     原 `BackupBtn` 去掉 `AccentButtonStyle`（让主操作只有一个）。
   - 工具条右侧 `StackPanel` 前插搜索框与版本筛选：

```xml
<AutoSuggestBox x:Name="SearchBox" Width="220" PlaceholderText="搜索世界名 / 文件夹名 / 版本"
                QueryIcon="Find" TextChanged="SearchBox_TextChanged" />
<ComboBox x:Name="VersionFilter" MinWidth="150" SelectionChanged="VersionFilter_SelectionChanged" />
```

   - `Grid.RowDefinitions` 由 4 行改 5 行：页头 / InfoBar(`OkBar`+`ErrorBar`) / **运行态 InfoBar(`RunBar`)** / 工具条 / 列表；
     相应 `Grid.Row` 编号后移（工具条 2→3，列表卡 3→4）。
   - 新增运行态：

```xml
<InfoBar x:Name="RunBar" Grid.Row="2" Severity="Informational" IsOpen="False" IsClosable="False"
         Title="正在运行" Message="">
    <InfoBar.ActionButton>
        <Button Content="停止游戏" Click="StopGame_Click" />
    </InfoBar.ActionButton>
</InfoBar>
```

   - 行模板名称行改为 `Grid` 两列（名称 + 徽章）：

```xml
<Grid ColumnSpacing="8">
    <Grid.ColumnDefinitions>
        <ColumnDefinition Width="Auto" />
        <ColumnDefinition Width="Auto" />
    </Grid.ColumnDefinitions>
    <TextBlock Text="{x:Bind Name}" FontWeight="SemiBold" TextTrimming="CharacterEllipsis" />
    <Border Grid.Column="1" CornerRadius="4" Padding="8,2" VerticalAlignment="Center"
            Background="{ThemeResource CardBackgroundFillColorSecondaryBrush}">
        <TextBlock Text="{x:Bind VersionLabel}" FontSize="11"
                   Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
    </Border>
    <Border Grid.Column="2" CornerRadius="4" Padding="8,2" VerticalAlignment="Center"
            Visibility="{x:Bind IsolatedVisibility}"
            Background="{ThemeResource CardBackgroundFillColorSecondaryBrush}">
        <TextBlock Text="隔离" FontSize="11" Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
    </Border>
</Grid>
```

     （三列，`ColumnDefinitions` 共 3 个 `Auto`；`Name` 列不参与 `*`，过长由 `TextTrimming` 截断。）
   - `EmptyHint` 的 `TextBlock` 命名（`EmptyTitle` / `EmptySubText`）以便按场景改文案。
3. `SavesPage.xaml.cs`：
   - `LoadSaves()`：改调 `_s.SaveLibrary.ListAll(_s.Settings.DotMinecraft, _s.Settings)`；
     映射 `SaveRow` 时 `VersionLabel = v.Isolated ? v.VersionId : "共享"`，
     `SearchKey = $"{v.LevelName} {v.FolderName} {v.VersionId}".ToLowerInvariant()`，
     `GameRoot = v.GameRoot`；填充版本筛选 `ComboBox`（`全部` / `共享` / `_s.SaveLibrary.ListVersionIds(dotMc)`），
     保持当前选中项不丢；最后调 `ApplyFilter()`。
   - 新增 `ApplyFilter()`：`_rows` → 按 `SearchBox.Text`（`SearchKey.Contains`，大小写不敏感）
     与 `VersionFilter`（`全部` 不过滤；`共享` 匹配 `VersionLabel == "共享"`；否则匹配 `VersionLabel == 选中项`）
     过滤 → `SaveList.ItemsSource`；空结果时 `EmptyHint` 显示并改文案为「没有匹配的存档 / 试试换个关键词或版本筛选」，
     无存档时用原文案。
   - `SearchBox_TextChanged` / `VersionFilter_SelectionChanged` → `ApplyFilter()`。
   - `OpenFolder_Click` / `Backup_Click` / `Delete_Click` 全部改用 `row.GameRoot`：
     `Path.Combine(row.GameRoot, "saves", row.FolderName)`；
     `Backup` 的 `backupsDir` = `Path.Combine(row.GameRoot, "backups")`。
   - `OpenSavesDir_Click` 保持打开共享根 `saves`（列表页级操作），文案不变。
   - `UpdateRowActions()` 增补 `LaunchOtherBtn.IsEnabled = has && !_busy` 、
     `CopyOtherBtn.IsEnabled = has && !_busy`。
4. 验证：构建 0 警告 0 错误；启动器运行后存档页能列出隔离版本存档并显示徽章（人工/UIA 抽查）。

## T7：App · 存档页跨版本启动（确认弹窗 + 复制 + 基础启动）

文件：`dotnet/src/Starcloud.App/Views/SavesPage.xaml.cs`

新增字段：`private Process? _gameProcess;`。

1. `LaunchOther_Click` / `CopyOther_Click` 共用 `ShowCrossVersionDialogAsync(bool startAfterCopy)`：
   - `if (SaveList.SelectedItem is not SaveRow row) return;`
   - 取 `dotMc = _s.Settings.DotMinecraft`；`store = new InstanceSettingsStore(dotMc)`；
     `versions = _s.SaveLibrary.ListVersionIds(dotMc)`；源标签 = `row.Isolated ? row.VersionId : "共享"`。
   - 目标候选 = `versions.Where(v => v != row.VersionLabel)`；为空 → `ErrorBar` 提示
     「没有其他已安装版本」并返回。
   - 弹 `ContentDialog`（`XamlRoot = Content.XamlRoot`），`Content` 为一个 `StackPanel`（`Spacing=12`）：
     - `InfoBar Severity="Warning" IsOpen="True"` Message =
       `此世界来自 {源标签}，将由 {目标版本} 打开。跨版本打开可能触发世界数据升级，且改动不可逆。`
     - `ComboBox`（目标版本，`ItemsSource = 候选`，`SelectedIndex = 0`）。
     - `TextBlock`「基础启动模式：不含文件校验与自动修复，失败请回首页启动。」
     - `TextBlock`「使用账户：{name}（{正版|离线}）」——账户名来自
       `await _s.LaunchAccount.ResolveAsync()`（只读展示；解析失败则显示错误并返回）。
     - `CheckBox`「我已了解跨版本风险」，`Checked/Unchecked` → `dlg.IsPrimaryButtonEnabled = ...`。
   - 按钮：`PrimaryButtonText = "复制并启动"`、`SecondaryButtonText = "仅复制"`、`CloseButtonText = "取消"`，
     `DefaultButton = Close`，`IsPrimaryButtonEnabled = false`。
   - **源根 == 目标根**（`InstanceLayout.GameRoot(dotMc, targetId, isIsolated) == row.GameRoot`）：
     改为只显示「启动」/「取消」，跳过复制步骤直接启动，弹窗内文案说明「同一游戏目录，无需复制」。
2. 复制（`startAfterCopy == true` 或点了「仅复制」）：
   - `targetIsolated = InstanceLayout.IsIsolated(store.Get(targetId), _s.Settings)`；
     `targetRoot = InstanceLayout.GameRoot(dotMc, targetId, targetIsolated)`；
     `targetSaves = Path.Combine(targetRoot, "saves")`，`Directory.CreateDirectory`。
   - 副本名：`$"{row.FolderName}-来自 {源标签}"`，重名走
     `InstanceLayout.NextAvailablePath(Path.Combine(targetSaves, 候选名))` 取最终名。
   - 复制在 `Task.Run` 中递归拷贝目录；期间 `_busy = true`、`BackupProgress.Visibility = Visible`、
     `UpdateRowActions()`；完成后复位。
   - 失败：`Directory.Delete(targetSaves/最终名, true)`（吞异常）→ `ErrorBar` → **不启动、不写记录**。
3. 基础启动（`startAfterCopy`）：
   - 前置校验：`App.RunningVersionId == targetId` → `ErrorBar`「该版本正在运行中」并返回。
   - `eff = InstanceLayout.Resolve(dotMc, targetId, store.Get(targetId), _s.Settings)`；
     `player = await _s.LaunchAccount.ResolveAsync()`；
     `options = new LaunchOptions(targetId, player.Name, eff.GameRoot, dotMc, eff.MinMem, eff.MaxMem,
     eff.Java, eff.ExtraJvmArgs, _s.Settings.UseBuiltinJvmArgs, player.Credentials, eff.ServerAddress,
     eff.Language, eff.LanguageOverride, eff.WindowW, eff.WindowH, eff.Fullscreen, eff.EnvVars,
     null, null)`（最后两个参数 = 不执行启动前/后命令，见计划期微修）。
   - **不调用** `_s.Preflight.CheckAsync` / `EnsureReadyAsync`。
   - `_gameProcess = await _s.Launcher.LaunchVersionAsync(dotMc, targetId, options);`
   - 成功后：`App.RunningVersionId = targetId`、`store.TouchPlayed(targetId)`、
     `new PendingWorldStore(dotMc).Save(new PendingWorldRecord { ... Copied = true, StartedAt = DateTime.Now, ProcessId = _gameProcess.Id })`、
     `RunBar.Message = $"正在以 {targetId} 运行「{row.Name}」"`、`RunBar.IsOpen = true`、`LoadSaves()`。
   - 失败：副本**保留**，`ErrorBar`「启动失败：{ex.Message}。副本已保留在 {targetId}，可稍后重试」，
     并写 `Copied = true` 的未决记录。
4. `_gameProcess.Exited` 挂 T8 的归属询问（T8 实现；T7 先留 `#region` 或直接在本任务一并接入）。
5. `StopGame_Click`：`try { _gameProcess?.Kill(true); } catch {}`，`RunBar.IsOpen = false`。
6. 验证：构建 0 警告 0 错误；手工跑通「选中共享存档 → 用其他版本启动 → 勾选 → 复制并启动」。

## T8：App · 退出归属三选一 + 中断补问

文件：`dotnet/src/Starcloud.App/Views/SavesPage.xaml.cs`

1. 抽 `private async Task AskWorldOutcomeAsync(PendingWorldRecord rec, bool copyStillOnDisk)`：
   - `XamlRoot` = `(App.MainWindow?.Content as FrameworkElement)?.XamlRoot`（**不依赖本页是否在前台**）。
   - `ContentDialog` 三选一：`PrimaryButtonText = "保存到当前版本"`、
     `SecondaryButtonText = "保留在原本的版本"`、`CloseButtonText = "丢弃本次改动"`，
     `DefaultButton = Primary`；`Content` 说明
     `「{WorldName}」在 {TargetVersion} 中的改动将如何处理？`，并在
     `copied == true` 但未启动（`process_id == 0` 或记录来自 T7 的失败分支）时先提示
     「已复制但未启动，是否保留」。
   - 三个分支：
     - **保存到当前版本**：不动磁盘（副本留在目标 `saves/`）。
     - **保留在原本的版本**：
       `planA = Path.Combine(rec.SourceGameRoot, "saves", rec.WorldFolder)`、
       `planB = Path.Combine(rec.TargetGameRoot, "saves", rec.WorldFolder + "-来自 " + 源标签)`；
       先 `_s.Saves.Backup(rec.SourceGameRoot, Path.Combine(rec.SourceGameRoot, "backups"), rec.WorldFolder)`；
       再 `_s.Saves.Delete(rec.SourceGameRoot, rec.WorldFolder)`；
       再递归复制 `副本目录` → `planA`；最后 `_s.Saves.Delete(rec.TargetGameRoot, 副本文件夹名)`。
       任一失败 → `ErrorBar` 给出备份 zip 路径并提示副本仍在目标版本。
     - **丢弃本次改动**：删目标副本（`_s.Saves.Delete(rec.TargetGameRoot, 副本文件夹名)`）。
   - 结束后 `new PendingWorldStore(dotMc).Clear()` + `LoadSaves()`。
2. `Exited` 回调（T7 的 `_gameProcess.Exited`）：
   - `DispatcherQueue.TryEnqueue(async () => { ... })`：`App.RunningVersionId = null`、
     `RunBar.IsOpen = false`、读未决记录；源根 == 目标根则直接清记录并刷新，**不弹窗**；
     否则 `await AskWorldOutcomeAsync(rec, copyStillOnDisk)`。
3. 中断补问：`Loaded` 里在 `LoadSaves()` **之前**检查
   `new PendingWorldStore(_s.Settings.DotMinecraft).Load()`：
   - 有残留 → 校验目标副本目录是否仍存在；
     - 不存在 → `Clear()`，跳过；
     - 存在 → `await AskWorldOutcomeAsync(rec, true)`。
   - `Loaded` 回调改为 `async (_, _) => { await ResumePendingAsync(); LoadSaves(); }`（`ResumePendingAsync` 内部吞异常）。
4. 验证：构建 0 警告 0 错误；手工验证「运行中强杀启动器 → 重启进存档页补问」。

## T9：App · 主窗口删底部 Nova / Orb 改色 / 个人中心入口

文件：`dotnet/src/Starcloud.App/MainWindow.xaml`、`MainWindow.xaml.cs`、`Views/HomePage.xaml.cs`

1. `MainWindow.xaml`：
   - `Grid.RowDefinitions` 删一行 → 3 行（48 / * / Auto）。
   - 删 `NovaBar` 整块（L122–167）。
   - 状态栏 `Border` 的 `Grid.Row="3"` → `"2"`；删右侧 `StackPanel` 内的
     `NovaStateDot` 与 `NovaStateText`（整段删，状态栏只留 `InstanceText`）。
   - `NovaOrb`（L28–64）改造：
     - 从 `Grid.Row="0" HorizontalAlignment="Right" Margin="0,0,140,0"` 改为放进新的右侧 `StackPanel`；
     - 删 `<Grid.Resources>` 内的两套 `AcrylicBrush`，`Border` 的
       `Background` 改 `{ThemeResource CardBackgroundFillColorDefaultBrush}`，保留 1px 描边；
     - 保留 `NovaOrbGlyph` 与 `NovaOrbDot`。
   - 右侧并列容器（放在 `NovaOrb` 原位置，`Grid.Row="0"`）：

```xml
<StackPanel Orientation="Horizontal" Spacing="6"
            HorizontalAlignment="Right" VerticalAlignment="Center" Margin="0,0,140,0">
    <!-- NovaOrb（原 Button 整体搬入） -->
    <!-- 个人中心按钮 -->
    <Button x:Name="AccountButton" Width="44" Height="36" Padding="0" CornerRadius="8"
            Background="Transparent" BorderThickness="0"
            AutomationProperties.Name="个人中心"
            ToolTipService.ToolTip="个人中心"
            Click="AccountButton_Click">
        <Grid Width="32" Height="32">
            <Ellipse Fill="{ThemeResource CardBackgroundFillColorSecondaryBrush}" />
            <TextBlock x:Name="AccountAvatarText" FontSize="13" FontWeight="SemiBold"
                       HorizontalAlignment="Center" VerticalAlignment="Center" />
            <FontIcon x:Name="AccountAvatarGlyph" Glyph="&#xE77B;" FontSize="15"
                      Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
        </Grid>
    </Button>
</StackPanel>
```

   - 在 `Popup` 之后加提示条：

```xml
<TeachingTip x:Name="NovaToastTip" Grid.Row="0" IsOpen="False"
             PreferredPlacement="Bottom" IsLightDismissEnabled="True" Title="Nova" />
```

2. `MainWindow.xaml.cs`：
   - 删 `NovaBarToggle_Click`（L95–100）。
   - `NovaOrb_Click`（L84–92）拆出 `ShowNovaPopup()`，并把 offset 改为按 Orb 实际位置计算：

```csharp
private void ShowNovaPopup()
{
    var root = Content as FrameworkElement;
    if (root is null) return;
    var p = NovaOrb.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(0, 0));
    NovaPopup.HorizontalOffset = Math.Max(12, Math.Min(p.X, root.ActualWidth - 380 - 12));
    NovaPopup.VerticalOffset = 48 + 8;
    NovaPopup.IsOpen = true;
}

private void NovaOrb_Click(object sender, RoutedEventArgs e)
{
    if (NovaPopup.IsOpen) { NovaPopup.IsOpen = false; return; }
    ShowNovaPopup();
}

public void OpenNovaPanel() => ShowNovaPopup();
```

   - 删 `OpenNovaBar()`（L103–107）；`HandleJoinUrlAsync`（L110–114）改调 `ShowNovaPopup()`。
   - `_toastResetTimer.Tick`（L34–38）改为 `NovaToastTip.IsOpen = false;`（不再写 `NovaStateText`）。
   - `OnOverlayToast`（L141–147）改为：

```csharp
private void OnOverlayToast(NovaOverlayToast toast) => DispatcherQueue.TryEnqueue(() =>
{
    NovaToastTip.Subtitle = toast.Text;
    NovaToastTip.IsOpen = false;
    NovaToastTip.IsOpen = true;
    _toastResetTimer.Stop();
    _toastResetTimer.Interval = TimeSpan.FromSeconds(8);
    _toastResetTimer.Start();
});
```

   - `ApplyNovaState`（L150–166）：删 `NovaStateText.Text/Foreground` 与 `NovaStateDot.Fill` 两段，
     保留 `NovaOrbGlyph.Foreground` / `NovaOrbDot.Visibility` / ToolTip /
     `NovaPanelLogin` / `NovaPanelChat` 切换；末尾追加 `RefreshAccountButton();`。
   - 新增个人中心：

```csharp
private void AccountButton_Click(object sender, RoutedEventArgs e) => NavigateTo("accounts");

private void RefreshAccountButton()
{
    var session = AppServices.Instance.StarcloudId.Current;
    var logged = session is not null;
    AccountAvatarText.Text = logged ? session!.DisplayName[..1] : "";
    AccountAvatarText.Visibility = logged ? Visibility.Visible : Visibility.Collapsed;
    AccountAvatarGlyph.Visibility = logged ? Visibility.Collapsed : Visibility.Visible;

    var tip = new StackPanel { Spacing = 2 };
    tip.Children.Add(new TextBlock { Text = logged ? session!.DisplayName : "未登录", FontWeight = FontWeights.SemiBold });
    if (logged)
    {
        tip.Children.Add(new TextBlock { Text = session!.UserId, FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
        tip.Children.Add(new TextBlock { Text = $"已登录 · {(session.Channel == StarcloudIdService.CloudChannel ? "云端通道" : "测试通道")}",
            FontSize = 12, Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
    }
    else
    {
        tip.Children.Add(new TextBlock { Text = "点击前往账户页登录 Starcloud ID", FontSize = 12,
            Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"] });
    }
    ToolTipService.SetToolTip(AccountButton, tip);
}
```

   - 构造函数里：`NovaToastTip.Target = NovaOrb;`、`RefreshAccountButton();`，
     并订阅 `Activated += (_, _) => RefreshAccountButton();`（登录后回到窗口即刷新）。
   - `HomePage.xaml.cs` 的 `AskNova_Click`：`OpenNovaBar()` → `OpenNovaPanel()`（`OnOverlayToast` 之外的
     另一处调用点，需一起改）。
3. 验证：构建 0 警告 0 错误；
   - 窗口底部无 Nova 对话条、状态栏只剩 `实例：xxx`；
   - 右上角 Orb 为实心卡片色，点开面板位置跟随 Orb；旁边个人中心按钮悬浮显示昵称/ID/登录态，
     点击进入账户页；未登录时显示人形图标。

## 构建与验收

每任务后：

```
Stop-Process -Name Starcloud.App -Force -ErrorAction SilentlyContinue
dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo
```

最终按 spec §8 的 11 条验收标准逐条人工验证，重点：

1. 隔离版本存档可见 + 徽章正确（隔离版显示版本 Id + 「隔离」，共享版显示「共享」）。
2. 搜索 × 版本筛选 AND 生效；筛无结果文案区分。
3. 跨版本复制启动成功，副本名 `-来自 1.20.1`；重名加 ` (2)`。
4. 退出三选一：三档行为与「先 zip 兜底再替换」；
5. 运行中强杀启动器 → 重启进存档页补问。
6. 「打开世界文件夹 / 备份 / 删除」对隔离版本存档落在正确目录。
7. 主页账户区界面与行为无变化（回归）。
