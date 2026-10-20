# Android 端交接说明

> 2026-10-21　仓库：`xingmiaobjm/Starcloud-Minecraft-Launcher-for-Android`
> 状态：Web UI 壳与安装链路已端到端验证；**仅剩 launch.start 需要 arm64 真机**。

## 1. 现在是什么形态

安卓端保留两套前端，共用同一份 Kotlin 业务层：

| 前端 | 入口 | 状态 |
| --- | --- | --- |
| Compose（原有） | `MainActivity`（launcher） | 保留，默认入口，功能完整 |
| Web UI 壳（新增） | `WebUiActivity` | Phase 1：React 产物 + 完整桥 |

Electron 只能跑桌面，所以"Electron 重构 UI"在安卓上的落地方式是
**同一份 Web 前端 + 两个容器**：桌面用 Electron，安卓用 WebView，两边都调
同一组 `window.bridge.request(method, params)`。

## 2. 已验证的（带证据）

| 项 | 怎么验的 | 结果 |
| --- | --- | --- |
| Web 壳渲染 | 模拟器 `nebula_api36`（x86_64，`-PemuAbi` 打包）`am start` | React 首页、导航 11+2 项、版本/下载/存档页全部正常 |
| 桥数据 | 同上，数据来自真实 Kotlin 侧 | 清单 920 个版本、离线账户、Java 未安装状态 |
| `versions.install` 全流程 | `tools/fake-repo` + `adb reverse` + `http.server` | `install.progress` 五阶段走完，`{"ok":true,"message":"安装完成：starcloud-test"}`，`catalog.installed` 出现该版本 |
| `launch.start` 链路 | 同上 | 收到 `game.log` 事件流后因 `components/manifest.json` 缺失中止（组件包未生成） |

### 走查过程中修掉的真 bug

1. `catalog.versions` 缺 `installed` 字段 → 下载页 `TypeError` 白屏。
2. 下载器 `USE_LOCAL_PROXY` 写死内网 `192.168.10.251:8889` → **所有真实用户下载必失败**。
3. asset 索引写盘前没有 `mkdirs` → 首次安装 `ENOENT` 失败（假仓库的空索引暴露）。

## 3. 唯一未完成项：launch.start 拉起 SDL

**为什么模拟器做不了**：JVM（`libstarcloudjvm.so`）与 SDL/JWJGL/Mesa 组件只有
arm64-v8a，模拟器是 x86_64，`System.load` 阶段就不可能过。

**需要**：一台 arm64 安卓真机（Android 8+，USB 调试或无线调试）。

### 三步跑完

```powershell
# 1) 组件包（需要 fcl-1.3.3.6-arm64.apk 与 zalith2-2.6.1-arm64.apk 放进 components/pkgs/）
powershell -ExecutionPolicy Bypass -File components/fetch-components.ps1
#    产物拷进 Starcloud.Launcher.Compose/app/src/main/assets/components/

# 2) 构建 arm64（不要带 -PemuAbi）
cd Starcloud.Launcher.Compose
gradle :app:assembleDebug

# 3) 装包 + 起壳 + 截屏 + 日志
powershell -ExecutionPolicy Bypass -File ..\tools\walkthrough.ps1
```

### 验收判据

```powershell
# 启动链：应看到 game.log 事件推进到 JVM 拉起/SDL 活动，且无 launch-failed
adb shell am start -n cn.starcloud.launcher.compose/cn.starcloud.launcher.web.BridgeSelfTestActivity `
    --es method launch.start --es id <已安装版本> --es player Tester
adb logcat -s ScSelfTest

# 通过的表现：ScSelfTest 里持续出现 game.log（[flow] 阶段推进），
# 前台出现 SdlGameActivity（dumpsys activity | findstr SdlGameActivity）
```

## 4. 调试设施（都只在 debug 生效）

| 设施 | 用途 |
| --- | --- |
| `-PemuAbi` | 打 x86_64 包，便于在模拟器验证 UI 与桥 |
| `src/debug/AndroidManifest.xml` | 让 `WebUiActivity` 可被 `adb am start` 进入 |
| `BridgeSelfTestActivity` | `am start` 直接调桥，结果写 logcat（tag `ScSelfTest`）；支持 `--es id/player/proxy/manifest` |
| `DownloadDebug.proxyBase` / `.manifestUrl` | 下载走本地代理 / 清单指向假仓库 |
| `Starcloud.Launcher.Compose/tools/forward_proxy.py` | 宿主机 TUN 掐断设备出网时的转发代理（含 `/v1/objects/` 路由、`-4`、`-L`） |
| `Starcloud.Launcher.Compose/tools/fake-repo/` | 最小假版本仓库，用于验证安装管线 |
| `tools/check-bridge.mjs` | 比对渲染层调用的方法与 Kotlin 已实现的方法 |

## 5. 已知限制与后续

- **桥覆盖 22 / 43**：未实现的集中在 `content.*`（内容库）、`nova.*`（云模型）、
  `multiplayer.*`、`plugins.*`（安卓无插件运行时）、`version.settings` 那组细节字段、
  各 `*.openFolder`（安卓无文件管理器）。这些大多需要安卓端先有对应功能，不是补映射。
- **宿主机加速器 TUN** 会掐断模拟器出网，真机走真实网络不受影响。
- **组件包不入库**，来源是两个第三方 APK（体积与许可原因）。
- **手机竖屏布局**：Web UI 是桌面比例，手机上偏挤，需要一轮响应式适配（P3）。

## 6. 提交线索

- 初始导入 `a2185ce`（662 文件 45.6 MB）
- 桥协议 `11be9fc` · React 产物接入 `faa9565` · settings/player `1a8ae3c`
- 模拟器走查修复 `a198a33` · 代理与调试钩子 `2ec3686`/`2149740` · 组件引导 `482dc07`
- 主仓库对应同步至 `9ad189d`
