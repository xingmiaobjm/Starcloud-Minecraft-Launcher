# 启动可靠性（崩溃诊断 · 实时日志 · 环境体检 · 自动重试）实现计划

日期：2026-09-11
设计文档：docs/designs/2026-09-11-launch-reliability-design.md
所属：PCL2 平替第 C 组（启动可靠性）

构建命令（每个任务完成后执行）：

```powershell
Stop-Process -Name Starcloud.App -Force -ErrorAction SilentlyContinue
dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo
```

目标：0 警告 0 错误。

## 计划期微修（相对 spec 的实现决定，不改变设计与验收）

1. **`Launch` 返回类型统一为 `GameSession`，并让它长得像 `Process`**。
   spec §3.2 写「返回 `GameSession` 而非裸 `Process`」，但三个调用点都用 `Process` 成员
   （`HomePage` L363/L364/L435/L470/L472/L596-623、`SavesPage` L418/L431/L432/L455、
   `NovaInviteService` L301 忽略返回值）。
   为让改动面收敛，`GameSession` 提供兼容子集：
   `Id`（=pid）/ `HasExited` / `Pid` / `Refresh()` / `MainWindowHandle` / `Kill(bool entireTree = true)` /
   `MainModule` 不需要；`Exited` 事件签名由 `EventHandler` 变为 `Action<int>`（带退出码）。
   `NovaInviteService` 仅丢弃返回值，无需改动。
2. **`SystemMemory.AutoMaxMb()` 保持无参签名**，实现改为 `MemoryAdvisor.SuggestMaxMb(_totalMb ?? 8192, 0, false)`
   兼容语义；带模组上下文的推荐走 `InstanceLayout.Resolve` 的**可选参数**重载
   （`autoMemContext`），既有调用点零改动。
3. **体检 Warn 级不弹窗**，只写启动页步骤行 + `launcher.log`（spec §6.3 已定），避免每次启动弹窗。
4. **`GameSessionGuard` 的锁文件放在 `.minecraft/starcloud/`**（随实例目录，共享 `.minecraft` 的多窗口互斥），
   而非 `LauncherPaths.DataDir`（spec §9 已定）。`GameSessionFile(dotMc)` 需建目录兜底。
5. **实时日志 UI 用 `TextBox`（只读、等宽、自动滚动）而不是 `TextBlock` + `ScrollViewer`**：
   `TextBox` 自带滚动与选中复制，追加 200 行后 `SelectionStart = Text.Length` 即可滚到底，代码量更小。
6. **日志页「游戏日志」下拉**用 `ComboBox` 列 `game-*.log` 最近 10 个（按修改时间倒序），
   默认选最新；不引入分页。

## 前置确认（已核实，实现时不再重复探索）

- **`GameLauncher`**（`dotnet/src/Starcloud.Core/Services/GameLauncher.cs`）：
  `LaunchOptions` L11–30；`LaunchCredentials` L33；`BuildCommandLineAsync` L56–196；
  `ResolveJavaAsync` L199–214；`QuoteIfNeeded` L244；`ModernWindowArgs` L248；
  **`Launch` L255–273**（`FileName = commandLine.Split(' ')[0].Trim('"')`，`EnableRaisingEvents = true`）；
  **`LaunchVersionAsync` L276–287**（`LoadVersionAsync` → `BuildCommandLineAsync` → `CreateDirectory(GameDir)`
  → `EnsureFirstRunLanguage` → `PatchLanguage` → `Launch`）；
  `RunPreLaunchAsync` L319–338；`RunCaptureAsync` L340–348；`AppendPostLaunchLog` L382–390
  （写到 `LauncherPaths.LogsDir/game-postlaunch.log`）。
- **`HomePage.xaml.cs`**：`LaunchStage` 枚举 L26–30（`Idle, VerifyingCredentials, CheckingFiles, RepairingFiles,
  StartingProcess, WaitingWindow, Running, Exited, Failed`）；字段 L22 `Process? _gameProcess`；
  `ApplyStage` 分支 L~260–332（`switch (stage)` 设置标题/进度/按钮可见性）；
  `SetStageUi` L335–344；`WindowTimer_Tick` L353–368（用 `Refresh()` + `MainWindowHandle`）；
  `ShowOverlay` L371–397；`HideOverlay` L401–410；`LaunchCancelBtn_Click` L427–438；
  `LaunchBtn_Click` L467–480（`_gameProcess is { HasExited: false }` → `Kill(true)`）；
  `StartLaunchAsync` L483–650（预检 L520–567、启动前命令 L569–594、拉起 L596–597、`Exited` 挂载 L598–611、
  取消兜底 L612–618、`OnGameLaunched` L623）。
