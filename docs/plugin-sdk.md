# Starcloud 启动器插件 SDK 开发指南（plugin-sdk）

> 插件是第三方独立开发的扩展，插件开发者独立承担插件全部法律责任。
> 启动器本体不提供、不支持、不接管任何账号身份验证功能；插件拿不到任何令牌。

对应规范：[plugin-spec.md](plugin-spec.md)（manifest / 权限 / 安全规则以规范为准；本文只讲"怎么写"）。
本指南覆盖 **24 项权限 / 23 个 ctx 成员**，每个能力都给出 JS 与 Python 两版可跑片段。

---

## 1. 五分钟上手（JS）

```
com.example.hello/
├─ manifest.json
├─ main.js
└─ assets/panel.html
```

`manifest.json`：

```json
{
  "id": "com.example.hello",
  "name": "Hello 插件",
  "version": "1.0.0",
  "apiVersion": "1.0",
  "author": "你的名字",
  "description": "演示：追加 JVM 参数 + 通知 + 面板 + 读取实例",
  "permissions": [
    "launcher:game_arguments",
    "launcher:notification",
    "launcher:ui_panel",
    "launcher:instances_read"
  ],
  "compatibility": { "minLauncherVersion": "0.3.2" }
}
```

`main.js`：

```javascript
function onLoad(ctx) {
    ctx.logger.info("hello 已加载，启动器 " + ctx.launcher.version);
    ctx.ui.registerPanel("main", "Hello 面板", "assets/panel.html");
    // 结构化返回值就是普通对象/数组，直接点开用
    ctx.instances.list().forEach(v => ctx.logger.info("已装：" + v.id));
}

function beforeGameLaunch(ctx) {
    ctx.args.append("-Xmx4G");            // 追加游戏参数
    ctx.args.appendJvm("-XX:+UseG1GC");   // 追加 JVM 参数
    ctx.ui.notify("Hello", "参数注入完成");
}
```

打包：目录压成 zip → 改后缀 `.mclplug` → 启动器「插件」页安装 → 启用（第三方插件需确认免责声明）。

API 2.0 插件（`apiVersion: "2.0"`）必须带 `integrity.json`（逐文件摘要 + 整包签名），请用打包工具生成：

```powershell
pwsh -File tools\pack-plugin.ps1 -Source .\my-plugin
# 官方签名（只有持私钥的一方需要）：
pwsh -File tools\pack-plugin.ps1 -Source .\my-plugin -KeyFile .\official-key.pem -KeyId starcloud-official-2026
```

工具会在暂存目录里组装文件并写入 `integrity.json`，再打成 `.mclplug`，最后把 zip 内容重算一遍自检；
`data/`（运行时私有目录）与产物自身不入摘要。规范见 [plugin-spec-v2.md](plugin-spec-v2.md) §8。

### 2.0 声明式面板

2.0 插件可以在清单里直接声明面板，不用写一行 UI 代码（详见 [plugin-spec-v2.md](plugin-spec-v2.md) §6.2 / §7）：

```json
{
  "apiVersion": "2.0",
  "permissions": ["ui:panel", "storage:kv(quota=64kb)"],
  "contributes": {
    "panels": [ { "id": "main", "title": "我的面板", "view": "panels/main.json" } ],
    "slots": [ { "point": "versions.top", "view": "slots/banner.json", "height": 72 } ],
    "versionSections": [ { "id": "report", "title": "运行报告", "view": "sections/report.json" } ],
    "homeCards": [
      { "id": "server", "title": "收藏的服务器", "view": "cards/server.json" },
      { "id": "pack",   "title": "今日推荐整合包", "view": "cards/pack.json" }
    ],
    "navItems": [
      { "id": "servers", "title": "服务器浏览", "icon": "server", "view": "views/servers.json" }
    ]
  }
}
```

