# Starcloud 安卓启动器（MAUI + 原生混合宿主 · 全版本 · 宽松组件）（2026-09-30）

## 1. 背景与目标

`PRODUCT.md` 目前写「仅 Windows x64，无移动端目标」。本设计推翻该定位：
新增**安卓端启动器**，作为仓库第四个子项目（与 `dotnet/`、`crossplatform/`、
`starcloud-multiplayer/` 平级，**每端单独一个顶层文件夹**）。

目标只有一句话：**手机上直接玩 Minecraft: Java Edition 的完整启动器**——
不是管理端、不是陪伴端，桌面端的主力功能（版本 / ModLoader / 整合包 / Mod 平台 / 账户）
在安卓 v1 全量对齐。

### 1.1 已确认决策（与用户逐条澄清）

| 维度      | 决定                                                              |
| ------- | --------------------------------------------------------------- |
| 核心目标    | 完整启动器：手机能玩                                                      |
| 授权路线    | **只用宽松组件**（BSD / MIT / LGPL 动态链接 / GPLv2+CE），不用 GPL fork 路线     |
| MC 版本范围 | **全版本**（含 1.12.2 及更老）                                           |
| 设备基线    | **arm64 + Android 9/10+**；gl4es 与 Zink 双渲染链都要                   |
| 壳技术栈    | **C#/.NET Android**，复用 `Starcloud.Core`（173/173 测试基线）           |
| UI 路线   | **MAUI + 原生混合**：管理页 MAUI，游戏宿主层原生 SurfaceView + 悬浮层              |
| v1 功能   | 全功能对齐（版本 / 加载器 / 整合包 / Mod 平台 / 账户），砍插件 / Nova / 外观长尾           |
| 联机      | v1 不含，架构预留 `IOnlineService` 集成点（将来接 `starcloud-multiplayer` 内核） |
| 分发      | APK 直装（GitHub Releases），与桌面端同款发布模式                              |

## 2. 非目标

* **联机**：v1 不实现（内核已独立存在，将来作为组件接入）。

* **插件系统 / Nova / 皮肤站外观自定义**等长尾功能。

* **iOS**：本设计只覆盖 Android；iOS 是另一套宿主（MoltenVK/Darwin），不在范围。

* **鸿蒙 ArkTS**：与联机项目的约束一致，不做。

* **Google Play 上架**：审核与第三方启动器合规风险，不走。

* **老版本渲染完美性**：1.12.2- 的 gl4es 链以「能玩」为准，不做性能压榨。

## 3. 运行时组件与授权核实（硬约束）

设计前逐组件核实过真实授权（2026-09-30 查证），**本表是选型的唯一依据**：

| 组件                                             | 用途                                | 授权                                | 备注                                                             |
| ---------------------------------------------- | --------------------------------- | --------------------------------- | -------------------------------------------------------------- |
| `openjdk-multiarch-jdk8u` 等（PojavLauncherTeam） | JRE 8 / 17 / 21（Android arm64）    | **GPLv2 + Classpath Exception**   | 链接例外，不传染我们的代码                                                  |
| `PojavLauncherTeam/lwjgl3`                     | LWJGL 3.3.3 Android 构建            | **BSD-3-Clause**                  | **已集成 lwjglx**，1.12.2 及更老版本直接跑这条链                              |
| `jre_lwjgl3glfw`（GLFW 胶水）                      | MC 1.13+ 的窗口 / 输入 / EGL           | **LGPL-3.0**（在 Pojav 主仓内，非 GPL-3） | 动态链接合规，见 §11 义务                                                |
| GL4ES                                          | OpenGL 2.1 → GLES 转译（老版本）         | **MIT**                           | <br />                                                         |
| Mesa 3D（Zink）                                  | OpenGL 3.2+ → Vulkan（1.15+/1.17+） | **MIT**                           | 自构建产物（含 turnip/freedreno/llvmpipe），不依赖系统驱动                     |
| OpenAL Soft                                    | 声音                                | **LGPL-2.1**                      | 独立 so 动态加载                                                     |
| lwjglx（原始仓库）                                   | LWJGL2 兼容层源码                      | **无 LICENSE（风险披露）**               | 我们不单独引入它；只用「已集成的 BSD-3 lwjgl3 仓库」的产物，上游多年公开分发实践风险低，README 如实披露 |

