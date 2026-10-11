# Starcloud 联机内核 — 任务完成交接（2026-09-30，接手方回执）

> 交接来源：`docs/plans/2026-09-30-starcloud-multiplayer-plan.md`（M1-M4）+
> 「未完成事项交接（2026-09-30）」。本回执可直接转交原负责 Agent。

## 结论（TL;DR）

交接单上的 5 项待办**全部完成**。自检（`selftest`）已从「卡在第 7 步」修到
**7/7 全绿、退出码 0**（连续两次复跑确认）。全量复验通过：`dotnet build` 0 错 0 警、
`dotnet test` 28/28。未 commit（遵守交接要求）。

**但有一个超出交接预判的关键发现**：交接方案里的本地会合节点如果按原定参数绑
`127.0.0.1`，在这台机器上**永远连不通**——已实测定位根因并改了方案，见下文。

## 关键发现（后续 Agent 必读）

### 1. EasyTier 连回环目标必败（Windows 10049）

按交接方案实现 `127.0.0.1` 会合节点后自检仍失败：两个 peer 连会合点报 Windows 错误
10049「在其上下文中，该请求的地址无效」。用裸 `easytier-core` 进程做了对照实验
（不经自检代码）独立复现，确认是 EasyTier 的环境行为而非我们代码的 bug：

- **根因**：EasyTier 连对端时按「到目标的路由」选源地址。本机有加速器 TUN 网卡
  （26.51.51.161）占默认路由，目标是 `127.0.0.1` 时绑到无效源地址 → 10049。
- **实测矩阵**：连 `127.0.0.1` 目标 = 必败；连非回环本机地址
  （192.168.10.251 / 26.51.51.161）= 立即成功（会合节点绑 `0.0.0.0` 或对应地址均可）。
- **最终方案**：会合节点绑 `0.0.0.0`，host/guest 的 `PublicServers` 用**非回环本机
  IPv4**（`NetworkInterface.GetAllNetworkInterfaces()` 取非回环/非 Tunnel/已启用的
  IPv4，可能有多个，全部通告）。

### 2. `cost` 字段实测形状与解析层真 bug

v2.6.4 的 `easytier-cli -o json peer` 输出里 `cost` 是**字符串**：`Local`（本机）/
`p2p`（直连）/ `relay(2)`（中继，括号内为转发代价）。原 `EasytierPeerList.PathOf`
按 `int.TryParse` 解析 → 直连会被判成 `Unknown`。已改为两种形状兼容（纯整数=旧版），
测试样例更新为实测 JSON（含 `cidr`、`tunnel_proto` 等完整字段）。强制中继场景
（`--disable-p2p`）实测确认 `relay(2)` 取值。

### 3. 其余实测结论（已回填设计稿）

- 公共节点域名 `public.easytier.top` / `public2.easytier.cn` 在本机 DNS 均 NXDomain
  （加速器劫持）——印证了交接单的判断，本地会合节点方案正确且必要。
- 同机双实例 `--p2p-only` 下打洞不成，流量经会合节点中继（`cost=relay(2)`），
  **不影响自检结论**（探测打到的是真实 EasyTier 虚拟网络）。交接单里预留的
  「去掉 `--p2p-only` 开关」**没有做**，因为不需要。
- 完整拓扑实验（会合节点 + 房主 + 客人 + 假 MC + 端口转发）在自检外独立验证过：
  peer 互发现、`127.0.0.1:<本地端口>` 经转发读到假 MC 应答，全通。

## 改动清单（均未 commit）

