# Starcloud 启动器插件规范 2.0（草案）

> **文档状态：草案（模型与校验层、依赖、声明式 UI 渲染已实现；async 运行时与 WASM 未实现）**
> 本文件描述下一代插件规范（API `2.0`）。**现行运行时仍是 [plugin-spec.md](plugin-spec.md)（v1.2 / API 1.0）**，
> 2.0 的**清单字段、细粒度权限模型、整包摘要与签名、依赖解析、声明式 UI 渲染**已落地
> （`PluginManifest` / `PluginPermissions` / `PluginVersionRange` / `PluginIntegrity` /
> `PluginDependencyResolver` / `PluginView` / `PluginViewRenderer`），因此 2.0 插件**可被识别、校验、安装并启用**。
> 仍未实现的部分：**async 钩子与预算、`ctx.tasks.spawn`、WASM 运行时、HTML 面板（`ui:html`）**；
> 相关能力在桥接时一律**不给**（fail-closed），启用时逐条写进插件日志，不会静默放大权限。
>
> **文档声明（与 1.x 完全一致，位于规范头部）**
> 插件是第三方独立开发的扩展，插件开发者独立承担插件全部法律责任。
> 启动器本体不提供、不支持、不接管任何账号身份验证功能：插件拿不到任何令牌，
> 也不能替用户完成或篡改登录。第三方插件行为与启动器本体开发主体无关。

版本：2.0（草案）｜ API 版本：`2.0` ｜ 配套：[plugin-sdk.md](plugin-sdk.md)（SDK 速查）、
[plugin-spec.md](plugin-spec.md)（1.x，现行有效）、示例：`examples/plugins/`

---

## 1. 2.0 要解决的四件事

| # | 1.x 的现状 | 2.0 的做法 | 章节 |
| --- | --- | --- | --- |
| 1 | 面板是**裸 HTML**（WebView2 + `file://`），只能靠 §8 的禁令防止「仿冒本体 UI 骗凭证」；面板与宿主**没有消息桥**，除静态展示外什么都做不了 | **声明式 UI**：宿主渲染的组件白名单 + 受权限校验的 `host_call` 消息桥；HTML 面板降级为需显式授权的 `ui:html` 模式，并强制外框归属标识 | §8 |
| 2 | 钩子**全同步**，单次硬超时 5s；长任务只能「事件驱动、每次一小步」；Python 依赖用户自装 Python | 钩子支持 **async + 预算（budget）**；长任务走 **`ctx.tasks.spawn`** 后台作业（可取消、进任务中心）；新增 **WASM/WASI** 运行时 | §5 |
| 3 | 24 项**粗粒度**权限：`network` 无主机限制、`instances_write` 含删除、`storage` 无配额、高风险操作只靠「开发者准则」自觉确认 | **细粒度能力**（带参数：主机、范围、配额）+ 风险分级 + 高风险**运行时二次确认** + **配额**与审计日志 | §6 |
| 4 | 签名只盖 `manifest.json` 字节 → **插件代码可被随意替换而签名依旧有效**；无插件间依赖、无更新检查 | **整包逐文件摘要**签名（签名对象含 `integrity`），插件间**依赖与版本区间**，插件**更新查询** | §9 |

**不变的三条红线**（与 1.x 一致，任何版本都不开口子）：

- 账号身份校验 / 认证流程接管 / UUID 覆写：接口层面不存在（黑名单，§6.4）。
- 插件拿不到任何令牌、不能替用户登录、不能决定「用哪个账户」。
- 插件不能管理其他插件（启用 / 停用 / 安装 / 卸载），不能安装启动器自更新。

---

## 2. 包结构

```
my-plugin.mclplug                 # zip（改后缀）
├─ manifest.json                  # 清单（必填）
├─ integrity.json                 # 整包摘要 + 签名（2.0 必填；由打包工具生成）
├─ main.js | main.py | main.wasm  # 入口（三选一，优先级 js > wasm > py，显式声明优先）
├─ assets/                        # 图标、样式、（ui:html 时）HTML
├─ modules/                       # 子模块
└─ data/                          # 插件私有数据（运行时创建，不入包）
```

- zip 条目禁止绝对路径与 `..` 穿越（沿用 1.x）。
- `integrity.json` 覆盖包内**其余全部文件**（含 `manifest.json`）；自身不入摘要。
- 打包工具：`tools/pack-plugin.ps1`（生成 `integrity.json`、可选 `-KeyFile` 签名、打包后自检；用法见 [plugin-sdk.md](plugin-sdk.md) §1）。
- 原始包归档于 `%APPDATA%/starcloud-launcher/plugins/packs/`（沿用 1.x）。

---

## 3. manifest.json 2.0

