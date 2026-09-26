# Starcloud 启动器插件开发规范

> **文档声明（重要，位于规范头部）**
> 插件是第三方独立开发的扩展，插件开发者独立承担插件全部法律责任。
> 启动器本体不提供、不支持、不接管任何账号身份验证功能：插件拿不到任何令牌，
> 也不能替用户完成或篡改登录。第三方插件行为与启动器本体开发主体无关。

版本：1.2 ｜ API 版本：`1.0` ｜ 适用：Starcloud Minecraft Launcher 及其他采用本规范的衍生版本（版本隔离约定见 §10）。
配套文档：[plugin-sdk.md](plugin-sdk.md)（SDK 速查、JS / Python 对照表、逐能力示例）；
设计说明：[designs/2026-09-23-plugin-sdk-expansion-design.md](designs/2026-09-23-plugin-sdk-expansion-design.md)（本轮能力扩展的取舍与实现口径）；
示例插件：`examples/plugins/hello-panel/`（最小可用）、`examples/plugins/instance-report/`（本轮扩展能力面）。

> **1.2 变更摘要（只增不改不删）**
> · 权限白名单从 4 项扩到 **24 项**，覆盖版本 / 实例 / 游戏进程 / 社区资源 / 下载 / 网络 /
>   设置 / 账户（只读）/ 日志 / 任务中心 / 存档与备份 / 联机 / 外观 / 更新查询 / 存储 / 系统 / 事件。
> · `ctx` 新增 17 个成员（§7.3 - §7.17），全部按权限装配，未声明即 `null`（JS `undefined`）。
> · 新增生命周期钩子 `onLauncherEvent(ctx)`（§6），配合 `ctx.events` 订阅宿主事件。
> · Python 侧新增 `host_call` 回调通道，能力面与 JS **完全对齐**（§2.3）。
> · 仍然**不开放**：账号验证 / 认证接管 / UUID 覆写（黑名单，§5.2），插件自管理、启动器自更新安装、
>   账户写操作（§5.4 说明）。

---

## 1. 总则

- 插件是运行在启动器内的**第三方扩展**，只能通过本规范声明的接口与启动器交互。
- 插件能力采用**白名单制**：接口层面不存在的钩子（如账号认证、UUID 覆写）任何插件都无法触达，从源头锁死。
- 插件开发者需自行保证插件内容合法、安全、不侵犯他人权益；启动器本体不对插件内容与行为负责。
- 加载非官方插件前，用户需逐个（id + 版本）手动确认免责声明，确认记录持久化于 `plugins/plugin-state.json`。

## 2. 插件形态与运行时

| 入口脚本 | 运行时 | 约束 |
| --- | --- | --- |
| `main.js` | 进程内 Jint 引擎（严格模式） | 内存上限 48MB / 单次执行 5s 超时 / 递归深度 256 |
| `main.py` | 进程外 `python.exe` 子进程 | 需本机 Python 3.8+；行式 JSON 协议（宿主 SDK：`Assets/pluginsdk/starcloud_host.py`） |

- 两种入口**二选一**，同时存在时优先 `main.js`。
- JS 运行在进程内沙箱，**无任何宿主外 IO / 网络能力**，仅可触达 ctx 注入的白名单对象。
- Python 运行在进程外，子进程无法直接触达启动器内存；参数 / 通知 / 面板经协议**声明式上报**，
  其余 ctx 能力经 `host_call` 回调（由本体做同一套权限校验）后落地。
- 钩子未定义 = 空操作；钩子抛异常不会拖垮启动器：加载失败则拒绝启用该插件，`beforeGameLaunch` 失败则跳过该插件本次注入并记录日志。

### 2.1 进程内 JS 沙箱

- 引擎为 Jint（严格模式），`limitMemory 48MB` / `timeoutInterval 5s` / `limitRecursion 256`。
- 「单次执行 5s 超时」是**单个钩子调用**的上限：`onLoad` 里做长时间轮询会导致加载失败。
- 钩子内可以做网络与磁盘动作（走 §7.6 / §7.7 的能力），但会阻塞所在线程：
  `onLoad` 在启动期加载线程上跑，`beforeGameLaunch` 在启动线程上跑，`onLauncherEvent` 在后台线程上跑。
  **拿不准就别在钩子里做重活**（规则见 §11.1）。

### 2.2 为什么 Python 插件要有宿主 SDK 文件

`starcloud_host.py` 由启动器随包分发（`Assets/pluginsdk/`），插件**不要**把它拷进自己的包。
它负责：编码统一为 UTF-8、把 `ctx.*` 调用翻译成 `host_call` 报文、把 `logs` / `panels` /
`notifications` / `append` 收集成响应载荷。插件只写 `main.py`。

### 2.3 host_call 协议（Python 专用，能力面与 JS 对齐）

```
插件 → 宿主  {"op":"host_call","id":7,"api":"instances.list","args":[]}
宿主 → 插件  {"op":"host_reply","id":7,"ok":true,"result":[…]}
宿主 → 插件  {"op":"host_reply","id":7,"ok":false,"error":"插件未声明 launcher:instances_read 权限，无法读取实例列表"}
```

- `api` 名 = §7 里 ctx 成员的「域 + 方法」，方法名小驼峰：`instances.list`、`game.launch`、
  `settings.get`、`storage.set`、`accounts.current`、`system.openUrl`…
