# Starcloud 联机（Starcloud ID 驱动 · P2P · 含好友与房间大厅）（2026-09-30）

## 1. 背景与目标

启动器现有联机能力是「外挂陶瓦联机（Terracotta）官方二进制 + 本地 HTTP 控制面」（见
`2026-09-21-multiplayer-sdk-design.md`），自己没有任何 P2P / 信令实现，也**没有好友关系**：
`StarcloudIdService` 只有单会话（user_id / display_name / api_key / channel / login_at），
全仓唯一的「邀请」是 Nova 的离线自包含令牌 `NovaInviteService`（`starcloud://join?t=...`）。

本次要做的是一套**自己的**联机能力，由 Starcloud ID 身份驱动，P2P 底座用 EasyTier，
并补上好友与房间大厅。**交付物是「联机内核」，不是桌面应用**：内核不含 UI，
各端界面将来各自接（Windows 启动器 WinUI、`crossplatform/` 的 Uno、安卓、鸿蒙）。
本次先做成可在开发机上独立构建、独立自检的内核 + 本地模拟服务端，不集成进启动器。

目标：

1. 用 Starcloud ID 登录后，能看到**好友列表与在线状态**，能**邀请好友进房**、也能**收到邀请一键加入**。
2. 有**公开房间大厅**：陌生人也能看到房间列表并加入。
3. 房主在 Minecraft 里「对局域网开放」之后，房间内其他人能连上（数据面走 P2P，不经过我们的服务器）。
4. 内核能被 Win / Linux / macOS / Android 四端复用；鸿蒙与鸿蒙 PC 另写一份实现，**两边共用同一套协议**。

### 1.1 平台矩阵（这是本设计的硬约束）

| 平台 | 内核实现 | 依据 |
| --- | --- | --- |
| Windows / Linux / macOS | **C# 内核**（`net8.0`，本方案） | .NET 官方支持；启动器与 `crossplatform/` 都是 .NET |
| Android | **同一份 C# 内核**（安卓工程落 `net10.0-android`，见 §4.1 偏差说明） | .NET 有 Android 目标框架，Uno 也支持 |
| 鸿蒙 / 鸿蒙 PC | **ArkTS 另写一份内核**，协议与本方案完全一致 | HarmonyOS NEXT 只跑 ArkTS/ArkUI + 原生 C/C++ 模块（NAPI），**没有任何 .NET 运行时**，也没有官方 .NET RID；实验性的 .NET-on-OpenHarmony 不具备生产可用性 |

因此**不引入 Rust**：跨端共享靠「协议一致」而不是「同一份二进制」。
C# 内核与 ArkTS 内核是两份实现，任何协议改动必须同时改两边——这是本方案明确的维护成本。

## 2. 非目标

- **不做陶瓦联机（Terracotta）兼容**。选址时曾考虑复刻其邀请码与 scaffolding 协议，
  已确认其机制（见 §15），但决定不复用：邀请码不再互通，也不实现它的 L7 协议。
- **不自研打洞/中继**：交给 EasyTier 内核与官方公共共享节点。
- **不做账号体系**：身份一律来自 Starcloud ID。
- **不做好友关系管理**（发申请/同意/删除/备注）：本次好友来自服务端预置数据。
- **不做聊天、语音、存档同步**。
- **不做 UI**：本次不产出任何界面（Windows 联机页、Uno 端、安卓端、鸿蒙端都不做），
  只产出内核 + 一个仅用于验收的命令行驱动。
- **不集成启动器**：新解决方案不引用启动器的任何程序集。
- **本次不交付鸿蒙 ArkTS 实现**：只把协议与接口约束定死，供鸿蒙侧照着实现（见 §4.2）。

## 3. 总体架构

