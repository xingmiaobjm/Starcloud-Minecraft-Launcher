# CurseForge 资源源接入（2026-09-21）

## 1. 目标与范围

让启动器的社区资源能力不止 Modrinth：**搜索、查看文件、下载安装 CurseForge 的
Mod / 资源包 / 光影包 / 数据包 / 整合包**，并且尽量不给既有链路分叉。

非目标：不做 CurseForge 账号登录、不做项目上传、不做"按 CF 项目反查更新"
（更新检查仍以 SHA1 → Modrinth 反查为准，见 §6）。

## 2. 接口选择：为什么不是旧版那套"无 key 爬官网"

旧 Tauri 版走 `https://www.curseforge.com/api/v1/mods/search`（PCL2 那套无 key 接口）。
2026-09-21 实测：

| 路径 | 结果 |
| --- | --- |
| `www.curseforge.com/api/v1/...` | **403**，响应头 `Cf-Mitigated: challenge`（Cloudflare 托管挑战，第三方客户端拿不到数据） |
| `api.curseforge.com/v1/...`（不带 key） | **403** `Forbidden: API Key missing or invalid` |
| `api.curse.tools/v1/...`（社区只读代理） | **200**，响应结构与官方完全一致（`data` / `pagination`） |

因此做成**两层可切换**，共用同一套映射代码：

1. 默认：`https://api.curse.tools/v1`（无需 key，开箱即用）；
2. 设置页填了 `CurseForge API Key`（可空）→ 直连 `https://api.curseforge.com/v1` 并带
   `x-api-key`，走官方授权路径（合规首选，Key 由用户自备）。

## 3. 关键设计：复用 Modrinth 的数据模型

搜索结果与文件列表**直接复用 `ModrinthHit` / `ModrinthVersion`**，只多两个 `[JsonIgnore]`
标记字段（`Source` / `WebUrl`）。于是：

- 下载页的列表、分页、筛选、图标、翻译提示全部照用；
- 下载窗的详情卡、版本芯片、文件行、安装动作全部照用；
- 资源库入库、装载到版本/存档、依赖检查全部照用。

`CurseForgeService` 只负责「官方响应 → 统一模型」的映射：

| 统一模型字段 | CurseForge 来源 |
| --- | --- |
| `ProjectId` / `Id` | `id`（数字串） |
| `Title` / `Description` | `name` / `summary` |
| `Author` | `authors[].name` |
| `Downloads` | `downloadCount` |
| `IconUrl` | `logo.thumbnailUrl ?? logo.url` |
| `Loaders` | `latestFilesIndexes[].modLoader`（1 Forge / 4 Fabric / 5 Quilt / 6 NeoForge） |
| `GameVersions` | `gameVersions` 里形如 `1.21.1` 的项（滤掉 `Fabric`/`Client` 这类标签） |
| `Files[0].Url` | `downloadUrl`，缺失时按官方分片规则拼 CDN 直链 |
| `Files[0].Sha1` | `hashes[]` 中 `algo==1`（SHA1） |
| `Dependencies[].DependencyType` | `relationType`（3 必需 / 2 可选 / 5 冲突 / 其余内嵌） |
| `VersionType` | `releaseType`（1 正式 / 2 Beta / 3 Alpha） |

分类筛选（`classId`）与排序（`sortField`）在服务内做了映射；**分类下拉只对 Modrinth 生效**
（CF 分类是另一套名称体系），切到 CurseForge 时该下拉灰显，避免"选了没反应"。

## 4. 整合包：CurseForge zip 与 .mrpack 走同一套下游

CurseForge 整合包是「zip + `manifest.json` + `overrides/`」，与 Modrinth 的 `.mrpack` 不同，
但对下游是同一件事：装版本 + 装加载器 + 铺文件。所以：