```json
{
  "type": "stack", "direction": "column", "spacing": 12,
  "children": [
    { "type": "text", "value": "运行次数", "style": "caption" },
    { "type": "text", "value": { "bind": "storage.launches" }, "style": "title" },
    { "type": "progress", "label": "进度", "progress": 0.6 },
    { "type": "toggle", "label": "启用", "bind": "storage.enabled" },
    { "type": "list", "items": { "bind": "storage.report" }, "itemView": "views/item.json" },
    { "type": "button", "label": "重置", "kind": "accent",
      "action": { "api": "storage.set", "args": ["launches", 0] } }
  ]
}
```

逐项模板（`list` 的 `itemView`）里用 `item.*` 取当前项字段：

```json
{ "type": "stack", "direction": "column", "spacing": 2,
  "children": [
    { "type": "text", "value": { "bind": "item.实例" }, "style": "body" },
    { "type": "text", "value": { "bind": "item.占用" }, "style": "caption" }
  ] }
```

要点：组件走白名单（`html` / `iframe` / `script` 一律拒绝）；`bind` 的 `storage.*` 读写自动写回并重绘；
`action.args` 是**位置参数数组**；动作与绑定都经宿主能力层，权限、网络主机白名单、存储配额与插件自己调用时完全一致。
没声明 `ui:panel` 的话面板、挂载区与首页卡片都不会注册。挂载点目前支持
`download.top` / `home.top` / `versions.top` / `version.detail.top`（+ `versionSections` → 版本详情页分区），
写了别的点会被跳过并在插件日志里说明。

首页卡片（`homeCards`）装在首页左列、`home.top` 与版本库之间，尺寸由宿主定死、插件不能要求更高：
**固定 120 高、等宽排列、最多 3 张**（跨插件合计，按插件加载顺序），没有贡献时整块不占位。
卡片内容超出 120 的部分会被裁掉且不能滚动，所以请控制内容量（建议一两行文字 + 一两个 `badge`），
或者用 `progress` 这类自带高度的小组件；详细规则见 [plugin-spec-v2.md](plugin-spec-v2.md) §6.3。

一级导航项（`navItems`）给「少数重插件」的主入口在左侧导航栏占一个位置，点开是一个独立一级页，
内容同样是声明式视图。**每个插件最多 1 条、跨插件最多 3 项**（按插件加载顺序），没贡献时连分隔线都不出现。
`icon` 只能填宿主内置的 8 个名字：`home` / `instance` / `download` / `saves` / `server` / `account` /
`extension`（默认）/ `log`；填别的会回落默认图标并在插件日志里说明，不会拒绝加载。
标题原样显示、不做本地化；详细规则见 [plugin-spec-v2.md](plugin-spec-v2.md) §6.4。

## 2. 五分钟上手（Python）

```
com.example.pysample/
├─ manifest.json
└─ main.py
```

```python
def onLoad(ctx):
    ctx.logger.info("python 插件已加载")
    for v in ctx.instances.list():        # 与 JS 完全同一份能力
        ctx.logger.info("已装：" + v["id"])

def beforeGameLaunch(ctx):
    if ctx.args is not None:              # 未声明 game_arguments 权限时为 None
        ctx.args.append("-Xmx4G")

def onLauncherEvent(ctx):
    ctx.logger.info(f"{ctx.event['name']} -> {ctx.event['data']}")
```

需要本机 Python 3.8+。启动器以进程外子进程运行插件，`Assets/pluginsdk/starcloud_host.py`
是宿主 SDK（**不要**把它拷进插件包）；`ctx.*` 调用会被翻译成 `host_call` 报文，权限校验在宿主侧完成。

## 3. ctx 成员与权限速查

