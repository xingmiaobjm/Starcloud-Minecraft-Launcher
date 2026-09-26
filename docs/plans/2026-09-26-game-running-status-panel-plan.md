# 游戏运行状态面板实现计划

日期：2026-09-26
设计文档：docs/designs/2026-09-26-game-running-status-panel-design.md
所属：游戏运行态可观测（独立「运行状态」页）

构建命令（每个任务完成后执行）：

```powershell
Stop-Process -Name Starcloud.App -Force -ErrorAction SilentlyContinue
dotnet build dotnet\Starcloud.slnx -v q -nologo
```

最终验收命令：

```powershell
dotnet build dotnet\Starcloud.slnx -v m
dotnet test dotnet\tests\Starcloud.Core.Tests\Starcloud.Core.Tests.csproj -v q -nologo
```

目标：0 错误 0 警告；测试全绿；UIA 实机复验通过。

---

## 计划期微修（相对设计文档的实现决定，不改变设计与验收）

1. **`GameRunInfo` 去掉 `Diagnostics` 字段**。设计 §3 让 Monitor 持有 `DiagReport?`，
   但 `GameDiagnosticsService.DiagnoseAsync` 是**全版本完整性扫描**（依赖 `InstallerService` + `ManifestService`），
   语义上是「用户点一下才做」的动作，不是每秒心跳该做的事。Monitor 只做进程采样，
   诊断由页面按 §6 的「诊断」按钮触发（`_s.Diagnostics.DiagnoseAsync(dotMc)`），结果放页面字段。
   好处：Monitor 保持零服务依赖、可测、无重活。
2. **Monitor 是 Core 单例**（`GameRuntimeMonitor.Instance`），不是 App 单例。
   `NovaToolsWrite.Versions.cs`（Core）是第三条启动路径，Core 里够不到 `AppServices`。
   `AppServices` 增加 `public GameRuntimeMonitor Runtime => GameRuntimeMonitor.Instance;` 作为便捷入口
   （设计 §2 的「AppServices 注册」以这种方式满足）。
3. **导航项运行中圆点复用既有 300ms 定时器**。`MainWindow._taskRefresh.Tick`（L56-60）
   已经在调 `WatchGameEvents()`，其中已算出 `current?.VersionId`。
   在 `WatchGameEvents` 内部把 `running` 传给 `UpdateRunningDot(running)` 即可，**不新增定时器**。
4. **「打开日志」走 `App.PendingLogSource` + `MainWindow.NavigateTo("logs")`**，
   而不是设计 §6 写的 `Frame.Navigate(typeof(LogsPage), "game")`。
   后者会绕过侧栏：选中态仍停在「运行状态」，用户按返回会以为是页面出错。
   `NavigateTo` 保证侧栏同步（`MainWindow.xaml.cs` L256-263 既有实现）。
   `PendingLogSource` 与 `PendingLaunchVersionId` 并列放在 `App.xaml.cs` L15 区。
5. **设计 §8 的「JVM 参数解析」测试改为「启动上下文往返保真」测试**。
   `ExtraJvmArgs` 本来就是 `List<string>` 结构化字段（`VersionProfile.ExtraJvmArgs`），
   落盘/读回全程不切分、不重组，不存在需要测试的解析器；
   把它写成「含引号 / `=` / 空格的参数往返后逐字相等」才对应真实风险（显示失真）。
6. **恢复路径的上下文缺失时，降级解析放 App 层**（页面），不放 `GameLaunchContextStore`。
   因为降级要用 `LauncherSettings` + `InstanceLayout.Resolve`，Store 拿到这些就把 Core 的职责搞混了。
   Store 只负责读写删。
7. **恢复路径拿不回退出码**。跨启动器重启识别到的会话没有 `GameSession` 对象，
   进程死掉时只能落一条 `ExitCode = null` 的历史（设计 §3 的注释已经预留了这个语义），
   `EndedAt` 用「探测到死亡的时刻」，属估算，界面上只在时长列显示。
8. **`PdhNative` 的注释要改**。`NovaFpsWatcher.cs` L212 现写「仅本文件内使用」，
   本计划起 `ProcessSampler` 也会用（同程序集 `internal`），注释与事实不符要同步。
9. **`GameSession.CommandLine` 只在 `Launch` 内部赋值**。
   `LaunchVersionAsync`（L344-355）内部才 `BuildCommandLineAsync`，三个调用方都拿不到，
   所以没有别的落点。赋值后**只内存持有**，`GameLaunchContext` 里刻意没有这个字段。

## 前置确认（已核实，实现时不再重复探索）

- **`GameLauncher`**（`dotnet/src/Starcloud.Core/Services/GameLauncher.cs`）：
  `LaunchOptions` L10-33（含 `MinMemoryMb/MaxMemoryMb/JavaExecutable/ExtraJvmArgs/WindowWidth/
  WindowHeight/Fullscreen/EnvVars`）；`LaunchCredentials` L36；
  `BuildCommandLineAsync` L50（返回 `string`，含 `--accessToken`）；
  **`Launch` L316-341**（L337 生成 `game-<yyyyMMdd-HHmmss>.log`，L338 `new GameSession(...)`）；
  **`LaunchVersionAsync` L344-355**（L348 `BuildCommandLineAsync`，L354 `return Launch(...)`）。
- **`GameSession`**（`.../Services/GameSession.cs`）：ctor L34 `internal`；
  `OutputLine` L55、`Exited`(Action&lt;int&gt;) L58、`VersionId` L60、`LogFile` L63、`StartedAt` L65、
  `Pid` L67、`Id` L70、`HasExited` L72、`ExitCode` L82、`Uptime` L91、`MainWindowHandle` L94、
  `Refresh()` L104、`Kill(bool entireTree = true)` L110、`Tail(int)` L116（环形上限 1000）、
  `WaitForExitAsync()` L126、`Dispose()` L128。
