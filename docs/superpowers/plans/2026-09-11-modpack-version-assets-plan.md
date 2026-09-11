# 整合包导出 · 本地整合包导入 · 版本备份与回档 实现计划

日期：2026-09-11
设计文档：docs/superpowers/specs/2026-09-11-modpack-version-assets-design.md（commit 8db189a）
所属：PCL2 平替第 B 组（整合包与版本资产）

构建命令（每个任务完成后执行）：

```powershell
Stop-Process -Name Starcloud.App -Force -ErrorAction SilentlyContinue
dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo
```

目标：0 警告 0 错误。

## 计划期微修（相对 spec 的实现决定，不改变设计与验收）

- **导出结果的体积预估**：spec §5.2 要求对话框显示「可下载 N / 打包 M」。反查 Modrinth 需要网络与 SHA1 计算，
  **不在勾选阶段进行**；勾选阶段只显示「目录项数 + 本地体积」，导出完成后回报真实的
  `DownloadableFiles / OverriddenFiles`。对话框文案相应写成「预计导出体积（本地）」，不承诺识别率。
- **版本 JSON 骨架**：隔离导入时 `versions/<newId>/<newId>.json` 写
  `{ "id": "<newId>", "inheritsFrom": "<MC 版本>", "type": "release" }`，
  让 `ManifestService.ListInstalled` 立即识别、`InstallerService.LoadVersionAsync` 能一层合并到原版。
- **`DiscoverDirs` 的 `IsStandard`**：仅用于勾选默认值，非白名单。
  预设 = `mods / config / resourcepacks / shaderpacks / datapacks`（标准）；
  `saves / schematics / litematica / schematica / scripts / kubejs / openloader / defaultconfigs`（非标准，列出现存项）。
- **`ListInstalled` 依赖的是 `versions/<id>/<id>.json`**；共享导入不建版本目录，因此不产生新版本条目——
  导入对话框在「不隔离」时提示「将写入共享目录，不会新建版本条目」。
- **备份的版本设置**：`starcloud/version-settings.json` 是全实例共用单文件，无法只摘出某版本的一条。
  「最小」粒度备份该文件**整份**，回档时整份还原（在对话框文案里说明）。

## 前置确认（已核实，实现时不再重复探索）

- **`InstanceLayout`**：`ContentDirs`(L198) / `IsIsolated`(L201–207) / `GameRoot`(L210–211) /
  `NextAvailablePath`(L305–316，`stem (n)` 从 2 递增) / `ContentDirFor`(L318–326) /
  `RenameInstance`(L329) / `ValidateVersionId`(L373–380，private) / `CopyDirectory`(L413–422，private)。
  → **新增 `BackupRoot` / `BackupIndexFile` / `SafeFileName` 放在 `ContentDirFor`(L326) 之后。**
  `ValidateVersionId` 为 private，App 侧判定名称合法性需要**公开一个 `IsValidVersionId`**（或在 App 内自查），
  决定：在 `InstanceLayout` 新增 `public static bool IsValidVersionId(string id)`，`ValidateVersionId` 内部改调它。
- **`MrPackInstallerService`**：`InstallAsync`(L28–82)，`dotMc` 取自 `settings.DotMinecraft`(L46)；
  DTO 为 `file` 作用域类 `MrPackIndex`(L99–112) / `MrPackFile`(L114–127) / `MrPackHashes`(L129–133)；
  `MrPackIndex` 字段 = `FormatVersion` / `GameVersion` / `Dependencies` / `Files`，**无 `name` / `versionId`** →
  需补 `Name`(`name`) / `VersionId`(`versionId`) 两个可空字段（`JsonPropertyName`）。
- **`ModrinthService`**：`GetVersionsByHashesAsync(sha1s, ct)`(A 组新增，100/批) →
  `Dictionary<string, ModrinthVersion>`；`ModrinthVersion.PrimaryFile` / `Files[i].Url` / `.Sha1` / `.Size`。
