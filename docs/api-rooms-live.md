# 联机房间服务（已上线）接口说明

> 面向负责启动器联机模块（`Starcloud.Core/Services/Rooms`）的 Agent。
> 本文档描述**已部署、已通过端到端验证的服务端实现**，字段与错误码均以服务端源码为准。
> 接口设计来源仍是 `docs/api-rooms.md`；两者冲突时以本文档为准。

## 1. 接入信息

| 项 | 值 |
| --- | --- |
| 服务地址 | `https://rooms.starcloudfur.cn`（标准 443，独立 IP `191.40.37.210`） |
| 鉴权 | `Authorization: Bearer <ApiKey>`（与好友/文件服务同一枚 `sk-...` Key） |
| 协议 | HTTPS（TLS 1.2 / 1.3，已开启 http2）+ SSE |
| 路径前缀 | `/v1/rooms` |

启动器侧只需改一处：

```csharp
// RoomService.cs
public const string BaseUrl = "https://rooms.starcloudfur.cn";
```

`auth: any` 表示 **API Key 或登录会话 token** 都可用，与 Sentry 一致：启动器侧继续用 API Key。

## 2. 部署结构（排查用）

```
客户端 --https(443)--> nginx (官网服务器 191.40.37.210) --proxy--> 127.0.0.1:54090 (systemd: starcloud-rooms)
                                                                 └── SQLite /opt/starcloud/server/data/starcloud.sqlite
```

- 机器：官网服务器 `191.40.37.210`（与官网 API 同机，独立公网 IP）
- 进程：`starcloud-rooms.service`（`STARCLOUD_PORT=54090`，`STARCLOUD_ROUTES=health,rooms`，代码目录 `/opt/starcloud`）
- 数据库：与官网主服务 `starcloud-id` 共用同一个 SQLite（`rooms` / `room_members` / `room_messages` 三张新表），`users` / `api_keys` 天然共享
- nginx vhost：`/www/server/panel/vhost/nginx/rooms.conf`，证书 `/www/server/panel/vhost/cert/rooms/`
- nginx 已配置 `proxy_buffering off` / `proxy_read_timeout 3600s`，SSE 可长连
- 对象存储配置在 `/opt/starcloud/rooms.env` 的 `STARCLOUD_S3_*`（桶 `starcloud`，公开前缀 `https://starcloud.cn-nb1.rains3.com`）
- 健康检查：`GET https://rooms.starcloudfur.cn/v1/health`
- 旧地址 `https://rooms.starcloudfur.cn:54088`（183.66.27.20）已下线，不要再使用

## 3. 通用约定

### 错误响应

统一为：

```json
{ "error": { "code": "room_not_found", "message": "房间不存在或已关闭" } }
```

`message` 可直接展示给用户（已是中文），`code` 用于分支判断。

### 限流

- 除建房外，均为**按 IP** 计数，窗口 1 分钟
- 建房按 **API Key 指纹** 计数：同一 Key 每 60 秒最多 1 次，且同一用户**同时最多 1 间开放中的房间**
- 触发限流：`429` + `Retry-After` 头 + `code=rate_limited`

各接口额度：

| 接口 | 额度 / 分钟 |
| --- | --- |
| `GET /v1/rooms`、`GET /v1/rooms/mine` | 120 |
| `POST /v1/rooms` | 3 次 / 60 秒（按 Key） |
| `POST /v1/rooms/packs` | 10 次 / 60 秒（按 Key） |
| `GET /v1/rooms/:code` | 120 |
| `POST /v1/rooms/:code/join` | 30 |
| `POST /v1/rooms/:code/leave`、`kick`、`PUT terracotta`、`POST messages` | 60 |
| `DELETE /v1/rooms/:code` | 20 |
| `GET /v1/rooms/:code/messages`、`SSE events`、`POST heartbeat` | 120 / 60 / 120 |

### 联机码

- 6 位数字，作为房间主键；服务端会忽略输入中的空格、`-`、`_`，非法格式按「不存在」处理
- 客户端可继续用 `RoomCodeRules.Normalize` 预校验

