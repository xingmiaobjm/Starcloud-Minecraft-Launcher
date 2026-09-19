# 统一资源库 · 下载装载 · 资源管理 · 版本内容管理 设计

日期：2026-09-11
状态：设计已确认（分节确认完成），待写实现计划
所属：PCL2 平替第 A 组（内容管理）

## 1. 背景与问题

用户诉求：

> 我想要 mod 等资源下载时，下载到启动器内的目录，下载的时候可以选择给哪些版本装载这个 mod 等，
> 也可以在下载导航页里，右上角已有资源进行管理，内部分类 mod，数据包，整合包，光影包，
> 同时支持智能依赖处理。

现状问题（探查结论）：

1. **下载即落盘到 `.minecraft` 根目录**。[DownloadPage.xaml.cs](../../../dotnet/src/Starcloud.App/Views/DownloadPage.xaml.cs)
   的 `Install_Click` 恒用 `Path.Combine(_s.Settings.DotMinecraft, info.Folder, file.Filename)`
   作为落点，**完全不考虑版本隔离**；用户选了「兼容版本」只用于拉取文件，安装位置与所选版本无关。
   结果是：隔离版本的 Mod 装不进去，且同一个 Mod 装到 N 个版本要下载 N 次。
2. **没有资源库概念**。启动器只认识 `.minecraft/<folder>/`，没有任何「已下载资源」的统一清单，
   因此无法跨版本复用、无法集中删除、无法集中检查更新。