```
 Starcloud ID 云端 (api.starcloudfur.cn)          ← 只负责登录并换取 ApiKey
        │ Bearer ApiKey
        ▼
 ┌──────────────────────────────┐
 │ 联机服务端（本次 = 本地 mock） │  好友列表 / 在线心跳 / 房间大厅 / 邀请
 │ /v1/multiplayer/*             │  ← 契约按云端形状写，将来原样搬到云端
 └──────────────────────────────┘
        ▲ HTTP（Bearer <api_key>）
        │
 ┌──────────────────────────────────────────────┐
 │ 联机内核（本次交付物，无 UI）                  │
 │  身份 · 好友 · 房间 · 邀请 · 会话状态机         │
 │              │                                │
 │        IP2PEngine（可插拔的 P2P 引擎契约）      │
 │              ├── 桌面：easytier-core 进程 + cli │
 │              └── 移动端：内嵌原生库（后续）      │
 └──────────────────────────────────────────────┘
        │
        ▼
  EasyTier 网络 ──── 打洞，失败则由公共共享节点中继 ──── 对端
```

**关键分工**：服务端只负责「谁是谁的好友、谁在线、有哪些房间、谁能拿到哪个房间的凭据」，
**用户流量一律不走服务端**。EasyTier 的网络名/密钥是进网的唯一凭据，因此它就是权限本身。

**内核与 UI 的边界**：内核只出数据与状态快照（`MultipartySnapshot` 形态），
不打印、不弹窗、不碰任何平台的 UI 类型；界面层（将来各端自己写）只做「把点击翻译成一次调用 + 画快照」。

## 4. 工程结构

新解决方案，与启动器完全解耦（不引用 `Starcloud.App` / `Starcloud.Core`）：

```
starcloud-multiplayer/
  Starcloud.Multiplayer.slnx
  src/
    Starcloud.Multiplayer.Core/        领域与纯逻辑：身份模型、好友与房间模型、
                                       房间码与网络凭据、EasyTier 命令行构造、cli 输出解析、
                                       IP2PEngine 契约、快照模型
    Starcloud.Multiplayer.Client/      HTTP 客户端 + 会话状态机 + 桌面 P2P 引擎实现
    Starcloud.Multiplayer.Cli/         仅用于验收的命令行驱动（不是产品界面）
    Starcloud.Multiplayer.MockServer/  本地模拟云端（Kestrel minimal API + JSON 文件存储）
  tests/
    Starcloud.Multiplayer.Tests/       xUnit，测试方法名用中文（与仓库既有风格一致）
  README.md                            怎么构建、怎么自检、许可与分发说明
```

数据目录：`%APPDATA%/Starcloud-Multiplayer/`（`session.json`、`settings.json`、
`easytier/`（内核二进制）、`logs/`）。**不复用启动器的数据目录，不读不写它的文件。**

### 4.1 C# 内核的目标框架

- `Starcloud.Multiplayer.Core`：`net8.0`（纯逻辑，无平台 API）——**这是唯一允许被各端引用的程序集**。
- `Starcloud.Multiplayer.Client`：`net8.0`（桌面/安卓通用），
  只把「平台差异」（数据目录、进程/原生库、日志落盘）收敛到 `IPlatformHost` 一个接口后面。
  安卓侧单独建 `Starcloud.Multiplayer.Client.Android` 工程（`net10.0-android`）——
  原计划的 `net8.0-android` 条件编译块改为独立工程：.NET 10 SDK 已把 net8.0-android 标记为 EOL
  （NETSDK1202，restore 直接报错），按预案退到 `net10.0-android`。
- `Starcloud.Multiplayer.Cli` / `MockServer`：`net8.0`（跑在开发机上的验收工具，不进移动端）。

### 4.2 鸿蒙侧（本次不写代码，只冻结协议）

鸿蒙与鸿蒙 PC 的 ArkTS 内核**照着本方案实现**，必须与 C# 内核在这几点上逐字节一致，
否则两边无法互相联机：