- `args` 是**位置参数数组**，按方法签名顺序绑定：`["k",1]` → `set("k", 1)`。
- 权限校验与 JS 完全同一处代码：未声明的权限返回 `ok:false` + 可读原因，宿主 SDK 抛 `PermissionError`。
- 回调只能在钩子执行期间发起（钩子外发起返回 `ok:false`），且必须在 15 秒内完成：
  超时按「插件无响应」处理。
- Python 侧 `ctx.files`（沙箱文件读写）也是通过 host_call 提供的，与 JS 行为一致。

## 3. 插件包结构

插件为单个 `.mclplug` 压缩包（zip 改后缀），解压根目录**必须**包含 `manifest.json`：

```
my-plugin.mclplug
├─ manifest.json      # 插件清单（必填，缺失直接拒绝加载）
├─ main.js / main.py  # 入口脚本（二选一）
├─ assets/            # 资源：图标、面板 HTML、静态文件
├─ modules/           # 子模块（自行 require / import 组织）
└─ data/              # 插件私有数据目录（由 ctx.files 读写，运行时自动创建）
```

打包约束：

- zip 条目禁止绝对路径与 `..` 穿越，违者拒绝安装。
- 原始安装包归档保留于 `%APPDATA%/starcloud-launcher/plugins/packs/`，便于审计。

## 4. manifest.json 规范