| ctx 成员 | 需要的权限 | 一句话 |
| --- | --- | --- |
| `launcher` | 无 | `{version, apiVersion, officialBuild}` |
| `logger` | 无 | `info` / `warn` / `error` |
| `args` | `game_arguments` | 仅 `beforeGameLaunch` 提供；只能追加 |
| `files` | `file_access` | 插件 `data/` 目录读写（越界拒绝） |
| `versions` | `instances_read` | 官方版本清单 / 最新版 / 已安装 |
| `instances` | `instances_read`/`write` | 实例详情、装原版与加载器、补全、改名、删除 |
| `game` | `game_launch` | 启动 / 状态 / 结束游戏进程 |
| `content` | `content_read`/`write` | Modrinth、CurseForge 搜索与装载 |
| `download` | `download` | 走启动器多源引擎下载到 `data/` |
| `http` | `network` | 插件自己的 HTTP 请求 |
| `settings` | `settings_read`/`write` | 启动器设置（主机私有键被隔离） |
| `storage` | `storage` | 私有键值存储，跨启动保留 |
| `accounts` | `accounts_read` | 账户身份只读（**永不返回令牌**） |
| `logs` | `logs_read` | 启动器 / 游戏 / 插件日志尾部 |
| `tasks` | `tasks` | 任务中心进度；取消 / 清空 |
| `saves` | `saves_read`/`write` | 存档清单、备份、还原、导出整合包 |
| `multiplayer` | `multiplayer` | 联机状态、装核心、开房 / 加入 / 结束 |
| `appearance` | `appearance` | 主题 / 背景 / 毛玻璃 |
| `updates` | `updates` | 查询启动器新版本（只查不装） |
| `system` | `clipboard`/`shell`/`ui_panel` | 剪贴板、开链接、开目录、确认框、开页 |
| `events` | `events` | 订阅 `game.launched` / `game.exited` / `launcher.ready` |
| `ui` | `ui_panel`/`notification` | 面板、页面挂载区、通知 |

**两条铁律**（规范 §5）：

1. 未声明的权限 → 对应成员是 `null` / `undefined`，调不到。
2. 身份相关能力不存在：黑名单权限（`account_verify` / `auth_override` / `uuid_override`）声明即拒绝加载；
   `args.append` 命中 `--username` / `--uuid` / `--accessToken` / `--userType` / `--clientId` /
   `--authlib-injector` 等身份参数会被直接丢弃并写日志。

## 4. JS ↔ Python 方法对照

JS 用驼峰，Python 用下划线，**能力名与参数顺序完全一致**：

| 能力 | JS | Python |
| --- | --- | --- |
| 已安装实例 | `ctx.instances.list()` | `ctx.instances.list()` |
| 装原版 | `ctx.instances.install("1.21.1")` | `ctx.instances.install("1.21.1")` |
| 装加载器 | `ctx.instances.installLoader("fabric", "1.21.1")` | `ctx.instances.install_loader("fabric", "1.21.1")` |
| 改实例设置 | `ctx.instances.updateSettings(id, json)` | `ctx.instances.update_settings(id, json)` |
| 启动游戏 | `ctx.game.launch(id)` | `ctx.game.launch(id)` |
| 游戏日志 | `ctx.game.log(null, 60)` | `ctx.game.log(None, 60)` |
| 搜索资源 | `ctx.content.search("sodium")` | `ctx.content.search("sodium")` |
| 装资源 | `ctx.content.install("AANobbMI", "mod")` | `ctx.content.install("AANobbMI", "mod")` |
| 读设置 | `ctx.settings.get()` | `ctx.settings.get()` |
| 改设置 | `ctx.settings.set(json)` | `ctx.settings.set(json)` |
| 读账户 | `ctx.accounts.current()` | `ctx.accounts.current()` |
| 下载文件 | `ctx.download.file(url, "a.jar", sha1)` | `ctx.download.file(url, "a.jar", sha1)` |
| 私有存储 | `ctx.storage.set("k", 1)` | `ctx.storage.set("k", 1)` |
| 订阅事件 | `ctx.events.on("game.exited")` | `ctx.events.on("game.exited")` |
| 确认框 | `ctx.system.confirm("标题", "正文")` | `ctx.system.confirm("标题", "正文")` |

## 5. 逐能力示例

### 5.1 读版本与实例（`instances_read`）

```javascript
const latest = ctx.versions.latest().release;            // 官方最新正式版
const missing = ctx.versions.list().filter(v => !v.installed);
ctx.logger.info(`最新版 ${latest}，还没装的 ${missing.length} 个`);

const detail = ctx.instances.get("1.21.1");
ctx.logger.info(`${detail.id}：内存 ${detail.memoryMaxMb}MB，隔离=${detail.isolated}`);
ctx.logger.info("mods：" + ctx.instances.files("1.21.1", "mods").map(f => f.name).join("、"));
```

