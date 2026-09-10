# 资源列表缓存 · 多 Java 选择 · 全局设置 · 版本设置三态 设计

日期：2026-09-11
状态：设计已确认（分节确认完成），待写实现计划

## 1. 背景与问题

用户诉求（四个子需求）：

> 模组和版本搜索速度太慢了，可以新增缓存，每次打开的时候先缓存一次，后面在拉取到新内容时
> 再自动替换（需要设置里新增是否启用资源列表拉取缓存）；新增多 java 版本搜索与选择，
> 自动选择游戏推荐的 java 版本，支持手动选择 java；游戏管理全部的能力都要复制到设置界面，
> 作为全局设置，包括新增游戏时是否启用版本隔离、全局 java 等各种全局设置；
> 版本设置界面默认填写的内容同全局设置，例如内存配置，有（全局设置 / 手动 / 自动）。

现状问题：

1. **A. 资源列表无缓存**。`ModrinthService` 只有内存态 `_categoriesCache`，
   `SearchAsync` 每次进入分区、切筛选、翻页都要走一次网络；`DownloadPage.LoadVersionsAsync()`
   虽调用 `GetManifestAsync(false)` 命中 `version_manifest_cache.json`，但该缓存是
   **无条件生效**的独立实现，用户既无法关闭、也无法清空，与「资源列表缓存开关」的诉求脱节。
   表现就是「打开页面先转圈、翻页再转圈」。
2. **B. Java 选择能力不足**。`SettingsPage` 的全局 Java 只是一个手填路径的 `JavaBox`；
   `VersionDetailPage` 的 `JavaCombo` 只在已安装版本里挑。既没有「自动 / 手动」的显式语义，
   也没有一处集中展示「已检测到的 Java 运行时」并提供搜索、下载、移除。
   `GameLauncher.ResolveJavaAsync()` 的自动推荐逻辑（按版本 JSON `javaVersion.majorVersion`
   精确匹配优先、其次最小超集、缺失则自动下载）已经存在，但用户看不到、也覆盖不了。
3. **C. 游戏管理能力未全局化**。`SettingsPage` 只有目录 / 内存 / 隔离默认 / 全局 Java /
   并发数 / 主题 / 浮窗，缺口包括：额外 JVM 参数、是否使用内置 JVM 参数、下载重试次数、
   默认窗口尺寸与全屏、全局环境变量、启动前后命令。这些目前只能在单个版本里设置。
4. **D. 版本设置与全局脱节**。`InstanceLayout.Resolve()` 里内存是
   `profile.MemoryMaxMb ?? settings.DefaultMaxMemoryMb`（**两态：有值就走自己的，无值跟随全局**，
   界面就是一个 NumberBox + 「恢复跟随」）；Java 同构（`profile.JavaPath ?? settings.JavaExecutable`）；
   窗口 `profile.WindowWidth/Height/Fullscreen` **完全没有全局回退**，隔离项已先行三态化
   （`"shared" | "isolated" | null`）。需求要求内存 / Java / 窗口统一为
   **跟随全局设置 / 自动配置 / 自定义** 三态。
5. **界面风格**。用户要求界面现代化，参照 PCL（Plain Craft Launcher）：
   左导航 + 白卡片 + 蓝色小节标题 + 圆点单选 + 胶囊开关 + 蓝色信息条 + 内存占用条。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| 缓存范围 | **只缓存默认列表**（进入分区、未搜索、未加分类筛选的第一页） |