1. **联机服务端 HTTP 契约**（§6）：路径、请求体、响应体、错误码。
2. **房间与凭据模型**（§7）：`room_id` 分配规则、`network_name = sc-mp-<room_id>`、
   `network_secret` 的字长与字符集。
3. **EasyTier 入网参数**（§8.2/§8.3）：同一房间的成员必须用同一组
   `--network-name` / `--network-secret` 与同一份公共共享节点列表。
4. **虚拟地址约定**：房主固定 `10.144.144.1`，客人 DHCP；客人的端口映射目标固定为
   `10.144.144.1:<mc_port>`。
5. **隧道自检的探针**（§9）：Minecraft Server List Ping（`0xFE` → `0xFF`）与
   现代 Handshake+Status 的最小应答，两边都要实现，才能互相自检。

鸿蒙侧额外要解决的平台问题（不在本次范围，但要记在案）：
鸿蒙不能像桌面那样 spawn 任意可执行文件，EasyTier 必须以**原生库（NAPI）或官方
`childProcessManager` 子进程**的方式跑起来；社区已有「EasyTier 鸿蒙版」先例，
落地前需要在真机上单独验证。

## 5. 身份（Starcloud ID）

- 会话模型与启动器保持一致：`{ user_id, display_name, api_key, channel, login_at }`，
  存 `session.json`。
- 两条通道，代码路径同一条：
  - `cloud`：`POST /v1/auth/login`（账号密码换 token）→ `POST /v1/auth/apikeys`（token 换 ApiKey），
    base `https://api.starcloudfur.cn`，与 `docs/designs/2026-09-09-starcloud-id-protocol.md` 一致。
  - `test`：本地确定性派生（不联网）。原型**默认走 test 通道**：同一台机器可以造多个账号，
    离线也能做双端验收。
- 身份凭据的使用方式：所有联机服务端请求都带 `Authorization: Bearer <api_key>`，
  `user_id` 由服务端从 ApiKey 解析（不在请求体里传，避免自报身份）。

## 6. 联机服务端契约（按云端形状定）

Base：cloud 通道 `https://api.starcloudfur.cn/v1/multiplayer`；原型指向本地 mock（同路径）。
统一响应体：成功 `{"ok":true,...}`，失败 `{"ok":false,"error":"<机器可读码>","message":"<中文说明>"}`；
HTTP 状态码用 401/403/404/409/429。

| 方法 | 路径 | 请求 | 响应 |
| --- | --- | --- | --- |
| GET | `/friends` | — | `[{user_id, display_name, online, room_id?}]` |
| POST | `/presence` | `{room_id?}` | `{ok}`（15s 一次心跳，30s 无心跳视为离线） |
| GET | `/rooms` | — | `[{room_id, name, host: {user_id, display_name}, players, capacity, created_at}]`（仅公开房） |
| POST | `/rooms` | `{name, public, mc_port, network_name, network_secret}` | `{room_id}`，房主自动进房 |
| DELETE | `/rooms/{room_id}` | — | `{ok}`；仅房主可关 |
| POST | `/rooms/{room_id}/join` | — | `{network_name, network_secret, host_user_id}` |
| POST | `/invites` | `{to_user_id, room_id}` | `{invite_id}` |
| GET | `/invites` | — | `[{invite_id, from: {user_id, display_name}, room_id, room_name, created_at}]` |
| POST | `/invites/{invite_id}/accept` | — | `{network_name, network_secret, host_user_id}` |
| POST | `/invites/{invite_id}/reject` | — | `{ok}` |

- 收到邀请而自己不在线时服务端也保留，下次上线可见（`GET /invites`）。
- 好友关系没有写接口：mock 用一份**预置数据**（`Starcloud.Multiplayer.MockServer/seed.json`，
  形如 `{"users":[...], "friendships":[["a","b"],...]}`，可手改后重启生效）把「谁和谁是好友」
  灌进去；客户端全程只读。将来接真云端时，这份预置换成真实的关系表。