```json
{
  "id": "com.example.myplugin",
  "name": "My Plugin",
  "version": "2.1.0",
  "apiVersion": "2.0",
  "author": "Example",
  "description": "Short description",
  "i18n": {
    "zh-CN": { "name": "我的插件", "description": "简短描述" }
  },
  "license": "MIT",
  "homepage": "https://example.com/my-plugin",
  "repository": "https://github.com/example/my-plugin",
  "keywords": ["optimization", "ui"],
  "engines": { "starcloud": ">=0.4.0 <1.0.0" },
  "runtime": "js",
  "permissions": [
    "instances:read",
    "game:arguments",
    "network:host=api.example.com",
    "ui:panel",
    "storage:quota=2mb"
  ],
  "optionalPermissions": ["logs:read"],
  "dependencies": { "com.example.lib": "^1.2.0" },
  "conflicts": { "com.example.rival": "*" },
  "contributes": {
    "panels": [ { "id": "main", "title": "我的面板", "view": "panels/main.json" } ],
    "slots": [ { "point": "download.top", "view": "views/banner.json", "height": 120 } ],
    "settings": { "schema": "settings.schema.json" },
    "versionSections": [ { "id": "report", "title": "实例报告", "view": "views/report.json" } ],
    "homeCards": [ { "id": "hello", "title": "Hello", "view": "views/hello.json" } ],
    "navItems": [ { "id": "servers", "title": "服务器浏览", "icon": "server", "view": "views/servers.json" } ],
    "contextMenu": [ { "point": "version.row", "title": "导出报告", "action": "report.run" } ]
  },
  "updates": { "manifestUrl": "https://example.com/my-plugin/update.json" }
}
```

| 字段 | 必填 | 说明 |
| --- | --- | --- |
| `id` | ✅ | 反域名，≤64 字符，全局唯一，发布后不可变更（1.x 同） |
| `name` / `version` / `author` | ✅ | 展示名 / 语义化版本（2.0 要求严格 `主.次.修订`）/ 作者 |
| `apiVersion` | ✅ | 本规范版本，2.0 插件必须写 `"2.0"` |
| `i18n` | 可选 | 语言标签（BCP-47）→ `{ name, description }` 覆盖；缺失时回落顶层字段 |
| `license` / `homepage` / `repository` / `keywords` | 可选 | 展示与检索元数据 |
| `engines.starcloud` | ✅ | 兼容的启动器版本**区间**（npm 风格：`^`、`~`、`>=x <y`、`*`）。取代 1.x 的 `compatibility.minLauncherVersion`（后者仍被接受为等价下限） |
| `runtime` | 可选 | `js` / `wasm` / `py`，缺省按入口文件推断（三选一） |
| `permissions` / `optionalPermissions` | 可选 | 2.0 权限串，语法见 §6.1 |
| `dependencies` / `conflicts` | 可选 | 插件间依赖与冲突，见 §9.3 |
| `contributes` | 可选 | 声明式 UI 与扩展点，见 §8 |
| `updates.manifestUrl` | 可选 | 插件更新清单地址，见 §9.4 |

**与 1.x 的差异**：`permissions` 语法不同（`launcher:network` → `network`，且可带参数）；
`signature` 从字符串变为对象且**移入 `integrity.json`**；新增 `i18n` / `engines` / `runtime` /
`dependencies` / `contributes` / `updates` / `license` 等元数据。

---

## 4. 运行时

| 入口 | 运行时 | 隔离 | 适用 |
| --- | --- | --- | --- |
| `main.js` | Jint（进程内） | 强沙箱（无宿主 IO，只经 ctx） | 轻逻辑、要最快启动 |
| `main.wasm` | WASM/WASI（进程内，独立线性内存） | **强隔离**：默认无 FS/网络，能力只经导入的 host 函数 | 重逻辑、第三方代码、CPU 密集 |
| `main.py` | `python.exe` 子进程 | 进程外 | 已有 Python 生态 |

- **未实现**：`main.wasm` 运行时；1.x 目前只支持 `js` / `py`。
- Python 运行时缺失时：插件标记为「缺少运行时」并保持停用（1.x 会直接加载失败），
  用户可在插件页一键跳转 Python 官网（**未实现**）。
- 三种运行时的能力面**完全一致**（同一份权限校验代码），差异只在性能与隔离强度。

### 4.1 钩子契约（async + 预算）

```javascript
export async function onLoad(ctx) { ... }          // 也可写 function onLoad(ctx)
export async function onUnload(ctx) { ... }
export async function beforeGameLaunch(ctx) { ... } // 仅声明 game:arguments 的插件
export async function onLauncherEvent(ctx) { ... }  // 仅声明 events 的插件
```

| 钩子 | 默认预算 | 超预算行为 |
| --- | --- | --- |
| `onLoad` / `onUnload` | 8s（可 `ctx.budget.extend(ms)` 至多 +30s，仅启动期可见进度提示） | 记日志、取消该次调用；`onLoad` 失败则停用插件 |
| `beforeGameLaunch` | 3s（**硬上限**，不可延长：用户就等在启动按钮上） | 跳过该插件本次注入并记日志 |
| `onLauncherEvent` | 2s | 记日志、丢弃本次事件处理 |
| `ctx.tasks.spawn` 作业 | 声明式 `timeoutMs`，默认 120s，上限 30min | 作业置 `failed`，进任务中心可见 |