**上游状态风险**：PojavLauncher 主仓 2025-09 已归档，官方继任为 Amethyst Launcher。
组件产物获取策略见 §8（自建 fork CI 兜底）。

## 4. 总体架构

```
┌─────────────────────────────────────────────┐
│ Starcloud.Android.Maui（MAUI 壳）            │
│  管理页：首页/版本/下载/整合包/账户/设置       │
│  游戏页：原生 SurfaceView + 悬浮键鼠层        │
├─────────────────────────────────────────────┤
│ Starcloud.Android.Host（net10.0-android）    │
│  JVM 拉起 · Surface 桥 · 输入桥 · 前台服务    │
│  组件部署器 · LaunchSpec 执行（进程内）        │
├─────────────────────────────────────────────┤
│ Starcloud.Android.Core（net8.0 纯逻辑）      │
│  组件清单/部署计划 · LaunchSpec 模型          │
│  输入映射 · 键位配置模型                      │
├─────────────────────────────────────────────┤
│ Starcloud.Core（net8.0，直接复用）            │
│  版本清单 · 下载引擎 · Modrinth/CurseForge   │
│  整合包 · 账户 · authlib-injector            │
└─────────────────────────────────────────────┘
```

* **进程模型**：游戏跑在宿主进程内（`JNI_CreateJavaVM` 拉起，Pojav 同款）。
  不搞多进程：Surface 直接交给游戏，输入桥路径最短，内存少一份运行时拷贝。

* **目标框架**：`net10.0-android`（与 `crossplatform/` 的 net10 对齐，.NET 10 是 LTS；
  net8.0-android 已 EOL——联机项目踩过 NETSDK1202）。

* **联机预留**：Host 定义 `IOnlineService` 空接口，v1 无实现。

## 5. 工程结构

```
android/
  Starcloud.Android.slnx
  src/
    Starcloud.Android.Core/        纯逻辑（net8.0）：组件清单与部署计划、LaunchSpec、
                                   输入映射表、键位配置模型（可被桌面单测复用）
    Starcloud.Android.Host/        宿主层（net10.0-android）：JNI 拉起 JVM、Surface 桥、
                                   输入桥、前台服务、组件释放、真实启动执行
    Starcloud.Android.Maui/        MAUI 壳（net10.0-android）：页面、导航、游戏画面页
  tests/
    Starcloud.Android.Core.Tests/  xUnit，测试方法名中文（仓库既有风格）
  components/
    manifest.json                  组件清单：名称/版本/URL/SHA256/解压形态/许可标注
    fetch-components.ps1           构建期预热缓存（哈希钉死，思路同联机 fetch-kernel.ps1）
  README.md                        构建、真机自检、许可与分发
```

* `Starcloud.Android.Core` 引用 `Starcloud.Core`（net8.0 → net8.0）；
  Host 与 Maui 引用 Core + Android.Core。

* 平台差异收敛原则与联机项目一致：**纯逻辑进 Android.Core 可测试，
  Android API 只出现在 Host/Maui**。

## 6. 游戏宿主层（技术核心）

### 6.1 JVM 拉起

* P/Invoke `libjvm.so`：`JNI_GetCreatedJavaVMs` / `JNI_CreateJavaVM`
  （JNI Invocation API；`libjvm.so` 从组件目录按绝对路径 `System.load`）。

* **LaunchSpec 结构化**（关键改造，见 §7）：JVM 参数 / classpath / main 类 / 程序参数 /
  native 目录 / 环境变量，作为模型贯穿 Core → Host。桌面端把它拼成命令行，
  安卓端把它变成 `JavaVMInitArgs` + JNI 调用。

* 游戏入口：自写引导（设置系统属性 → 拼胶水层 classpath → 反射调用 MC main），
  Pojav 模式；**版本 → JRE 映射**：≤1.16 → JRE 8，1.17 → 17，1.18+ → 21；
  ModLoader 的要求以其 `version.json` 的 `javaVersion.majorVersion` 为准。

