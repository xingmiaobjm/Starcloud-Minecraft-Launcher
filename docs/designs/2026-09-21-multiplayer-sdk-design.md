# 联机：陶瓦联机兼容 + 第三方联机 SDK（2026-09-21）

## 1. 目标

1. 让 .NET 版重新具备**陶瓦联机（Terracotta | 陶瓦联机）**能力（旧 Tauri 架构有、重写后缺失）：
   安装核心、创建房间、加入房间、看状态、看日志、停止联机。
2. 把「联机」做成**可换提供方**的能力：第三方联机 SDK 不必等启动器发版就能接进来。

非目标：不自己实现打洞 / 中继；不代管用户账户；不修改任何第三方核心的二进制。

## 2. 为什么是「本地核心进程 + 本地 HTTP 控制面」

主流联机方案对启动器暴露的接口高度同形，陶瓦联机与各家第三方 SDK 都是这套：

```
启动器 ──spawn──> 联机核心（未修改的官方二进制）
                   │  1) 把本地 HTTP 端口写进「端口文件」
                   │  2) 暴露 GET 接口：查状态 / 建房 / 进房 / 回空闲 / 关进程 / 取日志
启动器 ──HTTP────> 127.0.0.1:<port>
```

陶瓦联机（`v0.4.2` 实测）：

| 环节 | 事实 |
| --- | --- |
| 启动 | `terracotta.exe --hmcl <portFile>`，核心把 `{"port":N}` 写进该文件 |
| 平台包 | `terracotta-<ver>-<platform>-pkg.tar.gz`，内含 `terracotta-<ver>-<platform>.exe` + `VCRUNTIME140.DLL`，可执行文件需统一重命名（只改文件名、不动二进制） |
| 查状态 | `GET /state` → `{"state":"waiting\|host-scanning\|host-starting\|host-ok\|guest-connecting\|guest-starting\|guest-ok\|exception", ...}` |
| 建房 | `GET /state/scanning?player=<名>&public_nodes=<节点>`（可重复） |
| 进房 | `GET /state/guesting?room=<码>&player=<名>&public_nodes=<节点>` |
| 回空闲 | `GET /state/ide` |
| 关进程 | `GET /panic?peaceful=true` |
| 取日志 | `GET /log?fetch=true` |
| **Windows 细节** | `--hmcl` 只是**委派进程**：它再拉起一个无窗口的 `--hmcl2` 子进程后自己退出。所以「会话是否在跑」必须看**端口文件 + `/state` 是否响应**，不能看进程句柄 |
| 单实例锁 | 核心有全局互斥：第二个实例会连到已在跑的那个并把它的端口写进我们的端口文件（用户自己开着的陶瓦联机也能被复用） |

## 3. 分层

```
MultiplayerPage（WinUI 页面：只画状态 + 把点击翻译成一次操作）
        │
MultiplayerService（提供方注册表 / 设置持久化 / 状态轮询 / 调度中心集成）
        │
IMultiplayerProvider（联机 SDK 契约：装核心 / 起会话 / 建房 / 进房 / 停 / 日志 / 节点）
        │
ProtocolMultiplayerProvider（按「协议描述」驱动的唯一实现）
   ├── MultiplayerSdkProtocol.Terracotta        ← 内置：陶瓦联机
   └── MultiplayerSdkManifests.LoadAll(...)     ← 第三方：<数据目录>/multiplayer/providers/*.json
```

核心设计决定：**「协议描述」是一个数据结构，而不是接口实现**。陶瓦联机与第三方 SDK 的差异
全部收敛到 `MultiplayerSdkProtocol` 的常量（下载地址、启动参数、HTTP 路径、状态映射），
于是两者共用同一份会话/安装/状态代码，也就不存在「内置实现慢慢和第三方实现跑偏」的问题。

## 4. 第三方联机 SDK 的两种接入方式

### 4.1 清单式（零代码，推荐）

把一份 JSON 放进 `<数据目录>/multiplayer/providers/`（联机页有「打开清单目录」按钮），
重启启动器后出现在「联机方案」下拉里。最小清单：