- **预算不是 1.x 的「5 秒硬超时」**：等待 I/O（`await`）不计入 CPU 预算，只受总预算约束；
  纯 CPU 死循环由运行时强制中断（Jint `timeoutInterval` / WASI 燃料计量）。
- 钩子内 `await` 期间不阻塞 UI 线程：宿主把钩子调度到插件自己的单线程调度器上，
  同一插件的钩子**永不并发**（沿用 1.x 语义）。

### 4.2 长任务：`ctx.tasks.spawn`（未实现）

```javascript
const job = ctx.tasks.spawn(async (job) => {
    for (let i = 0; i < 100; i++) {
        if (job.cancelled) return;                 // 协作式取消
        job.progress(i / 100, `第 ${i} 步`);
        await ctx.download.file(urls[i], `p${i}.bin`);
    }
}, { name: "批量下载", timeoutMs: 600000 });

// job.id / job.state / job.cancel() / `await job.done`
```

- 作业必须出现在启动器**任务中心**（与本体下载 / 安装任务同一入口），用户可取消。
- 作业是 2.0 里唯一允许「长时间干活」的地方；钩子本身仍应短小（准则见 §11.1）。

---

## 5. 权限模型 2.0

### 5.1 语法

```
<namespace>:<verb>[<params>]            params := "(" k "=" v ("," k "=" v)* ")"
```

| 例子 | 含义 |
| --- | --- |
| `instances:read` | 读实例清单 / 详情 / 内容目录 |
| `instances:write` | 安装 / 补全 / 改名（**不含删除**） |
| `instances:write(allow_delete)` | 额外允许删除实例（高风险，运行时二次确认） |
| `network:host=api.example.com` | 只允许请求该主机；`*.example.com` 允许子域 |
| `network:any` | 任意主机（高风险，安装时显式红字提示） |
| `storage:quota=2mb` | 私有键值存储上限（默认 256kb，上限 32mb） |
| `game:arguments` | 追加游戏 / JVM 参数（仅增量，身份参数黑名单照旧） |
| `ui:panel` / `ui:html` | 声明式面板 / **HTML 面板（高风险）** |
| `saves:write(allow_delete)` | 备份 / 还原（含删除需显式参数） |

- 未知 namespace、未知 verb、未知参数名、超范围取值 → **拒绝加载**（白名单制，与 1.x 同）。
- `network` 必须显式声明 `host=<主机>`（支持 `*.example.com`）或 `any`；`host=` 为空视为非法（拒绝加载）。
- 1.x 写法（`launcher:xxx`）在 2.0 清单中不再接受，会被直接拒绝并提示迁移。

### 5.2 命名空间与风险分级

| namespace | verb（2.0 全集） | 默认风险 |
| --- | --- | --- |
| `instances` | `read`、`write`（+`allow_delete`） | read=低，write=中，allow_delete=**高** |
| `game` | `arguments`、`launch`、`kill`（+`confirm`） | arguments/launch=中，kill=**高** |
| `content` | `read`、`write` | 低 / 中 |
| `download` | `file` | 低 |
| `network` | `host=`、`any` | 低 / **高** |
| `settings` | `read`、`write` | 低 / **高** |
| `accounts` | `read`（**永不含令牌**） | 低 |
| `logs` | `read` | 低 |
| `saves` | `read`、`write`（+`allow_delete`） | 低 / 中 / **高** |
| `multiplayer` | `use` | 中 |
| `appearance` | `write` | 低 |
| `updates` | `read` | 低 |
| `events` | `subscribe` | 低 |
| `clipboard` | `read`、`write` | 中 / 低 |
| `shell` | `open` | 中 |
| `storage` | `kv`（+`quota=`） | 低 |
| `tasks` | `read`、`cancel`、`spawn` | 低 / 低 / 中 |
| `ui` | `panel`、`slot`、`notify`、`html` | 低 / 低 / 低 / **高** |

### 5.3 运行时二次确认与配额（未实现）

- **高风险能力**在**首次调用**时弹宿主确认框（标题含插件名与具体参数，如「删除实例 1.21.1」），
  可选「本次会话内总是允许」；拒绝即抛 `PermissionDeniedException`。
- 1.x 只把这些写成准则（§11.2），2.0 改为**本体强制**。
- 配额（默认值，用户可在插件页调整）：

