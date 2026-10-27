# 跨互联网 P2P：发现服务（rendezvous）设计

日期：2026-10-22
状态：方案（服务端接口已定稿，待实现）
关联：`2026-10-14-p2p-download-design.md`（§5 公网 swarm 预留位）、`2026-10-14-cross-device-sync-design.md`

## 1. 目标与硬约束

目标：下载游戏文件（原版 jar / 库 / 资源 / Mod / 整合包）时，如果互联网另一端有用户已有同一文件，直接从对方机器拉取；拉不到就静默回退现有 HTTP 多源，用户无感。

硬约束（用户明确）：

1. **不做中继**。不存在 TURN，服务器不转发任何文件字节。
2. **不占服务器带宽**。服务器只收发 JSON 信令，量级为每用户每分钟几百字节。
3. **服务器只做「发现设备的端点」**。谁在线、谁有哪个文件、双方公网地址是什么。

由此推出的设计边界：

- 打洞失败就是失败，没有兜底通道，**P2P 只能是加速项，绝不能成为下载依赖**；
- 服务器不缓存、不解密、不校验文件内容，只存 `sha1 + size` 与端点；
- 对称 NAT / 运营商级 NAT（CGNAT）/ 封 UDP 的企业网必然失败，属预期行为。

## 2. 一个关键前提：endpoint 由客户端自己发现

星云后端部署在 Railway，公网入口**只支持 TCP（HTTP/HTTPS），不支持 UDP**。所以不能让服务器开一个 UDP 端口去观测客户端的 NAT 映射（即不能自建 STUN）。

方案：**客户端用 STUN 自测公网端点，再通过 HTTPS 上报**。

- 客户端在与「将来打洞用的同一个 UDP socket」上向 STUN 服务器发 Binding Request，拿到 XOR-MAPPED-ADDRESS，这个地址就是该 socket 在公网上的真实映射；
- 用公开 STUN（`stun.l.google.com:19302`、`stun.cloudflare.com:3478`）即可；后续如果有一台带 UDP 的 VPS，可以换成自建 STUN-lite（几百行），协议不变；
- 服务器不参与 UDP，**也就天然没有 UDP 反射/放大攻击面**。

代价：服务器无法验证端点真实性。伪造端点只会让自己连不上，不影响他人；对端在数据面握手时用会话令牌互相校验，所以无需服务器担保。

## 3. 拓扑

```
              ┌──────────────────────────────┐
              │  starcloudfur.cn（仅信令）    │
              │  announce / peers / session  │
              │  SSE 事件推送                 │
              └───────▲──────────────▲───────┘
        announce/查询  │              │  announce/查询/收 SSE
              ┌───────┴──────┐ ┌─────┴────────┐
              │ 客户端 A      │ │ 客户端 B      │
              │ 文件索引      │ │ 文件索引      │
              └───┬──────▲───┘ └───┬──────▲───┘
                  │UDP   │         │UDP   │
                  ▼      │         ▼      │
                 ┌───────┴─────────┴───────┐
                 │   打洞后的直连数据面      │  ← 不经过服务器
                 │  分块传输 + SHA1 校验     │
                 └─────────────────────────┘
```

数据面只走两端之间；服务器的字节消耗 = 索引上报 + 查询 + 信令，与文件大小无关。

## 4. 服务端接口

全部挂在现有 `RouteModule` 体系下，鉴权复用 `auth: 'api_key'`，错误体复用 `errorBody`。

| 方法 | 路径 | 鉴权 | 限流 | 作用 |
|---|---|---|---|---|
| POST | `/v1/p2p/announce` | api_key | 30 次/时 | 上报端点候选 + 文件索引（分批） |
| DELETE | `/v1/p2p/announce` | api_key | 30 次/时 | 主动下线，立即不可被查 |
| GET | `/v1/p2p/peers?sha1=&size=` | api_key | 300 次/时 | 查谁有该文件 |
| POST | `/v1/p2p/session` | api_key | 120 次/时 | 申请一次传输会话，拿到令牌与对端候选 |
| POST | `/v1/p2p/session/:id/close` | api_key | 300 次/时 | 结束会话（可选，TTL 兜底） |
| POST | `/v1/p2p/session/:id/report` | api_key | 300 次/时 | 上报结果 `ok` / `failed` / `canceled`，用于失败降权（须在 close 之前） |
| GET | `/v1/friends/events` | api_key | 已有 | 复用现有 SSE，新增 `p2p.request` 事件 |

