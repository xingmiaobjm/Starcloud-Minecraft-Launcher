# 游戏运行状态面板设计

> 目标：**当前这一局游戏在做什么，一眼看得清**。
> 启动器里"游戏正在跑"目前只有一个进程句柄和一堆散落在各页的 `App.RunningVersionId` 判断，
> 用户想知道"它吃多少内存、JVM 怎么给的参数、日志有没有报错、上一局为什么崩"，
> 得自己开任务管理器、翻日志页、回版本详情页找配置。
> 本设计新增一个独立的「运行状态」页面，把这些信息收在一处，并让它**在启动器重启后依然可信**。

## 1. 需求与边界

### 1.1 用户价值

| 场景 | 现在 | 本设计之后 |
| --- | --- | --- |
| 游戏卡顿，想知道是不是内存不够 | 开任务管理器找 java 进程 | 页面直接给出 CPU / 内存曲线与峰值 |
| 想确认这局到底用了哪些 JVM 参数 | 回版本详情页翻设置 | 页面直接列出生效的 Java、`-Xmx`、窗口、额外参数 |
| 游戏崩了，想马上看尾部日志 | 切到日志页再手动翻 | 页面内嵌实时日志尾，选中即复制 |
| 上一局为什么崩、跑了多久 | 无从得知 | 「最近运行」列表给出时长、峰值内存、退出码 |
| 游戏其实是上一轮启动器拉起的 | 只有重启启动器才知道（锁文件） | 页面识别并标注「上一轮启动器启动」 |
| 想结束这一局 | 回首页 | 页面内「结束进程」，带二次确认 |

### 1.2 明确的非目标

- **不做**任务管理器替代品：不枚举全机器进程，只盯 Minecraft 这一局及其子进程树。
- **不做**历史趋势分析：运行历史只保留最近 50 条，不做跨日聚合、不做报表。
- **不做**远程监控：不把状态上报到任何服务端。
- **不改** `starcloud/game-session.json` 的结构与语义——它被 8 处消费（HomePage / SavesPage /
  VersionsPage / VersionDetailPage / ModesPackDialogs / RecordingService / UpdateStartup /
  GameSessionGuard），加字段的风险远大于收益。启动上下文另起一个文件。
- **不引入**第三方图表库：sparkline 用 `Polyline` 自绘。

### 1.3 已确认的产品决策

| 决策项 | 结论 |
| --- | --- |
| 面板形态 | 独立「运行状态」页面（不是抽屉 / 首页卡片 / 悬浮窗） |
| 导航位置 | **游戏组末尾**，即「联机」之后、分隔线之前 |
| 信息范围 | 进程与实例基础 + 性能与资源占用 + 启动配置与 JVM + 内容与实时日志 |
| 数据来源 | 跨启动器重启识别 + 运行历史 |
| 交互能力 | 基础操作 + 结束进程 + 诊断与重启 + 性能图表与采样设置 |
| 主区布局 | **单列分区 + 历史置底** |
| 技术方案 | Core 独立监控器（页面纯订阅，不含业务逻辑） |

## 2. 架构总览

```
Starcloud.Core
├── Services/GameRuntimeMonitor.cs      ← 新增：运行态唯一来源（1s 心跳）
├── Services/ProcessSampler.cs          ← 新增：纯采样（CPU / 内存 / 线程 / GPU）
├── Services/GameRunHistoryStore.cs     ← 新增：运行历史落盘（上限 50 条）
├── Services/GameLaunchContextStore.cs  ← 新增：启动上下文落盘（仅本页面消费）
├── Services/GameSessionGuard.cs        ← 复用，不改（跨进程判活）
├── Services/GameSession.cs             ← 复用，不改（日志尾 / Kill / Exited）
└── Services/NovaFpsWatcher.cs          ← 复用其中的 internal PdhNative（GPU 计数器）

Starcloud.App
├── Views/RunningStatusPage.xaml(.cs)   ← 新增：页面（纯订阅渲染）
├── Services/AppServices.cs             ← 注册 Runtime 单例
├── MainWindow.xaml(.cs)                ← 导航项 + tag→Type 映射
├── Views/HomePage.xaml.cs              ← 启动成功后 Attach
├── Views/SavesPage.xaml.cs             ← 启动成功后 Attach
└── Nova/Tools/NovaToolsWrite.Versions.cs ← 启动成功后 Attach
```

