# Nova 画面问答接口（中转需支持多模态）

面向：`https://starcloudfur.cn` 网关（Nova 云端中转）。
现状：启动器已经把截图按「OpenAI 多模态消息」的标准格式发过来了，但网关没定义 / 没透传
`content` 数组里的 `image_url`，于是上游 `deepseek-flash` 收不到图片，返回空正文。
本文档定义网关需要实现的字段与校验，客户端**无需再改**。

## 1. 端点

```
POST https://starcloudfur.cn/v1/chat/completions
Authorization: Bearer <sk-...>
X-Starcloud-Client: launcher
Content-Type: application/json
```

与现有文本对话同一个端点：是否带图由 `messages[].content` 的形状决定，不需要新路由。

## 2. 请求：`content` 两种形状

### 2.1 纯文本（现有行为，必须继续支持）

```json
{
  "model": "nova",
  "messages": [
    { "role": "system", "content": "你是 Nova…" },
    { "role": "user", "content": "帮我找一个优化整合包" }
  ],
  "max_tokens": 800
}
```

### 2.2 多模态（本次新增）

`content` 从字符串变成**数组**，元素二选一：

```json
{
  "model": "nova",
  "messages": [
    { "role": "system", "content": "你是 Starcloud 启动器的助手 Nova，正在看玩家 Minecraft 游戏的实时画面截图…" },
    {
      "role": "user",
      "content": [
        { "type": "text", "text": "结合画面，给我一条最实用的建议" },
        { "type": "image_url", "image_url": { "url": "data:image/jpeg;base64,/9j/4AAQSkZJRg…" } }
      ]
    }
  ],
  "max_tokens": 1400
}
```

约定：

| 字段 | 规则 |
| --- | --- |
| `type` | `text` 或 `image_url`，其它类型忽略（不报错，丢掉即可） |
| `text` | ≤ 4000 字符 |
| `image_url.url` | **data URL**（`data:image/jpeg;base64,...`）或 **https URL**；启动器目前只发 data URL |
| 图片格式 | `image/jpeg`（启动器固定 JPEG） |
| 单图大小 | base64 解码后 ≤ **2 MB**（启动器已压到 1080P、通常 150–400 KB） |
| 图片数量 | 建议 ≤ 4 张 / 请求（启动器只发 1 张） |
| `max_tokens` | 启动器给 1400；**不要**把 `max_tokens` 截到更小 |

## 3. 网关要做的事

1. **原样透传** `messages` 给上游（`deepseek-flash` 的 OpenAI 兼容接口本身支持
   `content: [{type:"text"},{type:"image_url"}]`）。网关不需要解析图片内容，也不要 base64 解码。
2. **不要**把 `content` 数组拍平成字符串——拍平就等于丢图。
3. 上游若对 data URL 有大小限制，报 400（见下）；不要静默截断。
4. 保留现有的 `model: "nova"` → 实际模型映射、额度记账（`X-Starcloud-Quota-*` 之类）、限流。

## 4. 响应

与文本对话一致，客户端只读这一条路径：

```json
{
  "choices": [
    { "message": { "role": "assistant", "content": "画面里天在下雨，建议先收作物……" } }
  ]
}
```

**注意 reasoning 模型**：`deepseek-flash` 会先输出 `reasoning_content` 再给 `content`。
若 `max_tokens` 给得太小，`content` 会是空字符串、`finish_reason: "length"`——客户端会当成
「没看成画面」并退化为文字建议。所以网关照传客户端的 `max_tokens`（默认 1400）即可，
**不要**在网关侧再压小。

## 5. 错误码（供客户端区分）

| 场景 | 状态 | body |
| --- | --- | --- |
| 多模态消息不被支持（网关暂未上线本能力） | 400 | `{"error":{"code":"unsupported_content","message":"当前端点不支持图片内容"}}` |
| 图片超限 | 413 | `{"error":{"code":"image_too_large","message":"单图需小于 2 MB"}}` |
| 图片格式不支持 | 400 | `{"error":{"code":"unsupported_image","message":"仅支持 jpeg/png/webp"}}` |
| 上游拒收 | 502 | `{"error":{"code":"upstream_error","message":"…"}}` |

**不要**用「200 + 空 content」表示不支持——客户端无法区分「模型没话说」和「图被丢了」，
会多花一次额度退化成文字建议。

## 6. 客户端当前行为（联调用）

- 截图：`GameCaptureService`，长边 1920（1080P）、JPEG 质量 0.72 起、超过 1.2 MB 逐档降到 0.42，
  典型 150–400 KB（base64 后 ×1.37）。
- 发送：`NovaService.ChatWithImageAsync` → 多模态 `content` 数组（本文 §2.2）。
- 失败兜底：网关返回 4xx/5xx 或空 `content` → 再发一次**纯文本**请求（带「画面来源」文字线索），
  回答会标注「云端接口暂未启用画面识别」，不会假装看懂了画面。
- 日志：`[nova]`（问答链路）、`[novacap]`（截图尺寸/体积/质量），排查时先看这两类。

## 7. 验收清单（网关侧自测）

1. 纯文本请求仍 200，行为不变。
2. 多模态请求 200，且 `content` 非空（不是 `finish_reason: length`）。
3. 上游日志/抓包里能看到图片被转发（不透传时上游收到的就是纯文本）。
4. 超大图（>2 MB）→ 413 + `image_too_large`，不是 200。
5. 不带 `image_url` 的数组（只有 text）→ 与纯文本等价。
