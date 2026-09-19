# 整合包导出 · 本地整合包导入 · 版本备份与回档 设计

日期：2026-09-11
状态：待用户确认
所属：PCL2 平替第 B 组（整合包与版本资产）

## 1. 背景与问题

用户诉求：

> 导出整合包、导入本地整合包、版本备份与回档都要。
> 导出时**我自己选要打包什么**，要能兼容投影（litematica / schematics）这类模组产生的文件。
> 备份粒度也让我自己选。

现状问题（探查结论）：

1. **导出整合包根本不存在**。[VersionsPage.xaml](../../../dotnet/src/Starcloud.App/Views/VersionsPage.xaml#L43-L50)
   的菜单项 `导出整合包` 是 `IsEnabled="False"` 的占位，ToolTip 还写着「C 阶段提供」。
2. **导入本地整合包没有入口**。启动器只有「从 Modrinth 下载 `.mrpack` 后自动安装」这一条链路
   （[DownloadPage.xaml.cs](../../../dotnet/src/Starcloud.App/Views/DownloadPage.xaml.cs#L835-L844)
   → `InstallMrPackAsync`），用户手上已有的 `.mrpack` / 整合包 zip **无处可导入**。
3. **`.mrpack` 安装恒落 `.minecraft` 根**。[MrPackInstallerService.cs](../../../dotnet/src/Starcloud.Core/Services/MrPackInstallerService.cs#L46-L76)
   的 `InstallAsync` 写死 `settings.DotMinecraft`，无法装进隔离实例，
   与 A 组已建立的「隔离 / 共享 / 全局跟随」三态布局脱节。
4. **没有版本级备份**。只有存档备份（[SaveService.Backup](../../../dotnet/src/Starcloud.Core/Services/SaveService.cs#L68-L81)，
   产出 `<gameRoot>/backups/<世界名>-<时间>.zip`）。
   改错一个 Mod、升级加载器失败、误删版本内容都**没有退路**；也没有「回档」概念。
5. **导出内容无法覆盖非标准目录**。`InstanceLayout.ContentDirs`
   （[InstanceSettings.cs](../../../dotnet/src/Starcloud.Core/Services/InstanceSettings.cs#L198)）
   只有 `mods / saves / config / resourcepacks / shaderpacks` 五个白名单目录。
   投影模组产生的 `schematics/`、`litematica/`，以及 `scripts/`、`kubejs/`、`openloader/` 等
   都在这之外——**照白名单导出会静默丢内容**，这正是用户点名要避免的。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| B 组范围 | **导出整合包 + 导入本地整合包 + 版本备份与回档**，三项全做 |
| 导出内容 | **导出时由用户勾选 / 自定义要打包的目录与内容**，不写死白名单；必须能导出投影等非标准目录 |
| 备份粒度 | **由用户选择粒度和内容**（不是固定档位） |
| 导出格式 | 仍用 `.mrpack`（`modrinth.index.json` + `overrides/`），与现有导入链路、Modrinth 生态互通 |
| 本地模组身份 | 导出时批量 SHA1 反查 Modrinth：**能识别的写进 `files[]`（对方下载），识别不了的打进 `overrides/`（对方解压即用）** |
| 备份存储位置 | `<dotMc>/starcloud/backups/`（沿用 `starcloud/` 已放 icons、version-settings.json 的约定），**不放进版本目录**（避免递归） |
| 备份格式 | zip（沿用 `SaveService.Backup` 的既有模式） |
| 回档安全网 | 回档前**自动先备份当前状态**，可反悔 |

### 与既有设计的偏离（需记录）

1. **导出整合包从「C 阶段」提前到 B 组**。
   [2026-09-09-version-management-design.md](2026-09-09-version-management-design.md#L13-L19)
   把「高级导出整合包」划给 C 阶段；用户在 B 组明确要求一并做。
   相应地 [VersionsPage.xaml](../../../dotnet/src/Starcloud.App/Views/VersionsPage.xaml#L48) 的
   ToolTip「C 阶段提供」必须同步删除。
2. **导出内容改为用户自选目录**，不再套用 `InstanceLayout.ContentDirs` 五目录白名单
   （A 组 spec 也沿用该白名单，但那是**装载与隔离迁移**场景，与「导出」目的不同）。

## 3. 数据模型

### 3.1 路径（`InstanceLayout` 追加）

```csharp
/// <summary>版本备份根目录（不放进版本目录，避免递归）</summary>
public static string BackupRoot(string dotMc) => Path.Combine(dotMc, "starcloud", "backups");

/// <summary>备份索引文件</summary>
public static string BackupIndexFile(string dotMc) =>
    Path.Combine(BackupRoot(dotMc), "backups.json");

/// <summary>把版本 id 变成可用目录名（去掉路径非法字符）</summary>
public static string SafeFileName(string name);
```

### 3.2 导出内容候选（App 层行模型）

```csharp
/// <summary>导出整合包的内容目录候选行</summary>
public sealed record ExportDirRow(
    string Name,          // 相对游戏根的目录名，如 "mods" / "schematics"
    string Detail,        // "128 项 · 236.4 MB"
    bool IsStandard,      // 是否属于常规内容目录（决定默认勾选）
    bool IsChecked);      // 初始勾选状态（双向绑定）
```

候选来源 = 预设清单 ∪ 实例根实际存在的一级子目录（排除运行期/公共目录），
因此 `schematics/`、`litematica/` 之类**只要存在就会出现在列表里**；
再提供「添加自定义目录…」兜底。

**排除名单**（不列入候选，防止导出体积失真或破坏对端）：
`libraries`、`assets`、`versions`、`logs`、`crash-reports`、`natives`、
`starcloud`、`.fabric`、`.mixin.out`、`backups`。

### 3.3 备份条目与索引

```csharp
public sealed class BackupEntry
{
    [JsonPropertyName("id")]         public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    [JsonPropertyName("version_id")] public string VersionId { get; set; } = "";
    [JsonPropertyName("name")]       public string Name { get; set; } = "";
    [JsonPropertyName("created_at")] public DateTime CreatedAt { get; set; } = DateTime.Now;
    [JsonPropertyName("size_bytes")] public long SizeBytes { get; set; }
    [JsonPropertyName("file_count")] public int FileCount { get; set; }
    /// <summary>zip 相对 BackupRoot 的路径</summary>
    [JsonPropertyName("zip_path")]   public string ZipPath { get; set; } = "";
    /// <summary>备份覆盖的内容标签：version / saves / shared / &lt;自定义目录名&gt;</summary>
    [JsonPropertyName("includes")]   public List<string> Includes { get; set; } = [];
}

public sealed class BackupIndex
{
    [JsonPropertyName("entries")] public List<BackupEntry> Entries { get; set; } = [];
}
```

`backups.json` 写入沿用**原子写**（temp + `File.Move(overwrite: true)`）；
解析失败静默重置为空索引，并在备份列表里提供「重建索引」（按 `BackupRoot` 下实际 zip 反推）。

### 3.4 整合包包信息

```csharp
/// <summary>本地 .mrpack 的元信息（导入前预览用）</summary>
public sealed record MrPackPackageInfo(
    string Name,
    string VersionId,
    string GameVersion,
    IReadOnlyDictionary<string, string> Dependencies,
    int DownloadFileCount,
    int OverrideFileCount,
    long TotalBytes,
    int FormatVersion);
```

## 4. 服务设计

### 4.1 `ModPackExportService`（Core 新增）

```csharp
public sealed class ModPackExportService(ModrinthService mods)
{
    /// <summary>扫描实例根，列出可作为导出内容的一级目录（含预设 + 实际存在目录）</summary>
    public List<ExportDirCandidate> DiscoverDirs(string gameRoot);

    /// <summary>
    /// 导出为 .mrpack：勾选目录内的文件逐个算 SHA1 → 批量反查 Modrinth →
    /// 命中写 files[]、未命中进 overrides/ → 打包 zip。
    /// </summary>
    public Task<ExportResult> ExportAsync(
        ExportRequest request, IProgress<string>? status = null,
        CancellationToken ct = default);
}

public sealed record ExportDirCandidate(string Name, bool IsStandard, int FileCount, long SizeBytes);

public sealed record ExportRequest(
    string GameRoot,            // 源实例根
    string TargetZipPath,
    string Name,                // 包显示名
    string GameVersion,         // MC 版本
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyList<string> Dirs  // 勾选的相对目录名
);

public sealed record ExportResult(
    int DownloadableFiles, int OverriddenFiles, int SkippedFiles, long TotalBytes, string? LoaderWarning);
```

**算法**：

1. 递归枚举每个勾选目录 → 得到 `(相对 gameRoot 的 posix 路径, 绝对路径)` 列表；
   跳过 `*.log`、`.DS_Store`、`Thumbs.db` 等噪音文件。
2. 后台线程逐个算 SHA1 → `ModrinthService.GetVersionsByHashesAsync`（已有，100/批分片）。
3. 命中且 `PrimaryFile.Url` 非空 →
   `files[]` 条目 `{ path, hashes:{sha1}, downloads:[url], fileSize }`。
4. 未命中 / 无下载地址 / 反查异常 → 复制进 `overrides/<相对路径>`。
5. 写 `modrinth.index.json`：
   ```json
   {
     "formatVersion": 1,
     "name": "<Name>",
     "versionId": "1.0.0",
     "gameVersion": "<GameVersion>",
     "dependencies": { "minecraft": "...", "fabric-loader": "..." },
     "files": [ ... ]
   }
   ```
6. zip 打包到临时文件 → `File.Move(temp, target, overwrite: true)`。
7. **加载器依赖推断**（`DependencyResolver` 私有逻辑）：
   - 优先 `InstallerService.LoadVersionAsync` 读该版本 JSON 的 `inheritsFrom`；
   - `versions/<id>` 命名含 `fabric` 且存在 `fabric-loader-<ver>-<mc>` 目录 → `fabric-loader`;
   - id 形如 `<mc>-forge-<build>` 或目录内 json 含 `net.minecraftforge` → `forge = <build>`;
   - 含 `neoforge` → `neoforge = <build>`（URL 规律见
     [ForgeInstallerService.cs](../../../dotnet/src/Starcloud.Core/Services/ForgeInstallerService.cs#L127-L186)）；
   - 推断不出 → 只写 `minecraft`，返回 `LoaderWarning` 供 UI 提示「未识别加载器，对端需自行安装」。

### 4.2 `MrPackInstallerService` 扩展（Core 修改）

```csharp
/// <summary>读取 .mrpack 元信息（不落地），用于导入前预览</summary>
public MrPackPackageInfo ReadPackageInfo(string mrpackPath);

/// <summary>安装：installRoot 为空时落 .minecraft 根（保持既有行为）</summary>
public async Task<MrPackInstallResult> InstallAsync(
    string mrpackPath, LauncherSettings settings, string? installRoot = null,
    IProgress<string>? status = null, IProgress<BatchProgress>? progress = null,
    CancellationToken ct = default)
```

- `ReadPackageInfo` 复用现有解压/解析逻辑，额外统计 `overrides/` 文件数与 `formatVersion`。
- `InstallAsync` 内部把 `var dotMc = settings.DotMinecraft;` 改为
  `var root = installRoot ?? settings.DotMinecraft;`，其余（下载 + overrides 覆盖）不变。
- 现有调用点（`DownloadPage.InstallMrPackAsync`）不传 `installRoot`，**行为零变化**。

### 4.3 `VersionBackupService`（Core 新增）

```csharp
public sealed class VersionBackupService
{
    public IReadOnlyList<BackupEntry> List(string dotMc, string? versionId = null);
    public Task<BackupEntry> BackupAsync(string dotMc, VersionBackupRequest request,
        IProgress<string>? status = null, CancellationToken ct = default);
    /// <summary>回档：先自动快照当前状态，再按 includes 覆盖还原</summary>
    public Task RestoreAsync(string dotMc, BackupEntry entry, bool isolated,
        IProgress<string>? status = null, CancellationToken ct = default);
    public void Delete(string dotMc, string entryId);
    /// <summary>索引损坏/外部手工放入 zip 后的修复入口</summary>
    public IReadOnlyList<BackupEntry> RebuildIndex(string dotMc);
}

public sealed record VersionBackupRequest(
    string VersionId,
    bool Isolated,
    string Name,
    IReadOnlyList<string> Dirs,   // 勾选的相对目录名（含 "versions/<id>" 表示版本目录本体）
    bool IncludeSaves);
```

**备份算法**：

- 收集 `(zipEntryPath, absPath)`：zip 内路径统一用**相对 `.minecraft` 根**的 posix 路径
  （如 `versions/<id>/mods/a.jar`、`mods/b.jar`、`saves/world/...`、`schematics/x.schem`），
  这样回档时天然知道该往哪还原。
- 排除：`logs/`、`crash-reports/`、`starcloud/backups/`、版本目录下 `*.log`。
- 后台逐个 `ZipArchive.CreateEntry` 流式写入 + 进度回调（文件数 + 字节）。
- 临时文件写完 → `File.Move` 落盘 → 更新 `backups.json`（原子写）。
- 与运行状态互斥：调用方（UI）在 `App.RunningVersionId == versionId` 时禁止备份/回档。

**回档算法**：

1. 先 `BackupAsync` 一次（名称 `回档前自动备份-<yyyy-MM-dd_HHmmss>`，内容 = 与本次备份相同的 `Includes`），
   作为安全网。
2. 按 `entry.Includes` 计算目标范围，**逐个删除范围内的现有内容**（只删范围，不删其它）。
3. 解压 zip 内条目覆盖到 `.minecraft` 根（`versions/<id>/...`、`mods/...` 等按 zip 内路径落位）。
4. 刷新版本列表。

## 5. 界面设计

### 5.1 版本页改造（`VersionsPage.xaml(.cs)`）

**a. 菜单项**（[VersionsPage.xaml](../../../dotnet/src/Starcloud.App/Views/VersionsPage.xaml#L43-L50)）

- `导出整合包`：去掉 `IsEnabled="False"`、`Click="Export_Click"`、删除 ToolTip「C 阶段提供」。
- 新增 `导入整合包`（`Click="Import_Click"`，`FontIcon` `&#xE896;`）。
- 新增 `备份与回档`（`Click="Backup_Click"`，`FontIcon` `&#xE81C;`）。

**b. 工具行**（L96-L115）新增按钮「导入整合包」（放「刷新」前），文案简洁。

**c. 整页拖拽**：`AllowDrop="True"` + `DragOver`/`Drop`，
把 `.mrpack` 或含 `modrinth.index.json` 的 `.zip` 拖进版本页 → 触发导入流程。

### 5.2 导出对话框（`Views/ModPackDialogs.cs` 新增静态构造方法）

```
┌ 导出整合包 ─────────────────────────────────┐
│ 源版本：1.20.1-fabric        MC 1.20.1 · Fabric 0.15.11 │
│                                              │
│ 打包内容            [仅 Mod 与配置] [全部内容] [全部（含存档）] │
│ ☑ mods          128 项 · 236.4 MB           │
│ ☑ config         42 项 · 1.2 MB              │
│ ☑ schematics      7 项 · 3.4 MB   （投影）    │
│ ☐ saves          3 项 · 512 MB               │
│ ☐ litematica     2 项 · 1.1 MB               │
│ [ + 添加自定义目录… ]                          │
│                                              │
│ 预计导出体积：约 241 MB（可下载 120 项 / 打包 60 项） │
│                       [取消] [导出…]          │
└──────────────────────────────────────────────┘
```

- 三分区快捷按钮：`仅 Mod 与配置`（mods/config）/ `全部内容（不含存档）` /
  `全部（含存档）`——一键改写勾选，用户仍可再微调（满足「用户自己选」）。
- 点「导出…」→ `FileSavePicker` 选路径 → 进度对话框（`status` 文案 + 进度条）→ 完成摘要
  （可下载 N / 打包 M / 体积 / 加载器提示）。

### 5.3 导入对话框

```
┌ 导入整合包 ─────────────────────────────────┐
│ 包名：Fabulously Optimized                    │
│ Minecraft 1.20.1 · Fabric 0.15.11 · 42 个文件 · 12.4 MB │
│                                              │
│ 新建版本名称：[ Fabulously Optimized      ]   │
│ ☑ 版本隔离（推荐，整合包内容独立存放）          │
│                                              │
│                       [取消] [导入]           │
└──────────────────────────────────────────────┘
```

- 非法包（无 `modrinth.index.json`）→ 明确报错弹窗，提示「本启动器支持 Modrinth 格式整合包
  （.mrpack 或内含 modrinth.index.json 的 zip）」。
- 名称冲突 → 自动 ` (2)` 递增。
- 导入中显示进度；联动装原版 + 加载器复用 **5.5 公共联动逻辑**。

### 5.4 备份与回档对话框

**备份页**：

```
┌ 备份与回档 · 1.20.1-fabric ─────────────────┐
│ 备份粒度： ○ 最小   ● 标准   ○ 完整          │
│ 附加内容： ☑ 世界存档  ☑ 配置  ☐ 资源包  ☐ 光影│
│ 备份名称：[ 1.20.1-fabric-2026-09-11_143012 ]│
│ 预计体积：约 1.2 GB                          │
│                      [取消] [开始备份]        │
├──────────────────────────────────────────────┤
│ 已有备份：                                    │
│  回档前自动备份-2026-09-11_1420  1.1 GB  刚刚  │
│  1.20.1-fabric-2026-09-11_1400   1.1 GB  2h前  │
│                    [回档] [删除] [打开备份目录] │
└──────────────────────────────────────────────┘
```

- 粒度三档一键设置附加勾选（仍可逐项改）：
  - 最小 = 版本定义 + 版本设置（`versions/<id>/<id>.json` + `starcloud/version-settings.json` 中该条）
  - 标准 = 版本目录整体（隔离实例的全部内容）或共享实例的内容目录
  - 完整 = 标准 + 共享 `.minecraft` 内容目录
- 回档：二次确认（写明「会先自动备份当前状态」）→ 先快照 → 覆盖还原 → 提示。
- 版本运行中（`App.RunningVersionId == id`）→ 备份/回档按钮禁用 + 文案说明。

### 5.5 公共联动安装（`Views/MrPackFlow.cs` 新增）

把 [DownloadPage.InstallMrPackAsync](../../../dotnet/src/Starcloud.App/Views/DownloadPage.xaml.cs#L1180-L1243)
里「装原版 + 按 `Dependencies` 装 Fabric/Forge/NeoForge」的段落抽成：

```csharp
internal static class MrPackFlow
{
    /// <summary>按整合包依赖装原版与加载器，返回中文摘要；失败不抛出，返回错误描述</summary>
    public static Task<string> EnsureGameAndLoaderAsync(
        MrPackInstallResult result, AppServices s, IProgress<BatchProgress>? progress);
}
```

`DownloadPage.InstallMrPackAsync` 与 `VersionsPage` 导入流程都调用它，避免两处漂移。

## 6. 文件清单

**Core 新增**
- `dotnet/src/Starcloud.Core/Services/ModPackExportService.cs` —— 目录发现 / SHA1 反查 /
  `files[]` + `overrides/` 生成 / 加载器依赖推断
- `dotnet/src/Starcloud.Core/Services/VersionBackupService.cs` —— 备份 zip / 回档 / 索引 / 重建

**Core 修改**
- `dotnet/src/Starcloud.Core/Services/MrPackInstallerService.cs` —— `ReadPackageInfo`、
  `InstallAsync(installRoot)`、`MrPackPackageInfo`
- `dotnet/src/Starcloud.Core/Services/InstanceSettings.cs` —— `InstanceLayout.BackupRoot` /
  `BackupIndexFile` / `SafeFileName`

**App 新增**
- `dotnet/src/Starcloud.App/Views/ModPackDialogs.cs` —— 导出 / 导入 / 备份回档 三个对话框
- `dotnet/src/Starcloud.App/Views/MrPackFlow.cs` —— 原版 + 加载器联动安装公共逻辑

**App 修改**
- `dotnet/src/Starcloud.App/Views/VersionsPage.xaml(.cs)` —— 启用导出、新增导入/备份入口与拖拽
- `dotnet/src/Starcloud.App/Views/DownloadPage.xaml.cs` —— `InstallMrPackAsync` 改调 `MrPackFlow`
- `dotnet/src/Starcloud.App/Views/PageRows.cs` —— `ExportDirRow` / `BackupRow`
- `dotnet/src/Starcloud.App/Services/AppServices.cs` —— 注册 `ModPackExportService` / `VersionBackupService`

**文档**
- `docs/USER_MANUAL.md` —— 新增「整合包导出 / 导入」「版本备份与回档」章节

## 7. 不做的事（明确排除）

- 不接 CurseForge 导出/导入（仍只 Modrinth 格式）
- 不做增量 / 差异备份，不做定时自动备份，不做云同步
- 不打包 `assets` / `libraries` / Java 运行时（对端按版本 JSON 自动补齐）
- 不做「无 `modrinth.index.json` 的任意 zip」格式猜测转换（明确报错，不猜）
- 不做备份加密 / 压缩率调节

## 8. 验收标准

**构建**：`dotnet build dotnet/src/Starcloud.App/Starcloud.App.csproj` 0 警告 0 错误。

**手动验收清单**

1. **导出含投影**：给实例放入 `schematics/test.schem` 与 `mods/<某 Mod>.jar` →
   导出时列表里出现 `schematics` 且可勾选 → 导出的 `.mrpack` 里
   `overrides/schematics/test.schem` 存在（或 `files[]` 命中 Modrinth 时走下载）。
2. **导出互通**：导出的 `.mrpack` 在导入流程里能被 `ReadPackageInfo` 正确读出
   MC 版本、加载器、文件数、体积。
3. **自定义目录**：导出对话框「添加自定义目录」填 `kubejs` → 打包内容里包含它。
4. **加载器推断**：导出一个 Fabric 实例 → `dependencies` 含 `fabric-loader`；
   导出一个未识别加载器的实例 → 提示「未识别加载器」，`dependencies` 只含 `minecraft`。
5. **导入建版本**：导入本地 `.mrpack` → 版本列表出现新版本；隔离选项开启时
   文件落在 `versions/<新id>/mods/`，共享时落在 `.minecraft/mods/`。
6. **导入联动**：本地包声明的 MC 版本未安装 → 导入后自动装原版 + 加载器，
   提示「已联动安装 Minecraft X + Fabric Y」。
7. **非法包**：导入一个普通 zip（无 `modrinth.index.json`）→ 明确报错，不产生半个版本。
8. **备份粒度**：以「标准」粒度备份隔离实例 → 回档前自动备份出现；
   zip 内路径为 `versions/<id>/...`（用压缩软件可验证）。
9. **回档还原**：删除实例内一个 Mod → 从备份回档 → 该 Mod 回来，其余文件保持；
   回档前自动备份可再次回档回滚这一步。
10. **运行中保护**：版本正在运行时点备份/回档 → 被禁用并给出说明，不产生损坏备份。
11. **索引修复**：手工删掉 `backups.json` → 打开备份面板仍可「重建索引」列出已有 zip。

## 9. 风险与对策

| 风险 | 对策 |
|---|---|
| 导出时逐个文件算 SHA1 慢（大整合包上千文件） | 后台线程 + 进度文案「正在校验文件（i/N）」；只对**勾选目录**内的文件算，不做全盘扫描 |
| 反查 Modrinth 触发限流 | 复用 A 组既有分片（100/批）+ 进程内缓存；导出是低频用户触发操作 |
| `overrides/` 体积过大（识别率低时） | 对话框实时显示「可下载 / 打包」项数预估；对极大目录给出提示 |
| 回档覆盖造成二次损失 | 回档前**强制自动快照**；只删除 `Includes` 覆盖范围内内容，不整盘清理 |
| zip 内路径穿越（`../`）导致还原越界 | 解压前校验每个 entry 的相对路径，拒绝含 `..` 或以盘符开头的条目 |
| 备份写入中断留下半个 zip | 先写 `.tmp` 再 `File.Move` 落盘；索引只在 zip 落盘成功后更新 |
| 隔离/共享判定错导致还原到错位置 | 备份时把 zip 内路径固定为「相对 `.minecraft` 根」，还原时不再做隔离推理 |
| 导出包对端导入后内容缺失（识别成 `files[]` 但下载失效） | `files[]` 仅在 `PrimaryFile.Url` 非空时写入；否则一律退回 `overrides/` |