设计原则：**Monitor 不知道 UI 的存在**（用事件 + 不可变快照，无 `DispatcherQueue` 引用）；
页面不自己起定时器，只订阅 Monitor 的心跳事件。

## 3. 数据模型

```csharp
/// 当前这一局的即时快照（不可变；采样更新时整体替换）
public sealed record GameRunInfo(
    int Pid,
    string VersionId,
    DateTime StartedAt,
    TimeSpan Uptime,
    bool RecoveredFromLock,        // true = 跨启动器重启识别到（本进程没 Attach 过）
    GameRunSample Latest,
    DiagReport? Diagnostics);      // 后台诊断结果，未完成时为 null

/// 一次采样的读数
public readonly record struct GameRunSample(
    DateTime At,
    double CpuPercent,
    long WorkingSetBytes,
    long PeakWorkingSetBytes,
    int ThreadCount,
    double? GpuPercent);           // null = 计数器不可用（不写 0，避免与"真的 0%"混淆）

/// 一局结束后的落盘记录（运行历史的一条）
public sealed record GameRunRecord(
    string VersionId,
    DateTime StartedAt,
    DateTime EndedAt,
    TimeSpan Duration,
    int? ExitCode,                 // null = 进程已被外部结束，拿不到退出码
    long PeakWorkingSetBytes,
    bool Crashed);

/// 启动上下文（写 <dotMc>/starcloud/game-launch.json，退出即删）
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

采样缓冲在 Monitor 内部：环形缓冲 **600 点（≈10 分钟）**，只在内存里，不落盘。

**启动命令行只在内存里**：`BuildCommandLineAsync` 的产物包含 `--accessToken`，
所以它由调用方在 `Attach` 时一并交给 Monitor 内存持有，**绝不写进任何落盘文件**（`GameLaunchContext` 里没有这个字段）。
跨启动器重启识别到的会话拿不到命令行，此时「复制命令」按钮禁用并提示原因。

## 4. 数据流

```
       ┌─ HomePage / SavesPage / NovaTools ─┐
       │  启动成功                            │
       └──────────┬───────────────────────────┘
                  │ monitor.Attach(session, launchContext, commandLine)
                  ▼
        GameRuntimeMonitor ── 1s 心跳 ──> ProcessSampler.Sample(pid)
                  │                            │
                  │  <── GameRunSample ────────┘
                  ├── 追加环形缓冲（600 点）
                  ├── 写 game-launch.json（Attach 时一次）
                  ├── 事件 GameRunChanged(GameRunInfo)
                  │
                  └─ session.Exited ──> 落 GameRunRecord
                                        └──> GameRunHistoryStore.Append
                                             （退出时删 game-launch.json）

       冷启动（启动器刚开，什么都没启动）
                  │
                  ▼
        GameSessionGuard.Current(dotMc)  ── IsAliveJavaProcess(pid) ──┐
                  │ 活着                        │ 死了                │
                  ▼                             ▼                     │
        RecoveredFromLock = true          忽略，显示空态              │
        + 读 game-launch.json 补上下文                                 │
        （缺失则退化为读当前版本配置 + 说明芯片）                       │
