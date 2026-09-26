# Nova 云端模型中转：服务端实现要求（2026-09-21）

- 状态：待实现（客户端已就绪，缺口只在服务端）
- 客户端对齐实现：[NovaGatewayClient.cs](../../dotnet/src/Starcloud.Core/Services/NovaGatewayClient.cs)、
  [NovaService.cs](../../dotnet/src/Starcloud.Core/Services/NovaService.cs)、
  [StarcloudIdService.cs](../../dotnet/src/Starcloud.Core/Services/StarcloudIdService.cs)
- 相关文档：[Starcloud ID 协议规范](2026-09-09-starcloud-id-protocol.md)
- 上游依赖：由服务端持有的模型供应商合约（**客户端不持有任何模型凭据**）

本文是**实现说明书**：读完应当可以直接写出服务端，并且不需要再回来问客户端行为。
所有「客户端假定」都以代码位置标注，改这些约定等于改协议。

---

## 0. 一句话说明

用户在启动器里用 Starcloud ID 登录，服务端给他一把 API Key；
启动器拿这把 Key 调 `https://api.starcloudfur.cn/v1/ai/*` 问 Nova 的问题，
网关转发给上游模型，**把 token 用量记在账号上**（每账号每周 100 万 token），
并在**每一个响应**里带回剩余额度，客户端据此显示进度条、讲清降级原因。

```
Nova 客户端 ──Bearer sk-<用户的Key>──> 网关 /v1/ai/*  ──上游合约──> 模型
                    │
                    └─ 记账：账号 × 自然周 × token 数  ← 唯一的额度权威
```

## 1. 目标 / 非目标

目标：

1. 启动器开箱可用：不填任何模型 Key，登录后 Nova 就能回答并调用工具。
2. 额度可见可控：每账号每周 100 万 token，用量与重置时间在客户端可见。
3. 与既有账号体系一体：复用 `/v1/auth/login`、`/v1/auth/apikeys`，Key 绑定账号、可吊销。
4. 失败可解释：额度用尽 / 未授权 / 上游故障要能被客户端区分，而不是一律 500。

非目标：

- 不在客户端做 token 计数（多端不一致，也挡不住刷；客户端只显示服务端给的数）。
- 不下发模型名、供应商、上游 Key 给客户端（客户端只发逻辑模型名 `nova`）。
- 不做对话内容审核之外的业务（Nova 的能力实现全在客户端本地）。

## 2. 端点总览

| 方法 | 路径 | 鉴权 | 用途 |
| --- | --- | --- | --- |
| POST | `/v1/auth/register` | 无 | 注册（已实现，见 ID 协议文档） |
| POST | `/v1/auth/login` | 无 | 登录 → 会话 token |
| POST | `/v1/auth/apikeys` | Bearer `<token>` | 创建 / 取回该账号的 API Key（幂等） |
| **GET** | **`/v1/ai/quota`** | Bearer `<api_key>` | 查本周额度（会话、进度条） |
| **POST** | **`/v1/ai/chat/completions`** | Bearer `<api_key>` | 对话（OpenAI 兼容，支持 `stream` + `tools`） |

公共约定：HTTPS 强制；UTF-8；`Content-Type: application/json`；字段 snake_case；
客户端会带 `X-Starcloud-Client: launcher`（可用于区分流量来源，服务端可忽略）。

## 3. 鉴权

- `Authorization: Bearer <api_key>`，`api_key` 是 `/v1/auth/apikeys` 下发的 `sk-` 前缀字符串。
- **额度记在账号（`user_id`）上，不记在 Key 上**：轮换 Key 不改动已用量。
- Key 失效 / 被吊销 / 属于其它账号 → `401`；Key 有效但账号被禁用 → `403`。
- 请求体或头部不要出现上游供应商的任何凭据。

## 4. `GET /v1/ai/quota`

响应 `200`：

