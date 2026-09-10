# 版本管理增强（A 阶段）实现计划

日期：2026-09-09
设计文档：docs/superpowers/specs/2026-09-09-version-management-design.md（commit 322346e，
含计划期微修：memory_min 对齐现有全局设置、新增 last_played_at 元数据字段）

构建命令：先 `Stop-Process -Name Starcloud.App -Force -ErrorAction SilentlyContinue`，
再 `dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo`
（每个任务完成后构建验证，目标 0 警告 0 错误）

前置确认（已核实，实现时不再重复探索）：

- `LaunchOptions`（GameLauncher.cs:11）现有字段：VersionId/PlayerName/GameDir/
  MinMemoryMb/MaxMemoryMb/JavaExecutable/ExtraJvmArgs/UseBuiltinJvmArgs/
  Credentials/QuickPlayMultiplayer/Language；`game_directory` 变量与进程工作目录
  均取 `options.GameDir`，但 `dotMc = options.GameDir` 同时被用于 libraries/
  assets/natives/classpath——**需拆分共享根与 GameRoot**
- 全局默认隔离已存在：`LauncherSettings.VersionIsolated`（bool，默认 true = 独立；
  NovaService.cs:543 已按此语义使用），**只补 SettingsPage UI，不加新 JSON 字段**
- 全局内存 min/max 已存在：`DefaultMinMemoryMb`(512)/`DefaultMaxMemoryMb`(4096)，
  版本层未覆盖时直接取全局值（spec 已微修对齐）
- HomePage.xaml.cs:477 构建 LaunchOptions 传 `_s.Settings.DotMinecraft` 与全局
  内存/Java/额外 JVM；预检 `Preflight.CheckAsync(dotMc, versionId)` 固定扫共享根
  （正确，不动）；`LaunchVersionAsync` 内 `EnsureFirstRunLanguage` 仅首启写 options.txt
- `JavaService.ListRuntimesAsync(ct, runtimeDir?)` → `JavaRuntime(Executable,
  MajorVersion, VersionString, Is64Bit, …)`；`InstalledVersionManager.Remove`
  删整个 `versions/<id>/` 目录（isolated 内容随之删除）
- `LocalVersionRow`（PageRows.cs:31）为 `(Id, KindText, SizeText)` 三元 record
- 无任何「上次启动时间」存储 → 新增 `last_played_at` 元数据（启动成功后写入）
- 版本 ≥ 1.13 窗口参数 / ≥ 1.20 quickPlay 判断：正则 `^1\.(\d+)` 解析版本 ID 主次版本；
  不匹配（快照 / 特殊命名 / 带 loader 后缀如 `1.20.1-fabric` 仍匹配前缀）视为现代版
- UIA 冒烟约束：PowerShell 5.1、脚本中文必须 UTF-8 BOM、控件需
  AutomationProperties.Name

## T1：Core · InstanceSettingsStore + InstanceLayout

新文件：`dotnet/src/Starcloud.Core/Services/InstanceSettings.cs`

1. `VersionProfile`（System.Text.Json 序列化，snake_case PropertyName）：

```csharp
public sealed class VersionProfile
{
    public string? Isolation { get; set; }          // "shared"|"isolated"|null
    public int? MemoryMinMb { get; set; }
    public int? MemoryMaxMb { get; set; }
    public string? JavaPath { get; set; }
    public int? WindowWidth { get; set; }
    public int? WindowHeight { get; set; }
    public bool? Fullscreen { get; set; }
    public List<string> ExtraJvmArgs { get; set; } = [];
    public List<string> ExtraGameArgs { get; set; } = [];
    public string? ServerAddress { get; set; }
    public Dictionary<string, string> EnvVars { get; set; } = [];
    public string? PreLaunchCommand { get; set; }
    public string? PostLaunchCommand { get; set; }
    public string? LanguageOverride { get; set; }
    public string? IconPath { get; set; }
    public string? Category { get; set; }
    public string? Note { get; set; }
    public DateTime? LastPlayedAt { get; set; }
}
```

2. `InstanceSettingsStore`：构造入参 `dotMc`；文件 `<dotMc>/starcloud/version-settings.json`
   （`{"version":1,"profiles":{id:profile}}`）；`Load()` 损坏时静默返回空；
   `Save()` 原子写（temp + File.Move overwrite）；`Get(id)`（无则 new 空档案，不入字典）、
   `Set(id, profile)`；`TouchPlayed(id)`（LastPlayedAt = Now + Save）；
   `RenameKey(oldId,newId)` / `Remove(id)`