| 缓存数据流 | 打开页面**先用缓存立即渲染**（带「缓存」角标）→ 并发网络请求 → 成功即写缓存并替换 UI；失败保留缓存、不弹错 |
| 缓存开关 | 设置页新增「启用资源列表拉取缓存」；**关 = 完全不读不写**；另有「清空缓存」按钮 |
| 版本清单缓存 | **纳入同一开关**；旧 `version_manifest_cache.json` 首次读时迁移到新缓存层并删除旧文件 |
| 缓存实现 | **新增 `ResourceCacheService`**，一 key 一文件，文件名 = key 的 SHA1 |
| 缓存目录 | `%APPDATA%/starcloud-launcher/cache/` |
| 手动刷新 | 用户主动点「搜索 / 刷新 / 翻页」**永远直连网络**，不走缓存 |
| 全局 Java 存储 | **不新增字段**：`java_executable` 为空 ⇔ 自动（按版本推荐），有值 ⇔ 手动；旧配置零迁移 |
| Java 自动推荐 | 复用 `GameLauncher.ResolveJavaAsync()` 现有逻辑，不改判定规则 |
| 三态命名 | 采用 PCL 命名：**跟随全局设置 / 自动配置 / 自定义** |
| 三态范围 | **仅内存 / Java / 窗口**三项；隔离保持「跟随全局 / 共享目录 / 独立目录」；额外参数与启动前后命令保持「留空即不追加」 |
| 三态落地方式 | `VersionProfile` 新增显式档位字段 `memory_mode` / `java_mode` / `window_mode` ∈ `global`/`manual`/`auto`，**`null` 等价 `global` 且兼容旧数据**（旧数据有值时按 `manual` 解释，见 3.4） |
| 内存「自动配置」语义 | 按物理内存比例：最大 = `clamp(物理/4, 2048MB, 物理×3/4)` 向下取整到 512 的倍数；**最小内存固定 512 MB 不可调**；界面显示「自动（约 X GB）」 |
| 全局内存界面 | **单滑块只调最大内存**，整数 GB 步进，最小内存固定 512 MB |
| 窗口「自动配置」语义 | 内置 854 × 480 窗口化 |
| 内存占用条 | **完整照 PCL，包含「已使用内存」**：左「已使用内存」右「游戏分配」，深蓝＝本机已用、浅蓝＝本版本分配段，数值行左「已使用 / 总量」右分配量 |
| 已使用内存取样 | **打开页面时取一次快照，不做秒级轮询**（`GlobalMemoryStatusEx`） |
| 全局新增项 | 全局环境变量、默认窗口尺寸、默认全屏、下载重试次数、额外 JVM 参数、是否使用内置 JVM 参数 |
| 设置页左导航分组 | **4 组：游戏 / Java / 下载与资源 / 通用** |
| 设置页视觉风格 | **PCL 风格**（左导航选中＝浅蓝底 + 蓝字 / 白卡片 + 1px 描边 + 微阴影 / 蓝色加粗小节标题 / 标签左·控件右 / 圆点单选 / 胶囊开关 / 蓝色左竖线信息条 / 内存占用条） |

设计稿（mockup）：`.superpowers/brainstorm/session-1/content/ui-mockups.html`（已被 `.gitignore` 忽略，
仅作设计参照，不入库）。

## 3. 数据模型

### 3.1 LauncherSettings 新增全局字段

`LauncherPaths.cs` 中的 `LauncherSettings` 追加字段，全部带 `[JsonPropertyName(snake_case)]` 且
**默认值保证旧配置零迁移**：

```csharp
[JsonPropertyName("resource_cache_enabled")] public bool ResourceCacheEnabled { get; set; } = true;
[JsonPropertyName("default_window_width")]  public int? DefaultWindowWidth  { get; set; } = 854;
[JsonPropertyName("default_window_height")] public int? DefaultWindowHeight { get; set; } = 480;
[JsonPropertyName("default_fullscreen")]     public bool DefaultFullscreen   { get; set; } = false;
[JsonPropertyName("global_env_vars")]        public Dictionary<string,string> GlobalEnvVars { get; set; } = new();
```

既有字段本次**补上界面入口**（模型已存在，不改结构）：
`extra_jvm_args`、`use_builtin_jvm_args`、`download_max_retries`。

### 3.2 VersionProfile 新增档位字段

`InstanceSettings.cs` 的 `VersionProfile` 追加：

```csharp
[JsonPropertyName("memory_mode")] public string? MemoryMode { get; set; }  // global | manual | auto
[JsonPropertyName("java_mode")]   public string? JavaMode   { get; set; }
[JsonPropertyName("window_mode")] public string? WindowMode { get; set; }
```

`null` 表示未设置。**不改动现有字段类型与语义**，`isolation` 的三态保持原样。

### 3.3 EffectiveLaunch 扩展

窗口三件套补上全局回退来源后，`EffectiveLaunch` 的字段签名不变，但求值改为全局可回退：

```csharp
public sealed record EffectiveLaunch(
    string GameRoot, bool Isolated,
    int MinMem, int MaxMem,
    string? Java,
    int? WindowW, int? WindowH, bool? Fullscreen,
    ...);   // 其余字段（ServerAddress / Language / ExtraJvmArgs / EnvVars / ...）不变
```

