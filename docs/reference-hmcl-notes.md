# 参考 HMCL 的功能盘点与差距分析

> 目的：了解一个成熟启动器**都实现了哪些功能**，用来评估本项目的功能完整度。
> 只记录功能清单与可观察到的行为边界，不含任何 HMCL 源码摘录。

## 0. 许可证边界

HMCL 是 **GPL-3.0-or-later**（`SPDX-License-Identifier: GPL-3.0-or-later WITH AdditionRef-HMCL`）。

- ✅ 阅读、学习设计、按公开事实独立实现
- ❌ 复制代码（GPL 传染，会把本项目整体变成 GPL 项目）

本文件只做功能盘点，不作为实现依据；真要参考某处，先确认是否只是"事实/常识"。

源码规模参考：Java 文件 990 个，分 `HMCL`（UI）/ `HMCLCore`（核心）/ `HMCLBoot`（引导）。

---

## 1. HMCL 的功能全貌（按模块）

### 1.1 游戏安装器（`HMCLCore/.../download/<loader>/`）

每种加载器一个独立安装任务：

| 安装器 | 说明 | 我们的状态 |
| --- | --- | --- |
| `game` | 原版 | ✅ |
| `fabric` | Fabric + FabricAPI | ✅ |
| `legacyfabric` | **Legacy Fabric**（1.8–1.13 时代的 Fabric）+ API | ❌ 完全没有 |
| `quilt` | Quilt + QuiltAPI | ✅ |
| `forge` | Forge，分 New/Old 两套安装链 | ✅（走官方 installer）|
| `neoforge` | NeoForge，同样分 New/Old | ✅ |
| `cleanroom` | **Cleanroom**（GTNH / GTO 用的特殊启动链） | ❌ 只能启动，不能自动安装 |
| `liteloader` | LiteLoader（1.5–1.12 时代） | ❌ 完全没有 |
| `optifine` | **OptiFine 独立安装**（从列表选版本 → 预处理 → 合并） | ⚠️ 仅有检测/提示，无安装流程 |
| `java` | **Java 运行时自动下载安装** | ⚠️ 有下载能力，需确认是否覆盖全部分发 |

### 1.2 整合包（`modpack/`）

| 格式 | 我们 |
| --- | --- |
| CurseForge | ✅ |
| Modrinth | ✅ |
| **MCBBS** | ✅（本轮新增）|
| HMCL | ✅ |
| ManuallyCreated（手工目录打包） | ✅（Raw 检测覆盖）|

额外能力：**OptionalFilesPage（可选文件勾选）** —— 安装时让用户选择跳过哪些可选 mod。我们目前没有这个交互。

### 1.3 实例与内容管理（`ui/instances/`）

| 功能 | 说明 | 我们 |
| --- | --- | --- |
| `GameInstancePage` | 版本详情（设置/mod/存档/光影…） | ✅ |
| `AddonUpdatesPage` | **已装 mod 批量检查更新 + 一键升级** | ⚠️ 只有单条更新检查，无批量入口 |
| `DataPackListPage` | 数据包列表管理 | ✅（存档维度）|
| `ScreenshotsPage` | **截图浏览/管理** | ⚠️ 仅版本详情页列举 |
| `GameInstanceIconDialog` | **自定义实例图标** | ❌ |
| `WorldListPage` / `WorldManagePage` / `WorldInfoPage` | 存档列表 / 管理 / 详情 | ✅ |
| `WorldBackupsPage` + `WorldBackupTask` | **存档定时/手动备份与回滚** | ⚠️ 只有"替换前备份"，无独立备份管理 |
| `WorldExportPage` | 存档导出分享 | ✅ |
| `GameSettingsPage` + `PresetManagementPane` | **游戏设置 + 跨实例预设复用** | ⚠️ 有版本设置，预设复用待确认 |

### 1.4 下载与源（`setting/DownloadProviders`、`HMCLCore/download/`）