| 字段 | 类型 | 必填 | 说明 |
| --- | --- | --- | --- |
| `limit` | int | 是 | 本周额度上限（token），当前策略固定 `1000000` |
| `used` | int | 是 | 本周已用（token），含所有已计费请求 |
| `remaining` | int | 是 | `max(0, limit - used)` |
| `reset_at` | string | 是 | 下次重置时间，ISO8601 带时区（如 `2026-09-28T00:00:00+08:00`） |
| `plan` | string | 否 | 套餐标识（如 `weekly_free`），客户端目前只做展示预留 |

```json
{
  "limit": 1000000,
  "used": 250000,
  "remaining": 750000,
  "reset_at": "2026-09-28T00:00:00+08:00",
  "plan": "weekly_free"
}
```

实现要求：

1. **必须便宜**：客户端每次进设置页 / 打开 Nova 面板都可能调一次，服务端要能读缓存（内存 + 落库），不要每次聚合全量明细。
2. `limit` 为 0 或缺字段会被客户端判为「没有额度信息」，UI 退化成「未知」——不要用 0 表达「未开通」，用 `403`。
3. 未鉴权返回 `401`；此时客户端**不报错**，只是不显示数值（拉取失败按「未知」处理，`NovaGatewayClient.FetchQuotaAsync` 返回 null）。

## 5. `POST /v1/ai/chat/completions`

### 5.1 请求体（客户端唯一会发的形状）

```json
{
  "model": "nova",
  "messages": [
    { "role": "system", "content": "<Nova 人格提示词>" },
    { "role": "user", "content": "帮我看看为什么崩了" },
    { "role": "assistant", "content": "……" },
    { "role": "tool", "tool_call_id": "call_1", "content": "工具结果" }
  ],
  "tools": [ { "type": "function", "function": { "name": "…", "parameters": { } } } ],
  "max_tokens": 1024,
  "stream": true
}
```

要点：

1. `model` 永远是逻辑名 `nova`（常量 `NovaGatewayClient.DefaultModel`）。**上游真实模型由网关选**，客户端不需要也不允许知道。
2. `messages` 直接透传给上游；**不要替换 system 提示词**（Nova 的人格与「动手前先出确认清单」的约束写在那里，覆盖它等于改产品行为）。
3. `tools` 可选，出现时表示这一轮允许工具调用（最多 6 轮往返，客户端自己控轮次）。
4. `max_tokens` 可选；客户端未指定时按服务端的默认上限（建议 2048，够 Nova 的短回答，也护住额度）。
5. 请求体积上限建议 256 KB（客户端已对崩溃日志做裁剪，正常远小于此）。

### 5.2 非流式响应（`stream` 缺省或 false）

标准 OpenAI 形状，客户端只读 `choices[0].message.content`：

```json
{ "id": "chatcmpl-1", "object": "chat.completion", "model": "nova",
  "choices": [ { "index": 0, "message": { "role": "assistant", "content": "…" }, "finish_reason": "stop" } ],
  "usage": { "prompt_tokens": 120, "completion_tokens": 300, "total_tokens": 420 } }
```

### 5.3 流式响应（`stream: true`）

**必须满足**（客户端的 SSE 解析器 `OpenAiSseClient.ParseSseResponseAsync` 逐行读，任一不满足就表现为「Nova 不出字」）：

1. `Content-Type: text/event-stream`；如果返回 `application/json`，客户端会退化成「一次性读完整 JSON」，等于没有流式。
2. 每个事件一行 `data: ` + 单行 JSON，事件之间空行分隔；UTF-8；**不要**换行折行 JSON。
3. 文本增量放在 `choices[0].delta.content`（字符串）。
4. 结束时给一个 `finish_reason` 事件，再给 `data: [DONE]`：

```
data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"content":"崩"}}]}

data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{"content":"溃原因是…"}}]}

data: {"id":"chatcmpl-1","choices":[{"index":0,"delta":{},"finish_reason":"stop"}]}

data: [DONE]
```