- **`HomePage.xaml`**：覆盖层 L331–409（`StackPanel MaxWidth=480 Spacing=14`），
  `LaunchErrorText` L390–393，按钮行 L396–407。日志区插在 `LaunchTipText` Border（L373–388）
  与 `LaunchErrorText`（L390）之间。
- **`SavesPage.xaml.cs`**：`Process? _gameProcess` L22；`LaunchCopiedAsync` L381–451
  （`App.RunningVersionId` 检查 L385–390；`LaunchVersionAsync` L418；`Exited` L431；`rec.ProcessId` L432）；
  `StopGame_Click` L453–457；`OnGameExitedAsync`（归属三选一，T8 已实现）。
- **`JavaService`**：`JavaRuntime(Executable, MajorVersion, VersionString, Is64Bit, Source)`；
  `ListRuntimesAsync(ct, officialRuntimeDir)`；`DownloadRuntimeAsync(major, progress, ct)`；`ProbeAsync(exe)`。
- **`SystemMemory`**：`Snapshot()` → `(TotalMb, UsedMb)`（失败回退 `(8192, 4096)`）；`AutoMaxMb()`。
- **`GameDiagnosticsService`**：`DiagnoseAsync(dotMc, ct)` → `DiagReport`；
  `RepairAsync(versionId, settings, ct)` → `Task<string>`（Fabric/vanilla 可修，Forge/NeoForge 抛异常）。
- **`ModConflictService`**：`ScanAsync(dotMc, ct)` → `ModConflictReport`；`ResolveAsync(dotMc, ct)`。
- **`InstanceLayout.Resolve`** 签名 L218–220；`"auto"` 内存档位 L226。
- **`LauncherSettings`**（`LauncherPaths.cs` L76–209）与 `LauncherPaths`（L8–55，含 `LogsDir`）。
- **`LogsPage`**：`.xaml` 内 `LogText` TextBlock；`.cs` 仅 `Load()` 读 `launcher.log` 尾部 400 行。
- **`AppServices`**（`Starcloud.App/Services/AppServices.cs`）：字段 L11–39，ctor L41–75，
  `Diagnostics` L64、`Preflight` L65、末行 `NovaRuntime = new NovaRuntime(this)` L74。

---

## T1：Core · 启动器日志基础设施

文件：`dotnet/src/Starcloud.Core/Services/LauncherLog.cs`（新建）、
`dotnet/src/Starcloud.Core/Services/LauncherPaths.cs`（改）

1. `LauncherPaths` 新增（放在 `LogsDir`(L45) 之后）：

```csharp
/// <summary>启动器侧保存的游戏日志（每局一个文件）</summary>
public static string GameLogsDir => Path.Combine(LogsDir, "game");

/// <summary>跨进程游戏会话锁文件（随实例目录，共享 .minecraft 的多窗口互斥）</summary>
public static string GameSessionFile(string dotMc) =>
    Path.Combine(dotMc, "starcloud", "game-session.json");
```

`EnsureDataDir()`(L47–54) 增补 `Directory.CreateDirectory(GameLogsDir);`。

2. 新建 `LauncherLog.cs`（spec §4.1）：静态类，`Write(category, message)` / `Error(category, message, ex)` /
   `Tail(count)` / `event Action<string>? Appended`。
   - 落盘路径 `LauncherPaths.LogsDir/launcher.log`；跨天时改写 `launcher-<yyyy-MM-dd>.log`（当日仍写 `launcher.log`，
     实现方式：进程启动时记录日期，`Write` 时若日期变化则切换文件名）。
   - 内存环形缓冲 `Queue<string>` 上限 500；`lock (_gate)` 保护。
   - 单文件 > 5 MB 时截断保留尾部 2 MB（读回重写，失败静默）。
   - 全部 IO 包 `try/catch` 静默（日志绝不能影响启动）。

3. 埋点（本任务先埋「日志系统自检」一条；启动链路埋点在 T8 一并加）：
   在 `AppServices` ctor 末尾 `LauncherLog.Write("app", $"启动器就绪，数据目录 {LauncherPaths.DataDir}");`。