### 3.4 InstanceLayout.Resolve 求值规则

为兼容旧数据，先按「旧字段是否有值」推断隐式档位，再按档位求值：

```csharp
static string Mode(string? explicitMode, bool hasLegacyValue)
    => explicitMode ?? (hasLegacyValue ? "manual" : "global");
```

| 项目 | 档位 | 求值 |
|---|---|---|
| 内存 | `global` | min = `settings.DefaultMinMemoryMb`（512）；max = `settings.DefaultMaxMemoryMb` |
| | `auto` | min = 512；max = `SystemMemory.AutoMaxMb()` |
| | `manual` | min = `profile.MemoryMinMb ?? 512`；max = `profile.MemoryMaxMb ?? settings.DefaultMaxMemoryMb` |
| Java | `global` | `settings.JavaExecutable`（空 ⇒ 由 `ResolveJavaAsync` 按版本推荐） |
| | `auto` | `null`（**强制走版本 JSON 推荐**，忽略全局 Java） |
| | `manual` | `profile.JavaPath` |
| 窗口 | `global` | `settings.DefaultWindowWidth/Height`、`settings.DefaultFullscreen` |
| | `auto` | 854 × 480、窗口化（`Fullscreen = false`） |
| | `manual` | `profile.WindowWidth/Height/Fullscreen` |

隐式档位推断（旧数据兼容，等价于改造前的行为）：

- 内存：`profile.MemoryMaxMb is not null` → `manual`，否则 `global`。
- Java：`profile.JavaPath is not null` → `manual`，否则 `global`。
- 窗口：`profile.WindowWidth is not null` → `manual`，否则 `global`。

`IsIsolated` 不动。**注意**：窗口新增了全局回退，改造前「版本未设窗口 ⇒ 无窗口参数」的行为
变为「⇒ 跟随全局（默认 854 × 480 窗口化）」，这是需求 D 期望的变更。

### 3.5 SystemMemory（新，Core 内静态工具）

```csharp
public static class SystemMemory
{
    public static (long TotalMb, long UsedMb) Snapshot();  // GlobalMemoryStatusEx
    public static int AutoMaxMb();                          // clamp(Total/4, 2048, Total*3/4) 向下取整到 512 倍数
}
```

- 取值方式：`GlobalMemoryStatusEx`（P/Invoke `kernel32.dll`）的 `ullTotalPhys` / `ullAvailPhys`；
  `UsedMb = (Total - Avail) / 1MB`。**一次调用即得总量与已用**，供自动内存与占用条共用。
- 不引入 `PerformanceCounter`、不做轮询。

### 3.6 ResourceCacheService（新）

`dotnet/src/Starcloud.Core/Services/ResourceCacheService.cs`

```csharp
public sealed class ResourceCacheService(Func<LauncherSettings> settings)
{
    public bool Enabled { get; set; }                       // 镜像 settings().ResourceCacheEnabled
    public bool TryRead<T>(string key, out T? value);
    public void Write<T>(string key, T value);
    public void ClearAll();
}
```

- 目录：`LauncherPaths.CacheDir => Path.Combine(DataDir, "cache")`。
- **一 key 一文件**，文件名 = key 的 SHA1（小写十六进制）+ `.json`，
  规避非法字符与长度问题；文件内容：

```json
{ "saved_at": "2026-09-11T12:00:00", "payload": { ... } }
```

- `TryRead<T>`：文件不存在或反序列化失败 → `false`（失败即视为未命中，**静默**，不抛错）。
- `Write<T>`：先写临时文件再 `File.Move(overwrite: true)`，与既有 `InstanceSettingsStore.Save` 同法。
- `Enabled == false` 时 `TryRead` 一律 `false`、`Write` 直接返回——即「完全不读不写」。
- **缓存 key**：
  - 社区默认列表：`modrinth.search.<section>.<loader>.<gameVersion>`
    （如 `modrinth.search.mod.fabric.1.21.1`）。`<loader>` / `<gameVersion>` 取当前解析结果
    （可能为空串）。
  - 原版版本清单：`manifest.vanilla`。
