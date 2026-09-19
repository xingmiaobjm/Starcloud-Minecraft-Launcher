# 存档跨版本管理设计

日期：2026-09-10
状态：设计已确认（分节确认完成），待写实现计划

## 1. 背景与问题

用户诉求：**存档界面可以读取所有游戏版本内的存档并显示出来，显示出具体在哪个版本下，
以及一键使用其他版本启动。**

现状问题：

1. `SavesPage` 只调用 `SaveService.ListSaves(_s.Settings.DotMinecraft)`，即**只扫共享根
   `<.minecraft>/saves`**。版本隔离开启后，隔离版本的存档位于
   `versions/<版本名>/saves`，在存档页**完全不可见**，用户只能手动去文件夹里翻。
2. 存档列表**没有任何版本归属信息**，无法判断某个世界属于哪个版本。
3. 存档页的「打开世界文件夹 / 备份 / 删除」都把 `_s.Settings.DotMinecraft` 硬编码为
   路径基准，对隔离版本存档会操作到错误目录。
4. 没有「用另一个版本打开这个世界」的能力。

根因是**版本隔离模型**：`InstanceLayout.GameRoot(dotMc, versionId, isolated)` 在隔离时为
`dotMc/versions/<id>/`，共享时为 `dotMc`；对应的存档目录分别是
`versions/<id>/saves` 与 `.minecraft/saves`。任何跨版本存档视图都必须以「实例根」而不是
「dotMinecraft」为单位遍历。

本 spec 额外包含两项用户在澄清中追加的界面级需求：

- 删除启动器底部的 Nova（对话条 + 状态栏 Nova 指示）。
- 右上角 Nova 按钮改为不透明中性卡片色，旁边新增「个人中心」入口。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| 启动语义 | **复制启动**：复制一份存档到目标版本后再启动 |
| 退出后归属 | **三选一弹窗**：保存到当前（目标）版本 / 保留在原本的版本 / 丢弃 |
| 列表组织 | **平铺 + 版本徽章 + 顶部版本筛选下拉** |
| 跨版本风险提示 | **提示 + 必须勾选确认**才能启动 |
| 中断兜底 | **记未决记录**（`starcloud/pending-world.json`），下次打开存档页补问 |
| 整体方案 | **方案 A（世界启动会话）** + 提醒这是「基础启动模式」 + 方案 C 的「仅复制」能力 |
| 基础启动模式 | **轻量状态 + 风险说明**：不做预检与自动修复，仅极简运行态 + 提示 |
| 入口形式 | **工具栏按钮**，点击弹窗选择用哪个版本启动；**存档页支持搜索存档** |
| 副本命名 | **带来源后缀**（`世界名-来自 1.20.1`） |
| 底部 Nova | **删除**（对话条 + 状态栏 Nova 指示都删） |
| 右上角背景 | 改为**中性卡片色**（实心，非 Acrylic） |
| 个人中心入口 | 右上角新增按钮，**并入账户页面**（本地实现）；悬浮卡显示 ID / 头像 / 昵称 / 登录态，用户头像作为按钮图标，风格类似 bilibili；点击跳 `accounts` 页 |
| 主页账户区 | **界面与行为完全不变** |
| 官网外链 | **本次不做**（个人中心暂不加外链） |
| 账户模块化 | 把账户独立为一个模块，**启动页与临时启动共用**；临时启动弹窗内**只显示、不可切换** |
| 成果回传 | **用副本替换源**：先把源存档 zip 备份进 `backups/` 兜底，再用副本内容替换源存档目录，最后删掉目标版本副本 |

## 3. 数据模型

### 3.1 SaveInfo 扩展

`SaveService.SaveInfo` 增加三个归属字段，用于定位与展示：

```csharp
public sealed record SaveInfo(
    string FolderName,
    string LevelName,
    DateTime? LastPlayed,
    long SizeBytes,
    string? IconPath,
    string VersionId,   // 所属版本 Id；"" 表示共享根
    bool Isolated,      // 所属版本是否隔离
    string GameRoot);   // 存档所在实例根，打开文件夹 / 备份 / 删除一律以此为准
```