- **`GameSessionGuard`**（`.../Services/GameSessionGuard.cs`）：
  `GameSessionLock{Pid,VersionId,StartedAt}` L11-16；`TryAcquire` L59；`IsAnyGameAlive` L76；
  **`Current(dotMc)` → `GameSessionLock?`** L87-91（已含「pid 存活且进程名 java 前缀」判定）；
  `WriteLock` L93（tmp + `File.Move(overwrite)`）；`IsAliveJavaProcess` L118 是 **private static**。
- **`LauncherPaths`**（`.../Services/LauncherPaths.cs`）：`DataDir` L14、`SettingsFile` L19、
  `LogsDir`（L45 附近）、`GameLogsDir` L105、`GameSessionFile(dotMc)` L108-109、
  `EnsureDataDir()` L111-（已建 `GameLogsDir`）。
- **`NovaFpsWatcher`**（`.../Services/NovaFpsWatcher.cs`）：
  CPU% 自算口径 L112-124（`TotalProcessorTime` 差分 ÷ 墙钟 ÷ `Environment.ProcessorCount` × 100，
  `Math.Clamp(...,0,100)`）；`SampleGpuPercent` L178-196；`MarkGpuFail` L198-201（连续 2 次判死）；
  `ExpandGpuInstances` L203-207（`\GPU Engine(pid_{pid}_*)\Utilization Percentage`）；
  **`internal static class PdhNative` L213**（顶层类，非嵌套；`ExpandWildcard` L217、`TryCollectSum`）。
- **`PdhNative.TryCollectSum`** 失败静默返回 `false`，`ExpandWildcard` 失败返回空 `List`。
- **`InstanceSettingsStore`**（`.../Services/InstanceSettings.cs`）：`VersionProfile` L11-83
  （`Isolation/MemoryMinMb/MemoryMaxMb/JavaPath/WindowWidth/WindowHeight/Fullscreen/
  ExtraJvmArgs/ExtraGameArgs/EnvVars/...`）；`Get(id)` L144（无则返回空档案）；
  `Save()` L129（原子写）；`EffectiveLaunch` L176-192；**`InstanceLayout.Resolve` L218-220**
  （`(int ModCount, bool HasShaders)? autoMemContext = null` 可选参）。
- **`MemoryAdvisor.Probe(gameRoot)`** → `(int ModCount, bool HasShaders)`（`MemoryAdvisor.cs` L31）。
- **`GameDiagnosticsService.DiagnoseAsync(dotMc, ct)`** → `DiagReport(int ScannedCount, List<DiagIssue> Issues)`；
  `DiagIssue(VersionId, Kind, Detail, AutoFixable)`（`GameDiagnosticsService.cs` L11-28）。
- **三条启动路径**：
  - `dotnet/src/Starcloud.App/Views/HomePage.xaml.cs` **L944-972**（`LaunchVersionAsync` L944、
    `Bind` L945、`OutputLine` L946、`Exited` L947、`App.RunningVersionId = versionId` L968）。
  - `dotnet/src/Starcloud.App/Views/SavesPage.xaml.cs` **L446-469**。
  - `dotnet/src/Starcloud.Core/Services/NovaToolsWrite.Versions.cs` **`LaunchGameAsync` L91-154**
    （`LaunchVersionAsync` L137、`Bind` L138、`Exited` L140、`_c.Host?.OnGameLaunched` L141）。
  - `VersionsPage.xaml.cs` L121 / `VersionDetailPage.xaml.cs` L894 只是写
    `App.PendingLaunchVersionId`，最终仍落到 HomePage，**不需要单独接线**。
- **`App.xaml.cs`**：`PendingLaunchVersionId` L15、`RunningVersionId` L18。
- **`HomePage.xaml.cs`**：`OnNavigatedTo` L85-91 消费 `App.PendingLaunchVersionId`；
  `ConsumePendingLaunch()` L94-（L107 `_gameProcess is { HasExited: false }` 时不叠加启动）。
- **`MainWindow.xaml`**：`MenuItems` L337-383（`联机` L361-365 → `NavigationViewItemSeparator` L366）；
  图标写法 `<FontIcon FontFamily="{StaticResource ScIconFont}" Glyph="&#xEC3B;" />`。
- **`MainWindow.xaml.cs`**：`_taskRefresh` 声明 L295、`Tick` 挂载 L56-61（调 `RefreshTaskRows()` +
  `WatchGameEvents()`）；`WatchGameEvents` L135-161（L141 `GameSessionGuard.Current(dotMc)`、
  L142 `running = current?.VersionId`）；`StopBackgroundTimers` L197-208；
  `Nav_SelectionChanged` L232-253（tag→Type switch，L247 `_ => typeof(HomePage)`）；
  `NavigateTo(string tag)` L256-263。
- **`LogsPage.xaml.cs`**：ctor L27、`Loaded` L（`_ready = true; LogSource.SelectedIndex = 0; Load();`）、
  `ApplySourceLabels()` L42-47、`IsGameSource => LogSource.SelectedIndex == 1` L49、`TailLines = 400` L25。
- **`AppServices`**（`Starcloud.App/Services/AppServices.cs`）：属性区 L13-87、ctor L89-209、
  末尾 L207-208（`Motion = new MotionService(...)` + `LauncherLog.Write("app", ...)`）。
- **`EmptyStateView`**（`Starcloud.App/Views/Controls/EmptyStateView.xaml.cs`）：
  `Glyph` / `IconImage` / `Title` / `Subtitle` / `ActionText` 五个依赖属性 L17-65、
  `event RoutedEventHandler? ActionClick` L13（无 `ActionText` 时按钮折叠 L20-23）。
- **`App.xaml` 样式 key**：`ScCard` L132、`ScCardHint` L141、`ScSectionSpine` L164、
  `ScSectionTitle` L172、`ScChip` L178、`ScChipText` L185、`ScChipAccent` L191、
  `ScChipAccentText` L195、`ScMetaLabel` L209、`ScMetaValue` L214、`ScIconFont` L37、
  `DangerBrush` L156、`SuccessBrush` L157。