- **`DownloadService`**：`DownloadBatchAsync(batch, "阶段名", concurrency, maxRetries, progress, ct)`。
- **`AppServices`**：字段 L11–37，ctor L40–71；`Mods = new ModrinthService(Download, ResourceCache)`(L50)；
  `MrPack = new MrPackInstallerService(Download)`(L55)；`Saves = new SaveService()`(L56)。
  → `ModPackExport` 注册在 `MrPack`(L55) 之后；`VersionBackup` 紧随其后。
- **`DownloadPage.xaml.cs`**：`InstallMrPackAsync`(L1180–1243) 内含「装原版 + Fabric/Forge/NeoForge」段落；
  `_s` 字段(未标注行号，文件里为 `private readonly AppServices _s = AppServices.Instance;`)。
- **`VersionsPage.xaml`**：菜单项 L43–50；工具行 StackPanel L96–115；Page 根 L2–7 无 `AllowDrop`。
- **`VersionsPage.xaml.cs`**：`_s`(L15) / `_menuRow`(L101) / `RowOf(sender)`(L107–108) /
  `RefreshList()`(L24–51) / `FormatSize`(L211–217) / 删除版本 L174–209（`App.RunningVersionId` 用法见 L179）。
- **存档备份惯例**：`SaveService.Backup`(L68–81) = `ZipFile.CreateFromDirectory` + 时间戳文件名。

---

## T1：Core · 备份路径与版本名校验

文件：`dotnet/src/Starcloud.Core/Services/InstanceSettings.cs`

1. `ValidateVersionId`(L373–380) 拆出公开判定：

```csharp
/// <summary>版本名是否合法（非空、不含路径非法字符、不是 . / ..）</summary>
public static bool IsValidVersionId(string id) =>
    !string.IsNullOrWhiteSpace(id) && id is not ("." or "..") &&
    id.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) < 0;

private static void ValidateVersionId(string id)
{
    if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("版本名不能为空");
    if (!IsValidVersionId(id)) throw new ArgumentException("版本名不能包含 < > : \" / \\ | ? * 或路径符号");
}
```

2. `ContentDirFor`(L326) 之后新增：

```csharp
/// <summary>版本备份根目录（不放进版本目录，避免递归）</summary>
public static string BackupRoot(string dotMc) => Path.Combine(dotMc, "starcloud", "backups");

/// <summary>备份索引文件</summary>
public static string BackupIndexFile(string dotMc) =>
    Path.Combine(BackupRoot(dotMc), "backups.json");

/// <summary>把任意名称变成可用文件名（去掉路径非法字符）</summary>
public static string SafeFileName(string name)
{
    var cleaned = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
    return cleaned.Length > 0 ? cleaned : "backup";
}
```

验证：构建（0 警告 0 错误）。

---

## T2：Core · MrPackInstallerService 扩展

文件：`dotnet/src/Starcloud.Core/Services/MrPackInstallerService.cs`

1. 文件顶部新增 `using Starcloud.Core.Services;` 不需要（同命名空间）；需 `System.Security.Cryptography` 不需要。
2. 新增记录（`MrPackInstallResult` 之后）：

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

3. `InstallAsync`(L28) 增加可选参数并改落点：

```csharp
public async Task<MrPackInstallResult> InstallAsync(
    string mrpackPath, LauncherSettings settings, string? installRoot = null,
    IProgress<string>? status = null, IProgress<BatchProgress>? progress = null,
    CancellationToken ct = default)
...
var dotMc = installRoot ?? settings.DotMinecraft;   // 原 L46
```

4. 新增 `ReadPackageInfo`：