- mock 端的 ApiKey→user_id 解析：test 通道用与客户端一致的派生规则反推（确定性，无需注册）；
  cloud 通道下 mock 额外提供一个**仅 mock 存在**的端点 `POST /_mock/keys`（客户端登录后把
  `api_key → user_id` 登记进去）。**该端点不属于云端契约**，将来接真云端时删掉即可。

## 7. 房间与凭据模型

房间：

```
room_id          8 位 base32（去掉易混字符），服务端保证唯一
name             房间显示名（默认「<昵称> 的房间」）
host_user_id     房主
public           是否进公开大厅
mc_port          房主侧 Minecraft 实际监听端口（「对局域网开放」给的那个）
network_name     sc-mp-<room_id>
network_secret   32 位 base32 随机（仅本次房间有效，关房即作废）
```

**凭据只在有权限时下发**：好友邀请 → 定向发给被邀请人；公开房 → 任何已登录用户 `join` 即可拿到。
关房 = `DELETE /rooms/{id}` + 杀掉本地 EasyTier 进程，密钥不复用。

服务端不认识 EasyTier：它只是「房间登记 + 凭据分发 + 准入」的存储。

## 8. EasyTier 内核驱动

### 8.1 引擎契约与释放内核

内核里 P2P 只是一个**可插拔的接口** `IP2PEngine`（起网络 / 加端口映射 / 读对端 / 停网络 / 探活），
它下面的实现按平台换：

| 平台 | 实现 | 本次 |
| --- | --- | --- |
| Windows / Linux / macOS | `EasytierProcessEngine`：释放并 spawn 官方 `easytier-core` + `easytier-cli` | **实现** |
| Android | `EasytierNativeEngine`（`Starcloud.Multiplayer.Client.Android`）：jniLibs 内置上游 musl 静态二进制，从 `nativeLibraryDir` 以**子进程**拉起，薄包装同一 `EasytierProcessEngine` | **实现** |
| 鸿蒙 / 鸿蒙 PC | 同上，由 ArkTS 侧实现 | 留契约，不实现 |
| 测试 | `FakeP2PEngine`：内存实现，只记录调用 | **实现** |

桌面实现的释放细节：

- 首次运行下载 `easytier-windows-x86_64-v2.6.4.zip`（GitHub Release；支持配置镜像前缀），
  校验 SHA256 后解开 `easytier-core.exe` / `easytier-cli.exe` 到 `<数据目录>/easytier/<版本>/`。
- **不把二进制放进代码仓库或安装包**，一律运行时下载（EasyTier 上游许可是 LGPL-3.0，见 §14）。
- 与服务端的 HTTP 请求一样，本机所有 HTTP 都必须**绕开系统代理**（见 §12）。
- 下载不到内核时，内核仍可构建、控制面（登录/好友/房间/邀请）仍可用，
  只有 `host` / `join` 报「P2P 引擎不可用」——把「控制面」与「数据面」解耦，便于分阶段验收。

安卓实现的释放细节（2026-09-30 已落地）：

- **形态定案：jniLibs 子进程，不是 JNI/NAPI 内嵌**。理由：
  1. EasyTier 上游没有提供 C ABI 动态库，只有自包含可执行文件，「编成原生库」等于自己 fork 源码；
  2. Android 10+ 有 W^X 限制（应用只能执行 `nativeLibraryDir` 下的 `lib*.so`），
     把上游二进制改名成 `libeasytier-core.so` / `libeasytier-cli.so` 打进 APK 的 jniLibs 正好合规
     （Magisk 等项目早有同样做法）；
  3. 子进程形态同时满足 LGPL-3.0「独立可执行 + 可替换」：APK 内置 + 运行时下载两者并存。