3. **更新检查与依赖处理缺失**。`ModrinthService` 只有 `SearchAsync` 与 `ListVersionsAsync`
   （[ModrinthService.cs](../../../dotnet/src/Starcloud.Core/Services/ModrinthService.cs#L77-L90)），
   `ModrinthVersion` 模型**没有 `dependencies` 字段**；本地已装的 jar 也没有任何 Modrinth 身份信息，
   无从判断「是否有新版」「缺哪个前置」。
4. **版本内内容不可管理**。`VersionDetailPage` 的「内容」卡片
   （[VersionDetailPage.xaml](../../../dotnet/src/Starcloud.App/Views/VersionDetailPage.xaml#L113-L124)）
   只是 5 行目录名 + 「打开目录」按钮，看不到文件、不能启禁、不能删除。
5. **停用约定已存在但无界面**。内核已有 `<file>.jar.disabled` 改名约定
   （[ModConflictService.cs](../../../dotnet/src/Starcloud.Core/Services/ModConflictService.cs#L33-L71)
   与 `NovaService`），但没有任何 UI 暴露它，也没有「冲突扫描结果」展示。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| 库与版本的关系 | **库做下载缓存，装载即复制**（不做硬链接，不引入跨盘回退复杂度） |
| 下载落点 | 先落 `library/<kind>/`，再由用户选择的版本各复制一份 |
| 装载目标选择 | **多选已安装版本 + 「暂不装载（仅入库）」** |
| 更新识别方式 | **SHA1 哈希反查 Modrinth**（`/v2/version_file`），同时支撑更新检查与依赖补全 |
| 资源管理入口 | 下载页**右上角按钮** → 主区覆盖式切换，**不新增导航项** |
| 资源管理分类 | 全部 / Mod / 资源包 / 光影 / 数据包 / 整合包 |
| 版本内容卡片 | 一并升级（启禁/删除/更新/冲突扫描/拖拽安装）——启禁是版本维度操作，必须在版本目录 |
| 数据包定位 | 装载目标是**存档**而非版本（复制到 `<存档>/datapacks/`），不做世界内启禁 |
| 整合包 | 保持现有 `MrPack.InstallAsync` 建新版本流程，文件归档到 `library/modpacks/` |
| 资源源 | 仍只接 Modrinth，不接 CurseForge |
| 不做 | 硬链接、独立「资源」导航页、世界内数据包启禁 |

## 3. 数据模型

### 3.1 路径（`LauncherPaths.cs`）

```csharp
/// <summary>启动器统一资源库目录</summary>
public static string LibraryDir => Path.Combine(DataDir, "library");

public static string LibraryIndexFile => Path.Combine(LibraryDir, "library.json");
```

`EnsureDataDir()` 追加 `Directory.CreateDirectory(LibraryDir)`。
子目录 `mods/ resourcepacks/ shaderpacks/ datapacks/ modpacks/` 与临时目录 `.tmp/`（下载暂存，
入库后即清空）由 `ContentLibraryService` 按需创建。

### 3.2 资源条目与索引

新增 `Starcloud.Core/Services/ContentLibraryService.cs`：

```csharp
public sealed class LibraryEntry
{
    [JsonPropertyName("id")]            public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("kind")]          public string Kind { get; set; } = "";   // mod|resourcepack|shader|datapack|modpack
    [JsonPropertyName("file_name")]     public string FileName { get; set; } = "";
    [JsonPropertyName("sha1")]          public string Sha1 { get; set; } = "";
    [JsonPropertyName("size_bytes")]    public long SizeBytes { get; set; }
    [JsonPropertyName("title")]         public string Title { get; set; } = "";
    [JsonPropertyName("icon_url")]      public string? IconUrl { get; set; }
    [JsonPropertyName("author")]        public string? Author { get; set; }
    [JsonPropertyName("project_id")]    public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")]    public string? ModrinthVersionId { get; set; }
    [JsonPropertyName("version_number")]public string? VersionNumber { get; set; }
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
    [JsonPropertyName("loaders")]       public List<string> Loaders { get; set; } = [];
    [JsonPropertyName("source_url")]    public string? SourceUrl { get; set; }
    [JsonPropertyName("downloaded_at")] public DateTime DownloadedAt { get; set; } = DateTime.Now;
}

public sealed class LibraryIndex
{
    [JsonPropertyName("entries")] public List<LibraryEntry> Entries { get; set; } = [];
}
```

`library.json` 写入沿用 `InstanceSettingsStore` 的**原子写**模式（临时文件 + `File.Move` overwrite）；
解析失败 → 静默重置为空索引（不阻塞使用）。

### 3.3 运行时视图模型（App 层）

`DownloadPage` / `VersionDetailPage` 展示用的行模型加到 `Views/PageRows.cs`
（WinUI `x:DataType` 要求命名空间级类型，不能是页面嵌套类型）：

```csharp
/// <summary>资源管理列表行</summary>
public sealed record LibraryRow(
    string Id, string Kind, string Title, string FileName, string SubText,
    string SizeText, string TimeText, ImageSource? Icon, string KindLabel);

/// <summary>版本内容分区的单个文件行</summary>
public sealed record ContentFileRow(
    string FileName, string SizeText, string StatusText, bool Enabled, string Kind, string FullPath)
{
    public string ToggleVerb => Enabled ? "停用" : "启用";
}
```

## 4. 服务设计（`ContentLibraryService`）

```csharp
public sealed class ContentLibraryService
{
    public ContentLibraryService(DownloadService downloader, ModrinthService mods, ManifestService manifest);

    // ── 库读写 ──
    public Task<IReadOnlyList<LibraryEntry>> ListAsync(string? kind = null, CancellationToken ct = default);
    public LibraryEntry? FindBySha1(string sha1);
    public LibraryEntry? FindByFileName(string kind, string fileName);
    public void Remove(string entryId);                       // 仅删库文件 + 索引
    public string DirFor(string kind);                        // library/<folder>

    // ── 入库 ──
    /// <summary>
    /// 把下载到 library/.tmp/ 的临时文件收进库：算 sha1 → 查重 → 命中则删临时文件并返回已有条目，
    /// 未命中则改名进 library/&lt;folder&gt;/ 并写索引。返回最终条目。
    /// </summary>
    public Task<LibraryEntry> AdoptAsync(string kind, string sourcePath, LibraryAdoptInfo info,
                                         CancellationToken ct = default);
    /// <summary>扫描库目录重建索引（索引损坏或外部手工放入资源后的修复入口）</summary>
    public Task<IReadOnlyList<LibraryEntry>> RescanAsync(CancellationToken ct = default);

    // ── 装载 ──
    /// <summary>把库条目复制到目标版本的内容目录（隔离/共享自适应），返回落点路径</summary>
    public Task<string> InstallToVersionAsync(LibraryEntry entry, string versionId, CancellationToken ct = default);
    /// <summary>把数据包 zip 复制到指定存档的 datapacks/</summary>
    public Task<string> InstallToSaveAsync(LibraryEntry entry, string versionId, string saveFolderName, CancellationToken ct = default);

    // ── 更新检查 ──
    /// <summary>批量 SHA1 反查 Modrinth，返回 sha1 → 项目身份（含进程内缓存）</summary>
    public Task<IReadOnlyDictionary<string, ModrinthIdentity>> ResolveIdentitiesAsync(
        IEnumerable<string> sha1s, CancellationToken ct = default);
    /// <summary>检查单个已装文件是否有更新（比对 project 的最新兼容版本）</summary>
    public Task<UpdateCheckResult?> CheckUpdateAsync(string filePath, string? gameVersion, string? loader,
        CancellationToken ct = default);

    // ── 依赖 ──
    /// <summary>解析一批 Modrinth 版本文件的必需依赖，返回目标版本目录中缺失的项目</summary>
    public Task<DependencyReport> CheckDependenciesAsync(
        ModrinthVersion version, string versionId, CancellationToken ct = default);
}

public sealed record LibraryAdoptInfo(
    string Title, string? IconUrl, string? Author, string? ProjectId,
    string? VersionId, string? VersionNumber, List<string> GameVersions, List<string> Loaders,
    string? SourceUrl);

public sealed record ModrinthIdentity(string ProjectId, string VersionId, string VersionNumber,
    List<string> GameVersions, List<string> Loaders);

public sealed record UpdateCheckResult(string FileName, string CurrentVersion, string LatestVersion,
    string LatestFileName, string LatestUrl, string? LatestSha1);

public sealed record DependencyReport(List<MissingDependency> Required, List<ModrinthDependency> Optional,
    List<string> IncompatiblePresent);

public sealed record MissingDependency(string ProjectId, string? VersionId, string Title, string? FileName);
```

**去重规则**：`AdoptAsync` 先算源文件 SHA1；库内已有同 SHA1 条目 → 删除临时文件、直接返回已有条目。
同名不同 SHA1 → 目标文件名加 `" (n)"` 后缀（复用 `InstanceLayout.NextAvailablePath` 的思路）。

**装载规则**：`InstallToVersionAsync` 用
`InstanceLayout.GameRoot(dotMc, versionId, InstanceLayout.IsIsolated(profile, settings))` 求根，
目标目录 = `<root>/<kind 对应目录>`；目标已有同名文件 → 先删除再复制（不做备份）。
返回落点绝对路径供 UI 提示。

**kind → 目录映射**（`InstanceSettings.cs` 的 `InstanceLayout` 追加）：

```csharp
/// <summary>资源类别 → 版本内容目录名</summary>
public static string ContentDirFor(string kind) => kind switch
{
    "mod" => "mods",
    "resourcepack" => "resourcepacks",
    "shader" => "shaderpacks",
    "datapack" => "datapacks",
    _ => "mods",
};
```

## 5. Modrinth 扩展（`ModrinthService.cs`）

### 5.1 模型

`ModrinthVersion` 追加：

```csharp
[JsonPropertyName("dependencies")]
public List<ModrinthDependency> Dependencies { get; set; } = [];
```

新增：

```csharp
public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")]      public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")]      public string? VersionId { get; set; }
    [JsonPropertyName("file_name")]       public string? FileName { get; set; }
    /// <summary>required | optional | incompatible | embedded</summary>
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "";
}
```

### 5.2 批量哈希反查

```csharp
/// <summary>批量按 SHA1 反查版本文件（POST /v2/version_files，一次请求 ≤ N 个哈希）</summary>
public async Task<Dictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(
    IReadOnlyCollection<string> sha1s, CancellationToken ct = default);
```

- 请求体 `{ "hashes": [...], "algorithm": "sha1" }`，响应为 `{ "<sha1>": ModrinthVersion }`
- 分片：每批 100 个哈希（Modrinth 建议上限），空集合直接返回空字典
- 单点备用：`GET /v2/version_file/{sha1}?algorithm=sha1` 用于「检查更新」单个文件时避免多余请求
- 未命中（不在 Modrinth 上）的哈希不出现在返回字典里，调用方据此判定「无法识别」

### 5.3 最新兼容版本

复用现有 `ListVersionsAsync(projectId, loader, gameVersion)`：返回列表已按发布时间倒序，
取首个非 alpha（若只有一个则取该个）。需要「最新版本号 + 下载 URL + sha1」时直接用其 `PrimaryFile`。

## 6. 界面设计

### 6.1 下载页改造（`DownloadPage.xaml(.cs)`）

**a. 页头右上角加按钮**

页头现为单 `StackPanel`（[DownloadPage.xaml](../../../dotnet/src/Starcloud.App/Views/DownloadPage.xaml#L15-L20)），
改为 `Grid` 两列：左标题文案不变，右 `Button x:Name="LibraryBtn"` 「资源管理」（`FontIcon` + 文字，
`Style="{ThemeResource AccentButtonStyle}"` 的次要变体或默认样式）。

**b. 主体加覆盖层**

`Grid.Row="1"` 的 `Grid` 追加第三个区块：

```xml
<Grid x:Name="LibraryPanel" Grid.ColumnSpan="2" Visibility="Collapsed">
    <!-- 顶部：← 返回 | 标题「资源管理」| 搜索框 -->
    <!-- 左侧：分类 ListView（全部 / Mod / 资源包 / 光影 / 数据包 / 整合包） -->
    <!-- 右侧：资源列表 ListView（LibraryRow 模板）+ 空状态 -->
    <!-- 底部：InfoBar（操作结果/更新提示/依赖提示） -->
</Grid>
```

`LibraryBtn_Click` → `LibraryPanel.Visibility = Visible` + 隐藏左侧分类与右侧内容；
「返回」按钮反向操作。打开时调 `RefreshLibraryAsync()`。

**c. 装载弹窗**

新增私有方法 `Task<IReadOnlyList<string>?> PickVersionsAsync(string kind, string title)`：

- `ContentDialog`，内容为 `ScrollViewer` + `CheckBox` 列表
- 数据源 `_s.Manifest.ListInstalled(dotMc)`，每项显示版本 id + 「独立目录 / 共享目录」徽标
- 底部「暂不装载（仅入库）」为 `SecondaryButton` → 返回空列表（非 null，表示「确认但零目标」）
- 「取消」为 `CloseButton` → 返回 `null`（放弃整个安装）
- 数据包场景改用 `PickSaveAsync(versionId)`：先选版本再列 `SaveService.ListSaves(...)` 的存档

**d. `Install_Click` 新流程**

```
1. ListVersionsAsync(projectId, loader, versionId) → version.PrimaryFile（不变）
2. 目标选择：
   - kind == "datapack" → PickSaveAsync（选版本 → 选存档）
   - kind == "modpack"  → 跳过（走 6.1e）
   - 其余              → PickVersionsAsync（多选版本 + 暂不装载）
   - 用户取消 → return
3. 下载到 `library/.tmp/<guid>.part`（`DownloadService` 校验 sha1），再用 `AdoptAsync` 收进库
   （算 sha1 → 去重 → 改名进 `library/<folder>/` + 写索引）
4. 拿到入库后的 `LibraryEntry`（含最终文件名与 Sha1）
5. 对每个目标版本 InstallToVersionAsync（或数据包 InstallToSaveAsync）
6. OkBar 摘要：「<文件名> 已入库，已装载到 N 个版本」（N=0 时「已入库（未装载）」）
7. 依赖检查（仅 kind == "mod" 且有目标版本）：CheckDependenciesAsync
   → Required 非空 → InfoBar 提示 + 「一键补全」按钮
```

**e. 整合包分支**

`.mrpack` 下载后先 `AdoptAsync("modpack", ...)` 归档，再走现有 `InstallMrPackAsync(dest)`；
`dest` 改为库内路径。其余逻辑（依赖联动装原版 + 加载器）不变。

### 6.2 版本内容卡片（`VersionDetailPage.xaml(.cs)`）

`VersionDetailPage.xaml` 的「内容」卡片 L113-L124 整块替换为：

- **头**：标题「内容」+ 右侧两个按钮「检查更新」「冲突扫描」
- **三个分区**（Mod / 资源包 / 光影）：折叠头（`Expander` 或自绘 `ToggleButton` 行）
  显示「Mod (12)」+ 分区内 `ListView`（`ContentFileRow` 模板）
- **每行**：`FontIcon` · 文件名（`TextTrimming="CharacterEllipsis"`）· 大小 · 状态文字（启用 /
  已停用 / 无法解析）· 行尾按钮「启用/停用」「检查更新」「删除」
  - 已停用行整体 `Opacity="0.5"`
- **卡片底部**：`saves`、`config` 两行保留「打开目录」按钮（原 `BuildContentRow` 保留但只用于这两项）
- **拖拽**：卡片 `AllowDrop="True"` + `DragOver` / `Drop` 处理
  - `.jar` → `mods`
  - `.zip` → `ContentDialog` 选「资源包 / 光影 / 数据包(再选存档)」
  - 拖入文件走「先 `AdoptAsync` 入库 → 再 `InstallToVersionAsync` 到当前版本」

**「检查更新」**（分区头批量）：收集该分区内所有已启用文件 → 算 SHA1 → `ResolveIdentitiesAsync`
→ 逐个 `ListVersionsAsync(projectId, loader, gameVersion)` 比对版本号 → 汇总列表用
`ContentDialog` 展示（文件名 / 当前版本 / 最新版本），支持「全部更新」（下载新版 →
`AdoptAsync` 入库 → 覆盖到本版本 → 旧文件删除）。

**「冲突扫描」**：调 `_s.ModConflicts.ScanAsync(dotMc)` → 用 `ContentDialog` 展示
`ModConflictReport`：已扫描数量 + 每条 `ModConflict.Kind / Detail / Suggestion / Files`；
有冲突时提供「自动处理」按钮（`ResolveAsync` → 展示返回的中文摘要）。

**状态判定**：
- 文件以 `.disabled` 结尾 → 已停用（`Enabled = false`）
- `ModConflictService` 解析出的 `Error` 非空 → 无法解析
- 其余 → 启用

### 6.3 视觉规范

沿用 PCL 风格：白卡片 + 1px 描边 + 圆角 / 13px 700 蓝色小节标题 / 胶囊状态文字 /
蓝色左竖线信息条（`InfoBar`）/ 列表行高 40、`Padding="8,4"`。

## 7. 智能依赖处理

1. **触发时机**：Mod 装载完成之后（不是之前——需要先知道目标版本已有什么）。
2. **已装项目集合**：对目标版本 `mods/` 下所有 `.jar`（含 `.jar.disabled`）算 SHA1 →
   `ResolveIdentitiesAsync` 批量反查 → 得到已装 `ProjectId` 集合。
   - 反查结果进程内缓存（`Dictionary<string, ModrinthIdentity?>`，含「未命中」的负缓存），
     同一 Mod 装 N 个版本只查一次。
3. **比对**：`version.Dependencies` 中
   - `required` 且 `ProjectId` 不在已装集合 → **缺失**（进 `DependencyReport.Required`）
   - `optional` → 进 `Optional`，仅列出
   - `incompatible` 且 `ProjectId` 在已装集合 → 进 `IncompatiblePresent`，警告
   - `embedded` → 忽略
4. **一键补全**：对每个缺失项目调 `ListVersionsAsync(projectId, loader, gameVersion)` 取首个兼容版本
   → 下载入库 → 装载到**同一批目标版本** → 递归检查该依赖自身的 `required`（**限深 2 层**防环，
   并按 `ProjectId` 去重防重复下载）。
5. **提示文案**：「缺少 3 个必需前置：Fabric API、Cloth Config、Architectury API」+
   「一键补全」/「忽略」。
6. 补全失败（网络/无兼容版本）不阻塞已完成的装载，只在 InfoBar 里列出失败项。

## 8. 文件清单

**Core 新增**
- `dotnet/src/Starcloud.Core/Services/ContentLibraryService.cs` —— 库索引 / 入库 / 装载复制 /
  批量哈希反查 / 更新检查 / 依赖解析补全

**Core 修改**
- `dotnet/src/Starcloud.Core/Services/ModrinthService.cs` —— `GetVersionsByHashesAsync`、
  `GetVersionByHashAsync`、`ModrinthVersion.Dependencies`、`ModrinthDependency`
- `dotnet/src/Starcloud.Core/Services/LauncherPaths.cs` —— `LibraryDir` / `LibraryIndexFile`
- `dotnet/src/Starcloud.Core/Services/InstanceSettings.cs` —— `InstanceLayout.ContentDirFor(kind)`

**App 修改**
- `dotnet/src/Starcloud.App/Views/DownloadPage.xaml(.cs)` —— 右上角按钮、`LibraryPanel`、
  装载弹窗、`Install_Click` 新流程
- `dotnet/src/Starcloud.App/Views/VersionDetailPage.xaml(.cs)` —— 内容卡片重写、拖拽、更新检查、冲突扫描
- `dotnet/src/Starcloud.App/Views/PageRows.cs` —— `LibraryRow` / `ContentFileRow` 等行模型
- `dotnet/src/Starcloud.App/Services/AppServices.cs` —— 注册 `ContentLibraryService`

**文档**
- `docs/USER_MANUAL.md` —— 新增「资源库与内容管理」章节；顺带修正 §8 设置页的旧描述
  （现写「最小内存 512 / 最大 4096」，实际已是单滑块调最大内存 + 三态档位）

## 9. 不做的事（明确排除）

- 不做硬链接（装载 = 复制，接受多份空间占用）
- 不新增独立「资源」导航页（入口只在下载页右上角 + 版本详情页卡片）
- 不接 CurseForge / 其他资源源（仍只 Modrinth）
- 数据包不做世界内启禁（只做复制 zip 到 `<存档>/datapacks/`）
- 不做版本回档 / 版本备份（属 B 组）

## 10. 验收标准

**构建**：`dotnet build dotnet/src/Starcloud.App/Starcloud.App.csproj` 0 警告 0 错误。

**手动验收清单**

1. **入库去重**：同一个 Mod 装到 3 个版本 → 库内只有 1 份文件、`library.json` 只有 1 条；
   再次安装同一 Mod 到另一版本 → 不产生第二次下载。
2. **多版本装载**：隔离版本与共享版本同时勾选 → 文件分别落在 `versions/<id>/mods/`（隔离）
   与 `.minecraft/mods/`（共享）。
3. **暂不装载**：选「暂不装载（仅入库）」→ 库里有文件，任何版本目录都没有该文件；
   之后从资源管理里「装载到版本」可正常复制过去。
4. **数据包**：下载数据包 → 弹窗选版本 + 存档 → 文件出现在 `<存档>/datapacks/`。
5. **依赖补全**：下载一个依赖 Fabric API 的 Mod 到新建的无 Mod 版本 → 提示缺 1 个必需前置 →
   一键补全后 `mods/` 里有 Fabric API。
6. **更新检查**：对库内一个旧版 Mod 点「检查更新」→ 显示最新版本号 → 更新后库与目标版本目录
   都换成新文件，旧文件不残留。
7. **启禁还原**：版本内容卡片里对某 Mod 点「停用」→ 文件名变 `xxx.jar.disabled`、状态显示「已停用」、
   行变灰；点「启用」→ 恢复原名。
8. **冲突扫描**：手动把同一个 Mod 的两个版本放进 `mods/` → 点「冲突扫描」→ 列出重复项 →
   「自动处理」后只留最新版，其余变 `.disabled`。
9. **拖拽安装**：把 `.jar` 拖到内容卡片 → 出现在 Mod 分区且已入库；把 `.zip` 拖入 → 弹选类型。
10. **索引损坏**：手工把 `library.json` 改成非法 JSON → 资源管理页仍能打开（空列表或重建），
    不崩溃。

## 11. 风险与对策

| 风险 | 对策 |
|---|---|
| 批量哈希反查触发 Modrinth 限流 | 分片 100/批 + 进程内缓存 + 只有用户点「检查更新」/安装后才触发，不做后台轮询 |
| 大文件算 SHA1 耗时 | 入库与反查在后台线程（`Task.Run`），UI 显示进度文案 |
| 复制大文件（如光影包）卡 UI | 同上，复制也在后台线程；`InfoBar` 显示「正在装载到 <版本>…」 |
| `library.json` 与库目录不同步（外部手工删文件） | `RescanAsync` 作为修复入口；`LibraryPanel` 打开时不自动重建，只在用户点「重建索引」或检测到条目缺失文件时提示 |
| 装载覆盖同名文件导致用户数据丢失 | 目标同名文件先删除再复制属于预期行为（同一 Mod 的新旧版本），但**仅在用户明确点安装时发生**；批量覆盖前 `InfoBar` 说明 |
