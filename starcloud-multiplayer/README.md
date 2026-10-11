# Starcloud 联机内核（starcloud-multiplayer）

Minecraft 联机的「内核」层：身份 / 好友 / 房间 / 邀请 / 会话状态机 / P2P 隧道，
**不含任何界面代码**。设计稿见 `../docs/designs/2026-09-30-starcloud-multiplayer-design.md`，
实施计划见 `../docs/plans/2026-09-30-starcloud-multiplayer-plan.md`。

- **C# 内核**（`Core` + `Client`）：Win / Linux / macOS / Android 四端复用；
  平台差异收敛到 `IPlatformHost` 一个接口后面。鸿蒙 ArkTS 照 §协议冻结五条 另行实现（本次不交付）。
- **本地 mock 服务端**（`MockServer`）：按云端形状实现 `/v1/multiplayer/*`，JSON 文件存储，
  预置好友数据（`seed.json`）。控制面与数据面解耦：内核二进制缺失时登录 / 好友 / 房间 / 邀请照常可用。
- **验收 CLI**（`Cli`，`starcloud-mp`）：不是产品界面，只用于跑通与自检。

数据面用 [EasyTier](https://github.com/EasyTier/EasyTier)（版本锁 **v2.6.4**）组网：
房主固定虚拟地址 `10.144.144.1`，客人 DHCP 并把 `127.0.0.1:<本地端口>` 映射过去，
Minecraft 只需要连 `127.0.0.1`。

## 构建

需要 .NET SDK 8（或更新）。仓库根目录：

```sh
dotnet build starcloud-multiplayer/Starcloud.Multiplayer.slnx   # 0 错 0 警
dotnet test starcloud-multiplayer/Starcloud.Multiplayer.slnx    # 31 个测试全绿
```

解决方案里含安卓工程（见下节），全量构建需要 `dotnet workload install android`；
只想构建桌面/测试侧时，直接构建 `src/Starcloud.Multiplayer.Cli` 或 `tests/` 的 csproj 即可。

不引用启动器程序集，与 `dotnet/src/Starcloud.App` 完全解耦。

## 安卓端（Starcloud.Multiplayer.Client.Android）

同一份 C# 内核跑在安卓上：`src/Starcloud.Multiplayer.Client.Android/`（`net10.0-android`，
MinSdk 24），只是类库工程，不含任何界面。

**内核形态：jniLibs 子进程，不是 JNI 内嵌**。Android 10+ 有 W^X 限制——应用只能执行
`nativeLibraryDir` 下的 `lib*.so`。因此把 EasyTier 上游 linux musl 静态二进制改名成
`libeasytier-core.so` / `libeasytier-cli.so` 打进 APK 的 jniLibs，运行时从
`nativeLibraryDir` 以子进程拉起（`EasytierNativeEngine` 薄包装同一进程引擎）。
这同时满足 LGPL-3.0「独立可执行 + 可替换」。

构建准备（一次性）：

```sh
dotnet workload install android          # android 工作负载
winget install Microsoft.OpenJDK.17      # JDK 17
# Android SDK 缺失时由构建引导安装（弹许可）：
dotnet build src/Starcloud.Multiplayer.Client.Android/Starcloud.Multiplayer.Client.Android.csproj ^
  -t:InstallAndroidDependencies -p:AndroidSdkDirectory=<SDK目录> -p:AcceptAndroidSDKLicenses=True
```

TFM 偏差说明：设计稿原写 `net8.0-android`，在 .NET 10 SDK 下已 EOL（NETSDK1202），
按预案落 `net10.0-android`。

**内核二进制靠 `scripts/fetch-kernel.ps1` 构建期拉取**（版本锁 v2.6.4，哈希钉死）：

```powershell
powershell -File starcloud-multiplayer/scripts/fetch-kernel.ps1
# 拉取 easytier-linux-{aarch64,x86_64}-v2.6.4.zip → 校验 SHA256 →
# 改名落 src/Starcloud.Multiplayer.Client.Android/jniLibs/{arm64-v8a,x86_64}/
```

- 缓存目录 `%TEMP%\easytier-android-fetch\`：下载命中缓存即跳过网络；
  **网络受限时**用浏览器手动下载两个 zip 放进该目录（文件名保持上游原名），脚本验哈希通过即可。
- jniLibs 里的 `.so` **不进仓库**（`.gitignore`），克隆后必须先跑 fetch 脚本再打包 APK。

## CLI 快速上手

```sh
mp=starcloud-multiplayer/src/Starcloud.Multiplayer.Cli/bin/Debug/net8.0/starcloud-mp.exe

$mp mock            # 起本地 mock 联机服务端（默认 127.0.0.1:5510）
$mp login alice     # test 通道：本地派生身份，密码至少 4 位（任意内容）
$mp friends
$mp host --port 25565 --public   # 房主：先在 Minecraft 里「对局域网开放」
$mp join <房间号>                 # 客人：Minecraft 连 127.0.0.1:<本地映射端口>
$mp status | leave | logout
```

首次 `host` / `join` 前需要 P2P 内核就位（`$mp kernel` 或开房时自动下载）。
下载不到时可用离线包：`--kernel-zip <EasyTier 压缩包>`（哈希不符一律拒绝）。

## 端到端自检（selftest）

一台机器、不开 Minecraft、**不需要外网**，把整条链路跑一遍：

```sh
$mp selftest [--kernel-zip %TEMP%\easytier-dl\easytier-windows-x86_64-v2.6.4.zip]
```

七步：内核就位 → 两账号登录 → 好友可见 → 房主开房（假 MC 服务端顶替真实游戏）
→ 公开大厅可见 → 邀请 → 接受并起隧道 → 等对端出现 → 向本地映射端口发 Minecraft
Server List Ping。**退出码 0 即通过**。

「不需要外网」的会合点是这样做到的：自检会用已释放的内核再拉一个 `easytier-core`
作为本地会合节点（共享节点形态：无网络身份、`--no-tun`，只做信令不中继），
host/guest 的公共节点列表指向它。实测两个原因让默认公共节点不可用：
`public.easytier.top` / `public2.easytier.cn` 在 DNS 被加速器劫持的机器上直接 NXDomain；
且 EasyTier 连回环目标（`127.0.0.1`）会按「到目标的路由」选源地址，在加速器 TUN
占默认路由的机器上绑到无效源地址（Windows 报 10049「请求的地址无效」），
**回环会合点永远连不上**。所以自检的会合节点绑 `0.0.0.0`，host/guest 用
**非回环**本机 IPv4 地址连入。

同机双实例 `--p2p-only` 下打洞通常不成，自检流量经会合节点中继（peer 列表里
`cost=relay(2)`），这不影响自检结论——隧道探测打到的是真实的 EasyTier 虚拟网络。

## 协议冻结五条（鸿蒙 ArkTS 照此实现，逐字节一致）

1. **联机服务端 HTTP 契约**：`/v1/multiplayer/*` 的路径、请求体、响应体、错误码
   （设计稿 §6）。统一响应体 `{"ok":true,...}` / `{"ok":false,"error":...,"message":...}`。
2. **房间与凭据模型**（设计稿 §7）：`room_id` 为 8 位 base32（去易混字符 `0/O/1/I/L`）；
   `network_name = sc-mp-<room_id>`；`network_secret` 32 位同字符集，密码学随机。
3. **EasyTier 入网参数**（设计稿 §8.2/§8.3）：同一房间的成员必须用同一组
   `--network-name` / `--network-secret` 与同一份公共共享节点列表。
4. **虚拟地址约定**：房主固定 `10.144.144.1`（机名 `sc-mp-host-<mc_port>`），
   客人 DHCP；客人的端口映射目标固定为 `10.144.144.1:<mc_port>`。
5. **隧道自检的探针**（设计稿 §9）：Minecraft Server List Ping（`0xFE` → `0xFF`）
   与现代 Handshake+Status 的最小应答，两端都要实现才能互相自检。

## Windows 实测陷阱（换机器 / 换版本时先想到这两条）

- **双绑判定**：Windows 允许 `0.0.0.0:port` 被占时仍绑 `127.0.0.1:port` 成功。
  判「端口空闲」必须绑 `IPAddress.Any` 试探（`LocalPorts.IsFree`），
  否则 EasyTier 端口转发绑 `0.0.0.0` 时 os error 10048 直接退出。
- **回环不可连**：见上节——EasyTier 连 `127.0.0.1` 目标在 TUN 环境下 10049，
  联调用非回环本机地址。

## `easytier-cli -o json peer` 实测字段（v2.6.4）

```json
{
  "cidr": "10.144.144.2/24", "ipv4": "10.144.144.2", "hostname": "…",
  "cost": "p2p",            // Local（本机）/ p2p（直连）/ relay(2)（中继）；旧版为纯整数
  "lat_ms": "1.14", "loss_rate": "0.0%", "rx_bytes": "1.53 kB", "tx_bytes": "1.87 kB",
  "tunnel_proto": "tcp,tcp6", "nat_type": "PortRestricted",
  "id": "1513560122", "version": "2.6.4-8428a89d"
}
```

解析层（`EasytierPeerList`）刻意做得宽：缺字段跳过、陌生取值落 `Unknown`。

## 许可与分发

- 本目录代码属于 Starcloud 启动器项目，随项目许可。
- **EasyTier 为 LGPL-3.0**：
  - **桌面端**采用**运行时下载**：首次使用才从 GitHub Releases 拉
    `easytier-<平台>-<架构>-v2.6.4.zip`（校验 SHA256 后解压到 `<数据目录>/easytier/v2.6.4/`），
    **二进制不进代码仓库、不进安装包**。Windows x86_64 包实测 SHA256：
    `27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84`。
  - **安卓端**内核随 APK 内置（jniLibs，见上节）——这是 Android W^X 的硬约束；
    LGPL 合规靠「内置的就是上游独立可执行文件（仅改名）+ 可被同路径替换」满足。
    上游包实测 SHA256：aarch64 `f533ec25a7ea714e09f645615012200278058525795cc3bb690ff011aec1a70f`、
    x86_64 `61b659eaedba658fa66fe47d17e1426cdd77e5d02fa15fed447bb4357c09dfd6`。
- 换 EasyTier 版本时同步更新 `EasytierProvisioner.Version`、`fetch-kernel.ps1` 的
  `$Version` 与期望哈希，并复跑 selftest。

## 明确不做（本次范围外）

好友关系管理、房间密码/容量/踢人、聊天语音、`--secure-mode` + `--credential`、
鸿蒙 ArkTS 内核、任何 UI（安卓端只交付类库工程，不打包成 App；真机验收待做）。
