# 启动页覆盖层（Launch Overlay）设计

日期：2026-09-09
状态：设计已确认（逐段确认完成），待写实现计划

## 1. 背景与问题

当前点击「启动游戏」后，主页只有右栏一行 `StatusText` 文字反馈
（"正在验证 Microsoft 凭据…" → "正在检查游戏文件完整性…" → "补全缺失文件…" →
"游戏运行中"）。大型整合包补全文件可长达数分钟，一行小字既不显眼也无数值反馈，
与 PCL2 的整页启动视图体验差距大。

PCL2 的启动页：整页替换主内容，居中显示镐子图标 + 「已启动游戏」+ 版本号 +
阶段进度条 + 「当前步骤 / 登录方式」键值行 + 「你知道吗」小贴士 + 取消按钮，
游戏窗口出现后停留在启动页。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| 呈现方式 | **整页覆盖式**：点启动后主页内容区整体切换为启动视图，游戏退出/取消后切回主页 |
| 游戏窗口出现后 | **留在启动页**，显示运行中状态，用户点「返回主页」手动切回 |
| 小贴士来源 | **内置静态池为主**；已登录 Starcloud ID 时 Nova 偶尔生成动态贴士（克制版，见 §3.5） |
| 视觉风格 | **品牌主题色**：浅黄 #FFD322 / 深绿 #3DD322，随深浅色主题适配 |
| 实现架构 | **方案 A：HomePage 内覆盖层**（两层 Grid 切换，不用 Frame 导航/Popup） |

## 3. 详细设计

### 3.1 布局结构（HomePage.xaml）

HomePage 根节点改为两层 Grid：

```
Grid (根)
├─ 现有两列主页内容（Grid x:Name="HomeContent"，原样保留）
└─ Grid x:Name="LaunchOverlay" (Visibility="Collapsed"，不透明背景铺满)
     └─ StackPanel（最大宽 480，水平垂直居中，间距 14）
          ├─ 镐子图标 Path（48×48，像素风，主题色渐变填充）
          ├─ TextBlock x:Name="LaunchStageTitle"（24 SemiBold，状态标题）
          ├─ TextBlock x:Name="LaunchVersionText"（13 次要色，版本名）
          ├─ ProgressBar x:Name="LaunchProgress"（高 4，圆角，品牌色）
          ├─ 键值两行 Grid：
          │    「当前步骤」 LaunchStageStepText
          │    「登录方式」 LaunchLoginModeText（正版登录 / 离线登录）
          ├─ 小贴士 Border（圆角 8，卡片底色，左侧 4px 品牌色竖条）
          │    └─ TextBlock x:Name="LaunchTipText"（12，可换行）
          └─ 按钮行：
               Button x:Name="LaunchCancelBtn"（取消——启动中显示）
               Button x:Name="LaunchBackBtn"（返回主页——运行中/已退出/失败显示）
```

- 覆盖层盖住主页两列内容区，**NavigationView 侧栏保持可见**（WinUI 习惯，
  用户仍可导航；离开 HomePage 即视为放弃本次启动视图，见 §3.4）
- 覆盖层背景用页面背景色（不透明白/深灰），不露底下主页内容
- 错误态：标题「启动失败」红色（SystemFillColorCritical），错误详情小字最多两行 +
  「返回主页」按钮

### 3.2 启动状态机（HomePage.xaml.cs）

```csharp
private enum LaunchStage
{
    Idle, VerifyingCredentials, CheckingFiles, RepairingFiles,
    StartingProcess, WaitingWindow, Running, Exited, Failed
}
```

```
Idle → VerifyingCredentials → CheckingFiles → RepairingFiles
     → StartingProcess → WaitingWindow → Running → Exited
任意阶段异常 → Failed
任意阶段可取消 → Idle（覆盖层收起）
```

阶段 → UI 映射（集中在一个 `ApplyStage(LaunchStage)` 方法，统一驱动标题 /
步骤文字 / 进度条 / 按钮，避免散落赋值）：

| 阶段 | 标题 | 当前步骤 | 进度条 |
|---|---|---|---|
| VerifyingCredentials | 正在启动 | 验证 Microsoft 凭据… | 0→10% 缓慢爬升 |
| CheckingFiles | 正在启动 | 检查游戏文件完整性… | 10→25% 缓慢爬升 |
| RepairingFiles | 正在启动 | 补全缺失文件 x/y（p%） | 25→75%（**真进度**：BatchProgress.Percentage 线性映射） |
| StartingProcess | 正在启动 | 构建启动命令… | 75→85% |
| WaitingWindow | 已启动游戏 | 等待游戏窗口出现… | 85→100% 爬升 + 不确定态闪烁 |
| Running | 已启动游戏 | 游戏运行中 | 100% 定格 |
| Exited | 游戏已退出 | 本局时长 x 分 x 秒 | 清空 |
| Failed | 启动失败 | 错误信息（≤2 行） | 清空 |

- 爬升进度：`DispatcherTimer`（250ms tick），阶段区间内每 tick +0.5%，
  到达区间上限即停（PCL2 同款"氛围进度"，不承诺精确）
- RepairingFiles 是唯一真进度：现有 `Progress<BatchProgress>` 回调直接映射到
  25-75% 区间
- 「登录方式」在启动开始时一次性赋值（正版登录 / 离线登录），之后不变

### 3.3 窗口出现检测（WaitingWindow → Running）

- `DispatcherTimer` 每 500ms 轮询：`_gameProcess.Refresh()` 后检查
  `MainWindowHandle != IntPtr.Zero`