3. `InstanceLayout` 静态类：
   - `ContentDirs = ["mods","saves","config","resourcepacks","shaderpacks"]`
   - `IsIsolated(profile, settings)`：profile.Isolation switch（null →
     settings.VersionIsolated；"shared" → false；"isolated" → true）
   - `GameRoot(dotMc, versionId, isolated)`：isolated → `versions/<id>/`，否则 dotMc
   - `EffectiveLaunch Resolve(dotMc, versionId, profile, settings)`：record 输出
     GameRoot / Isolated / MinMem / MaxMem / Java / WindowW / WindowH / Fullscreen /
     ServerAddress / Language / ExtraJvmArgs（全局+版本拼接）/ ExtraGameArgs /
     PreCmd / PostCmd / EnvVars
   - `Task MoveContentDirsAsync(string fromRoot, string toRoot, Action<string> log)`：
     对 5 个内容目录，源存在且非空才移动；目标重名 → 双方保留，目标加 ` (2)` 后缀
     （同 `name (2).ext` 规则递增）；失败 → 已移动项移回并 rethrow
   - `static void RenameInstance(dotMc, oldId, newId)`：校验 newId 非空且不含
     `< > : " / \ | ? *`；`versions/<old>/` 目录整体 Move；目录内
     `<old>.json` / `<old>.jar` 同步改名；JsonDocument 读 json 改 `id` 字段写回；
     校验失败 throw ArgumentException
   - `static void CopyInstance(dotMc, oldId, newId, bool copyContent)`：json 复制后
     改 id；jar 复制（存在才拷）；copyContent 为真 → 复制 5 个内容目录（重名规则同上）
4. 验证：构建通过

## T2：Core · GameLauncher 启动链路扩展

文件：`dotnet/src/Starcloud.Core/Services/GameLauncher.cs`

1. `LaunchOptions` 新增字段（保持旧位置参数兼容，新字段全部带默认值）：
   `string SharedDir`（插在 GameDir 后，调用方 T3 同步改）、
   `string? LanguageOverride = null`、`int? WindowWidth/WindowHeight = null`、
   `bool? Fullscreen = null`、`IReadOnlyDictionary<string,string>? EnvVars = null`、
   `string? PreLaunchCommand/PostLaunchCommand = null`
2. `BuildCommandLineAsync` 拆分：
   - `var dotMc = options.SharedDir;`（libraries/assets/natives/classpath/jar 全走共享根）
   - `vars["game_directory"] = options.GameDir;`（GameRoot）
   - 新代补全路径 `--gameDir` 用 `options.GameDir`（line 158）
   - `resolution_width/height`：`options.WindowWidth/Height ?? 1280/720`
   - gameArgs 追加尾部：静态 `ModernWindowArgs(versionId)`（`^1\.(\d+)`，主版本 < 13
     返回 false，不匹配返回 true）为真且 WindowWidth/Height 有值 →
     `--width <w> --height <h>`；Fullscreen == true → `--fullscreen`
3. `Launch(commandLine, workingDirectory, envVars = null)`：envVars 非空 →
   逐项 `psi.Environment[k] = v`
4. `LaunchVersionAsync`：
   ```csharp
   Directory.CreateDirectory(options.GameDir);
   var lang = options.LanguageOverride ?? options.Language;
   EnsureFirstRunLanguage(options.GameDir, lang);
   if (options.LanguageOverride is not null) PatchLanguage(options.GameDir, options.LanguageOverride);
   ```
   `PatchLanguage`：读 options.txt 全行，替换/追加 `lang:` 行（失败静默）
5. 新增 `Task<(int ExitCode, string Output)> RunPreLaunchAsync(LaunchOptions options)`：
   命令为空 → (0,"")；否则 `cmd.exe /c <命令>`（`$GAME_DIR` 替换为 GameRoot），
   WorkingDirectory = GameDir，注入 EnvVars，重定向输出捕获；返回退出码
6. 新增 `void RunPostLaunch(LaunchOptions options)`：同步 fire-and-forget 由调用方
   包裹；命令非空才执行；异常追加写 `LauncherPaths.LogsDir/game-postlaunch.log`
7. 验证：构建通过

