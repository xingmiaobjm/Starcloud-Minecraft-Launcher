# Starcloud Minecraft Launcher — GUI 插件开发指南

> **【已弃用 · 2026-09-06】** 本指南描述的 GUI 插件系统（furry:// 协议）属于旧
> Tauri/WebView 架构。项目已全面转向 .NET / WinUI 3（见根目录 README.md 与
> docs/ARCHITECTURE.md），.NET 版暂未实现插件系统，本文仅作历史参考。

> 版本 0.2.0 | 2026-08-09
> 面向插件开发者：如何为启动器编写 GUI 插件（furry:// v2 通用资源协议）。

---

## 1. 插件机制概述

启动器 GUI 支持两类插件角色：

| 类型 | 说明 | 挂载方式 |
|------|------|----------|
| 视图插件（`gui.views`） | 声明**一个或多个具名视图**，可嵌入任意界面位置 | 按 `slots` 插槽挂载：page（独立页面）/ drawer（抽屉）/ home（首页）/ settings（设置）/ sidebar（侧栏）；page 视图自动进入侧边栏"插件视图"区域 |
| 主壳插件（`gui.shell`） | 整体接管主界面（原 replacement） | 插件启用后接管整个窗口 shell（默认 GUI 兜底） |

插件前端由启动器通过自定义协议 `furry://` 托管静态资源，无需自带服务器：

```
furry://view/{plugin_id}/{view_id}/{path}   # 具名视图资源（可嵌入任意插槽）
furry://shell/{plugin_id}/{path}            # 主壳接管资源
furry://assets/{plugin_id}/{path}           # 静态资源（跨场景共享，无声明校验）
```

插件内部可以直接调用启动器后端命令（Tauri IPC）并订阅事件，实现深度集成。

## 2. 目录规范

安装后的插件目录结构：

```
plugins/{plugin_id}/
├── plugin.json        # 清单（必填）
├── frontend/          # 前端静态资源（furry:// 服务根目录）
│   ├── index.html     # 默认入口（entry 未指定时）
│   ├── app.js
│   └── style.css
└── native/            # 原生模块（预留，本版本不加载）
```

## 3. plugin.json 清单（v2）

```json
{
  "id": "com.example.myplugin",
  "name": "我的插件",
  "version": "1.0.0",
  "api_version": 2,
  "min_launcher_version": "0.2.0",
  "gui": {
    "views": [
      {
        "id": "main",
        "title": "我的页面",
        "icon": null,
        "entry": "index.html",
        "slots": ["page", "sidebar"]
      }
    ],
    "shell": null
  }
}
```

字段说明：

| 字段 | 必填 | 说明 |
|------|------|------|
| `id` | 是 | 唯一标识。合法字符：字母/数字/`.`/`_`/`-`，≤128 字符；**禁止**空、`.`/`..`、路径分隔符（防路径穿越） |
| `name` | 是 | 显示名称 |
| `version` | 是 | 插件版本号（如 `1.0.0`） |
| `api_version` | 否 | 插件 API 版本，默认 `2`。必须等于启动器当前支持的版本（`2`），否则标记不兼容 |
| `min_launcher_version` | 否 | 最低启动器版本，低于当前启动器版本（`0.2.0`）时标记不兼容 |
| `gui.views` | 否 | 具名视图列表（至少声明一个或声明 `gui.shell`） |
| `gui.views[].id` | 是 | 视图 ID，`furry://view/{pid}/{vid}/` 路由用。v1 迁移默认 `"main"` |
| `gui.views[].title` | 否 | 侧边栏/插槽显示标题 |
| `gui.views[].icon` | 否 | 预留图标路径（当前版本不渲染，填 `null`） |
| `gui.views[].entry` | 否 | 相对 `frontend/` 的入口，默认 `index.html` |
| `gui.views[].slots` | 否 | 可嵌入插槽数组：`page` / `drawer` / `home` / `settings` / `sidebar`（空则仅作声明，不自动挂载） |
| `gui.shell` | 否 | 主壳接管声明：`{ title, entry }`（entry 默认 `index.html`） |
| `gui.native_module` | 否 | 预留原生模块路径（当前版本不加载） |