* **【M0-S1 已验证 2026-10-01，荣耀平板 BTK-W00 / Android 12】**JNI Invocation 路线
  （P/Invoke 函数表直调，不依赖绑定生成）端到端可行，九步全过：dup2 重定向 →
  绝对路径 `NativeLibrary.Load` → `JNI_GetCreatedJavaVMs`=0 → `JNI_CreateJavaVM`
  （请求 JNI 1.8）→ `GetVersion`/`FindClass`/`GetStaticMethodID` → `NewStringUTF`/
  `NewObjectArray`/`CallStaticVoidMethod` 反射调 `HelloWorld.main` → 输出断言
  （`java.version=17-internal`）→ 子线程 attach 双形态 → `DestroyJavaVM` rc=0。
  要点与教训：
  - **SELinux 否定性实证**：`/data/local/tmp` 下文件为 `shell_data_file` 上下文，
    `untrusted_app` 域 dlopen 的 mmap PROT_EXEC 被拒（avc denied { execute }，
    permissive=0）；JRE 必须放 app 私有目录（`app_data_file` 允许应用加载自身 so，
    即加载 app 数据内自有代码的标准允许项）。调试时 adb push 后经 `run-as cp` 拷入
    `files/`，上下文自然变为 `app_data_file`，无需 restorecon。
  - **不依赖 LD_LIBRARY_PATH 成立**：绝对路径加载 libjvm.so 后，HotSpot 按
    `sun.boot.library.path`（= `java.home/lib`）自行加载 libjava.so 等同伴库。
    bionic dlopen 返回 soinfo 句柄而非基址，真实基址看 `/proc/self/maps`。
  - **函数表索引教训（本轮根因崩溃）**：JavaVM 表（JNIInvokeInterface_）保留 3 槽
    （[0-2]），[3]DestroyJavaVM、[4]AttachCurrentThread、[5]DetachCurrentThread、
    [6]GetEnv、[7]AttachCurrentThreadAsDaemon；JNIEnv 表保留 4 槽（[0-3]），[4]GetVersion 起。
    两表保留槽数不同；此前 JavaVM 索引错用 4~8（整体 +1 错位），把 AttachCurrentThread
    当 DestroyJavaVM 调，产生「普通 Attach 确定性崩溃」的幻象（pc=attach_current_thread+0x58，
    fault addr=0x3，已附加线程快速路径写 \*p_env 命中垃圾值）。索引修正后幻象消失，
    双形态 attach 全部成功——崩溃从未是 attach 本身的问题。
  - **attach 双形态实测**：原生 pthread 全周期（attach → GetEnv → GetVersion → detach），
    普通 AttachCurrentThread 与 AttachCurrentThreadAsDaemon 均成功（rc 全 0）；
    `GetVersion` 返回 0xA0000（JNI_VERSION_10，HotSpot JDK10+ CurrentVersion）——
    请求版本在支持范围内时 Create/GetEnv 按规范返回 JNI_OK，GetVersion 如实上报实际版本。
  - **输出捕获**：Android 应用进程 stdout 进 /dev/null，须 dup2 到文件；
    .NET Android 的 Console.\* 走 logcat、不受 dup2 影响，托管文件 IO 才是可靠落盘通道。
  - **M3 正式实现**：沿用手写函数表索引（JavaVM 常量 [3]~[7]，与 JNIEnv 表严格区分）；
    JRE 部署器直接落 app 私有目录；继续不依赖 LD_LIBRARY_PATH。

### 6.2 Surface / EGL 宿主

* 游戏画面 = MAUI 页面内嵌原生 `SurfaceView`（混合视图）。

* `ISurfaceHolderCallback` 的创建 / 销毁 / 尺寸变化 → JNI 通知 `jre_lwjgl3glfw`；
  EGL 上下文与 GL 调用全部由胶水层和 MC 自己管理，**宿主绝不碰 GL**。

* **【M0-S4 已验证 2026-10-01，荣耀平板 BTK-W00 / Android 12】**混合视图方案可行：
  `ViewHandler<GameSurfaceView, SurfaceView>` 映射正常，Surface 生命周期回调可达
  （创建 + 1440×1723 尺寸变化），`SetOnTouchListener` 返回 true 时 Pressed/Moved/Released
  完整事件序列不被 MAUI 吞掉。**M4 游戏页正式采用 MAUI 嵌入方案**（不退原生 Activity）。
  实现要点：net10 binding 中触摸接口为嵌套形式 `View.IOnTouchListener`；
  触摸枚举真名 `MotionEventActions`；MAUI `Editor.Text` 默认值为 null。

### 6.3 渲染链自动选择