## 4. 数据结构

### RoomSummary

```json
{
  "code": "123456",
  "name": "周五联机房",
  "owner": { "user_id": "sc-xxxx", "display_name": "星渺", "avatar_url": "https://... | null" },
  "members": 3,
  "max_players": 8,
  "has_password": true,
  "mc_version": "1.21.4",
  "loader": "fabric",
  "pack": {
    "file_id": "rooms/packs/<sha256>.zip",
    "url": "https://starcloud.cn-nb1.rains3.com/rooms/packs/<sha256>.zip",
    "size": 123456789, "sha256": "..." ,
    "format": "modrinth", "mc_version": "1.21.4", "loader": "fabric", "name": "整合包名"
  },
  "has_terracotta": true,
  "created_at": "2026-10-30T...Z",
  "state": "open"
}
```

`pack` 为 `null` 表示非整合包房间。整合包文件放在对象存储，`url` 就是公开下载地址（直接给启动器下载，不经过服务端）。

### RoomDetail（详情 / 加入 响应中的 room 字段）

```json
{
  "room": RoomSummary,
  "members": [
    { "user_id": "sc-xxxx", "display_name": "星渺", "avatar_url": null,
      "role": "owner | member", "online": true }
  ],
  "joined": true,
  "is_owner": false
}
```

> `online` = 该用户在房间内**当前存在至少一条 SSE 连接**。启动器只有保持 SSE 连接才会显示在线。

### RoomMessage

```json
{
  "id": 1024,
  "room_code": "123456",
  "from_user_id": "sc-xxxx",
  "display_name": "星渺",
  "avatar_url": null,
  "content": "开服了",
  "created_at": "2026-10-30T...Z"
}
```

## 5. 接口清单

### 5.1 大厅

`GET /v1/rooms` → `200`

```json
{ "rooms": [RoomSummary...] }
```

最多返回 100 间开放中的房间（按活跃倒序）。

### 5.2 我的房间

`GET /v1/rooms/mine` → `200` `{ "rooms": [RoomSummary...] }`

返回 7 天内活跃、且我为成员的房间，最多 20 条（含已关闭的，可据 `state` 区分）。

### 5.3 创建房间

`POST /v1/rooms`

```json
{
  "name": "周五联机房",
  "max_players": 8,
  "password": "可选，≤64 字符",
  "mc_version": "1.21.4",
  "loader": "vanilla | forge | fabric | neoform | quilt",
  "pack": {
    "file_id": "必填，先调 POST /v1/rooms/packs 上传后返回的 file_id",
    "size": 123,
    "sha256": "可选",
    "format": "modrinth | curseforge | multimc | raw",
    "mc_version": "1.21.4",
    "loader": "fabric",
    "name": "整合包名"
  }
}
```

- `name` 必填，≤32 字符
- `max_players` 默认 8，范围 2–20（超范围 400）
- `loader` / `pack.format` 不在白名单内会被**忽略**（置 null），不报错
- 无 `pack` 时按普通房间创建
- `pack.size` / `pack.sha256` 以服务端登记的上传记录为准，客户端传了也不采信

响应 `200`：`{ "room": RoomSummary }`（成员数此时为 1）

错误：

| 状态 | code | 说明 |
| --- | --- | --- |
| 400 | `invalid_name` | 名称为空 |
| 400 | `name_too_long` | 名称超 32 字符 |
| 400 | `invalid_max_players` | 人数不在 2–20 |
| 400 | `password_too_long` | 密码超 64 字符 |
| 400 | `invalid_pack` | 缺少 `file_id`，或该 `file_id` 未在对象存储登记 |
| 429 | `room_limit` | 已有开放中的房间，需先解散 |

### 5.4 房间详情

`GET /v1/rooms/:code` → `200` `{ "room": RoomDetail }`

不含密码，陶瓦码也不在详情里（只对已加入成员在 join 响应中下发）。

错误：`404 room_not_found`（不存在；已关闭的房间同样返回 404）。

### 5.5 加入房间

