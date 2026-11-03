# 启动器云服务接口（活跃上报 / 一键反馈 / 远程配置 / S3 热门缓存）

> 面向服务端。四个轻量接口 + 一个定时任务，全部挂在官网主站 `https://starcloudfur.cn/v1` 下。
> 客户端已按本文实现完毕（`LauncherCloudService` + `UpdateStartup` + `DownloadSourceRegistry`），
> 所有接口客户端都做了"失败即静默"，服务端没上线前用户无感。

## 1. 活跃用户上报

```
POST /v1/telemetry/active
Authorization: Bearer <sk-...>   （登录后才会上报；未登录客户端跳过）
Content-Type: application/json

{
  "launcher_version": "0.4.0",
  "os": "Microsoft Windows NT 10.0.26220.0",
  "os_bits": 64
}
```

- 识别码 = token 里的 StarcloudID 用户（服务端从 token 解出 user_id，客户端不重复传）
- **客户端每次启动只调一次**（启动后 6 秒），失败静默
- 服务端建议：`user_id + date` 唯一约束，插入或忽略 —— 这样天然得到 DAU；
  同一用户一天内多次启动不会被重复计数
- 响应 200 `{}` 即可，客户端不读响应体
- 建议同时记录首见日期（`first_seen`），便于算留存

## 2. 一键反馈

```
POST /v1/feedback
Authorization: Bearer <sk-...>   （未登录时客户端不调用，改走本地落盘）
Content-Type: application/json

{
  "content": "用户描述的问题",
  "diagnostics": "版本/系统/内存/游戏目录/下载源/最近 60 行日志 的文本包",
  "launcher_version": "0.4.0"
}
```

响应 200 `{}`。失败（4xx/5xx/超时）客户端把完整反馈落到本地
`%APPDATA%/starcloud-launcher/feedback-<时间戳>.txt` 提示用户手动转交。

服务端建议：入库 `feedback(id, user_id, content, diagnostics, version, created_at, diagnostic_code)`，
`diagnostics` 是纯文本，可以整块存 TEXT，排查时直接搜关键字（"下载失败"/"启动崩溃"）；
`diagnostic_code` 可空 —— 用户先双击标题上传过诊断的话自动附带（见 2.5）。

### 2.4 每日签到（领云端 token 额度）

```
GET  /v1/checkin            → 200 { "signed_today": false, "reward_tokens": 0, "streak": 0, "month_days": 0 }
POST /v1/checkin            → 200 { "signed_today": true, "reward_tokens": 5000, "streak": 3, "month_days": 5 }
Authorization: Bearer <sk-...>   （必须登录；识别码 = token 里的用户）
```

- 建议奖励：基础 5000 token/天，连续签到可递增（服务端自定策略）
- 重复 POST：返回幂等结果（`signed_today: true` + 当次已有奖励），不要报错
- `streak` = 连续签到天数；`month_days` = 本月累计
- 客户端两处入口：主窗口账户头像下的弹窗（未签才弹）+ Nova 页「每日签到」板块

### 2.5 诊断码上传（双击标题栏触发）

```
POST /v1/diagnostics
Authorization: Bearer <sk-...>   （可选：未登录也接受，user_id 记空）
Content-Type: application/json

{
  "launcher_version": "0.4.0",
  "os": "Microsoft Windows NT 10.0.26220.0",
  "diagnostics": "版本/系统/配置/最近 60 行日志 的文本包（3-8 KB）"
}
```

响应 200：

```json
{ "code": "K7F3M2QX" }
```

- **诊断码**：8 位大写字母数字，**必须去掉易混字符**（0/O、1/I/L），用户要手抄进飞书问卷
- 服务端入库 `diagnostics(code, user_id?, content, version, created_at)`，`code` 唯一索引
- 保留 30 天后可清理（用户端弹窗里已说明）
- 用户双击启动器左上角「Starcloud」标题触发上传；拿到码后弹窗展示，用户填进飞书问卷，
  开发者用码在后台查全文诊断
- 一键反馈（§2）若用户上传过诊断，请求体会带 `diagnostic_code` 字段，把反馈和诊断关联起来
- 未登录：接受上传（`user_id` 记空）——报错的人未必登录着