### 3.2 SaveService 改为「认实例根」

现有 API 以 `dotMinecraft` 为基准并自行拼 `saves`，必须改为由调用方传入
**实例根 + 归属信息**，否则隔离版本存档无法正确操作：

```csharp
public List<SaveInfo> ListSaves(string gameRoot, string versionId, bool isolated)
public string Backup(string gameRoot, string backupsDir, string folderName)
public void Delete(string gameRoot, string folderName)
```

- `ListSaves` 扫 `Path.Combine(gameRoot, "saves")`，为每个条目填 `VersionId` / `Isolated` /
  `GameRoot`。
- `Backup` 的 `backupsDir` 由调用方给出（共享根场景为 `<dotMc>/backups`，隔离版本场景为
  `<dotMc>/versions/<id>/backups`），保持「备份跟着实例走」。
- `NbtReader` / `NbtCompound` 等 NBT 解析代码**不动**。

影响面：`SaveService` 目前仅被 `SavesPage` 使用；`NovaService` 虽注入 `_saves` 但从未调用，
改签名无行为回归。

### 3.3 SaveLibraryService（新）

聚合所有实例的存档，供存档页一次性拉取。

```csharp
public sealed class SaveLibraryService(ManifestService manifest)
{
    public List<SaveInfo> ListAll(LauncherSettings settings);
}
```

扫描范围：

1. **共享根**：`<dotMc>/saves` → `VersionId = ""`，`Isolated = false`，`GameRoot = dotMc`。
2. **各已装版本**：`manifest.ListInstalled(dotMc)` 逐个取 `VersionProfile`，用
   `InstanceLayout.IsIsolated(profile, settings)` 判断隔离：
   - 隔离 → 扫 `<dotMc>/versions/<id>/saves` → `VersionId = id`，`Isolated = true`。
   - 非隔离 → 其游戏根就是共享根，**跳过**（否则与第 1 步重复）。
3. 合并结果，按 `LastPlayed` 倒序（无时间戳排最后）。

**取舍（已向用户说明）**：共享版本由于多个版本共用同一个 `.minecraft/saves`，列表里
其存档徽章统一显示 **「共享」**，不显示具体版本号——因为存档本身不带版本信息，
标一个具体版本号反而是误导。只有隔离版本的存档才会显示具体版本 Id。

### 3.4 LaunchAccountService（新）+ ResolvedPlayer

把「当前用哪个账户启动」从 HomePage 私有控件逻辑中抽出，主页与存档页共用。
**不新增存储**，复用已有的 `AccountStore`（`Accounts` / `Default` / `SetDefault`，
已有持久化）。

```csharp
public sealed record ResolvedPlayer(string Name, LaunchCredentials? Credentials);

public sealed class LaunchAccountService(
    AccountStore accounts,
    MicrosoftAuthService auth,
    Func<LauncherSettings> settings)
{
    public string Mode { get; set; }        // "microsoft" | "offline"
    public string OfflineName { get; set; } // 无离线账户时手输的玩家名
    public Task<ResolvedPlayer> ResolveAsync();
}
```

- `Mode` 未显式设置时，按 `settings().LastUsername` 的 `ms:` 前缀推断（与现有
  `HomePage.SetMode` 同逻辑）。
- `ResolveAsync` 行为与现有 `HomePage.ResolveMicrosoftAccountAsync` 一致：
  - 正版：优先 `RefreshAsync(refresh_token)` 续期并写回 `AccountStore`；网络异常时降级用
    存量 `accessToken`；两者都不可用则抛错「登录已过期，请到账户页重新登录」。
  - 离线：返回 `AccountStore.Default` 或手输名的 `ResolvedPlayer`，`Credentials = null`。
- 离线无账户且无手输名时，回退 `LauncherSettings.LastUsername`。

**HomePage 改造**：删除 `ResolveMicrosoftAccountAsync` / `ResolvePlayerName`，
改为调用该模块。**界面与行为完全不变**（账户下拉框、离线名输入框、头像、默认账户
写回逻辑原样保留）。

### 3.5 未决记录 pending-world.json

文件：`<dotMc>/starcloud/pending-world.json`

