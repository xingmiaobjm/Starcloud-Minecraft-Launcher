# Starcloud Minecraft Launcher — GUI 插件开发指南

> 版本 0.2.0 | 2026-08-09  
> 面向插件开发者：如何为启动器编写 GUI 插件（view / replacement 双通道）。

---

## 1. 插件机制概述

启动器 GUI 支持两类插件：

| 类型 | 说明 | 挂载方式 |
|------|------|----------|
| `view` | 视图插件，作为独立页面 | 侧边栏"插件视图"区域出现入口，点击后在主区域以 iframe 加载 |
| `replacement` | 替换插件，整体接管主界面 | 插件启用后接管整个窗口 shell（默认 GUI 兜底） |

插件前端由启动器通过自定义协议 `furry://` 托管静态资源，无需自带服务器：

```
furry://views/{plugin_id}/{path}       # view 插件资源
furry://replacement/{plugin_id}/{path} # replacement 插件资源
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

## 3. plugin.json 清单

```json
{
  "id": "com.example.myplugin",
  "name": "我的插件",
  "version": "1.0.0",
  "api_version": 1,
  "min_launcher_version": "0.2.0",
  "gui": {
    "type": "view",
    "title": "插件标题",
    "icon": null,
    "entry": "index.html"
  }
}
```

字段说明：

| 字段 | 必填 | 说明 |
|------|------|------|
| `id` | 是 | 唯一标识。合法字符：字母/数字/`.`/`_`/`-`，≤128 字符；**禁止**空、`.`/`..`、路径分隔符（防路径穿越） |
| `name` | 是 | 显示名称 |
| `version` | 是 | 插件版本号（如 `1.0.0`） |
| `api_version` | 否 | 插件 API 版本，默认 `1`。必须等于启动器当前支持的版本（`1`），否则标记不兼容 |
| `min_launcher_version` | 否 | 最低启动器版本，低于当前启动器版本（`0.2.0`）时标记不兼容 |
| `gui.type` | 是 | `"view"` 或 `"replacement"` |
| `gui.title` | 否 | 侧边栏显示标题（view） |
| `gui.icon` | 否 | 预留图标路径（当前版本不渲染，填 `null`） |
| `gui.entry` | 否 | 相对 `frontend/` 的入口，默认 `index.html` |
| `gui.native_module` | 否 | 预留原生模块路径（当前版本不加载） |

## 4. 开发一个 view 插件（最小示例）

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
  "api_version": 1,
  "min_launcher_version": "0.2.0",
  "gui": { "type": "view", "title": "版本速览", "entry": "index.html" }
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

## 5. 开发 replacement 插件

replacement 插件与 view 插件结构相同，仅 `gui.type` 为 `"replacement"`：

```json
{
  "id": "com.example.skin",
  "name": "皮肤整合",
  "version": "1.0.0",
  "api_version": 1,
  "gui": { "type": "replacement", "title": "皮肤整合", "entry": "index.html" }
}
```

启用后，插件入口将整体接管主窗口（侧边栏/标题栏/状态栏均由插件自行绘制）。此时插件页面地址为 `furry://replacement/{id}/index.html`。

> 注意：replacement 插件应自绘完整 UI（含窗口控制）。当前版本两个 replacement 插件同时启用时，以插件页中启用的顺序为准（后启用者生效）。

## 6. 可用的后端命令（节选）

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
| `get_gui_plugins` | — | `GuiPluginInfo[]` |
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

## 7. 打包与安装

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
- `plugin.json` 必须合法；`id` 通过白名单校验；`gui.type` 必须为 `view`/`replacement`；
- 覆盖安装会先删除旧目录。

管理操作：插件页支持**删除**与**启用/禁用**。禁用通过目录内 `.disabled` 标记文件实现（存在即禁用、扫描跳过）。

## 8. 开发调试建议

- **本地联调**：用 `cargo tauri dev` 启动开发模式，将插件 zip 通过插件页安装；修改后删除重装即可。
- **环境变量覆盖插件目录**：设置 `STARCLOUD_PLUGINS_DIR` 指向自定义插件目录，可绕过默认位置直接扫描。
- **浏览器预览**：view 插件可在普通浏览器中打开 `frontend/index.html` 预览样式；调用命令会失败（无 Tauri 桥接），应做降级提示（见 4.4 的 invoke 封装）。
- **排查加载失败**：`plugin.json` 解析失败会跳过该插件并输出 `跳过无效 GUI 插件清单` 日志；检查 `id` 字符集与 `gui.type` 取值。

## 9. 参考实现

完整示例见仓库 `examples/gui-plugin-demo/`：

```
examples/gui-plugin-demo/
├── plugin.json          # view 插件清单
└── frontend/
    ├── index.html
    ├── app.js           # 调用 list_installed / get_settings
    └── style.css
```
