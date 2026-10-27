# 好友聊天文件传输接口（后端实现说明）

前端（Windows 启动器）已完成：选文件 → 上传 → 以附件消息发出；收到附件消息 → 点击保存。
**消息通道零改动**：附件消息就是一条普通的私聊消息，`content` 用约定前缀承载 JSON，
后端只透传，不需要为它建表或改消息接口。需要新做的只有两个 HTTP 接口：

## 1. 上传

```
POST /v1/files
Authorization: Bearer <StarcloudID access token>
Content-Type: multipart/form-data
  file: <二进制>
```

- 大小限制：**图片 ≤ 30MB，其他（视频/文件）≤ 200MB**（前端已预检，服务端兜底）
- 建议存对象存储或磁盘 + 元数据表（id、归属用户、大小、SHA1、时间）
- `id` 全局唯一，建议带随机段防遍历（如 `f_` + 24 位随机串）

响应 200：

```json
{ "id": "f_AbCdEf1234567890abcd", "size": 1048576 }
```

- `size` 返回服务端确认的实际字节数（前端气泡上显示它）

错误：401（未登录）、413（超限）、400（表单缺失）。

## 2. 下载

```
GET /v1/files/{id}
Authorization: Bearer <StarcloudID access token>
```

响应：文件二进制，带 `Content-Length` 与原始文件名（`Content-Disposition: attachment; filename*=UTF-8''...`）。

- 鉴权：登录即可；如需收紧可校验「上传者与下载者是好友」，首轮建议先放开
- 前端用流式下载（响应头即开始读），请勿把整个文件读进内存再返回
- 可选加速：`GET /v1/files/{id}/thumb` 返回 ≤512px 缩略图（图片压缩、视频抽帧），前端后续会接

### 2.1 内联展示（图片 / 视频）

```
GET /v1/files/{id}?inline=1
```

- `Content-Disposition` 变为 `inline`，图片/视频可直接在气泡里渲染或播放
- 不传 `inline` 时保持 `attachment`（文件卡片点击「保存」用这个）
- 响应带 `Accept-Ranges: bytes`，并支持 `Range: bytes=start-end`（返回 206 +
  `Content-Range`）。**视频播放器拖动进度条依赖它**，图片可忽略
- 范围不合法时返回 416 + `Content-Range: bytes */<size>`

推荐用法：`kind=image` / `kind=video` 用 `?inline=1` 直接展示，`kind=file` 走默认附件下载。

## 3. 消息协议（后端透传，无需实现）

上传成功后，前端发送一条普通私聊消息，`content` 固定为：

```
[文件]{"name":"a.png","size":1048576,"kind":"image","id":"f_AbCdEf1234567890abcd"}
```

- `kind`: `image` / `video` / `file`
- 后端原样存储与推送即可；前端收到 `content` 以 `[文件]` 开头的消息会渲染成文件卡片
- 同样适用于未来的群聊：群聊消息只要也走 `content` 字段，附件协议直接复用

## 4. 多端同步（同一账号多设备）

服务端已保证「同一账号的多个连接同时在线」，同一账号 A 端发消息，B 端会立刻看到同一条消息。

### 4.1 SSE 会向发送方自身回显

`POST /v1/friends/{userId}/messages` 成功后，服务端会向**收发双方**都推 `friend_message`：

- 接收方（对端）实时收到
- 发送方自己的**其它设备**也收到同一条（`from_user_id` 就是自己）

因此客户端必须以 `message.id` **去重**：本机发出的那条会在 HTTP 响应里返回，
同时也会从 SSE 收到一次，两边 id 相同，只渲染一次。

### 4.2 事件带 id，可作断点

`friend_message` 事件的 SSE 帧带 `id: <message.id>`（形如）：

```
id: 10231
event: friend_message
data: {"id":10231,"from_user_id":"sc-...","to_user_id":"sc-...","content":"你好","created_at":"...","read_at":null}
```

### 4.3 已读回执

`POST /v1/friends/{userId}/messages/read` 会向双方推 `friend_read`：

```json
{ "reader_id": "sc-...", "with_user_id": "sc-...", "updated": 3, "read_at": "2026-10-27T..." }
```

- 对端据此把发出的消息标为「已读」
- 本端其它设备据此把未读红点清零

### 4.4 断线重连补齐（增量拉取）

```
GET /v1/friends/sync?since_id=<本地最大 message.id>&limit=200
Authorization: Bearer <...>
```

响应：

```json
{
  "items": [ /* 跨会话的新消息，按 id 升序 */ ],
  "next_since_id": 10231,
  "has_more": false,
  "unread": { "total": 3, "items": [{ "user_id": "sc-...", "unread": 3 }] }
}
```

- SSE 断开重连后调用一次即可补齐离线期间的消息；`has_more` 为 true 时用
  `next_since_id` 继续拉，直到 false
