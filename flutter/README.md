# Starcloud Launcher（Flutter 跨平台版）

> 微软 Fluent 设计系统（[fluent_ui](https://pub.dev/packages/fluent_ui)）+ Flutter。
> **Linux 端先行**，macOS 共用同一套 UI 源码与插件内核。
> 插件模型规范：[`docs/plugin-spec-v3.md`](../docs/plugin-spec-v3.md)（内核 + 插件，六端共用一份）。

## 运行

下载 / 安装 / 启动 / 设置这些后端能力**只在 C# 里实现一份**（见下节），
Flutter 端运行时拉起桥接进程。所以先发布桥接宿主：

```bash
# 在仓库根（Windows 侧）执行一次；产物落在 bridge-linux-x64/（已 gitignore）
dotnet publish dotnet/src/Starcloud.Bridge -c Release -r linux-x64 \
  --self-contained false -o bridge-linux-x64

cd flutter
# 需要中国镜像时（WSL 里已写入 /etc/profile.d/flutter.sh）
export FLUTTER_STORAGE_BASE_URL=https://storage.flutter-io.cn
export PUB_HOSTED_URL=https://pub.flutter-io.cn

flutter pub get
flutter run -d linux      # WSLg 下直接弹窗
flutter test              # 内核单元测试（离线，无窗口）
flutter analyze
```

桥接宿主路径默认按"安装布局 → 仓库开发布局"查找；也可用
`STARCLOUD_BRIDGE_DLL=/path/to/Starcloud.Bridge.dll` 直接指定。

## 后端复用：C# 写一次，其它端直接跑

```
┌──────────────── Flutter（Linux / macOS 共用）────────────────┐
│ shell / pages / plugins  ← 只依赖 contracts/ 里的抽象类       │
│            │                                                 │
│   starcloud.csharp_core（内置 engine 插件）                   │
│            │  stdio 行式 JSON（lib/bridge/core_bridge.dart）  │
└────────────┼─────────────────────────────────────────────────┘
             ▼
   dotnet/src/Starcloud.Bridge（net8.0，复用 Starcloud.Core）
   DownloadService / ManifestService / InstallerService /
   JavaService / GameLauncher / AccountStore / LauncherSettings
```

- 下载、版本清单、依赖合并、natives、assets、Java 探测、启动参数、
  进程管理**全部在 C# 侧**；Dart 侧只做 DTO 映射与事件转发。
- 数据根经 `STARCLOUD_DATA_DIR` 注入，C# 与 Flutter 共享同一份
  `launcher.settings.json` / 缓存 / 账户，不会出现"UI 改了设置、后端还按旧值下载"。
- 协议：请求 `{"id","method","params"}`、响应 `{"id","ok","result|error"}`、
  事件 `{"event","params"}`（`install.progress` / `install.done` /
  `game.output` / `game.exited` …）。安装是"先回执已受理、进度走事件"的长任务。
- 端到端脚本（不经界面）：`dart run tool/bridge_e2e.dart all --version 1.20.4`。

## 分层（规范 §12 的落地）

```
lib/
  kernel/     内核：零业务词。描述符/状态机/契约注册表/事件总线/贡献点注册表/
              调度器抽象/路径三根/插件上下文/插件宿主
  contracts/  宿主契约定义（按能力切、纯抽象类）
  bridge/     C# 桥接进程的 Dart 客户端（进程/请求关联/事件流）
  plugins/
    builtin/  内置插件（编译期登记 —— 规范 §0.3 决策①）
    external/ 外部（沙箱）插件宿主 —— 基座期占位，未实现
  shell/      外壳：Fluent 主题、导航、页面宿主、插件诊断页
```

**"万物皆插件"**：壳的导航项**不是硬编码**，由内置插件 `starcloud.shell_pages`
经贡献点注册表挂上来；壳只消费贡献点表。导航最后的"插件"诊断页实时展示每个
插件的状态 / 激活耗时 / 贡献数 / 错误（规范 §4.1 的可观测性要求）。

### 已声明的取舍（与规范的差异）

1. 内核允许 import Flutter（贡献点里的页面工厂是 `WidgetBuilder`）——本端就是
   Flutter 宿主；"引擎无关"由"内核零业务词、壳零硬编码导航"保证。
2. 契约键用 Dart `Type`（内置插件编译期登记）。外部插件 manifest 的契约名字符串
   将来由宿主做"名字 → Type"映射（随 v2 沙箱合并一起做）。

### 基座验收（2026-10-18 实测）

- `flutter analyze`：**No issues found**
- `flutter test`：**72 用例全绿**（覆盖规范 §13 可离线验证的全部条目：
  描述符校验 / 注册表语义 / 生命周期与激活次序 / 循环依赖 / 懒激活 /
  requires·depends 自动处理与反向级联 / essential / conflicts / 事件总线隔离 /
  贡献点增删与懒创建 / 三根路径 / 配置原子写与损坏容忍 / 离线账户持久化 /
  宿主退出逆序停用 / 游戏会话状态与防双开）
- WSLg `flutter build linux --debug` + 运行：窗口弹出，11 个导航项由
  `starcloud.shell_pages`（10 项）与 `starcloud.plugin_manager`（1 项）两个内置
  插件贡献；诊断页显示两者 Active。截图：[`docs/images/flutter-base-linux.png`](../docs/images/flutter-base-linux.png)
- 启动日志（WSLg 实测）：

  ```
  [kernel/info] 内核初始化（apiVersion 3.0）
  [kernel/info] 插件 starcloud.shell_pages 已激活（11ms，贡献 20，契约 0）
  [kernel/info] 插件 starcloud.plugin_manager 已激活（1ms，贡献 2，契约 0）
  ```

- 后端复用 E2E（WSL + BMCLAPI，`dart run tool/bridge_e2e.dart all`）：
  `starcloud.csharp_core` 激活 460ms / 登记 5 个契约；1.20.4 安装 3864 个文件
  （.minecraft 约 716MB，natives 7 个 .so 已解压）；Java 17 探测成功；启动后
  日志出现 `Setting user: StarcloudE2E` 与 `Backend library: LWJGL 3.3.2`。
  单文件下载实测 24,445,539 字节、39 条进度事件，末条 `done=true` 且落盘大小一致。
- 退出回收（SIGTERM 实测）：`内核已停止（逆序停用 8 个插件）`，随后
  `starcloud.csharp_core` 的 shutdown 杀掉桥接进程；无残留 `dotnet` 进程。

### 桥接健壮性（2026-10-18 加固）

- stdio 两端固定 UTF-8；Dart 侧 `allowMalformed`，一行坏字节不会判死整条协议流。
- 长请求（`download.*` / `java.list` / `launch.start`）在宿主侧并发执行，
  不再阻塞 `ping` / `game.kill` / 安装受理。
- 游戏进程退出事件做了"订阅前已退出"兜底，Dart 的 `exitCode` 不会永远悬挂。
- 父进程看门狗：Flutter 崩溃/热重启后，C# 桥接进程在 2 秒内自行退出。
- 安装事件 10 分钟无进度即判死，UI 不会无限转圈；下载源支持 `auto` 多源竞速。
- 单文件下载广播 `download.progress`（约 5Hz，`received` 为累计字节，
  `total=-1` 表示长度未知，结束时 `done=true`）。

### 已知项

- fluent_ui 在 Linux(GTK3) 的窗口装饰由 WM 提供，与 Windows 标题栏样式有差异。
- 内核跑在 Dart 单事件循环上，注册表"线程安全"由此保证；后台计算一律走注入的
  调度器 `spawn_background`（Isolate），不跨隔离共享可变状态（规范 §11.3）。