### 4.1 POST /v1/p2p/announce

```json
{
  "nat": "cone",                  // cone | symmetric | unknown（STUN 双服务器比对得出）
  "ttl": 300,
  "candidates": [
    { "addr": "203.0.113.7", "port": 51234, "family": 4, "source": "stun" },
    { "addr": "203.0.113.7", "port": 51235, "family": 4, "source": "stun" },
    { "addr": "2001:db8::1", "port": 51236, "family": 6, "source": "stun" }
  ],
  "files": [
    { "sha1": "3f1c...（40 位小写十六进制）", "size": 20480000 }
  ],
  "snapshot": 7,                  // 客户端自增；同 snapshot 重复上报视为幂等
  "batch": 1, "batches": 3        // 分批上报，服务端只在该批到齐后原子切换索引
}
```

响应：

```json
{ "ok": true, "accepted": 2000, "next_announce_in": 300 }
```

规则：

- `candidates` 最多 4 个、端口 1024–65535、IPv4/IPv6 **各自最多 2 个**（后端硬校验，超了整条 400 `bad_candidates`）——客户端必须自己按家族裁剪，不能只截总数；
- `files` 单批最多 2000 条，只接受**有官方 SHA1 的文件**——客户端本来就从 manifest 里拿到了 SHA1，没有 SHA1 的文件不上报；
- **只索引 ≥ 2 MiB 的文件**（字节主体都在大文件上，小文件走 P2P 不划算）;
- 索引按用户整表替换：`snapshot` 变化的最后一批到齐后，删除该用户不在新快照里的行；
- 端点 TTL 默认 300s，客户端每 120–240s 刷新一次；索引 TTL 24h，与端点解耦（离线也保留索引，但 `peers` 只返回端点新鲜的用户）。

### 4.2 GET /v1/p2p/peers

```
GET /v1/p2p/peers?sha1=<40hex>&size=<bytes>
```

响应（自己排除；只返回 300s 内有心跳、且 `nat != symmetric` 优先排序）：

```json
{
  "sha1": "3f1c...",
  "peers": [
    {
      "id": "9a1c4e7f",                       // user_id 的不可逆短哈希，用于日志与选人
      "candidates": [{ "addr": "198.51.100.9", "port": 40122, "family": 4 }],
      "nat": "cone",
      "age": 42
    }
  ],
  "total": 1
}
```

- 最多返回 8 个对端，按「同账号设备 > 新鲜度 > 历史成功率」排序（成功率是服务端聚合的匿名计数，见 §5）；
- 不返回对端 Starcloud ID / 用户名 / 设备名，避免把索引变成社交图谱；
- 请求方拿不到对端 IP 之外的任何身份信息。

### 4.3 POST /v1/p2p/session

```json
{ "sha1": "3f1c...", "peer": "9a1c4e7f" }
```

响应：

```json
{
  "session": "s-8f3a...",
  "token": "base64(32 字节随机)",
  "expires_in": 120,
  "peer": { "candidates": [ { "addr": "198.51.100.9", "port": 40122, "family": 4 } ] }
}
```

同时向 `peer` 的 SSE 连接推送：

```
event: p2p.request
data: {"session":"s-8f3a...","token":"...","from":{"candidates":[...]},"sha1":"3f1c...","expires_in":120}
```

- `token` 是 32 字节随机数，服务器**不签名、不派生**，只在两个已鉴权通道里各发一份：请求方拿 HTTP 响应，提供方拿 SSE 推送。双方用它做 HKDF 派生数据面握手密钥，解决「跨账号双方不共享 apiKey」的鉴权问题；
- 提供方收到即视为同意（可在设置里关掉「帮陌生用户加速」），随后双方开始打洞；
- 会话 120s 过期，服务端只存 `session → (requester, provider, sha1, state)`；
- 两端都应在传输结束后上报结果：`ok` 计入成功率，`failed` 累计同一对端 3 次后 24 小时内互相不再推荐，`canceled` 不惩罚（多对端并发时输掉的那路）。