## T3：App · HomePage 启动链路接入 EffectiveLaunch

文件：`dotnet/src/Starcloud.App/Views/HomePage.xaml.cs`、
`dotnet/src/Starcloud.App/App.xaml.cs`（或 AppServices.cs）

1. `App` 静态字段：`public static string? PendingLaunchVersionId;`（版本页/详情页
   请求启动的交接）、`public static string? RunningVersionId;`（运行中判定）
2. 从 `LaunchBtn_Click` 抽出 `StartLaunchAsync(string versionId)`（第一击逻辑整体
   搬入，LaunchBtn_Click 保留第二击停止 + 调用 StartLaunchAsync）
3. OnNavigatedTo / Loaded：`App.PendingLaunchVersionId` 非空 → 取出置 null、
   VersionList 选中该项、调 `StartLaunchAsync`
4. `StartLaunchAsync` 内 LaunchOptions 构造改为：
   ```csharp
   var store = new InstanceSettingsStore(dotMc);
   var eff = InstanceLayout.Resolve(dotMc, versionId, store.Get(versionId), _s.Settings);
   var options = new LaunchOptions(versionId, playerName, eff.GameRoot, dotMc,
       eff.MinMem, eff.MaxMem, eff.Java, eff.ExtraJvmArgs, _s.Settings.UseBuiltinJvmArgs,
       cred, eff.ServerAddress, eff.Language, eff.LanguageOverride,
       eff.WindowW, eff.WindowH, eff.Fullscreen, eff.EnvVars, eff.PreCmd, eff.PostCmd);
   ```
5. `ApplyStage(StartingProcess)` 后、`LaunchVersionAsync` 前：
   `var pre = await _s.Launcher.RunPreLaunchAsync(options);` 退出码 ≠ 0 →
   ContentDialog（标题「启动前命令失败」+ 输出尾部 + 「仍然启动 / 取消」，取消走
   HideOverlay(null) return）
6. 启动成功（`_runningStart = TickCount64` 附近）：`App.RunningVersionId = versionId;`
   + `store.TouchPlayed(versionId);`
7. Exited 回调收尾：`App.RunningVersionId = null;` + try-catch 包裹
   `_s.Launcher.RunPostLaunch(options);`
8. 验证：构建通过 + 手工启动一次共享版本确认行为不变（GameRoot = dotMc 路径同前）

## T4：App · 版本列表页升级

文件：`dotnet/src/Starcloud.App/Views/PageRows.cs`、
`Views/VersionsPage.xaml`、`Views/VersionsPage.xaml.cs`

1. `LocalVersionRow` 扩展：`(string Id, string KindText, string SizeText,
   string IconPath, string Category, bool Isolated, string LastPlayedText,
   string SearchKey)`（SearchKey = Id+Category 小写，供过滤）
2. XAML 工具行：搜索 TextBox（`SearchBox`，TextChange 过滤）+ 分类 ComboBox
   （`CategoryFilter`：全部 / 未分类 / 动态去重类别集合）
3. 行模板重排：左 40×40 图标（IconPath 有值 → Image + BitmapImage，异常回退
   FontIcon E7E8）+ ID（SemiBold）+ 第二行 KindText + 类别徽章 Border + 「独立」徽章
   （Isolated 时显示）；右列 SizeText + LastPlayedText + 「…」更多按钮
4. 菜单（右键 ContextFlyout 与「…」共用同一 MenuFlyout 资源）：
   启动 / 版本详情 / 打开目录 / 重命名 / 复制版本 / 查看 JSON / 导出整合包
   （IsEnabled=false，ToolTip「C 阶段提供」）/ 问 Nova / 删除
5. 交互：
   - 「启动」：`App.PendingLaunchVersionId = id; MainWindow.NavigateTo("home");`
   - 「版本详情」/ 单击行：`ContentFrame.Navigate(typeof(VersionDetailPage), id);`
     （ListView `IsItemClickEnabled="True"` + `ItemClick`，不用 SelectionChanged）
   - 「打开目录」：Process.Start 该版本 GameRoot（经 InstanceLayout 判断）
   - 重命名 / 复制 / 查看 JSON / 删除：T7 实现共用逻辑，本任务先接空实现跳转
6. RefreshList 改为读 store：LastPlayedAt → 「今天 14:32 / 3 天前 / 从未启动」
   （私有 FormatLastPlayed）
