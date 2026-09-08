# 启动前自动补全（Launch Preflight）实现计划

日期：2026-09-08
设计文档：docs/superpowers/specs/2026-09-08-launch-preflight-design.md（commit 498e373）

构建命令：`dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo`
（每个任务完成后构建验证，目标 0 警告 0 错误）

## T1：InstallerService 提取 ExtractNatives

文件：`dotnet/src/Starcloud.Core/Services/InstallerService.cs`

1. 把 `InstallVersionContentAsync` 内联的 natives 解压逻辑（L173-183）提取为公开静态方法：

```csharp
public static void ExtractNatives(string nativesJarPath, string nativesDir, ExtractSpec? extract)
```

2. 原处改为 `ExtractNatives(job.Dest, nativesDir, job.Extract);`，行为不变
3. 验证：构建通过

## T2：Preflight 数据模型 + CheckAsync

新文件：`dotnet/src/Starcloud.Core/Services/LaunchPreflightService.cs`

1. 数据模型（设计 3.1）：

```csharp
public sealed record PreflightIssue(string Kind, string Path, string? Detail);
public sealed record PreflightFailedFile(string Path, string Reason);
public sealed class PreflightReport { Issues / VersionJsonBroken / ManifestJsonUrl / NeedsRepair / CanAutoRestoreJson }
public sealed class PreflightException : Exception { IReadOnlyList<PreflightFailedFile> FailedFiles }
```

Kind 取值：`jar` / `lib` / `natives` / `index` / `asset`

2. `LaunchPreflightService`（依赖 InstallerService / DownloadService / ManifestService）
3. `CheckAsync(dotMc, versionId, ct)`：
   - JSON：try `LoadVersionAsync`；捕获 FileNotFound/DirectoryNotFound/Json/InvalidOperation →
     `VersionJsonBroken = true`，尝试 `GetManifestAsync(false)` 按 id 查 URL（失败则 null），
     **直接返回 report**（JSON 坏时无法继续其他检查）
   - 客户端 jar：`VersionJarPath` 存在性 + SHA1（Downloads.Client.Sha1）
   - 库文件：`RulesAllow` + `Artifact is { Url: not "" }` → 存在性 + SHA1（Artifact.Sha1）
   - natives jar：`Classifiers["natives-windows"]` → 存在性 + SHA1
   - 资源索引：`assets/indexes/{AssetIndex.Id}.json` 存在性；存在才继续解析并检查资源
   - 资源文件：`assets/objects/{hash[..2]}/{hash}` 仅存在性（Detail 存 hash）
4. SHA1 辅助：`static async Task<bool> Sha1MatchesAsync(string path, string? expected, CancellationToken ct)`
   → 用 `SHA1.HashData(File.OpenRead(...))` 流式计算（.NET 8 支持 Stream 重载）
5. 验证：构建通过

## T3：EnsureReadyAsync 自动补全

同文件 `LaunchPreflightService`：

```csharp
public async Task EnsureReadyAsync(string dotMc, string versionId, PreflightReport report,
    LauncherSettings settings, IProgress<BatchProgress>? progress, CancellationToken ct = default)
```

1. `report.VersionJsonBroken && report.CanAutoRestoreJson` → `_downloader.DownloadFileAsync(url, VersionJsonPath)`；
   JSON 不可恢复的情况由 UI 层在调用前拦截（不进入 EnsureReady）
2. `LoadVersionAsync` 重新加载版本
3. 按 Issues 构造下载任务（Path 反查）：
   - `jar` → `(client.Url, VersionJarPath, client.Sha1, client.Size)`
   - `lib` → 遍历 `version.Libraries` 匹配 `libraries/{Artifact.Path}` 相同路径 → Artifact 四元组
   - `natives` → 匹配 Classifier 路径，收集 natives 解压任务
   - `index` → `(AssetIndex.Url, indexPath, AssetIndex.Sha1, 0)`
   - `asset` → 若 index 是 Issue：先下索引后**重新解析索引、资源全量入任务**；
     否则按 Issue.Detail 里的 hash 构造 `resources.download.minecraft.net/{xx}/{hash}`
4. 调用 `DownloadBatchAsync`（复用并发/重试/进度/SHA1 校验），进度透传
5. natives 解压：对解压任务逐个调 T1 的 `ExtractNatives`
6. 失败收集：`DownloadBatchAsync` 任一文件失败会抛异常（Task.WhenAll 语义）→
   catch 后对全部任务重扫 `File.Exists` + SHA1，收集仍不达标的文件 →
   抛 `PreflightException(FailedFiles)`；未抛异常即成功返回
7. 验证：构建通过

## T4：AppServices 注册

文件：`dotnet/src/Starcloud.App/Services/AppServices.cs`

1. 属性 `public LaunchPreflightService Preflight { get; }`
2. 构造函数 `Diagnostics` 之后：`Preflight = new LaunchPreflightService(Installer, Download, Manifest);`
3. 验证：构建通过

## T5：HomePage 启动链路集成

文件：`dotnet/src/Starcloud.App/Views/HomePage.xaml.cs`

`LaunchBtn_Click` 中构造 `options` 之后、`LaunchVersionAsync` 之前插入：

1. `StatusText.Text = "正在检查游戏文件完整性…"; StatusText.Visibility = Visible;`
2. `var report = await _s.Preflight.CheckAsync(dotMc, versionId);`
3. `report.NeedsRepair` 时：
   - `VersionJsonBroken && !CanAutoRestoreJson` → LocalErrorBar
     「版本 JSON 缺失或损坏，无法自动恢复，请到下载页重新安装」→ return
   - `StatusText.Text = "补全缺失文件…"`，`await _s.Preflight.EnsureReadyAsync(...)`
     progress 回调刷新：`$"补全缺失文件 {DoneFiles}/{TotalFiles}（{Percentage}%）…"`
   - catch `PreflightException ex` → ContentDialog（XamlRoot = this.XamlRoot）：
     标题「补全失败」，正文失败清单（最多列 5 条 + 「等 N 个文件」），按钮
     「仍然启动」（Primary）/「取消」→ 取消则 return
   - 其余异常（如 JSON 重下失败）→ 现有 catch 统一 LocalErrorBar 展示
4. 通过后继续原 `LaunchVersionAsync` 链路（`finally` 已有按钮恢复逻辑，不动）
5. 验证：构建通过

## T6：整体验证

1. 构建：0 警告 0 错误
2. UIA 冒烟（PowerShell + System.Windows.Automation，脚本 UTF-8-BOM）：
   - 启动应用 → 主窗口定位 → 首页 StatusText 存在
   - （可选，依赖环境）选中版本点启动 → 观察状态文本出现「正在检查游戏文件完整性」→
     完好文件时快速进入「游戏运行中」
3. 手工验证清单（留给用户）：
   - 正常版本启动无感（预检 1-2 秒内通过）
   - 手动删除 `libraries` 中一个文件再启动 → 状态栏显示补全 → 自动补齐后进游戏
   - 断网点启动 → 弹窗列出失败文件 → 选「仍然启动/取消」行为符合预期