```csharp
/// <summary>读取 .mrpack 元信息（解压到临时目录后统计，不落地任何游戏文件）</summary>
public MrPackPackageInfo ReadPackageInfo(string mrpackPath)
{
    var tempDir = Path.Combine(Path.GetTempPath(), "starcloud-mrpack", Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(tempDir);
    try
    {
        ZipFile.ExtractToDirectory(mrpackPath, tempDir, overwriteFiles: true);
        var indexPath = Path.Combine(tempDir, "modrinth.index.json");
        if (!File.Exists(indexPath))
            throw new InvalidOperationException("不是有效的 .mrpack 整合包（缺少 modrinth.index.json）");
        var index = JsonSerializer.Deserialize<MrPackIndex>(File.ReadAllText(indexPath), JsonOpts)
                    ?? throw new InvalidOperationException("modrinth.index.json 解析失败");
        var overridesDir = Path.Combine(tempDir, "overrides");
        var overrideCount = Directory.Exists(overridesDir)
            ? Directory.EnumerateFiles(overridesDir, "*", SearchOption.AllDirectories).Count() : 0;
        return new MrPackPackageInfo(
            string.IsNullOrWhiteSpace(index.Name) ? Path.GetFileNameWithoutExtension(mrpackPath) : index.Name,
            index.VersionId ?? "",
            index.Dependencies.GetValueOrDefault("minecraft") ?? "",
            index.Dependencies,
            index.Files.Count(f => f.Downloads.Count > 0),
            overrideCount,
            index.Files.Sum(f => Math.Max(f.Size, 0)),
            index.FormatVersion);
    }
    finally
    {
        try { Directory.Delete(tempDir, recursive: true); } catch { }
    }
}
```

5. `MrPackIndex`(L99–112) 补两个字段（放在 `GameVersion` 之后）：

```csharp
[JsonPropertyName("name")]
public string? Name { get; set; }

[JsonPropertyName("versionId")]
public string? VersionId { get; set; }
```

验证：构建（0 警告 0 错误）；`DownloadPage` 既有调用不传 `installRoot`，行为不变。

---

## T3：Core · ModPackExportService

文件：新增 `dotnet/src/Starcloud.Core/Services/ModPackExportService.cs`

1. 头部：`// 导出 .mrpack 整合包：目录发现 → SHA1 反查 Modrinth → files[] + overrides 打包`；
   `using System.IO.Compression; using System.Security.Cryptography; using System.Text.Json; using System.Text.Json.Serialization;`
2. records：

```csharp
/// <summary>导出时的候选目录（扫描实例根得到）</summary>
public sealed record ExportDirCandidate(string Name, bool IsStandard, int FileCount, long SizeBytes);

/// <summary>导出请求</summary>
public sealed record ExportRequest(
    string GameRoot,
    string TargetZipPath,
    string Name,
    string GameVersion,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyList<string> Dirs);

/// <summary>导出结果</summary>
public sealed record ExportResult(
    int DownloadableFiles, int OverriddenFiles, int SkippedFiles, long TotalBytes, string? LoaderWarning);
```

3. 服务：

```csharp
public sealed class ModPackExportService(ModrinthService mods)
{
    /// <summary>运行期/公共目录，不作为导出候选</summary>
    private static readonly HashSet<string> Excluded = new(StringComparer.OrdinalIgnoreCase)
    {
        "libraries", "assets", "versions", "logs", "crash-reports", "natives",
        "starcloud", ".fabric", ".mixin.out", "backups",
    };

    private static readonly string[] StandardDirs =
        ["mods", "config", "resourcepacks", "shaderpacks", "datapacks"];

    public List<ExportDirCandidate> DiscoverDirs(string gameRoot);
    public Task<ExportResult> ExportAsync(ExportRequest request,
        IProgress<string>? status = null, CancellationToken ct = default);
}
```

4. `DiscoverDirs`：`StandardDirs` 全部列入（即使不存在，`FileCount=0`，`IsStandard=true`）∪
   `gameRoot` 下实际存在的一级目录（排除 `Excluded`，`IsStandard=false`），按名称排序；
   统计 `FileCount` / `SizeBytes`（跳过噪音文件）。