- **图标码位**（`docs/designs/2026-09-21-harmonyos-icons-design.md` L25-103，且已 Glob 确认全库未占用）：
  导航 `EC3B`（计时器）、空态 `EC70`（空队列）、刷新 `E918`、列表 `EA94`、无 `EC68`（历史/备份）、
  `EA7D`（诊断/报告）、`EA6D`（启动参数）、`EC64`（日志）。
- **测试工程**：`dotnet/tests/Starcloud.Core.Tests/`；xunit；测试方法名用中文；
  临时目录写法见 `GameLauncherClasspathTests.cs` L86 `private sealed class TempDotMc : IDisposable`。
- **UIA 脚本**：`dotnet/uia-dump.ps1` / `uia-mods.ps1` / `uia-nova-settings.ps1` /
  `uia-version-detail.ps1` / `uia-launch-overlay.ps1`（纯 ASCII，中文用 `S @(0x...)` 生成）。

---

## T1：Core · 启动上下文存储

文件：`dotnet/src/Starcloud.Core/Services/GameLaunchContextStore.cs`（新建）

1. 数据模型（设计 §3）：

```csharp
/// <summary>启动上下文（写 &lt;dotMc&gt;/starcloud/game-launch.json，本局退出即删）</summary>
public sealed record GameLaunchContext(
    string VersionId,
    string JavaPath,
    int MaxMemoryMb,
    int MinMemoryMb,
    int WindowWidth,
    int WindowHeight,
    bool Fullscreen,
    string[] ExtraJvmArgs,
    string Isolation,              // "shared" | "isolated"
    int ModCount,
    int ShaderPackCount,
    int ResourcePackCount,
    DateTime CapturedAt);
```

   **刻意没有命令行字段**：`BuildCommandLineAsync` 的产物含 `--accessToken`，
   它只由 `GameSession.CommandLine` 内存持有（见 T5），绝不落盘。

2. `GameLaunchContextStore` 静态类：
   - `FileFor(string dotMc)` → `Path.Combine(dotMc, "starcloud", "game-launch.json")`；
   - `Write(string dotMc, GameLaunchContext ctx)`：建目录 → `tmp + File.Move(tmp, file, overwrite: true)`
     （与 `GameSessionGuard.WriteLock` L93-105 同款原子写）；全程 `try/catch` 静默；
   - `Delete(string dotMc)`：`try { File.Delete(...) } catch { }`；
   - `Read(string dotMc, string? expectVersionId = null)` → `GameLaunchContext?`：
     文件不存在 / 反序列化失败 / `expectVersionId` 非空且与 `ctx.VersionId` 不符 → 返回 `null`。
   - 序列化选项用 `JavaScriptEncoder.UnsafeRelaxedJsonEscaping`（仓库统一口径）。

3. `Capture(...)` 便捷构造：给定 `dotMc / versionId / EffectiveLaunch eff / int modCount /
   int shaderCount / int packCount` → 组装 `GameLaunchContext`。
   三个计数由调用方传入（App 层用目录计数，Core 的 Nova 路径用 `MemoryAdvisor.Probe` + 目录计数）。

4. 目录计数私有助手（供 App / Core 两处复用，放本文件 `public static int CountFiles(string dir)`）：
   `Directory.Exists(dir)` → `Directory.EnumerateFiles(dir).Count()`；异常吞掉返回 0。

构建（`Starcloud.Core`）。

---

## T2：Core · 运行历史存储

文件：`dotnet/src/Starcloud.Core/Services/GameRunHistoryStore.cs`（新建）

1. 数据模型（设计 §3）：

```csharp
/// <summary>一局结束后的落盘记录</summary>
public sealed record GameRunRecord(
    string VersionId,
    DateTime StartedAt,
    DateTime EndedAt,
    TimeSpan Duration,
    int? ExitCode,                 // null = 拿不到（跨启动器重启识别到的会话）
    long PeakWorkingSetBytes,
    bool Crashed);
```

2. `GameRunHistoryStore`（实例类，`ctor(string dataDir)`，文件 `<dataDir>/game.history.json`）：
   - `const int MaxRecords = 50`；
   - `IReadOnlyList<GameRunRecord> Records { get; }`（内存镜像，新→旧不倒序，按写入顺序）；
   - `bool TryLoad()`：文件不存在 → 空列表 + `_loadFailed = false`；
     解析失败 → 空列表 + **`_loadFailed = true`**（设计 §7：读可以兜底）；
   - `bool Append(GameRunRecord rec)`：
     ① `_loadFailed` → 记 `LauncherLog.Write("runtime", "历史文件损坏，拒绝写入以免覆盖")` 并返回 `false`
     （设计 §7 硬要求：不能把用户攒的历史用空库盖掉）；
     ② 否则插入队首（或末尾，取统一口径：**新记录插到 index 0**，界面上「最近运行」直接正序读），
     超过 50 条 `RemoveRange(50, Count-50)`；
     ③ 原子写（tmp + `File.Move(overwrite)`）；写失败返回 `false` 且回滚内存列表到写前快照。
   - 序列化选项同上；根对象 `{ "version": 1, "records": [...] }`（与 `InstanceSettingsStore.Save` L129-141 同构）。

构建（`Starcloud.Core`）。

---

## T3：Core · 进程采样器

文件：`dotnet/src/Starcloud.Core/Services/ProcessSampler.cs`（新建）、
`NovaFpsWatcher.cs`（改：只改 L212 那句注释）

1. 采样读数（设计 §3）：

```csharp
/// <summary>一次采样的读数；GpuPercent = null 表示计数器不可用（不写 0，避免与"真的 0%"混淆）</summary>
public readonly record struct GameRunSample(
    DateTime At, double CpuPercent, long WorkingSetBytes, long PeakWorkingSetBytes,
    int ThreadCount, double? GpuPercent);
```