```json
{
  "id": "demo-link",
  "displayName": "示例联机",
  "vendor": "Demo SDK",
  "vendorUrl": "https://example.com",
  "description": "一句话说明",
  "version": "1.2.3",
  "executableName": "demo-core.exe",
  "downloadUrls": ["https://example.com/demo-{version}-{platform}.tar.gz"],
  "archiveKind": "tar.gz",
  "launchArguments": ["--hmcl", "{port_file}"],
  "portFile": "http.json",
  "apiBase": "http://127.0.0.1:{port}",
  "statePath": "/state",
  "idlePath": "/state/ide",
  "hostPath": "/state/scanning",
  "joinPath": "/state/guesting",
  "logPath": "/log?fetch=true",
  "shutdownPath": "/panic?peaceful=true",
  "phaseMap": { "waiting": "idle", "host-ok": "host-ready", "boom": "failed" }
}
```

字段说明（未列出的都有默认值，默认即陶瓦同形）：

| 字段 | 说明 |
| --- | --- |
| `id` | 稳定标识，也是核心目录名；只允许字母数字与 `-` `_`（防路径穿越），不可与 `terracotta` 冲突 |
| `downloadUrls` | 按顺序尝试（国内镜像优先），占位符 `{version}` `{platform}` `{arch}` |
| `archiveKind` | `tar.gz` / `zip` / `raw`（raw = 直接下载一个可执行文件） |
| `executableName` | 解压后统一重命名成的固定文件名 |
| `launchArguments` | 占位符 `{port_file}` = 端口文件绝对路径 |
| `portFile` / `portJsonKey` | 端口文件相对路径与其中的端口字段名 |
| `apiBase` / `*Path` | 本地控制面地址模板（`{port}`）与各动作路径；`shutdownPath` 留空表示"停联机时不关它的进程" |
| `nodesQueryKey` / `nodeListUrl` | 公共节点查询参数名与节点列表地址（空 = 不接受外部节点 / 不拉取） |
| `*Field` | 状态 JSON 的字段名（`stateField` / `roomField` / `addressField` / `playersField` / `errorTypeField`） |
| `phaseMap` | 原始状态字符串 → 归一化阶段：`idle` `host-scanning` `host-starting` `host-ready` `guest-connecting` `guest-entering` `guest-ready` `failed` |
| `errorMap` | 异常类型 → 用户可读文案（中文原文即本地化键，界面走 `Loc.T` 翻译） |

清单只描述"装什么、怎么起、怎么问状态"，不含任何可执行代码；解压带路径穿越防护
（拒绝绝对路径、`..`、盘符），`id` 也会做字符白名单校验。

### 4.2 代码式（深度集成）

第三方 SDK 若能提供 .NET 程序集，直接实现 `IMultiplayerProvider` 并调用
`MultiplayerService.Register(provider)`；同 id 时内置提供方优先，界面与设置无需改动。

## 5. 会话与停止语义

- **探活（`ProbeSessionAsync`）**：只用「已知端口 / 端口文件 + `/state` 响应」判断，**不启动新进程**
  ——刷新页面不会顺手把核心拉起来。上次启动器留下的核心若还活着，会被自动接管（复用它的端口）。
- **停止联机（`StopSessionAsync`）**：先 `GET /state/ide` 回空闲，再决定要不要关进程：
  只有**本启动器拉起的**核心才发 `shutdownPath`。用户自己开着的陶瓦联机不会被启动器杀掉。
- **关闭本进程**：不主动杀核心——核心独立于启动器存活，下次进来还能接管（也符合"用户自己开的窗口自己管"）。
- 第三方清单不写 `shutdownPath` 时，停止联机只回空闲，进程留着（不猜别家的接口）。

## 6. 必须绕开系统代理（实测踩坑）

不少玩家开着 Clash / mihomo 之类的系统代理（本机实测系统代理为 `http://127.0.0.1:26561/`）。
.NET 的 `HttpClient` 默认**连访问本机回环地址也会走系统代理**，代理对 localhost 目标直接回 404 ——
现象是「核心明明起来了，状态一直查不到，30 秒后报启动超时」。

因此联机控制面固定用 `SocketsHttpHandler { UseProxy = false }`（`curl` 同请求 200、默认 `HttpClient` 404 已实测对比）。

## 7. 授权与合规

- Terracotta 以 AGPL-3.0-or-later 发布，另附**聚合例外条款**：允许启动器下载并
  原样运行官方发布包、通过其本地接口驱动，而启动器本身可保持原许可。
- 本项目据此保持 MIT：只**下载官方包 + 解压 + 重命名可执行文件**（不改二进制内容、不重链接、
  不静态链接），且联机页底部显著标注「联机由 Terracotta | 陶瓦联机 提供技术支持」。