5. `ExportAsync`（`Task.Run` 内跑，`ct` 检查）：
   - 枚举勾选目录 → `List<(string Rel, string Abs)>`（posix 相对路径，相对 `GameRoot`）；噪音跳过并计 `SkippedFiles`。
   - 逐个 `SHA1.HashData(File.ReadAllBytes(...))`（大文件用流式 `SHA1.Create().ComputeHash(stream)`）→ 十六进制小写。
   - `Mods.GetVersionsByHashesAsync(sha1s, ct)`；命中且 `PrimaryFile?.Url` 非空 → `files[]` 条目。
   - 其余 → `overrides/<Rel>` 复制进临时目录。
   - 写 `modrinth.index.json`（`formatVersion:1` + `name` + `versionId:"1.0.0"` + `gameVersion` + `dependencies` + `files[]`）。
   - `ZipFile.CreateFromDirectory(tempDir, tmpZip)` → `File.Move(tmpZip, TargetZipPath, overwrite:true)`。
   - 反查异常 → 全部回退 `overrides`，不抛（记录到 `LoaderWarning`? 不——静默降级，见风险表）。
6. `files[]` DTO 用私有 `file` 作用域类：`ExportIndex` / `ExportFile` / `ExportHashes`。

验证：构建（0 警告 0 错误）。

---

## T4：Core · VersionBackupService

文件：新增 `dotnet/src/Starcloud.Core/Services/VersionBackupService.cs`

1. 头部：`// 版本备份与回档：zip 快照 + backups.json 索引 + 安全网回档`；
   `using System.IO.Compression; using System.Text.Json; using System.Text.Json.Serialization;`
2. 模型（照抄 spec §3.3）：`BackupEntry` / `BackupIndex`。
3. `VersionBackupRequest(string VersionId, bool Isolated, string Name, IReadOnlyList<string> Dirs)`。
4. 服务：

```csharp
public sealed class VersionBackupService
{
    public IReadOnlyList<BackupEntry> List(string dotMc, string? versionId = null);
    public Task<BackupEntry> BackupAsync(string dotMc, VersionBackupRequest request,
        IProgress<string>? status = null, CancellationToken ct = default);
    public Task RestoreAsync(string dotMc, BackupEntry entry,
        IProgress<string>? status = null, CancellationToken ct = default);
    public void Delete(string dotMc, string entryId);
    public IReadOnlyList<BackupEntry> RebuildIndex(string dotMc);
}
```

5. 备份：zip 内路径 = 相对 `dotMc` 的 posix 路径；集合来源 =
   - `Dirs` 里的普通目录名 → `<dotMc>/<名>`（若 `Isolated` 且该目录属于内容目录，则源为
     `versions/<id>/<名>`，但 zip 内路径仍写 `versions/<id>/<名>`）；
   - 特殊值 `"version"` → `versions/<id>` 整体（zip 内路径 `versions/<id>/...`）；
   - 特殊值 `"saves"`：隔离版本取 `versions/<id>/saves`，共享版本取 `<dotMc>/saves`；
   - 排除 `logs` / `crash-reports` / `starcloud/backups` / 版本目录下 `*.log`。
   写 `.tmp` → `File.Move` → 更新索引（原子写）。
6. 回档：
   - 先自动快照：`BackupAsync(dotMc, new VersionBackupRequest(entry.VersionId, ..., includes 同 entry))`
     名称 `回档前自动备份-<yyyy-MM-dd_HHmmss>`。
   - 计算目标范围（同备份集合）→ 删除范围内现存内容 → 解压 zip 条目覆盖
     （entry 路径校验：拒绝含 `..` 或以盘符开头者）。
7. `RebuildIndex`：扫 `BackupRoot` 下所有 `*.zip`，反推 `VersionId`（zip 名首段/目录）与大小，重建索引。

验证：构建（0 警告 0 错误）。

---

## T5：App · 服务注册 + 行模型 + MrPackFlow 抽取

文件：`dotnet/src/Starcloud.App/Services/AppServices.cs`、`dotnet/src/Starcloud.App/Views/PageRows.cs`、
新增 `dotnet/src/Starcloud.App/Views/MrPackFlow.cs`

1. `AppServices`：字段区新增