| 资源 | 默认 | 超限行为 |
| --- | --- | --- |
| 网络请求 | 60 次 / 分钟、并发 4 | 429 语义拒答 + 日志 + 插件页告警 |
| 私有存储 | 256 KB | 写入抛错 |
| 日志 | 500 行 / 分钟 | 丢弃并记一行「插件日志超限」 |
| 内存（JS/WASM） | 64 MB | 运行时中断当前调用（沿用 1.x 思路，阈值提高） |
| 后台作业并发 | 2 | `spawn` 抛错 |
| 面板数量 | 3 | 注册失败 |

- **审计日志**：`%APPDATA%/starcloud-launcher/plugins/audit.log`，逐条记录
  `时间 / 插件 id / 能力 / 参数摘要 / 用户是否确认 / 结果`，插件页可查看与导出。

### 5.4 黑名单与不开放（与 1.x 相同）

`account_verify`、`auth_override`、`uuid_override` 出现即拒绝加载；
插件管理、启动器自更新安装、账户写操作三者不开放。
`beforeGameLaunch` 追加的身份类参数照旧直接丢弃并记日志。

---

## 6. 事件与扩展点目录

### 6.1 事件（`events:subscribe`）

| 事件 | 载荷 | 说明 |
| --- | --- | --- |
| `launcher.ready` | `{ version, plugins }` | 沿用 1.x |
| `game.launched` / `game.exited` | `{ versionId, pid? }` | 沿用 1.x |
| `game.log.line` | `{ versionId, line }` | **新增**：行级日志流；宿主默认 50 行/秒节流，插件可 `events.throttle("game.log.line", 200)` |
| `instance.installed` / `instance.removed` | `{ versionId }` | 新增 |
| `content.installed` | `{ versionId, kind, fileName }` | 新增 |
| `download.completed` / `download.failed` | `{ url, fileName, ok, error? }` | 新增 |
| `account.changed` | `{ id, type }`（**无令牌**） | 新增 |
| `appearance.changed` | `{ theme }` | 新增 |

事件一律在插件的调度线程上投递；同一插件不并发；不排队、不重放（沿用 1.x）。

### 6.2 扩展点（`contributes`）

| 扩展点 | 位置 | 2.0 形态 | 状态 |
| --- | --- | --- | --- |
| `panels` | 侧栏插件页 | 声明式视图（JSON），或 `ui:html` 的 HTML | ✅ 声明式；HTML ⏳ |
| `slots` | 页面挂载区（`download.top` / `home.top` / `versions.top` / `version.detail.top`） | 声明式视图 | ✅ 上述四点 |
| `versionSections` | 版本详情页分区（并入「概览」分区组） | 声明式视图 | ✅ |
| `homeCards` | 首页左列卡片带（`home.top` 与版本库之间） | 声明式视图 | ✅ 固定 120 高、最多 3 张、无贡献时不占位（见 §6.3） |
| `navItems` | 左侧导航栏一级入口（Nova 之后的独立分组） | 声明式视图 | ✅ 最多 3 项、每插件 1 项（见 §6.4） |
| `contextMenu` | 版本 / 存档 / 资源行右键 | 菜单项 + `action`（宿主回调插件处理器） | ⏳ |
| `settings` | 插件页内的设置区 | 由 `settings.schema.json` 生成，值自动进 `ctx.storage`（键前缀 `setting.`） | ⏳ |

宿主实际支持的挂载点白名单在 `PluginHost.SupportedSlotPoints`；清单里写了不支持的挂载点会被**跳过并记日志**，
不会出现「声明了却静默不生效」。宿主页面在没有任何贡献时挂载区自身 `Collapsed`（不占高度），
因此没装插件的用户看到的界面与从前完全一致。

**贡献集合变化**：插件是后台加载的，首页这类「启动即渲染」的页面首次 `Loaded` 时可能还没有贡献，
因此 `PluginHost` 提供 `ContributionsChanged` 事件；宿主侧的 `PluginContributionPanel`（挂载区）、
`HomeCardStrip`（首页卡片带）与主窗口的**插件导航组**订阅它，并在插件加载/停用后重建内容（回 UI 线程），
不需要用户手动刷新页面。

**动作（action）回调**：`contributes.*.action` 指向插件导出的处理器名
（`export async function reportRun(ctx, payload) {}`），由宿主在权限校验后调用，预算 3s（⏳ 未实现）。

### 6.3 首页卡片（`homeCards`）✅

```json
"contributes": {
  "homeCards": [
    { "id": "server", "title": "收藏的服务器", "view": "cards/server.json" },
    { "id": "pack",   "title": "今日推荐整合包", "view": "cards/pack.json" }
  ]
}
```

首页是**固定视口布局**（无滚动条、卡片与窗口同高），任何新内容都得从版本库或账户卡身上切走一块高度，
所以卡片带的尺度由宿主写死、插件不能要求更高：

