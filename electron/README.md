# Starcloud Minecraft Launcher · Linux

Starcloud Minecraft 启动器的 **Linux 端**（Electron + React + TypeScript）。
界面按 WinUI 3 / Fluent Design 规范实现，运行在 Linux（X11 优先，Wayland 有已知问题）。

> 内核（下载 / 安装 / 启动 / 账户 / 联机 / Nova …）在 C# 侧实现，本仓库通过
> `Starcloud.Bridge` 进程复用，**不重写一份 TS 后端**。
> 桥接源码在主仓库 [`Starcloud-Minecraft-Launcher`](https://github.com/xingmiaobjm/Starcloud-Minecraft-Launcher) 的 `dotnet/` 下。

## 快速开始

```bash
# 1. 安装依赖
npm install

# 2. 取得 C# 桥接（见下方「桥接」）
./scripts/fetch-bridge.sh

# 3. 开发（Vite + Electron，热更新）
npm run dev

# 4. 打包
npm run package        # deb + AppImage → release/
```

开发环境若在 WSL / 容器 / root 下运行，需要：

```bash
ELECTRON_DISABLE_SANDBOX=1 npm run dev
```

## 桥接（Starcloud.Bridge）

界面本身可独立运行（缺桥接时会显示空列表并报「桥接进程未运行」），
但要让下载 / 启动生效，需要把 C# 桥接发布到 `bridge-linux-x64/`：

```bash
# 已有主仓库副本时（默认路径 ../starcloud-minecraft-launcher）
./scripts/fetch-bridge.sh

# 或手动指定主仓库路径
./scripts/fetch-bridge.sh /path/to/starcloud-minecraft-launcher

# 或让脚本克隆主仓库（较慢）
STARCLOUD_CLONE=1 ./scripts/fetch-bridge.sh
```

也可以直接用环境变量指定 DLL：

```bash
STARCLOUD_BRIDGE_DLL=/abs/path/Starcloud.Bridge.dll npm run dev
```

## 工程结构

```
src/
  main/index.ts        主进程：窗口骨架（无边框 + 自绘标题栏）、C# 桥接进程管理、IPC
  preload/index.ts     contextBridge 暴露 bridge / bridgeEvents / theme / win
  renderer/
    index.html
    src/
      main.tsx         入口：先定主题再 render（避免首帧闪白）
      App.tsx          应用壳：标题栏 + 导航 + 内容区 + 主题
      nav.tsx          NavigationView（鸿蒙图标库字形 + 两个自绘 SVG）
      titlebar.tsx     Fluent 标题栏（最小化/最大化/关闭，Wayland 拖拽兜底）
      theme.ts         亮/暗/跟随系统 + 窗口底色同步
      winui.css        WinUI 3 视觉层（令牌、材质、控件、动效）
      bridge.ts        桥接客户端（浏览器调试时自动降级为 mock）
      ui.tsx           共用原子（Modal / Toast / Segmented / 右键菜单 …）
      pages/           13 个页面
      assets/          HarmonyOS Icons 字体、Inter、MC-Icons、品牌 SVG
tools/                 取证与截图脚本（非运行时依赖）
```

## 设计与移植说明

- **设计令牌**：取自 Win 端 `App.xaml` 与实际渲染值，见 [`DESIGN.md`](./DESIGN.md)。
  圆角 8/16、动效 `Pace=1.5`（进场 375ms / 退场 210ms / 悬停 300→450ms / 错峰 39ms）、
  四条缓动曲线（进场 / 退场 / 控件 / 明暗渐变）。
- **图标**：导航图标用**鸿蒙图标库字体**（与 Win 端同一份 ttf，码位一一对应）；
  「实验室」「投影管理」是**自绘 SVG**；版本行用仓库 `MC-Icons` 的等距方块图。
- **材质**：Linux 没有原生 Mica/Acrylic，用 `backdrop-filter` + 分层渐变模拟，
  `[data-blur="off"]` 可降级为纯色（软件渲染 / WSLg 下更稳）。

### Linux 平台已知问题

| 现象 | 原因 | 规避 |
|---|---|---|
| 窗口圆角被 WM 覆盖 | X11 下窗口形状由 WM 决定 | 窗口不透明，圆角交给 CSS |
| 首帧闪白 | 主题在 effect 里才写入，浏览器已合成一帧 | `main.tsx` 挂载前写入 `data-theme` |
| Wayland 下标题栏拖不动 | `-webkit-app-region` 依赖 X11 `_NET_WM_MOVERESIZE` | 标题栏内置 JS 拖拽兜底（IPC `win:moveBy`） |
| `backdrop-filter` 掉帧 | 软件渲染 / WSLg 无 GPU | 模糊只铺装饰层，可关 |
| 字体缺字 / 图标空白 | 依赖 CDN 字体 | 字体与图标字体全部本地打包 |
| root / 容器里起不来 | Chromium 沙箱不可用 | `ELECTRON_DISABLE_SANDBOX=1` |

## 打包

```bash
npm run package        # deb + AppImage
npm run package:dir    # 只出未压缩目录，便于排查
```

`electron-builder.yml` 会把 `../bridge-linux-x64` 作为 `extraResources` 打进包里
（**打包前先跑 `scripts/fetch-bridge.sh`**，否则安装包里没有桥接）。

产物在 `release/`：

- `starcloud-launcher-linux_x.y.z_amd64.deb`
- `Starcloud Launcher-x.y.z.AppImage`

## 许可

AGPL-3.0，见 [LICENSE](./LICENSE)。