构建。

---

## T2：Core · `GameSession` 与输出重定向

文件：`dotnet/src/Starcloud.Core/Services/GameSession.cs`（新建）、
`dotnet/src/Starcloud.Core/Services/GameLauncher.cs`（改）

1. 新建 `GameSession`（spec §4.2）：
   - 构造 `internal GameSession(Process process, string versionId, string logFile)`；
   - 两个读流循环：`ReadLineAsync`（`Process.StandardOutput` / `StandardError`）→
     `HandleLine(stream, line)`：加 `[OUT]`/`[ERR]` 前缀 → 入环形缓冲（上限 1000）→ 追加 `StreamWriter`（UTF-8 无 BOM，
     `AutoFlush = true`）→ 触发 `OutputLine`；
   - 退出：`await process.WaitForExitAsync()` 后 `ExitCode = process.ExitCode`（**必须在 WaitForExit 之后读**）、
     `HasExited = true`、`RaiseExited(exitCode)`（`Interlocked` 保证只触发一次），最后关流；
   - 兼容子集：`Id => process.Id`、`Pid`、`Refresh()`（`process.Refresh()`）、
     `MainWindowHandle => process.MainWindowHandle`（`HasExited` 时返回 `IntPtr.Zero`）、
     `Kill(bool entireTree = true)`（`process.Kill(entireTree)`，吞异常）；
   - `Uptime => DateTime.Now - StartedAt`；`Tail(count)` 返回快照；
   - `Dispose()`：解绑并 `process.Dispose()`。
   - 日志文件超 20 MB 停止写入（置标志位，不再追加）。
2. `GameLauncher.Launch`(L255–273) 改造：
   - 加 `RedirectStandardOutput = true, RedirectStandardError = true,
     StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8`；
   - 方法签名 `public GameSession Launch(string commandLine, string workingDirectory,
     string versionId, IReadOnlyDictionary<string,string>? envVars = null)`；
   - 日志文件名 `game-<yyyyMMdd-HHmmss>.log` 放 `LauncherPaths.GameLogsDir`（先 `CreateDirectory`）；
   - 去掉 `EnableRaisingEvents`（改由 `GameSession` 的 `WaitForExitAsync` 统一收口，避免双路径）；
   - 返回 `new GameSession(p, versionId, logFile)`。
3. `LaunchVersionAsync`(L276–287)：返回类型 `Task<GameSession>`，末尾 `return Launch(cmd, options.GameDir,
   versionId, options.EnvVars);`。
4. `NovaInviteService` L301 忽略返回值，无需改动。

构建（此时 `HomePage`/`SavesPage` 会因类型不匹配报错，属预期 —— T8/T10 修复；**本任务不要求整仓通过**，
但需确认 `Starcloud.Core` 单独编译通过：`dotnet build dotnet\src\Starcloud.Core\Starcloud.Core.csproj -v q -nologo`）。

---

## T3：Core · 智能内存顾问

文件：`dotnet/src/Starcloud.Core/Services/MemoryAdvisor.cs`（新建）、
`SystemMemory.cs`（改）、`InstanceSettings.cs`（改）

1. 新建 `MemoryAdvisor`（spec §8.2）：`SuggestMaxMb(long totalPhysicalMb, int modCount, bool hasShaders)`、
   `Explain(...)`、`Probe(string gameRoot)` → `(int ModCount, bool HasShaders)`。
   - `Probe`：`gameRoot/mods/*.jar` 计数；`gameRoot/shaderpacks` 目录存在且非空 → `HasShaders = true`；
     目录不存在返回 `(0, false)`；异常吞掉返回 `(0, false)`。
   - 公式按 spec §8.2，结果向下取 512 倍数，下限 2048。
2. `SystemMemory.AutoMaxMb()` 改为委托 `MemoryAdvisor.SuggestMaxMb(_totalMb, 0, false)`，
   其中 `_totalMb` 取 `Snapshot().TotalMb`（`<= 0` 时用 8192 兜底）。
3. `InstanceLayout.Resolve` L218 加重载：

```csharp
public static EffectiveLaunch Resolve(string dotMc, string versionId, VersionProfile profile,
    LauncherSettings settings, (int ModCount, bool HasShaders)? autoMemContext = null)
```

