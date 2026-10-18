# 跨设备存档 / 版本数据同步 设计

日期：2026-10-14
状态：M1 已实现（局域网通道全量可用；蓝牙 / 星闪通道按能力探测降级）
关联：`2026-10-14-p2p-download-design.md`（共用同一条设备链路 DeviceLink）

## 1. 目标与非目标

**目标**：同一 Starcloud ID 下的多台设备（PC / 笔记本）之间，同步：

| 同步对象 | 内容 | M1 |
|---|---|---|
| 存档 | 世界目录（`saves/<folder>`，含 level.dat / 区域文件 / 图标） | ✅ 推送 / 拉取 |
| 版本数据 | 该存档所属版本的实例配置（隔离 / 内存 / JVM 参数） | ✅ 随存档附带 |
| 全局配置 | `.minecraft` 全局设置子集 | ⛔ M2（协议位已留） |

**非目标（明确不做）**：
- 不做实时自动同步（无文件监视器）——同步是显式动作（用户点「同步到设备」），避免游戏中写坏存档；
- 不经云端中转存档内容（隐私 + 体积）；云端只负责「让同账号设备互相认识」这件事由本地账号体系自证（见 §4）。

## 2. 通道

统一抽象 `IBearerProbe`（通道探针），三通道按能力探测，UI 如实展示状态：

| 通道 | 承载 | 状态 |
|---|---|---|
| WiFi / 局域网 | UDP 广播发现（:47777） + TCP 传输（:47778），IPv4 | **主通道，M1 全量实现** |
| 蓝牙 | RFCOMM SPP（固定服务 GUID），发现走 SDP | 需要应用包身份（unpackaged 无法声明 `bluetooth` capability）→ 探针返回 `NeedsPackaging`，UI 标注"暂不可用"；数据面协议与 LAN 同一套（帧协议不依赖承载） |
| 星闪 NearLink | 华为 NearLink SDK（私有栈） | 探针检测本机 NearLink 驱动服务，不存在 → `Unavailable`；协议接入点已在 `IBearerProbe` 预留，未来华为 SDK 发布后按 LAN 同一套帧协议实现 |

> 关键设计：**帧协议与承载解耦**。`PeerLinkServer/Client` 只面向 Stream；LAN 是 `NetworkStream`，未来蓝牙/星闪通道把各自的 Socket 流接进来即可复用全部握手与命令。

## 3. 设备发现（UDP beacon）

- 每 3 秒向 `255.255.255.255:47777` 广播一条 JSON：

```json
{"v":1,"uid":"<sha256(user_id) 前 16 位 hex>","name":"设备名","port":47778,"app":"starcloud"}
```

- 监听方校验 `v`/`app`，维护 peer 表（`uid+port` 为键，`lastSeen` 超过 15 秒剔除）。
- **同账号过滤**：`uid` 不等于本机 `uid` 的广播直接丢弃——不同 Starcloud ID 的设备互相不可见（隐私默认）。

## 4. 鉴权（依赖 Starcloud ID 登录）

- 同步功能总闸 = `DeviceLinkService.Start()` 前置条件：`StarcloudId.IsLoggedIn` 且 `Settings.DeviceLinkEnabled`。未登录 / 未开启时 beacon 与 TCP 服务都不启动，UI 入口提示登录。
- TCP 握手（挑战-应答，apiKey 永不上线）：
  1. C→S hello：`{uid, name, challenge}`（S 校验 `uid == 本机 uid`，不同账号直接断开）；
  2. S→C：`{challenge2}`；
  3. C→S：`hmac1 = HMAC-SHA256(apiKey, challenge + challenge2)`；
  4. S 验证后回 `hmac2 = HMAC-SHA256(apiKey, challenge2 + challenge)`；双方各验一次。
- 同账号 apiKey 由同一登录体系下发（测试通道为确定性派生，云端通道为服务端创建），同账号设备天然持同一密钥。
- 威胁模型与边界：M1 保护的是「同网段陌生人无法发现 / 无法读取」（uid 过滤 + HMAC）；同账号账号被劫持属于账号安全问题，不在链路层解决。M2 计划在文件流上叠加会话密钥加密（ECDH → AES-GCM）。

## 5. 命令协议（JSON 行 + 长度前缀二进制）

握手后每行一个 JSON 命令，文件体用「8 字节小端长度 + 流」：

| 命令 | 方向 | 语义 |
|---|---|---|
| `saves.list` | C→S | 对端存档清单（SaveLibraryService 聚合：共享根 + 隔离实例根） |
| `saves.get` | C→S | 取指定世界：对端现场打 zip（复用 SaveService.Backup 打包逻辑）→ 回 `{size,sha256}` + zip 流 |
| `cfg.get` | C→S | 取指定版本的实例配置 JSON（隔离 / 内存 / JVM / 窗口） |
| `p2p.has` / `p2p.fetch` | C→S | P2P 下载加速（见另一篇设计） |

## 6. 同步流程与冲突策略

**推送（存档页 →「同步到其他设备」）**：

1. 选目标设备 → 发起端把本地世界打包（复用备份 zip 逻辑）；
2. 目标端同名世界已存在 → 冲突裁决：比较双方 `level.dat` 的 `LastPlayed`：
   - **本地（目标端）较新 → 拒收**，发起端提示「对端的世界更新，已保留对端版本」；
   - ** incoming 较新 → 目标端旧世界自动改名 `<folder>.conflict-<时间戳>` 保留后再接收**；
   - 两边永远不丢数据（与项目「停用即改名」同一哲学）。
3. zip 落地后 SHA256 校验 → 解压进目标端对应根的 `saves/`；
4. 附带版本配置：发起端同时发送该版本的实例配置 JSON——目标端已装同 Id 版本则直接应用，未安装则缓存为 `<dotMc>/starcloud/pending-config/<versionId>.json`，安装该版本时读取应用。

**拉取**（设置页 →「从设备拉取存档」）：对称，方向反转，`saves.get` + 同一套冲突裁决。

## 7. 设置项

| 键 | 默认 | 说明 |
|---|---|---|
| `device_link_enabled` | false | 设备链路总开关（beacon + TCP 服务） |
| `device_name` | 机器名 | 对端展示名 |
| `p2p_download_enabled` | true | P2P 下载加速（见另一篇设计） |

## 8. M1 实现清单

- Core：`Services/DeviceLink/`（PeerBeacon / PeerLinkServer / PeerLinkClient / DeviceLinkService / SaveSyncService / PeerFileReuse）
- 单测：握手（含错误密钥拒答）、清单、zip 取回、p2p 命中/未命中、冲突裁决纯函数、beacon 帧编解码
- App：设置页「跨设备」卡片（开关 / 设备名 / 通道状态）、存档页「同步到其他设备」、AppServices 接线与启停