```csharp
public ModPackExportService ModPackExport { get; }
public VersionBackupService VersionBackup { get; }
```

ctor 中 `MrPack = new MrPackInstallerService(Download);`(L55) 之后：

```csharp
ModPackExport = new ModPackExportService(Mods);
VersionBackup = new VersionBackupService();
```

2. `PageRows.cs` 追加行模型：

```csharp
/// <summary>导出整合包的内容目录候选行（勾选状态可变）</summary>
public sealed class ExportDirRow
{
    public string Name { get; init; } = "";
    public string Detail { get; init; } = "";
    public string Badge { get; init; } = "";
    public bool IsStandard { get; init; }
    public bool IsChecked { get; set; }
}

/// <summary>备份列表行</summary>
public sealed record BackupRow(
    string Id, string Name, string TimeText, string SizeText, string IncludesText);
```

3. `MrPackFlow.cs`：把 `DownloadPage.InstallMrPackAsync` 里「装原版 + 加载器」段落搬来：

```csharp
internal static class MrPackFlow
{
    /// <summary>按整合包依赖装原版与加载器；返回中文摘要，失败返回带「失败」字样的说明</summary>
    public static async Task<string> EnsureGameAndLoaderAsync(
        MrPackInstallResult result, AppServices s, IProgress<BatchProgress>? progress)
    { /* 照搬 L1191–1222 逻辑，progress 透传 */ }
}
```

4. `DownloadPage.InstallMrPackAsync` 改为调用 `MrPackFlow.EnsureGameAndLoaderAsync(result, _s, progress)`，
   保留弹窗与 `dlg.Hide()` 收尾。

验证：构建（0 警告 0 错误）。

---

## T6：App · VersionsPage 入口

文件：`dotnet/src/Starcloud.App/Views/VersionsPage.xaml(.cs)`

1. XAML 菜单：`导出整合包` 去掉 `IsEnabled="False"` → `Click="Export_Click"`，删除 ToolTip；
   之后新增 `导入整合包`（`Click="Import_Click"`，Glyph `&#xE896;`）与
   `备份与回档`（`Click="Backup_Click"`，Glyph `&#xE896;` 换 `&#xE81C;`）。
2. 工具行 StackPanel(L96) 首位新增「导入整合包」按钮（`Click="Import_Click"`）。
3. Page 根加 `AllowDrop="True" DragOver="Page_DragOver" Drop="Page_Drop"`。
4. `.cs` 新增事件：`Export_Click` / `Import_Click` / `Backup_Click` / `Page_DragOver` / `Page_Drop`，
   分别转调 T7/T8/T9 的对话框方法（`row` 通过既有 `RowOf(sender)` 取；工具行/拖拽无行上下文时提示先选版本）。

验证：构建（0 警告 0 错误）。

---

## T7：App · 导出对话框

文件：新增 `dotnet/src/Starcloud.App/Views/ModPackDialogs.cs`

`public static class ModPackDialogs`，方法：

```csharp
/// <summary>导出整合包：目录勾选 → 选保存路径 → 进度 → 摘要</summary>
public static async Task ExportAsync(XamlRoot root, AppServices s, LocalVersionRow row);
```

要点：
- 读 `profile` / `IsIsolated` / `GameRoot`；`_s.ModPackExport.DiscoverDirs(gameRoot)` 建 `ExportDirRow` 列表。
- 内容 = `StackPanel`：信息行 + 三个快捷按钮 + `ListView`（`CheckBox` 双向绑定 `IsChecked`）+
  「添加自定义目录…」`TextBox` + 「添加」按钮 + 底部体积文本。
- `ContentDialog`（PrimaryButton「导出…」/ CloseButton「取消」），PrimaryButtonClick 需异步选路径 →
  用 `PrimaryButtonClick` 事件内 `args.Cancel = true` 再自行 `ShowAsync`/`Hide` 的惯用法，
  或先 `await dlg.ShowAsync()` 拿结果再 `FileSavePicker`。决定：**先 ShowAsync 拿结果**，
  再弹 `FileSavePicker`（`SuggestedFileName = "<id>-<yyyyMMdd>.mrpack"`）。