7. 验证：构建通过

## T5：App · VersionDetailPage 骨架 + 概览 / 内容 / 操作卡片

新文件：`dotnet/src/Starcloud.App/Views/VersionDetailPage.xaml`、`.xaml.cs`

1. 导航：`OnNavigatedTo` 取 `e.Parameter as string` → `_versionId`；
   NavigationCacheMode="Disabled"；顶部返回按钮 `Frame.GoBack()` +
   AutomationProperties.Name="返回版本列表"
2. 布局（ScrollViewer 内单列 StackPanel MaxWidth 1000，Spacing 16，样式对齐现有页）：
   - 头部 Grid：56×56 图标 + 换图标小按钮（FileOpenPicker 选 png/jpg/ico → 复制到
     `<dotMc>/starcloud/icons/<id>.<ext>` → profile.IconPath 存相对路径 +
     Save）+ ID 大字 + 类别/隔离徽章 + 主按钮「启动」（同 T4 启动语义）+
     黄色 InfoBar `RunningBanner`（`App.RunningVersionId == _versionId` 时显示）
   - 卡片「概览」：GameRoot 路径（InstanceLayout 实时解析）、上次启动时间、
     类别 TextBox（`CategoryBox`）、备注 TextBox（`NoteBox`）
   - 卡片「内容」：5 个内容目录行（图标 + 目录名 + 存在/不存在灰字 + 「打开目录」）
   - 卡片「操作」：验证文件 / 重命名 / 复制版本 / 查看 JSON / 删除（删除红字样式）
3. `RefreshAll()`：载入 profile → 全控件赋值（T6 完成设置卡片部分）
4. 「验证文件」：`_s.Preflight.CheckAsync(dotMc, _versionId)` → 无需修复弹「一切正常」；
   NeedsRepair → 弹「发现 N 个缺失文件，立即补全？」→ 确定则
   `EnsureReadyAsync`（Progress 对话框更新，完成后成功提示）
5. 「删除」：沿用 VersionsPage 确认文案 + isolated 追加「将同时删除独立目录下的
   mods/存档等」→ `InstalledVersionManager.Remove` + `store.Remove(id)` + Frame.GoBack()
6. 验证：构建通过

## T6：App · 「游戏设置」卡片（字段全集 + 交互规则）

文件：`Views/VersionDetailPage.xaml`、`.xaml.cs`

1. XAML 卡片「游戏设置」（控件与 spec §6.2 一一对应，全部加 AutomationProperties.Name）：
   - 隔离 RadioButtons `IsolationRadio`（跟随全局默认 / 共享目录 / 独立目录）+
     灰字 `GameRootPreview`
   - 内存 NumberBox `MinMemBox`/`MaxMemBox`（Minimum 512 Maximum 131072
     SmallChange 512）+ 行下红字 `MemErrorText`
   - Java ComboBox `JavaCombo`（首项「跟随全局」）+ 「浏览」按钮
   - 窗口 NumberBox `WidthBox`/`HeightBox`（200–32767）+ 全屏 RadioButtons
     `FullscreenRadio`（跟随全局 / 开 / 关）
   - 连服 TextBox `ServerBox` + `ServerHintText`（灰/橙提示）
   - 环境变量：ListView `EnvList`（行 = 名称 TextBox + 值 TextBox + 删除按钮）+
     「添加变量」按钮 + 黄字 `EnvWarnText`
   - 前后命令 TextBox `PreCmdBox`/`PostCmdBox` + 灰字说明（$GAME_DIR、失败弹窗语义）
   - 语言 ComboBox `LangCombo`（跟随全局 / zh_CN / zh_TW / en_US / ja_JP）+
     灰字 `LangHintText`
   - 额外参数 TextBox `ExtraJvmBox`/`ExtraGameBox`（AcceptsReturn=true，每行一个）