> **v1 兼容**：`api_version: 1` 的 `gui.type`（`"view"` / `"replacement"`）清单会被自动归一化为 v2——`view` → `views:[{id:"main", slots:["page"]}]`，`replacement` → `shell`。旧插件的 `furry://views/{pid}/` 与 `furry://replacement/{pid}/` URL 同样兼容。

## 4. 开发一个视图插件（最小示例）

### 4.1 目录结构

```
my-plugin/
├── plugin.json
└── frontend/
    ├── index.html
    ├── app.js
    └── style.css
```

### 4.2 plugin.json

```json
{
  "id": "com.example.versions",
  "name": "版本速览",
  "version": "1.0.0",
  "api_version": 2,
  "min_launcher_version": "0.2.0",
  "gui": {
    "views": [{ "id": "main", "title": "版本速览", "slots": ["page", "sidebar"] }]
  }
}
```

### 4.3 frontend/index.html

```html
<!DOCTYPE html>
<html lang="zh-CN">
  <head>
    <meta charset="UTF-8" />
    <meta name="viewport" content="width=device-width, initial-scale=1.0" />
    <link rel="stylesheet" href="style.css" />
  </head>
  <body>
    <h1>已安装版本</h1>
    <ul id="versions"></ul>
    <script src="app.js"></script>
  </body>
</html>
```

### 4.4 frontend/app.js — 调用后端命令

插件运行在 Tauri WebView 中，可直接使用 Tauri 注入的全局桥接：

```js
// 优先使用全局 invoke，未注入时报错提示
function invoke(cmd, args) {
  const raw =
    window.__TAURI_INTERNALS__ && window.__TAURI_INTERNALS__.invoke;
  if (typeof raw === "function") return raw(cmd, args);
  return Promise.reject(new Error("非 Tauri 环境，无法调用后端命令"));
}

async function load() {
  try {
    const versions = await invoke("list_installed");
    const ul = document.getElementById("versions");
    ul.innerHTML = "";
    versions.forEach((v) => {
      const li = document.createElement("li");
      li.textContent = `${v.id} · ${(v.size_bytes / 1024 / 1024).toFixed(1)} MB`;
      ul.appendChild(li);
    });
  } catch (e) {
    document.getElementById("versions").textContent = "读取失败：" + e;
  }
}

load();
```

### 4.5 订阅后端事件

```js
// 通过 @tauri-apps/api（需在插件包内自带依赖）或全局桥接订阅事件
async function onProgress() {
  const { listen } = await import("@tauri-apps/api/event");
  const unlisten = await listen("download-progress", (e) => {
    const p = e.payload;
    console.log(`${p.stage} ${p.percentage.toFixed(1)}%`);
  });
  // 页面卸载时调用 unlisten() 取消订阅
}
```

## 5. 多视图与插槽嵌入（v2 新能力）

一个插件可声明多个视图，分别挂载到不同位置。例如一个"皮肤库"插件同时提供首页组件、独立页面与设置项：

```json
{
  "id": "com.example.skins",
  "name": "皮肤库",
  "version": "1.0.0",
  "api_version": 2,
  "gui": {
    "views": [
      { "id": "home", "title": "今日推荐", "slots": ["home"] },
      { "id": "browser", "title": "皮肤浏览器", "slots": ["page", "sidebar"] },
      { "id": "options", "title": "皮肤设置", "slots": ["settings"] }
    ]
  }
}
```

挂载规则：

- `page` / `sidebar`：视图入口出现在侧边栏"插件视图"区域，点击后主区 iframe 加载 `furry://view/{pid}/{vid}/`
- `home` / `settings` / `drawer`：由宿主界面在对应插槽按需嵌入（当前版本宿主仅自动挂载 page 插槽）
- 同一视图可声明多个插槽（如上面 `browser` 同时挂 page 与 sidebar）

## 6. 开发主壳插件（原 replacement）