### 4.4 限流与配额

- 复用 `RateLimitRule`（`by: 'id'`），另有全局限流：单 IP 每分钟 600 次；
- 单用户索引上限 2000 条，超出的上报整批拒绝（`index_too_large`）；
- 会话并发上限 3（同一用户同时为 3 个对端供文件），超出直接 `429`——这同时是客户端上传限速的兜底；
- 失败计数：同一 `(requester, provider)` 连续 3 次会话无数据流动，24h 内不再互相推荐。

## 5. 数据模型

```sql
CREATE TABLE IF NOT EXISTS p2p_nodes (
  user_id    TEXT PRIMARY KEY REFERENCES users(user_id) ON DELETE CASCADE,
  nat        TEXT NOT NULL DEFAULT 'unknown',
  candidates TEXT NOT NULL,          -- JSON 数组
  snapshot   INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL,
  last_ok_at INTEGER
);

CREATE TABLE IF NOT EXISTS p2p_files (
  sha1       TEXT NOT NULL,          -- 40 位小写十六进制
  user_id    TEXT NOT NULL REFERENCES users(user_id) ON DELETE CASCADE,
  size       INTEGER NOT NULL,
  updated_at INTEGER NOT NULL,
  PRIMARY KEY (sha1, user_id)
);
CREATE INDEX IF NOT EXISTS idx_p2p_files_sha1 ON p2p_files(sha1);

CREATE TABLE IF NOT EXISTS p2p_sessions (
  session_id TEXT PRIMARY KEY,
  requester  TEXT NOT NULL,
  provider   TEXT NOT NULL,
  sha1       TEXT NOT NULL,
  token      TEXT NOT NULL,
  created_at INTEGER NOT NULL,
  expires_at INTEGER NOT NULL,
  state      TEXT NOT NULL DEFAULT 'offered'   -- offered | active | failed
);
CREATE INDEX IF NOT EXISTS idx_p2p_sessions_exp ON p2p_sessions(expires_at);

-- 匿名成功率，用于排序；不记录对端身份
CREATE TABLE IF NOT EXISTS p2p_stats (
  key        TEXT PRIMARY KEY,       -- 'ok:nat' / 'fail:nat' 之类的聚合键
  hits       INTEGER NOT NULL DEFAULT 0,
  updated_at INTEGER NOT NULL
);
```

清理：进程内 `setInterval` 每 60s 执行 `DELETE FROM p2p_sessions WHERE expires_at < now`；`p2p_files` 按 `updated_at < now - 24h` 清理；离线节点（`updated_at < now - 300s`）不出现在 `peers` 里，但索引保留。

存储量：单用户 2000 条索引 ≈ 80 KB；1 万活跃用户 ≈ 800 MB（SQLite 完全够用，实际远低于此）。

## 6. 打洞时序

```
A                       服务器                        B
│ ① STUN(3 个 socket)      │                           │ ① STUN(3 个 socket)
│ ② announce(candidates)  │                           │ ② announce
│ ③ GET peers(sha1) ─────▶│                           │
│◀── 返回 B 的 3 个候选    │                           │
│ ④ POST session ────────▶│── SSE: p2p.request ──────▶│ ⑤ 拿到 A 的候选 + token
│◀── token + B 候选       │                           │
│ ⑥ 每 400ms 向 B 的每个候选发 PUNCH ───────────────▶│
│◀─────────────── 每 400ms 向 A 的每个候选发 PUNCH ──│
│ ⑦ 收到对方 PUNCH → 回 PUNCH_ACK(带 token MAC) ────▶│
│◀──────────────────────────── PUNCH_ACK ───────────│
│ ⑧ 握手完成，走分块传输（任选一条打通的 socket 对）    │
```

参数与失败判定：

