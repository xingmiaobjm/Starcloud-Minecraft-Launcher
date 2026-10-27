# 安卓端渲染器接入（MG / MGL）· 简版

状态：待实现 · 2026-10-25

## 目标

在现有 `Zink / Gl4es` 之外加 **MG**、**MGL** 两档渲染后端，可在设置里选，自动选优，失败自动换下一档。

启动核心（JVM 链、LaunchSpec、组件部署）已完成，**不动**。

## 三件事

1. **组件**：`android/components/fetch-components.ps1` 增加 MG/MGL 两个 zip 包（只收 `arm64-v8a/*.so`），同步两份 `manifest.json` 的条目（name/version/url/sha256/archive/license）。
2. **后端**：`RuntimeSelector.kt` 的 `RenderBackend` 加两项，`select()` 加 GPU 能力入参；`LaunchSpecBuilder.kt` 加两个 `when` 分支写环境变量（`POJAV_RENDERER` / `LIBGL_ES` / `POJAVEXEC_EGL` / GL 库名）。已有两档不许改。
3. **回退**：60 秒内没进主菜单或日志出现 GL/EGL 错误 → 记为失败后端，下次自动跳过，落到下一档。

## 验收

- 单测：四个后端的选择规则、显式选择优先、库名映射互不重复。
- 真机：Adreno / Mali × 1.12.2 / 1.20.1 / 1.21.x / 26.x 各跑一次能进主菜单。

## 待填（不填不能开工）

MG、MGL 各自的：上游仓库、版本号、`.so` 列表、EGL 库名、LICENSE、是否依赖 Vulkan。

合规：AGPL/LGPL 类组件不得打进 apk，改运行时下载。