- 多种下载源可切换（官方 / 镜像）
- **下载源可配置为"优先某源，失败自动换"**
- 我们已有：竞速池 + 健康度评分 + 官方镜像（SJTU / Starcloud S3 / Fastmirror / BMCLAPI）

### 1.5 启动（`launch/`）

HMCL 为启动失败定义了**一组可区分的异常类型**，这比"启动失败：xxx"有用得多：

| 异常 | 现实含义 | 我们 |
| --- | --- | --- |
| `CommandTooLongException` | Windows 命令行 > 32767 | ✅ 本轮补上（预估 + 中文提示）|
| `NotDecompressingNativesException` | natives 解压失败（占用/权限） | ⚠️ 无专门分类 |
| `PermissionException` | 脚本不可执行（Unix 权限位） | ➖ 仅 Windows |
| `ExecutionPolicyLimitException` | PowerShell 执行策略禁止运行脚本 | ➖ 不导出脚本 |
| `ProcessCreationException` | 进程创建失败（杀软/权限） | ⚠️ 无专门分类 |
| `ExitWaiter` | 退出码 + 日志尾部收口 | ✅（GameSession.Exited）|

另有：
- `CommandBuilder` + **导出启动脚本**（.bat / .ps1 / .sh）—— 我们无导出功能
- `ProcessPriority` 进程优先级设置 —— 我们无
- `QuickPlayOption / QuickPlayType` 快速进服 —— ✅（`--quickPlayMultiplayer`）

### 1.6 账户（`auth/`）

- 微软账户（设备码 / OAuth 回调，`OAuthServer`）
- Authlib-Injector 第三方登录（**服务器列表管理**）
- 离线账户
- HMCL 有 `AuthlibInjectorServerList`：多个皮肤站服务器可保存切换；我们目前是单服务器字段

### 1.7 其他

| 功能 | 我们 |
| --- | --- |
| `CrashReportAnalyzer` 崩溃日志关键字分析 | ✅ 有类似（Nova 冲突分析）|
| `Terracotta` 陶瓦联机集成 | ✅（我们是房间系统 + 陶瓦核心）|
| 自更新（`upgrade/`） | ✅（签名清单）|
| 主题/背景（`theme/`、`BackgroundType`） | ✅ |
| 字体管理（`FontManager`） | ⚠️ 有固定字体，无管理 |
| 多游戏目录（`GameDirectoryManager`） | ✅ |
| 启动器自身多实例保护 | ⚠️ 待确认 |

---

## 2. 差距优先级建议

按"用户实际会缺"排序：

| # | 缺口 | 为什么值得做 | 成本 |
| --- | --- | --- | --- |
| 1 | **Cleanroom 自动安装** | GTNH / GTO 用户必经；现在只能靠手动导入整合包 | 中（maven 版本 JSON 合并）|
| 2 | **OptiFine 独立安装** | 老版本玩家高频需求；现在只能当 mod 下载 | 中（版本列表 + 预处理）|
| 3 | **mod 批量更新** | 已装几十个 mod 时逐条点很痛苦 | 中（已有单条检查能力）|
| 4 | **存档备份管理** | 数据安全，用户最在意的损失 | 低-中（zip 基础设施已有）|
| 5 | **LegacyFabric / LiteLoader** | 老版本玩家群，量小但无替代 | 低（安装链与 Fabric 同构）|
| 6 | 实例自定义图标 | 纯体验 | 低 |
| 7 | 整合包可选文件勾选 | 安装体验 | 中 |
| 8 | 启动脚本导出 | 排障/高级用户 | 低 |
| 9 | Authlib 服务器列表（多皮肤站） | 多站用户 | 低 |
| 10 | 进程优先级设置 | 边玩边用电脑 | 低 |

---

## 3. 本轮已落地（来自这份盘点的直接产出）

- **命令行超长检测**：`GameLauncher.Launch` 在 Windows 上按"参数长度 + 引号开销"
  估算总长度，超过 32000 就抛出中文说明（原本会得到
  「文件名或扩展名太长」这种 Win32 206 错误，完全指不到原因）。
  大型整合包（几百个库）的 `-cp` 是真能撞上 32767 上限的。