| 规则 | 值 | 说明 |
| --- | --- | --- |
| 位置 | 左列 `home.top` 与版本库之间 | 不动右栏启动卡这个主 CTA |
| 高度 | 固定 120 DIP | 保证可预测，避免一张卡把版本列表挤没 |
| 张数 | 最多 3 张（跨插件合计，按插件加载顺序） | 第 4 张起不显示并写一行 `launcher.log` |
| 宽度 | 等宽均分，间距 12 | 与版本卡 / 账户卡同一套卡面（`ScCard`） |
| 无贡献 | 整行 `Collapsed`（高度 0） | 没装插件的用户看到的首页与从前完全一致 |

不做收起：注册了就摆着，不想看就在插件页把那个插件停掉（停用即摘除卡片）——
比多一个折叠开关更符合「插件的内容归插件管」，也避免收起后留一条空条。

- 与 `slots` 的区别：`home.top` 是一条通栏横幅；`homeCards` 是一组**各带标题、可多张并排**的卡片，
  每张卡有清单里的 `title` 作为卡头。
- 与 `slots` / `versionSections` 同样需要 **`ui:panel`** 能力，没有则整批不注册并记日志。
- 卡片内的动作结果没有地方就地显示（卡片带高度固定），统一汇到首页左列的通知区（成功走提示条、失败走错误条）。
- 卡片内容超出 120 的部分被裁掉，卡片内**不滚动** —— 插件应自己控制内容量（建议一两行 + 一两个芯片）。

### 6.4 一级导航项（`navItems`）✅

```json
"contributes": {
  "navItems": [
    { "id": "servers", "title": "服务器浏览", "icon": "server", "view": "views/servers.json" }
  ]
}
```

给「少数重插件」的主入口在左侧导航栏占一个位置，点开是一个**独立一级页**（不是挂载区里的一块），
内容由声明式视图渲染：

```
首页 · 版本 · 下载 · 存档 · 联机 · 运行状态
───────────────────────────────
账户 · 插件 · Nova
───────────────────────────────   ← 运行时插入的分隔线
服务器浏览                        ← 插件导航项（最多 3 个）
───────────────────────────────
启动器日志 · 设置（底部固定）
```

| 规则 | 值 | 说明 |
| --- | --- | --- |
| 位置 | `Nova` 之后的独立分组 | 不动既有游戏/个人两组 |
| 每插件条数 | 最多 1 条 | 多于 1 条在 `Validate` 里报错（一级位置稀缺） |
| 跨插件条数 | 最多 3 项（按插件加载顺序） | 第 4 项起不显示并写一行 `launcher.log` |
| 无贡献 | 连分隔线都不出现 | 没装插件的用户看到的导航栏与从前逐项一致 |
| 页面形态 | 独立一级页 `PluginNavPage` | 无返回按钮（导航栏本身就是返回）；插件被停用则回落「插件」页 |
| 标题 | 清单里的 `title` 原样用 | **不参与宿主本地化**（插件自己的文案，宿主不猜） |
| 路由 | tag = `plugin:<插件 id>:<导航 id>` | 与内置 tag（`home` / `versions` …）不可能撞 |

`icon` 只能填宿主内置图标名（填别的会回落默认图标并记日志，**不报错**）：

| 名字 | 含义 | 字形 |
| --- | --- | --- |
| `home` | 首页 / 概览 | `EC77` |
| `instance` | 版本 / 实例 | `EA94` |
| `download` | 下载 / 资源 | `E90C` |
| `saves` | 存档 / 世界 | `EA90` |
| `server` | 联机 / 服务器 | `EB91` |
| `account` | 账户 / 身份 | `EB8F` |
| `extension` | 扩展 / 插件（**默认**） | `EABC` |
| `log` | 日志 / 记录 | `EC64` |

- 与其它 UI 扩展点一样需要 **`ui:panel`** 能力，没有则整条不注册并记日志。
- 不做：自定义图标文件、二级子项、排序权重、徽标数字。位置就三个，按加载顺序排。

---

## 7. 声明式 UI 视图 ✅（仅 HTML 模式未实现）

已实现：解析器 `Starcloud.Core/Plugins/PluginView.cs`（白名单、嵌套 ≤12 层、节点 ≤500、绑定与动作语法、
`itemView` 逐项模板）、渲染器 `Starcloud.App/Views/Plugins/PluginViewRenderer.cs`、
面板宿主 `PluginPanelPage` 与挂载区宿主 `Views/Controls/PluginContributionPanel.cs`
（`contributes.panels` / `slots` / `versionSections` 在插件启用时自动注册，不需要插件代码）。

### 7.1 组件白名单

`view` 文件是 JSON：`{ "type": "...", ... }`，宿主渲染为原生控件（WinUI）。允许的组件（✅ = 已实现渲染）：