```json
{
  "id": "com.example.myplugin",
  "name": "插件名称",
  "version": "1.0.0",
  "apiVersion": "1.0",
  "author": "作者",
  "description": "简短描述",
  "permissions": [
    "launcher:game_arguments",
    "launcher:notification"
  ],
  "optionalPermissions": [],
  "compatibility": { "minLauncherVersion": "0.2.4" },
  "requires_official_ai": false,
  "signature": null
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | ✅ | 反域名格式（`^[a-zA-Z][A-Za-z0-9_]*(\.[A-Za-z0-9_]+)+$`），≤64 字符，全局唯一 |
| `name` / `version` / `author` | ✅ | 展示名 / 语义化版本（建议 `主.次.修订`）/ 作者 |
| `apiVersion` | ✅ | 必须等于启动器当前 API 版本（`1.0`），不匹配拒绝加载 |
| `description` | 建议 | 简短描述 |
| `permissions` | 建议 | 申请的权限（白名单制，未知权限拒绝，黑名单权限拒绝） |
| `optionalPermissions` | 可选 | 同样走白名单校验 |
| `compatibility.minLauncherVersion` | 可选 | 最低启动器版本（语义化数字段逐位比较），不满足拒绝加载 |
| `requires_official_ai` | 可选 | AI 模块标记，见 §10 |
| `signature` | 可选 | 官方签名，见 §9 |

### 命名规范

- `id`：反域名（`组织.插件名`），发布后**不可变更**——它是免责确认、数据目录、面板归属的唯一键。
- `version`：每次发布必须递增；同 id 新版本需用户重新确认免责声明。

## 5. 权限模型（白名单制）

### 5.1 白名单（允许声明）

共 24 项，全部为 `launcher:` 前缀。**未声明 = ctx 成员为 `null`（JS `undefined`），物理上不可调用**；
即使有人绕过装配直接调方法，方法入口还有第二道权限门会抛 `UnauthorizedAccessException`。

**基础（v1.0 起）**

| 权限 | 能力 |
| --- | --- |
| `launcher:game_arguments` | 在 `beforeGameLaunch` 中追加游戏 / JVM 参数（**仅增量**，见 §7.2） |
| `launcher:ui_panel` | 注册侧栏 UI 面板（WebView2 展示插件 HTML） |
| `launcher:notification` | 发送启动器内弹窗通知 |
| `launcher:file_access` | 读写插件自身 `data/` 目录（路径穿越直接拒绝） |

**版本与实例**

| 权限 | 能力 |
| --- | --- |
| `launcher:instances_read` | 读官方版本清单、已安装实例、实例详情与内容目录文件（§7.3 / §7.4） |
| `launcher:instances_write` | 装原版 / 装加载器 / 补全 / 改名 / 删除实例、改实例设置（§7.4） |

**游戏进程**

| 权限 | 能力 |
| --- | --- |
| `launcher:game_launch` | 按当前账户启动实例、查进程状态、结束进程（§7.5）。**身份由本体解析，插件拿不到也传不了凭证** |

**社区资源**

| 权限 | 能力 |
| --- | --- |
| `launcher:content_read` | 搜索 Modrinth / CurseForge、翻译搜索词、读资源库、检查资源更新（§7.6） |
| `launcher:content_write` | 安装 / 装载 / 删除资源、安装本地整合包（§7.6） |

**下载与网络**

| 权限 | 能力 |
| --- | --- |
| `launcher:download` | 用启动器多源下载引擎把文件下载到插件自身 `data/` 目录（§7.7） |
| `launcher:network` | 插件自己的 HTTP 请求（GET / POST，30 秒超时，仅 http/https）（§7.7） |

**设置 / 账户 / 日志**

| 权限 | 能力 |
| --- | --- |
| `launcher:settings_read` | 读启动器全局设置（主机私有键已过滤，§7.8） |
| `launcher:settings_write` | 改启动器全局设置（白名单字段，主机私有键一律拒绝，§7.8） |
| `launcher:accounts_read` | 读账户身份元数据：id / 名字 / 类型 / 是否默认。**永远没有令牌**（§7.10） |
| `launcher:logs_read` | 读启动器日志、游戏日志、插件自身日志的尾部（§7.11） |

**存档、联机、外观、更新**

| 权限 | 能力 |
| --- | --- |
| `launcher:saves_read` | 读存档清单与备份清单（§7.12） |
| `launcher:saves_write` | 备份 / 还原 / 删除实例与存档、导出整合包（§7.12） |
| `launcher:multiplayer` | 读联机状态、装联机核心、开房 / 加入 / 结束联机（§7.13） |
| `launcher:appearance` | 改主题 / 背景模式 / 背景图 / 遮罩透明度 / 模糊半径（§7.14） |
| `launcher:updates` | 查询启动器是否有新版本（只查不装，§7.15） |

**宿主交互与数据**

| 权限 | 能力 |
| --- | --- |
| `launcher:events` | 订阅 `game.launched` / `game.exited` / `launcher.ready`，由 `onLauncherEvent` 接收（§7.17） |
| `launcher:clipboard` | 读 / 写系统剪贴板（§7.16） |
| `launcher:shell` | 用默认浏览器打开链接、在资源管理器里打开插件 data 目录（§7.16） |
| `launcher:storage` | 插件私有键值存储（`data/storage.json`，跨启动保留，§7.9） |
| `launcher:tasks` | 读任务中心（下载 / 安装进度）、取消任务、清空记录（§7.11） |

### 5.2 黑名单（出现即拒绝加载，无申请通道）

- `launcher:account_verify` —— 账号身份校验
- `launcher:auth_override` —— 认证流程接管
- `launcher:uuid_override` —— UUID 覆写

插件不能接管、篡改、替换账号身份校验逻辑；本体不暴露对应钩子，接口层面锁死。

同一道防线还包含：`beforeGameLaunch` 追加的身份类参数（`--username` / `--uuid` / `--accessToken` /
`--userType` / `--userProperties` / `--clientId` / `--xuid` / `--authlib-injector` / `--authServer`）会被
直接丢弃并记日志（见 §7.2）。

### 5.3 不开放的能力（未列入白名单，声明即被拒）

| 未开放 | 理由 |
| --- | --- |
| 插件管理（启用 / 停用 / 安装 / 卸载其他插件） | 插件不应决定同类的生死：否则一个插件就能停掉用户依赖的插件，或把自己伪装成"被信任的那一份" |
| 启动器自更新安装 | 更新包会覆盖本体二进制，只能由用户在设置页手动触发 |
| 账户写操作（移除账户 / 改默认账户） | 身份相关操作一律留在本体界面，插件只读 |

> 想扩展这三类能力，正确路径是提升本规范版本并在本体里实现，而不是插件侧绕。

### 5.4 最小权限原则

- 只申请实际用到的权限；未声明权限对应的 ctx 成员为 `null`（JS `undefined`），物理上不可调用。
- 允许零权限插件（仅具备 `ctx.logger` 日志能力）。
- 用户安装 / 启用插件时会看到完整权限清单；**权限是插件对用户的第一份承诺**，
  申请了却不用会在后续审计里被视为可疑。

## 6. 生命周期

```javascript
function onLoad(ctx) {}            // 插件加载后调用（注册面板 / 初始化在此进行）
function onUnload(ctx) {}          // 插件停用 / 启动器退出前调用（清理在此进行）
function beforeGameLaunch(ctx) {   // 每次游戏启动前调用（仅声明 game_arguments 权限且已启用的插件）
    ctx.args.append("-Xmx4G");
}
function onLauncherEvent(ctx) {    // 订阅过的宿主事件到达时调用（仅声明 events 权限的插件）
    // ctx.event = { name: "game.exited", data: "{\"versionId\":\"1.21.1\"}" }
}
```

调用时序：

1. 用户启用插件 → 校验 manifest → 选择运行时 → `onLoad`。
2. 游戏启动前 → 依声明权限逐个调用 `beforeGameLaunch`，收集参数增量合并进命令行。
3. 所有已启用插件加载完毕 → 广播 `launcher.ready`（订阅者此时才收到，保证"别人也在了"）。
4. 游戏进程起 / 停（所有启动入口都算：主页按钮、版本页、Nova 工具、插件自己）→
   广播 `game.launched` / `game.exited` 给订阅者。
5. 用户停用 / 卸载 / 退出 → `onUnload` → 注销面板与订阅 → 释放运行时。

**注意：`onLoad` / `onUnload` 阶段 `ctx.args` 为 `null`**（参数管理器仅在 `beforeGameLaunch` 钩子提供）；面板注册、通知等其余能力在所有钩子中均可用。

`onLauncherEvent` 的调用约定：

- 只在**已订阅**该事件名时调用（`ctx.events.on(...)` 在 `onLoad` 里做即可）。
- 事件载荷在 `ctx.event`：JS 侧 `ctx.event.name` / `ctx.event.data`；Python 侧 `ctx.event["name"]` / `ctx.event["data"]`。
  `data` 是 JSON **字符串**（需要字段就自己 `JSON.parse` / `json.loads`），这样 JS 与 Python 语义一致。
- 事件在**后台线程**上投递，且同一插件的钩子不会并发执行；单个插件抛异常只记日志，不影响其他插件与界面。
- 事件不排队、不重放：插件加载前发生的事收不到（需要历史就用 §7.11 / §7.12 主动查询）。

## 7. ctx 上下文 API

### 7.1 成员总览

| 成员 | 权限要求 | 说明 | 章节 |
| --- | --- | --- | --- |
| `ctx.launcher` | 无 | 只读 `{ version, apiVersion, officialBuild }` | §7.1 |
| `ctx.logger` | 无 | `info` / `warn` / `error`，写入插件独立日志 | §7.1 |
| `ctx.args` | `game_arguments` | 启动参数管理器，**仅 `beforeGameLaunch` 提供** | §7.2 |
| `ctx.files` | `file_access` | `read` / `write` / `exists` / `list`，root 锁定插件自身 `data/` | §7.2 |
| `ctx.versions` | `instances_read` | 官方版本清单 / 已安装实例 | §7.3 |
| `ctx.instances` | `instances_read` / `instances_write` | 实例详情与安装维护 | §7.4 |
| `ctx.game` | `game_launch` | 启动 / 状态 / 结束游戏进程 | §7.5 |
| `ctx.content` | `content_read` / `content_write` | Modrinth / CurseForge 资源 | §7.6 |
| `ctx.download` | `download` | 走多源下载引擎下载到插件 data 目录 | §7.7 |
| `ctx.http` | `network` | 插件自己的 HTTP 请求 | §7.7 |
| `ctx.settings` | `settings_read` / `settings_write` | 启动器全局设置 | §7.8 |
| `ctx.storage` | `storage` | 插件私有键值存储 | §7.9 |
| `ctx.accounts` | `accounts_read` | 账户身份只读（无令牌） | §7.10 |
| `ctx.logs` | `logs_read` | 启动器 / 游戏 / 插件日志尾部 | §7.11 |
| `ctx.tasks` | `tasks` | 任务中心（下载 / 安装进度） | §7.11 |
| `ctx.saves` | `saves_read` / `saves_write` | 存档与备份 | §7.12 |
| `ctx.multiplayer` | `multiplayer` | 联机（陶瓦 / 第三方 SDK） | §7.13 |
| `ctx.appearance` | `appearance` | 主题 / 背景 / 毛玻璃 | §7.14 |
| `ctx.updates` | `updates` | 启动器更新查询 | §7.15 |
| `ctx.system` | `clipboard` / `shell` / `ui_panel` | 剪贴板 / 开链接 / 开目录 / 确认框 / 开页 | §7.16 |
| `ctx.ui` | `ui_panel` / `notification` | 面板 / 挂载区 / 通知 | §7.18 |
| `ctx.events` | `events` | 事件订阅 | §7.17 |
| `ctx.event` | `events` | 本次事件载荷（仅 `onLauncherEvent` 内非空） | §7.17 |

返回值约定：

- **结构化数据**统一是「字典 / 数组」：JS 侧直接 `ctx.instances.list()[0].id`；
  Python 侧是 `dict` / `list`（如 `ctx.instances.list()[0]["id"]`）。
- **文本结果**（搜索、清单、诊断类）是启动器生成的字符串，直接 `print` / `logger.info` 给用户看即可。
- **写操作**统一返回 `{ ok, message }`：`ok=false` 表示宿主拒绝了这次操作（原因在 `message`）。
- 所有能抛异常的地方都抛：权限不足 `UnauthorizedAccessException`（Python 里表现为 `PermissionError`）、
  参数不合法 `ArgumentException`、宿主不支持该能力 `InvalidOperationException`。

### 7.2 ctx.args（仅增量，无覆写）

| 方法 | 行为 |
| --- | --- |
| `append(arg)` / `appendGame(arg)` | 追加游戏参数 |
| `appendJvm(arg)` | 追加 JVM 参数 |
| `removeKey(key)` | 移除**插件自己此前追加**的参数（含成对取值），对本体参数无效 |

安全不变量：

- **本体核心参数物理不在增量管理器内**，插件无法读取、修改、移除本体参数。
- 追加的参数在入口处做**身份参数黑名单**比对（忽略大小写，`--key value` 与 `--key=value` 两种形式均拦截），
  命中即丢弃并记录日志：`--username` / `--uuid` / `--accessToken` / `--userType` / `--userProperties` /
  `--clientId` / `--clientid` / `--xuid` / `--authlib-injector` / `--authServer` / `--authserver`。
- 接口不存在全量覆写方法（无 `overwrite` / `setAll`）。

### 7.2.1 ctx.files（受限文件访问）

- root 锁定在插件解压目录的 `data/` 子目录；绝对路径与 `..` 穿越抛 `UnauthorizedAccessException`。
- `read(name)` 文件不存在返回 `null`；`write(name, text)` 自动创建父目录；`list()` 仅列 `data/` 顶层文件。
- 仅限文本内容。Python 侧经 `host_call` 走同一份实现，行为完全一致。

### 7.3 ctx.versions（launcher:instances_read）

| 方法 | 返回 | 说明 |
| --- | --- | --- |
| `list(includeSnapshots = false)` | 数组 | 官方版本清单：`{ id, type, releaseTime, installed }` |
| `latest()` | 对象 | `{ release, snapshot }` 官方最新正式版 / 快照版 |
| `installed()` | 数组 | 已安装实例：`{ id, sizeBytes }` |

```javascript
const releases = ctx.versions.list();           // 默认不含快照
ctx.logger.info("官方最新版：" + ctx.versions.latest().release);
const notInstalled = ctx.versions.list().filter(v => !v.installed);
```

### 7.4 ctx.instances（launcher:instances_read / instances_write）

| 方法 | 需要权限 | 返回 | 说明 |
| --- | --- | --- | --- |
| `list()` | read | 数组 | 实例清单：`{ id, sizeBytes, isolated, category, note, lastPlayedAt, running }` |
| `exists(versionId)` | read | 布尔 | 版本 JSON 与 client jar 是否都在 |
| `get(versionId)` | read | 对象 | 生效内存 / Java / 窗口 / 隔离 / 自动连服 + `mods` 文件清单 |
| `files(versionId, kind = "mods")` | read | 数组 | 内容目录文件：`{ name, sizeBytes }`（kind：`mods` / `resourcepacks` / `shaderpacks` / `saves` / `datapacks`） |
| `install(versionId)` | write | `{ok,message}` | 安装官方原版版本 |
| `installLoader(loader, mcVersion, build = null)` | write | `{ok,message}` | `forge` / `neoforge` / `fabric` / `quilt` |
| `repair(versionId)` | write | `{ok,message}` | 补全缺失 / 损坏文件 |
| `rename(versionId, newId)` | write | `{ok,message}` | 改名（目录与实例档案一起迁移） |
| `delete(versionId)` | write | `{ok,message}` | 删除实例（不可撤销） |
| `updateSettings(versionId, settingsJson)` | write | `{ok,message}` | 改实例覆盖设置（字段同 §7.8 的实例部分） |

```javascript
const mc = ctx.versions.list().filter(v => !v.installed)[0];
ctx.logger.info(ctx.instances.install(mc.id).message);
ctx.instances.updateSettings(mc.id, JSON.stringify({ memory_max_mb: 8192, isolation: true }));
```

`updateSettings` 可用字段：`memory_mode`（global / auto / manual）、`memory_min_mb`、`memory_max_mb`、
`java_mode`、`java_path`、`window_mode`、`window_width`、`window_height`、`fullscreen`、`isolation`、
`server_address`、`clear_server_address`、`language_override`、`extra_jvm_args`、`pre_launch_command`、
`post_launch_command`、`note`。

### 7.5 ctx.game（launcher:game_launch）

| 方法 | 返回 | 说明 |
| --- | --- | --- |
| `launch(versionId)` | `{ok,message}` | 启动实例；身份由本体解析，缺会话凭证时如实告知需要重新登录 |
| `status()` | 对象 | `{ running, versionId, pid, startedAt, uptimeSeconds }` |
| `isRunning()` | 布尔 | 是否有存活的游戏进程 |
| `kill()` | `{ok,message}` | 强制结束当前游戏进程（未保存进度会丢） |
| `log(versionId = null, lines = 40)` | 字符串 | 某实例最近的游戏日志尾部（需同时声明 `launcher:logs_read`） |

```javascript
const s = ctx.game.status();
if (!s.running) ctx.game.launch("1.21.1");
```

**身份不变量**：`launch` 没有 `username` / `uuid` / `token` 这类参数，
插件无法指定、伪造或旁观任何凭证；用哪个账户由启动器本体（§7.10 只读）决定。

### 7.6 ctx.content（launcher:content_read / content_write）

| 方法 | 需要权限 | 说明 |
| --- | --- | --- |
| `search(query, kind = "mod", source = "modrinth", gameVersion = null, loader = null)` | read | 搜索资源，返回可直接展示的文本清单（含项目 ID） |
| `translate(query)` | read | 中文 → 英文项目名（两个平台都只有英文名） |
| `library(kind = null, query = null)` | read | 本地资源库清单（返回 `file_name` 供 `enable` 用） |
| `checkUpdates(versionId = null)` | read | 扫描实例已装资源，列出可更新项 |
| `install(projectId, kind = "mod", source = "modrinth", versionNumber = null, targetVersion = null)` | write | 下载并装载到实例 |
| `enable(fileName, kind = "mod", targetVersion = null)` | write | 把资源库已有文件装载到实例（不重新下载） |
| `removeFromLibrary(fileName, kind = "mod")` | write | 从资源库删除（实例里的副本不受影响） |
| `installModpack(path, name = null, targetVersion = null)` | write | 安装本地 .mrpack / CurseForge 导出 zip |

`kind` 取值：`mod` / `resourcepack` / `shader` / `datapack` / `modpack`。

### 7.7 ctx.download 与 ctx.http

| 方法 | 权限 | 说明 |
| --- | --- | --- |
| `ctx.download.file(url, fileName, sha1 = null)` | `download` | 走启动器**多源下载引擎**（分片 / 竞速 / 重试）下载到插件 `data/<fileName>`，返回落盘绝对路径；`sha1` 非空时校验，不匹配抛错 |
| `ctx.download.text(url)` | `network` | 拉一段文本，返回正文 |
| `ctx.http.get(url)` | `network` | 返回 `{ status, ok, contentType, body }` |
| `ctx.http.postJson(url, jsonBody)` | `network` | 同上，`Content-Type: application/json` |
| `ctx.http.post(url, body, contentType = "text/plain")` | `network` | 任意正文 |

```javascript
const path = ctx.download.file("https://example.com/data.json", "data.json");
const r = ctx.http.get("https://api.example.com/status");
if (r.ok) ctx.logger.info(JSON.parse(r.body).version);
```

- 只允许 `http` / `https` 绝对地址；超时 30 秒；不做重定向限制之外的额外放行。
- **插件只能写到自己的 data 目录**：`download.file` 的 `fileName` 越界会被拒绝。

### 7.8 ctx.settings（launcher:settings_read / settings_write）

| 方法 | 权限 | 说明 |
| --- | --- | --- |
| `get()` | read | 返回设置字典 + `filtered`（被过滤掉的主机私有键名列表） |
| `set(settingsJson)` | write | JSON 对象，仅接受白名单键；越权键抛 `UnauthorizedAccessException` |

可读 / 可写的白名单字段（与 Nova 的 `update_settings` 完全同一批）：

`download_source`、`download_concurrency`、`download_max_retries`、`download_custom_mirror_url`、
`default_min_memory_mb`、`default_max_memory_mb`、`last_version`、`version_isolated`、`java_executable`、
`use_builtin_jvm_args`、`theme`、`language`、`ui_language`、`ui_animations`、`account_walk_animation`、
`auto_update_enabled`、`launch_auto_retry`、`show_game_log_on_launch`、`resource_cache_enabled`、
`microsoft_login_embedded`、`background_mode`、`background_builtin_id`、`background_image_path`、
`background_image_opacity`、`background_blur_radius`、`background_mask_opacity`、`background_follow_theme`、
`default_window_width`、`default_window_height`、`default_fullscreen`、`extra_jvm_args`、`global_env_vars`。

主机私有键（读不到、也写不了）：`nova_llm_api_key` / `nova_llm_base_url` / `nova_llm_model` /
`nova_llm_profiles` / `nova_llm_active_id` / `nova_gateway_enabled` / `nova_quota_*` /
`curseforge_api_key` / `update_manifest_url`。

> 这样设计是因为这些字段直通「模型密钥」与「更新源」：给插件就等于把用户的密钥与
> 更新通道交出去。插件要联网，用自己的 `ctx.http` 与自己的配置。

### 7.9 ctx.storage（launcher:storage）

插件私有键值存储，落在 `data/storage.json`，跨启动保留；键是**数据不是路径**（带 `../` 的键不会写到目录外）。

| 方法 | 说明 |
| --- | --- |
| `get(key)` | 读一个键；不存在返回 `null` |
| `set(key, value)` | 写一个键（值可以是字符串 / 数字 / 布尔 / 数组 / 对象） |
| `delete(key)` | 删除并返回此前是否存在 |
| `keys()` | 全部键名 |
| `clear()` | 清空 |

```javascript
const count = ctx.storage.get("launches") || 0;
ctx.storage.set("launches", count + 1);
```

### 7.10 ctx.accounts（launcher:accounts_read）

| 方法 | 返回 | 说明 |
| --- | --- | --- |
| `list()` | 数组 | `{ id, name, type, isDefault, server }`；`type` = `microsoft` / `external` / `offline` |
| `current()` | 对象 | `{ mode, name, type, ready, reason? }` 当前启动会用的身份 |

- **永远拿不到令牌**：accessToken / refreshToken / clientToken / xuid 都不在这份返回里。
- 会话内未登录时 `current()` 返回 `ready=false` 与原因（`reason`），插件应提示用户去账户页，
  而不能（也无法）代替用户登录。
- 离线账户也在列表里：`type=offline`、`id` 是名字派生的 UUID（与各启动器通用的
  `nameUUIDFromBytes("OfflinePlayer:<name>")` 一致），仅用于单人 / 局域网。

### 7.11 ctx.logs 与 ctx.tasks

| 方法 | 权限 | 说明 |
| --- | --- | --- |
| `ctx.logs.launcher(lines = 100)` | `logs_read` | 启动器日志尾部（当天，跨天连前一天） |
| `ctx.logs.game(lines = 100)` | `logs_read` | 启动器保存的最新一局游戏日志 |
| `ctx.logs.plugin(lines = 100)` | 无（自身日志） | 插件自己的运行日志尾部 |
| `ctx.tasks.list()` | `tasks` | 任务快照：`{ id, title, state, progress, detail }` |
| `ctx.tasks.cancel(taskId = null)` | `tasks` | 取消一个任务；不传 id 取消全部进行中的 |
| `ctx.tasks.clear()` | `tasks` | 清空任务记录（先取消进行中的） |

行数上限 2000；读日志用的是「共享读」句柄，游戏正在写也不会报错。

### 7.12 ctx.saves（launcher:saves_read / saves_write）

| 方法 | 需要权限 | 说明 |
| --- | --- | --- |
| `list()` | read | 所有实例的存档清单（名称 / 所属实例 / 大小 / 上次游玩） |
| `backups(versionId = null)` | read | 备份记录清单（id 供还原 / 删除用） |
| `backupInstance(versionId, name = null, includeSaves = true)` | write | 备份实例，返回备份 id |
| `restore(backupId)` | write | 还原备份（覆盖同名文件） |
| `deleteBackup(backupId)` | write | 删除备份记录与 zip |
| `backup(saveName, versionId = null)` | write | 备份单个存档目录 |
| `delete(saveName, versionId = null)` | write | 删除单个存档目录 |
| `exportModpack(versionId = null, name = null)` | write | 导出 .mrpack，返回产物路径 |

### 7.13 ctx.multiplayer（launcher:multiplayer）

| 方法 | 说明 |
| --- | --- |
| `status()` | 联机状态：提供方 / 房间号 / 玩家数 / 节点地区 / 可用提供方（含第三方联机 SDK） |
| `installCore()` | 安装当前提供方的联机核心（首次联机前需要） |
| `host()` | 开房（先在游戏里「对局域网开放」），返回邀请码 |
| `join(room)` | 用邀请码加入房间 |
| `stop(idleOnly = false)` | 结束联机；`idleOnly=true` 只把核心切回空闲 |

### 7.14 ctx.appearance（launcher:appearance）

`set(theme = null, backgroundMode = null, backgroundImagePath = null, opacity = null, blurRadius = null)`：
只传要改的项。`theme` = `light` / `dark` / `system`；`backgroundMode` = `none` / `builtin` / `file`；
`opacity` 0.2-1；`blurRadius` 0-64。

```javascript
ctx.appearance.set("dark", null, null, 0.7, 24); // 跟随插件自己的视觉风格切换主题与毛玻璃
```

### 7.15 ctx.updates（launcher:updates）

`check()` —— 返回远端版本号与更新说明文本。**只查询**：安装更新仍然只能由用户在设置页触发。

### 7.16 ctx.system（launcher:clipboard / shell / ui_panel）

| 方法 | 权限 | 说明 |
| --- | --- | --- |
| `clipboard()` | `clipboard` | 读剪贴板文本（空返回 `null`） |
| `copy(text)` | `clipboard` | 写剪贴板 |
| `openUrl(url)` | `shell` | 用默认浏览器打开 http / https 链接 |
| `openFolder(relative = "")` | `shell` | 在资源管理器里打开插件 data 目录（或其中子目录） |
| `confirm(title, message)` | `ui_panel` | 弹确认框，用户点确认返回 `true`，取消返回 `false` |
| `alert(title, message)` | `ui_panel` | 弹提示框（只有一个按钮） |
| `openPage(page)` | `ui_panel` | 切到 `home` / `versions` / `download` / `accounts` / `saves` / `logs` / `settings` / `nova` / `multiplayer` / `plugins` |
| `version()` | 无 | 启动器版本号 |

```javascript
if (ctx.system.confirm("清空缓存", "将删除插件自己下载的缓存文件，继续？")) {
    ctx.storage.clear();
}
```

确认框在宿主 UI 线程上弹（插件钩子可以在任意线程），用户停留时间不计入插件超时；
宿主没有界面（无头运行）时 `confirm` 返回 `false`——插件要按「未确认」处理，不要当成确认。

### 7.17 ctx.events（launcher:events）

| 方法 | 说明 |
| --- | --- |
| `on(eventName)` | 订阅事件（重复订阅幂等） |
| `off(eventName)` | 取消订阅 |
| `subscribed()` | 当前已订阅的事件名 |
| `current()` | 本次事件载荷 `{ name, data }`（仅 `onLauncherEvent` 内非空） |

事件与载荷：

| 事件 | 载荷 | 时机 |
| --- | --- | --- |
| `launcher.ready` | `{ version, plugins }` | 所有已启用插件加载完毕 |
| `game.launched` | `{ versionId, pid }` | 游戏进程拉起后（任何入口都覆盖） |
| `game.exited` | `{ versionId }` | 游戏进程结束 |

```javascript
function onLoad(ctx) { ctx.events.on("game.exited"); }
function onLauncherEvent(ctx) {
    const data = JSON.parse(ctx.event.data);
    ctx.logger.info(`${ctx.event.name}：${data.versionId}`);
}
```

未知事件名 `on()` 会抛 `ArgumentException`；事件在后台线程投递，同一插件的钩子不会并发执行。

### 7.18 ctx.ui

```javascript
// 注册面板（launcher:ui_panel）：entry 必须是插件目录内相对路径（HTML）
ctx.ui.registerPanel("main", "我的面板", "assets/panel.html");

