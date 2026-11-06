# 联机房间接口（/v1/rooms/*，后端实现说明）

前端（Windows 启动器）已按本文档实现完毕（`RoomService` + 房间页三页签），
接口未上线期间前端自动降级本地演示数据，服务端按本文实现后无需前端改动。

**鉴权**：与 `/v1/friends/*` 相同 —— `Authorization: Bearer <StarcloudID access token>`。
所有端点都要登录；`user_id` 一律取 token 里的用户，不信任请求体。

**字段风格**：snake_case（与 friends/files 一致）。响应都是单层 JSON 对象，
列表包在 `{"rooms": [...]}` / `{"messages": [...]}` 里。

---

## 0. 核心概念与生命周期

- **联机码 `code`**：6 位数字字符串（如 `"318274"`），房间的唯一标识，**服务端生成**，
  全服唯一、不与历史重复（回收后的码至少 24h 内不得复用）。
- **密码**：只在服务端校验与存储（建议 hash），**任何列表/详情接口都不得下发**，
  只给 `has_password: true/false`。
- **陶瓦邀请码 `terracotta_code`**：房主在游戏内开好局域网联机后由客户端上报
  （形如 `U/XXXX-XXXX-XXXX-XXXX` 的长码，陶瓦核心自己管理有效期）。
  **只有验密加入成功（或自己是房主）才下发**，大厅列表与未加入的详情一律不给。
- **整合包 `pack`**：房主建房时上传（复用 `/v1/files` 的文件 id），玩家加入后
  客户端自行 `GET /v1/files/{id}` 下载安装。服务端只存元数据，不解析包内容。

```
房主                          服务端                         玩家
 │ POST /v1/rooms（建房） ────► 生成 6 位码
 │ 启动游戏 → 开局域网联机
 │ PUT .../terracotta ───────► 存陶瓦码 ──── SSE terracotta_ready ───► 已在房内的玩家
 │                              ▲
 │                              │ POST .../join（验密）────────► 玩家请求加入
 │                              │   返回 detail + terracotta_code
 │                              │ SSE member_joined 广播全体
 ▼ 游戏退出 / 断线              │
 └── 服务端检测超时 ──► state=closed ── SSE room_closed ──► 全体成员，码失效
```

## 1. 数据形状

### RoomSummary（大厅行 / 详情里的 room / mine 列表共用）

```json
{
  "code": "318274",
  "name": "生存服一起玩",
  "owner": { "user_id": "u_100", "display_name": "阿星", "avatar_url": null },
  "members": 3,
  "max_players": 8,
  "has_password": false,
  "mc_version": "1.20.1",
  "loader": "fabric",
  "pack": {
    "file_id": "f_AbCdEf1234567890abcd",
    "size": 52428800,
    "sha256": "…（可空）",
    "format": "modrinth",
    "mc_version": "1.20.1",
    "loader": "fabric",
    "name": "轻量生存包"
  },
  "has_terracotta": true,
  "created_at": "2026-10-27T12:00:00Z",
  "state": "open"
}
```

- `loader`：`vanilla / forge / fabric / neoform / quilt`，客户端首字母大写展示，空则不显示。
- `format`：`modrinth / curseforge / multimc / raw`。
- `has_terracotta`：房主是否已上报陶瓦码（大厅里可当「房主已开房，可直连进入」的提示）。
- `state`：`open / closed`；大厅只返回 `open`。
- `pack` 可为 `null`（房主还没传包）。

### RoomDetail（GET 详情 / join 返回的 room 字段）

```json
{
  "room": { …RoomSummary… },
  "members": [
    { "user_id": "u_100", "display_name": "阿星", "avatar_url": null,
      "role": "owner", "online": true },
    { "user_id": "u_200", "display_name": "小云", "avatar_url": null,
      "role": "member", "online": true }
  ],
  "joined": true,
  "is_owner": false
}
```

- `role`：`owner / member`。`online`：房间内在线（断线即移出或标记离线，见 §6）。
- `joined`：当前 token 的用户是否在房间内；`is_owner`：是否本房房主。

