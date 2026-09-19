# Starcloud 启动器插件开发规范

> **文档声明（重要，位于规范头部）**
> 本插件规范仅提供游戏启动参数扩展、UI 扩展能力。插件开发者独立承担插件全部法律责任。
> 启动器本体不提供、不支持、不接管任何账号身份验证功能。第三方插件行为与启动器本体开发主体无关。

版本：1.1 ｜ API 版本：`1.0` ｜ 适用：Starcloud Minecraft Launcher 及其他采用本规范的衍生版本（版本隔离约定见 §10）。
配套文档：[plugin-sdk.md](plugin-sdk.md)（SDK 速查与最小示例）；示例插件：`examples/plugins/hello-panel/`。

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
| `main.py` | 进程外 `python.exe` 子进程 | 需本机 Python 3.8+；行式 JSON 协议（宿主 SDK：`Assets/pluginsdk/starcloud_host.py`）；暂不开放 `ctx.files` |

- 两种入口**二选一**，同时存在时优先 `main.js`。
- JS 运行在进程内沙箱，**无任何宿主外 IO / 网络能力**，仅可触达 ctx 注入的白名单对象。
- Python 运行在进程外，参数 / 通知 / 面板全部经协议**声明式上报**，由本体过滤校验后落地。
- 钩子未定义 = 空操作；钩子抛异常不会拖垮启动器：加载失败则拒绝启用该插件，`beforeGameLaunch` 失败则跳过该插件本次注入并记录日志。

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

| 权限 | 能力 |
| --- | --- |
| `launcher:game_arguments` | 在 `beforeGameLaunch` 中追加游戏 / JVM 参数（**仅增量**，见 §7.2） |
| `launcher:ui_panel` | 注册侧栏 UI 面板（WebView2 展示插件 HTML） |
| `launcher:notification` | 发送启动器内弹窗通知 |
| `launcher:file_access` | 读写插件自身 `data/` 目录（路径穿越直接拒绝） |

### 5.2 黑名单（出现即拒绝加载，无申请通道）

- `launcher:account_verify` —— 账号身份校验
- `launcher:auth_override` —— 认证流程接管
- `launcher:uuid_override` —— UUID 覆写

插件不能接管、篡改、替换账号身份校验逻辑；本体不暴露对应钩子，接口层面锁死。

### 5.3 最小权限原则

- 只申请实际用到的权限；未声明权限对应的 ctx 成员为 `null`（JS `undefined`），物理上不可调用。
- 允许零权限插件（仅具备 `ctx.logger` 日志能力）。

## 6. 生命周期

```javascript
function onLoad(ctx) {}          // 插件加载后调用（注册面板 / 初始化在此进行）
function onUnload(ctx) {}        // 插件停用 / 启动器退出前调用（清理在此进行）
function beforeGameLaunch(ctx) { // 每次游戏启动前调用（仅声明 game_arguments 权限且已启用的插件）
    ctx.args.append("-Xmx4G");
}
```

调用时序：

1. 用户启用插件 → 校验 manifest → 选择运行时 → `onLoad`。
2. 游戏启动前 → 依声明权限逐个调用 `beforeGameLaunch`，收集参数增量合并进命令行。
3. 用户停用 / 卸载 / 退出 → `onUnload` → 注销面板 → 释放运行时。

**注意：`onLoad` / `onUnload` 阶段 `ctx.args` 为 `null`**（参数管理器仅在 `beforeGameLaunch` 钩子提供）；面板注册、通知等其余能力在所有钩子中均可用。

## 7. ctx 上下文 API

### 7.1 成员总览

| 成员 | 权限要求 | 说明 |
| --- | --- | --- |
| `ctx.launcher` | 无 | 只读 `{ version, apiVersion, officialBuild }` |
| `ctx.logger` | 无 | `info` / `warn` / `error`，写入插件独立日志 |
| `ctx.args` | `launcher:game_arguments` | 启动参数管理器，**仅 `beforeGameLaunch` 提供** |
| `ctx.files` | `launcher:file_access` | `read` / `write` / `exists` / `list`，root 锁定插件自身 `data/` 目录 |
| `ctx.ui` | `ui_panel` 或 `notification` | `registerPanel(id, title, entry)` / `notify(title, message)` |

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

### 7.3 ctx.files（受限文件访问）

- root 锁定在插件解压目录的 `data/` 子目录；绝对路径与 `..` 穿越抛 `UnauthorizedAccessException`。
- `read(name)` 文件不存在返回 `null`；`write(name, text)` 自动创建父目录；`list()` 仅列 `data/` 顶层文件。
- 仅限文本内容；Python 插件暂不开放本能力。

### 7.4 ctx.ui

```javascript
// 注册面板（launcher:ui_panel）：entry 必须是插件目录内相对路径（HTML）
ctx.ui.registerPanel("main", "我的面板", "assets/panel.html");

// 弹窗通知（launcher:notification）
ctx.ui.notify("Hello", "参数注入完成");
```

- `registerPanel` 的 `entry` 拒绝绝对路径与 `..`；相同 `(panelId)` 重复注册为覆盖。
- 面板由启动器内嵌 WebView2 以 `file://` 加载展示，仅支持静态 HTML / CSS / JS。
- 未声明对应权限时 `ctx.ui` 为 `null`，或调用 `notify` 抛 `UnauthorizedAccessException`。

### 7.4 页面挂载区（slots）

```js
// 注册挂载区（launcher:ui_panel）：在启动器页面固定位置嵌入插件 HTML
// slotId：宿主支持的挂载点（当前：download.top = 下载页右侧内容区顶部）
// height：像素高度，48-320，超出截断
ctx.ui.registerSlot("download.top", "assets/banner.html", 120);
```

- 挂载区在页面加载时挂载（onLoad 内注册即可），以独立 WebView2 `file://` 渲染，同样仅静态内容、不暴露宿主对象。
- 插件卸载即移除其全部挂载区；启停切换后需重进页面生效。
- 挂载区同样适用第 8 章全部行为规范。

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

## 11. 开发者行为准则

1. **性能**：`onLoad` 应快速返回（Jint 单次执行 5s 超时，超时即加载失败）；不要在钩子内做长时间轮询。
2. **稳定**：钩子异常会导致插件功能缺失，请自行捕获可预期异常；`onUnload` 中完成清理。
3. **隐私**：不收集、不回传用户账号信息；文件访问仅限插件自身 `data/` 目录。
4. **合规**：插件内容须符合当地法律法规；禁止携带恶意代码、广告注入、挖矿等行为。
5. **命名**：面板 `panelId` 建议以插件功能命名（如 `main`、`settings`），避免与自身其他面板冲突（同名覆盖）。

## 12. 调试与测试

- 插件日志：`%APPDATA%/starcloud-launcher/plugins/logs/<id>.log`。
- 启动器统一日志分类 `plugin` / `launch`（「启动器日志」页可见身份参数拦截记录）。
- 本地迭代：修改代码 → 重新打包 `.mclplug` → 在「插件」页停用再启用（重新走 `onLoad`）。
- 示例插件：`examples/plugins/hello-panel/`（追加 JVM 参数 + 通知 + 面板），可直接打包安装验证环境。