| type | 关键属性 | 渲染 |
| --- | --- | --- |
| `stack` | `direction`(row/column)、`spacing`、`children[]` | ✅ StackPanel |
| `text` | `value`、`style`(title/body/caption/code)、`wrap` | ✅ TextBlock（title 20 / body 14 / caption 12） |
| `button` | `label`、`action`、`kind`(normal/accent/danger) | ✅ Button（无 action 则禁用并提示） |
| `toggle` | `label`、`bind` | ✅ ToggleSwitch（仅 `storage.*` 绑定可写回） |
| `input` | `label`、`placeholder`、`bind`、`multiline` | ✅ TextBox（同上） |
| `select` | `label`、`bind`、`options[]` | ✅ ComboBox（同上） |
| `list` | `items`(bind)、`itemView` | ✅ 逐项按模板渲染（模板里用 `item.*`；最多 40 项、嵌套 3 层） |
| `table` | `columns[]`、`rows`(bind) | ✅ Grid（对象数组按列名取值，二维数组按位置取值；最多 8 列 × 50 行） |
| `image` | `src`（包内相对路径或 https）、`height` | ✅ Image（高度上限 320，圆角 8） |
| `progress` | `progress`(0–1)、`label` | ✅ ProgressBar（值裁剪到区间内） |
| `badge` | `value` | ✅ 与全站一致的 ScChip |
| `divider` | — | ✅ 1px 分隔线 |
| `link` | `value`、`href` | ✅ HyperlinkButton（只允许 https） |
| `code` | `value`、`language` | ✅ 等宽代码块（可选中、横向滚动） |

**禁止**：`html`、`iframe`、`frame`、`script`、`style`、`object`、`embed`、`svg`、`canvas`、`form`
—— 这些都在 `ui:html` 模式下才可能出现（§7.4，未实现）。解析器遇到即拒绝整份视图并给出可读错误。

### 7.2 数据绑定与动作

```json
{
  "type": "stack", "direction": "column", "spacing": 8,
  "children": [
    { "type": "text", "value": { "bind": "storage.launches" }, "style": "title" },
    { "type": "toggle", "label": "启用", "bind": "storage.enabled" },
    { "type": "button", "label": "重置计数", "kind": "danger",
      "action": { "api": "storage.set", "args": ["launches", 0] } }
  ]
}
```

- `bind`：`storage.<key>`（需 `storage:kv`）、`state.<key>`（视图内本地状态）、`host.<字段>`（`version` / `plugin` / `api_version`）、
  `item.<字段>`（**仅 list 逐项模板内**；支持 `item.a.b`、`item.0` 下钻，裸 `item` 取整项）。
  可写成 `{"bind":"..."}` 对象（值字段）或 `"bind": "..."` 顶层属性（表单控件）。
- `list`：`items` 绑定一个数组，逐项按 `itemView`（包内相对路径）渲染；模板自身也会被完整校验
  （深度、节点数、白名单与主视图同一套），并限制 40 项 / 3 层嵌套，防止模板自引用把界面撑爆。
- `action`：`{ api, args }` 直接映射到能力层（§5 的同一套权限校验、主机白名单、存储配额都在链路上），
  **`args` 是位置参数数组**（与 Python `host_call` 协议同形，如 `["launches", 0]`）；
  需要复杂逻辑时用 `{ "handler": "handlerName" }` 回调插件代码（⏳ 未实现，按钮会提示）。
- 面板动作要求插件**处于启用状态**（`PluginHost.InvokeAction` 按清单即时装配 ctx）。
- 视图刷新：`storage` 写回成功后宿主自动整份重绘；`ctx.ui.refresh(panelId)` ⏳ 未实现。
- 渲染错误不静默：解析失败直接显示错误列表，运行时异常显示在面板里，动作结果进出结果条（InfoBar）。

### 7.3 与 1.x 的关系

1.x 的 `ctx.ui.registerPanel(id, title, entryHtml)` 在 2.0 中仍可用，但需 **`ui:html`** 权限，
且宿主强制：面板顶部固定归属条（插件名 + 版本 +「第三方内容」）、禁用窗口控件覆盖、
HTML 内 `postMessage` 只能走 §7.4 的消息桥。

### 7.4 HTML 模式与消息桥（统一 host_call）

```javascript
// 面板内（HTML 模式）
const reply = await window.starcloud.call("storage.get", ["launches"]);
```

宿主侧协议与 1.x 的 Python `host_call` **同形**（`{op, id, api, args}` / `{op, id, ok, result|error}`），
因此 Python / WASM / HTML / 声明式四种形态的能力面与权限校验是同一份实现。

---

## 8. 完整性、签名、依赖与更新

### 8.1 integrity.json

```json
{
  "schema": "starcloud-integrity/2",
  "id": "com.example.myplugin",
  "version": "2.1.0",
  "algorithm": "sha256",
  "files": {
    "manifest.json": "9f2c…",
    "main.js": "41ab…",
    "assets/icon.png": "7d00…"
  },
  "package": "c4d1…"
}
```

- `files`：包内每个文件（除 `integrity.json` 本身）的 sha256（小写十六进制）。
- `package`：把 `files` 按路径升序拼接 `path\0hex\n` 后再 sha256 —— 供签名与快速比对。
- 安装时校验 ✅：任一条目不匹配即拒绝安装（1.x 只校验 zip 路径安全）；2.0 包缺 `integrity.json` 亦拒绝安装。