- 检测到窗口 → `ApplyStage(Running)`
- **120 秒超时兜底**：超时未检测到窗口直接转 Running（游戏可能用独立窗口或
  检测失效，不阻塞用户）——进度条停 100%，标题照常「已启动游戏」
- 轮询只在 WaitingWindow 阶段运行，进 Running/Exited/Failed 即停
- 与 Nova 浮窗的 pid 监测（NovaFpsWatcher）互不影响，各管各的

### 3.4 生命周期与取消

**进入覆盖层**：`LaunchBtn_Click` 通过前置校验（版本已选、玩家名合法）后立即
显示覆盖层，后续所有阶段反馈都在启动页上。

**取消逻辑**（「取消」按钮，复用现有能力）：
- 已拉起进程（WaitingWindow 及之后）：置 `_cancelRequested` 标记 +
  `_gameProcess.Kill(true)`——与现有 LaunchBtn 第二击同一逻辑；Exited 回调检测到
  取消标记 → **直接收起覆盖层**回主页 + 右栏 StatusText「已取消启动」（不显示
  「游戏已退出」覆盖层，一次点击完成取消）
- 未拉起进程（凭据/预检/补全阶段）：置 `_cancelRequested` 标记 +
  按钮「正在取消…」禁用；补全循环不中断，在当前 await 完成后的阶段边界检查
  标记退出流程；凭据验证/预检这类短阶段完成后检查即可
- 两种路径殊途同归：覆盖层收起，状态机回 Idle

**返回主页按钮**（Running / Exited / Failed 态显示）：
- 只收覆盖层（`LaunchOverlay.Visibility = Collapsed` + 状态机回 Idle），
  不杀进程
- 若游戏仍在运行：右栏 StatusText 显示「游戏运行中」，LaunchBtn 文案复用现有
  第二击「停止游戏」行为
- Exited 事件在覆盖层收起后触发时：走现有 Exited 回调（右栏 StatusText
  「游戏已退出」+ Nova 收尾），不重新展开覆盖层

**离开页面**：用户在覆盖层显示期间点侧栏导航离开 HomePage → OnNavigatedFrom
触发与「取消（未拉起）」相同的取消语义（已拉起则不杀游戏，仅放弃视图，
Exited 回调安全落到已销毁页面前需判空——复用 `_dq.TryEnqueue` 现有模式）。

**错误**：`LaunchBtn_Click` 的 catch 不再弹 LocalErrorBar（启动期间的异常），
改为 `ApplyStage(Failed)` + 错误信息写入启动页。预检「版本 JSON 损坏无法恢复」、
补全失败弹窗这两个既有分支的 UI 保持现状（ContentDialog 打断在覆盖层之上），
选「取消」后覆盖层收起。

### 3.5 小贴士

**静态池**：新文件 `dotnet/src/Starcloud.App/Views/LaunchTips.cs`，
`static class LaunchTips`，`string[] All` 约 24 条，内容三类：
- 启动器功能提示：右键版本可「问 Nova」/删版本；Nova 页可开游戏内浮窗；
  版本设置可调内存等
- MC 游玩小知识：床会记重生点但会炸下界；铁傀儡怕水不成;
- 常见问题预防：内存并非越大越流畅；Java 版本由整合包自动匹配等

**轮换策略**：进入覆盖层时从洗牌队列取一条（窗口存活期内不重复），
队列空了重新洗牌。

**Nova 动态贴士（克制版）**：
- 触发条件：已登录 Starcloud ID 且 Nova AI 可用 → 进入覆盖层时 30% 概率
- 行为：先显示静态贴士；后台请求 Nova 生成一条针对当前版本/整合包的提示，
  8 秒超时；结果**缓存进队列尾部，下一次启动才显示**（不中途替换干扰阅读）
- 失败/超时静默丢弃，无任何 UI 提示

### 3.6 视觉

- 强调色（图标渐变、进度条 Foreground、贴士竖条）：沿用现有品牌色资源
  （浅黄 #FFD322 浅色主题 / 深绿 #3DD322 深色主题）；若 App 资源里尚无对应
  主题画刷资源则在 App.xaml 补 `BrandAccentBrush`（Light/Dark 两套）
- 镐子图标：手绘像素风 Path（Geometry），48×48，主题色线性渐变；不用位图资源
- 覆盖层进入/退出不做复杂动画：淡入 150ms 即可（克制）

## 4. 改动范围

| 文件 | 改动 |
|---|---|
| `dotnet/src/Starcloud.App/Views/HomePage.xaml` | 根节点两层 Grid + LaunchOverlay 覆盖层 XAML |
| `dotnet/src/Starcloud.App/Views/HomePage.xaml.cs` | LaunchStage 状态机 / ApplyStage / 爬升定时器 / 窗口轮询 / 取消标记 / 小贴士接入；LaunchBtn_Click 改为驱动状态机 |
| `dotnet/src/Starcloud.App/Views/LaunchTips.cs` | 新文件：静态贴士池 |
| `dotnet/src/Starcloud.App/App.xaml` | （若缺）补 BrandAccentBrush 主题资源 |

不改：GameLauncher / LaunchPreflightService / NovaRuntime / StatusText 现有文案。

## 5. 范围外（明确不做）

- 启动器窗口自动最小化 / 隐藏
- 启动页显示实时游戏日志
- 多实例并行启动的启动页状态
- 覆盖层复杂入场动画
