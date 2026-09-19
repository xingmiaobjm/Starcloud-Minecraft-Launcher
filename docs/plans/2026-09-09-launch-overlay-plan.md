# 启动页覆盖层（Launch Overlay）实现计划

日期：2026-09-09
设计文档：docs/designs/2026-09-09-launch-overlay-design.md（commit 09ba716）

构建命令：先 `Stop-Process -Name Starcloud.App -Force -ErrorAction SilentlyContinue`，
再 `dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo`
（每个任务完成后构建验证，目标 0 警告 0 错误）

前置确认（已核实）：
- 品牌色刷已存在：`AccentFillColorDefaultBrush`（浅色 #FFD322 / 深色 #3DD322），
  App.xaml **无需改动**（spec §4 表中 App.xaml 行作废）
- Nova 可用性判断：`_s.StarcloudId.Current is not null`
- Nova 生成接口：`_s.Nova.ChatAsync(message, _s.Settings, null, ct)` → `NovaReply`

## T1：LaunchTips 静态贴士池

新文件：`dotnet/src/Starcloud.App/Views/LaunchTips.cs`

```csharp
internal static class LaunchTips
{
    public static readonly string[] All = [ /* 约 24 条 */ ];
    /// <summary>洗牌队列取一条：窗口存活期内不重复，取空重洗</summary>
    public static string Next();
    /// <summary>Nova 生成的动态贴士入队尾（下一次启动显示）</summary>
    public static void Enqueue(string tip);
}
```

1. `All` 内容三类（spec 3.5）：启动器功能提示 / MC 游玩小知识 / 常见问题预防，
   全部简体中文，单条 ≤ 40 字
2. 内部维护 `Queue<string>`（初始化为 All 洗牌）；`Next()` 出队，空则重洗
3. `Enqueue` 直接入队尾
4. 验证：构建通过

## T2：HomePage.xaml 覆盖层

文件：`dotnet/src/Starcloud.App/Views/HomePage.xaml`

1. 根节点改为两层：外层 `<Grid>`（不再带 MaxWidth）→ 内层现有两列 Grid 加
   `x:Name="HomeContent"`（原样保留）→ 兄弟节点 `Grid x:Name="LaunchOverlay"`
   （`Visibility="Collapsed"`，`Background="{ThemeResource ApplicationPageBackgroundThemeBrush}"`
   不透明铺满，盖住两列内容，NavigationView 侧栏不受影响）