// 注册页面挂载区（launcher:ui_panel）：slotId 目前支持 download.top，height 48-320
ctx.ui.registerSlot("download.top", "assets/banner.html", 120);

// 弹窗通知（launcher:notification）
ctx.ui.notify("Hello", "参数注入完成");
```

- `registerPanel` 的 `entry` 拒绝绝对路径与 `..`；相同 `(panelId)` 重复注册为覆盖。
- 面板与挂载区由启动器内嵌 WebView2 以 `file://` 加载展示，仅支持静态 HTML / CSS / JS。
- 需要与用户确认、开页、剪贴板、打开链接时用 `ctx.system`（§7.16），不要在面板里伪造本体 UI。

- 挂载区在页面加载时挂载（`onLoad` 内注册即可），以独立 WebView2 `file://` 渲染，
  同样仅静态内容、不暴露宿主对象；插件卸载即移除其全部挂载区，启停切换后需重进页面生效。
- 未声明对应权限时 `ctx.ui` 为 `null`，或调用 `notify` 抛 `UnauthorizedAccessException`。

## 8. UI 面板行为规范

1. **禁止伪造本体 UI**：不得模仿启动器原生界面诱导用户输入账号、密码、令牌等任何凭据。
2. **禁止数据回传**：面板不得收集、上报用户身份信息；插件若需自建网络服务须在插件描述中明示。
3. **界面自洽**：面板窗口内自带样式，不得试图操作面板外的启动器 DOM。
4. 面板标题将展示插件归属，用户可随时在「插件」页停用插件（面板同步注销）。