- **LaunchWrapper 系实例的 Java 上限**（见下节 3.1）

前一轮（PCL 侧）已落地：库顺序、参数去重、Java 上下界、继承链防护、MCBBS 格式。

---

## 3.1 第二轮精读的发现（实现细节层）

这轮不看功能清单，只看「它显式处理了哪些边界」。

### 已落地：LaunchWrapper 只能 Java 8

`JavaVersionConstraint` 里有一条硬约束值得抄思路（不是抄代码）：**走 LaunchWrapper 的实例
（Forge ≤1.12 / LiteLoader / OptiFine）最高只支持 Java 8**——LaunchWrapper 假定系统类加载器
是 `URLClassLoader`，Java 9+ 的 `AppClassLoader` 不是，直接抛类型错误。

判定方式：`mainClass` 含 `launchwrapper` 且 MC ≤ 1.12。lwjgl3ify / Cleanroom 会把 mainClass
换成自己的 Bootstrap（`retrofuturabootstrap.Main` / Cleanroom 的 boot 类），所以不会误伤。

`1.12.2 + Java 17` 是高频误配（用户以为新 Java 更好），现在体检会给
「这个版本用的是 LaunchWrapper 启动器…只支持 Java 8」，而不是等它抛看不懂的类加载错误。

### 已确认我们做对了的

| 边界 | HMCL 的做法 | 我们的实现 |
| --- | --- | --- |
| 库去重 | 同 artifact：规则不同都保留；规则相同留高版本；版本相同且坐标不同都保留 | `DedupeLibrariesByArtifact` 规则一致 ✓ |
| natives 解压 | 按 `extract.exclude` 过滤；跳过 `.sha1`/`.git` | 有 exclude 过滤 ✓（我们只在安装时解压，不需要"同尺寸跳过"优化）|
| 版本 JSON 继承 | 自引用检测、循环检测 | 第二轮已补（含 8 层深度上限）✓ |

### 看到但**不做**的

| 机制 | 为什么不做 |
| --- | --- |
| 按 mainClass 分流做 tweakClass 重排（LaunchWrapper / ModLauncher / BootstrapLauncher） | 深度绑定 Forge+OptiFine+LiteLoader 的组合，还依赖 HMCL 自有的 transformer-discovery-service 组件；我们不走改写用户版本 JSON 的路线 |
| 崩溃日志规则表（已知崩溃特征的正则库） | 规则内容属于其经验积累，我们按需自建（Nova 已有冲突分析）|
| 下载源/Java 运行时安装计划（JavaInstallTask） | 我们已有 JavaService 下载能力 |
| Cleanroom 的 Java 版本按 Cleanroom 版本查表 | 我们目前按固定下限 21，够用；等有实际失败案例再细化 |

### 差距表里仍待做（按之前优先级）

Cleanroom 自动安装（**本轮已完成**）、OptiFine 独立安装、mod 批量更新、存档备份管理、
LegacyFabric/LiteLoader、实例自定义图标、整合包可选文件勾选。

可选文件的判定标准（事实，供后续实现）：
- Modrinth：`files[].env.client == "optional"`
- CurseForge：`files[].required == false`

---

## 4. 结论

HMCL 的功能完整度体现在三个地方，不一定需要全抄：

1. **安装器按加载器分治**：每种加载器一个独立任务，边界清晰（Cleanroom/LiteLoader/LegacyFabric 这类冷门加载器也各有实现）
2. **启动失败分类**：把系统级错误翻译成可行动提示（我们本轮补了最常见的一种）
3. **内容生命周期管理**：不只是"装进去"，还有"更新/备份/回滚/导出"

我们的优势在**联机房间系统**（HMCL 只是集成陶瓦，没有房间/大厅/整合包分发的完整链路）和**云控/自更新**，这两块 HMCL 没有对等物。