- 构建期由 `scripts/fetch-kernel.ps1` 拉取上游 `easytier-linux-{aarch64,x86_64}-v2.6.4.zip`
  （哈希钉死校验，缓存在 `%TEMP%\easytier-android-fetch\`），改名后落到
  `jniLibs/arm64-v8a` 与 `jniLibs/x86_64`；jniLibs 内容不进仓库（`.gitignore`），APK 打包时随包分发。
- `EasytierNativeEngine` 只是 `EasytierProcessEngine` 的薄包装（显式路径构造器注入
  `nativeLibraryDir` 下的两个二进制路径），协议与桌面端零差异。

### 8.2 房主

```
easytier-core
  --network-name sc-mp-<room_id>
  --network-secret <secret>
  -p tcp://public.easytier.top:11010            （公共共享节点，可配置多个）
  -p tcp://public2.easytier.cn:54321
  --no-tun
  --compression=zstd  --multi-thread  --latency-first  --enable-kcp-proxy
  -l tcp://0.0.0.0:0  -l udp://0.0.0.0:0
  --p2p-only
  --hostname sc-mp-host-<mc_port>
  -i 10.144.144.1
  --tcp-whitelist=<mc_port>  --udp-whitelist=<mc_port>
  -r <本地RPC端口>
```

### 8.3 客人

参数同上，但：

- 去掉 `-i 10.144.144.1`、`--hostname sc-mp-host-*`、端口白名单换成 `--tcp-whitelist=0 --udp-whitelist=0`；
- 加 `-d`（DHCP，自动分配虚拟 IP，默认段 10.126.126.0/24，不与房主的 10.144.144.1 冲突）；
- 起来之后加端口映射（客人用 Minecraft 连 `127.0.0.1:<本地端口>`）：

```
easytier-cli -p 127.0.0.1:<rpc> port-forward add tcp 0.0.0.0:<本地端口> 10.144.144.1:<mc_port>
easytier-cli -p 127.0.0.1:<rpc> port-forward add udp 0.0.0.0:<本地端口> 10.144.144.1:<mc_port>
```

本地端口优先尝试**与房主同一个端口号**（部分模组如 SimpleVoiceChat 依赖固定 UDP 端口），
被占用时退到系统分配的动态端口并在 `status` 里如实提示。

### 8.4 探测与展示

```
easytier-cli -p 127.0.0.1:<rpc> -o json peer
```

解析出对端列表（hostname / ipv4 / nat_type / cost），用于：

- 判断房主是否已发现、是否**直连**（cost 为 Local 之外的直连）还是**走中继**；
- 展示 NAT 类型（FullCone / Restricted / Symmetric …），打洞失败时给出可操作提示。

> **v2.6.4 实测已回填**：字段为 `cidr`（形如 `10.144.144.2/24`）/ `ipv4` / `hostname` /
> `nat_type` / `cost` / `lat_ms` / `loss_rate` / `rx_bytes` / `tx_bytes` / `tunnel_proto` /
> `id` / `version`。其中 **`cost` 是字符串**：`Local`（本机自己）、`p2p`（直连）、
> `relay(2)`（中继，括号内为转发代价）；旧版是纯整数（1 = 直连）。解析层两种形状都认，
> 样例 JSON 进了 `EasytierPeerListTests`，陌生取值一律落 `Unknown` 不猜。

### 8.5 为什么是 `--no-tun`

不建虚拟网卡 ⇒ **不需要管理员权限**，代价是进出都必须靠端口映射（EasyTier 的用户态 TCP/UDP 代理）。
这一取舍与陶瓦联机一致，是本机体验换来的（见 §15）。

## 9. 隧道自检（单机可验收）

`selftest` 让「不开 Minecraft、只用一台机器」就能证明隧道通：

1. 房主侧以 `--selftest` 开房：不连真实 Minecraft，改为在 `mc_port` 上起一个**内置假 MC 服务端**
   （实现 Minecraft Server List Ping：旧式 `0xFE` → `0xFF`，以及现代 Handshake+Status 的最小应答）。
2. 客人侧加入后，向本地映射端口发一次 Server List Ping。
3. 收到合法应答 ⇒ 打印「隧道已打通：本机 127.0.0.1:<端口> → 房主 <mc端口>，路径=直连/中继，
   延迟=<ms>」，退出码 0；否则退出码非 0 并打印失败环节。

这个自检既是开发期的验收手段，也进集成测试（§13）。

**会合点不依赖外网（实测后补）**：公共节点域名在 DNS 被加速器劫持的机器上解析不出来
（`public.easytier.top` / `public2.easytier.cn` 均 NXDomain），「不需要外网」必须把会合点
也本地化。做法：自检用已释放的内核再拉一个 `easytier-core` 当**本地会合节点**
（共享节点形态：不带网络身份、`--no-tun`、`-l tcp/udp://0.0.0.0:<port>`，只做信令不中继），
host/guest 的公共节点列表在构造内核时就指向它。注意通告地址必须用**非回环**本机 IPv4
（回环目标在 TUN 环境下 10049，见 §12）。同机双实例 `--p2p-only` 下打洞通常不成，
流量经会合节点中继（peer 列表 `cost=relay(2)`），不影响自检结论——探测打到的是真实
EasyTier 虚拟网络。实测退出码 0，peer 发现与端口转发链路全部走通。