5. **必须即时 flush**：每个 chunk 生成即写出（反向代理要关缓冲，nginx 用 `proxy_buffering off;` + `X-Accel-Buffering: no`）。攒完一次性返回等于毁掉流式体验。
6. 输出里出现空行、注释行、其它 `event:` 字段无害（客户端只认 `data:` 前缀行）。

### 5.4 工具调用（`tools` + `tool_calls`）

网关必须原样透传上游的工具调用增量，形状：

```
data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_1","type":"function","function":{"name":"list_mods","arguments":"{\"ver"}}]}}]}
data: {"choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"sion\":\"1.20.1\"}"}}]}}]}
data: {"choices":[{"index":0,"delta":{},"finish_reason":"tool_calls"}]}
data: [DONE]
```

客户端按 `index` 累积 `arguments` 分片（`ToolCallAccumulator`），所以：

- `index` 必须从 0 递增且同一次调用内稳定；
- `id` 与 `name` 可以在任一分片里出现（客户端会记住首次出现的值）；
- 不要把 `arguments` 合成一个 JSON 对象下发（必须是**字符串分片**）。

## 6. 配额规则

### 6.1 计量口径

- 单位：**token**，按上游 `usage.total_tokens`（`prompt_tokens + completion_tokens`）累计。
- 流式请求必须向上游要 usage（OpenAI 兼容：`stream_options: {"include_usage": true}`；或在网关侧用同一 tokenizer 计数），**不要**用字符数/估算值代替——估算会与用户看到的数字打架。
- 计费范围：请求消息 + 响应内容 + 工具调用的参数与结果（都算上游 token）。

### 6.2 周界与重置

- 每账号 **1,000,000 token / 自然周**（周一 00:00 Asia/Shanghai 起算，周界不可按首次使用时间滚动）。
- `reset_at` 返回 `下周一 00:00 +08:00`；`used` 到点自动归零（读时按周界过滤，不必写定时任务）。
- 额度用尽不等于封禁：下一个自然周自动恢复，不需要用户做任何操作。

### 6.3 边界与竞态

- 请求开始时先算「剩余」，`remaining <= 0` → 直接 `402`，**不打上游**（省钱也省延迟）。
- 一次请求可能跨过额度线：允许最后一个请求把额度略微用超（建议余量 ≤ 单次 `max_tokens`），不要中途掐断已经出字的流。
- 同一账号并发：建议同一 Key 最多 2 个在途请求，超出排队 2s 后返回 `429`。
- 幂等：同一 `Idempotency-Key` 请求头（客户端目前不发）应复用结果且不重复计费；至少要保证「重试同一请求不会双倍扣费」在流式断线场景下不发生。

### 6.4 不计费的请求

以下情况不扣额度：`401/403`、`402/429`（被拒）、上游 5xx（网关没产出 token）、以及 `max_tokens` 之前就报错的请求。

## 7. 响应头：每次调用都带回额度

```
X-Quota-Limit: 1000000
X-Quota-Used: 250420
X-Quota-Remaining: 749580
X-Quota-Reset: 2026-09-28T00:00:00+08:00
```

- **成功与失败都要带**（`402/429` 尤其要带，客户端据此把进度条改成「已用尽」）。
- 客户端解析逻辑在 `NovaGatewayClient.ParseQuotaHeaders`：三个数字头至少给两个，客户端能互相反推；日期头 ISO8601。
- 带了这些头，客户端就不需要额外调 `/v1/ai/quota`（省一次往返）。
- 流式响应同样要带头（先发响应头再发 body，客户端在开始读流前就解析）。

## 8. 错误码与错误体

| 状态 | 何时 | 客户端行为 |
| --- | --- | --- |
| `400` | 请求体不合法（缺 messages / 超长） | 显示「云端模型暂时不可用（HTTP 400）」+ 摘要 |
| `401` | Key 无效 / 已吊销 | 提示「重新登录 Starcloud ID」 |
| `403` | 账号被禁用 / 未开通中转 | 提示查看额度与登录状态 |
| `402` | 本周额度已用尽 | 提示「本周云端额度已用尽（已用 X / 共 Y）」+ 重置时间 |
| `429` | 限流（额度未用尽） | 提示「请求过于频繁，请稍后再试」 |
| `5xx` | 上游故障 | 提示「云端模型暂时不可用（HTTP 5xx）」+ 摘要 |

