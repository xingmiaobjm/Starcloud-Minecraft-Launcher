# Starcloud 联机内核（C#）实施计划（2026-09-30）

对应设计稿：`docs/designs/2026-09-30-starcloud-multiplayer-design.md`

## 交付物

一个与启动器完全解耦的新解决方案 `starcloud-multiplayer/`：

- **C# 联机内核**（`Core` + `Client`）：身份、好友、房间、邀请、会话状态机、可插拔 P2P 引擎。
  目标框架 `net8.0`（桌面 + 安卓共用），平台差异收敛到 `IPlatformHost`。
- **本地模拟服务端**（`MockServer`）：按云端形状实现 `/v1/multiplayer/*`，JSON 文件存储 + 预置好友数据。
- **验收用 CLI**（`Cli`）：不是产品界面，只用于跑通与自检。
- **测试**（`Tests`）：xUnit，测试方法名用中文。
- **README**：构建、自检、协议冻结清单、许可与分发。

不交付：任何 UI；鸿蒙 ArkTS 内核；Android 原生引擎。

## 里程碑

### M1 骨架与纯逻辑（Core）

产出：解决方案、`Directory.Build.props`、`Core` 全部纯逻辑。

- 领域模型：`StarcloudIdentity`、`FriendInfo`、`RoomInfo`、`InviteInfo`、`RoomCredentials`。
- 房间码：`room_id`（8 位 base32，去易混字符 `0/O/1/I/L`）；`network_name = sc-mp-<room_id>`；
  `network_secret`（32 位同字符集，密码学随机）。
- 协议常量：`/v1/multiplayer/*` 路径、错误码、DTO（`System.Text.Json`）。
- `IP2PEngine` 契约 + `P2POptions`（房主/客人两套参数）→ `EasytierArguments.Build(...)` 纯函数。
- `easytier-cli -o json peer` 输出解析 → `PeerInfo { hostname, ipv4, natType, isLocal, path }`。
- Minecraft Server List Ping 探针（`0xFE` → `0xFF` 旧式 + Handshake+Status 现代式）。
- test 通道身份派生（与启动器同一套规则：`user_id = "sc-" + sha256(lower(id))[:12]`）。

验收：`dotnet build` 0 错 0 警；上述每项的单元测试全绿。

### M2 控制面端到端（MockServer + Client + 测试）

产出：mock 服务端、HTTP 客户端、`MultiplayerKernel`（控制面部分）。

- `MockServer`：Kestrel minimal API；`seed.json` 预置用户与好友关系；
  test 通道按派生规则反推 `user_id`；内存 + JSON 落盘；presence 15s/30s 过期。
- `MpHttpClient`：Bearer 鉴权、统一错误体解析、**绕开系统代理**（`UseProxy=false`）。
- `MultiplayerKernel`：`Login / Logout / Friends / Rooms / Host(登记) / Join / Invite / Accept / Leave`，
  对外只给快照。

验收（**不需要外网**）：集成测试里本机起 mock 服务端，两个 test 通道账号走完
登录 → 好友在线 → 开房 → 大厅看到房间 → 邀请 → 接受 → 拿到凭据。

### M3 数据面（P2P 引擎 + 隧道自检）✅ 2026-09-30

产出：`EasytierProvisioner`（下载/解压/缓存）、`EasytierProcessEngine`、自检。

- Provisioner：版本固定 `v2.6.4`，下载 `easytier-windows-x86_64-v2.6.4.zip`，
  **校验 SHA256**（本次会真的下载一次并把实际哈希固定进目录），解压到 `<数据目录>/easytier/<版本>/`。
- `EasytierProcessEngine`：起 `easytier-core`（房主/客人两套参数）、
  用 `easytier-cli` 读 peer、加端口映射、停网络。
- 隧道自检：房主 `--selftest` 时在 `mc_port` 上起假 MC 服务端；
  客人加入后向本地映射端口发 Server List Ping，收到合法应答即通过。