## 10. CLI 命令面

```
login <starcloud_id> [--channel test|cloud] [--password <pwd>]
logout | whoami

friends                                    好友 + 在线状态（在房间的显示房间名）
rooms                                      公开房间大厅
invite <好友>                              邀请好友进我当前所在的房间
invites [--accept <id> | --reject <id>]    我收到的邀请

host [--name X] [--port 25565] [--public] [--selftest]
join <room_id|好友名>
status                                     身份 / 房间 / 内核存活 / peer 路径 / 本地映射端口
leave

selftest                                   端到端隧道自检（起房主+客人两个会话，跑完自动收尾）
```

约定：`host` 与 `join` 是**前台驻留**命令（Ctrl+C 退出并收尾）；`--port` 缺省 25565，
占用了就把实际端口报给服务端；`status` 在任何时候都能看当前会话。

`join <好友名>` 的语义：好友的房在**公开大厅**里就直接进；好友开着**非公开房**时提示
「对方的房间未公开，请让 TA 邀请你」，不做任何绕过（凭据只能由服务端定向下发）。

## 11. 客户端会话状态机

```
Idle ──host/join──> Preparing（确保内核就绪 + 注册房间/取凭据）
                        │
                        ├─> Hosting（房主：内核已起 + 已登记房间 + 心跳）
                        │      └─> GuestJoined / GuestLeft（来自房间玩家列表的变化）
                        └─> Joining（客人：内核已起 + 加端口映射）──> Connected
                        │
                        └─> Failed(reason) ──> Idle
Idle <──leave/Ctrl+C── Hosting | Connected（杀内核 + 关房/退房 + 停心跳）
```

心跳与 `status` 由状态机统一驱动（15s 一次），各状态对外只暴露 `Snapshot`。

## 12. 错误处理与已知坑