- 3 个 socket × 3 个对端候选 = 9 条路径并行打洞；PUNCH 包固定 32 字节；
- 每个路径每 400ms 一次，最多 8 次（≈3.2s）；全部无响应则整体判定失败；
- 总预算 8s（含 STUN 与信令），到点直接回退 HTTP 多源，进度条不显示错误，只在日志里记一行「P2P 未打通，回退」；
- 收到对端 PUNCH 但 MAC 不对：丢弃并计入失败（防止被用作探测反射）；
- 端口预测：3 个 socket 依次 bind（端口可能与映射连续），对端会把它当作独立候选来试，这已覆盖大部分端口受限锥形 NAT；
- IPv6：有全球地址时优先用 v6 直连（通常无需打洞），v4 打洞作为并行路径；
- STUN 必须走**同一个 socket 的唯一收包循环**：常驻收包循环一旦启动，谁再自己 `ReceiveFrom` 就会互相抢数据报（实测表现是「测试进程里探测正常、应用里全部超时」）；
- NAT 判定：同一 socket 向两个不同 STUN 服务器取映射，一致为 `cone`，不一致为 `symmetric`（`symmetric` 仍上报，但 `peers` 排序靠后，且打洞预算减半）。

预期成功率（经验值，非承诺）：全锥/受限锥 ~85%，端口受限锥 ~70%，对称 NAT ~10–30%，CGNAT/封 UDP 接近 0。

## 7. 数据面协议（UDP 上的分块传输）

打洞成功后在 UDP 上跑一个极简可靠传输；不复用现有 TCP 的 `PeerLinkServer`（TCP 无法打洞），但**语义保持 `p2p.has` / `p2p.fetch` 一致**，只是帧从「JSON 行 + 8 字节长度前缀」换成定长头 + 分块。

帧格式（小端）：

```
offset size field
0      2    magic 0x5343 ("SC")
2      1    version = 1
3      1    type: 1=HELLO 2=HELLO_ACK 3=DATA 4=ACK 5=FIN 6=ABORT
4      4    session tag（token 的 HKDF 前 4 字节）
8      4    seq
12     4    ack_seq
16     4    ack_bits（位图，ack_seq 之前 32 个块）
20     n    payload（DATA 时 ≤1100 字节）
```

- 握手：`HELLO { sha1, size, nonce }` → `HELLO_ACK { nonce+1 }`，两端都用 `HMAC-SHA256(HKDF(token, "sc-p2p-v1"), nonce | session tag)` 验证，任何一端验证失败立即 ABORT；
- 请求方发 `HELLO` 时带 `offset`（续传位点），提供方从该偏移开始发；
- 窗口 64 块（≈70 KB 在途），接收方每收到 16 块或 10ms 回一次 `ACK`；发送方 3 次重复 ACK 或 RTO（初始 300ms，按 RTT 估算）重传；
- 限速：令牌桶默认 4 MB/s/对端，可在设置里调；单用户并发供文件 ≤3；
- 完整性：传输结束（`FIN`）后请求方校验整文件 SHA1，不匹配就整份丢弃并回退 HTTP——**与局域网 P2P 同一安全线**；
- MTU：payload 1100 字节，配合 20 字节头 = 1120，低于 1280（IPv6 最小 MTU），不触发分片。

## 8. 安全与滥用

| 风险 | 处理 |
|---|---|
| 把服务当通用网盘索引 | 只接受有 SHA1 的文件；只索引 ≥2 MiB；单用户 2000 条上限；单批上限 2000 |
| 端点被伪造用于探测/扫描 | 服务端不保证端点；对端收到 PUNCH 必须通过 token MAC 才回应；失败计数自动降权 |
| 数据投毒 | 请求方整文件 SHA1 校验，失败即弃；来源不计入评分 |
| 反射/放大 | 服务器不监听 UDP；两端 PUNCH 包等长（32B），无放大比 |
| 隐私（IP 暴露） | 只有发起会话的双方互换公网 IP，且仅在该 sha1 上；`peers` 不暴露身份；可关「帮陌生用户加速」 |
| 版权分发 | 索引只来自官方 manifest 的 SHA1 与用户本地已有文件，与现有局域网 P2P 同一口径；不服役任何中心文件库 |
| 频率滥用 | 各端点独立限流 + 单 IP 全局限流；异常用户由管理员封禁 |
| 令牌泄漏 | 会话令牌 120s 过期、一次性；服务端不落明文日志（只记 session_id 与 user_id 短哈希） |