2. `ProcessSampler : IDisposable`：
   - `ctor(int pid, Func<int, bool>? isAliveProbe = null)`：`Process.GetProcessById(pid)` 一次持有
     （不每秒重开句柄）；构造失败 → `_process = null`，此后 `TrySample` 恒 `false`；
   - `bool TrySample(bool collectGpu, out GameRunSample sample)`：
     ① 存活判定：`_process` 为 null 或 `HasExited` → `false`（**不抛 `InvalidOperationException`**，
     这是 `Process` 退出后读 `TotalProcessorTime` 的真实行为）；
     ② `Refresh()` 后取 `WorkingSet64` / `PeakWorkingSet64` / `Threads.Count`，
     每项各自 `try/catch`（`Threads` 在受限权限下会抛）→ 失败回退 0，不整条失败；
     ③ CPU%：**完全复用 `NovaFpsWatcher` L112-124 的口径**——
     `(nowTotal - lastTotal) / elapsed / Environment.ProcessorCount * 100`，`Math.Clamp(...,0,100)`；
     首次采样无差分基准 → `CpuPercent = 0` 并记下基准；
     ④ GPU：`collectGpu == false` 直接给 `null`；否则走 `SampleGpuPercent(pid)`；
     ⑤ 时间戳 `At = DateTime.Now`。
   - `SampleGpuPercent(int pid)` / `MarkGpuFail()` / `ExpandGpuInstances(pid)`：
     `NovaFpsWatcher.cs` L178-207 的**同口径私有副本**（`NovaFpsWatcher` 那几个成员是 private 实例方法，
     不能直接调），只把 `PdhNative` 换成直接复用：`PdhNative.ExpandWildcard(pattern)` /
     `PdhNative.TryCollectSum(paths, out sum)`；连续 2 次失败置 `_gpuBroken` 判死。
   - `Dispose()`：`_process?.Dispose()`。

3. `NovaFpsWatcher.cs` L212 注释改为
   `/// <summary>PDH P/Invoke（同程序集 internal，供 NovaFpsWatcher 与 ProcessSampler 共用）…</summary>`。

构建（`Starcloud.Core`）。

---

## T4：Core · 运行监控器

文件：`dotnet/src/Starcloud.Core/Services/GameRuntimeMonitor.cs`（新建）

1. 快照模型（设计 §3，按微修 1 去掉 `Diagnostics`）：

```csharp
public sealed record GameRunInfo(
    int Pid, string VersionId, DateTime StartedAt, TimeSpan Uptime,
    bool RecoveredFromLock,          // true = 跨启动器重启识别到
    GameRunSample Latest);
```

2. `GameRuntimeMonitor : IDisposable`：
   - `public static GameRuntimeMonitor Instance { get; } = new();`
     （生产单例；测试自己 `new`，不碰单例）
   - `void Configure(Func<string> dotMcProvider, GameRunHistoryStore? history = null,
     Func<int, bool>? isAliveProbe = null)`：AppServices ctor 里调一次；
     测试用临时目录 + 假探针。`isAliveProbe` 默认实现 = 设计 §7 要求的那份判活
     （`Process.GetProcessById` + `ProcessName.StartsWith("java")`），
     **与 `GameSessionGuard.IsAliveJavaProcess`（private static）同口径但不共享代码**。
   - 状态：`GameSession? _session`、`ProcessSampler? _sampler`、
     `GameLaunchContext? _context`、`string? _commandLine`、
     `LinkedList<GameRunSample> _samples`（上限 **600**）、
     `int _consecutiveFailures`（≥3 暂停采样）、`bool _killedByUser`。
   - 公开面：
     - `GameRunInfo? Current { get; }`
     - `string? CommandLine { get; }`（仅内存；恢复路径为 `null`）
     - `GameLaunchContext? LaunchContext { get; }`
     - `IReadOnlyList<GameRunRecord> History { get; }`（转发 `_history.Records`）
     - `IReadOnlyList<GameRunSample> Samples(int last)`（sparkline 取末 N 点）
     - `bool SamplingPaused { get; }`
     - `int IntervalMs { get; set; } = 1000`（页面「采样设置」写它）
     - `bool CollectGpu { get; set; } = true`（同上）
     - `event Action<GameRunInfo?>? Changed`（后台线程触发；Monitor 不引用 `DispatcherQueue`）
     - `event Action<GameRunRecord>? RunFinished`
     - `void Attach(GameSession session, GameLaunchContext? context, string? commandLine)`
     - `void Kill()`（`_killedByUser = true` → `_session?.Kill()`）
     - `void Detach()`（重启/结束时停跟踪，不删历史）
     - `IReadOnlyList<string> TailLog(int count)`：有 `_session` → `session.Tail(count)`；
       恢复路径 → 读 `LauncherPaths.GameLogsDir` 下最新 `game-*.log` 的尾 `count` 行（读失败返回空）
     - `void Start()` / `void Stop()`（`System.Threading.Timer`，`IntervalMs` 变更后重建）
   - `Attach` 流程：`_killedByUser = false`；`_consecutiveFailures = 0`；
     `_session = session`、`_context = context`、`_commandLine = commandLine`；
     `_sampler = new ProcessSampler(session.Pid, _isAliveProbe)`；
     `_samples.Clear()`；`GameLaunchContextStore.Write(dotMc, context)`（context 非空时）；
     `session.Exited += OnSessionExited`；起 Timer。
   - `OnSessionExited(int exitCode)`：
     ① 落历史：`Duration = session.Uptime`、`ExitCode = exitCode`、
     `PeakWorkingSetBytes` 取 `_samples` 里的 `Max(PeakWorkingSetBytes)`、
     `Crashed = !_killedByUser && exitCode != 0` → `_history.Append` + 触发 `RunFinished`；
     ② `GameLaunchContextStore.Delete(dotMc)`；
     ③ `session.Exited -= OnSessionExited`、`_sampler?.Dispose()`、`_samples.Clear()`、
     `_commandLine = null`、`_context = null`、`_session = null`、停 Timer；
     ④ `Changed?.Invoke(null)`。
   - Timer tick（后台线程，全程 `try/catch` 不外抛）：
     ① `Current` 为 null → 走**恢复探测**：`GameSessionGuard.Current(dotMc)`；
     活着 → `RecoveredFromLock = true` + `Read(dotMc, versionId)` 补上下文 +
     `new ProcessSampler(pid, _isAliveProbe)` + `Changed`；死了 → 不动（保持空态）；
     ② `Current` 非空 → `_sampler.TrySample(CollectGpu, out var s)`：
     成功 → 追加环形缓冲（超 600 丢弃最旧）+ `_consecutiveFailures = 0` + `Changed`；
     失败 → `++_consecutiveFailures`；达到 3 → `SamplingPaused = true` + `Changed`（并保留最后已知值）；
     ③ 恢复路径的死亡收口：恢复的会话无 `GameSession`，探测到 `GameSessionGuard.Current` 变 null 时，
     落一条 `ExitCode = null` 的历史（微修 7）+ 删残留 `game-launch.json` + `Changed(null)`。
   - `Uptime` 口径（设计 §4）：Attach 路径用 `session.StartedAt`；
     恢复路径用锁文件 `StartedAt`（界面加「约」字，T7 处理）。