`POST /v1/rooms/:code/join`

```json
{ "password": "有密码时必填" }
```

响应 `200`：

```json
{ "room": RoomDetail, "terracotta_code": "1234567890 | null" }
```

- 已在房内再次调用 → 幂等返回，不重复计成员
- 密码只存 scrypt 哈希，永不下发；`has_password` 仅供 UI 判断是否弹输入框

错误：

| 状态 | code | 说明 |
| --- | --- | --- |
| 404 | `room_not_found` | 房间不存在 |
| 410 | `room_closed` | 房间已关闭 |
| 403 | `bad_password` | 密码错误 |
| 409 | `room_full` | 人数已满 |

### 5.6 退出房间

`POST /v1/rooms/:code/leave` → `200` `{}`

未在房内也返回 `200 {}`（幂等）。房主不能退出，`400 owner_must_dissolve`。

SSE：向房间广播 `member_left`。

### 5.7 踢人（房主）

`POST /v1/rooms/:code/kick`，body `{ "user_id": "目标用户" }` → `200 {}`

- 不能踢自己（`400 invalid_target`）
- 目标不在房内 → `404 member_not_found`

SSE：**仅**向被踢者推 `kicked`（`{user_id, by}`），同时向全房推 `member_left`。

### 5.8 解散房间（房主）

`DELETE /v1/rooms/:code` → `200 {}`

SSE：先广播 `room_closed`，随后服务端主动断开该房间所有 SSE 连接。

### 5.9 上报陶瓦联机码（房主）

`PUT /v1/rooms/:code/terracotta`，body `{ "terracotta_code": "≤128 字符" }` → `200 {}`

SSE：`terracotta_ready`（`{ "terracotta_code": "..." }`）。

错误：`400 invalid_terracotta`、`403 not_owner`。

### 5.10 聊天记录

`GET /v1/rooms/:code/messages?before_id=&limit=` → `200` `{ "messages": [RoomMessage...] }`

- 返回**按 id 升序**（旧 → 新），便于直接追加进列表
- `limit` 默认 50，服务端上限 100
- `before_id` 用于向上翻页（取 id 小于它的一页）
- 需在房内，否则 `403 not_in_room`

### 5.11 发送消息

`POST /v1/rooms/:code/messages`，body `{ "content": "≤2000 字符" }` → `200` `{ "message": RoomMessage }`

错误：`400 empty_message` / `400 message_too_long` / `403 not_in_room` / `410 room_closed`

SSE：向全房广播 `message`（内容与 REST 响应中的 message 相同）。客户端按 `id` 去重即可避免自己发出的消息重复。

### 5.12 实时事件流（SSE）

`GET /v1/rooms/:code/events`

- `Content-Type: text/event-stream`，每 30 秒一行 `: ping` 注释心跳
- 需在房内，否则 `403 not_in_room`
- **该连接同时承担「在线状态」与「保活」**：建立即 `online=true`；断开 60 秒内房主无任何在线连接 → 房间被自动关闭

事件格式：

```
event: <type>
data: <json>

```

事件类型：

| event | data | 说明 |
| --- | --- | --- |
| `member_joined` | `{ "member": {...} }` | 有人加入（含 role / online） |
| `member_left` | `{ "user_id": "..." }` | 主动退出 / 被踢 / 超时移出 |
| `kicked` | `{ "user_id": "...", "by": "..." }` | 仅推给被踢者 |
| `message` | `RoomMessage` | 新消息 |
| `terracotta_ready` | `{ "terracotta_code": "..." }` | 房主上报联机码 |
| `room_closed` | `{}` | 解散或超时自动关闭，随后连接被服务端关闭 |

### 5.13 心跳（保活）

`POST /v1/rooms/:code/heartbeat` → `200 {}`

需在房内。用于**不开 SSE 的界面**（如仅挂在房间里等待）刷新活跃时间，避免 60 秒超时关房。错误：`403 not_in_room`。

### 5.14 上传整合包（对象存储）

`POST /v1/rooms/packs?name=整合包.zip`

