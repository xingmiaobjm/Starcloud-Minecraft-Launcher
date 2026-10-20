# Starcloud 安卓版 · Kotlin/Compose 重写

Uno 壳（Starcloud.Launcher.Uno / .Phone）因 Skia 渲染器全页重栅格化在低端 GPU 上切页卡顿，
按「业务逻辑保留、前端重写」决策启动本工程。**Uno 壳在功能对齐前继续服役。**

## 架构

- UI：Kotlin + Jetpack Compose（Material3，品牌黄 #FFD322 深色主题）
- 业务：C# Core 按模块移植为 Kotlin（本工程 `data/` 包，逐步交付）
- 游戏宿主：**直接复用现有 Java Host**（`../Starcloud.Android.Host/Platforms/Android/Java/com/starcloud/launcher/**`，
  纯 Java 源码，Gradle sourceSets 直接纳入），C# 侧仅是 JNI 包装，Kotlin 原生调用更直接
- 数据目录：沿用 external files `.minecraft` 约定（与 Uno 壳一致，可并存共存）

## C# → Kotlin 移植映射（里程碑）

| 里程碑 | C# 源 | Kotlin 目标 | 说明 |
|---|---|---|---|
| M0（已完成 ✓） | — | 工程骨架 + 首页骨架 + 主机探针 | 链路验证：CLI 构建/安装/运行 |
| M1（已完成 ✓ 真机） | Core.ManifestService.ListInstalled | `data/VersionsRepository.kt` + `screens/VersionsScreen.kt` | 版本列表/类型/大小/删除（MatePad 实测 26.3 上屏） |
| M2（清单拉取 ✓ 真机 / 下载引擎待做） | Core 下载引擎（多源聚合） | `data/ManifestRepository.kt` + `screens/DownloadScreen.kt` | 清单已通（BMCLAPI 优先+8s 超时+缓存回落）；下一步：版本 json/jar/libraries/assets 下载落地 |
| M3 启动链 | Android.Core GameLauncherService（组件部署→Java 桥→JVM） | `game/LaunchOrchestrator.kt` + SurfaceView 游戏页 | 复用 Host Java；前台服务照搬 |
| M4 账户域 | Core 账户（微软 OAuth/authlib）+ Android.Core LaunchAccountService | `data/AccountsRepository.kt` | 离线先行，OAuth 随后 |
| M5 设置/其余 | Java 管理/插件宿主/联机/Nova | 按需 | 插件宿主含 Python 子进程方案需重新评估 |

## 构建与安装

```bash
C:/Users/xingmiao/tools/gradle-8.7/bin/gradle.bat assembleDebug   # 仓库镜像走阿里云
adb install -r app/build/outputs/apk/debug/app-debug.apk
```

包名 `cn.starcloud.launcher.compose`，与 Uno 壳并存，数据目录独立（后续对齐）。

## M3 启动链前置条件（未完成）

- JvmHost 在 C# 侧 P/Invoke `JNI_CreateJavaVM`（libjvm.so）——Kotlin 无法直接调 C 导出符号，
  **需要 NDK 编一个极小封装 .so**（Java_native 方法内部调 JNI_CreateJavaVM + AttachCurrentThread）。
  先确认 NDK 是否安装（`$env:LOCALAPPDATA/Android/Sdk/ndk`）。
- 其余 Host Java 类（JavaBridge/CallbackBridge/GameForegroundService/SdlGameActivity）为纯 Java，
  Kotlin 直接调用/纳入 sourceSets 即可。
- 组件部署（assets/components 解压）逻辑在 C# GameLauncherService，需 Kotlin 化（纯 unzip + 路径编排）。


## M3 启动链（骨架已通；jre25 SIGSEGV 已破；main 静默快退待查）

### Uno 壳 UI 修复（2026-10-11 二级导航色块/光斑/壁纸）
- **CategoryNavItem 色块方角**：Uno 的 Control 没有 CornerRadius 属性（WinUI 有），
  样式 Setter 与 TemplateBinding 静默失效——CategoryNavItem 显式注册 CornerRadius DP
  （new 隐藏基类成员），真机验证选中底/悬停底圆角恢复