| MC 版本       | LWJGL          | 转译层                      | 说明                                      |
| ----------- | -------------- | ------------------------ | --------------------------------------- |
| 1.17+       | BSD-3 lwjgl3   | **仅 Zink**（自构建 Mesa）     | 1.17+ 要求 OpenGL 3.2，gl4es（GL 2.1 上限）兜不住 |
| 1.13 - 1.16 | BSD-3 lwjgl3   | 优先 Zink，失败回退 gl4es       | 此区间 MC 只需 GL 2.0+，gl4es 可跑              |
| 1.12.2-     | 同上（已集成 lwjglx） | gl4es（OpenGL 2.1 → GLES） | 老版本只此一条链                                |

* 注入机制：**所有 native 库按绝对路径** **`System.load`** **加载，不依赖** **`LD_LIBRARY_PATH`**
  （bionic 对运行期修改该变量不可靠，这是与桌面 glibc 的关键差异，M0 spike 首先验证）。

* 转译层切换 = 组件部署目录不同 + 加载名替换（gl4es 提供 `libGL.so` 语义），
  设置页可强制指定渲染链（默认自动）。

### 6.4 输入桥

* 事件流：`SurfaceView.Touch` → **InputMapper（Android.Core 纯逻辑，单测覆盖）**
  → GLFW 鼠标 / 键盘事件 → JNI 回调进胶水层。

* 触屏默认语义：单指点按 = 左键；长按 = 右键；双指拖动 = 纯指针移动（不点击）。
  全部可配置。

* 外接键鼠（USB / 蓝牙）：Android 原生 `InputDevice` 事件直接透传映射（WASD / ESC / F3 等）。

* 虚拟按键点击 = 合成 GLFW 键盘事件，与触屏手势共用同一条 InputMapper 出口。

### 6.5 虚拟键鼠

* 悬浮层 = 游戏页内 `FrameLayout` 叠加（**应用内视图，不用系统悬浮窗权限**）。

* 组成：方向键组（WASD）、跳跃、潜行、背包、聊天、准星模式开关、可收起虚拟键盘。

* 布局可编辑（位置 / 大小 / 透明度），配置 JSON 走 Core 配置体系，可导出分享（v2 再说）。

### 6.6 生命周期与稳定性

* 游戏运行期挂**前台服务**（`dataSync`/`specialUse` 按 targetSdk 选型）+ 通知栏常驻，防杀。

* `onPause` 不强杀游戏（MC 暂停菜单语义自行处理）；Surface 丢失 → GLFW 窗口图标化事件。

* 崩溃：JVM 侧 UncaughtExceptionHandler + native crash 日志落 `<数据目录>/logs/`；
  兜底进程级回收。

* 退出链：MC 正常退出 → `DestroyJavaVM` → 前台服务停止 → 返回管理页。

## 7. Core 复用与适配点

| Core 能力                                           | 安卓侧处置                                                                          |
| ------------------------------------------------- | ------------------------------------------------------------------------------ |
| `GameLauncher.BuildCommandLineAsync`（参数构造）        | **复用**；新增结构化 `LaunchSpec` 产出，桌面 `Process.Start` 路径保持不变（回归靠 173/173 基线）         |
| `GameLauncher` 进程执行                               | **不复用**：安卓走 `InProcessJvmLauncher`（Host 内），执行端抽象为接口，桌面实现原样保留                   |
| `JavaService`（Adoptium 下载 + 注册表检测）                | **不适用**：Adoptium 无 Android aarch64 产物；安卓运行时来源 = 组件清单（§8）。`java/<major>` 目录约定沿用 |
| `DownloadService` / Modrinth / CurseForge / 整合包导入 | 直接复用                                                                           |
| 账户（微软 OAuth / 离线 / authlib-injector）              | 复用；OAuth 回调改安卓自定义 scheme deep link                                             |
| `PlatformInfo`                                    | 补安卓实现（路径 / 平台名）；Windows 注册表等平台代码确保有 OS 守卫                                      |
| 数据目录                                              | MC 数据目录 = `Android/data/<pkg>/files`（外部私有目录：文件管理器可达、卸载即清），`.minecraft` 结构沿用    |

## 8. 运行时组件管理