3. `AppServices`（`Starcloud.App/Services/AppServices.cs`）：
   - 属性区 L87 之后加 `public GameRuntimeMonitor Runtime => GameRuntimeMonitor.Instance;`
     （**不 new**，单例在 Core 里）；
   - ctor L208 `LauncherLog.Write(...)` 之前加：
     `GameRuntimeMonitor.Instance.Configure(() => Settings.DotMinecraft);` +
     `GameRuntimeMonitor.Instance.Start();`

构建（整仓 `dotnet build dotnet\Starcloud.slnx -v q -nologo`）。

---

## T5：Core · `GameSession` 暴露启动命令行

文件：`dotnet/src/Starcloud.Core/Services/GameSession.cs`（改）、`GameLauncher.cs`（改）

1. `GameSession` 加两个只读成员（**只内存，不落盘，不写事件**）：

```csharp
/// <summary>本局完整启动命令行（含 --accessToken，仅内存持有，绝不落盘）</summary>
public string? CommandLine { get; }
```

   并在 ctor（L34）签名后加一个可选参数：
   `internal GameSession(Process process, string versionId, string logFile, string? commandLine = null)`，
   ctor 体内 `CommandLine = commandLine;`。
   `LaunchFile`/`_recent`/两个 pump 的顺序不变——**只多一个字段赋值**。

2. `GameLauncher.Launch`（L316-341）L338 改为
   `var session = new GameSession(p, versionId, logFile, commandLine);`。
   `LaunchVersionAsync`（L344-355）**零改动**——它已经把 `cmd` 传进 `Launch`。

构建（`Starcloud.Core`）。

---

## T6：Core · Nova 启动路径接入

文件：`dotnet/src/Starcloud.Core/Services/NovaToolsWrite.Versions.cs`（改）

1. `LaunchGameAsync`（L91-154）在 L137-141 之间插 Attach：

```csharp
var session = await _c.Launcher.LaunchVersionAsync(DotMc, id, options, ct);
SessionHandle.Bind(session.Pid);
var handle = SessionHandle;
session.Exited += _ => handle.Dispose();

// 运行状态监控：上下文取自本次已解析的 eff（不再重复 Resolve）
var gameRoot = eff.GameRoot;
var ctx = GameLaunchContextStore.Capture(DotMc, id, eff,
    GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "mods")),
    GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "shaderpacks")),
    GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "resourcepacks")));
GameRuntimeMonitor.Instance.Attach(session, ctx, session.CommandLine);
```

2. `MemoryAdvisor.Probe(GameRootOf(id))`（L113）保持不动——它已经在算 mods / shaderpacks，
   但 `Probe` 只返回 `(ModCount, HasShaders)`，拿不到资源包数，故这里用 `CountFiles` 直接数
   （两处读的是同一批目录，无一致性问题）。

构建（`Starcloud.Core`）。

---

## T7：App · 运行状态页

文件：`dotnet/src/Starcloud.App/Views/RunningStatusPage.xaml`（新建）、
`RunningStatusPage.xaml.cs`（新建）

### 7.1 XAML 骨架

根容器照 `LogsPage.xaml` 范式：`NavigationCacheMode="Disabled"` +
`Grid MaxWidth="1280" HorizontalAlignment="Stretch" Padding="32,12,24,24" RowSpacing="16"`。

页头：`TextBlock` 28 号「运行状态」（`beh:TextOutlineBehavior.IsEnabled="True"`）+ 描述行
「当前这一局游戏发生了什么」；右侧横排「刷新」（Glyph `E918`）与「采样设置」
（`DropDownButton` → `MenuFlyout`：采样间隔 1s/2s/5s 三选一 + 「采集 GPU 占用」`ToggleMenuFlyoutItem`）。

主区 `ScrollViewer` → `StackPanel Spacing="16"`，四波段（设计 §5.1）：

1. **Hero**（`Border Style="{StaticResource ScCard}" x:Name="HeroCard"`）：
   第一行 = 运行中芯片（`Border ScChipAccent` + `TextBlock ScChipAccentText` 文案「运行中」，
   `FontWeight="Bold"`，前景走 `TextOnAccentFillColorPrimaryBrush` = 品牌 accent 面上的黑字）+ 
   `PID 18342` + `已运行 00:42:17`（等宽）；
   第二行 = 版本 / 隔离 / 内存（`ScMetaValue`）；
   若 `RecoveredFromLock` 追加一枚 `ScChipCaution` 芯片「上一轮启动器启动」，且时长前缀加「约」。
   按钮行右对齐：`打开日志` / `诊断` / `复制命令` / `结束进程`。
   `结束进程` 用 `Button`，`Foreground="{ThemeResource DangerBrush}"` 语义色不铺底。
   无游戏时整块换成 `EmptyStateView`（`Glyph="&#xEC70;"`、
   `Title="现在没有游戏在运行"`、`Subtitle="去首页启动一个版本，或看看下面的最近运行记录"`、
   `ActionText="去首页"` → `ActionClick` 里 `MainWindow.NavigateTo("home")`）。