```json
{
  "world_folder": "我的世界",
  "world_name": "我的世界",
  "source_version": "",
  "target_version": "1.21",
  "source_game_root": "C:/.../.minecraft",
  "target_game_root": "C:/.../.minecraft/versions/1.21",
  "copied": true,
  "started_at": "2026-09-10T12:00:00",
  "process_id": 12345
}
```

生命周期：

- 复制成功后写入；启动被拒 / 复制失败时删除。
- 用户在退出弹窗中完成三选一后删除。
- 启动器在游戏运行期间被关闭 → 记录残留；下次进入存档页 `Loaded` 时发现残留即补问。
- 补问前校验目标副本目录是否仍存在；不存在则直接清记录，不弹窗。

## 4. 存档页 UI 设计

### 4.1 布局

在现有 4 行（页头 / InfoBar / 工具条 / 列表）基础上：

- 工具条左侧新增 **「用其他版本启动」**（`AccentButtonStyle`）与 **「复制到其他版本」**，
  与原有 备份 / 删除 / 打开文件夹 并列；无选中项时禁用。
- 工具条右侧新增 **搜索框**（`AutoSuggestBox` 或 `TextBox`）与 **版本筛选 `ComboBox`**。
- 列表区保留 `ListView`，行内新增版本徽章。
- 新增一条运行态 `InfoBar`（见 5.6），默认折叠。

### 4.2 行视图模型 SaveRow

`PageRows.cs` 中 `SaveRow` 扩展：

```csharp
public sealed record SaveRow(
    string FolderName, string Name, string? IconPath,
    string LastPlayedText, string SizeText,
    string VersionLabel,          // "共享" 或版本 Id
    bool Isolated,
    string GameRoot,
    string SearchKey);            // 世界名 + 文件夹名 + 版本 Id，小写
```

新增派生属性 `IsolatedVisibility`（隔离小标显隐）。

### 4.3 搜索与筛选

- 搜索：对 `SearchKey` 做大小写不敏感包含匹配；搜索框文案「搜索世界名 / 文件夹名 / 版本」。
- 版本筛选 `ComboBox` 选项：`全部` / `共享` / 各已装版本 Id（来自 `Manifest.ListInstalled`）。
- 二者为 **AND** 关系：先按搜索过滤，再按版本过滤。
- 过滤在内存中对已加载的 `_rows` 执行，不重新扫描磁盘。

### 4.4 行内版本徽章

在名称行右侧显示徽章：`共享` 或 `1.20.1-fabric`；隔离版本的徽章后再跟一枚
「隔离」小标。样式沿用现有 `CornerRadius="4" Padding="8,2"` 的轻量标签风格，
底色 `CardBackgroundFillColorSecondaryBrush`，文字 `TextFillColorSecondaryBrush`。

### 4.5 空状态

`EmptyHint` 需区分两种文案：

- **无任何存档**：「暂无存档」/「启动游戏创建世界后，可在此进行备份与管理」。
- **筛选无结果**：「没有匹配的存档」/「试试换个关键词或版本筛选」。

### 4.6 既有操作改用 row.GameRoot

`OpenFolder_Click` / `Backup_Click` / `Delete_Click` 一律改为基于 `row.GameRoot`
（打开文件夹 = `Path.Combine(row.GameRoot, "saves", row.FolderName)`）。

## 5. 跨版本启动流程

### 5.1 入口与确认弹窗

工具栏「用其他版本启动」（及「复制到其他版本」）点击后，对当前选中行弹出
`ContentDialog`（`XamlRoot` 取自窗口根元素）：

- 顶部 `InfoBar Severity="Warning"`：**「此世界来自 1.20.1，将由 1.21 打开。跨版本打开
  可能触发世界数据升级，且改动不可逆。」**（源为共享根时，来源显示为「共享存档」）
- 目标版本 `ComboBox`：仅列已装版本，**默认选中与源版本不同的最近一个**；源为共享存档
  （`VersionId = ""`）时默认选中列表中的第一个版本；无其他版本时禁用主按钮并提示
  「没有其他已安装版本」。