- 第三方 SDK 同理：清单只指向厂商自己的发布地址，启动器不改其二进制。

### 7.1 联机核心一律「装完启动器之后再下载」（硬约束）

**任何第三方联机核心都不得进入便携版 zip / NSIS 安装器 / 仓库。** 流程固定为：

```
用户装好启动器（包里没有任何联机二进制）
        ↓  第一次点「安装联机核心」
启动器运行时从厂商官方 Releases 下载平台包 → 解压到 %APPDATA%\starcloud-launcher\multiplayer\core\<id>\
```

理由：AGPL 的聚合例外只覆盖"运行用户自己下载的官方二进制"，把二进制塞进我们的安装包等于
**再分发**，那就得连带承担 AGPL 的源码提供义务，也会把本项目的 MIT 许可拖进去。

配套的三道保险：

1. 启动器代码里没有任何内置核心：核心只存在于数据目录，随装随下（`ProtocolMultiplayerProvider.InstallCoreAsync`）；
2. `tools/build-installer.ps1` 在 publish 之后加了**打包闸门**：只要产物里出现
   `terracotta*` / `easytier*` / `multiplayer/core`，直接构建失败并打印命中路径；
3. `.gitignore` 忽略 `terracotta*.exe`、`easytier*`、`**/multiplayer/core/`，防止有人把核心
   丢进仓库再被打包规则扫进去。

「复用旧版（Tauri 架构）已下载的核心」不违反这条：那是用户本机上一次运行时下载的产物，
仍在数据目录里，只是复制到新目录，不随我们的分发包走。

## 8. 数据目录

```
<数据目录>/starcloud-launcher/multiplayer/
├── providers/                    第三方 SDK 清单（*.json）
├── core/<providerId>/            各提供方核心（terracotta.exe + version.txt）
├── run/<providerId>/http.json    会话期端口文件
└── download/                     平台包下载缓存（装完即删）
```

设置新增：`multiplayer_provider`（当前提供方）、`multiplayer_nickname`（玩家名，空 = 跟随当前账户）、
`multiplayer_node_region`（`auto` / `cn` / `global`，同房间玩家应接同一地区节点）。

## 9. 界面

导航栏新增「联机」（首页「好友联机」卡片同一入口）。

**排版原则：一屏只留「现在要做什么」。** 常驻只有两块——

1. 状态卡：核心 / 会话 / 当前状态（一行三个值）+ 需要时才出现的长说明与安装进度；
2. 操作卡：按当前处境只显示一组动作（装核心 或 建房/加房 或 停止联机）。

其余全部折叠或按需展开，首屏不摆当前用不到的控件：

| 内容 | 归属 |
| --- | --- |
| 玩家名 / 节点地区 | 状态卡内「高级选项」折叠区 |
| 邀请码输入框 | 点「加入好友的房间」才展开 |
| 房间邀请码 / 加入侧提示 / 异常 | 操作卡底部「联机详情」，只在对应阶段出现 |
| 核心日志 / 使用步骤 / 第三方 SDK 说明 | 页尾三个默认收起的折叠区 |

状态由服务轮询（1.5 秒）+ 每次操作后刷新驱动，页面不解析任何协议；
状态值是短标签（「创建房间中」），需要用户动作时的长说明另起一行，平时整行隐藏。

## 10. 验证

- 单元测试（随普通测试跑）：清单解析与默认值、非法清单拒绝（缺字段 / id 穿越 / 与内置冲突 /
  未知阶段）、占位符展开、内置陶瓦描述指向官方发布包。
- 真机联调（`STARCLOUD_IT_MULTIPLAYER=1 dotnet test --filter 真机联调`）：
  真实下载核心 → 拉起会话 → 读状态（空闲）→ 创建房间（游戏未开，停在「扫描中」）→ 回空闲 → 停止会话 → 确认不再响应。
  实测通过（约 10 秒，含 8 MB 下载）。

## 11. 已知边界

- 房主侧仍需**游戏内先「对局域网开放」**：核心靠扫描本机局域网世界的端口来接力，这一步替代不了。
- 节点地区是"偏好"而非强制：节点列表拿不到时用核心内置默认节点，不影响联机。
- 第三方 SDK 必须提供本地 HTTP 控制面（或自实现 `IMultiplayerProvider`）；纯 GUI 工具无法接入。
- 目前只在 Windows x64 上验证（平台包与路径选择按 `{platform}` 生成，其它平台未实测）。