- **迁移**：首次访问 `manifest.vanilla` 未命中时，若旧 `version_manifest_cache.json` 存在，
  读入写入新 key 并删除旧文件；失败静默忽略。

## 4. UI 设计（PCL 风格）

### 4.1 视觉规范

沿用 mockup（`.superpowers/brainstorm/session-1/content/ui-mockups.html`）中定义的自定组件：

- **左导航**：条目 `padding 7×10`、圆角 6；选中＝`--selected-bg` 浅蓝底 + `--accent` 蓝字加粗。
- **卡片**：白底（深色主题为 `--bg-secondary`）+ 1px 描边 + `box-shadow 0 1px 2px rgba(0,0,0,.04)`，
  卡片间 10px 间距。
- **小节标题**：13px / 700 / `--accent` 蓝色。
- **行**：标签左（`--text-secondary`，约 86px 固定宽）、控件右自适应。
- **圆点单选**：15px 圆环，选中时圆心 7px 蓝点。
- **胶囊开关**：34 × 19，选中＝蓝底且滑块靠右。
- **信息条**：左侧 3px 蓝竖线 + 浅蓝底 + 蓝字，用于「将会使用：Java 21 …」。
- **内存占用条**：7px 高、圆角 4 的双段条，深蓝＝已使用、浅蓝＝游戏分配。

### 4.2 设置页结构

`SettingsPage.xaml` 由现有 3 个 `StackPanel`（`SectionGame` / `SectionJava` / `SectionGeneral`）
重构为 **左导航 + 右内容区**，`CategoryList` 扩为 4 组：

| 左导航 | 内容 |
|---|---|
| 游戏 | 游戏目录 / 内存分配 / 版本隔离（默认）/ 默认窗口 / 启动参数 / 全局环境变量 |
| Java | 全局 Java（自动 / 手动）+ 已检测到的运行时列表 |
| 下载与资源 | 下载并发数、下载重试次数、资源列表缓存（开关 + 清空缓存） |
| 通用 | 主题外观、Nova 游戏内浮窗 |

`SavedBar` / `ErrorBar` 保留。

### 4.3 设置 › 游戏

- **游戏目录**：`DotMcBox` + 「浏览…」不变。
- **内存分配**：单滑块（整数 GB，1 ~ 自动上限）+ 右上角实时值；下方刻度文案
  「1 GB … 12 GB（物理内存 16 GB 的 3/4）」；再下方一行 `p-hint`
  「整数 GB 步进；最小内存固定 512 MB」。
- **版本隔离（默认）**：圆点单选「独立目录（推荐）」/「共享目录」→ 写 `settings.version_isolated`。
- **默认窗口**：宽 × 高（`DefaultWindowWidth` / `DefaultWindowHeight`）+ 圆点单选 窗口 / 全屏
  （`DefaultFullscreen`）。
- **启动参数**：额外 JVM 参数（写 `ExtraJvmArgs`）、「使用启动器内置 JVM 参数」胶囊开关
  （写 `UseBuiltinJvmArgs`）。额外游戏参数与启动前后命令不设全局项，仍只在版本详情页设置。
- **全局环境变量**：键值行列表 + 每行「删除」+「添加变量」→ `GlobalEnvVars`。

### 4.4 设置 › Java

- **全局 Java**：圆点单选「自动选择（按版本推荐）」/「手动指定」。
  - 自动 ⇔ `settings.JavaExecutable` 为空；手动 ⇔ 有值（复用现有 `JavaBox` 作为手动模式下的路径输入）。
- **蓝色信息条**：显示当前实际会用的 Java，如
  「将会使用：Java 21 (21.0.7.0)：`C:\...\runtime\java-runtime-delta\bin\javaw.exe`」；
  自动模式下按当前选中版本的推荐值解析后展示。
- **已检测到的 Java 运行时**：由 `JavaService.ListRuntimesAsync()` 填充，每行显示
  `Java <版本> [来源][位数]`（来源：启动器 / 官方启动器 / 系统 PATH），
  当前生效项标「当前」，其余提供「选择」「移除」（移除仅对 `Source == "launcher"` 的运行时可用）。
- 底部按钮：**「浏览 java.exe…」**（填入手动路径）、**「下载 Java <major> ▾」**
  （下拉列常用 major，走 `JavaService.DownloadRuntimeAsync`，复用 `JavaProgress`）。
