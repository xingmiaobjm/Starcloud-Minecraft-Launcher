# 参考 PCL 的实现研究与落地记录

> 2026-11-01 起做的一次「对照成熟启动器」的工程复盘。只记录**设计思路与已知的兼容性事实**，
> 不含任何 PCL 源码摘录。许可证边界见文首。

## 0. 许可证边界（先看这个）

PCL 存储库**不是开源许可**，是《PCL 分发有限许可》。它把使用情形分成两档：

| 档位 | 判定 | 要求 |
| --- | --- | --- |
| 轻度使用 | 参考**极小部分**内容，或产品与 PCL 实质功能**无重叠** | 署名 |
| 重度使用 | 基于本库修改；或参考**较大量**内容以重新实现启动 / 下载 Java 版 Minecraft / 下载管理 Mod 等实质功能 | 声明为第三方二次创作、**产品名须以 `Plain Craft Launcher (PCL)` 开头**、关于页首位署名并开源、主色相须为蓝色、附带正版购买劝导 |

Starcloud Launcher 与 PCL 的实质功能**完全重叠**（都是 MC 启动器），因此我们只能在
**轻度使用**范围内工作：

- ✅ 阅读源码，理解其处理了哪些边界情况（思想与事实）
- ✅ 按公开的兼容性结论独立实现（如 Java 版本上下界、classpath 顺序规则）
- ✅ 在文档里给出署名
- ❌ 复制代码、复制文件结构、按模块逐一移植

本文件登记参考范围，供后续维护者判断边界。署名见文末。

---

## 1. 已经落地到本项目的改进

### 1.1 版本合并：子版本库必须排在父版本之前

**问题**：`MergeVersions` 原来是 `parent.Libraries.AddRange(child.Libraries)`——父库在前。

**为什么错**：Java 8 及更早版本按 `-cp` 顺序取**第一个**同名类。加载器（Forge / OptiFine /
Cleanroom / lwjgl3ify）正是靠"自己的 jar 覆盖原版类"生效的。父库在前 → 加载到原版类 →
表现为「找不到主类」「tweak 不生效」「混淆方法不存在」。

**改动**：`InstallerService.MergeVersions` 改为 `InsertRange(0, child.Libraries)`。
测试：`LaunchArgumentsTests.MergeVersionsOrderTests`（三层继承也验证了顺序）。

### 1.2 启动参数去重：重复参数是"两个一起失效"

**问题**：多层继承 + 用户自定义参数 + 插件参数叠加后，同一个键可能出现多次
（两份 `--width`、两份 `--uuid`）。

**关键事实**：重复的启动参数不是"后者生效"，而是**两个都失效**——关键参数会让游戏起不来，
且日志里完全看不出异常。

**改动**：新增 `LaunchArgumentSanitizer`：

| 参数形态 | 重复时的处理 |
| --- | --- |
| 单参数（`-XX:+UseG1GC`） | 丢弃后面的 |
| 游戏参数键值对（`--width 1920`） | **后值覆盖前值**（子层想覆盖父层） |
| JVM 参数键值对（`-Xss 1M`） | 仅键、值都相同时去重（`-D` 属性允许同名不同值共存） |
| `--tweakClass` | 允许重复（OptiFine + Forge 要靠多个 tweak 一起生效） |

负数取值（`--xPos -50` 里的 `-50`）不会被误判成参数。
接入点：`GameLauncher.BuildArgsCore` 的返回处，所有启动路径共用。

### 1.3 Java 版本：只有下限是不够的，还要有上限

**关键事实**（社区长期验证的兼容边界）：

- 1.5.2 及更早：Java 9+ 的类文件版本与旧 ASM 不兼容 → 最高 Java 8
- 1.8 – 1.11 + OptiFine：必须**恰好** Java 8
- 1.12 + OptiFine：最高 Java 8

**问题**：我们只算了"最低 Java"，用户给 1.12.2 + OptiFine 配 Java 17 时，
崩溃信息是 `Unsupported class file major version 61`——对用户毫无指向性。

**改动**：`JavaRequirement.MaxSupported / TooNewWarning`；环境体检里按**实际要用的
Java**（显式指定优先，否则取满足下限的最新运行时）核一次上限，超了给可执行提示
（"请换 Java 8"而不是一句技术错误）。

