# 安卓端 Web UI 壳（Electron 同构）设计

> 日期：2026-10-21　状态：Phase 0 已落地
> 相关：`docs/PROJECT-MAP.md`、`electron/`（Linux 桌面壳）、`android/Starcloud.Launcher.Compose/`

## 1. 为什么不是「Electron 跑在安卓上」

Electron 自带 Chromium **和 Node 主进程**，主进程是桌面进程模型（窗口、托盘、文件系统、子进程），
没有 Android 运行时。安卓上无法安装 Electron 应用。

可行的等价做法是：**同一份 Web 前端，两个容器**。

```
                 ┌──────────────────────────┐
                 │  web UI（React/Vite 一套）│
                 └───────┬──────────┬───────┘
                         │          │
          ┌──────────────▼──┐   ┌───▼──────────────────┐
          │ Electron（桌面） │   │ WebView（安卓）       │
          │ main + preload  │   │ StarcloudBridge      │
          └──────────────┬──┘   └───┬──────────────────┘
                         │          │
                 ┌───────▼──────────▼───────┐
                 │ 原生能力：Kotlin / .NET   │
                 └──────────────────────────┘
```

桌面侧已有 `electron/`（React + Fluent UI）；安卓侧本次补的是 WebView 容器与桥。
**两边暴露同一组 JS 方法**，页面代码不需要为平台分叉。

## 2. 复用什么、替换什么

| 层 | 现状 | 本次决定 |
| --- | --- | --- |
| Kotlin 业务层（下载、版本解析、启动参数、JVM 拉起） | `compose/data`、`compose/game`、`Android.Host` | **原样复用**，一行不改 |
| 原生库（`libstarcloudjvm.so`、SDL、LWJGL/Mesa） | `app/src/main/cpp`、`jniLibs` | 原样复用 |
| 现有 Compose 界面 | `compose/screens/*` | **保留**，仍是默认入口 |
| 新 Web 界面 | `assets/web/` | 新增，先做占位页，后续接 React 产物 |

原则：**只替换最上面那层皮**。Compose 页面在迁移期继续可用，Web 壳通过独立 Activity 进入，
两者共享同一份 Kotlin 仓储，不存在"两套业务逻辑"。

## 3. 已落地的 Phase 0

| 文件 | 作用 |
| --- | --- |
| `app/src/main/java/cn/starcloud/launcher/web/WebUiActivity.kt` | 全屏 WebView，加载 `assets/web/index.html`，注入桥 |
| `.../web/StarcloudBridge.kt` | `@JavascriptInterface`：`appInfo()` / `versions()` / `log()` |
| `app/src/main/assets/web/index.html` | 占位页：展示已安装版本，验证通道 |
| `AndroidManifest.xml` | 注册 `WebUiActivity`（`exported=false`，不是 launcher 入口） |

进入方式（不影响正常启动）：

```bash
adb shell am start -n cn.starcloud.launcher.compose/cn.starcloud.launcher.web.WebUiActivity
adb logcat -s StarcloudWeb
```

已验证：`gradle :app:assembleDebug` 通过，APK 产出正常（含原生库，debug 160 MB）。

## 4. 桥接契约（两端共用）

约定：**只传字符串**，参数与返回值都是 JSON 文本；方法名小驼峰，域用点号分组。
这样 Electron 的 `preload` 与安卓的 `@JavascriptInterface` 可以用同一份 TypeScript 声明。

| 方法 | 参数 | 返回 |
| --- | --- | --- |
| `appInfo()` | — | `{name,version,platform,sdk,root}` |
| `versions()` | — | `{versions:[{id,sizeBytes}]}` |
| `log(msg)` | string | — |

后续按能力补齐（括号内是首版要接的）：

- `versions.install(id)`、`versions.remove(id)`（下载与删除）
- `launch.start(id)`（复用 `LaunchSpecBuilder` + `StarcloudJvm`）
- `settings.get()` / `settings.set(key,value)`（对齐 Windows 端 `LauncherSettings` 键名）
- `nova.chat(text)`（走 `Starcloud.Bridge` 的同一套 JSON 协议）

## 5. 迁移路线

| 阶段 | 内容 | 验收 |
| --- | --- | --- |
| P0（已完成） | WebView 容器 + 桥 + 占位页 | 真机能进页面、能列出已装版本 |
| P1 | 把 `electron/src/renderer` 拆成可复用包（`ui/`），去掉 `window.api` 依赖，改为注入式 `bridge` | 同一份 bundle 在浏览器、Electron、WebView 都能跑 |
| P2 | 桥补齐 install / launch / settings，Web 壳能完整走通「装一个版本 → 启动」 | 真机跑通一条完整链路 |
| P3 | Web 壳设为默认入口，Compose 保留为「兼容界面」开关 | 用户可切回 Compose |
| P4 | 移除 Compose 界面代码 | — |

## 6. 风险与对策

1. **WebView 与游戏抢资源**：启动游戏前把 WebView 置后台（`onPause` + `pauseTimers`），
   退出游戏再恢复。
2. **大文件/长时间任务**：桥只能同步返回字符串，下载进度用
   `evaluateJavascript("window.__bridge_event(...)")` 反向推送，不走轮询。
3. **安全**：`addJavascriptInterface` 只在加载 `file:///android_asset/` 时启用；
   不加载远程页面，桥方法全部做参数校验。
4. **构建体积**：React 产物进 `assets/`，需要 `npm run build` 后拷贝；先手工拷贝，
   P1 起用 Gradle 任务自动同步，避免产物与源码不一致。
