# Starcloud ID 协议规范

- 状态：v1（与客户端实现 [StarcloudIdService.cs](../../dotnet/src/Starcloud.Core/Services/StarcloudIdService.cs) 对齐）
- 日期：2026-09-09
- 参考实现：`Starcloud.Core/Services/StarcloudIdService.cs`、`NovaService.CloudChatAsync`、`LauncherPaths.StarcloudIdFile`

---

## 1. 概述

Starcloud ID 是 Starcloud 启动器的统一账号体系。客户端用 ID + 密码登录换取**会话**；登录成功后，服务端为该用户**自动创建（或取回）一个云 AI 助手 API Key**。该 Key 用于 Nova 云端 AI 能力（LLM 降级链第 2 级），也预留为后续云服务（好友联机、云同步等）的鉴权凭据。

设计目标：

1. 一次登录，全端可用：会话含 API Key，客户端无需二次申请。
2. 通道可替换：测试通道（本地模拟）与云端通道（HTTP）共用同一会话模型，后端就绪后无缝切换。
3. 失败不卡死：Nova 降级链中云端不可达时自动回退（用户自建 Key → 云端 → 本地模板）。

## 2. 术语

| 术语 | 含义 |
|---|---|
| Starcloud ID | 用户账号名，全局唯一，大小写不敏感 |
| user_id | 服务端内部用户标识，`sc-` 前缀 |
| session | 登录会话，含 API Key |
| API Key | 云 AI 助手访问凭据，`sk-` 前缀，Bearer 方式使用 |
| 通道 channel | `test`（本地模拟）/ `cloud`（云端 HTTP） |

## 3. 账号规则

| 规则 | 约束 | 校验位置 |
|---|---|---|
| ID 非空 | 必填，去首尾空白后非空 | 客户端 + 服务端 |
| ID 唯一 | 大小写不敏感（比较前统一小写） | 服务端权威；测试通道进程内模拟 |
| 注册密码 | ≥ 6 位 | 客户端预校验 + 服务端 |
| 登录密码 | 云端由服务端校验；测试通道 ≥ 4 位（任意内容） | 服务端 |

## 4. 标识与 Key 派生

### 4.1 测试通道（确定性派生，无服务端）

```
user_id = "sc-" + SHA256_HEX(lower(id))[0..12]
api_key = "sk-test-" + SHA256_HEX("starcloud:" + user_id + ":" + id)[0..32]
```

- 同一 ID 多次登录得到**同一 Key**，模拟服务端幂等存储。
- 仅用于本地联调与 UI 演示，**不具安全性**（可离线推算），不得用于生产。

### 4.2 云端通道（服务端权威）

`user_id`、`api_key` 均由服务端生成并返回；API Key 创建**幂等**（同一用户重复登录返回同一 Key，或按服务端策略取回最近有效 Key）。客户端不对云端 Key 做任何派生假设。

## 5. 云端 HTTP 协议

### 5.1 公共约定

| 项 | 值 |
|---|---|
| Base URL | `https://ai.starcloud.example`（**占位**，定稿前由服务端下发/配置替换；常量 `StarcloudIdService.CloudBaseUrl`） |
| 协议 | HTTPS 强制 |
| 编码 | UTF-8，`Content-Type: application/json` |
| 鉴权 | 登录/注册免鉴权；其余接口 `Authorization: Bearer <token 或 api_key>` |
| 错误语义 | 4xx 客户端错误（客户端展示 `(int)StatusCode`）；5xx 服务端错误；2xx 视为成功 |
| 字段风格 | JSON 小写下划线（snake_case） |

### 5.2 注册 `POST /v1/auth/register`

请求：

```json
{ "id": "alice", "password": "******" }
```

响应：`2xx` 空体即成功；ID 已存在等业务失败用 4xx 表达。

### 5.3 登录 `POST /v1/auth/login`

请求：

```json
{ "id": "alice", "password": "******" }
```

响应 `2xx`：

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `token` | string | 是 | 会话令牌，用于下一步创建 API Key |
| `user_id` | string | 是 | `sc-` 前缀用户标识（缺失视为空串） |
| `display_name` | string | 否 | 展示名，缺省回退为 ID |

### 5.4 创建 API Key `POST /v1/auth/apikeys`

- 请求头：`Authorization: Bearer <login 返回的 token>`，无请求体。
- 响应 `2xx`：`{ "api_key": "sk-..." }`，缺失即失败。
- 幂等：同一用户重复调用返回同一（或仍有效的）Key。

### 5.5 API Key 使用（云 AI 示例）`POST /v1/ai/chat`

- 请求头：`Authorization: Bearer <api_key>`（注意：这里用 **API Key** 而非 login token）。
- 请求：`{ "message": "...", "history": [ { "role": "user"|"assistant", "content": "..." } ] }`
- 响应：`{ "text": "..." }`，缺 `text` 视为失败。

## 6. 会话对象与客户端存储

会话记录 `StarcloudSession`（JSON snake_case，写入时缩进格式化）：

| 字段 | 类型 | 说明 |
|---|---|---|
| `user_id` | string | 服务端用户标识 |
| `display_name` | string | 展示名 |
| `api_key` | string | 云 AI 凭据（Bearer） |
| `channel` | string | `test` / `cloud`，标记会话来源通道 |
| `login_at` | datetime | 登录时间（本地时间） |

客户端存储约定：

- 路径：`LauncherPaths.StarcloudIdFile` = `<数据目录>/starcloud-id.json`。
- 登录成功即覆写持久化；启动时读取恢复会话，**解析失败静默视为未登录**（不崩溃）。
- 登出：清空内存会话并删除该文件。
- 文件内含 `api_key` 明文，属本机用户态敏感数据（与第三方 LLM Key 同级，只存本机）。

## 7. 客户端行为与错误处理

- 客户端校验失败：抛 `InvalidOperationException`，中文文案（「请输入 Starcloud ID」「密码至少 6 位」等），UI 直接展示。
- 云端失败：`EnsureSuccessStatusCode` 后按状态码抛「注册失败（<code>（<状态码>）」/ HTTP 请求失败类文案；不重试、不降级登录（登录是显式用户动作）。
- Nova 使用侧（与登录解耦）：云端 `/v1/ai/chat` 失败时按降级链静默回退，不打扰用户。

## 8. 安全要求（服务端义务 / 演进方向）

1. 密码**绝不**明文存储：服务端加盐哈希（argon2id/bcrypt）；传输依赖 HTTPS。
2. `token` 有效期 ≤ 15 分钟（一次性，换取 API Key 后即弃）；过期返回 401。
3. API Key 支持吊销与轮换；泄露时用户可重置（重置后旧 Key 立即失效）。
4. 注册/登录限流（按 IP + 按 ID），防爆破。
5. `login_at` 建议改存 UTC（当前实现为本地时间，仅用于展示）。

## 9. 已知限制

| 限制 | 说明 |
|---|---|
| Base URL 占位 | `ai.starcloud.example` 为占位域名，云端通道实际不可用直至后端定稿部署 |
| 测试通道去重仅进程内 | 重启后 `TestRegisteredIds` 清空，可重复注册；仅为 UI 联调设计 |
| 会话无过期 | 客户端不主动过期会话，登出/删除文件即失效；云端 token 过期逻辑后端定 |
| 单会话 | 一台设备仅保存最后一个登录会话，无多账号切换 |