| 文件 | 改动 |
| --- | --- |
| `starcloud-multiplayer/src/Starcloud.Multiplayer.Cli/SelfTest.cs` | 新增私有 `LocalRendezvous : IAsyncDisposable`（会合节点进程：stdout/stderr 进内存环形缓冲待诊断、退出即 `Kill(entireProcessTree:true)`、≤20s 等通告地址可连、失败带日志尾）；会合端口在构造两个 kernel 前用 `LocalPorts.FindFreeTcpPort()` 选好；kernel 的 `PublicServers` 覆盖为非回环本机地址列表；7/7 打印房主侧对端前补一次 `host.RefreshAsync(includePeers: true)`（原来永远是空列表） |
| `starcloud-multiplayer/src/Starcloud.Multiplayer.Client/MultiplayerKernel.cs` | 新增 `EngineCoreExecutablePath` 属性（`_provisioner.IsReady ? CoreExecutablePath : null`） |
| `starcloud-multiplayer/src/Starcloud.Multiplayer.Core/P2P/EasytierPeerList.cs` | `PathOf` 支持 `p2p` / `relay(N)` / 纯整数三种形状；类注释回填实测字段清单 |
| `starcloud-multiplayer/tests/Starcloud.Multiplayer.Tests/EasytierPeerListTests.cs` | 样例 JSON 更新为 v2.6.4 实测形状（4 条 peer：`p2p` / `relay(2)` / `Local` / 旧版整数 `1`），断言相应更新 |
| `starcloud-multiplayer/README.md`（新增） | 构建、CLI 快速上手、selftest 用法与原理（回环 10049 / DNS 劫持两条实测陷阱）、协议冻结五条（设计稿 §4.2）、peer 实测字段、许可与分发（EasyTier LGPL-3.0 运行时下载，二进制不进仓库/安装包，Win x86_64 v2.6.4 SHA256 `27af91e270e554709b048bd32327fefd2dfce5062ae1e8701af7550c6f525f84`） |
| `docs/designs/2026-09-30-starcloud-multiplayer-design.md` | §8.4 回填实测字段；§9 补「会合点不依赖外网」整段；§12 补三条实测坑（双绑陷阱、回环 10049、DNS 劫持）；§17 验收清单打勾 |
| `docs/plans/2026-09-30-starcloud-multiplayer-plan.md` | M3/M4 打勾，附实测记录 |

前一轮已落盘、本轮确认无误的改动：`LocalPorts.IsFree` 绑 `IPAddress.Any`（正确）；
`EasytierProcessEngine` 退出分支先抓 ExitCode/日志再 StopAsync（正确）。

## 验证结果

- `dotnet build starcloud-multiplayer/Starcloud.Multiplayer.slnx`：**0 错 0 警**
- `dotnet test`：**28/28 全绿**（`IsFree` 语义变更后无回归；peer 解析测试按实测形状更新后通过）
- `selftest --kernel-zip %TEMP%\easytier-dl\easytier-windows-x86_64-v2.6.4.zip`：
  **退出码 0，7/7 全绿**，连续两次；隧道探测拿到假 MC 的版本（`starcloud/selftest`）
  与 MOTD；路径如实显示「经公共节点中继」（`--p2p-only` 下同机打洞不成，流量走会合
  节点转发，符合设计预期）
- 离线内核包哈希核对一致；跑完后无残留 easytier 进程（自检与实验都做了清理）

## 剩余事项

- **设计稿 §17 验收清单仅第 4 条未勾**：真实 Minecraft 端到端（客人游戏内连
  `127.0.0.1:<本地端口>` 进房主世界）——需要真机双开，属人工验收，代码侧无从代办。
- 交接单明确的「不做」未越界：未 commit、无鸿蒙 ArkTS、无 Android 原生引擎、无 UI、
  无好友关系管理、无房间密码/容量/踢人。
- 实验用的临时文件在 `%TEMP%\et-exp\`、`%TEMP%\selftest-*.log`，可留作排障参考也可删。

## 硬约束执行情况

- 全程中文（代码注释/测试名也是）✅
- 构建 0 错 0 警 ✅
- EasyTier 版本锁 v2.6.4，Win x86_64 SHA256 与锁定值一致 ✅
- 本机 HTTP 绕开系统代理（沿用既有 `SocketsHttpHandler{UseProxy=false}` 路径，未破坏）✅
- 控制面与数据面解耦（内核缺失时登录/好友/房间/邀请仍可用，测试覆盖）✅