错误体（建议形状，客户端会把整个 body 截断 160 字附在提示后，便于诊断）：

```json
{ "error": { "code": "quota_exceeded", "message": "weekly quota exhausted", "reset_at": "2026-09-28T00:00:00+08:00" } }
```

建议 `code` 取值：`invalid_request` / `unauthorized` / `forbidden` / `quota_exceeded` / `rate_limited` / `upstream_error` / `upstream_timeout`。

客户端约定（`NovaService` 的降级链）：**有状态码的错误会讲清楚原因，网络层失败才静默降级到本地能力**。
所以服务端不要用 `200 + {"error":...}` 表达失败——那会被当成正文念念给用户。

## 9. 可靠性要求

1. 上游超时建议：非流式 60s、流式首 token 30s / 总时长 180s（客户端 HttpClient 超时 180s，agent 轮次 120s）。
2. 上游 5xx / 连接失败：网关内部可重试 1 次（只在**尚未向客户端写出任何字节**时重试）。
3. 已经出字之后上游断了：直接断开连接（不要补 `[DONE]`）。客户端会把已收到的部分留在气泡里，并把这次当「半截回答」，用户可以再问一次。
4. 断线 / 客户端取消：以已产生的 token 计费，并在 `usage` 里如实反映。
5. 请求日志必须带：`user_id`、`api_key_id`（不是 Key 明文）、`request_id`、`model`、`usage`、`status`、`latency_ms`、`upstream_status`。

## 10. 安全与合规

1. 上游供应商凭据只存在网关（KMS / 环境变量），**永不返回给客户端**；客户端源码与二进制里不出现任何模型 Key。
2. API Key 落库只存哈希（或至少加密存储），明文只在创建时返回一次；支持吊销与轮换，轮换不影响额度。
3. 限流：按 Key + 按账号 + 按 IP 三层；`/v1/auth/*` 另有更严的爆破防护（见 ID 协议文档）。
4. CORS：只允许 `https://starcloudfur.cn` 及启动器 WebView 来源；启动器原生请求不带 Origin，要允许无 Origin 的请求。
5. 内容策略：转发上游的拒答（内容安全）时，如实返回 `400` 与 `code: "content_filter"`，不要静默改成空回答。
6. 日志里的用户内容默认不落全文（隐私）：只留长度、token 数、hash；排查需要原文时另设开关并写审计。
7. 数据保留：`usage` 明细建议保留 13 周（够对账与用户申诉），到期自动清理。

## 11. 可观测性

- 指标：请求数 / 成功率 / P95 延迟（按状态码分桶）、每周额度的消耗曲线、上游 token 成本、`quota_exceeded` 次数、并发峰值。
- 告警：上游错误率 > 5% 持续 5 分钟；单账号 1 小时消耗 > 20 万 token（疑似刷或异常客户端）。
- 对账：网关侧 `usage` 汇总 与 上游账单 每日对一次，误差 > 1% 要能定位到具体 `request_id`。

## 12. 客户端已经假定的事（不要违反）