- 保留 `JavaProgress` / `JavaListText` 作为下载进度展示。

### 4.5 设置 › 下载与资源 / 通用

- 下载：并发数 `ConcurrencyBox`（1–64）+ **重试次数**（新，`DownloadMaxRetries`，1–10）。
- 资源列表缓存：胶囊开关（`ResourceCacheEnabled`）+ 说明文案 + **「清空缓存」** 按钮
  （调 `ResourceCacheService.ClearAll()`，完成后提示）。
- 通用：主题 `ThemeCombo`（保持现状）、`OverlaySwitch`（Nova 游戏内浮窗）。

### 4.6 版本详情页三态

`VersionDetailPage.xaml` 已在用 `RadioButtons`，内存 / Java / 窗口改为同构三态：

| 项 | 三选项 | 落地控件 |
|---|---|---|
| 内存分配 | 跟随全局设置 / 自动配置 / 自定义 | `MemoryModeRadio`；「自定义」展开单滑块（整数 GB，上界 = 自动上限）；**移除 `MinMemBox`**，最小内存固定 512 MB |
| Java | 跟随全局设置 / 自动配置 / 自定义 | `JavaModeRadio`；「自动配置」显示「自动（按版本推荐）：Java 17 —— 忽略全局 Java…」；「自定义」显示 `JavaCombo` + `JavaBrowseBtn` |
| 窗口 | 跟随全局设置 / 自动配置 / 自定义 | `WindowModeRadio`；「跟随全局」显示「跟随全局默认窗口：1280 × 720（窗口化）」；「自动配置」显示内置 854 × 480 |

- 每项保留 `XxxReset`「恢复跟随」（等价切到「跟随全局设置」）语义。
- `GameRootPreview` / `MemErrorText` / `JavaHintText` / `WindowHintText` 等既有提示控件沿用。

### 4.7 内存占用条（含已使用内存）

在内存分配区（设置 › 游戏 与版本详情页三态处）统一展示：

```
已使用内存                    游戏分配
[██████████░░░░░░░░░▒▒▒▒▒]      ← 深蓝＝已用，浅蓝＝本版本分配
12.1 GB / 16 GB                   4 GB
```

- 数据来源：`SystemMemory.Snapshot()` **打开页面时取一次快照**，不轮询。
- 深蓝段宽 = `Used / Total`；浅蓝段宽 = `分配量 / Total`（从深蓝段末尾续接）。
- 「自动配置」态下分配量显示为「自动（约 X GB）」。

## 5. 流程

### 5.1 资源列表缓存数据流

```
Loaded / 切换分区
  ├─ 若 Enabled 且命中缓存 → 立即渲染（列表右上角显示「缓存」角标）
  └─ 并发发起网络请求
        ├─ 成功 → Write 缓存 → 替换列表、去掉角标
        └─ 失败 → 保留缓存内容；无缓存时才显示 ErrorBar
用户主动搜索 / 点刷新 / 翻页 → 跳过缓存，直连网络（结果不回写缓存）
```

- 仅当 `offset == 0 && query == "" && category == null && index == "relevance"` 时读写缓存，
  即「只缓存默认列表」。
- 翻页（`offset > 0`）不读不写；`PrevBtn` / `NextBtn` 保持现状。
- `DownloadPage.LoadVersionsAsync()` 的原版清单同样走缓存路径（key `manifest.vanilla`）。

### 5.2 Java 解析链

```
EffectiveLaunch.Java
  ├─ 非空 → 直接用（全局手填 / 版本自定义）
  └─ 空   → GameLauncher.ResolveJavaAsync(version, dotMc)
              ├─ 取 version.JavaVersion?.MajorVersion ?? 8
              ├─ ListRuntimesAsync() 中「精确匹配优先，其次最小超集」
              └─ 都无 → DownloadRuntimeAsync(required) 自动下载
```

改造后新增的语义只有一条：**版本 `java_mode = auto` 时把 `EffectiveLaunch.Java` 置空**，
从而强制走版本 JSON 推荐、忽略全局 Java。

### 5.3 全局内存滑块与自动值

- 自动上限 = `SystemMemory.AutoMaxMb()`；滑块范围 1 GB ~ 自动上限（整数 GB）。
- 默认值 4 GB（`default_max_memory_mb` 既有默认）。
- 保存写回 `settings.default_max_memory_mb`；最小内存不由界面暴露，恒为
  `settings.default_min_memory_mb`（512）。