`"auto"` 分支（L226）改为：有 `autoMemContext` 时
`(512, MemoryAdvisor.SuggestMaxMb(SystemMemory.Snapshot().TotalMb, ctx.ModCount, ctx.HasShaders))`，
否则 `(512, SystemMemory.AutoMaxMb())`。

构建（`Starcloud.Core`）。

---

## T4：Core · 游戏会话锁（防双开）

文件：`dotnet/src/Starcloud.Core/Services/GameSessionGuard.cs`（新建）

1. 按 spec §8.3 实现 `GameSessionGuard.TryAcquire(dotMc, versionId, out string? blockedBy)` +
   `GameSessionHandle : IDisposable`（`VersionId` / `Pid` / `Bind(pid)` / `Dispose()`）。
   - 锁文件 JSON：`{ "pid": n, "versionId": "x", "startedAt": "..." }`（`JsonSerializer` + `UnsafeRelaxedJsonEscaping`）。
   - `TryAcquire`：读锁 → `Process.GetProcessById(pid)` 存活且 `ProcessName` 以 `java` 开头 → 返回 null
     （`blockedBy = 锁里的 versionId`）；读锁抛异常/pid 死/进程名不符 → 视为失效锁，覆盖写自身锁。
   - `Dispose`：删锁文件（吞异常）。**同时提供 `static void ReleaseIfOwner(string dotMc, int pid)`**
     兜底（pid 匹配才删），用于启动失败路径。
   - `Pid` 在 `Bind(pid)` 前为 0（锁内容先写 0，`Bind` 后重写）；`TryAcquire` 判定时 `pid <= 0` 视为无效锁。

构建（`Starcloud.Core`）。

---

## T5：Core · 启动前环境体检

文件：`dotnet/src/Starcloud.Core/Services/EnvironmentCheckService.cs`（新建）

1. 模型按 spec §6.1：`EnvLevel` / `EnvIssue` / `EnvironmentReport`。
2. `CheckAsync(string dotMc, string versionId, LaunchOptions options, LauncherSettings settings,
   CancellationToken ct = default)`：
   - 解析 GameRoot = `options.GameDir`；模组上下文 `MemoryAdvisor.Probe(options.GameDir)`；
   - 检查项按 spec §6.2 表逐条实现；`java_missing` 用 `JavaService.ListRuntimesAsync`
     （需要注入 `JavaService` 与 `InstallerService`（读 `javaVersion.majorVersion` 用 `LoadVersionAsync`，
     失败则按 8 处理，不抛）；
   - 全程不联网、不做 SHA1；单条检查各自 try/catch（单项失败不拖垮体检）。
3. 构造函数注入 `InstallerService` + `JavaService`。

构建（`Starcloud.Core`）。

---

## T6：Core · 崩溃诊断

文件：`dotnet/src/Starcloud.Core/Services/CrashAnalysisService.cs`（新建）

1. 模型按 spec §7.2：`CrashFixKind` 枚举 + `CrashDiagnosis` record。
2. `AnalyzeAsync(dotMc, versionId, exitCode, sessionTail, ct)`：
   - 证据回退链（spec §7.2）：`crash-reports/*.txt`（`LastWriteTime >= sessionStartedAt` 需调用方传入
     `DateTime? startedAt` 参数；改为 `AnalyzeAsync(dotMc, versionId, exitCode, DateTime? startedAt,
     IReadOnlyList<string>? sessionTail, ct)`）→ `<options.GameDir>/logs/latest.log` 尾 200 行 →
     `sessionTail` 尾 60 行；
   - 规则表按 spec §7.2 顺序匹配（大小写不敏感），命中即返回；
   - 内存类规则用 `MemoryAdvisor.SuggestMaxMb(SystemMemory.Snapshot().TotalMb, modCount, hasShaders)`
     填 `SuggestMaxMemoryMb`；modCount 由 `MemoryAdvisor.Probe(options.GameDir)` 提供
     （`AnalyzeAsync` 增参 `string gameRoot`）；
   - 全部未命中 → `Fix = OpenCrashReport` 的兜底诊断；
   - 证据为空（无任何日志）→ `Fix = ReinstallVersion` 的「启动即失败」诊断。
3. 证据片段 `Evidence` 取命中行本身 + 前一行（≤3 行），单行 ≤200 字符。

构建（`Starcloud.Core`）。

---

## T7：App · 服务装配与设置项

文件：`dotnet/src/Starcloud.App/Services/AppServices.cs`、`LauncherPaths.cs`、`LogsPage.xaml(.cs)`

