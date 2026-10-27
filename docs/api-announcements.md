# 云端公告接口（后端需实现）

客户端已按本契约实现完毕（`AnnouncementService` + `AnnouncementDialog`）。后端按此实现即可上线；
**在接口就绪之前客户端会静默跳过**（404 / 网络失败不弹、不报错），不影响任何现有功能。

## 1. 拉取公告

```
GET https://starcloudfur.cn/v1/announcements
```

- **鉴权**：匿名（公告是公开信息，不带 Bearer 也能取；带了也不应报错）
- **方法**：GET
- **超时**：客户端连接 8 秒、整体 12 秒
- **请求头**：无特殊要求（客户端只带默认 UA）
- **建议限流**：按 IP 每分钟 30 次（客户端只在启动时请求一次）

### 响应（200）

```json
{
  "items": [
    {
      "id": "2026-10-24-maintenance",
      "title": "今晚维护通知",
      "body_md": "# 维护安排\n\n- 22:00 开始，预计 30 分钟\n- 期间下载与联机不可用\n\n> 完成后自动恢复，无需重登。",
      "published_at": "2026-10-24T10:00:00Z",
      "pinned": true
    }
  ]
}
```

客户端同时兼容三种外层形态（任选其一，**推荐 `items`**）：

```json
{ "items": [ ... ] }
[ ... ]
{ "data": [ ... ] }
```

### 字段

| 字段 | 类型 | 必填 | 说明 |
|---|---|---|---|
| `id` | string | 是 | 稳定唯一标识。**同一 id 只弹一次**（客户端记已读），改了内容要换新 id |
| `title` | string | 是 | 弹窗标题。建议 ≤ 60 字 |
| `body_md` | string | 是 | Markdown 正文。建议 ≤ 20 KB |
| `published_at` | string | 否 | ISO 8601（UTC）。仅用于后端排序/前台展示，客户端不解析 |
| `pinned` | bool | 否 | 置顶。多条未读时客户端优先弹置顶的那条 |

- 缺 `id` / `title` / `body_md` 任一项的条目会被客户端**丢弃**（不会半渲染）。
- 未来时间的公告由后端自己过滤（客户端不做时间判断）。

### 错误

| 状态 | 客户端行为 |
|---|---|
| 200 | 正常解析 |
| 空数组 / `items: []` | 不弹 |
| 4xx / 5xx | 静默忽略，不提示用户 |
| 超时 / 断网 | 静默忽略 |

客户端**不做重试、不轮询**：每次启动后 3 秒请求一次，失败就等下次启动。

## 2. Markdown 渲染范围（客户端能力边界）

客户端只把正文当**纯 Markdown 文本**渲染成排版，**不解析 HTML、不加载任何远程资源、不执行脚本**：

| 支持 | 说明 |
|---|---|
| 标题 `#`~`######` | 渲染为不同字号 |
| 段落、空行 | 正常分段 |
| 列表 `-` / `*` / `1.` | 渲染为带符号的行 |
| 引用 `>` | 渲染为引用行 |
| 代码块 ``` | 等宽字体块 |
| 行内 `**粗**` `*斜*` `~~删~~` `代码` | 内联样式 |
| 链接 `[文字](https://…)` | 仅 `http`/`https` 可点，点击交给系统浏览器 |

| 不支持 | 表现 |
|---|---|
| 图片 `![](…)` | 不加载，按文本显示 —— **请不要用图片** |
| 表格 | 按纯文本显示 |
| 内嵌 HTML / `<script>` / `iframe` | 当作普通文本显示，**永不执行** |
| 相对链接、`file://`、`javascript:` | 不可点击 |

## 3. 发放公告（后端内部）

建议加一个仅供管理员的写入通道（与现有 admin 路由同一套鉴权），字段校验同上：

```
POST /v1/admin/announcements
{ "id": "...", "title": "...", "body_md": "...", "published_at": "...", "pinned": true }
```

表结构建议：

```sql
CREATE TABLE IF NOT EXISTS announcements (
  id           TEXT PRIMARY KEY,
  title        TEXT NOT NULL,
  body_md      TEXT NOT NULL,
  published_at TEXT NOT NULL,
  pinned       INTEGER NOT NULL DEFAULT 0,
  created_at   TEXT NOT NULL,
  revoked      INTEGER NOT NULL DEFAULT 0
);
```

- 拉取时过滤 `revoked = 0 AND published_at <= now`，按 `pinned DESC, published_at DESC` 排序；
- 下架 = 置 `revoked = 1`（客户端已记已读的不会再弹，未读的自然消失）；
- 注意：**内容改动请换新 id**，同 id 改内容老用户看不到（已读记录是按 id 的）。

## 4. 客户端存储

- 已读 id 存在 `launcher.settings.json` 的 `seen_announcement_ids`，最多保留最近 50 条；
- 卸载 / 清数据后已读记录丢失，公告会重新弹一次（可接受）。

---

# 附：Java 发行版用到的外部接口（无需后端）

客户端「设置 → Java → 下载并安装」直接请求这些公开接口，不经过我们的服务器。

## Temurin（Adoptium）

| 用途 | 请求 |
|---|---|
| 可用版本列表 | `GET https://api.adoptium.net/v3/info/available_releases` → `available_releases: [8,11,17,21,25]` |
| 下载地址 | `GET https://api.adoptium.net/v3/assets/latest/{major}/hotspot?architecture=x64&image_type=jdk&os=windows&vendor=eclipse`（客户端已有逻辑） |

## 龙井（Alibaba Dragonwell）

| 用途 | 请求 |
|---|---|
| 各版本最新发布 | `GET https://api.github.com/repos/alibaba/dragonwell{8,11,17,21}/releases/latest`（带 `User-Agent: StarcloudLauncher/1.0`，不带会 403） |
| 下载 | 从 `assets[].browser_download_url` 里挑：Windows `.zip` / unix `.tar.gz`，x64 或 aarch64 |

> 若用户反馈「显示无连接」：Adoptium 域名在部分网络不可达。目前的兜底是**换发行版**（龙井走 GitHub，网络路径不同）。
> 要真正国内加速，需要一个境内镜像（清华 TUNA / 阿里云 OSS / 自建反代），把地址给我即可接成第三条通道。

## 客户端落盘位置

| 发行版 | 目录 |
|---|---|
| Temurin | `%APPDATA%\starcloud-launcher\java\<大版本>\`（与旧版兼容） |
| 龙井 | `%APPDATA%\starcloud-launcher\java\dragonwell-<大版本>\` |
