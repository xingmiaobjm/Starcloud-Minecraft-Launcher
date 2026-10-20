# Starcloud Minecraft Launcher · Android

Starcloud 启动器的 Android 客户端。桌面端（Windows / Linux）在另一个仓库
[Starcloud-Minecraft-Launcher](https://github.com/xingmiaobjm/Starcloud-Minecraft-Launcher)。

## 结构

```
Starcloud.Launcher.Compose/    当前前端：Kotlin + Jetpack Compose
  app/src/main/java/cn/starcloud/launcher/compose/
    MainActivity.kt            入口：横屏 + 左侧五格竖排导航
    screens/                   页面（主页 / 版本 / 下载 / 存档 / Nova / 设置 …）
    data/                      版本清单、下载器、组件部署
    game/                      启动参数、JVM 拉起、运行时选择
  app/src/main/java/cn/starcloud/launcher/web/
    WebUiActivity.kt           Web UI 壳（Phase 0）：全屏 WebView
    StarcloudBridge.kt         Web ↔ Kotlin 桥（appInfo / versions / log）
  app/src/main/assets/web/     Web UI 页面（后续接 React 产物）
  app/src/main/cpp/            libstarcloudjvm.so：JNI_CreateJavaVM 封装

src/Starcloud.Android.Core/    安卓内核：下载引擎、版本解析、启动参数
src/Starcloud.Android.Host/    宿主层：JVM 嵌入、SDL、输入法、系统调用
src/Starcloud.Launcher.Uno/    早期 Uno 前端（已停更，保留参考）
src/Starcloud.Launcher.Phone/  更早的移动壳
spike-uno/ spikes/             技术验证（JVM 嵌入、SDL 等）
components/                    fetch-components.ps1：按需下载 JRE / LWJGL / Mesa
tests/                         安卓内核单测
```

## 构建

前置：JDK 17、Android SDK（`compileSdk 36`）、NDK（`libstarcloudjvm.so` 需要 CMake 3.22.1）。

```bash
cd Starcloud.Launcher.Compose

# 组件包（JRE / LWJGL / Mesa，约 340 MB，不入库）
powershell -ExecutionPolicy Bypass -File ../components/fetch-components.ps1

./gradlew assembleDebug          # 或本机 gradle 8.7+
```

产物：`app/build/outputs/apk/debug/app-debug.apk`。

## 界面路线

同一份 Web 前端 + 两个容器：桌面用 Electron，安卓用 WebView（Electron 本身跑不到安卓）。
安卓侧的 Web 壳已经落地 Phase 0，不设启动入口，验证方式：

```bash
adb shell am start -n cn.starcloud.launcher.compose/cn.starcloud.launcher.web.WebUiActivity
adb logcat -s StarcloudWeb
```

详见 `docs/2026-10-21-android-web-ui-shell-design.md`。

## 真机走查

设备接上后一条命令：

```powershell
powershell -ExecutionPolicy Bypass -File tools/walkthrough.ps1
# 无线调试：-Serial 192.168.x.x:5555
```

脚本：等设备 → `install -r` → `am start WebUiActivity` → 截屏 + 抓 `StarcloudWeb` 日志。

**当前状态（2026-10-21）**：真机未接入；已在 **x86_64 模拟器（nebula_api36）** 上跑通
Web 壳与桥，见下方「模拟器走查结果」。

不依赖设备、已完成的验证：

| 项 | 结果 |
| --- | --- |
| `gradle :app:assembleDebug` | BUILD SUCCESSFUL，APK 12.3 MB |
| APK 里 `WebUiActivity` | 已注册（`exported=false`） |
| APK 里 Web 资源 | 16 个（`assets/web/app/index.html` + bundle + 字体 + 图标） |
| APK 里原生库 | `libstarcloudjvm.so` / `libstarcloudwindow.so` 均在 |
| 桥协议（JS 侧） | Node 模拟原生跑通 request/reply、事件、错误码映射 |
| React 产物 | Chromium 1280×800 无头渲染首页正常（mock 桥） |

### 模拟器走查结果（2026-10-21）

模拟器 x86_64（`gradle :app:assembleDebug -PemuAbi` 打 x86_64 包）：

| 项 | 结果 |
| --- | --- |
| Web 壳启动 | ✅ `am start WebUiActivity` 进入，React 首页完整渲染 |
| 导航 / 页面 | ✅ 首页、版本、下载、存档均正常（数据来自真实桥：账户 Player、Java 未安装、清单 920 个版本） |
| `catalog.versions` | ✅ 拉到 920 个版本；**发现 bug**：缺 `installed` 字段导致下载页 `TypeError`，已修 |
| `versions.install` | ⚠ 桥链路通（返回结构化 `install-failed`），但模拟器网络被宿主机 TUN 代理掐断，实际下载未完成 |
| `launch.start` | ⚠ 桥链路通（收到 `game.log` 事件 + `launch-failed`），失败原因是组件包未部署（`components/manifest.json` 不存在） |
| 顺带发现 | **下载器 `USE_LOCAL_PROXY` 被写死为内网代理 `192.168.10.251:8889`**，会让所有真实用户下载失败，已改为默认关闭 |

真机待确认：组件包部署后 `versions.install` 能真正装完、`launch.start` 能拉起 SDL 活动。

渲染层调用 28 个桥方法，安卓已实现 8 个（catalog/install/launch/settings/saves 列表），
其余 20 个（账号、内容库、Java、多人、Nova、插件、投影、备份删除、版本详情）返回
`unsupported` —— 这些功能安卓端尚未实现，对应页面会走到错误分支。

## 约定

- 版本号唯一来源：`Starcloud.Launcher.Compose/app/build.gradle.kts` 的 `versionName`。
- 业务逻辑只在 `Android.Core` / `compose/data` / `compose/game`，界面层不写业务。
- 提交前跑一次 `gradle :app:assembleDebug`，要求 0 错误。
- 组件包、构建产物、反编译参考（`spikes/fcl-src`）不入库。