| 场景 | 处理 |
| --- | --- |
| **系统代理**（Clash/mihomo）把 `127.0.0.1` 也截走 | 本机 HTTP 与 EasyTier RPC 一律绕开代理：自建 `SocketsHttpHandler{UseProxy=false}`；启动器已在同一个坑里栽过 |
| 内核下载失败 / 哈希不匹配 | 明确报错 + 给出镜像前缀配置项；不静默降级 |
| EasyTier 进程起不来 / 中途退出 | 捕获退出码并保留最后 500 行日志（照抄陶瓦的排障思路），`status` 里显示 |
| 公共共享节点不可达 | 打洞失败时提示「中继不可用」，允许在 `settings.json` 换节点列表 |
| 对称 NAT 打洞失败 | EasyTier 自动回退公共节点中继，界面如实显示「走中继」而不是假装直连 |
| `mc_port` 未开放 / 端口被占 | 房主侧先探一次本机端口（TCP 连通性）；不通就拒绝开房并提示「先在游戏里对局域网开放」 |
| 客人本地端口被占 | 退到动态端口，`status` 里说明「端口与房主不同，部分模组可能受影响」 |
| 房主关房/掉线 | 服务端把房间标记为失效；客人内核失去 peer 后由状态机判定 `Failed` 并收尾 |
| 邀请指向的房间已关闭 | `accept` 返回 404/409，客户端提示「房间已关闭」 |
| **Windows 双绑陷阱**（实测）：`0.0.0.0:port` 被占时 `127.0.0.1:port` 仍能绑成功 | 判「端口空闲」必须绑 `IPAddress.Any` 试探（`LocalPorts.IsFree`），绑 Loopback 试探会误判空闲，EasyTier 端口转发随后绑 `0.0.0.0:port` 报 os error 10048 直接退出 |
| **EasyTier 连回环目标必败**（实测）：连 `127.0.0.1:*` 时按「到目标的路由」选源地址，加速器 TUN 占默认路由的机器上绑到无效源地址，Windows 报 10049「请求的地址无效」 | 任何要被 EasyTier 连入的本机监听（自检会合节点）都绑 `0.0.0.0` 并用**非回环**本机 IPv4 地址通告；联调排查时先想到这条 |
| **公共节点域名被加速器 DNS 劫持**（实测）：`public.easytier.top` / `public2.easytier.cn` 均 NXDomain | 见 §9：自检用本地会合节点；产品侧用户可在设置里换节点列表 |

## 13. 测试策略

**单元测试**（默认全部运行）：

- 房间码生成：长度、字符集、唯一性；`network_name`/`network_secret` 派生规则。
- 服务端逻辑（内存实现）：好友列表、在线超时（15s/30s）、房间大厅过滤（公开/非公开）、
  邀请流转（邀请→接受→拿到凭据、拒绝、过期、非房主不能关房）。
- **EasyTier 命令行构造**：纯函数，房主/客人两套参数逐条断言（含镜像与自定义节点）。
- **`easytier-cli` 输出解析**：喂样例 JSON，断言 peer 路径（直连/中继）与 NAT 类型映射。
- CLI 参数解析与错误提示。

**集成测试**（默认跳过，`STARCLOUD_MP_IT=1` 才跑）：

- 真下载内核 → 同机起房主（`--selftest`）+ 客人 → 跑 §9 的自检 → 断言隧道通、退出码 0。
- 与服务端契约的端到端：mock 服务端起在本机随机端口，两个客户端（两个 test 通道账号）
  走完 登录 → 加好友数据 → 开房 → 邀请 → 接受 → 隧道通。

**手工验收**：真实 Minecraft，两台机器（或同机双开），房主「对局域网开放」，
客人在游戏里连 `127.0.0.1:<本地端口>` 能进世界。

## 14. 许可与分发

- **EasyTier 上游是 LGPL-3.0**。本方案只在运行时下载**未经修改的官方二进制**并作为独立进程
  驱动，不静态/动态链接进我们的程序集；仓库与安装包里都不带它的二进制。
- 客户端 README 与 `status` 输出里注明「P2P 内核：EasyTier（LGPL-3.0）」。
- 陶瓦联机（AGPL-3.0 + 例外）本次**不复用代码、二进制或协议**，因此不引入它的许可义务。
- 公共共享节点是社区公益资源：默认节点列表写在配置里可换，文档里注明「请勿滥用」。

## 15. 参考：陶瓦联机是怎么做的（先例，非依赖）

调研期读了 `burningtnt/Terracotta` 源码（已确认其机制），以下取舍被我们直接沿用：

- 桌面端也是**释放自己的 EasyTier 二进制 + spawn + 用 `easytier-cli` 做 RPC**；
  EasyTier 参数几乎一致（`--no-tun --compression=zstd --multi-thread --latency-first
  --enable-kcp-proxy -l tcp/udp://0.0.0.0:0 --p2p-only`）。