### 8.2 签名

```json
{
  "algorithm": "ecdsa-p256-sha256",
  "keyId": "starcloud-official-2026",
  "value": "<base64>"
}
```

- 放在 `integrity.json` 的 `signature` 字段；签名载荷 = 规范化 JSON（键**升序**、无空白）：
  `{"id":…,"package":…,"version":…}`。
- 这样**代码被替换必然破坏 `package` 摘要**，签名随即失效 —— 修掉 1.x「只签 manifest」的漏洞。
- 三态 ✅：**官方已验证**（徽标）/ **未签名**（【第三方未验证】，启用时逐个免责确认）/
  **签名无效**（**拒绝加载**，1.x 只提示）。
- `keyId` 支持轮换：本体内置多把公钥，`keyId` 不在其中按「未签名」处理并提示升级启动器。

### 8.3 依赖 ✅

- `dependencies: { id: "semver 区间" }`；宿主按拓扑序加载（依赖先于被依赖者，同层按 id 升序），
  **循环依赖全部拒绝加载**。
- 依赖缺失 / 未启用 / 版本不满足 / 其自身不可用 → 该插件被阻断，并给出具体原因
  （如「未安装依赖 com.example.lib ^1.2.0」「依赖 com.example.lib 未启用」）。
- `conflicts` 命中（冲突插件已启用且版本落在区间内）→ **声明 `conflicts` 的一方**被阻断并提示
  （不自动停用对方；规则确定：双向声明则双方都停）。
- **未实现**：懒启用与「一键安装依赖」引导（当前只在启用时拒绝并说明原因）。

### 8.4 更新（未实现）

- 插件可写 `updates.manifestUrl`，返回 `{ "version": "2.2.0", "url": "…", "sha256": "…", "changelog": "…" }`。
- 宿主只**查询与提示**（插件页「可用更新」徽标 + 版本对比）；安装仍需用户点确认。
- 启动器**不下发**远程插件列表，也不自动安装：与 1.x 的谨慎一致。
- 撤销：本体可内置 `revokedIds`（离线也可用的本地黑名单），命中则强制停用并说明原因。

---

## 9. 兼容与迁移（1.x → 2.0）

| 1.x | 2.0 | 迁移动作 |
| --- | --- | --- |
| `apiVersion: "1.0"` | `apiVersion: "2.0"` | 改字段；宿主按 `apiVersion` 分派校验与运行时 |
| `compatibility.minLauncherVersion` | `engines.starcloud: ">=x"` | 等价改写（旧字段仍接受） |
| `launcher:network` | `network:host=…` 或 `network:any` | 必须显式声明主机（安全强化，非等价） |
| `launcher:instances_write` | `instances:write`（删除另需 `(allow_delete)`） | 用不到删除就别加参数 |
| `launcher:storage` | `storage:kv` (+`quota=`) | 加配额参数 |
| `signature`（字符串，只签 manifest） | `integrity.json.signature` | 用打包工具重新出包 |
| `ctx.ui.registerPanel(..., html)` | 声明式视图，或加 `ui:html` | 建议改写为声明式（更安全且能交互） |
| Python `host_call` | 不变（四种形态统一到同一协议） | 无需改动 |

**双版本共存**：本体同时支持 `1.0` 与 `2.0`，1.x 插件的既有行为**不变**；
2.0 插件在只支持 1.0 的旧本体上被拒绝加载并提示升级（沿用 1.x 的对应策略）。

---

## 10. 调试与自测清单（2.0 增量）

1. **权限最小化**：每条 `permissions` 都能对应到代码里的实际调用；能写 `network:host=` 就别写 `any`。
2. **配额**：跑满网络配额与存储配额，确认插件是「优雅报错 + 提示」而不是崩掉或静默丢数据。
3. **二次确认**：`allow_delete` 类操作在拒绝确认后不留半成品。
4. **签名**：改一个字节的 `main.js` 后重打包 → 应安装失败（签名无效）。
5. **扩展点**：面板 / 挂载区 / 版本详情分区 / 首页卡片 / 导航项在**深色 + 浅色**下都自洽；停用插件后扩展点全部消失，导航项与分隔线一并消失、当前页回落「插件」页。
6. **长任务**：`ctx.tasks.spawn` 的作业出现在任务中心，取消后插件收到 `job.cancelled`。
7. **依赖**：缺失依赖时插件停用并给出可操作提示；无循环依赖。

---

## 11. 本体侧实现映射（供对照行为）