* **manifest.json**（进仓库）：每组件声明名称 / 版本 / 下载 URL / SHA256 / 解压形态 / 许可标注。
  组件包括：`jre8` / `jre17` / `jre21`、`lwjgl3`（natives + jar）、`glfw-stub`（胶水 jar）、
  `gl4es`、`mesa-zink`、`openal`。

* **不打入 APK**（JRE 每个数十 MB）：首次使用按需下载（复用 Core 下载引擎，SHA256 校验，
  失败给镜像前缀配置项——与联机内核下载同款策略）。

* 部署目录：`<数据目录>/components/<名称>/<版本>/`；`fetch-components.ps1` 供 CI 预热缓存。

* **产物来源策略**：优先上游 Actions / Amethyst 现成产物；
  断供则 fork 上游仓库自建 GitHub Actions 构建（MIT/BSD/LGPL 均允许），
  清单 URL 指向我们自己的 Releases。

## 9. UI 设计

* 管理页（竖屏，MAUI Shell 导航）：首页（卡片布局，对齐桌面首页卡片思路）、
  版本、下载（Mod 平台）、整合包、账户、设置。

* 游戏页（强制横屏）：全屏 SurfaceView + 边缘滑出工具条
  （虚拟键鼠开关 / 键盘 / 全屏 / 退出）。

* 视觉：品牌色浅黄 `#FFD322` / 深绿 `#3DD322` 沿用；安卓用 Material 3 风格
  （桌面端 Fluent 绑定是 WinUI 专属约束，不跨端强搬）；简体中文文案、无 emoji。

* 应用内设「开源组件」许可页（§11 义务）。

## 10. 构建、签名与分发

* `dotnet publish -f net10.0-android -c Release` 产 APK；自签 keystore（CI secrets 注入）。

* CI：GitHub Actions（Windows runner）：fetch 组件缓存 → publish → 签名 → 附到 Release。

* **构建 0 错 0 警硬约束沿用**（`TreatWarningsAsErrors`）。

* 更新：启动器内检查版本 → 跳转 Releases 下载 APK 触发安装 intent；不做静默自更新。

## 11. 许可与分发义务

* 启动器自身 **MIT**（沿用仓库 LICENSE），桌面 / 跨平台 / 安卓同源。

* LGPL 义务落实：`glfw-stub` / `openal` 以**独立 jar / so** 部署于组件目录（可替换、
  独立加载 = 动态链接合规）；应用内许可页列全部第三方组件 + 授权文本，
  并给出各组件对应的上游源码地址与我们构建所用的 commit。

* GPLv2+CE：OpenJDK 以独立组件使用，Classpath Exception 覆盖链接场景。

* README 风险披露：lwjglx 原始仓库无 LICENSE 的事实与只用集成产物的决策（§3）。

## 12. 错误处理与已知坑（预判 + Pojav 生态已知问题）

| 场景                                | 处理                                                              |
| --------------------------------- | --------------------------------------------------------------- |
| bionic 运行期改 `LD_LIBRARY_PATH` 不可靠 | 全部 native 绝对路径 `System.load`（M0 首验）                             |
| SELinux 禁止 app 加载 `/data/local/tmp` 的 so（M0-S1 实证 2026-10-01） | JRE / 组件直接部署到 app 私有目录（`app_data_file`）；adb 调试经 `run-as cp` 拷入 |
| JNI 函数表索引：JavaVM 表保留 3 槽、JNIEnv 表保留 4 槽（M0-S1 踩坑 2026-10-01） | 索引常量集中在唯一 helper，禁止散写魔法数；JavaVM 用 [3]~[7]，JNIEnv 从 [4] 起，两表严格区分 |
| .NET Android `Console.*` 走 logcat，不受 dup2 重定向影响（M0-S1 实证 2026-10-01） | 需要落盘的诊断走托管文件 IO；dup2 只对原生侧 stdout/stderr（JVM 日志、ExceptionDescribe）生效 |
| Android 10+ W^X                   | 只 `dlopen/System.load`，不 `exec` 私有目录文件；MC 不需要 exec（Pojav 已验证路径） |
| targetSdk 34+ 前台服务类型限制            | 声明对应类型与权限，游戏服务用 `specialUse` 并写说明                               |
| 低内存设备 OOM                         | JVM 堆按设备内存分级（复用 `SystemMemory` 思路），默认上限给保护值                     |
| Adreno 上 Zink 驱动差异                | 用自构建 Mesa（含 turnip），不依赖系统 Vulkan 驱动版本                           |
| 组件下载失败 / 哈希不匹配                    | 明确报错 + 镜像前缀配置项，不静默降级                                            |
| Surface 异常销毁（切后台被回收）              | GLFW 图标化事件 + 回前台重建链路，M1 真机专项验证                                  |
| Debug 构建 FastDev 模式（已踩坑 2026-10-01）| 程序集放设备 `__override__` 目录不进 APK，`adb install` 直装必崩（monodroid Abort）→ csproj 固定 `EmbedAssembliesIntoApk=true`，与 APK 直装分发路线一致 |