- 房主固定 `-i 10.144.144.1`，客人 `-d`，客人侧加 `port-forward`（`local → 10.144.144.1:<端口>`）——
  §8 的做法即来自这里。
- 客人本地端口优先取与房主相同的端口号（为了 SimpleVoiceChat 之类依赖 UDP 的模组）。
- 它的**邀请码**是 `U/XXXX-XXXX-XXXX-XXXX`（34 进制字母表 `0123456789ABCDEFGHJKLMNPQRSTUVWXYZ`，
  16 位小端拼成 u128 且必须是 7 的倍数，再派生成 `scaffolding-mc-<前8>` / `<后8>` 当网络名与密钥），
  外加一层自研 L7「scaffolding」协议用于交换真实 MC 端口与玩家列表。
  **本次已明确不做这套兼容**，此处仅作记录，避免以后重复调研。

## 16. 未决 / 后续（明确不做的这次不写代码）

- **鸿蒙 / 鸿蒙 PC 的 ArkTS 内核**（协议见 §4.2）：需要先真机验证 EasyTier 在鸿蒙上的
  运行方式（NAPI 原生库 or `childProcessManager` 子进程）。
- （已移除）~~Android 的 `EasytierNativeEngine`~~：2026-09-30 已按 jniLibs 子进程形态实现，见 §8.1。
- 好友关系管理（申请/同意/删除/备注）、房间容量与踢人、房间密码。
- 端到端更严的凭据控制：EasyTier `--secure-mode` + `--credential`（临时凭据私钥，
  不用把 `network_secret` 发出去）——需要单独验证与公共节点的兼容性。
- 一键拉起 Minecraft（由启动器负责，本次不做）。
- 并回启动器 / Uno 端 / 安卓端：把 `Core` + `Client` 作为类库引用，各端只写界面。

## 17. 验收清单

本次交付物 = C# 内核（Win/Linux/macOS/Android 复用）+ 本地 mock 服务端 + 验收用 CLI。

1. ✅ `dotnet build starcloud-multiplayer/Starcloud.Multiplayer.slnx` 0 错 0 警；单测全绿（31/31，2026-09-30）。
2. ✅ **控制面端到端**（不需要外网、不需要 EasyTier）：起 mock 服务端，两个 test 通道账号走完
   登录 → 看好友在线 → 开房登记 → `rooms` 看到公开房 → 邀请 → 接受 → 拿到房间凭据
   （`ControlPlaneEndToEndTests`，进单测常跑）。
3. ✅ **隧道自检**：同机起房主（`--selftest`）+ 客人，客人探测本地映射端口拿到合法 Minecraft
   Server List Ping 应答（`selftest` 退出码 0，本地会合节点方案见 §9；2026-09-30）。
4. ⬜ 真实 Minecraft 端到端：客人在游戏里连 `127.0.0.1:<本地端口>` 能进房主的世界（**待人工验收**，需要真机双开）。
5. ✅ 打洞失败时如实显示「走中继」（`cost=relay(2)` → 路径=经公共节点中继）；系统代理开着也不影响本机控制面。
6. ✅ 协议冻结：§4.2 的五条约定写进 `starcloud-multiplayer/README.md`，鸿蒙侧可直接照着实现。
7. ✅ **安卓工程**（2026-09-30）：`Starcloud.Multiplayer.Client.Android`（`net10.0-android`）进 slnx，
   含真实 jniLibs 的全量构建 0 错 0 警；`fetch-kernel.ps1` 双 ABI（arm64-v8a / x86_64）实测通过；
   安卓内核路径与进程行为的回归由新增 3 条 `EasytierProcessEnginePathTests` 覆盖（进 31/31）。
   ⬜ 真机部署 APK 起内核（待真机验收）。