- 请求体就是文件**原始字节**：`Content-Type: application/octet-stream`，建议显式带 `Content-Length`
- 单文件上限 **200MB**，超出返回 `413`
- 服务端边落盘边算 sha256，随后转存对象存储（内容寻址：同内容重复上传得到同一个 `file_id`，不会重复占空间）
- 响应 `200`：

```json
{
  "pack": {
    "file_id": "rooms/packs/<sha256>.zip",
    "url": "https://starcloud.cn-nb1.rains3.com/rooms/packs/<sha256>.zip",
    "size": 123456789,
    "sha256": "...",
    "name": "整合包.zip"
  }
}
```

- 建房时把 `file_id` 放进 `pack.file_id` 即可；`url` 直接给启动器做下载链接（对象存储公开可读）
- 扩展名白名单：`.zip` `.mrpack` `.jar` `.7z` `.rar`，其它一律按 `.zip` 存

错误：`400 empty_file` / `400 upload_failed`（上传中断）/ `413 pack_too_large` / `502 storage_failed`（转存失败）/ `503 storage_unavailable`（未配置对象存储）。

## 6. 房间生命周期

| 规则 | 值 |
| --- | --- |
| 房主无在线连接超时 | 60 秒 → 自动关房并广播 `room_closed` |
| 普通成员无连接超时 | 10 分钟 → 自动移出并广播 `member_left` |
| 关闭房间保留 | 24 小时后级联删除（期间该联机码不会被复用） |
| 回收扫描周期 | 15 秒 |
| 同一用户同时开放房间 | 1 间 |

「活跃」判定口径：房间内存在 SSE 连接，**或**最近 60 秒内有任意 `/v1/rooms/:code/*` 请求（详情、心跳、消息等都会刷新）。

## 7. 客户端对接要求

1. `RoomService.BaseUrl` 改为 `https://rooms.starcloudfur.cn`（原 IP + http + 54088 端口已弃用）。
2. SSE 必须走**无超时**的 HttpClient（`RoomService.cs` 中 `StreamHttp` 已是 `Timeout.InfiniteTimeSpan`），并处理断线重连：重连后先拉一次 `GET /v1/rooms/:code` 补齐 `members` 与 `state`，再拉一次 `messages` 补消息（SSE 不保证补偿断线期间的事件）。
3. 断线重连退避建议 2s / 5s / 10s，最多 30s；`room_closed` 后不再重连。
4. 建房 429 `room_limit` 要提示「已有房间，先解散」，不要当成普通限流重试。
5. 整合包流程：`POST /v1/rooms/packs`（原始字节流）→ 拿到 `file_id` 与 `url` → 建房时引用 `file_id`；用户点下载直接用 `url`，不要再走服务端。
6. 联机流量不经过服务端：陶瓦码/地址由房主上报，服务端只转发消息与事件，启动器自行建立 Minecraft 连接。

## 8. 已知问题与约束

- 整合包存放在 rains3 对象存储（桶 `starcloud`，前缀 `rooms/packs/`），随房间解散**不会**删除对象，便于复用；对象存储出口带宽有限，大包（100MB+）下载速度可能只有几 MB/s，启动器需要给下载进度与失败重试。
- 对象存储为公开读：任何拿到 `url` 的人都能下载，不要把该地址当私有资源。
- 房间接口同时挂在官网主站 `/v1` 下（`https://starcloudfur.cn/v1/rooms`），但**事件流必须走 `rooms.starcloudfur.cn`**：SSE 连接保存在独立进程内存中，走主站进程收不到事件。
- 部署验证：官网服务器上 e2e 冒烟全部通过 —— 基础链路 16 项（建房 / 详情 / 大厅 / 我的 / SSE message 事件 / 消息历史 / 陶瓦码 / 404 / 401 / 解散），整合包链路 7 项（上传 → 对象存储 URL → 公网下载 200 → 未登记 pack 拒绝 → 建房引用 → 返回 url → 解散）；测试数据已清理，仅对象存储遗留 2 个几十字节的测试对象。