2. LaunchOverlay 内容（居中 StackPanel，MaxWidth 480，Spacing 14）：
   - 镐子图标：`Path` 48×48，像素风剪影，`Fill` 用
     `LinearGradientBrush`（#FFD322 → #3DD322 对角渐变，两主题通用）；
     起点像素 Data（实现时可微调，验收 = 48 网格对齐的镐剪影、无位图依赖）：

   ```
   M16,4 H32 V8 H36 V12 H40 V20 H36 V24 H32 V20 H28 V16 H24 V40 H20 V44 H16 V16 H12 V24 H8 V20 H12 V12 H16 Z
   ```

   - `TextBlock x:Name="LaunchStageTitle"` FontSize 24 SemiBold
   - `TextBlock x:Name="LaunchVersionText"` FontSize 13 次要色 TextTrimming
   - `ProgressBar x:Name="LaunchProgress"` Height 4，CornerRadius 2，
     `Foreground="{ThemeResource AccentFillColorDefaultBrush}"`
   - 键值两行 Grid（Auto/* 列，标签次要色 13）：`LaunchStageStepText` /
     `LaunchLoginModeText`
   - 小贴士 Border：CornerRadius 8 卡片底，内 Grid 左列 4px 竖条
     （`AccentFillColorDefaultBrush`）+ `LaunchTipText`（FontSize 12，Wrap）
   - `LaunchErrorText`（FontSize 12，`SystemFillColorCriticalBrush`，Wrap，
     默认 Collapsed，Failed 态显示）
   - 按钮行：`LaunchCancelBtn`（取消，启动中可见）/ `LaunchBackBtn`
     （返回主页，Running/Exited/Failed 可见），各占一列 Stretch
3. 验证：构建通过

## T3：状态机骨架 + 覆盖层生命周期

文件：`dotnet/src/Starcloud.App/Views/HomePage.xaml.cs`

1. `private enum LaunchStage { Idle, VerifyingCredentials, CheckingFiles,
   RepairingFiles, StartingProcess, WaitingWindow, Running, Exited, Failed }`
2. 字段：`_stage`、`_cancelRequested`、`_runningStart`（Environment.TickCount64）、
   `_stageTimer`（DispatcherTimer 250ms 爬升）、`_windowTimer`（DispatcherTimer
   500ms 轮询）、`_windowDeadline`
3. `ApplyStage(LaunchStage stage)` 集中驱动（spec 3.2 表）：
   - 标题 / 步骤文字 / 进度条（值、IsIndeterminate）/ 两个按钮可见性 /
     LaunchErrorText 可见性
   - VerifyingCredentials→StartingProcess：停旧爬升、按区间重启 `_stageTimer`
     （每 tick +0.5%，到区间上限停）；RepairingFiles 由真进度驱动不爬升
   - WaitingWindow：进度 85 + IsIndeterminate=true，启动 `_windowTimer`，
     `_windowDeadline = TickCount64 + 120_000`
   - Running：停双定时器、进度 100 定格；Exited：停双定时器、进度清零、
     步骤文字 = 本局时长（`_runningStart` 差值）；Failed：停双定时器
4. `ShowOverlay(versionId, isMicrosoft)`：赋版本号/登录方式、取 `LaunchTips.Next()`
   显示、`Visibility=Visible` + 150ms 淡入（Storyboard Opacity 0→1）、
   `ApplyStage(VerifyingCredentials)`
5. `HideOverlay(statusText)`：收覆盖层（含清 `_cancelRequested`）、
   `ApplyStage(Idle)`、StatusText 可选赋值
6. 按钮事件：
   - `LaunchCancelBtn_Click`：`_cancelRequested = true`；已拉起进程 →
     按钮「正在取消…」禁用 + `_gameProcess.Kill(true)`（Exited 回调统一收尾）；
     未拉起 → 同上按钮态，等阶段边界检查（T4）
   - `LaunchBackBtn_Click`：游戏运行中 → StatusText「游戏运行中」+
     LaunchTitleText 改「停止游戏」（复用现有第二击杀进程路径）；否则仅收覆盖层
7. `OnNavigatedFrom`（新增 override）：覆盖层可见时按「取消」语义处理
   （未拉起置标记；已拉起不杀游戏只放弃视图；`_windowTimer`/`_stageTimer` 停掉）
8. 验证：构建通过

## T4：LaunchBtn_Click 改造为状态机驱动

文件：`dotnet/src/Starcloud.App/Views/HomePage.xaml.cs`

1. 前置校验（版本已选、玩家名合法）通过后：`ShowOverlay(versionId, _mode == "microsoft")`，
   后续 `StatusText.Text = ...; StatusText.Visibility = ...` 全部替换为
   `ApplyStage(...)` + `LaunchStageStepText` 辅助更新（保留 StatusText 供覆盖层外使用）
2. 阶段映射：凭据验证前 `VerifyingCredentials` → 预检前 `CheckingFiles` →
   需要补全时 `RepairingFiles`（`Progress<BatchProgress>` 回调改为
   真进度映射 25→75：`LaunchProgress.Value = 25 + p.Percentage * 0.5`，
   步骤文字同现有格式）→ `LaunchVersionAsync` 前 `StartingProcess`
3. 阶段边界检查取消：凭据验证返回后、预检返回后、补全 catch/正常返回后、
   `LaunchVersionAsync` 返回前——`if (_cancelRequested) return;`（finally 收尾）
4. 进程拉起成功：`_runningStart = TickCount64`、`ApplyStage(WaitingWindow)`；
   `_windowTimer` tick：`_gameProcess.Refresh()` → `MainWindowHandle != IntPtr.Zero`
   → `ApplyStage(Running)`；超 `_windowDeadline` → 也转 Running（兜底）
5. Exited 回调改造：`_cancelRequested` 为真 → `HideOverlay("已取消启动")`（不显示
   Exited 态）；覆盖层可见 → `ApplyStage(Exited)`；已收起 → 现有逻辑
   （StatusText「游戏已退出」）；三种路径都保留 `UpdateVersionMeta()` +
   `_s.NovaRuntime.OnGameExitedAsync()`
6. catch：启动流程异常 → 覆盖层可见时 `ApplyStage(Failed)` +
   `LaunchErrorText.Text = ex.Message`（不再弹 LocalErrorBar）；覆盖层不可见
   （理论不发生）→ 保留 LocalErrorBar 兜底。finally：LaunchBtn 恢复 +
   `ApplyStage(Idle)` 不做（避免覆盖 Failed/Exited 态）——仅当覆盖层不可见时复位
7. 补全失败 ContentDialog、版本 JSON 损坏两个既有分支：选「取消」/ return 前
   调 `HideOverlay(null)` 收覆盖层
8. 验证：构建通过

## T5：小贴士接入 + Nova 动态贴士

文件：`dotnet/src/Starcloud.App/Views/HomePage.xaml.cs`（T3 的 ShowOverlay 内）

1. 显示贴士 = `LaunchTips.Next()`（T3 已接）
2. Nova 动态（ShowOverlay 内 fire-and-forget）：
   - 条件：`_s.StarcloudId.Current is not null` 且 `Random.Shared.Next(10) < 3`
   - `CancellationTokenSource(8_000)`，`await _s.Nova.ChatAsync(
     $"用一句话（≤40字）生成一条面向「{versionId}」玩家的启动小贴士，不要解释。",
     _s.Settings, null, cts.Token)` → `LaunchTips.Enqueue(reply.Text)`
   - 整体 try-catch 静默；不碰 UI（下一次启动才显示，spec 3.5）
3. 验证：构建通过

## T6：整体验证

1. 构建：0 警告 0 错误
2. UIA 冒烟（PowerShell + System.Windows.Automation，脚本 UTF-8-BOM）：
   - 启动应用 → 主页定位 → 选中版本点「启动游戏」→ LaunchOverlay 出现 →
     标题文本流转（正在启动 → 已启动游戏）→ 进度条存在 → 游戏窗口出现后
     标题「已启动游戏」+「返回主页」按钮出现 → 点返回主页 → 覆盖层收起、
     StatusText「游戏运行中」
   - （可选）启动中点「取消」→ 覆盖层收起 + StatusText「已取消启动」
3. 手工验证清单（留给用户）：
   - 大整合包启动：补全阶段进度条按真实百分比走，贴士框每启动换一条
   - 游戏正常退出：覆盖层显示「游戏已退出」+ 本局时长
   - 登录 Starcloud ID 后多次启动：偶尔下一次启动出现 Nova 生成的贴士
   - 覆盖层显示期间点侧栏切页再回来：无残留覆盖层、无假死定时器