| 关注点 | 文件（现状 / 2.0 落点） |
| --- | --- |
| 清单模型与校验 | `Starcloud.Core/Plugins/PluginManifest.cs`（1.x + 2.0 字段与按版本分派校验 ✅） |
| 权限白名单 / 黑名单 | `Starcloud.Core/Plugins/PluginPermissions.cs`（1.x 24 项 + 2.0 语法 / 风险分级 / 配额 ✅） |
| 语义化版本区间 | `Starcloud.Core/Plugins/PluginVersionRange.cs` ✅（`engines.starcloud`、`dependencies`） |
| 整包摘要与签名 | `Starcloud.Core/Plugins/PluginIntegrity.cs` ✅（安装时校验：`Starcloud.App/Services/PluginService.cs`） |
| 打包工具 | `tools/pack-plugin.ps1` ✅（生成 `integrity.json` + 可选签名 + 打包自检） |
| 插件页信息展示 | `Starcloud.App/Views/PluginsPage.xaml(.cs)` ✅（API 版本芯片、能力与风险分级） |
| 依赖解析与冲突 | `Starcloud.Core/Plugins/PluginDependencyResolver.cs` ✅（接入：启用闸门与启动顺序，`PluginService`） |
| 声明式视图模型与解析 | `Starcloud.Core/Plugins/PluginView.cs` ✅（白名单 / 深度与节点上限 / 绑定与动作语法） |
| 声明式视图渲染 | `Starcloud.App/Views/Plugins/PluginViewRenderer.cs` ✅（模型 → WinUI 控件树） |
| 声明式面板宿主 | `Starcloud.App/Views/PluginPanelPage.xaml(.cs)` ✅（1.x 走 WebView2，2.0 走声明式） |
| 声明式挂载区宿主 | `Starcloud.App/Views/Controls/PluginContributionPanel.cs` ✅（页面挂载点 / 分区，两种入口） |
| 声明式视图文件入口 | `PluginViewRenderer.RenderFile` ✅（挂载区与首页卡片带共用同一份解析 + 渲染 + 错误兜底） |
| 首页卡片宿主 | `Starcloud.App/Views/Controls/HomeCardStrip.cs` ✅（等宽卡片带、最多 3 张、无卡片时整块不占位） |
| 挂载点接入 | `DownloadPage` / `HomePage` / `VersionsPage` / `VersionDetailPage` ✅（download.top / home.top / versions.top / version.detail.top + version.detail.sections） |
| 贡献集合变化通知 | `PluginHost.ContributionsChanged` ✅（后台加载完成后重建挂载区与首页卡片带） |
| `list` 逐项模板 | `PluginViewNode.ItemView` + `PluginViewRenderer.BuildList` ✅ |
| 面板动作执行 | `PluginHost.InvokeAction` ✅（与插件调用共用能力层，权限/白名单/配额一致） |
| 2.0 能力桥接 | `PluginPermissions.BridgeApi2` ✅（只放行不放大权限的能力，其余 fail-closed + 记日志） |
| 网络主机白名单 | `PluginApiCore.IsHostAllowed` ✅（`network:host=` / `network:any`） |
| 存储配额 | `PluginStorageApi.WriteCore` ✅（`storage:kv(quota=)`，默认 256KB / 上限 32MB） |
| 运行时 API 版本闸门 | `PluginHost.Start`（仅 `PluginEnvironment.RuntimeApiVersions` 可启用 ✅；1.0 与 2.0） |
| 首页卡片（`homeCards`） | `PluginHost.RegisterContributedHomeCards` + `HomeCardStrip` ✅（见 §6.3） |
| 一级导航项（`navItems`） | `PluginHost.RegisterContributedNavItems` ✅（见 §6.4） |
| 插件导航页 | `Starcloud.App/Views/PluginNavPage.xaml(.cs)` ✅（独立一级页，无返回按钮 + 停用回落「插件」页） |
| 导航组装配与路由 | `MainWindow.RebuildPluginNavGroup` ✅（Nova 之后插分隔线 + 最多 3 项；`PluginExtensionPoints.PluginNavTag` / `TryParseNavTag` 拼解 tag） |
| 导航项图标白名单 | `PluginExtensionPoints.NavIcons` / `TryResolveNavIcon` ✅（8 个内置名 + 默认 `extension`，未命中回落并记日志） |
| HTML 面板与消息桥（`ui:html`） | 2.0 新增（未实现，能力暂不放行） |
| async 钩子 / `ctx.tasks.spawn` / WASM | 2.0 新增（未实现，能力暂不放行） |
| 审计日志与高风险二次确认 | 2.0 新增（未实现；配额默认值已定义在 `PluginPermissions`） |
| 能力层单测 | `dotnet/tests/Starcloud.Core.Tests/PluginSdkTests.cs`、`PluginSpecV2Tests.cs`、`PluginViewTests.cs` ✅ |
| 钩子分发与事件 | `Starcloud.Core/Plugins/PluginHost.cs`（1.x 同步；async + 预算待实现） |
| JS / Python 运行时 | `JsPluginRuntime.cs` / `PyPluginRuntime.cs`（WASM 运行时待实现） |