2. **性能三卡**（`Grid` 三列 `ColumnSpacing="16"`，每卡 `ScCard`）：
   每卡 = `ScMetaLabel` 标签 + 大号数字（`FontSize="30" FontWeight="SemiBold"`，等宽）
   + `Polyline` sparkline（`Height="44"`，`Stroke="{ThemeResource AccentFillColorDefaultBrush}"`
   `StrokeThickness="1.5"`）+ 次要行 `ScMetaValue`。
   CPU 卡次要行「N 线程」；内存卡次要行「峰值 X · N 线程」；GPU 卡次要行
   `GpuPercent is null ? "计数器不可用" : "GPU 引擎占用"`。
   `SamplingPaused` 时三卡统一覆盖一枚 `ScChipCaution`「采样已暂停」+ 页头「重试」按钮。
3. **双栏**（`Grid` 两列，`ColumnSpacing="16"`，`ColumnDefinitions` 用
   `Width="1*"` / `Width="1*"`，窄窗（<900）时退化为单列——用 `VisualStateManager` 或
   简单 `Grid` + `x:Name` 两栏 `Border` 各占 `ColumnSpan`，实现取与仓库既有双栏页一致的写法）：
   - 左「启动配置与 JVM」`ScCard`：分区标题（`ScSectionSpine` + `ScSectionTitle`）；
     行 = Java 路径（等宽、超长省略 + `ToolTip`）、`-Xmx` / `-Xms`、窗口 `W×H`（全屏时写「全屏」）、
     隔离模式、额外 JVM 参数（每条一行、等宽、可选中）；
     上下文来自 `GameLaunchContextStore` 缺失时（降级）：用
     `InstanceLayout.Resolve(dotMc, versionId, store.Get(versionId), _s.Settings, MemoryAdvisor.Probe(gameRoot))`
     现场解析，并加一枚 `ScChipCaution`「启动参数按当前版本配置显示」。
   - 右「内容与实时日志」`ScCard`：首行三枚 `ScChip`「Mod 142」「着色器 2」「资源包 3」；
     下方 `ScrollViewer Height="180"` + `TextBlock FontFamily="Cascadia Code" FontSize="12"
     TextWrapping="NoWrap" IsTextSelectionEnabled="True"`（日志尾 `TailLog(200)`）。
     自动吸底；用户手动上滚后暂停吸底并显示「回到底部」按钮（`ScrollViewer.ViewChanged` 判
     `VerticalOffset < ScrollableHeight - 8`）。
4. **最近运行**（`ScCard`）：`ListView`（`ScPlainListItem` 行样式），
   默认显示前 5 条、`展开全部`按钮切到 50 条；每行 = 版本 / 时长 / 峰值内存 / 退出码与状态
   （`Crashed` → `DangerBrush`「异常退出」；`ExitCode == 0` → `SuccessBrush`「正常退出」；
   `ExitCode is null` → `ScMetaValue`「已结束」；非 0 → `DangerBrush`「退出码 N」）。
   历史为空 → 该卡内换成 `EmptyStateView`（`Title="还没有运行记录"`，无按钮）。

### 7.2 代码后置

- 字段：`private readonly AppServices _s = AppServices.Instance;`、`DispatcherQueue _dq = ...`、
  `bool _autoScroll = true`、`bool _histExpanded`、`List<GameRunInfo?> ` 不需要——
  渲染入口只有一个 `Render()`。
- `Loaded`：订阅 `_s.Runtime.Changed += OnChanged`、`_s.Runtime.RunFinished += OnRunFinished`；
  调 `Render()`；启动 `_uiTimer`（`DispatcherQueueTimer` 1s，只更新「已运行 00:42:17」这一行，
  避免每秒重建整页）。`Unloaded`：全部退订 + `_uiTimer.Stop()`（页面 `NavigationCacheMode="Disabled"`，
  每次进入都是新实例，退订是防止泄漏）。
- `OnChanged(GameRunInfo? info)` → `_dq.TryEnqueue(() => { _latest = info; Render(); })`
  （Monitor 在后台线程触发，必须回 UI 线程）。
- `Render()`：按 `_latest is null` 切 Hero/EmptyState 两块可见性；填数字；
  重建三条 `Polyline.Points`（`Samples(60)` → 归一化到 `Width=160 Height=44`，
  空/单点时不画，避免除零）；刷新日志尾（仅在行数变化时重设 `Text`，保住用户的选中）。
- 按钮：
  - `打开日志` → `App.PendingLogSource = "game"; (App.MainWindow as MainWindow)?.NavigateTo("logs");`
  - `诊断` → `await _s.Diagnostics.DiagnoseAsync(dotMc)`，结果渲染到卡下方 `InfoBar`
    （`Severity`：有 issue → `Warning`，无 → `Success`），
    每条 issue 一行 `ScMetaLabel`（Kind / Detail），`AutoFixable` 时不提供一键修复（本页不做修复，
    修复入口仍在既有诊断流程里，避免两套修复路径）。
  - `复制命令` → `Clipboard.SetContent(new DataPackage { RequestedText = cmd })`；
    `_s.Runtime.CommandLine is null` 时 `IsEnabled = false` + `ToolTip="上一轮启动器启动的会话，拿不到命令行"`。
  - `结束进程` → `ContentDialog`（`Title="结束当前游戏？"`、`PrimaryButtonText="结束进程"`、
    `CloseButtonText="取消"`、`DefaultButton=Close`）→ 确认后 `_s.Runtime.Kill()`；
    抛错/被拒 → `InfoBar` 提示原因（设计 §7）。
  - `重启`（Hero 上与「结束进程」并列，文案「重启」）→ 确认后
    `_s.Runtime.Kill(); App.PendingLaunchVersionId = info.VersionId;
    (App.MainWindow as MainWindow)?.NavigateTo("home");`
    （微修：复用既有跨页启动通道，不新增第二条启动路径）。
  - `采样设置` → 写 `_s.Runtime.IntervalMs` / `CollectGpu`（只存内存，随页面销毁回到默认）。
  - `刷新` → `_s.Runtime.Start()`（重起 Timer，同时清 `SamplingPaused`）。