2. code-behind 规则（spec §6.1）：
   - 载入：控件值 = 覆盖值 ?? 全局生效值；有全局层字段行尾「恢复跟随」HyperlinkButton
     （仅覆盖时可见），点击清字段 + 刷新显示 + ScheduleSave
   - 保存：所有变更走 `ScheduleSave()`——DispatcherTimer 500ms 单发；
     `OnNavigatedFrom` 强制 flush（store.Set + Save，含类别/备注/图标）
   - 校验：NumberBox 越界/Value 为 NaN → 红框（官件自带）+ MemErrorText；
     min > max 双红框不写入；连服正则 `^[^\s:]+(:\d{1,5})?$` 且 port ≤ 65535；
     env 名称 `^[A-Za-z_][A-Za-z0-9_]*$`，重名黄字提示合并
   - 隔离切换：SelectionChanged → 解析目标 GameRoot；与当前不同 → 迁移
     ContentDialog（标题「切换版本隔离」，正文列已存在内容目录，按钮
     立即移动(默认) / 仅切换不移动 / 取消）；「取消」还原 RadioButtons；
     「立即移动」→ `MoveContentDirsAsync`（IsolatedDirectory 操作期间
     IsolationRadio + 重命名禁用）；`App.RunningVersionId == _versionId` →
     整组禁用
   - Java「浏览」：FileOpenPicker 取 java.exe → `Process -version 解析主版本`
     （复用/补 JavaService 检测能力）→ 成功加入下拉选中（profile.JavaPath =
     绝对路径），失败 ContentDialog
   - 连服提示：`^1\.(\d+)` ≥ 20 → 灰字 quickPlay 说明；< 20 → 橙字「不支持」
   - 语言：选中「跟随全局」写 null；其余写值 + LangHintText 显示全局当前语言
   - 额外参数：TextBox.Text ↔ List<string>（按行 Split，Trim，去空行）
3. 验证：构建通过

## T7：App · 重命名 / 复制 / 查看 JSON（与 T4 菜单接线）

文件：`Views/VersionDetailPage.xaml.cs`、`Views/VersionsPage.xaml.cs`

1. 重命名（两处共用）：ContentDialog + 新 ID 输入框 →
   `InstanceLayout.RenameInstance(dotMc, old, new)`（try-catch 显示异常）→
   `store.RenameKey(old, new)` → 刷新（详情页内改名后更新 `_versionId` 与头部）
2. 复制：ContentDialog（新 ID + CheckBox「同时复制 mods/存档等隔离内容」，
   仅源 isolated 时可见）→ `InstanceLayout.CopyInstance` + store 深拷贝档案
   （LastPlayedAt/IconPath 置空）
3. 查看 JSON：ContentDialog 内 ScrollViewer + 只读 TextBox（JsonNode 解析后
   WriteIndented 展示，解析失败显示原文）+ 「打开所在文件夹」按钮
4. 运行中（App.RunningVersionId 匹配）或下载中（无全局下载态可查，本期以运行中
   为准）→ 两操作按钮禁用
5. 验证：构建通过

## T8：App · SettingsPage 全局默认隔离模式

文件：`dotnet/src/Starcloud.App/Views/SettingsPage.xaml`、`.xaml.cs`

1. SectionGame 内存卡片下新增「版本隔离」卡片：RadioButtons `IsolationDefaultRadio`
   （独立目录（推荐）/ 共享目录）→ `settings.VersionIsolated`（独立 = true）
2. 载入 / 保存挂现有 settings 读写流（沿用 SavedBar 反馈）
3. 验证：构建通过

## T9：整体验证

1. 构建：0 警告 0 错误
2. UIA 冒烟（新脚本 `dotnet/uia-version-detail.ps1`，PowerShell 5.1 + UTF-8 BOM，
   System.Windows.Automation）：
   - 启动应用 → 侧栏「版本」（SelectionItemPattern）→ 列表第一项
     （SelectionItemPattern.Select + ItemClick 语义）→ 详情页加载断言
     （头部版本 ID 文本 + 各卡片存在）
   - 内存 MaxMemBox 改值（ValuePattern）→ 返回 → 重进详情 → 值保留（持久化断言）
   - 「返回版本列表」按钮存在可点
3. 手工验证清单（留给用户）：
   - isolated 版本启动：`versions/<id>/` 下生成 saves/config 等，logs/截图确认
     game_directory 正确；共享版本启动行为与改造前一致
   - 隔离切换：双向迁移对话框出现；造重名文件验证 ` (2)` 后缀；取消可还原
   - 重命名 / 复制 / 删除后 versions 目录、version-settings.json、列表三者一致
   - 连服地址启动 1.20+ 版本直连服务器；旧版本显示「不支持」提示
   - 启动前命令（如 `echo ok > $GAME_DIR\pre.txt`）验证执行与失败弹窗；
     环境变量注入后游戏内可见