- 一行「**基础启动模式**：不含文件校验与自动修复，失败请回首页启动。」
- 一行只读账户：**「使用账户：Steve（正版）」** / **「……（离线）」**，**不可切换**；
  要换账户请回主页。
- 必勾 `CheckBox`：「我已了解跨版本风险」；未勾选时主按钮 `IsEnabled = false`。
- 按钮：**「复制并启动」**（主）/ **「仅复制」**（即方案 C 能力）/ **「取消」**。
  - 「仅复制」：复制完成后 `InfoBar` 提示「已复制到 1.21，可随时启动」，
    **不启动、不写未决记录**。
- **源根与目标根相同**（目标版本非隔离，游戏根即共享根）时：判定「无需复制」，
  弹窗内直接说明并只保留「启动」按钮。

### 5.2 复制规则

- 目标根：`InstanceLayout.GameRoot(dotMc, targetId, isolated)`；复制到 `<目标根>/saves/`。
- 副本名：`<源文件夹名>-来自 <源版本标签>`（源为共享根时用 `共享`）。
- 重名走 `InstanceLayout.NextAvailablePath` → `世界名-来自 1.20.1 (2)`。
- 复制在 `Task.Run` 中执行，复用现有 `BackupProgress` 的不定进度条；复制期间禁用工具条。
- **复制失败**：清理半成品副本 → `ErrorBar` 报错 → **不启动**。

### 5.3 基础启动

- 用 `InstanceLayout.Resolve(dotMc, targetId, profile, settings)` 构造 `LaunchOptions`，
  **`GameDir = eff.GameRoot`**；libraries / assets / natives / runtime 仍走共享目录。
- **跳过 `Preflight.CheckAsync` 与自动补全**（这正是「基础启动模式」的含义）。
- 启动前校验：
  - 目标版本未在运行：`App.RunningVersionId == targetId` 则拒绝并提示。
  - 玩家身份可解析（`LaunchAccountService.ResolveAsync` 成功）。
- 成功后：`App.RunningVersionId = targetId`、`InstanceSettingsStore.TouchPlayed(targetId)`、
  写未决记录、刷新列表。
- **启动抛异常不回滚副本**，提示「启动失败：X。副本已保留在 1.21，可稍后重试」，
  并写未决记录（`copied = true`）。

### 5.4 退出后归属三选一

游戏进程 `Exited` 时弹出 `ContentDialog`（用窗口根 `XamlRoot`，因此**玩家已离开存档页
也能弹**），三个选项：

1. **保存到当前版本**（默认）：副本保留在目标 `saves/`，源存档不动。
2. **保留在原本的版本**（用副本替换源）：
   - 先用 `SaveService.Backup` 把源存档 zip 备份进 `backups/` 兜底；
   - 再用副本内容替换源存档目录；
   - 最后删掉目标版本副本。
3. **丢弃本次改动**：删目标副本，源不动。

选择后清除未决记录并刷新存档页。**源根 = 目标根时不弹窗**（没有副本概念）。

### 5.5 中断兜底

启动器在游戏运行期间退出 → 未决记录残留。下次进入存档页 `Loaded` 时：

- 发现残留记录 → 补问同一套三选一；
- 补问前校验目标副本是否存在，不存在则直接清记录；
- 记录 `copied = true` 但实际未启动的情况下，提示改为「已复制但未启动，是否保留」。

### 5.6 运行态与停止

存档页顶部一条 `InfoBar`：**「正在以 1.21 运行『世界名』」** + 「停止游戏」按钮
（`Kill` 进程）。这是「轻量状态」的全部实现，**不做进度、不做日志流**。

## 6. 主窗口 UI 调整

### 6.1 删除底部 Nova