- **跟手光斑触屏禁用**：HoverShadowBehavior.Wire 入口按 HasSystemFeature(FEATURE_PC)
  短路（触屏-only 设备整体不接线）——跟手光斑是鼠标悬停语义，手指按下即触发、
  抬起常驻不灭，Uno/触摸端整页被光斑层罩住（账号页实测整页灰罩）
- **内置壁纸生效**：csproj 的 UnoImage 会把 Wallpapers\*.jpg 转 drawable 导致原件不进
  APK（File.Exists 恒 false）——改 AndroidAsset 原件打包；ShellRootView 加安卓壁纸层
  （WallpaperImage + 主题遮罩，RowSpan 2，RefreshAppearance 按背景模式加载：
  builtin=AppAssets 资产流解 Bitmap / file=本地路径 / system=隐藏）

### hs_err 深度分析（2026-10-11）
信号清空修复后 HotSpot 正常接管崩溃并生成 hs_err（此前从未生成）。崩点读 [x0+0x10]
（x0 = 损坏 oop，非 48 位合法地址；x9 = 0x10 字段偏移），R2 = java.lang.String 的 Class
oop——**HotSpot 堆被宿主进程环境破坏**。全排除清单再+2：SerialGC、-XX:-UseCompressed
（均无效）。当前最优组合 targetSdk28 + CreateVM + SerialGC + 信号清空 + 全参数
（最深：GameConfig$QuickPlayVariant）。

已交付并真机验证：
- **libstarcloudjvm.so**（NDK 26.1，app/src/main/cpp/starcloud_jvm.c）：
  starcloudSetEnv + starcloudRunJvm（JNI_CreateJavaVM 路径）+ starcloudRunJvmJli（JLI_Launch 路径）
- **组件部署 Kotlin 化**：assets/components（349M 内置包）→ sha256 → tar.xz/zip 解压 →
  .starcloud-ok 标记；组件根必须在内部存储（linker namespace 白名单）
- **LaunchSpecBuilder Kotlin 化**：JRE/渲染链选择、classpath/env/jvmArgs/gameArgs 组装
  （关键 option：-Djava.home/-Dpojav.exec.path/-XX:ErrorFile；支持 Pojav 系 arguments.jvm
  的「对象+裸字符串」混合数组）
- **Java 宿主层 32 文件复用**（SDLActivity/FCLBridge/SdlGameActivity/键码桥）；
  JavaCompile 必须 options.encoding=UTF-8；game/StarcloudJvm.kt 对 Java 类用反射调用
- **首页「启动游戏」= 真实启动入口**（部署→Java 桥→spec→SDL 活动→JVM→退出码回显）

### jre25 SIGSEGV 突破（2026-10-10 JLI_Launch）
- **根因链之一已修**：Pojav 系版本 json 的 arguments.jvm 是「对象 + 裸字符串」混合数组，
  初版解析只处理对象形态，丢失 12 项关键参数（-XX:StackShadowPages=32、--enable-native-access、
  --add-exports×2、LWJGL/netty 工作目录、-cp ${classpath} 等）——appendValues 已支持混合形态
- **game 参数三修**：features 规则判定（--demo 的 is_demo_user 应滤掉）、
  resolution_width/height 变量补齐、空串 value 不追加（--quickPlayPath "" 会让 main 静默退出）
- **架构切换**：JNI_CreateJavaVM 的类加载 SIGSEGV 改走 **JLI_Launch**（bin/java 同代码入口，
  Pojav 系官方做法，见 PoavLauncher issue #713）——shim 新增 starcloudRunJvmJli（dlopen
  libjli.so → JLI_Launch(argc, argv...)），真机 JLI_Launch 启动成功、SIGSEGV 消除
- **so 链修复**：JLI 前必须执行组件 so 的 RTLD_GLOBAL 加载链（load_native_libraries），
  否则游戏 JVM 的 native 方法（FCLBridge.setLdLibraryPath 等）按 RTLD_DEFAULT 解析失败，
  SDL Error「No implementation found」——已加 nativeDirs 参数
- **注意**：bin/java 的 execve 被 SELinux 禁（targetSdk 36 + app data 可执行文件），
  独立进程形态不可用；run-as 域读外部 FUSE 也被拒，bin/java 复测需在 app 上下文