## 3. 启动器远程配置（云控）

```
GET /v1/launcher/config
Authorization: Bearer <sk-...>   （可选；未登录也发，方便统计配置命中率）
```

响应 200：

```json
{
  "silent_update": false,
  "s3_mirror": true,
  "multiplayer": true,
  "notice": "可空，一行公告"
}
```

| 字段 | 语义 | 客户端行为 |
| --- | --- | --- |
| `silent_update` | 静默更新总开关 | true 时：启动 6 秒后检查更新，发现新版**不弹窗**，后台下载完直接静默升级（进程退出→升级→自动重启）。用户关了"自动检查更新"则整条链路不跑 |
| `s3_mirror` | Starcloud S3 加速镜像参与竞速 | false 时用户端下载源池剔除 S3（服务端清理/故障时一键切走） |
| `multiplayer` | 联机功能总开关（房间/大厅/我的） | false 时好友页隐藏「联机」标签、房间页只显示"联机功能维护中"；**不**影响联机房间服务器（rooms.starcloudfur.cn）本身 |
| `notice` | 更新卡片上的一行公告 | 纯展示 |

- **拉取失败 / 404 / 超时：客户端用默认值（silent_update=false, s3_mirror=true）**，所以
  "要灰度静默更新"就是把这个接口上线并返回 true；"要全量停用静默更新"返回 false 即可
- 建议加 `Cache-Control: max-age=300`，客户端每次启动只拉一次

## 4. Starcloud S3 热门版本缓存（每日任务）

对象存储：rains3 桶 `starcloud`，公开读，前缀 **`mc/`**，**布局与 BMCLAPI 完全一致**
（客户端用与 BMCLAPI 相同的改写逻辑，只是根不同）：

| Mojang 官方路径 | S3 路径 |
| --- | --- |
| `piston-meta.mojang.com/X` | `mc/X` |
| `launchermeta.mojang.com/X` | `mc/X` |
| `piston-data.mojang.com/X` | `mc/X` |
| `launcher.mojang.com/X` | `mc/X` |
| `resources.download.minecraft.net/X` | `mc/assets/X` |
| `libraries.minecraft.net/X` | `mc/maven/X` |

例：`https://piston-meta.mojang.com/mc/game/version_manifest_v2.json`
→ `https://starcloud.cn-nb1.rains3.com/mc/mc/game/version_manifest_v2.json`

### 每日任务（服务端定时，建议凌晨跑）

1. 统计**昨天**客户端下载的版本 id 次数（数据源：下载审计日志或 `version_manifest` 的
   请求计数，按版本号聚合；客户端在 version manifest 请求上会打到自家源）
2. 取前 3 名，对每个版本：
   - 抓取官方 `version_manifest_v2.json` → 该版本 JSON → 递归收集 client jar、libraries、assets 索引
   - 全部按上表映射转存到 S3（`mc/` 前缀）
   - assets 数量大（数百到上千个小文件），按索引逐个转存即可，单日增量小
3. 缓存保留策略：只要进过前三就保留；连续 7 天跌出前三的版本可以清（省空间，客户端会自动回落官方源）

### 客户端行为（已实现）

- S3 是竞速池里的一个普通源（`starcloud`），健康度系统自动处理：
  缓存命中 → 快；未命中 404 → 连续失败自动降级到池尾，**不需要预知哪些文件在缓存里**
- 云控 `s3_mirror=false` 时整个源退出竞速池

## 5. 客户端联调清单

1. `GET /v1/launcher/config` 未上线时客户端用默认值，日志 `[cloud] 远程配置拉取失败`
2. `POST /v1/telemetry/active` 上线后，日志 `[cloud] 活跃上报：HTTP 200`，服务端能看到 DAU 记录
3. `POST /v1/feedback` 上线后，设置页「一键反馈」提交返回 200；未登录时客户端落盘
   `feedback-*.txt` 而不是发请求
4. S3 缓存：放入任意一个官方文件（按上表路径）后，客户端"自动"模式能在竞速池里命中它
   （日志 `[download]` 里源名显示 Starcloud 加速）