### RoomMessage

```json
{
  "id": 90001,
  "room_code": "318274",
  "from_user_id": "u_200",
  "display_name": "小云",
  "avatar_url": null,
  "content": "包我在下了",
  "created_at": "2026-10-27T12:03:00Z"
}
```

`id` 全房间内递增（雪花或自增均可），前端用 `before_id` 翻页。
`content` 为纯文本；附件复用好友聊天的 `[文件]` JSON 前缀协议，**服务端透传不解析**。

## 2. 大厅

### GET /v1/rooms

所有 `state=open` 的房间，按 `created_at` 倒序。建议上限 100 条 + 简单分页（前端首版不翻页）。

响应 200：`{ "rooms": [RoomSummary, …] }`

### GET /v1/rooms/mine

「我的房间」历史：我创建过或加入过、且 7 天内活跃过的房间（含已关闭的，便于重连展示），
按最后活跃倒序，上限 20 条。

响应 200：`{ "rooms": [RoomSummary, …] }`

## 3. 建房

### POST /v1/rooms

```json
{
  "name": "阿星 的房间",
  "max_players": 8,
  "password": "1234",
  "pack": { "file_id": "f_…", "size": 52428800, "sha256": null,
            "format": "modrinth", "mc_version": "1.20.1", "loader": "fabric", "name": "轻量生存包" }
}
```

- `name` ≤ 32 字；`max_players` 2–20；`password` 可空（空 = 无密码）；
  `pack` 可空（先占坑后上传的流程），`password`/`pack` 不得下发回显。
- 服务端校验 `pack.file_id` 存在且属于当前用户。

响应 200：`RoomSummary`（含生成的 `code`，`has_password` 按是否有密码返回）。

创建者自动成为第一个成员（role=owner），无需再调 join。

错误：400（字段非法）、401、413（pack.size 超限）、429（单人建房频率限制，建议 1 间/分钟，同时最多 1 间）。

## 4. 加入 / 退出

### GET /v1/rooms/{code}

搜索与邀请卡片共用。**未加入时 `members` 只给人数与房主一项**（或直接给全量也可，
首版从简：全量返回，但绝不含密码与陶瓦码）。

响应 200：`RoomDetail`（`joined=false` 时 `pack` 可保留——玩家要展示包信息）。
错误：404 `room_not_found`。

### POST /v1/rooms/{code}/join

```json
{ "password": "1234" }
```

- 有密码的房间：密码错误 → **403 + `{"error": "bad_password"}`**（不提示“密码错误”几次之类，前端统一文案）。
- 房间满员 → **409 + `{"error": "room_full"}`**。
- 已在房内：幂等，直接返回成功结果。
- 房间 `state=closed` → 410 + `{"error": "room_closed"}`。

响应 200（**验密通过才生成**）：

```json
{
  "room": { …RoomDetail… },
  "terracotta_code": "U/XXXX-XXXX-XXXX-XXXX"
}
```

`terracotta_code`：房主已上报则给，未上报给 `null`（前端提示「房主尚未开房」，
等 SSE `terracotta_ready` 事件）。

### POST /v1/rooms/{code}/leave

无请求体。退出即从成员表移除；房主调用等同解散（或返回 400 让房主走 DELETE，二选一，建议后者）。
响应 200：`{}`。幂等（不在房内也返回 200）。

### POST /v1/rooms/{code}/kick

```json
{ "user_id": "u_200" }
```

仅房主可调；不能踢自己。被踢者立即收到 SSE `kicked` 并断开。
响应 200：`{}`。错误：403（非房主）、404（目标不在房内）。

### DELETE /v1/rooms/{code}

解散。仅房主可调。全体成员（含房主）收到 SSE `room_closed`，房间与聊天数据
建议保留 24h（供 mine 历史展示）后清理。响应 200：`{}`。

## 5. 陶瓦码上报与聊天

### PUT /v1/rooms/{code}/terracotta

```json
{ "terracotta_code": "U/XXXX-XXXX-XXXX-XXXX" }
```