验收：✅ 同机起房主 + 客人，自检退出码 0 并打印路径（直连/中继）。
（实测记录：自检会合点用本地 `easytier-core` 共享节点，见设计稿 §9/§12；
`cost` 字段实测为 `p2p` / `relay(2)` 字符串，解析层已按实测校正。）
✅ 下载不到内核时降级为「引擎不可用」，且控制面不受影响。

### M4 收尾 ✅ 2026-09-30

- ✅ README（构建、自检、协议冻结五条、许可与分发）——`starcloud-multiplayer/README.md`。
- ✅ 全量 build + test 复验（0 错 0 警，28/28）；实测结论已回填设计稿（§8.4 peer 字段、
  §9 本地会合节点、§12 双绑陷阱与回环 10049、§17 验收清单）。

### M5 安卓端 ✅ 2026-09-30

形态定案（与用户对齐）：**jniLibs 子进程**，不做 JNI/NAPI 内嵌——上游无 C ABI 动态库、
Android 10+ W^X 只允许执行 `nativeLibraryDir` 下的 `lib*.so`（上游二进制改名
`libeasytier-core.so` / `libeasytier-cli.so` 打进 APK 合规）、子进程同时满足 LGPL-3.0
「独立可执行 + 可替换」。

- ✅ `EasytierProcessEngine` 重构：新增「显式路径构造器」（core/cli 路径 + 缺失提示由调用方给定），
  桌面便捷构造（Provisioner）保持兼容；`IsAvailable` / `UnavailableReason` 改走显式路径。
- ✅ 新工程 `src/Starcloud.Multiplayer.Client.Android/`（`net10.0-android`，MinSdk 24）：
  `AndroidPlatformHost`（FilesDir/multiplayer 数据目录 + `Android.Util.Log` 落 logcat）+
  `EasytierNativeEngine`（薄包装进程引擎，从 `ApplicationInfo.NativeLibraryDir` 解析内置内核）。
  TFM 偏差：原计划 `net8.0-android` 在 .NET 10 SDK 下 EOL（NETSDK1202），按预案退 `net10.0-android`。
- ✅ `scripts/fetch-kernel.ps1`：拉取上游 `easytier-linux-{aarch64,x86_64}-v2.6.4.zip`，
  哈希钉死校验，缓存于 `%TEMP%\easytier-android-fetch\`，提取改名落 jniLibs 双 ABI；
  jniLibs 内容不进仓库（`.gitignore`）。网络受限时可手动下载 zip 放进缓存目录（哈希兜底）。
- ✅ 回归：新增 3 条 `EasytierProcessEnginePathTests`（缺失不可用含位置 / 在位即可用 /
  缺省提示文案）；slnx 挂载后全量 build **0 错 0 警**（含真实 jniLibs），
  `dotnet test` **31/31**；fetch 脚本双 ABI 全链路实测通过（4 个 .so 就位）。
- ⬜ 真机部署 APK 起内核（待真机验收）。

## 风险与对策

| 风险 | 对策 |
| --- | --- |
| 下载不到 EasyTier（外网/GitHub 不通） | 支持镜像前缀与本地 zip 目录；内核照常构建，引擎不可用只影响 `host`/`join` |
| 公共共享节点不稳定 | 自检不依赖打洞成功（同机同网段通常直连；失败也有中继），失败时如实打印路径与错误 |
| `easytier-cli -o json peer` 字段与预期不符 | 解析层单独抽函数，先喂样例 JSON 做单测；实测后校正并更新样例 |
| 端口占用（25565 等） | 自动探测可用端口，并在快照里给出「实际端口」 |

## 明确不做

好友关系管理、房间密码/容量/踢人、聊天语音、`--secure-mode` + `--credential`、
鸿蒙 ArkTS 内核、任何 UI（安卓端只交付类库工程，不打包成 App；真机验收待做）。