## 9. 打包、签名与分发

### 9.1 打包

1. 目录压成 zip（根目录含 `manifest.json`）→ 改后缀 `.mclplug`。
2. 在启动器「插件」页安装；安装时校验 manifest、权限白名单、版本兼容性、zip 路径安全。

### 9.2 官方签名（可选）

- 签名对象：`manifest.json` 文件字节的 SHA256；算法：ECDSA P-256；编码：base64，填入 `signature` 字段。
- 启动器内置公钥验签通过 → 标记「官方」徽标。
- 无签名 / 验签失败 → 标记【第三方未验证插件】，**不阻断加载**，但启用时强制免责确认（每个 id+版本确认一次，持久化）。

### 9.3 分发

- 插件由开发者自行分发；官方渠道发布的插件可申请签名。
- 禁止插件修改启动器本体二进制（本体自更新带 SHA256 校验，见 self-update-server-guide.md）。

## 10. 版本兼容约定

- `apiVersion` 采用主版本号策略：`1.0` 期间只增不改不删；升级主版本（如 `2.0`）时旧插件拒绝加载并提示升级。
- `compatibility.minLauncherVersion` 按语义化数字段逐位比较（缺失段按 0），当前启动器版本低于要求则拒绝加载。
- AI 模块等仅官方国内发布版可用的插件标记 `requires_official_ai: true`；其他衍生版本加载时检测到该标记即拒绝启用并提示：**该模块仅官方国内发布版可用**。
- **能力新增**：新 ctx 成员、新事件、新权限都属于"只增"，老插件不受影响；老启动器遇到新权限会在
  manifest 校验阶段报"未知权限（白名单制）"并拒绝加载——这是有意的，避免插件在新能力上静默降级。