## 9. 服务端实现落点

新增文件：

- `server/routes/p2p.ts`：四个端点的 handler，风格对齐 `routes/friends.ts`；
- `server/repositories/p2p.ts`：节点/索引/会话的读写与清理；
- `server/db.ts`：追加 §5 的建表 SQL（现有 `db.exec` 里直接加 `CREATE TABLE IF NOT EXISTS`）；
- `server/routes/index.ts`：把 `p2pModule` 加进 `routeModules`；
- `server/realtime.ts`：`FriendEventType` 增加 `'p2p.request'`（SSE 通道复用，不新开连接）。

骨架（节选，落地时按现有仓库风格补全）：

```ts
// routes/p2p.ts
import type { RequestHandler, Response } from 'express'
import { getAuth } from '../middleware/auth.ts'
import { publish } from '../realtime.ts'
import {
  announceNode, findPeers, createSession, closeSession, prune,
} from '../repositories/p2p.ts'
import { errorBody, type RouteModule } from '../types.ts'

const MAX_BATCH = 2000
const MAX_CANDIDATES = 4
const SESSION_TTL = 120

function fail(res: Response, status: number, code: string, message: string) {
  res.status(status).json(errorBody(code, message))
}

const announce: RequestHandler = (req, res) => {
  const auth = getAuth(res)
  const body = req.body ?? {}
  const candidates = Array.isArray(body.candidates) ? body.candidates : []
  const files = Array.isArray(body.files) ? body.files : []
  if (candidates.length === 0 || candidates.length > MAX_CANDIDATES) {
    return fail(res, 400, 'bad_candidates', '端点候选数量不合法')
  }
  if (files.length > MAX_BATCH) {
    return fail(res, 400, 'batch_too_large', `单批最多 ${MAX_BATCH} 条`)
  }
  const result = announceNode(auth.userId, body)
  res.status(200).json(result)
}

const peers: RequestHandler = (req, res) => {
  const auth = getAuth(res)
  const sha1 = String(req.query.sha1 ?? '').toLowerCase()
  if (!/^[0-9a-f]{40}$/.test(sha1)) return fail(res, 400, 'bad_sha1', 'sha1 不合法')
  res.status(200).json({ sha1, ...findPeers(auth.userId, sha1) })
}

const session: RequestHandler = (req, res) => {
  const auth = getAuth(res)
  const sha1 = String(req.body?.sha1 ?? '').toLowerCase()
  const peer = String(req.body?.peer ?? '')
  const created = createSession(auth.userId, peer, sha1, SESSION_TTL)
  if (!created) return fail(res, 404, 'peer_unavailable', '对端不在线或没有该文件')
  // 提供方走已有 SSE 通道
  publish(created.provider, {
    type: 'p2p.request',
    data: {
      session: created.sessionId,
      token: created.token,
      sha1,
      expires_in: SESSION_TTL,
      from: { candidates: created.requesterCandidates },
    },
  })
  res.status(200).json({
    session: created.sessionId,
    token: created.token,
    expires_in: SESSION_TTL,
    peer: { candidates: created.providerCandidates },
  })
}

export const p2pModule: RouteModule = {
  name: 'p2p',
  routes: [
    { method: 'post', path: '/v1/p2p/announce', auth: 'api_key',
      rateLimit: { windowMs: 3600_000, max: 30, by: 'id' }, handler: announce },
    { method: 'delete', path: '/v1/p2p/announce', auth: 'api_key',
      rateLimit: { windowMs: 3600_000, max: 30, by: 'id' }, handler: offline },
    { method: 'get', path: '/v1/p2p/peers', auth: 'api_key',
      rateLimit: { windowMs: 3600_000, max: 300, by: 'id' }, handler: peers },
    { method: 'post', path: '/v1/p2p/session', auth: 'api_key',
      rateLimit: { windowMs: 3600_000, max: 120, by: 'id' }, handler: session },
    { method: 'post', path: '/v1/p2p/session/:id/close', auth: 'api_key',
      rateLimit: { windowMs: 3600_000, max: 300, by: 'id' }, handler: close },
  ],
}
```