### 当前状态与下一步
- **双路径收敛（2026-10-11 凌晨）**：JLI_Launch 与 JNI_CreateJavaVM 的 SEGV 是同一
  HotSpot 内部缺陷（固定 pc libjvm+0xb24db0，fault addr d81310ec/f8131036 家族模式，
  类加载期 GameConfig$QuickPlayVariant 后，CPU 0 非计算）。参数/env/classpath 已与
  Uno 成功轮全对齐仍崩 → **换构建验证已完成（2026-10-11）**：新 ZalithLauncher 1.4.1.4
  的 jre25（25.0.3-internal-adhoc，另一人另一日期构建）同点同模式 SEGV——构建特异性排除，
  问题确认为 MobiPort HotSpot 与纯 ART 宿主的交互（Zalith2/FCL 纯 ART 能跑的差异在其
  启动器实现的细节）。**下一步**：对照 Zalith2/FCL 的 JVM 启动源码（FCL 的 Amethyst 后端 /
  Zalith 的 Pojav 继承层）找 CreateJavaVM 前后的缺失步骤；或装 Zalith2 真机对照验证设备
- **观测突破（2026-10-10 深夜）**：targetSdk 28 + CreateJavaVM 组合下加 -Xlog 观测成功——
  **MC main 真实运行**：Main/GameConfig/jopt 类全部加载、深入参数解析后**卡死**
  （SilentInitException 类被加载——MC 的静默初始化失败路径；进程存活但 Xlog/stdout 冻结）。
  卡点 = GameConfig/NativeLibrariesBootstrap 之间（JNA bootstrap 链最可疑——
  jna.boot.library.path 已设 5.17.0 bionic 版，需验证 JNA 实际加载）
- **卡点精确定位（2026-10-10 深夜，-Xlog:class+load=info:stdout 实时观测）**：
  类加载冻结于 Main$3 后零新类（6 分钟无进展、进程存活）——即 **MC 引导的
  LWJGL/native 库初始化之前**（Uno 成功轮此阶段后紧跟 [LWJGL] Version 输出，我们没有）；
  SilentInitException = Window/GLFW 创建失败的静默包装（MC 26.x 官方行为），
  本轮未到该点即冻结。下一步：对照 Zalith2（jre25 同源、纯 ART 跑通）的
  NativeLibrariesBootstrap/JNA 阶段行为，或 -Xlog:class+load=debug 拿更细粒度
- **targetSdk 矩阵结论**：36+JLI=exec 禁（SELinux）；28+JLI=SEGV 复发；36+CreateVM=卡早期；
  **28+CreateVM=main 活**（当前采用）；JLI_Launch 路径代码保留备用
- 此前「JLI 后静默快退」的主因即上述缺失项，argv/env 快照（launch_snapshot.txt）与
  Xlog 观测已持久化到 .minecraft 供逐项比对
- 对照基线：Uno 壳（C#+Mono 进程）同组件跑通 26.3 到 renderFrame 循环（其 Vulkan surface 有
  SURFACE_LOST 黑屏待解；Uno JvmHost.cs 已加 [jvm-opt]/[jvm-env]/[jvm-main]/[jvm-game]
  全量 Trace，复跑即得成功轮参数）
- 环境铁律：run-as 域读外部 FUSE 被拒；组件可执行验证须先 chmod 755 bin/java

## Fluent UI（对齐 Win 端）
- FluentTheme：品牌黄 #FFD322 / 深色窗体 #161616 / 卡片 #2A2A2A，深浅色跟随系统
- 壳骨架：44dp 顶栏（品牌+版本徽章）+ 64dp 左侧图标导航 + 内容区（home/versions/download/multiplayer）
- 首页：自检 chips + 版本库卡（LazyColumn 真实扫描）+ 240dp 启动卡（accent 启动按钮）
- 版本页/下载页：PageHeader 大标题 + Fluent 卡片圆角/间距统一；下载页日期友好化
- 已知工具链坑：composable 带默认参数时 PageHeader 标题字形整体不绘制（真机 K2.0.20 复现），
  PageHeader 采用必传签名规避（详见 FluentUi.kt 注释）；截图验证务必像素量化（亮度扫描），
  screencap 为 2200x1440 原始分辨率，勿按预览缩放图估算裁剪坐标。