- 生成 `Dependencies`：调用新增的私有 `ResolveDependencies(dotMc, versionId)`（放本文件），
  逻辑按 spec §4.1 步骤 7。
- 进度对话框复用 `CreateProgressPanel(out statusText, out bar)` 私有方法。
- 完成后 `ContentDialog` 摘要：`可下载 N 个 / 打包 M 个 / 共 X`，若有 `LoaderWarning` 附加提示。

验证：构建（0 警告 0 错误）。

---

## T8：App · 导入对话框

文件：`dotnet/src/Starcloud.App/Views/ModPackDialogs.cs`（同一文件追加）

```csharp
/// <summary>导入本地整合包（.mrpack 或含 index 的 zip）</summary>
public static async Task ImportAsync(XamlRoot root, AppServices s, string mrpackPath);
```

要点：
- `s.MrPack.ReadPackageInfo(path)`；异常 → 错误弹窗（文案见 spec §5.3）。
- 确认弹窗：包信息文案 + 名称 `TextBox`（默认 `SafeFileName(info.Name)`）+ `CheckBox`「版本隔离」（默认勾选）。
- 名称冲突 → `for (i=2;;i++)` 找 `versions/<name> (i)` 不存在者；名称非法 → 提示重输（循环）。
- 安装：
  ```
  root = isolated ? Path.Combine(dotMc, "versions", newId) : dotMc
  if (isolated) 写 versions/<newId>/<newId>.json 骨架
  await s.MrPack.InstallAsync(path, s.Settings, root, progress: progress)
  var summary = await MrPackFlow.EnsureGameAndLoaderAsync(result, s, progress)
  new InstanceSettingsStore(dotMc).Set(newId, new VersionProfile { Isolation = isolated ? "isolated" : "shared" })
  ```
- 进度弹窗（可「后台导入」）+ 结果 InfoBar/摘要。

验证：构建（0 警告 0 错误）。

---

## T9：App · 备份与回档对话框

文件：`dotnet/src/Starcloud.App/Views/ModPackDialogs.cs`（同一文件追加）

```csharp
/// <summary>版本备份与回档：粒度/内容勾选 + 备份列表（回档/删除/打开目录）</summary>
public static async Task BackupAsync(XamlRoot root, AppServices s, LocalVersionRow row);
```

要点：
- 运行中（`App.RunningVersionId == row.Id`）→ 显示说明弹窗并 `return`。
- 面板 = 上半「新建备份」（粒度 `RadioButtons` 三档 + 附加 `CheckBox` 项 + 名称 `TextBox` + 预计体积）
  + 下半「已有备份」`ListView`（`BackupRow`）+ 按钮「回档/删除/打开备份目录/重建索引」。
- 粒度切换 → 改写勾选项；`标准` = `version` 标签；`完整` = `version` + 共享内容目录。
- 备份：`_s.VersionBackup.BackupAsync(...)` + 进度弹窗；完成后刷新列表。
- 回档：二次确认（含「会先自动备份当前状态」）→ `RestoreAsync` + 进度 → 提示 → 刷新。
- 删除：确认 → `Delete` → 刷新。打开目录：`Process.Start`。
- 列表空 + 目录有 zip → 提示可「重建索引」。

验证：构建（0 警告 0 错误）。

---

## T10：文档

文件：`docs/USER_MANUAL.md`

新增两节（插入在「版本内容管理」之后，后续章节重编号）：
「整合包导出与导入」「版本备份与回档」。含入口位置、操作步骤、注意事项
（导出识别率、投影目录需手动勾选、回档会先自动快照、运行中不可备份）。

验证：构建（0 警告 0 错误）。

---

## 收尾

- 全量构建通过后，一次性提交（B 组全部改动）。
- 提交信息：`实现整合包导出/导入与版本备份回档（B 组：整合包与版本资产）`