- `since_id` 省略或为 0 时从头返回（首启全量拉取）

> 注意：SSE 是**进程内**广播，不支持跨实例。多实例部署需要共享 pub/sub（如 Redis）。

## 5. P2P 语音 / 视频通话（信令）

服务端只做**信令中转**（SDP / ICE 透传），**不转发任何音视频流量**，媒体走双方 P2P。
信令统一复用现有的 `/v1/friends/events` SSE 长连接，无需新建通道。

### 5.1 事件类型

| 事件 | 方向 | 含义 |
| --- | --- | --- |
| `call.ringing` | 主叫其它端 | 我方发起的通话已在响铃 |
| `call.incoming` | 被叫 | 有人呼叫我，`data.offer` 为对端的 SDP offer |
| `call.answered` | 主叫 | 被叫已接听，`data.answer` 为 SDP answer |
| `call.declined` | 主叫 | 被叫拒接 |
| `call.ice` | 双方 | 对端的一个 ICE 候选，`data.from_user_id` 标明来源 |
| `call.ended` | 双方 | 通话结束（挂断、拒接或超时） |

所有 `call.*` 事件的 `data` 都含通话视图字段：`call_id`、`caller_id`、`callee_id`、
`media`（`audio` / `video`）、`state`（`ringing` / `active` / `ended`）、
`created_at`、`answered_at`、`ended_at`、`ended_by`。

### 5.2 发起通话

```
POST /v1/friends/{userId}/call
Authorization: Bearer <...>
Content-Type: application/json

{ "media": "audio", "offer": { "type": "offer", "sdp": "v=0\r\n..." } }
```

- `media` 只能是 `audio` 或 `video`，省略按 `audio`
- `offer` 是对端不透明载荷（WebRTC 的 RTCSessionDescriptionInit），服务端只透传，上限 64KB
- 返回 201 `{ "call": { "call_id": "c_...", ... } }`
- 错误：`403 not_friends` / `400 cannot_call_self` / `409 busy`（任一方已有进行中的通话）/ `400 invalid_media`

### 5.3 接听 / 拒接（被叫）

```
POST /v1/calls/{callId}/answer
{ "accept": true, "answer": { "type": "answer", "sdp": "v=0\r\n..." } }
```

- `accept: false` 即拒接（此时可省略 `answer`）
- 接听成功后主叫收到 `call.answered`（含 `data.answer`）
- 错误：`403 not_callee` / `410 call_expired` / `409 call_not_ringing` / `404 call_not_found`

### 5.4 交换 ICE 候选

```
POST /v1/calls/{callId}/ice
{ "candidate": { "candidate": "candidate:...", "sdpMid": "0", "sdpMLineIndex": 0 } }
```

- 每方每通电话发多条；服务端转发给对端（`call.ice`）并回显给本端其它设备
- 返回 202；上限 200 条/通话（超出 413 `too_many_candidates`）
- 先到先发的候选会被缓存，对端建连后仍可补收

### 5.5 挂断 / 取消

```
POST /v1/calls/{callId}/hangup
```

- 任意一方调用；响铃阶段调用即「取消呼叫」
- 双方收到 `call.ended`

### 5.6 查询进行中的通话

```
GET /v1/calls
```

返回 `{ "calls": [...], "total": n }`，用于多端登录或重连后补齐通话状态。

### 5.7 超时与并发约束

| 约束 | 值 |
| --- | --- |
| 响铃超时 | 45 秒未接自动结束并推 `call.ended` |
| 单通最长 | 4 小时（兜底回收） |
| 并发 | 一个用户同时只能参与 1 通未结束的通话 |
| 好友限制 | 只能呼叫好友 |
| 通话状态 | **仅存内存**，服务端重启即丢（进行中的通话会断开） |

### 5.8 客户端接入提示（WebRTC）

1. 呼叫：`createOffer()` → `POST .../call` 带上 offer
2. 被叫：收到 `call.incoming` → `accept? POST .../answer` 带 `createAnswer()`
3. 双方：`onicecandidate` → `POST .../ice`；收到 `call.ice` → `addIceCandidate()`
4. 挂断：`POST .../hangup`，并在收到 `call.ended` 时 `close()` 本地连接
5. 媒体是 P2P，需要双方网络允许直连；若失败可在后续版本加 TURN

> .NET 启动器若不便集成 WebRTC，可先在网页端（`starcloudfur.cn`）实现通话 UI，
> 服务端信令完全一致，两端口径无需区分。

## 6. 建议的运维参数

| 项 | 建议值 |
| --- | --- |
| 单文件上限 | 200MB（图片 30MB） |
| 用户配额 | 可先不限，观察后加（如 5GB/人） |
| 文件保留期 | 建议永久（或 ≥90 天，删除前前端要有提示机制） |
| 下载限速 | 无（前端流式下载） |