构建（整仓）。

---

## T8：App · 两条 App 启动路径接入

文件：`dotnet/src/Starcloud.App/Views/HomePage.xaml.cs`（改）、`SavesPage.xaml.cs`（改）

1. `HomePage.xaml.cs` L944-948 之后插 Attach（用已经算好的 `eff` / `options`，
   若局部变量名不同以实际为准）：

```csharp
var gameRoot = options.GameDir;
var ctx = GameLaunchContextStore.Capture(dotMc, versionId,
    eff,   // 启动链路已解析的 EffectiveLaunch（若不可见则按 T7 降级方式现场 Resolve）
    GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "mods")),
    GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "shaderpacks")),
    GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "resourcepacks")));
_s.Runtime.Attach(session, ctx, session.CommandLine);
```

   放在 `_sessionHandle?.Bind(...)` 与 `session.Exited += OnGameExited;` 之间，
   保证取消分支（L950 `_cancelRequested` → `Kill`）走的是 `Kill → Exited → 落历史` 同一条收口。
2. `SavesPage.xaml.cs` L446-466 同样插 Attach（位置在 `_sessionHandle?.Bind(_gameProcess.Pid);` 之后）。
3. 三处都不改 `App.RunningVersionId` 的既有语义（它仍由 HomePage L968 维护，
   本计划不动它，避免影响版本页/详情页的既有判断）。

构建（整仓）。

---

## T9：App · 导航项与运行中圆点

文件：`dotnet/src/Starcloud.App/MainWindow.xaml`（改）、`MainWindow.xaml.cs`（改）

1. `MainWindow.xaml`：在 `联机`（L361-365）与 `<NavigationViewItemSeparator />`（L366）之间插：

```xml
<NavigationViewItem loc:Loc.Text="运行状态" Tag="running">
    <NavigationViewItem.Icon>
        <FontIcon FontFamily="{StaticResource ScIconFont}" Glyph="&#xEC3B;" />
    </NavigationViewItem.Icon>
</NavigationViewItem>
```

   （`EC3B` 计时器，已确认全库未占用；位置落在「游戏组末尾」。）
   运行中圆点用导航项内容右侧的一枚静态 `Ellipse`：把 `Content` 换成横排
   `StackPanel`（`TextBlock` 文案 + `Ellipse x:Name="RunningDot" Width="6" Height="6"
   Fill="{ThemeResource SuccessBrush}" Visibility="Collapsed" Margin="8,0,0,0"`），
   **不做呼吸动画**（设计 §5.2 明确要求）。

2. `MainWindow.xaml.cs`：
   - `Nav_SelectionChanged` L236-248 的 switch 加 `"running" => typeof(RunningStatusPage),`；
   - `WatchGameEvents` L135-161 内，在算出 `running` 之后调一句 `UpdateRunningDot(running);`
     （**复用 L56-61 的 300ms `_taskRefresh`，不新增定时器**）；
   - 新增：

```csharp
/// <summary>导航项「运行状态」右侧圆点：有游戏在跑就点亮（静态，不做呼吸动画）</summary>
private void UpdateRunningDot(string? running)
{
    var dot = RunningDot;
    if (dot is null) return;
    var on = running is not null;
    if (on == (dot.Visibility == Visibility.Visible)) return;   // 状态没变不碰 XAML
    dot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
}
```

   `WatchGameEvents` 现有的 `catch { }`（L157-160）正好兜住本句可能的 XAML 访问异常。

构建（整仓）。

---

## T10：App · 日志页跳转通道

文件：`dotnet/src/Starcloud.App/App.xaml.cs`（改）、`Views/LogsPage.xaml.cs`（改）

1. `App.xaml.cs` L15 附近加（与 `PendingLaunchVersionId` 并列）：

```csharp
// 跨页日志来源请求：「运行状态」页点「打开日志」→ 导航到日志页并切到游戏日志
public static string? PendingLogSource;
```

2. `LogsPage.xaml.cs` 加：

```csharp
protected override void OnNavigatedTo(NavigationEventArgs e)
{
    base.OnNavigatedTo(e);
    var src = App.PendingLogSource;
    App.PendingLogSource = null;
    // Loaded 晚于 OnNavigatedTo：这里只记意图，真正切来源在 Loaded 里做
    _pendingSource = src;
}
```

   `Loaded` 回调（现 L27 附近）改为：`var idx = _pendingSource == "game" ? 1 : 0; _pendingSource = null;
   LogSource.SelectedIndex = idx; _ready = true; Load();`——**默认仍是 0（启动器日志），
   为 `null` 时行为与现状完全一致**（`Send` 空值检查不能破坏既有 400 行尾部读取）。

构建（整仓）。

---

## T11：Core 测试

文件：`dotnet/tests/Starcloud.Core.Tests/GameRuntimeTests.cs`（新建，一个文件装全部用例，
内部两个 `TempDir` 助手类：`TempDataDir`（用于 `GameRunHistoryStore` / `LauncherPaths` 相关）
与沿用 `GameLauncherClasspathTests.cs` L86 的 `TempDotMc` 写法）