1. `LauncherSettings` 新增 `LaunchAutoRetry = true` / `ShowGameLogOnLaunch = false`（spec §9）。
2. `AppServices` 新增字段 `SessionGuard`（静态类不需要实例，改为 Core 静态调用，**不注册**）、
   `CrashAnalyzer`（`CrashAnalysisService`）、`EnvCheck`（`EnvironmentCheckService`）；
   ctor 中 `CrashAnalyzer = new CrashAnalysisService(Installer, MemoryAdvisor 静态即可)`;
   `EnvCheck = new EnvironmentCheckService(Installer, Java);`（放在 `Preflight`(L65) 之后）。
3. `LogsPage.xaml`：顶部加 `RadioButtons`（水平，两项「启动器日志」「游戏日志」）+ `ComboBox`（游戏日志文件）
   + 「打开日志目录」Button；下方保留 `LogText`。
4. `LogsPage.xaml.cs`：`Load()` 按选择读 `launcher.log` 或 `GameLogsDir/game-*.log`；
   切到「游戏日志」时填充 `ComboBox`（最近 10 个，倒序）；空态文案区分两种日志。

构建（整仓，此时 T2 引入的 HomePage/SavesPage 类型错误仍在 → 只确认本任务文件无新增错误；
**若整仓编译被 T2 阻塞，则本任务只验证 XAML/新增代码语法，最终由 T8 收口**）。

---

## T8：App · 主页启动链路改造（体检 + 会话 + 重试 + 实时日志）

文件：`dotnet/src/Starcloud.App/Views/HomePage.xaml(.cs)`

1. `HomePage.xaml` 覆盖层加实时日志区（spec §5）：位于 `LaunchTipText` Border(L373–388) 与
   `LaunchErrorText`(L390) 之间：

```xml
<ToggleButton x:Name="LaunchLogToggle" Content="显示日志" FontSize="12"
              HorizontalAlignment="Center" Checked="LaunchLogToggle_Changed"
              Unchecked="LaunchLogToggle_Changed" />
<TextBox x:Name="LaunchLogText" Visibility="Collapsed" Width="440" Height="180"
         FontFamily="Consolas" FontSize="11" IsReadOnly="True" AcceptsReturn="True"
         TextWrapping="NoWrap" VerticalScrollBarVisibility="Auto"
         HorizontalScrollBarVisibility="Auto" />
<HyperlinkButton x:Name="LaunchLogOpenBtn" Content="打开日志文件" FontSize="12"
                 Visibility="Collapsed" HorizontalAlignment="Center"
                 Click="LaunchLogOpen_Click" />
```