## 13. 测试策略

* **单元测试**（Android.Core.Tests，全量常跑）：组件清单解析与部署计划、
  LaunchSpec 构造、输入映射表、键位配置、版本→JRE/渲染链选择。

* **Core 回归**：桌面 173/173 基线不得回归（LaunchSpec 改造是 Core 少数改动点）。

* **M0 真机 spike**：JVM 拉起 hello-world；LWJGL3 demo 经胶水层渲染；MAUI 嵌 SurfaceView。

* **手工验收**：真机跑最新 vanilla（Zink 链）+ 1.12.2（gl4es 链）+ Forge / Fabric 各一版 +
  整合包导入 + 微软账户登录。

## 14. 风险与未决

| 风险               | 兜底                                                  |
| ---------------- | --------------------------------------------------- |
| JVM 拉起（.NET Android 宿主内 JNI Invocation） | ~~M0 spike 先行验证~~ **已验证可行（2026-10-01，见 §6.1），风险消除** |
| MAUI 成熟度（混合视图嵌入） | ~~M0 spike 先行验证~~ **已验证可行（2026-10-01，见 §6.2），M4 采用 MAUI 嵌入方案，风险消除** |
| Pojav 归档 → 组件断供  | §8 自建 fork CI；清单 URL 可指任意镜像                         |
| gl4es 老版本性能      | 版本兼容性页明示预期；v1 以能玩为准                                 |
| lwjglx 授权灰       | 只用 BSD-3 集成产物 + README 披露（§3）                       |
| 未决：顶层文件夹名        | 默认 `android/`；如需与联机命名对齐可改 `starcloud-android/`，一行改名 |

## 15. 里程碑

* **M0 技术验证（spike）**：独立最小工程真机验证四件事——JVM 拉起、
  LWJGL3 + 胶水层渲染、gl4es 链、MAUI 嵌 SurfaceView。结论回填本稿。

* **M1 骨架与纯逻辑**：解决方案 + Android.Core 全部纯逻辑 + 单测。

* **M2 组件管理与 Core 适配**：manifest / fetch / 部署器；LaunchSpec 改造（桌面回归全绿）。

* **M3 宿主层**：JVM 拉起 / Surface 桥 / 输入桥 / 前台服务。

* **M4 MAUI 壳最小可玩**：首页 / 版本下载 / 离线账户 / 游戏页 / 虚拟键鼠——真机跑通最新 vanilla。

* **M5 全功能对齐**：多 JRE + 渲染链自动选 / 微软账户 / Forge / Fabric / 整合包 / Mod 平台。

* **M6 收尾**：许可页 / CI / README / 验收清单回填。

## 16. 验收清单

1. ⬜ `dotnet build android/Starcloud.Android.slnx` 0 错 0 警；Android.Core 单测全绿。
2. ⬜ `Starcloud.Core` 桌面测试 173/173 不回归。
3. ⬜ 真机（arm64，Android 9/10+）：最新 vanilla 从下载到进世界全程在本启动器内完成。
4. ⬜ 真机：1.12.2 经 gl4es 链可进世界（全版本承诺的最低线）。
5. ⬜ Forge / Fabric 各一版可安装并启动；Modrinth / CurseForge 可浏览下载安装。
6. ⬜ 整合包（Modrinth 格式）导入可用。
7. ⬜ 微软账户登录 + 离线账户可用；authlib-injector 外置登录可用。
8. ⬜ 触屏 / 外接键鼠 / 虚拟键鼠三套输入均可玩；前台服务防杀（切后台 10 分钟不死）。
9. ⬜ 应用内开源组件许可页完整（§11 义务）；README 披露 lwjglx 状态。