| # | 用例 | 断言 |
| --- | --- | --- |
| 1 | 历史：上限裁剪 | 追加 60 条 → `Records.Count == 50`，且最早 10 条不在其中 |
| 2 | 历史：原子写 | 写坏 `.tmp` 后 `Append` → 原文件内容逐字不变 |
| 3 | 历史：损坏容错 | 文件写 `{ 不是 json` → `TryLoad()` 返回空列表、不抛 |
| 4 | 历史：读失败拒绝写 | 索引损坏时 `Append` 返回 `false` 且**文件字节数未变** |
| 5 | 采样器：自身进程 | `new ProcessSampler(Environment.ProcessId)` → `TrySample` 为 true、`CpuPercent >= 0`、`WorkingSetBytes > 0`、`ThreadCount > 0` |
| 6 | 采样器：GPU 不可用 | `collectGpu: false` → `GpuPercent is null`（不是 0） |
| 7 | 采样器：进程已退出 | 起一个短命子进程（`cmd /c exit`）等它退出 → `TrySample` 返回 `false`，不抛 |
| 8 | 监控器：恢复路径 | 预置锁文件（写一个存活的自身 pid + 假探针恒 true）+ 预置上下文 → `Attach` 前 tick 一次 → `Current!.RecoveredFromLock == true`、`VersionId` 正确 |
| 9 | 监控器：恢复路径（已死） | 假探针恒 false → `Current is null`，且 `game-session.json` 未被本进程改写 |
| 10 | 监控器：Attach → 退出落历史 | 用短命子进程包 `GameSession`? 不可行 → **改为注入式**：`Attach` 后直接调内部收口方法（`internal void OnSessionExited(int)`，`InternalsVisibleTo` 或把它设为 `public`）→ 历史新增一条，`Duration` / `ExitCode` 正确 |
| 11 | 监控器：环形缓冲上限 | 推 700 次采样（走同一注入路径）→ `Samples(1000).Count == 600`，首尾顺序正确 |
| 12 | 上下文：往返 | 写读一致，含引号 / `=` / 空格的 `ExtraJvmArgs` 逐字相等（微修 5） |
| 13 | 上下文：损坏 / 版本不符 | 写坏 json → `Read` 返回 null；`Read(dotMc, "别的版本")` 返回 null |
| 14 | 敏感信息不落盘 | `Attach(session, ctx, "… --accessToken SECRET123 …")` → 枚举 `dotMc/starcloud/*` + `dataDir/*` 所有文件内容，**都不含** `SECRET123` |
| 15 | 锁语义兼容 | 走既有 `GameSessionGuard.TryAcquire/Bind/Dispose` 全流程后，`GameSessionGuard.Current` 与 Monitor 的恢复探测结论一致（证明未破坏既有语义） |

说明：
- 用例 10 / 11 需要 Monitor 暴露测试入口。**实现方式**：把收口方法写成
  `internal void RaiseSessionExitedForTest(int exitCode)` 与
  `internal void PushSampleForTest(in GameRunSample s)`，
  并在 `Starcloud.Core.csproj` 加
  `<InternalsVisibleTo Include="Starcloud.Core.Tests" />`
  （先确认 csproj 里是否已有 `InternalsVisibleTo`；已有就只加程序集名）。
- 用例 8 / 9 / 15 的「假探针」注入点就是 T4 的 `Configure(..., Func<int,bool>? isAliveProbe)`。
- 全表不得依赖机器上真的跑着 java、不得占用真实 `LauncherPaths.DataDir`
  （用 `STARCLOUD_DATA_DIR` 重定向或直接给 `GameRunHistoryStore` 传临时目录）。

构建 + 测试（`dotnet test dotnet\tests\Starcloud.Core.Tests`）。

---

## T12：文档 + 全量验证

1. `docs/USER_MANUAL.md`：
   - 在「## 11. 启动可靠性」之后新增「## 12. 运行状态页」，原 12-16 顺延为 13-17：
     内容 = 页面能看到什么（Hero / 性能三卡 / 启动配置 / 内容与日志 / 最近运行）、
     五个操作（打开日志 / 诊断 / 复制命令 / 结束进程 / 重启）、
     跨启动器重启识别与「约」的说明、GPU 计数器不可用时的表现。
   - 「## 2. 界面总览」的导航列表补一项「运行状态」。
   - 「## 14. 启动器日志页」补一句：从运行状态页点「打开日志」会直接落到游戏日志来源。
2. `docs/ARCHITECTURE.md`：在启动链路那一节补一段「运行态监控」——
   `GameRuntimeMonitor` 是运行态唯一来源，锁文件是跨进程唯一真相，页面只订阅。
3. `docs/API.md`：如存在 `GameSession` / `GameLauncher` 条目，
   `GameSession` 补 `CommandLine` 成员说明（含「仅内存、不落盘」）。
4. `docs/PERFORMANCE_REPORT.md`：补一行采样开销（1s 心跳，PDH GPU 计数器复用 Nova 口径）。
5. 新增 UIA 复验脚本 `dotnet/uia-running-status.ps1`（**纯 ASCII**，中文用
   `function S([int[]]$c) { -join ($c | ForEach-Object { [char]$_ }) }` 生成；
   按 `ControlType` 枚举后逐个比对 `$e.Current.Name -eq $name`，不用 `PropertyCondition(Name, 中文)`）：
   - 断言导航栏存在 `运行状态` 且其前一项是 `联机`；
   - 点击进入 → 断言空态标题存在；
   - 断言「最近运行」卡存在。
6. 全量验证：
   - `dotnet build dotnet\Starcloud.slnx -v m` → 0 错 0 警；
   - `dotnet test dotnet\tests\Starcloud.Core.Tests\Starcloud.Core.Tests.csproj` → 全绿；
   - 实机：启动一局游戏 → 页面出现 Hero「运行中」、三卡数字 1s 跳动、日志尾滚动；
   - 实机：启动游戏 → 完全退出启动器 → 重开 → 页面仍显示「运行中」并标「上一轮启动器启动」；
   - 实机：深色 / 浅色两套主题目视无对比度问题；设置里把 UI 动效关掉后无残留动画。

## 依赖顺序

```
T1 → T2 → T3 → T4 → T5 → T6 ─┐
                              ├→ T7 → T8 → T9 → T10 → T11 → T12
T5 ───────────────────────────┘
```

- T1–T3 互相独立，可并行；但 `T4` 依赖三者，`T6` 依赖 `T4` + `T5`。
- `T7` 依赖 `T4`（页面订阅 Monitor），`T8` 依赖 `T7`（要用 `_s.Runtime` 与 `AppServices.Runtime`）。
- 全程不引入任何第三方依赖（sparkline 自绘、GPU 复用同程序集 `PdhNative`）。
