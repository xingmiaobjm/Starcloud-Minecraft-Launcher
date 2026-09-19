# 启动前自动补全（Launch Preflight）设计

日期：2026-09-08
状态：设计已确认（逐段确认完成），待写实现计划

## 1. 背景与问题

当前启动链路（`GameLauncher.LaunchVersionAsync`）没有任何完整性检查：

- 构造 classpath 时用 `.Where(File.Exists)` 静默跳过缺失库文件——游戏会崩溃或闪退，用户不知道原因
- 客户端 jar 缺失、损坏不报错
- 资源文件缺失直接启动（材质/音效异常）

现有 `GameDiagnosticsService` 只检查版本 JSON / 父版本 / client jar 体积，不检查库与资源，
且修复粒度是整版本重装。Rust 旧实现（已废弃）有 `ensure_installed` / `needs_download`
（SHA1 校验）参考。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| 修复策略 | **全自动静默补**：检测到什么都自动补，按钮变进度态，补完直接启动 |
| 检查深度 | **标准**：库/jar 存在性 + SHA1 校验；资源只查存在性 |
| 补全范围 | 仅本次要启动的版本 |
| 失败降级 | **询问后再定**：弹窗告知失败清单，用户选「仍然启动 / 取消」 |
| 实现架构 | **独立预检服务**（LaunchPreflightService），复用 InstallerService/DownloadService |

## 3. 详细设计

### 3.1 核心服务：LaunchPreflightService

新文件 `dotnet/src/Starcloud.Core/Services/LaunchPreflightService.cs`。

构造依赖：`InstallerService`（LoadVersionAsync / 路径方法 / RulesAllow）、
`DownloadService`（下载 + SHA1 + 重试）、`ManifestService`（重下 vanilla 版本 JSON）。

#### CheckAsync(dotMc, versionId, ct) → PreflightReport（只查不动）

| # | 检查项 | 深度 | 自动补依据 |
|---|---|---|---|
| 1 | 版本 JSON | 存在 + 可解析 | vanilla：manifest 按 id 查 URL 重下；Fabric 合并版不可自动恢复 |
| 2 | 客户端 jar | 存在性 + SHA1 | Downloads.Client（Url + Sha1） |
| 3 | 库文件（RulesAllow 过滤、有 Artifact.Url） | 存在性 + SHA1 | Artifact（Url + Sha1） |
| 4 | natives jar（natives-windows classifier） | 存在性 + SHA1 | Classifier（Url + Sha1），补后重新解压 |
| 5 | 资源索引 assets/indexes/{id}.json | 存在性 | AssetIndex.Url |
| 6 | 资源文件 assets/objects/{xx}/{hash} | 仅存在性 | URL 按 hash 构造，Sha1 = hash |

- SHA1 校验：`SHA1.HashData`（流式读取）。库 + jar 总量约 100-200MB，SSD 秒级；
  资源不校验内容（量可达数 GB）
- 文件完好时预检开销 1-2 秒，直接启动，用户无感

数据模型：

```csharp
public sealed record PreflightIssue(string Kind, string Path, string? Detail);

public sealed class PreflightReport
{
    public List<PreflightIssue> Issues { get; }   // jar/库/natives/索引/资源
    public bool VersionJsonBroken { get; }        // JSON 缺失/解析失败
    public string? ManifestJsonUrl { get; }       // vanilla 可自动恢复的重下地址
    public bool NeedsRepair => Issues.Count > 0 || VersionJsonBroken;
    public bool CanAutoRestoreJson => VersionJsonBroken && ManifestJsonUrl is not null;
}
```

#### EnsureReadyAsync(dotMc, versionId, report, IProgress<BatchProgress>, ct)（自动补全）

1. JSON 损坏且有 ManifestJsonUrl → 重下版本 JSON
2. 重新 LoadVersionAsync（inheritsFrom 合并，天然覆盖 Fabric）
3. 按 Issues 构造**只含缺失/损坏文件**的下载任务（构造逻辑与
   `InstallerService.InstallVersionContentAsync` 相同：jar + 库 + natives + 索引 + 资源）
