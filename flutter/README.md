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
   DownloadService / ManifestService / InstallerService / JavaService /
   GameLauncher / SaveLibraryService / AccountStore / MicrosoftAuthService /
   YggdrasilService / LauncherSettings
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
- `flutter test`：**73 用例全绿**（覆盖规范 §13 可离线验证的全部条目：
  描述符校验 / 注册表语义 / 生命周期与激活次序 / 循环依赖 / 懒激活 /
  requires·depends 自动处理与反向级联 / essential / conflicts / 事件总线隔离 /
  贡献点增删与懒创建 / 三根路径 / 配置原子写与损坏容忍 / 宿主退出逆序停用 /
  游戏会话状态与防双开 / 版本页·版本详情·存档页·账户页·账户详情·联机页·Nova 页）
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
  `starcloud.csharp_core` 激活约 0.4~4s / 登记 12 个契约；1.20.4 安装 3864 个文件
  （.minecraft 约 716MB，natives 7 个 .so 已解压）；Java 17 探测成功；启动后
  日志出现 `Setting user: StarcloudE2E` 与 `Backend library: LWJGL 3.3.2`。
  单文件下载实测 24,445,539 字节、39 条进度事件，末条 `done=true` 且落盘大小一致。
- 账户 E2E（`dart run tool/bridge_e2e.dart accounts`）：离线账户增/选/删成功；
  微软设备码真实返回 `userCode` 与 `https://www.microsoft.com/link`；启动时按
  `accountUuid` 解析凭证（离线走 legacy 占位令牌）。第三方账户启动需要
  `authlib-injector`（默认从仓库 `Starcloud.App/Assets` 读取，可用
  `STARCLOUD_AGENT_JAR` 覆盖）。
- 版本管理 E2E（`dart run tool/bridge_e2e.dart versions`）：列表返回目录/大小/
  jar 存在性/默认/隔离/`inheritsFrom`；删除被依赖的父版本被拒绝；临时版本
  清理后列表与默认版本均正确。版本页已接真实实现（打开目录 / 设为默认 / 删除）。
- 存档 E2E（`dart run tool/bridge_e2e.dart saves`）：跨版本列表、备份 zip、
  删除均成功；越界 `gameRoot` 与 `../` 目录名被宿主拒绝。存档页已接真实实现
  （打开目录 / 备份 / 删除）。
- 联机 E2E（`dart run tool/bridge_e2e.dart multiplayer`）：提供方列表（陶瓦）、
  核心状态、阶段快照、玩家名/节点地区持久化均正确。联机页已接真实实现
  （安装核心 / 创建房间 / 加入房间 / 停止 / 日志）。
- 内容管理 E2E（`dart run tool/bridge_e2e.dart content`）：版本详情返回实例根/
  MC 版本/加载器；假模组创建→停用→启用→删除；Modrinth 搜索（Faithful 系列）
  并安装资源包到版本目录、随后清理。版本详情页已接真实实现
  （模组/资源包/光影 列表、启停、删除、Modrinth 搜索安装、必需依赖递归）。
- 版本增强 E2E（`dart run tool/bridge_e2e.dart version`）：实例设置保存/恢复
  （隔离、内存、窗口的生效值正确）；更新检查；装 Sodium Extra 后删掉 Sodium，
  依赖检查正确报告 `missing=Sodium`（`checked=2 resolved=2 inspected=2`）。
  顺带修掉 Modrinth `version_files` 哈希大小写不匹配导致身份反查永远为空的问题。
- 皮肤 E2E（`dart run tool/bridge_e2e.dart skins`）：导入 64×64 PNG → 指定给
  离线账户 → 型号覆盖 slim → 取消 → 删除，库与账户引用均正确清理。启动 E2E
  还会先挂一张本地皮肤，验证回环服务 + `--userProperties` 注入后游戏仍正常启动。
- Nova E2E（`dart run tool/bridge_e2e.dart nova`）：LLM 配置读写/清除；
  「诊断 1.20.4」走本地能力路由，流式返回 delta 与 done（无需模型）。
  Nova 页已接真实实现（流式对话、工具调用提示、自接模型配置）。
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

### 与 Windows 端的差距（持续收敛中）

- 已对齐：首页 / 下载 / 版本（含详情、内容管理、实例参数、更新与依赖检查）/
  存档 / 联机 / 运行状态 / 账户（含详情与皮肤）/ Nova / 启动器日志 / 设置。
- Nova 最小形态：流式对话、自接 OpenAI 兼容模型、本地能力路由（诊断/冲突/
  联机指引）与**只读工具**；写操作确认清单暂不执行（`NovaToolRegistry`
  的 `readOnlyOnly` 开关，后续补确认清单 UI）。
- 未移植：实验室（deepslate 投影渲染）、插件市场
  （外部沙箱插件按 spec v3 决策暂缓）。

### 已知项

- fluent_ui 在 Linux(GTK3) 的窗口装饰由 WM 提供，与 Windows 标题栏样式有差异。
- 内核跑在 Dart 单事件循环上，注册表"线程安全"由此保证；后台计算一律走注入的
  调度器 `spawn_background`（Isolate），不跨隔离共享可变状态（规范 §11.3）。