| 客户端假设 | 代码位置 | 违反的后果 |
| --- | --- | --- |
| 逻辑模型名是 `nova` | `NovaGatewayClient.DefaultModel` | 客户端要发版才能改 |
| 基址是 `StarcloudIdService.CloudBaseUrl + "/v1/ai"` | `NovaGatewayClient.CloudBaseUrl` | 全部请求 404 |
| 额度头名 `X-Quota-Limit/Used/Remaining/Reset` | `ParseQuotaHeaders` | 额度条永远「未知」 |
| 流式是 `text/event-stream` + `data:` 行 + `[DONE]` | `OpenAiSseClient` | Nova 不出字或一次性蹦出全文 |
| `tools` 用 OpenAI 形状、`arguments` 是字符串分片 | `NovaAgentLoop` / `ToolCallAccumulator` | Nova 无法调用工具（退化成纯问答） |
| 只有 Key 来自**云端通道**才走中转（`channel == "cloud"`） | `NovaGatewayClient.CanUse` | 测试通道的假 Key 会打到网关拿 401 |
| 客户端不本地计数、只信服务端 | `NovaService` 注释 | 两端显示的用量不一致 |
| 额度用尽必须能降级（不能卡死） | `NovaService.ChatAsync` / `ChatStreamAsync` | 违反产品红线 |

## 13. 验收清单（可直接跑的联调）

前置：拿到一把云端通道的 Key（`POST /v1/auth/login` → `POST /v1/auth/apikeys`）。

```bash
KEY=sk-xxxx

# 1) 额度查询：limit=1000000、used/remaining 自洽、reset_at 是下周一
curl -sS -D- -H "Authorization: Bearer $KEY" https://api.starcloudfur.cn/v1/ai/quota

# 2) 非流式对话：choices[0].message.content 有内容，且响应头带 X-Quota-*
curl -sS -D- -X POST https://api.starcloudfur.cn/v1/ai/chat/completions \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"model":"nova","messages":[{"role":"user","content":"用一句话说明你是 Nova"}]}'

# 3) 流式：Content-Type=text/event-stream，逐块到达，最后有 [DONE]
curl -sS -N -X POST https://api.starcloudfur.cn/v1/ai/chat/completions \
  -H "Authorization: Bearer $KEY" -H "Content-Type: application/json" \
  -d '{"model":"nova","messages":[{"role":"user","content":"数到五"}],"stream":true}'

# 4) 用量真的涨了：再查一次 quota，used 增加且剩余量同步减少
# 5) 未鉴权：不带 Authorization → 401
# 6) 错误 Key：Authorization: Bearer sk-invalid → 401（客户端会提示重新登录）
```

客户端侧验收（人工）：

1. 云端通道登录 → Nova 页「云端模型」卡片出现进度条与剩余量；顶栏 Nova 面板标头出现「云端额度：剩余 … 」。
2. 随便问一句 → 回答逐字出现（不是一次性蹦出），且卡片数值随之减少（响应头驱动，无需手动刷新）。
3. 关掉「使用云端模型」→ Nova 只用自填模型（没配置时走本地能力），卡片仍显示额度。
4. 断网 / 网关 5xx → Nova 仍能用本地能力回答，不卡死、不转圈。
5. 把额度打到 0（测试账号或临时调小 `limit`）→ 提示「本周云端额度已用尽（已用 X / 共 Y）…」+ 重置时间；进度条到 100%，芯片变红。

## 14. 分期实现建议

| 阶段 | 内容 | 完成标志 |
| --- | --- | --- |
| M1 | `/v1/ai/quota` + 非流式 `/v1/ai/chat/completions` + 额度头 + 计费 | 验收 1/2 通过，额度条能动 |
| M2 | 流式（SSE）+ 工具调用透传 | 验收 3 通过，Nova 能逐字出字并调用只读工具 |
| M3 | 限流 / 幂等 / 对账 / 告警 / Key 吊销 | 验收 4~6 通过，能扛并发 |

## 15. 未实现前的行为（现状）

服务端缺口不影响启动器可用性，客户端已按「三级降级」兜住：

1. 用户自填模型 Key（配置了就优先用）；
2. Starcloud 云端中转（网关没上线时，`FetchQuotaAsync` 与对话请求失败 → 静默降级，UI 显示「未知」）；
3. 本地能力引擎（崩溃排查 / 游戏体检 / 模组冲突 / 资源查找 / 录屏，永不失败）。

也就是说：网关一上线，客户端**不需要发版**即可生效（额度卡片自动从「未知」变成真实数值）。