```python
detail = ctx.instances.get("1.21.1")
ctx.logger.info(f"{detail['id']}：内存 {detail['memoryMaxMb']}MB")
```

### 5.2 装版本 / 装加载器（`instances_write`）

```javascript
ctx.logger.info(ctx.instances.install("1.21.1").message);
ctx.logger.info(ctx.instances.installLoader("fabric", "1.21.1").message); // 也支持 forge/neoforge/quilt
ctx.logger.info(ctx.instances.repair("1.21.1").message);                  // 补全缺失文件
```

> 装加载器会先自动补装原版；`{ ok, message }` 里的 `message` 是给人看的进度结论。

### 5.3 启动与跟随游戏（`game_launch`）

```javascript
if (!ctx.game.isRunning()) {
    const r = ctx.game.launch("1.21.1");
    ctx.logger.info(r.message);   // 缺登录凭证时 message 会明说「需要重新登录」
}

function onLoad(ctx) { ctx.events.on("game.exited"); }
function onLauncherEvent(ctx) {
    if (ctx.event.name === "game.exited") {
        const data = JSON.parse(ctx.event.data);
        ctx.ui.notify("游戏结束", `实例 ${data.versionId} 已退出`);
    }
}
```

### 5.4 资源搜索与装载（`content_read` / `content_write`）

```javascript
const hits = ctx.content.search("钠 sodium", "mod", "modrinth", "1.21.1", "fabric");
ctx.logger.info(hits);                       // 文本清单，含项目 ID
ctx.content.install("AANobbMI", "mod", "modrinth", null, "1.21.1");

const updates = ctx.content.checkUpdates("1.21.1");   // 已装资源里哪些能升级
ctx.logger.info(updates);
```

资源库里已有的文件可以直接装载（不重新下载）：`ctx.content.enable("sodium-0.6.0.jar", "mod", "1.21.1")`。

### 5.5 下载与网络（`download` / `network`）

```javascript
// 走启动器多源引擎（分片 / 竞速 / 重试），落盘在插件 data/ 目录
const jar = ctx.download.file("https://example.com/my-mod.jar", "my-mod.jar", null);

// 插件自己的接口请求
const r = ctx.http.postJson("https://api.example.com/report",
    JSON.stringify({ version: ctx.launcher.version }));
if (!r.ok) ctx.logger.warn("上报失败：" + r.status);
```

### 5.6 设置与私有存储（`settings_*` / `storage`）

```javascript
const s = ctx.settings.get();                 // 敏感键已过滤，filtered 里列出被过滤的键名
ctx.logger.info("当前下载源：" + s.download_source);
ctx.settings.set(JSON.stringify({ theme: "dark" }));   // 白名单字段

const launches = ctx.storage.get("launches") || 0;
ctx.storage.set("launches", launches + 1);
```

### 5.7 账户身份（只读，`accounts_read`）

```javascript
const me = ctx.accounts.current();
if (me.ready) ctx.logger.info(`本局身份：${me.name}（${me.type}）`);
else ctx.logger.warn("还没准备好身份：" + me.reason);   // 引导用户去账户页，插件代替不了登录
```

`list()` 返回 `{ id, name, type, isDefault, server }`；离线账户 `type` 为 `offline`，`id` 是
名字派生 UUID（与各启动器通用的 `OfflinePlayer:` 算法一致），只能玩单人 / 局域网。

### 5.8 日志、任务与存档

```javascript
ctx.logger.info(ctx.logs.launcher(200));      // 启动器日志尾部
ctx.logger.info(ctx.logs.game(100));          // 最新一局游戏日志
ctx.tasks.list().forEach(t => ctx.logger.info(`${t.title} ${t.progress}`));

ctx.logger.info(ctx.saves.list());            // 存档清单
ctx.logger.info(ctx.saves.backups("1.21.1")); // 备份清单
if (ctx.system.confirm("备份实例", "备份 1.21.1 并包含存档？")) {
    ctx.logger.info(ctx.saves.backupInstance("1.21.1", "发版前", true).message);
}
```

### 5.9 联机（`multiplayer`）