### 1.4 版本继承链防护

**问题**：`LoadVersionAsync` 递归加载父版本，遇到自引用（`inheritsFrom` 写成自己）
或循环（A→B→A）会**栈溢出直接杀进程**，日志里没有任何线索。

**改动**：传递"已走过的 id 集合"，重复即报「继承关系出现循环」；
链路超过 8 层报"继承链过深，疑似版本 JSON 异常"（8 层足够覆盖
lwjgl3ify → forge → vanilla 这类真实结构）。

### 1.5 新增 MCBBS 整合包支持（国内主流格式）

**格式事实**：

- 标志文件：`mcbbs.packmeta`，或带 `addons` 数组的 `manifest.json`
  （**与 CurseForge 同名**，只能靠有没有 `addons` 区分）
- `addons` 是 `[{id, version}]`：`id` 可取 `game`（必需）/`forge`/`neoforge`/`fabric`/`quilt`/`optifine`
- 内容在 `overrides/` 下；清单文件本身不该出现在玩家游戏目录
- `launchInfo.javaArgument` / `launchInfo.launchArgument`：整合包自带的启动参数

**改动**：`ModPackFormat.Mcbbs` + `LooksLikeMcbbs` + `ReadMcbbsMeta`（把 addons
归一成依赖表用的 `fabric-loader` 等键）+ 安装走 `InstallMcbbsAsync`
（有 `overrides/` 只铺它，没有就整包铺）。测试：`McbbsPackTests`。

---

## 2. 观察到但**没有**落地的点（附判断）

| 观察 | 为什么没做 |
| --- | --- |
| 游戏路径含 `!` / `;` 直接拒绝启动 | 理论上 classpath 分隔符与 jar URL 会冲突；但现代启动器普遍用 `-cp` 单引号路径 + 我们走 `ArgumentList`，实际风险低。**加检查会误伤合法路径**，先观察真机反馈 |
| 用 `@argfile` 传 JVM 参数（JDK-8272352 命令行编码问题） | 中文路径/玩家名在 Windows 命令行可能乱码。我们已用 `ProcessStartInfo.ArgumentList`（不拼字符串），风险显著降低；真要踩再说 |
| 总把 `--width/--height` 追加到末尾再靠去重覆盖 | 我们已经是显式追加 + 现在有了去重，语义等价 |
| OptiFine + Forge 的 tweakClass 顺序修正 | 我们目前不走"改写用户版本 JSON"这条路；`--tweakClass` 已允许重复，顺序问题等到真机复现再处理 |
| 整合包支持 MCBBS 之外的 `LauncherPack` / 纯压缩包泛化识别 | 与我们 Raw 检测有重叠，收益不明显 |

---

## 3. 对照表：我们能从"它处理了什么"学到什么

不是代码对照，而是**边界清单**——成熟启动器的价值主要在"踩过的坑都写成了判断"。

| 领域 | PCL 显式处理的边界 | 我们的状态 |
| --- | --- | --- |
| 版本合并 | 子库在前（classpath 顺序）| ✅ 已对齐（1.1）|
| 版本继承 | 自引用、循环、过深 | ✅ 已对齐（1.4）|
| 启动参数 | 单参数/键值对去重、游戏参数覆盖、tweakClass 例外、负数取值 | ✅ 已对齐（1.2）|
| Java | 上下界 + OptiFine 特例 | ✅ 已对齐（1.3）|
| 整合包 | CurseForge/Modrinth/HMCL/MMC/**MCBBS**/压缩包 | ✅ 现在也含 MCBBS（1.5）|
| 整合包 | OptiFine 单独拉取匹配 | ❌ 未做（需要 OptiFine 镜像列表）|
| 启动前 | 路径非法字符、版本状态、登录可用性 | ⚠️ 部分（登录已有，路径未查）|

---

## 4. 署名

本项目在实现上述改动时**参考了 PCL（Plain Craft Launcher）存储库公开的设计说明与
兼容性结论**，未复制其源代码。PCL 作者：龙腾猫跃（https://github.com/Meloong-Git/PCL）。

如后续需要引用更多内容，请先回到本文第 0 节确认档位——重度使用会触发
"产品名必须以 PCL 开头 + 主色相必须蓝色 + 开源"等强制要求，与本品定位冲突。
