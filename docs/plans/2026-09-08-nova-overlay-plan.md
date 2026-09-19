# Nova 游戏内浮窗（在场阶段）实现计划

> 日期：2026-09-08
> 设计规格：`docs/designs/2026-09-08-nova-overlay-design.md`（已批准）
> 技术栈：.NET 8 / WinUI 3 / Windows App SDK 1.8（未打包 `WindowsPackageType=None`）
> 构建命令：`dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo`（0 警告 0 错误）

---

## 已核对的现有接缝

| 接缝 | 位置 | 现状 |
|---|---|---|
| 游戏进程句柄 | `HomePage.xaml.cs` 259 行 `_gameProcess = await _s.Launcher.LaunchVersionAsync(...)` | 返回 `Process`，pid 可直接取 |
| 游戏生命周期通知 | `NovaRuntime.OnGameLaunched(versionId)` / `OnGameExitedAsync()` | 签名扩展点 |
| 时刻事件流 | `NovaMoments.CandidateProposed`（`event Action<NovaMomentCandidate>?`） | Runtime 已订阅，无需新增 |
| 对话组件 | `NovaChatView`：无参 `UserControl`，自绑 Runtime，Unloaded 自解绑 | 浮窗直接嵌入 |
| 设置持久化 | `LauncherSettings`（`LauncherPaths.cs` 47 行，`SettingsJson` 自动序列化） | 加属性即持久化 |
| 许可层开关 UI 范式 | `NovaPage` Permission_Toggled（`_toggling` 抑制标志） | 设置页开关照抄范式 |
| 状态机 | `NovaRuntime`：`StateChanged` / `Messages` / `RefreshState()` / `RunOnUi` | 事件转发挂这里 |

---

## 阶段 P1：Core 层（无 UI 依赖，先行）

### T1. 新建 `Starcloud.Core/Services/NovaFpsWatcher.cs`

启发式卡顿监测服务，纯软件层、零采集。

```
class NovaFpsWatcher
  Start(int processId)     // HomePage 启动游戏后传入 javaw pid
  Stop()                   // 游戏退出即停
  IsRunning                // Runtime 判断用

  event Action<StutterEvidence>? SuspectedStutter

record StutterEvidence(
    double CpuAvgPercent,      // 滑窗内 CPU 均值
    double CpuStdDev,          // 波动幅度
    int WindowSeconds,         // 命中窗口时长
    bool GpuAvailable,         // GPU 计数器是否可用（诚实标注数据源）
    double? GpuAvgPercent)
```

实现要点：
- **CPU%**：`Process.TotalProcessorTime` 差分 ÷ 采样间隔 ÷ `Environment.ProcessorCount`（自算，不用 PerformanceCounter，避免其初始化开销与兼容性坑）；进程句柄 `Process.GetProcessById(pid)` 一次获取，`HasExited` 后自动 Stop
- **GPU%**：PDH `PdhOpenQuery` + 「GPU Engine(pid_*)\Utilization Percentage」通配实例聚合；**任何 PDH 失败 → `GpuAvailable=false` 静默降级，绝不抛出**
- **采样节奏**：`System.Threading.Timer`（非 UI 线程）每 3s；滑动窗口 60s（环形 20 点）
- **判定**：窗口内 ≥4 个采样点 CPU>85% **且** 标准差 >15（卡顿是"高且抖"，纯高占用不一定是卡顿）→ 触发
- **冷却**：触发后 10 分钟内不再触发（防提醒风暴）
- **线程**：事件在采样线程触发，由 Runtime `RunOnUi` 转发（Watcher 不碰 UI）
- 异常策略：任何采样异常 → 计失败计数，连续 3 次失败 → 自动 Stop（禁用帧数提醒，静默）

验收：无 UI 依赖可独立实例化；PDH P/Invoke 仅本文件内。

### T2. `LauncherSettings`（LauncherPaths.cs）新增设置项

```csharp
public bool NovaOverlayEnabled { get; set; } = false;   // 默认关：开启=显式授权
public int? NovaOverlayX { get; set; }                   // 位置记忆（屏幕坐标）
public int? NovaOverlayY { get; set; }
```

`SettingsJson` 自动序列化新属性，无需改持久化代码。

验收：构建通过；settings.json 保存/读取往返不丢字段。

---

## 阶段 P2：浮窗本体（App 层）