- 建议把 `minLauncherVersion` 设成"第一个提供自己所需能力的版本"（例如用到 `ctx.events` 就写
  `0.3.2`），这样老启动器会直接提示升级，而不是让插件在运行时才发现成员是 `undefined`。

## 11. 开发者行为准则

1. **性能**：钩子是同步执行的，`onLoad` 里做重活会拖慢启动（Jint 单次 5s 超时，超时即加载失败）。
   长任务请拆成"事件驱动 + 每次少量工作"：`onLauncherEvent` 里走一小步，别在钩子里轮询。
2. **写操作要有交代**：`instances.delete` / `saves.delete` / `saves.restore` 这类不可逆操作，
   先用 `ctx.system.confirm(...)` 让用户点头再执行；返回 `false` 就按"用户取消"处理。
3. **稳定**：钩子异常会导致插件功能缺失，请自行捕获可预期异常；`onUnload` 中完成清理（含 `ctx.events.off`）。
4. **隐私**：不收集、不回传用户账号信息；文件访问仅限插件自身 `data/` 目录；
   需要联网时用 `ctx.http`，并在插件描述里写清楚连到哪个域。
5. **合规**：插件内容须符合当地法律法规；禁止携带恶意代码、广告注入、挖矿等行为。
6. **命名**：面板 `panelId` 建议以插件功能命名（如 `main`、`settings`），避免与自身其他面板冲突（同名覆盖）。
7. **幂等**：用户"停用再启用"会重新走 `onLoad`；面板注册、事件订阅、存储写入都要能重复执行。