4. 复用 `DownloadBatchAsync`（并发 + 重试 + 下载后 SHA1 校验），进度回调上报；
   并发数复用 `Settings.DownloadConcurrency`、重试次数复用 `Settings.DownloadMaxRetries`
5. natives jar 补完后解压（见 3.2 InstallerService 改动）
6. 个别文件重试穷尽仍失败 → 抛 `PreflightException`，携带失败文件清单 + 原因

#### 异常模型

```csharp
public sealed record PreflightFailedFile(string Path, string Reason);

public sealed class PreflightException : Exception
{
    public IReadOnlyList<PreflightFailedFile> FailedFiles { get; }
}
```

### 3.2 InstallerService 改动

把 `InstallVersionContentAsync` 内联的 natives 解压逻辑（L173-183）提取为公开静态方法：

```csharp
public static void ExtractNatives(string nativesJarPath, string nativesDir, ExtractSpec? extract)
```

原处改为调用该方法，行为不变。LaunchPreflightService 补完 natives jar 后同样调用。

### 3.3 服务注册（AppServices.cs）

```csharp
public LaunchPreflightService Preflight { get; }
// 构造函数：
Preflight = new LaunchPreflightService(Installer, Download, Manifest);
```

### 3.4 启动链路集成（HomePage.xaml.cs）

插入点：`LaunchBtn_Click` 中构造 `options` 之后、`LaunchVersionAsync` 之前
（账户验证已完成，避免补全半天才发现没登录）：

```
StatusText = "正在检查游戏文件完整性…"
report = await _s.Preflight.CheckAsync(...)
if report.NeedsRepair:
    if VersionJsonBroken 且 !CanAutoRestoreJson:
        → LocalErrorBar「版本 JSON 缺失或损坏，无法自动恢复，请到下载页重新安装」→ return
    StatusText = "补全缺失文件 12/58（34%）…"（复用现有 StatusText 区域，不加新控件）
    try: await _s.Preflight.EnsureReadyAsync(..., progress → StatusText 刷新)
    catch PreflightException ex:
        → ContentDialog「以下文件补全失败：<清单>」+ [仍然启动] [取消]
        → 取消 → return；仍然启动 → 继续启动
→ LaunchVersionAsync（正常启动）
```

- 检查/补全期间 `LaunchBtn` 由现有 `finally` 逻辑保持禁用，无需改按钮态
- 进度数据：`BatchProgress.DoneFiles/TotalFiles/Percentage` 直接格式化
- ContentDialog 需设置 XamlRoot（WinUI 3 要求）

### 3.5 边界处理

| 场景 | 行为 |
|---|---|
| Fabric（inheritsFrom） | LoadVersionAsync 合并父版本库清单，检查天然覆盖 |
| 库无下载信息（部分 Forge 库） | 跳过检查（现状启动同样跳过，保持一致） |
| 断网 / 下载重试穷尽 | PreflightException → 弹窗「仍然启动 / 取消」 |
| 首次删光 assets 再启动 | 资源批量补，进度按文件数显示 |
| 用户重复点击启动按钮 | LaunchBtn 已禁用，无重入 |
| 版本 JSON 损坏（vanilla） | manifest 重下 → 继续后续检查 |
| 版本 JSON 损坏（Fabric 等） | 报错引导下载页重装（不可自动恢复） |

## 4. 明确不做（范围外）

- 不接 Nova 体检/修复 UI（检查逻辑已独立成服务，未来可复用）
- 不改 `GameLauncher.BuildCommandLineAsync` 的静默跳过行为（补全后文件已齐，该分支自然少走）
- 不做全版本扫描（只查本次启动的版本）
- 不做资源内容 SHA1 校验（体积原因，只查存在性）
- 不加补全中途取消按钮（下载失败才有弹窗决策点）