- 删除整个 `NovaBar`（`Grid.Row="2"` 的 `NovaBarCollapsed` + `NovaBarExpandedHost`）。
- 删除状态栏右侧 `NovaStateDot` / `NovaStateText`。
- 主窗口由 4 行改为 **3 行**（48 标题栏 / * 内容 / Auto 状态栏），状态栏 `Grid.Row` 3 → 2。
- 连带代码改动：
  - 删 `MainWindow.NovaBarToggle_Click`。
  - `OpenNovaBar()` → **`OpenNovaPanel()`**（抽出 `NovaOrb_Click` 的打开逻辑），
    弹出 Orb 面板。
  - `HomePage.AskNova_Click` 与 `MainWindow.HandleJoinUrlAsync` 改为调用
    `OpenNovaPanel()`。
  - `ApplyNovaState` 删除对 `NovaStateText` / `NovaStateDot` 的赋值，
    **保留 Orb 五态着色**。
  - `OnOverlayToast` 改用 **`TeachingTip`**，锚定在 `NovaOrb` 上。

### 6.2 NovaOrb 改造

- 背景改为**中性卡片色实心**：`CardBackgroundFillColorDefaultBrush` + 描边，
  删除浅/深两套 `AcrylicBrush` 资源。
- 保留 `NovaOrbGlyph`（五态着色）与 `NovaOrbDot`（`HasPlan` 时显示）。

### 6.3 个人中心入口

- 在标题栏右侧用 `StackPanel` 并列放 **NovaOrb + 个人中心按钮**，
  原来的 `Margin="0,0,140,0"` 移到该 `StackPanel` 上。
- 按钮图标 = **本地生成用户头像**：已登录取 `StarcloudIdService.Current.DisplayName` 首字
  + 色底（与主页 `AvatarText` 同法）；未登录用人形 `FontIcon`。
  （`StarcloudSession` 没有头像字段，头像本地生成。）
- `ToolTip` 悬浮卡内容：头像 / 昵称 / `UserId` / 登录态（已登录/未登录）/ 通道
  （测试通道 / 云端通道），类似 bilibili。
- 点击 → `NavigateTo("accounts")`。
- **个人中心即账户页已有的 Starcloud ID 区块**，不新建页面；**本次不加官网外链**。

## 7. 关键操作细节

- **失败不留垃圾**：复制失败清副本；启动被拒清副本 + 清记录。
- **路径基准统一**：存档页所有磁盘操作以 `SaveInfo.GameRoot` 为准，
  不再出现 `Settings.DotMinecraft` 硬编码。
- **不重复扫描**：非隔离版本的根与共享根重合，聚合时跳过。
- **账户只读**：临时启动弹窗不允许切换账户，避免与主页默认账户产生二义。
- **备份先于替换**：执行「保留在原本的版本」时，源存档一定先 zip 备份再被覆盖。

## 8. 验收标准

1. 隔离版本（`versions/<id>/saves`）下的存档能在存档页列出，并显示其版本 Id 与「隔离」标。
2. 共享根存档列出并显示「共享」徽章。
3. 搜索（世界名 / 文件夹名 / 版本 Id）与版本筛选可组合生效；筛无结果显示专用空状态。
4. 选一个世界 →「用其他版本启动」→ 选目标版本 → 勾选风险确认 → 复制并启动成功；
   目标版本 `saves/` 下出现 `<名>-来自 <源版本>` 副本。
5. 退出游戏后弹出三选一；选「保留在原本的版本」后源存档被替换且 `backups/` 有 zip 兜底，
   目标副本被删除。
6. 运行期间强杀启动器，下次进存档页能补问同一套三选一。
7. 运行中存档页显示「正在以 X 运行『世界名』」并可停止游戏。
8. 「打开世界文件夹 / 备份 / 删除」对隔离版本存档操作到正确目录。
9. 主窗口底部无 Nova 对话条与状态栏 Nova 指示；NovaOrb 为不透明卡片色，
   `OnOverlayToast` 以 TeachingTip 弹出。
10. 右上角个人中心按钮悬浮显示头像 / 昵称 / ID / 登录态，点击进入账户页；
    未登录时显示人形图标。
11. 主页账户区界面与行为与改造前完全一致。

## 9. 非目标

- 不做跨版本存档预检与自动修复（不做版本兼容性检测、不做区块降级）。
- 不做存档内容级合并或 diff。
- 不做官网外链跳转（个人中心页本次不加外链）。
- 不改主页账户区的界面与交互。
- 不做存档云端上传/同步。
- 不重构 `NovaService`（其注入的 `SaveService` 未被使用，随签名调整一并修正）。
