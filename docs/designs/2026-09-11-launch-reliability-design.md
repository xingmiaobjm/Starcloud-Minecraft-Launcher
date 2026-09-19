# 启动可靠性（崩溃诊断 · 实时日志 · 环境体检 · 自动重试）设计

日期：2026-09-11
状态：待用户确认
所属：PCL2 平替第 C 组（启动可靠性）

## 1. 背景与问题

用户诉求：

> 崩溃诊断 + 一键修复、实时游戏日志面板、启动前环境体检、自动重试 + 智能内存 + 防双开，都要。

现状问题（探查结论，均已核实到行）：

1. **启动失败无自动重试，退出码不读**。[HomePage.xaml.cs](../../../dotnet/src/Starcloud.App/Views/HomePage.xaml.cs#L598-L611)
   的 `Exited` 回调只做收尾（置空 `App.RunningVersionId`、切状态、跑收尾命令），
   **完全不读 `Process.ExitCode`**，也不判断「启动几秒就崩」——用户只能自己再点一次启动。
2. **崩溃无结构化诊断，更没有一键修复**。唯一崩溃分析是
   [NovaService.CrashTriageAsync](../../../dotnet/src/Starcloud.Core/Services/NovaService.cs#L207-L268)：
   12 条关键词规则 → **纯建议文本**，且必须用户在 Nova 面板里主动说「崩了」才触发。
   与启动流程零耦合，**没有一键修复闭环**。
3. **游戏 stdout/stderr 被丢弃**。[GameLauncher.Launch](../../../dotnet/src/Starcloud.Core/Services/GameLauncher.cs#L255-L273)
   的 `ProcessStartInfo` **未重定向输出**，进程控制台内容全部丢失；
   启动器的 `LogsPage` 只静态读 `logs/launcher.log`
   （[LogsPage.xaml.cs](../../../dotnet/src/Starcloud.App/Views/LogsPage.xaml.cs#L16-L33)），
   而 **C# 侧没有任何代码写这个文件**（写它的只有已废弃的 Rust 实现
   `src/logging.rs`）→「启动器日志页」实际永远是「（暂无日志）」。
4. **启动前预检只覆盖游戏文件完整性，不覆盖运行环境**。
   [LaunchPreflightService.CheckAsync](../../../dotnet/src/Starcloud.Core/Services/LaunchPreflightService.cs#L51-L140)
   只校验版本 JSON / client jar / library / natives / assets 的**存在性与 SHA1**；
   磁盘空间、路径可写性、非 ASCII/超长路径、内存参数自相矛盾、Java 主版本是否匹配
   **一律不查**——这些恰恰是「点启动就失败」的高频原因。
5. **内存推荐只有一刀切**。`SystemMemory.AutoMaxMb()` = `clamp(物理/4, 2048, 物理*3/4)`，
   **不看模组数量、不看光影**，重度整合包容易 OOM。
6. **没有防双开**。唯一互斥是进程内静态 `App.RunningVersionId`
   （[HomePage.xaml.cs](../../../dotnet/src/Starcloud.App/Views/HomePage.xaml.cs#L80)），
   换页/重启启动器即失效；存档页 `LaunchCopiedAsync`
   （[SavesPage.xaml.cs](../../../dotnet/src/Starcloud.App/Views/SavesPage.xaml.cs#L381-L451)）
   绕过预检直接启动，两条链路各有一套 `_gameProcess`，可同时拉起两个游戏。
7. **存档页启动绕过预检/修复**。同上，`LaunchCopiedAsync` 注释明写「不做预检 / 自动修复」。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| C 组范围 | **崩溃诊断 + 一键修复 / 实时游戏日志面板 / 启动前环境体检 / 自动重试 + 智能内存 + 防双开**，四项全做 |
| 诊断深度 | 结构化诊断（原因分型 + 可执行修复动作），不是纯文本建议 |
| 一键修复边界 | 只修复「启动器能安全改的」：内存参数、Java 选择、重装缺失文件、打开崩溃报告目录；**不自动删模组**（涉及用户数据，只定位与建议） |
| 实时日志范围 | 游戏 stdout/stderr 实时回流启动页 + 落盘；同时补上启动器自身日志（`launcher.log`），让日志页真正可用 |
| 自动重试边界 | 仅「启动即崩」（进程存活过短且非用户取消）自动重试，**最多 1 次**；再次失败转崩溃诊断弹窗，不死循环 |
| 智能内存 | 推荐值 = 物理内存 + 模组数量 + 光影，作为版本设置「自动」档位的实现，并可被崩溃诊断一键套用 |
| 防双开粒度 | 游戏进程级互斥（跨进程锁文件 + pid 存活校验），覆盖主页与存档页两条启动链路；**不做启动器自身单实例**（超出范围） |
| 日志隐私 | 游戏日志本地落盘，**不自动上传**；Nova 分析仅在用户点击「问 Nova」时读取本地文件 |

## 3. 设计总览

### 3.1 新增文件

| 层 | 文件 | 职责 |
|---|---|---|
| Core | `Services/LauncherLog.cs` | 启动器日志：`logs/launcher.log` 按日滚动 + 内存环形缓冲 + `Changed` 事件（供实时面板订阅） |
| Core | `Services/GameSession.cs` | 游戏进程会话：stdout/stderr 异步读流 → 行事件 + 落盘 `logs/game-<时间戳>.log` + 尾部环形缓冲 |
| Core | `Services/GameSessionGuard.cs` | 防双开：`starcloud/game-session.json` 锁 + pid 存活校验 |
| Core | `Services/CrashAnalysisService.cs` | 崩溃诊断：读崩溃报告 / latest.log / 本局 stdout → 结构化 `CrashDiagnosis`（原因 + 修复动作） |
| Core | `Services/EnvironmentCheckService.cs` | 启动前环境体检：磁盘/路径/权限/内存/Java/模组量 → `EnvironmentReport` |
| Core | `Services/MemoryAdvisor.cs` | 智能内存推荐（物理 + 模组数 + 光影） |
| App | `Views/CrashDialog.cs` | 崩溃诊断弹窗（结构化展示 + 一键修复 + 重启） |
| App | `Views/EnvCheckDialogs.cs` | 环境体检弹窗（体检结果 + 修复/忽略） |

### 3.2 修改文件

| 文件 | 改动 |
|---|---|
| `Core/Services/GameLauncher.cs` | `Launch` 重定向 stdout/stderr（UTF-8），返回 `GameSession` 而非裸 `Process`；`LaunchVersionAsync` 返回类型同步 |
| `Core/Services/SystemMemory.cs` | `AutoMaxMb()` 委托给 `MemoryAdvisor.Suggest(...)`（保持签名兼容） |
| `Core/Services/LauncherPaths.cs` | 新增 `GameLogsDir`、`GameSessionFile` 路径；`LauncherSettings` 新增 `LaunchAutoRetry` / `ShowGameLogOnLaunch` |
| `Core/Services/InstanceSettings.cs` | `InstanceLayout.Resolve` 的 auto 内存档位改用 `MemoryAdvisor`（带模组数/光影上下文） |
| `App/Services/AppServices.cs` | 装配 `SessionGuard` / `CrashAnalyzer` / `EnvCheck` |
| `App/Views/HomePage.xaml(.cs)` | 启动状态机接入体检/重试/崩溃诊断；覆盖层加实时日志区 |
| `App/Views/SavesPage.xaml.cs` | `LaunchCopiedAsync` 接入会话锁（防双开）与启动器日志 |
| `App/Views/LogsPage.xaml(.cs)` | 顶部切换「启动器日志 / 游戏日志」，支持选择历史对局日志 |
| `App/Views/SettingsPage.xaml(.cs)` | 新增「自动重试」「启动时显示日志」开关 + 内存智能推荐按钮 |

### 3.3 启动流程新链路

```
HomePage.StartLaunchAsync
  ├─ 账户解析（不变）
  ├─ 层叠合并 EffectiveLaunch（不变）
  ├─ [新] GameSessionGuard.Acquire(dotMc, versionId)   ← 防双开，冲突直接提示并退出
  ├─ [新] ApplyStage(EnvChecking) EnvironmentCheckService.CheckAsync  ← §6
  │     └─ Block 级问题 → 体检弹窗（修复后继续 / 仍然启动 / 取消）
  ├─ 文件预检 + 补全（不变）
  ├─ 启动前命令（不变）
  ├─ [改] LaunchVersionAsync → GameSession（stdout/stderr 回流 UI + 落盘）
  ├─ [新] 挂 session.Exited：(exitCode, 存活时长) → 分类处理
  │     ├─ 用户取消 / exitCode==0            → 正常收尾
  │     ├─ 存活 < 8s 且非取消 且未重试        → [新] 自动重试一次（提示「启动失败，正在重试…」）
  │     └─ 异常退出（含重试后仍失败）          → CrashAnalysisService → 崩溃弹窗（一键修复 / 重启 / 返回）
  └─ 释放会话锁
```

## 4. 详细设计：日志基础设施

### 4.1 `LauncherLog`（启动器自身日志）

现状 `logs/launcher.log` 无人写入，日志页形同虚设。新增：

```csharp
/// <summary>启动器日志：按日滚动落盘 + 内存尾部缓冲 + 变更事件（实时面板订阅）</summary>
public static class LauncherLog
{
    /// <summary>写一行（自动加时间戳；落盘失败静默）</summary>
    public static void Write(string category, string message);

    /// <summary>异常便捷写法</summary>
    public static void Error(string category, string message, Exception ex);

    /// <summary>最近 N 行（内存缓冲，最多 500 行）</summary>
    public static IReadOnlyList<string> Tail(int count);

    /// <summary>追加一行时触发（实时面板订阅；可能在工作线程）</summary>
    public static event Action<string>? Appended;
}
```

- 文件：`%APPDATA%/starcloud-launcher/logs/launcher.log`（沿用 `LauncherPaths.LogsDir`）。
- 滚动：写入前比对当日日期，跨天自动拆文件 `launcher-YYYY-MM-DD.log`（沿用 Rust 版「按日滚动」语义）。
- 线程安全：`lock` 保护缓冲与写盘。
- 埋点（不追求全覆盖，只埋启动链路关键节点）：
  - `LauncherLog.Write("launch", "启动 <版本>（隔离=是/否，Java=…，内存=512-4096M）")`
  - 预检/补全/启动前命令/进程拉起/退出码/重试/体检结论 各一条。
- 单文件上限：超过 5 MB 截断保留尾部（避免无限增长）。

### 4.2 `GameSession`（游戏进程会话）

```csharp
/// <summary>一局游戏的进程会话：stdout/stderr 实时回流 + 落盘 + 退出信息</summary>
public sealed class GameSession : IDisposable
{
    public int Pid { get; }
    public string VersionId { get; }
    public string LogFile { get; }          // logs/game-<yyyyMMdd-HHmmss>.log
    public DateTime StartedAt { get; }
    public bool HasExited { get; }
    public int? ExitCode { get; }           // 退出后才有值
    public TimeSpan Uptime { get; }

    /// <summary>一行输出（已去换行；UI 线程订阅需自行 DispatcherQueue 转发）</summary>
    public event Action<string>? OutputLine;
    /// <summary>进程退出（参数 = 退出码）；仅触发一次</summary>
    public event Action<int>? Exited;

    /// <summary>最近 N 行（内存环形缓冲，最多 1000 行）</summary>
    public IReadOnlyList<string> Tail(int count);

    /// <summary>杀掉进程树（取消 / 停止游戏）</summary>
    public void Kill();

    public void Dispose();
}
```

实现要点：

1. `GameLauncher.Launch` 内构造 `ProcessStartInfo` 时加：
   ```csharp
   RedirectStandardOutput = true,
   RedirectStandardError = true,
   StandardOutputEncoding = Encoding.UTF8,
   StandardErrorEncoding = Encoding.UTF8,
   ```
   **必须持续读流**——Java 进程 stdout 管道写满会阻塞游戏（这是重定向的经典陷阱）。
2. `GameSession` 构造时启动两条 `ReadLineAsync` 循环（`Task.Run`），分别打标 `[STDOUT]` / `[STDERR]`
   推入环形缓冲、追加落盘、触发 `OutputLine`；两流任一结束不终止另一流，`WaitForExitAsync` 后统一 `Exited`。
3. 退出码读取必须在 `WaitForExit()` 之后（否则 `ExitCode` 抛异常）；`Process.Exited` 事件与
   `WaitForExitAsync` 二者取一即可（用 `WaitForExitAsync` 统一，避免 `EnableRaisingEvents` 的双路径）。
4. 落盘编码 UTF-8（无 BOM），写入加 `StreamWriter` + `AutoFlush`，避免游戏崩溃时丢尾部。
5. 单个游戏日志文件上限 20 MB，超出截断（游戏自身也会滚动 `logs/latest.log`，此文件是启动器侧副本）。

### 4.3 启动器日志页改造（`LogsPage`）

- 顶部 `RadioButtons`（水平）：「启动器日志 / 游戏日志」。
- 游戏日志：下拉选择 `LauncherPaths.GameLogsDir` 下最近 10 个 `game-*.log`（默认最新），
  读尾部 400 行；提供「打开日志目录」按钮。
- 保留原有「暂无日志」空态文案。

## 5. 详细设计：实时游戏日志面板

在 [HomePage.xaml](../../../dotnet/src/Starcloud.App/Views/HomePage.xaml#L330-L409) 的启动覆盖层内新增：

```
[像素镐/标题/进度/步骤/贴士]（不变）
[新] 折叠式日志区：
     一行：「显示日志 ▾」ToggleButton + 「打开日志文件」HyperlinkButton（仅运行中有值）
     展开：Border（高 180，CardBackground）内 ScrollViewer + 等宽 TextBlock（最后 200 行）
[错误文本/按钮行]（不变）
```

行为：

1. **默认折叠**（克制，不抢启动页视觉）；`settings.ShowGameLogOnLaunch == true` 时默认展开。
2. 订阅 `GameSession.OutputLine` → `_dq.TryEnqueue` 追加到 `TextBox`/`TextBlock` 文本（**必须限速**：
   用 100ms `DispatcherTimer` 批量 flush 缓冲行，避免每行一次 UI 更新拖慢游戏启动）。
3. 自动滚动到底（`ScrollViewer.ChangeView(null, double.MaxValue, null)`）。
4. 仅保留最近 200 行在 UI 中（内存缓冲保留 1000 行）。
5. 退出后保留本局日志可继续查看；点击「打开日志文件」用 `explorer.exe /select,<file>`。
6. 覆盖层未显示（用户已收层）时，日志仍继续落盘，不更新 UI。

## 6. 详细设计：启动前环境体检

### 6.1 模型

```csharp
/// <summary>体检项级别：Block 会阻断启动，Warn 提示可继续，Ok 无问题</summary>
public enum EnvLevel { Ok, Warn, Block }

/// <summary>单个体检项</summary>
public sealed record EnvIssue(
    string Code,          // 稳定标识：disk_low / path_non_ascii / path_too_long / readonly / mem_invalid / mem_over_physical / java_missing / mods_many
    EnvLevel Level,
    string Title,         // 一句话问题
    string Detail,        // 数值/路径等细节
    bool AutoFixable,     // 是否可一键修复
    string? FixHint);     // 修复按钮文案（null = 不可修）

/// <summary>体检汇总</summary>
public sealed record EnvironmentReport(
    List<EnvIssue> Issues,
    long FreeDiskMb,
    int ModCount,
    bool HasShaders)
{
    public EnvLevel Worst => Issues.Count == 0 ? EnvLevel.Ok
        : Issues.Max(i => i.Level);
    public bool NeedsAttention => Issues.Count > 0;
}
```

### 6.2 检查项（全部为轻量本地操作，不联网、不枚举杀软进程）

| Code | 级别 | 判定 | 一键修复 |
|---|---|---|---|
| `disk_low` | FreeDisk < 512 MB → Block；< 2 GB → Warn | `DriveInfo.AvailableFreeSpace`（GameRoot 所在盘） | 无（提示清理磁盘） |
| `path_non_ascii` | Warn | `DotMinecraft` 或 GameRoot 含非 ASCII 字符（中文/空格以外的 Unicode） | 无（提示已知风险，不影响启动） |
| `path_too_long` | Warn | GameRoot 全路径长度 > 200 | 无（提示可能的写入失败） |
| `readonly` | Block | 尝试在 GameRoot 写临时文件失败 | 无（提示以管理员运行或换目录） |
| `mem_invalid` | Block | `MinMem > MaxMem` 或 `MaxMem < 512` | 有：恢复为推荐值（`MemoryAdvisor`） |
| `mem_over_physical` | Warn | `MaxMem > 物理内存 * 0.8` | 有：下调到推荐值 |
| `java_missing` | Block | 该版本要求的 Java 主版本，`JavaService.ListRuntimesAsync` 既无匹配、`DownloadRuntimeAsync` 也可能因断网失败 | 无（引导到设置页选 Java / 联网自动下载） |
| `mods_many` | Warn | mods 目录 `.jar` 数量 > 150 | 有：套用智能内存推荐值 |

说明：

- `java_missing` 判定：版本 JSON 的 `javaVersion.majorVersion`（缺省 8）→
  `JavaService.ListRuntimesAsync` 找 `MajorVersion >= required` 的运行时；
  找不到时**不算 Block**（因为启动时会自动下载），仅在**离线且无缓存**时降级为 Warn 提示
  「未找到匹配的 Java，将在启动时尝试自动下载」。真正 Block 的是
  `options.JavaExecutable` 被显式指定但文件不存在。
- `mods_many` 同时用于 `MemoryAdvisor.Suggest` 的输入。
- 体检耗时目标 < 300ms（只做目录枚举与少量文件系统调用，不做 SHA1）。

### 6.3 UI 流程

1. 启动流程在文件预检**之前**插入 `ApplyStage(LaunchStage.EnvChecking)`。
2. `report.Worst == Ok` → 静默继续（不打扰）。
3. `Worst == Warn` → **不弹窗**，把首条警告写进启动页步骤文案（`当前步骤` 行），继续启动；
   日志记一条。避免「每次启动都弹窗」骚扰。
4. `Worst == Block` → 弹 `EnvCheckDialogs.ShowAsync`：列出全部 Block/Warn 项，
   每项右侧给「修复」按钮（`AutoFixable` 才可点）；底部「仍然启动 / 取消」。
   修复后重新体检，全绿则自动继续。

## 7. 详细设计：崩溃诊断与一键修复

### 7.1 触发时机与退出码语义

`GameSession.Exited(exitCode)` 后：

| 条件 | 处理 |
|---|---|
| `_cancelRequested`（用户点了取消/停止） | 正常收层，不诊断 |
| `exitCode == 0` | 正常退出，走原有 Exited 展示（本局时长） |
| 存活 < 8 秒 且 未重试 且 `settings.LaunchAutoRetry` | **自动重试一次**（见 §8.1），不诊断 |
| 其余非零退出 | 进入崩溃诊断 → 弹 `CrashDialog` |

> 退出码补充：被 `Kill` 的进程 Windows 退出码常见 `-1`（0xFFFFFFFF）或 `1`；
> 用户取消路径已由 `_cancelRequested` 拦截，不会误判。
> 部分整合包/模组会以非零码正常退出（少见），故诊断弹窗首行始终标注「退出码 N」，
> 并允许用户「忽略并返回」。

### 7.2 `CrashAnalysisService`

```csharp
/// <summary>崩溃诊断结果</summary>
public sealed record CrashDiagnosis(
    string Title,               // 「内存不足」
    string Summary,             // 一到两句人话解释
    string Evidence,            // 命中的原文片段（≤3 行，供用户核对）
    string Source,              // 证据来源：crash-reports/xxx.txt / logs/latest.log / 本局输出
    List<string> Suggestions,   // 可执行建议（有序）
    CrashFixKind Fix,           // 一键修复动作
    string? FixLabel,           // 修复按钮文案（null = 无可自动修复）
    int? SuggestMaxMemoryMb);   // 内存类修复的推荐值

public enum CrashFixKind
{
    None, OpenCrashReport, SetMemory, ReinstallVersion, OpenModsFolder, ReinstallJavaHint
}

public sealed class CrashAnalysisService
{
    /// <summary>诊断一局崩溃；session 为本局会话（用于 stdout），versionId 用于定位版本</summary>
    public Task<CrashDiagnosis> AnalyzeAsync(string dotMc, string versionId, int exitCode,
        IReadOnlyList<string>? sessionTail, CancellationToken ct = default);
}
```

证据优先级（逐级回退，取第一个存在的）：

1. `crash-reports/*.txt` 中**修改时间晚于本局启动时刻**的最新文件；
2. `<gameRoot>/logs/latest.log` 尾部 200 行；
3. 本局 `GameSession` 的内存尾部（stdout/stderr）。

规则表（在 Nova 现有 12 条基础上扩展，覆盖退出码分型）：

| 命中特征 | Title | Fix | 说明 |
|---|---|---|---|
| `OutOfMemoryError` / `heap space` / `exit -1073741571`（0xC00000FD 栈溢出） | 内存不足 | `SetMemory`（推荐值来自 `MemoryAdvisor`） | 一键把该版本内存档位设为 manual + 推荐值 |
| `UnsupportedClassVersionError` / `class file version` | Java 版本不匹配 | `ReinstallJavaHint` | 展示 class file version → Java 版本对照，引导设置页 |
| `java.lang.NoSuchMethodError` / `NoClassDefFoundError` / `ModResolutionException` / `Incompatible mod set` | 模组依赖冲突 | `OpenModsFolder` + 建议「检查模组冲突」（复用 `ModConflictService.ScanAsync`） | 只定位不删 |
| `GLFW error` / `Failed to create window` / `Pixel format not accelerated` | 显卡/窗口初始化失败 | `None`（建议更新驱动 / 降内存 / 关光影） | |
| `AccessDeniedException` / `Permission denied` | 文件权限被拒 | `None` | 建议换目录 / 管理员运行 / 排除杀软 |
| `The game crashed whilst` 无已知命中 | 未知崩溃 | `OpenCrashReport` | 提供「问 Nova」（复用现有 CrashTriage） |
| 非零退出且日志为空/极短 | 进程启动即失败 | `ReinstallVersion`（复用 `GameDiagnosticsService.RepairAsync`） | Forge/NeoForge 不支持时按钮置灰并提示到下载页 |

> **Nova 关系**：`CrashAnalysisService` 是**离线确定性**诊断（不依赖 LLM，秒级出结果）；
> Nova 的 `CrashTriageAsync` 保留为「深层分析」入口——崩溃弹窗里放一个
> 「让 Nova 再深入看看」按钮，把本地证据拼成 prompt 交给 Nova。两者互补而非替代。

### 7.3 一键修复动作

| `CrashFixKind` | 实现 |
|---|---|
| `SetMemory` | `InstanceSettingsStore.Get(id)` → `MemoryMode="manual"`、`MemoryMinMb=512`、`MemoryMaxMb=SuggestMaxMemoryMb` → `Set` + `Save`；提示「已把内存调整为 X MB，重新启动生效」 |
| `ReinstallVersion` | 调 `GameDiagnosticsService.RepairAsync(versionId, settings)`（带进度对话框）；失败时给原文 |
| `OpenCrashReport` / `OpenModsFolder` | `explorer.exe` 打开目录（崩溃报告不存在时打开 `logs/`） |
| `ReinstallJavaHint` | 不自动改，跳设置页 |

修复完成后弹窗按钮变为「重新启动」，点击即复用 `HomePage.StartLaunchAsync(versionId)`。

### 7.4 弹窗结构（`CrashDialog`）

```
标题：游戏异常退出（退出码 1）
副标题：内存不足
正文：图一：Summary
      「证据」可折叠区（等宽小字，≤3 行 + 来源文件）
      建议列表（• 项）
底部按钮：[一键修复：调整内存为 6144 MB]  [重新启动]  [打开崩溃报告]  [返回主页]
```

- 同一时刻只允许一个 `ContentDialog`（WinUI 限制），因此崩溃弹窗**必须等启动覆盖层交互结束**再 Show；
  实现上在 `Exited` 回调里 `await`，并在 show 前 `if (_crashDialogOpen) return;` 去重。
- 「重新启动」前先 `HideOverlay(null)` 复位状态机。

## 8. 详细设计：自动重试 · 智能内存 · 防双开

### 8.1 自动重试

- 新增设置 `LaunchAutoRetry`（默认 `true`）。
- 判定：`!cancel && exitCode != 0 && Uptime < 8s && retryCount == 0 && settings.LaunchAutoRetry`。
- 动作：`ApplyStage(LaunchStage.Retrying)` → 启动页文案「启动失败，正在重试…」
  → 重新执行 §3.3 从「文件预检」起的段落（**不重跑体检**，避免重复打扰），`retryCount++`。
- 第二次仍失败：`retryCount == 1` → 直接进崩溃诊断（不再重试）。
- 重试失败同样计入 `launcher.log`。

### 8.2 智能内存推荐

```csharp
/// <summary>智能内存推荐：物理内存 + 模组数量 + 光影 → 建议最大内存（MB，512 对齐）</summary>
public static class MemoryAdvisor
{
    /// <summary>推荐最大内存；totalMb <= 0 时用 SystemMemory 兜底</summary>
    public static int SuggestMaxMb(long totalPhysicalMb, int modCount, bool hasShaders);

    /// <summary>一句话解释推荐依据（「16 GB 内存 + 187 个模组 + 光影 → 建议 6144 MB」）</summary>
    public static string Explain(long totalPhysicalMb, int modCount, bool hasShaders);

    /// <summary>探测某 GameRoot 的 mods 数量与光影目录是否存在</summary>
    public static (int ModCount, bool HasShaders) Probe(string gameRoot);
}
```

公式（可解释、保守）：

```
base = clamp(totalMem/4, 2048, totalMem*3/4)      // 沿用既有基线
modBoost  = modCount > 300 ? 1.6 : modCount > 150 ? 1.35 : modCount > 60 ? 1.15 : 1.0
shaderBoost = hasShaders ? 1.25 : 1.0
max = clamp(base * modBoost * shaderBoost, 2048, totalMem*3/4)
result = 向下取 512 倍数
```

接入点：

1. `SystemMemory.AutoMaxMb()` 改为 `MemoryAdvisor.SuggestMaxMb(SystemMemory.TotalMbOrFallback(), 0, false)`
   （保持既有调用点行为等价）。
2. `InstanceLayout.Resolve` 的 `"auto"` 档位：需要模组数/光影上下文，
   故新增重载 `Resolve(dotMc, versionId, profile, settings, (int ModCount, bool HasShaders)? autoMemContext = null)`，
   `auto` 档位有上下文时用 `MemoryAdvisor`，无上下文回退旧行为（兼容现有调用点）。
3. `HomePage` 调用 `Resolve` 时传入 `MemoryAdvisor.Probe(eff.GameRoot)`。
4. 设置页/版本设置页的「智能推荐」按钮：显示推荐值 + 依据文案，一键写入。

### 8.3 防双开（`GameSessionGuard`）

```csharp
/// <summary>跨进程游戏会话锁：同一 .minecraft 下只允许一局游戏在跑</summary>
public sealed class GameSessionGuard
{
    /// <summary>尝试占锁；成功返回持有句柄（Dispose 释放）。已被占用时返回 null 并给出占用信息</summary>
    public static GameSessionHandle? TryAcquire(string dotMc, string versionId, out string? blockedBy);
}

public sealed class GameSessionHandle : IDisposable
{
    public string VersionId { get; }
    public int Pid { get; }
    public void Bind(int pid);   // 进程拉起后回填真实 pid
    public void Dispose();       // 移除锁文件
}
```

- 锁文件：`<dotMc>/starcloud/game-session.json` = `{ pid, versionId, startedAt }`。
- `TryAcquire`：
  1. 读锁文件；若存在且 `Process.GetProcessById(pid)` 存活且进程名形如 `java*` → 返回 `null`，`blockedBy = versionId`；
  2. 否则（不存在/pid 已死/内容损坏）→ 覆盖写入自身锁，返回句柄。
- 释放：`Dispose` 删锁文件（进程退出、启动取消、启动失败都要走 `finally`）。
- 接入：
  - `HomePage.StartLaunchAsync`：进覆盖层前 `TryAcquire`；失败 → `LocalErrorBar`「版本 X 正在运行中」并 return。
  - `SavesPage.LaunchCopiedAsync`：同样 `TryAcquire`；失败走现有 `ErrorBar`。
  - 原 `App.RunningVersionId` 静态标记保留（UI 状态用），但**不再是唯一互斥**。
- 兼容：`App.RunningVersionId` 与实际锁文件不一致时**以锁文件为准**（防止启动器重启后静态标记丢失）。

## 9. 设置与数据变更

`LauncherSettings` 新增（`LauncherPaths.cs`）：

```csharp
/// <summary>启动即崩时自动重试一次（默认开）</summary>
[JsonPropertyName("launch_auto_retry")]
public bool LaunchAutoRetry { get; set; } = true;

/// <summary>启动覆盖层默认展开实时日志（默认关，克制）</summary>
[JsonPropertyName("show_game_log_on_launch")]
public bool ShowGameLogOnLaunch { get; set; } = false;
```

`LauncherPaths` 新增：

```csharp
/// <summary>启动器侧保存的游戏日志（每局一个文件）</summary>
public static string GameLogsDir => Path.Combine(LogsDir, "game");

/// <summary>跨进程游戏会话锁文件（放在 .minecraft 下，随实例目录生效）</summary>
public static string GameSessionFile(string dotMc) =>
    Path.Combine(dotMc, "starcloud", "game-session.json");
```

> `GameSessionFile` 需 `Directory.CreateDirectory(Path.GetDirectoryName(...))` 兜底。

## 10. 非目标

1. **不做启动器自身单实例**（WinUI 3 单实例重定向复杂，且与「防双开」诉求不同）。
2. **不自动删除/禁用模组**——涉及用户数据，只定位并引导。
3. **不做显卡驱动检测/杀软进程枚举**（成本高、误报多、拖慢启动）。
4. **不自动上传崩溃报告**（隐私）；Nova 分析仅在用户点击时读本地文件。
5. **不改游戏内行为**（不注入 JVM agent、不改 mod 加载）。

## 11. 验收标准

1. **实时日志**：启动游戏后覆盖层可展开看到 stdout 实时滚动（≤1s 延迟）；`logs/game/game-*.log` 有完整本局输出；
   日志页能在「启动器日志 / 游戏日志」间切换，启动器日志不再显示「（暂无日志）」（有 `launcher.log`）。
2. **崩溃诊断**：人为制造失败（如把内存上限设为 1 MB 触发 OOM 或直接运行损坏版本）→
   退出后自动弹出结构化诊断，命中原因正确、证据来自真实文件、建议可执行。
3. **一键修复**：点击「调整内存为 N MB」→ `version-settings.json` 中该版本 `MemoryMaxMb` 变为 N 且 `MemoryMode="manual"`；
   再启动时 JVM 参数 `-XmxN M` 生效。
4. **环境体检**：把 `.minecraft` 指向一个只读目录或塞入一个 `MinMem > MaxMem` 的版本配置 →
   启动前弹 Block 级体检弹窗；把磁盘剩余空间做小 → Warn 不弹窗只在步骤行提示。
5. **自动重试**：构造「启动 8 秒内非零退出且非取消」场景 → 自动重试一次；第二次失败进崩溃诊断，无死循环。
6. **智能内存**：187 个模组 + 光影的实例，「自动」档位推荐值显著高于空实例；设置页「智能推荐」显示依据文案。
7. **防双开**：启动一个游戏后，从主页与存档页再次启动均被拦截并提示；关闭游戏后可正常启动；
   手动结束 java 进程后锁文件失效（下次启动不被误拦截）。
8. **构建**：`dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj` **0 警告 0 错误**。