2. `HomePage.xaml.cs`：
   - `private Process? _gameProcess` → `private GameSession? _gameProcess`；`_gameProcessSession` 不需要（同一个）。
   - 新增字段：`_crashDialogOpen`、`_retryCount`、`_launchStartedAt`、`_logFlushBuffer`（`List<string>`）+ `_logTimer`
     （100ms `DispatcherTimer`，批量 append 到 `LaunchLogText`，上限 200 行）。
   - `LaunchStage` 枚举加 `EnvChecking`、`Retrying` 两项；`ApplyStage` 加对应 `case`
     （`EnvChecking`：标题「正在启动」/步骤「检查运行环境…」；`Retrying`：标题「正在重试」/
     步骤「启动失败，正在重试…」；进度均为氛围爬升）。
   - `StartLaunchAsync` 改造（spec §3.3）：
     1. `ApplyStage(LaunchStage.EnvChecking)` → `_s.EnvCheck.CheckAsync(dotMc, versionId, options, _s.Settings)`；
        `Worst == Block` → `await EnvCheckDialogs.ShowAsync(...)`（T9 提供；返回是否继续）；
        `Warn` → 步骤行写首条 warning，继续；取消检查点照旧。
     2. 层叠合并 `Resolve` 传入 `MemoryAdvisor.Probe(eff.GameRoot)`（T3 重载）。
     3. `GameSessionGuard.TryAcquire(dotMc, versionId, out var blockedBy)`；
        失败 → `HideOverlay(null)` + `LocalErrorBar`「版本 X 正在运行中」+ return；
        成功 → 存 `_sessionHandle`，`finally` 里 `Dispose`（正常路径由 `Exited` 收尾释放，
        需在 `Exited` 回调里 `_sessionHandle?.Dispose()` 并置 null）。
     4. 拉起：`_gameProcess = await _s.Launcher.LaunchVersionAsync(dotMc, versionId, options);`
        → `_sessionHandle?.Bind(_gameProcess.Pid)` → `_gameProcess.OutputLine += OnGameOutputLine;`
        → `_gameProcess.Exited += OnGameExited;`（`Action<int>`）。
     5. `Exited` 回调重写为独立方法 `OnGameExited(int exitCode)`：
        - `_dq.TryEnqueue` 内执行；
        - `App.RunningVersionId = null`；`_sessionHandle?.Dispose()`；`_sessionHandle = null`；
        - 取消 → `HideOverlay("已取消启动")`；正常退出 → 原有 `ApplyStage(Exited)` 展示本局时长；
        - 非零且非取消：`if (!_cancelRequested && exitCode != 0)` →
          `if (_retryCount == 0 && _s.Settings.LaunchAutoRetry && uptime < 8s)` → `_retryCount++` →
          `ApplyStage(Retrying)` → 重跑「预检 + 拉起」（提取私有方法 `LaunchPreparedAsync`）；
          否则 → `await CrashDialog.ShowAsync(...)`（T9），结果决定「重新启动」还是收层；
        - 收尾命令 + `NovaRuntime.OnGameExitedAsync()` 保留。
     6. `LaunchCancelBtn`/`LaunchBtn` 的 `Kill(true)` → `_gameProcess?.Kill()`。
     7. `WindowTimer_Tick` 的 `Refresh()`/`MainWindowHandle` 走 `GameSession` 兼容成员。
     8. `ShowOverlay` 清空 `LaunchLogText.Text`、`_retryCount = 0`、`_logFlushBuffer.Clear()`，
        并按 `_s.Settings.ShowGameLogOnLaunch` 设 `LaunchLogToggle.IsChecked`。
     9. `OnGameOutputLine(string line)`：`lock` 塞 `_logFlushBuffer`（不直接碰 UI）。
     10. 日志埋点：拉起/退出码/重试/体检结论各一条 `LauncherLog.Write("launch", ...)`。

构建：**本任务后整仓必须 0 警告 0 错误**（T9 的 `CrashDialog`/`EnvCheckDialogs` 需同批创建，
故 T8 与 T9 合并提交为一次构建验证，见 T9 说明）。

---

## T9：App · 崩溃弹窗 + 体检弹窗 + 修复动作

文件：`dotnet/src/Starcloud.App/Views/CrashDialog.cs`（新建）、
`dotnet/src/Starcloud.App/Views/EnvCheckDialogs.cs`（新建）

1. `EnvCheckDialogs.ShowAsync(XamlRoot, EnvironmentReport, Func<EnvIssue, Task<bool>> fixAsync)`
   → `Task<bool>`（true = 继续启动）：
   - `ContentDialog`（标题「运行环境检查」，PrimaryButtonText「仍然启动」，CloseButtonText「取消」）；
   - 内容 `StackPanel`：每个 issue 一行（等级色点 + Title + Detail + `AutoFixable` 时「修复」Button，
     点击 `await fixAsync(issue)` 后把该行标记为已修复/置灰）。
2. `CrashDialog.ShowAsync(XamlRoot, CrashDiagnosis, string versionId, Func<Task> restartAsync,
   Func<Task> askNovaAsync)`：按 spec §7.4 结构；
   - 「一键修复」按钮文案 `diagnosis.FixLabel`，点击执行修复（`SetMemory` 写 `InstanceSettingsStore`；
     `ReinstallVersion` 调 `GameDiagnosticsService.RepairAsync` 带进度；`OpenCrashReport`/`OpenModsFolder` 开目录；
     `ReinstallJavaHint` 跳设置页）；
   - 修复后按钮换成「重新启动」→ `restartAsync()`；
   - 「打开崩溃报告」「问 Nova」次级按钮；
   - 固定 `CloseButtonText = "返回主页"`。
3. 修复动作的 `SetMemory` 实现：`store.Get(versionId)` → `MemoryMode = "manual"`、`MemoryMinMb = 512`、
   `MemoryMaxMb = diagnosis.SuggestMaxMemoryMb` → `store.Set` + `store.Save()`；
   修复前用 `ConfirmAsync` 说明改动内容（涉及落盘，遵循 Nova 的「先给确认清单」惯例）。