- `CurseForgePackInstaller` 解析 `manifest.json`（`minecraft.version`、`modLoaders[].id`、
  `files[].{projectID,fileID}`、`overrides`），逐个文件解析直链并下载到 `mods/`，
  再把 `overrides/` 铺到实例根；
- 安装结果直接返回 **`MrPackInstallResult`**，加载器联动继续走既有的
  `MrPackFlow.EnsureGameAndLoaderAsync`（`forge-47.2.0` / `neoforge-21.1.9` /
  `fabric-0.15.11` 这类 id 会被翻译成依赖键）；
- `overrides` 条目逐条校验路径，拒绝绝对路径、`..` 与盘符（整合包是第三方产物，不可信）。
- 作者未开启分发（拿不到直链）的文件会被跳过并写进启动器日志，不让整包装配失败——
  与 PCL2 / HMCL 的处理一致。

## 5. 实测踩坑：Forge CDN 与系统代理

同一 URL 的下载实测（2026-09-21）：

| 方式 | 结果 |
| --- | --- |
| `curl -L`（直连） | **200**，1,574,609 字节 |
| .NET `HttpClient` 默认（走系统代理 `127.0.0.1:26561`） | **403** |
| .NET `HttpClient` + `UseProxy=false` | **200** |

即：**开着 Clash 之类系统代理的用户，`*.forgecdn.net` 会被代理回 403**（国内相当常见），
而纯直连在个别网络下又可能不通。所以 `DownloadService` 对 `forgecdn.net` 域名改成
**先直连、失败再走系统代理**（`FetchWholeAsync`），两个方向都试过才不至于各挂一半。

## 6. 资源库与更新检查

- `LibraryEntry` 新增 `source` 字段（老索引缺省按 Modrinth 处理），入库时记录实际来源；
- CurseForge 文件**不写 `ModrinthVersionId`**（CF 的文件 id 不是 Modrinth 版本 id，
  写进去会让更新检查拿错 ID 去问 Modrinth）；`SourceUrl` 指向 CF 项目页；
- 结果是：CF 装的 Mod 在没有对应 Modrinth 项目时不会被误报"有新版本"；若同一个 jar
  恰好也发布在 Modrinth（很常见），SHA1 反查仍能命中，更新检查照常工作。

## 7. 界面

- 下载页「模组资源」筛选卡新增**来源**下拉（Modrinth / CurseForge），切换即按新来源重搜；
  切到 CurseForge 时分类下拉灰显、缓存角标隐藏（缓存只属于 Modrinth）；
- 下载窗顶部新增**来源切换**（两个分段按钮），覆盖两个入口（下载页卡片 / 版本详情页内容芯片），
  切来源即清空结果并用同一关键词重搜，详情卡的「来源」一行同步显示；
- 设置页「下载」卡新增 **CurseForge API Key（可空）**：留空用社区只读代理，填了走官方 API。

## 8. 验证

离线（随普通测试跑）：搜索响应映射、文件映射（加载器/版本/类型/SHA1/依赖关系）、
CDN 分片直链规则、整合包加载器标识解析、整合包清单读取与 overrides 路径穿越拒绝。

真机联调（`STARCLOUD_IT_CURSEFORGE=1 dotnet test --filter 真机联调_搜索`）：
真实搜索 `sodium`（fabric / 1.21.1）→ 列文件 → 单独查同一文件（整合包走的就是这条）→
**真实下载首文件并校验落盘字节数与 SHA1 与官方一致**。实测通过（约 2 秒）。

## 9. 已知边界

- 分类筛选（CF 分类树）未接入，CurseForge 下按关键词 + 加载器 + 游戏版本筛选；
- 依赖提示对 CF 项目是「取标题列出来」，不会自动安装 CF 依赖（Modrinth 侧有自动补全）；
- 社区只读代理是第三方基础设施：不可用时填自己的官方 API Key 即可切到官方路径；
- 作者未开启分发的整合包文件无法自动下载（这是 CF 的授权限制，不是启动器能绕过的）。