```

**为什么恢复路径要读 `GameSessionGuard` 而不是 `App.RunningVersionId`**：
后者是静态字段，启动器一重启就没了；锁文件是跨进程的唯一真相（`GameSessionGuard` 头注释已写明）。

**`Uptime` 的口径**：`Attach` 路径用 `session.StartedAt`（≈真实启动时刻）；
恢复路径用锁文件里的 `StartedAt`（`GameSessionGuard.Bind` 在拿到 pid 时会重写为当时时间，误差可接受），
并在界面上标「约」字说明这是估算值。

## 5. 页面设计

### 5.1 版式

遵循 `DESIGN.md`：根容器 `MaxWidth="1280"` 居中铺满；页头 = 28 号标题 + 描述行。

```
┌ 运行状态 ────────────────────────────────────────────────┐
│ 当前这一局游戏发生了什么                                   │
├──────────────────────────────────────────────────────────┤
│ ① Hero                                            [中性]  │
│   [● 运行中]  PID 18342 · 已运行 00:42:17                 │
│   1.20.1-Fabric 0.15.2 · 隔离实例 · 内存 4096 MB          │
│                    [打开日志] [诊断] [复制命令] [结束进程] │
├──────────────────────────────────────────────────────────┤
│ ② 性能三卡（各一张大数字 + 40–56px sparkline）             │
│   ┌ CPU ┐    ┌ 内存 ┐    ┌ GPU ┐                          │
│   │ 62% │    │3.1GB │    │ 41% │                          │
│   │ 〰〰 │    │ 〰〰  │    │ 〰〰 │                          │
├──────────────────────────────────────────────────────────┤
│ ③ 双栏                                                    │
│   ┌ 启动配置与 JVM ──────┐ ┌ 内容与实时日志 ───────────┐ │
│   │ Java / -Xmx / 窗口   │ │ Mod 142 · 着色器 2 · 包 3 │ │
│   │ 额外参数（可复制）   │ │ ┌ 日志尾（等宽、可选中）┐ │ │
│   └──────────────────────┘ └───────────────────────────┘ │
├──────────────────────────────────────────────────────────┤
│ ④ 最近运行（最多 5 行，可展开到 50）                       │
│   1.20.1-Fabric  1h 12m  峰值 4.2 GB        正常退出      │
└──────────────────────────────────────────────────────────┘
```

### 5.2 视觉规则（对齐 `DESIGN.md`）

- **颜色只落在有意义的地方**：整页卡片一律中性（`GlassCardBrush` + `CardStrokeColorDefaultBrush`）；
  accent 仅用于 Hero 的「运行中」焦点芯片（accent 面 **黑字 `#E61A1A1A` 加粗**）与 sparkline 折线。
  紫色继续只归 Nova。
- **字阶**：Hero 的「运行中」是唯一强调芯片；性能卡大数字用 `ScMetaValue` 之上的单一大号数字；
  PID、`-Xmx`、日志时间戳一律等宽（Cascadia Code）。
- **结构语言**：分区标题用 `ScSectionTitle` + 3px `ScSectionSpine` 强调脊柱；
  辅助元信息用 `ScMetaLabel(11px)` / `ScMetaValue(12px)`；每张卡用 `ScCard`。
- **日志区**：`ScrollViewer` + `TextBlock`（Cascadia Code 12、`TextWrapping="NoWrap"`、
  `IsTextSelectionEnabled="True"`），自动吸底；用户手动上滚后暂停吸底并显示「回到底部」。
- **动效**：全部走 `MotionService.Enabled` 与 `MotionFx` 曲线族；关闭时瞬时到位。
  导航项的运行中标记是**静态圆点，不做呼吸动画**（常驻动画耗电，且违反"每个界面都要流畅"的初衷）。
- **危险操作**：「结束进程」走 `ContentDialog` 二次确认（`DESIGN.md` 硬要求）。

### 5.3 空态（没有游戏在跑）

复用 `EmptyStateView`（`Title` / `Subtitle` / `ActionText`），文案：「现在没有游戏在运行」
+ 「去首页启动一个版本，或看看下面的最近运行记录」，`Glyph` 用图标库中的空队列码位。
下方照常显示「最近运行」；历史也为空时，`EmptyStateView` 换文案「还没有运行记录」。

## 6. 交互细节

| 操作 | 行为 |
| --- | --- |
| 打开日志 | `Frame.Navigate(typeof(LogsPage), "game")` 跳到日志页并切到「游戏日志」来源（LogsPage 加 `OnNavigatedTo` 消费该参数，为 null 时保持现状） |
| 诊断 | 调 `GameDiagnosticsService.DiagnoseAsync(dotMc)`，结果以 InfoBar / 内联列表呈现，可「一键修复」 |
| 复制命令 | 把内存中持有的完整启动命令行复制到剪贴板；恢复路径下按钮禁用并说明原因 |
| 结束进程 | `ContentDialog` 二次确认 → `session.Kill(entireTree: true)`；被拒时 InfoBar 提示 |
| 重启 | 结束当前进程 → 写 `App.PendingLaunchVersionId` → 导航回首页（复用既有跨页启动通道） |
| 采样设置 | 页头右侧一个轻量入口：采样间隔（1s / 2s / 5s）与是否采 GPU，只存内存、随页销毁 |

「重启」的实现刻意走 `App.PendingLaunchVersionId`：`HomePage.OnNavigatedTo`（L88-89）已有消费逻辑，
不新增第二条启动通道。

## 7. 错误处理与降级

