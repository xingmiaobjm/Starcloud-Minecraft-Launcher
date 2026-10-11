# Starcloud 安卓启动器实施计划（2026-09-30）

对应设计稿：`docs/designs/2026-09-30-android-launcher-design.md`

## 交付物

新解决方案 `android/`（与 `dotnet/`、`crossplatform/`、`starcloud-multiplayer/` 平级）：

- **`Starcloud.Android.Core`**（net8.0 纯逻辑）：组件清单与部署计划、LaunchSpec 模型、
  输入映射表、键位配置模型、版本→JRE/渲染链选择。
- **`Starcloud.Android.Host`**（net10.0-android）：JNI 拉起 JVM、Surface 桥、输入桥、
  前台服务、组件释放、InProcessJvmLauncher。
- **`Starcloud.Android.Maui`**（net10.0-android）：MAUI 壳（管理页 + 游戏画面页 + 虚拟键鼠层）。
- **`Starcloud.Android.Core.Tests`**（xUnit，中文测试方法名）。
- **`components/manifest.json` + `fetch-components.ps1`**：组件清单与构建期预热脚本。
- **README**：构建、真机自检、许可与分发义务。

不交付：联机（仅留 `IOnlineService` 空接口）、插件/Nova/外观、iOS、鸿蒙、Google Play 渠道。

全局硬约束：构建 0 错 0 警；桌面 `Starcloud.Core` 测试 173/173 不回归；
设计未经用户批准不写实现代码（本计划获批准后开始）。

## 里程碑

### M0 技术验证（spike，独立最小工程，结论回填设计稿）

产出：四个真机可跑的最小验证（可放在 `android/spikes/`，验收后删除或归档）。

- **S1 JVM 拉起**：P/Invoke `libjvm.so` → `JNI_CreateJavaVM` → 跑一段编译好的
  hello-world class 的 main → 拿到标准输出 → `DestroyJavaVM`。
  验证：绝对路径 `System.load`、`JavaVMInitArgs` 编组、线程 attach。
- **S2 LWJGL3 + 胶水层**：SurfaceView + `jre_lwjgl3glfw` + lwjgl3 fork 产物渲染一个
  LWJGL demo（清屏变色即可）。验证：Surface 生命周期→GLFW 事件、EGL 由胶水层托管。
- **S3 gl4es 链**：同一 demo 走 gl4es 转译（验证加载名替换机制）。
- **S4 MAUI 混合视图**：MAUI 页面内嵌原生 SurfaceView，触摸事件可达原生层。
  验证：MAUI Handler↔PlatformView 桥不丢事件。

验收：四条 spike 真机（arm64，Android 9/10+）各自跑通；每条结论（含失败备选）
回填设计稿 §6/§12/§14。S4 失败则游戏页退原生 Activity（设计稿已备案）。

### M1 骨架与纯逻辑（Android.Core）

产出：解决方案、`Directory.Build.props`（TreatWarningsAsErrors）、Android.Core 全量纯逻辑。

- 组件模型：`ComponentManifest` 解析 / 校验（名称、版本、URL、SHA256、许可标注）、
  部署计划（缺失检测 → 下载项 → 解压目录布局）。
- `LaunchSpec`：JvmArgs / Classpath / MainClass / ProgramArgs / NativeDirs / Env /
  JreVersion；版本→JRE 映射（≤1.16→8，1.17→17，1.18+→21，ModLoader 按
  `javaVersion.majorVersion` 覆盖）。
- 渲染链选择：版本→Zink/gl4es 自动选 + 强制指定覆盖。
- 输入映射表：Android 触摸/按键语义 → GLFW 事件码（纯函数，逐条断言）。
- 键位配置模型：布局/大小/透明度/语义绑定，JSON 序列化往返。

验收：`dotnet build` 0 错 0 警；上述每项单元测试全绿。

### M2 组件管理与 Core 适配

产出：manifest.json（真实组件清单）、fetch-components.ps1、组件部署器、Core 改造。

- `manifest.json` 首版落地：jre8/17/21、lwjgl3、glfw-stub、gl4es、mesa-zink、openal
  （URL/SHA256 在 M2 逐个实测钉死；来源优先上游 Actions / Amethyst，断供转自建 fork CI）。
- `fetch-components.ps1`：哈希钉死预热缓存（思路同联机 `fetch-kernel.ps1`）。
- 部署器（Host 侧）：按部署计划下载（复用 Core `DownloadService`，镜像前缀可配）→
  SHA256 校验 → 解压到 `components/<名称>/<版本>/`。
- **Core 改造（唯一触碰桌面代码的点）**：`GameLauncher` 参数构造新增结构化
  `LaunchSpec` 产出（现有命令行路径保持）；执行端抽象 `IGameProcessLauncher`。
- Core 的 `PlatformInfo` 补安卓实现；账户 OAuth 回调支持自定义 scheme。

验收：Core 桌面测试 173/173 全绿；Android.Core 部署计划单测全绿；
fetch 脚本实测拉齐全部组件并通过哈希校验。

### M3 宿主层

产出：Starcloud.Android.Host 完整宿主能力。

- `InProcessJvmLauncher`：消费 LaunchSpec → JavaVMInitArgs → 拉起 JVM →
  引导（系统属性 + 胶水层 classpath + 反射 main）。
- Surface 桥：SurfaceView 生命周期 → JNI 通知胶水层。
- 输入桥：原生事件 → InputMapper → JNI 回调；外接键鼠透传。
- 前台服务 + 通知栏；崩溃钩子与日志落盘；退出链（DestroyJavaVM → 服务停止）。
- JVM 堆按设备内存分级。

验收：用 M2 组件在真机跑通**最新 vanilla 从启动到主菜单**（M0 spike 的工程化版本）。

### M4 MAUI 壳最小可玩

产出：可日常使用的安卓启动器雏形。

- MAUI Shell 导航：首页 / 版本 / 下载 / 账户 / 设置（版本下载与离线账户先行）。
- 游戏画面页：全屏 SurfaceView + 工具条 + 虚拟键鼠默认布局（WASD/跳跃/潜行/背包/准星）。
- 微软账户 OAuth deep link 登录。

验收（真机）：最新 vanilla 从 cold start 到进世界全程在本 App 内完成；
触屏 + 虚拟键鼠可完成基础操作（移动/破坏/放置/背包）；切后台 10 分钟进程不死。

### M5 全功能对齐

产出：对齐桌面主力功能。

- 全版本支持：三套 JRE 组件按需部署；gl4es/Zink 自动链 + 真机实测 1.12.2（gl4es）。
- Forge / Fabric 安装（复用 Core installer 链）并各验收一版。
- Modrinth / CurseForge 浏览下载安装；整合包导入。
- authlib-injector 外置登录。
- 联机预留：`IOnlineService` 空接口落位（无实现）。

验收（真机）：设计稿 §16 第 3-7 条逐项通过。

### M6 收尾

- 应用内「开源组件」许可页（全部组件 + 授权文本 + 上游 commit 指引）。
- GitHub Actions：构建 + 签名 + Release APK。
- README（构建、真机自检步骤、许可义务、lwjglx 披露）。
- 全量复验：android 解决方案 0 错 0 警、Core 173/173、设计稿 §16 验收清单逐条回填。

## 依赖与顺序

M0 → M1 → M2 → M3 → M4 → M5 → M6 串行；其中 M0 的 S4（MAUI 混合视图）结论
决定 M4 游戏页形态（MAUI 嵌入 or 原生 Activity），必须最先确认。