构建：**T8 + T9 一起构建，整仓 0 警告 0 错误。**

---

## T10：App · 设置页 + 存档页接入

文件：`dotnet/src/Starcloud.App/Views/SettingsPage.xaml(.cs)`、`SavesPage.xaml.cs`

1. 设置页新增：
   - `ToggleSwitch`「启动失败自动重试一次」（绑定 `LaunchAutoRetry`）；
   - `ToggleSwitch`「启动页默认展开实时日志」（绑定 `ShowGameLogOnLaunch`）；
   - 「内存智能推荐」区块：显示 `MemoryAdvisor.Explain(...)`（物理内存 + 全局 mods 探测），
     按钮「应用为默认最大内存」→ 写 `DefaultMaxMemoryMb` + `Save`；
   - 三者均 `Save()` 落盘。
2. `SavesPage.xaml.cs`：
   - `Process? _gameProcess` → `GameSession?`；
   - `LaunchCopiedAsync` L385–390 的检查改为 `GameSessionGuard.TryAcquire(dotMc, targetId, out var blockedBy)`
     失败即 `ErrorBar`；
   - `LaunchVersionAsync` 结果 `Bind(pid)`；`_gameProcess.Exited += _ => ...`（Action<int> 语义）；
     `rec.ProcessId = _gameProcess.Pid`；
   - `StopGame_Click` L455 `Kill()`；
   - `OnGameExitedAsync` 内释放会话句柄（`Dispose`）。

构建：整仓 0 警告 0 错误。

---

## T11：文档 + 全量验证 + 提交

1. `docs/USER_MANUAL.md` 新增章节（插在「## 10. 版本备份与回档」之后，后续章节重编号）：
   - 「## 11. 启动可靠性」：实时游戏日志 / 崩溃诊断与一键修复 / 启动前环境体检 / 自动重试 / 智能内存 / 防双开。
   - 日志页说明补「启动器日志 / 游戏日志」两种。
2. `docs/API.md`（如存在 `LaunchVersionAsync` 签名条目 L174）同步为 `Task<GameSession>`；
   `docs/ARCHITECTURE.md` L135 的链路描述补一句日志会话。
3. 最终构建 0 警告 0 错误。
4. 一次性提交（单行中文 commit），文件清单：

```
docs/USER_MANUAL.md
docs/API.md
docs/ARCHITECTURE.md
docs/designs/2026-09-11-launch-reliability-design.md
docs/plans/2026-09-11-launch-reliability-plan.md
dotnet/src/Starcloud.Core/Services/LauncherLog.cs
dotnet/src/Starcloud.Core/Services/GameSession.cs
dotnet/src/Starcloud.Core/Services/GameSessionGuard.cs
dotnet/src/Starcloud.Core/Services/MemoryAdvisor.cs
dotnet/src/Starcloud.Core/Services/EnvironmentCheckService.cs
dotnet/src/Starcloud.Core/Services/CrashAnalysisService.cs
dotnet/src/Starcloud.Core/Services/LauncherPaths.cs
dotnet/src/Starcloud.Core/Services/SystemMemory.cs
dotnet/src/Starcloud.Core/Services/InstanceSettings.cs
dotnet/src/Starcloud.Core/Services/GameLauncher.cs
dotnet/src/Starcloud.App/Services/AppServices.cs
dotnet/src/Starcloud.App/Views/HomePage.xaml
dotnet/src/Starcloud.App/Views/HomePage.xaml.cs
dotnet/src/Starcloud.App/Views/CrashDialog.cs
dotnet/src/Starcloud.App/Views/EnvCheckDialogs.cs
dotnet/src/Starcloud.App/Views/LogsPage.xaml
dotnet/src/Starcloud.App/Views/LogsPage.xaml.cs
dotnet/src/Starcloud.App/Views/SettingsPage.xaml
dotnet/src/Starcloud.App/Views/SettingsPage.xaml.cs
dotnet/src/Starcloud.App/Views/SavesPage.xaml.cs
```

## 依赖顺序

```
T1 → T2 → T3 → T4 → T5 → T6 → T7 → T8+T9 → T10 → T11
```

- T3–T6 之间无相互依赖，可并行实现（但都串行构建验证更稳）。
- T2 会让 App 层暂时编译失败，T8/T9/T10 收口；期间 `Starcloud.Core` 必须通过。