```javascript
ctx.logger.info(ctx.multiplayer.status());      // 提供方 / 房间 / 玩家数 / 可用提供方
ctx.multiplayer.installCore();                  // 首次联机前装核心
const room = ctx.multiplayer.host();            // 开房（先在游戏里「对局域网开放」）
ctx.logger.info(room.message);                  // message 里含邀请码
ctx.multiplayer.stop(true);                     // 只把核心切回空闲
```

### 5.10 外观、剪贴板与开页

```javascript
ctx.appearance.set("dark", null, null, 0.8, 20);       // 主题 + 遮罩 + 模糊
ctx.system.copy(ctx.game.status().versionId || "");    // 写剪贴板
ctx.system.openUrl("https://example.com/help");        // 打开浏览器
ctx.system.openPage("download");                       // 切到下载页
```

## 6. 事件一览（`events`）

| 事件 | 载荷 | 说明 |
| --- | --- | --- |
| `launcher.ready` | `{ version, plugins }` | 所有已启用插件加载完毕（此时才确定"别人也在") |
| `game.launched` | `{ versionId, pid }` | 游戏进程拉起（主页 / 版本页 / Nova / 插件入口都覆盖） |
| `game.exited` | `{ versionId }` | 游戏进程结束 |

```javascript
function onLoad(ctx) {
    ctx.events.on("game.launched");
    ctx.events.on("game.exited");
}
function onLauncherEvent(ctx) {
    ctx.logger.info(`${ctx.event.name} ${ctx.event.data}`);
}
```

`ctx.event.data` 是 JSON 字符串（两语言一致），字段自己解析。事件不排队、不重放。

## 7. 常见问题

| 现象 | 原因 / 处理 |
| --- | --- |
| `ctx.xxx is null / undefined` | manifest 没声明对应权限；对照 §3 的表补齐（声明后需停用再启用插件） |
| 安装时报"未知权限（白名单制）" | 权限名拼错，或用了启动器尚未支持的权限；对照规范 §5.1 |
| `onLoad` 失败、插件被停用 | 钩子里抛异常（常见：把 `null` 成员当对象用）。日志见 `plugins/logs/<id>.log` |
| Python 插件提示找不到 Python | 需要本机 Python 3.8+（`python` 或 `py` 在 PATH 里） |
| Python 调用报 `PermissionError` | 宿主侧的权限判定；确认 manifest 与调用一致 |
| 事件收不到 | 没在 `onLoad` 里 `ctx.events.on(...)`；或该事件发生在插件加载之前（不重放） |
| 设置写不进去 | 字段不在白名单，或属于主机私有键（模型密钥 / 更新源地址）——这类字段设计上不给插件 |
| `ctx.system.confirm()` 总是返回 `false` | 弹窗必须在非 UI 线程的钩子里调用；`onLoad` 有时在 UI 线程上跑（用户在插件页手动启用），此时宿主无法弹模态框并按"未确认"返回（启动器日志里有记录）。把确认放到 `beforeGameLaunch` / `onLauncherEvent`，或改成面板内自己画一个确认区 |
| Python 插件"无响应" | 钩子里的 `host_call` 超过 15 秒未返回会按超时处理；把长任务拆小 |

## 8. 官方签名（可选）

官方渠道发布的插件可在 manifest 中携带 `signature`：对 `manifest.json` 文件字节做
SHA256 → ECDSA P-256 签名 → base64。启动器内置公钥验签通过后展示「官方」徽标，
否则展示【第三方未验证】并在启用时强制免责确认（每个 id+版本确认一次）。

## 9. 示例与调试

- 示例插件（都可直接打包安装）：
  - `examples/plugins/hello-panel/`：最小可用（参数追加 + 通知 + 面板）；
  - `examples/plugins/instance-report/`：本轮能力面（读实例 / 读身份 / 任务中心 / 事件订阅 / 私有存储）。
- 插件日志：`%APPDATA%/starcloud-launcher/plugins/logs/<id>.log`。
- 启动器日志分类 `plugin` / `launch`（「启动器日志」页可见身份参数拦截与事件记录）。
- 自测清单见规范 §12.1。