仅房主可调，可重复调（房主重开局后陶瓦码会变）。成功后向全体成员广播
SSE `terracotta_ready`。响应 200：`{}`。

### GET /v1/rooms/{code}/messages?before_id=90001&limit=50

- `before_id` 缺省 = 从最新开始；返回按 `id` **升序**（老→新）。
- `limit` 缺省 50，最大 100。只校验“在房内”，历史对房内成员全量可见。

响应 200：`{ "messages": [RoomMessage, …] }`

### POST /v1/rooms/{code}/messages

```json
{ "content": "包我在下了" }
```

`content` ≤ 2000 字符。服务端补 `id` / `from_user_id` / `display_name` / `created_at`，
并向全体成员广播 SSE `message`。响应 200：`RoomMessage`。

## 6. 实时事件（SSE）

### GET /v1/rooms/{code}/events

`text/event-stream`。连接要求调用者在房内；不在房内返回 403。

事件格式与 friends 一致：`event: <类型>` + `data: <JSON>`，事件名与负载：

| event | data | 说明 |
|---|---|---|
| `member_joined` | `{"member": RoomMember}` | 有新成员加入 |
| `member_left` | `{"user_id": "u_200"}` | 成员主动退出 |
| `kicked` | `{"user_id": "u_200", "by": "u_100"}` | 只发给被踢者 |
| `message` | `RoomMessage` | 新聊天消息 |
| `terracotta_ready` | `{"terracotta_code": "U/…"}` | 房主开好房，可直连了 |
| `room_closed` | `{}` | 房间解散/超时关闭，客户端断连接并退出房间 UI |

**连接保活**：30s 一次 `: ping` 注释行；断线由客户端指数退避重连（3s→60s），
重连后从 `GET messages` 拉缺口，事件不要求补发。

## 7. 心跳与超时回收（房间自动关闭）

- 客户端每 15s 调一次 `POST /v1/rooms/{code}/heartbeat`（无请求体，响应 `{}`）——
  **房主和成员都调**，服务端记 `last_seen`。
- 房主 `last_seen` 超过 60s：服务端自动 `state=closed`、广播 `room_closed`、释放联机码。
- 成员超时：从成员表移除（广播 `member_left`），不计入 `members` 数。
- 首版若嫌心跳麻烦，可先只做「房主 SSE 断开 + 60s 未重连」判定关闭；heartbeat 端点保留。

## 8. 错误码约定（客户端判定依据）

HTTP 状态 + `{"error": "<code>"}`，客户端的判定逻辑如下，服务端照此返回即可：

| 场景 | HTTP | error code | 客户端行为 |
|---|---|---|---|
| 密码错误 | 403 | `bad_password` | 提示「密码不正确」 |
| 房间不存在 | 404 | `room_not_found` | 提示「房间不存在或已关闭」 |
| 房间已关闭 | 410 | `room_closed` | 同上 + 退出房间 UI |
| 满员 | 409 | `room_full` | 提示「房间人数已满」 |
| 未在房内 | 403 | `not_in_room` | SSE/聊天/踢人接口返回 |
| 非房主 | 403 | `not_owner` | 踢人/解散/上报陶瓦码接口返回 |
| 接口未部署 | 404 | `not_found`（网关默认文案「未找到接口」） | 前端自动切演示数据，不算错误 |

`error` 字段缺失时前端兜底展示 `message` 字段或 HTTP 状态，因此错误响应体
建议统一为 `{"error": "...", "message": "人类可读文案"}`。

## 9. 前端已就绪的部分（后端无需关心）

- 6 位码输入规范化（全角转半角、去空格连字符）在前端做，服务端只收纯 6 位数字。
- 密码弹窗、聊天 UI、成员右键菜单、大厅卡片、Mock 降级全部完成。
- 整合包下载安装（`GET /v1/files/{id}` → 解包 → 自动装游戏与加载器）前端已有，
  只要求 `pack.file_id` 能走通文件下载。
- 附件消息：房间聊天复用好友的 `[文件]` content 前缀协议，服务端当纯文本透传。