## 6. 关键操作细节

- **失败静默**：缓存读写、旧清单缓存迁移、Java 运行时探测失败一律静默处理，只在
  「无缓存可用且网络失败」时向用户报错。
- **开关即时生效**：`ResourceCacheEnabled` 保存后立即写回 `ResourceCacheService.Enabled`，
  无需重启。
- **向后兼容**：`LauncherSettings` 与 `VersionProfile` 的每个新字段都有安全默认值；
  三个档位字段为 `null` 时按 3.4 的隐式推断解释，旧配置行为不回退。
- **清空缓存即删文件**：`ClearAll()` 删除 `cache/` 下所有 `*.json`，不删目录本身。
- **Java 移除只删启动器自管**：`Source == "launcher"`（`LauncherPaths.JavaDir/<major>/`）
  的运行时才允许移除，系统 PATH 与官方启动器运行时只读。
- **自动配置不写档位字段之外的东西**：内存「自动配置」不改 `memory_max_mb`；
  仅靠 `memory_mode = "auto"` 表达，切回自定义时能还原用户原先填的值。

## 7. 验收标准

1. 关闭「资源列表拉取缓存」后，进入社区分区、切换分区、查看原版版本列表**不再读任何缓存文件**
   （`cache/` 无新增写入）；打开开关后再次进入能看到「缓存」角标并立刻出内容。
2. 打开开关后断网启动：社区分区与原版列表能用缓存渲染出上次的内容，且不弹出错误提示。
3. 「清空缓存」后 `%APPDATA%/starcloud-launcher/cache/` 下无 JSON 文件；旧
   `version_manifest_cache.json` 在首次成功读取后被删除。
4. 设置 › Java 的「已检测到的 Java 运行时」如实列出启动器 / 官方 / 系统 PATH 三类运行时，
   标出「当前」；点「选择」后信息条「将会使用」随之更新；点「移除」仅对启动器自管的生效。
5. 「浏览 java.exe…」选中的路径在保存后成为全局手动 Java；切回「自动选择（按版本推荐）」后
   该路径不再生效（`java_executable` 被清空）。
6. 设置 › 游戏 出现内存滑块（整数 GB、上界为物理内存的 3/4）、默认窗口尺寸与全屏、
   额外 JVM 参数、使用内置 JVM 参数开关、下载重试次数、全局环境变量，保存后写入
   `launcher.settings.json` 对应 snake_case 字段。
7. 版本详情页内存 / Java / 窗口三项均为「跟随全局设置 / 自动配置 / 自定义」三态；
   切到「跟随全局设置」时改用全局值，「自动配置」时内存按物理内存比例、窗口 854 × 480、
   Java 按版本推荐（忽略全局 Java）。
8. 老配置（`memory_max_mb` / `java_path` / `window_width` 有值、`*_mode` 为 null）
   升级后行为与改造前一致（被解释为 `manual`）。
9. 内存分配区显示占用条：左「已使用内存」/ 右「游戏分配」，深蓝＝本机已用、
   浅蓝＝本版本分配，数值行给出「已使用 / 总」与分配量；页面打开时取样一次。
10. 全部界面为 PCL 风格（左导航选中浅蓝、白卡片、蓝色小节标题、圆点单选、胶囊开关、
    蓝色信息条）。

## 8. 非目标

- 不做缓存的过期时间与后台自动刷新调度（每次打开都先出缓存、再拉新；不做定时任务）。
- 不缓存搜索结果的分页（`offset > 0`）与带搜索词 / 分类筛选的结果。
- 不做离线模式的完整降级（仅缓存命中可用）。
- 不新增全局「启动前后命令」与全局「额外游戏参数」字段（这两项仍只在版本详情页设置）。
- 不改 `GameLauncher.ResolveJavaAsync()` 的推荐判定规则（精确匹配优先、其次最小超集）。
- 不做 Java 运行时的完整卸载流程（仅删启动器自管目录）。
- 不改动版本隔离三态的语义与存储（`isolation` 原样保留）。
- 不做内存占用条的实时轮询 / 曲线图。
- 不重构 `ModrinthService` 的接口签名（仅新增缓存读写调用点）。