## 12. 调试与测试

- 插件日志：`%APPDATA%/starcloud-launcher/plugins/logs/<id>.log`。
- 启动器统一日志分类 `plugin` / `launch`（「启动器日志」页可见身份参数拦截与事件投递记录）。
- 插件也能读日志：`ctx.logs.launcher(200)` 是排查"我的调用到底有没有发生"最直接的办法。
- 本地迭代：修改代码 → 重新打包 `.mclplug` → 在「插件」页停用再启用（重新走 `onLoad`）。
- 示例插件：`examples/plugins/hello-panel/`（追加 JVM 参数 + 通知 + 面板）与
  `examples/plugins/instance-report/`（读实例 / 读身份 / 任务中心 / 事件订阅 / 私有存储），
  都可直接打包安装验证环境。

### 12.1 自测清单

1. **权限对照**：manifest 声明的每一项权限都真的用到了；代码用到的每一项也都声明了。
2. **无权限路径**：删掉某项权限后插件应优雅降级（成员为 `null` / `undefined`），而不是崩在 `onLoad`。
3. **事件**：`game.launched` 与 `game.exited` 各触发一次（启动一局游戏再关掉即可验证）。
4. **写操作**：删除 / 覆盖类动作先过 `ctx.system.confirm`，取消时不留半成品。
5. **存储**：`data/storage.json` 里不出现明文密钥（插件自己的凭据也应让用户填、用户自己保管）。

### 12.2 参考实现（本体侧，便于对照行为）

| 本体文件 | 内容 |
| --- | --- |
| `dotnet/src/Starcloud.Core/Plugins/PluginPermissions.cs` | 权限白名单 / 黑名单 |
| `dotnet/src/Starcloud.Core/Plugins/PluginApis.cs` / `PluginApis.Host.cs` | ctx 各成员与两道权限门 |
| `dotnet/src/Starcloud.Core/Plugins/PluginHost.cs` | 钩子分发、事件广播、`host_call` 分发 |
| `dotnet/src/Starcloud.Core/Plugins/PyPluginRuntime.cs` | Python 行式协议与能力回调 |
| `dotnet/src/Starcloud.App/Assets/pluginsdk/starcloud_host.py` | Python 侧 SDK |
| `dotnet/src/Starcloud.App/Services/PluginHostActions.cs` | 确认框 / 开页 / 剪贴板 / 打开链接 |
| `dotnet/tests/Starcloud.Core.Tests/PluginSdkTests.cs` | 能力层单测（权限门控 / 事件 / 存储 / 设置过滤） |