| 情况 | 行为 |
| --- | --- |
| GPU 计数器不可用 / 被占用 / 无权限 | 该卡显示「计数器不可用」，其余指标正常；不抛错、不打错误日志 |
| 连续 3 次采样失败（进程疑似僵死） | 暂停采样、页面保留最后已知值并标注「采样已暂停」，给出「重试」 |
| `game.history.json` 损坏 | `TryLoad` 读失败 → 按空列表返回（读可以兜底）；写路径若读失败则**拒绝写入**并记日志，避免"用户攒的历史被空库覆盖" |
| `game-launch.json` 缺失 / 损坏 | 降级为读当前版本 `InstanceSettingsStore` 配置，并加一枚「启动参数按当前版本配置显示」说明芯片 |
| `game-launch.json` 是上一局的残留（版本 id 与当前不符） | 忽略并删除，不显示为当前上下文 |
| 结束进程被系统拒绝 | InfoBar 提示原因（权限 / 进程已退出），不抛异常 |
| 锁文件指向的 pid 已变成别的 java 进程 | 复用 `GameSessionGuard.IsAliveJavaProcess` 的判定口径（进程名以 `java` 开头），不做 pid 复用深度校验 |

**可测试性要求**：`GameSessionGuard.IsAliveJavaProcess` 是 `private static`，Monitor 不能直接依赖它。
Monitor 构造注入 `Func<int, bool>? isAliveProbe = null`，默认实现走 `Process.GetProcessById` + 进程名前缀，
测试时注入假探针，从而**不依赖机器上真的跑着 java**。

## 8. 测试计划（`Starcloud.Core.Tests`）

| 用例 | 断言 |
| --- | --- |
| 历史存储：上限裁剪 | 追加 60 条 → 只留 50，且保留最新 50 |
| 历史存储：原子写 | 写到一半中断（写坏 tmp）→ 原文件内容不变 |
| 历史存储：损坏容错 | 文件写入 `{ 不是 json` → `TryLoad` 返回空列表、不抛 |
| 历史存储：读失败时拒绝写 | 索引损坏时 `Append` 返回失败且**文件未被清空** |
| `ProcessSampler`：自身进程 | 对 `Environment.ProcessId` 采样：CPU% ≥ 0、WorkingSet > 0、ThreadCount > 0 |
| `ProcessSampler`：GPU 不可用 | 注入不可用计数器 → `GpuPercent == null`（不是 0） |
| `ProcessSampler`：进程退出后 | 采样返回 false，不抛 `InvalidOperationException` |
| `GameRuntimeMonitor`：恢复路径 | 预置锁文件 + 注入"存活"探针 → `RecoveredFromLock == true`、`VersionId` 正确 |
| `GameRuntimeMonitor`：恢复路径（进程已死） | 注入"已死"探针 → 无当前运行，锁文件被清理 |
| `GameRuntimeMonitor`：Attach → Exited 落历史 | 触发 `Exited` → 历史新增一条，时长与退出码正确 |
| `GameRuntimeMonitor`：环形缓冲上限 | 推 700 次采样 → 缓冲长度 600，首尾顺序正确 |
| `GameLaunchContextStore`：往返 | 写读一致；损坏返回 null；版本不符返回 null |
| JVM 参数解析 | 含引号、含 `=`、含空格的参数正确切分（与启动器实际拼接口径一致） |
| 敏感信息不落盘 | `Attach` 时传入含 `--accessToken` 的命令行 → 落盘目录下**任何**文件都不含该 token 字符串 |
| 锁扩展兼容 | 用既有 `GameSessionGuard.TryAcquire/Bind/Dispose` 全流程后，Monitor 能正确识别（证明未破坏既有语义） |

测试不覆盖 UI 层；UI 用 UIA 脚本实机复验。

## 9. 验收标准

1. `dotnet build .\Starcloud.slnx -v m`：0 错 0 警。
2. `dotnet test .\tests\Starcloud.Core.Tests`：全绿（含上表新增用例）。
3. UIA 实机复验：导航栏「运行状态」项存在且位置在「联机」之后；无游戏时显示空态；
   启动一局游戏后页面出现 Hero「运行中」、三张性能卡数字 1s 跳动、日志尾滚动。
4. 跨启动器重启：启动游戏 → 完全退出启动器 → 重新打开 → 页面仍显示「运行中」并标注「上一轮启动器启动」。
5. 深色 / 浅色两套主题下均无对比度问题；`MotionService` 关闭时无残留动画。
6. 全程无第三方依赖引入（图表自绘、PDH 复用同程序集实现）。