主壳插件与视图插件结构相同，声明 `gui.shell`：

```json
{
  "id": "com.example.skin",
  "name": "皮肤整合",
  "version": "1.0.0",
  "api_version": 2,
  "gui": { "shell": { "title": "皮肤整合" } }
}
```

启用后，插件入口将整体接管主窗口（侧边栏/标题栏/状态栏均由插件自行绘制）。此时插件页面地址为 `furry://shell/{id}/index.html`。

> 注意：主壳插件应自绘完整 UI（含窗口控制）。当前版本多个主壳插件同时启用时，以插件页中启用的顺序为准（后启用者生效）。

## 7. 可用的后端命令（节选）

插件可调用的常用命令（与主界面一致，见 `src-tauri/src/commands.rs`）：

| 命令 | 参数 | 返回 |
|------|------|------|
| `list_versions` | — | `VersionInfo[]`（远程 + 安装状态） |
| `list_installed` | — | `InstalledVersion[]` |
| `install_version` | `versionId` | `void`（后台，进度走 `download-progress` 事件） |
| `launch_game` | `versionId, username, dryRun` | `void`（后台，日志走 `game-log` 事件） |
| `stop_game` | — | `void` |
| `list_accounts` | — | `StoredAccount[]` |
| `get_settings` | — | `Settings` |
| `get_gui_plugins` | — | `GuiPluginInfo[]`（含 `views` / `shell` / `slots` 字段） |
| `get_game_logs` | — | `string` |

事件（通过 `@tauri-apps/api/event` 的 `listen` 订阅）：

| 事件 | 负载 | 时机 |
|------|------|------|
| `launcher-status` | `{ status, detail }` | 启动器状态变化 |
| `download-progress` | `ProgressPayload` | 下载进度 |
| `install-done` | `{ versionId, success, message }` | 安装完成 |
| `game-log` | `{ line, isStderr }` | 游戏输出行 |
| `game-exited` | `{ exitCode, crashed, message }` | 游戏退出 |
| `error-occurred` | `{ context, message }` | 错误 |

## 8. 打包与安装

插件以 **zip 包** 分发，根级必须包含 `plugin.json`：

```bash
cd my-plugin
zip -r my-plugin.zip plugin.json frontend/
```

安装方式（插件页）：

1. **URL**：填写 zip 包下载地址（`http(s)://…`），启动器下载后安装；
2. **本地路径**：填写本机 zip 包绝对路径。

安装安全校验：

- 解压使用 `enclosed_name()` 拒绝绝对路径与 `..` 穿越条目；
- `plugin.json` 必须合法；`id` 通过白名单校验；`gui` 必须声明至少一个视图或主壳（`gui.views` / `gui.shell`）；
- 覆盖安装会先删除旧目录。

管理操作：插件页支持**删除**与**启用/禁用**。禁用通过目录内 `.disabled` 标记文件实现（存在即禁用、扫描跳过）。

## 9. 开发调试建议

- **本地联调**：用 `cargo tauri dev` 启动开发模式，将插件 zip 通过插件页安装；修改后删除重装即可。
- **环境变量覆盖插件目录**：设置 `STARCLOUD_PLUGINS_DIR` 指向自定义插件目录，可绕过默认位置直接扫描。
- **浏览器预览**：视图插件可在普通浏览器中打开 `frontend/index.html` 预览样式；调用命令会失败（无 Tauri 桥接），应做降级提示（见 4.4 的 invoke 封装）。
- **排查加载失败**：`plugin.json` 解析失败会跳过该插件并输出 `跳过无效 GUI 插件清单` 日志；检查 `id` 字符集、`api_version` 与 `gui.views`/`gui.shell` 声明。

## 10. 参考实现

完整示例见仓库 `examples/gui-plugin-demo/`：

```
examples/gui-plugin-demo/
├── plugin.json          # v2 视图插件清单（api_version 2）
└── frontend/
    ├── index.html
    ├── app.js           # 调用 list_installed / get_settings
    └── style.css
```