### T3. 新建 `Starcloud.App/Views/NovaOverlayWindow.xaml(.cs)`

独立 WinUI 3 Window，自持生命周期，崩溃不外溢（对外接口全部 try/catch）。

**窗口样式**（`AppWindow` + Win32 兜底）：
- `OverlappedPresenter`：`IsAlwaysOnTop=true`、`IsResizable=false`、`IsMinimizable=false`、`IsMaximizable=false`
- P/Invoke `SetWindowLong(GWL_EXSTYLE)` 加 `WS_EX_TOOLWINDOW`（不进任务栏/Alt-Tab）
- 初始尺寸：收起 48×48；`AppWindow.Move` 到记忆位置（无记忆则右下角，距边 16px）

**收起态**：48×48 圆角 NovaOrb（复用 MainWindow 同款五态着色逻辑：灰/紫/黄/绿/暗灰 + 有提案黄点）
- 拖动：`PointerPressed/Moved/Released` + `AppWindow.Move`；拖动结束写回 `NovaOverlayX/Y`
- 点击（未拖动判定：位移 <4px）：展开

**展开态**：320px 宽、max-h 400px 毛玻璃面板
- 标头：Nova 标识 + 状态文字 + 收起按钮
- 内容：`<views:NovaChatView />`（零改动嵌入，对话/时刻确认/邀请清单/LLM 降级链全部天然可用）
- 展开/收起切换窗口尺寸并保持贴角锚点（以右下角为锚调整位置）

**气泡提醒**：`ShowToast(string text)`——Orb 旁弹出单条卡片，10s DispatcherQueueTimer 自动收；新 toast 替换旧 toast

**Ctrl+N 全局热键**：
- P/Invoke `RegisterHotKey(hwnd, 0x4E4F, MOD_CONTROL, 'N')` 绑定浮窗 hwnd
- comctl32 `SetWindowSubclass` 拦 `WM_HOTKEY` → 展开/收起切换（子类化回调里 TryEnqueue 到 UI 线程）
- 注册失败 → `ShowToast("热键被占用，点我也可以")`，一次性提示，功能不劣化

**全屏独占检测**：2s UI timer
- `GetForegroundWindow` == 游戏主窗口（pid 匹配）且 `GetWindowRect` 覆盖整屏且无 `WS_CAPTION`
- 判定独占 → `Hide()` + 通过 Runtime 发一次状态栏提示「游戏全屏独占中，浮窗不可见；无边框窗口模式可显示」（每次游戏会话只提示一次）
- 判定解除 → 恢复显示

**五态着色**：订阅 `NovaRuntime.StateChanged`（同 MainWindow `ApplyNovaState` 逻辑，抽共用或复制小函数——**倾向复制**，避免为 20 行代码建抽象）

验收：窗口样式正确（无任务栏项、置顶、不可调大小）；拖动记忆；热键可用；独占隐藏；Toast 10s 自收。

### T4. `NovaRuntime` 扩展（事件转发 + 生命周期挂钩）

```
新增事件  event Action<NovaOverlayToast>? OverlayToast
          record NovaOverlayToast(string Text, StutterEvidence? Evidence = null)

OnGameLaunched(versionId, gamePid = 0)   // 签名扩展：新增 pid 参数（默认值兼容旧调用）
  - 旧逻辑不变（StartSession / 时刻观察）
  - 若 Settings.NovaOverlayEnabled → 显示浮窗（懒创建，单例持有）
  - 若 pid>0 且 设置开 → NovaFpsWatcher.Start(pid)
  - 订阅（一次）：Watcher.SuspectedStutter → RunOnUi：
      Messages.Add(证据卡消息：一句话 + [看体检方案][别管了] 计划卡)
      OverlayToast?.Invoke(摘要)   // 浮窗弹一句，详情在对话流

OnGameExitedAsync()
  - 停 Watcher
  - 旧战报逻辑不变；战报卡同时 OverlayToast 一句话
  - 浮窗延迟 5s 隐藏（让用户看完战报）
```

「疑似卡顿」证据卡文案（遵守 Nova 语气：具体、不臆造）：
> 「刚才 90 秒里 CPU 平均 92%、抖动明显，**疑似**卡顿（启发式推测，不是实测帧数）。要我跑一次游戏体检找原因吗？」
> [看体检方案] [别管了]