服务器进程启动时挂一个 60s 的清理定时器（`index.ts` 或 repository 初始化里），避免会话表无限增长。

## 10. 客户端配合

新增（`dotnet/src/Starcloud.Core/Services/P2P/`）：

| 文件 | 职责 |
|---|---|
| `StunClient.cs` | 3 个 UDP socket 的 STUN Binding 探测（v4 / v6 分别探）、双服务器比对判 NAT、候选生成 |
| `RendezvousClient.cs` | announce / peers / session 的 HTTP 调用，与 `FriendsApi` 同一 apiKey 体系 |
| `P2PUdpChannel.cs` | 多 socket 打洞、会话帧收发、分块发送/重传/ACK、令牌桶限速 |
| `P2PFileLedger.cs` | 下载成功后记账 `sha1+size+路径`，按 ≥2 MiB 过滤、上限 5000 条、JSON 落盘；供之前整文件复算 SHA1 |
| `P2PIndexBackfill.cs` | 启动时按已安装版本 json 反推 client jar / libraries / assets，存在且大小一致即登记（不哈希，供文件前才复算） |
| `InternetP2PService.cs` | 生命周期、announce 循环、`IPeerFileSource` 拉取、`p2p.request` 供文件 |

接入顺序（在现有管线里插在第 ③ 步与第 ④ 步之间）：

```
① 去重 → ② 本地复用 → ③ 局域网对端复用 → ③.5 互联网 P2P（新） → ④ HTTP 多源
```

- 只对 `sha1 + size` 都已知、且 ≥2 MiB 的文件尝试；
- 预算分两段：建链（查对端 → 申请会话 → 打洞 → 握手）9 秒；传输按 2 MB/s 保守估算（15–120 秒），避免稍大的 jar 在传一半时被掐断；
- 超时/失败立即切 HTTP；多个对端并行拉取，先完成者原子落盘，其余立刻取消；
- 开关：设置页「跨设备加速」新增「互联网用户互助」（默认关，首次开启弹一次说明：会向其他用户披露公网地址、并占用少量上行）；
- 安卓端同一套 Core，开关默认关。

## 11. 成本与容量

| 项目 | 估算 |
|---|---|
| 单用户 announce | ~2000 条 × 40 字节 ≈ 8 KB / 5 分钟 |
| 单次 peers 查询 | < 1 KB |
| 单次会话信令 | < 1 KB |
| 文件流量 | **0**（服务器不接触文件字节） |
| 1 万活跃用户 | 索引 ~800 MB SQLite，峰值 QPS < 10 |
| 带宽 | 与文件分发无关，量级为几十 KB/s |

对比：若做中继，一次 500 MB 的整合包分发就是 500 MB 服务器流量；本方案恒为 0。

## 12. 分阶段

| 阶段 | 内容 | 验收 |
|---|---|---|
| M1 | 后端四端点 + 索引表 + SSE 事件；客户端 `P2PIndex` + `RendezvousClient` | 两端 announce 后，`peers` 能互相查到；索引与 TTL 正确 |
| M2 | STUN 探测 + UDP 打洞 + 分块传输 + 供文件 | 同城两家宽带（一锥一受限锥）实测互通；SHA1 一致 |
| M3 | 端口预测、多候选、IPv6 优先、失败降权 | 受限锥成功率 ≥60%，失败静默回退无感知 |
| M4 | 观测（命中率/上传量/回退原因）、设置页开关、安卓对齐 | 后台能看到总体打洞成功率与流量节省 |

## 13. 已知限制（必须向用户说明）

- 对称 NAT / CGNAT / 企业防火墙封 UDP：打洞不成立，**本方案不做中继就不会有兜底**；
- 两端必须同时在线（TTL 300s），离线用户不会被推荐；
- 服务器只保证「索引可信度」为「用户自称 + 数据面 SHA1 校验」，不保证提供方一定给得出来；
- 首次互通需要双方都在下载/在线状态，冷启动命中率低，热门文件（原版库、资源）命中率会随时间上升。
