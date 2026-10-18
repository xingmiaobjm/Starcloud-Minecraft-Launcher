# Starcloud Launcher · Linux（Electron + Fluent UI）

Linux 端界面与 Windows 端对齐：**同一套 C# 后端**（`Starcloud.Bridge` 复用 `Starcloud.Core`），
前端换成 **Electron + React + Fluent UI v9（微软 Web 端 Fluent）**，视觉更接近 WinUI 3。

```
electron/src/main        主进程：拉起 C# 桥接进程 + IPC（请求/事件）
electron/src/preload     contextBridge 安全桥（request / events）
electron/src/renderer    12 个页面（Fluent UI v9）
bridge-linux-x64/        C# 桥接宿主（dotnet publish 产物，不入库）
```

## 运行

### 开发

```bash
# 一次性：WSL 里装 Node（Ubuntu）
sudo apt-get install -y nodejs npm

# 装依赖（国内镜像；CEF/Electron 大文件走 ELECTRON_MIRROR）
cd ~/dev/starcloud-electron        # 建议放在 Linux 文件系统，别放 /mnt/c（npm install 极慢）
ELECTRON_MIRROR=https://npmmirror.com/mirrors/electron/ npm install

# 先发布 C# 桥接（仓库根，Windows 侧执行一次）
dotnet publish dotnet/src/Starcloud.Bridge -c Release -r linux-x64 \
  --self-contained false -o bridge-linux-x64

# 跑起来（WSL 里以 root 运行需要关沙箱）
DISPLAY=:0 ELECTRON_DISABLE_SANDBOX=1 \
STARCLOUD_BRIDGE_DLL=$PWD/../bridge-linux-x64/Starcloud.Bridge.dll \
npm run dev
```

### 打包

```bash
ELECTRON_MIRROR=https://npmmirror.com/mirrors/electron/ \
ELECTRON_BUILDER_BINARIES_MIRROR=https://npmmirror.com/mirrors/electron-builder-binaries/ \
npm run package
```

产物：`release/starcloud-launcher-linux_0.1.0_amd64.deb`（apt 安装）与
`release/Starcloud Launcher-0.1.0.AppImage`（免安装）。C# 桥接随包放在
`resources/bridge-linux-x64/`，无需单独部署。

## 页面

| 页面 | 说明 |
|---|---|
| 首页 | 已安装版本列表（点击选中）+ 账户卡 + 黄色启动按钮 + 实时日志 |
| 版本 | 打开目录 / 设为默认 / 删除（继承与运行中保护由 C# 侧校验） |
| 下载 | 远端清单、安装进度（事件驱动） |
| 存档 | 跨版本世界、备份、删除 |
| 账户 | 离线账户增删、设为默认 |
| 运行状态 / 启动器日志 | 会话状态与 `game.output` 实时滚动 |
| 联机 | 陶瓦 / 第三方 SDK 快照、建房/进房/停止、核心安装进度 |
| 实验室 | litematic 解析 + canvas 等距体素 3D 预览 + 世吞模板生成 |
| Nova | 流式对话、工具调用提示、自接模型设置 |
| 插件 | .mclplug 安装、JS/Python/声明式运行时、免责确认 |
| 设置 | 游戏目录 / Java / 内存 / 下载源（写入 C# 共享设置） |

## 动效

页面切换淡入上移、列表行错峰入场（24ms 递增）、hover 过渡、启动按钮按压缩放与呼吸光晕、
运行中状态点脉冲、Toast 从下弹入、骨架屏加载、页面级错误边界。

## 快捷键

`Ctrl+1..9` 切换前 9 个页面，`Ctrl+0` 打开设置。

## 与 Flutter 版的关系

`flutter/` 目录是此前的 Flutter + fluent_ui 实现，功能已全部覆盖，保留作参考；
Linux 交付版本以 `electron/` 为准。

## 已知

- WSL 下 GPU 进程会报错，走软件渲染（不影响功能）。
- 首次 `npm install` 需要 Electron 运行时（约 100MB），建议放 Linux 文件系统。