「别管了」复用 `CancelPlan`（记流水），「看体检方案」复用既有体检意图（`ChatAsync` 体检路由）。

验收：游戏启动 → 浮窗出现 + Watcher 运行；退出 → 战报 + 5s 后隐藏；证据卡在底部条/浮窗/页面三处一致可见。

### T5. `HomePage.xaml.cs` 启动处传递 pid

```csharp
_gameProcess = await _s.Launcher.LaunchVersionAsync(...);
_s.NovaRuntime.OnGameLaunched(versionId, _gameProcess.Id);   // 传入 pid
```

`AppServices` 无需新成员（Watcher 由 Runtime 内部持有）。

验收：构建通过；启动游戏后 Watcher 收到有效 pid。

---

## 阶段 P3：设置与主窗口联动

### T6. `SettingsPage` Nova 区新增「游戏内浮窗」开关

- ToggleSwitch + 说明文案：「游戏运行时在屏幕角落显示 Nova 浮窗（Ctrl+N 呼出对话）。默认关闭。」
- 范式照抄 NovaPage 许可层开关（`_toggling` 抑制标志防代码设置触发事件）
- 开启即生效：下次游戏启动生效（不追溯当前会话——克制，不中途弹窗）

### T7. `MainWindow` 状态栏提示通道

- `NovaRuntime.OverlayToast` 订阅：独占隐藏提示 / 热键占用提示 → 状态栏 `NovaStateText` 短暂替换后恢复（或 InfoBar，取现有状态栏实现最小改动方案）
- 浮窗实例由 Runtime 懒创建持有，MainWindow 不持引用（避免循环依赖）

---

## 阶段 P4：验证与收尾

### T8. 构建
`dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo` → **0 警告 0 错误**

### T9. 运行时验证（PowerShell UIA + 场景脚本）
1. UIA：浮窗 Orb 元素（AutomationProperties.Name="Nova 浮窗"）在 UIA 树可见
2. 场景：开启开关 → 启动游戏 → Orb 出现（截图/位置断言）
3. CPU 突增模拟 → 「疑似卡顿」证据卡出现且带实测数值（可用测试钩子或临时高负载进程）
4. Ctrl+N → 展开/收起；拖动 → 位置记忆重启后保持
5. 退出游戏 → 战报卡 → 5s 后浮窗隐藏
6. 收尾：UIA 清理进程，报告给用户

---

## 风险与对策

| 风险 | 对策 |
|---|---|
| WinUI 3 Window 无 HwndSource，WM_HOTKEY 难拦 | `SetWindowSubclass(comctl32)` 对浮窗自身 hwnd 子类化（未打包下可用）；失败降级仅点击 |
| PDH GPU Engine 计数器在部分驱动缺失 | 静默降级仅 CPU（GpuAvailable=false，诚实标注） |
| 拖动与点击冲突 | 位移阈值 4px 区分 |
| 浮窗抢游戏焦点 | 收起态 Orb 仅接收点击；展开态才接受键盘焦点；MC 窗口化失焦不暂停 |
| 全屏独占误判（无边框窗口也被判独占） | 判定条件限定「无 WS_CAPTION 且覆盖全屏」→ 无边框同样命中会被隐藏——**修正：无边框（borderless fullscreen）不隐藏**，需区分：检测 `WS_CAPTION` 缺失 + rect 全屏 + **再查游戏进程窗口是否独占模式**（简化：无边框窗口含 `WS_EX_APPWINDOW` 或通过 `QueryDisplayConfig` 确认独占；实现时以「能读到游戏窗口且未失败」为准，独占时 GetWindowRect/读样式通常失败或返回装饰性差异——实现阶段用真机校准，宁可漏判（不隐藏）不误判） |
| 提醒风暴 | 10 分钟冷却 + toast 10s 自收 + 单条替换 |

---

## 任务顺序与依赖

```
T1 (Watcher) ──┐
T2 (设置)  ────┼──→ T4 (Runtime 挂钩) ──→ T5 (HomePage pid) ──→ T8 构建
T3 (浮窗)  ────┘                        ──→ T6 (设置页) ──→ T7 (状态栏) ──→ T9 UIA 验证
```

T1/T2/T3 相互独立可并行；T4 依赖三者；T5-T7 依赖 T4；T8 全量构建；T9 运行时验证。
