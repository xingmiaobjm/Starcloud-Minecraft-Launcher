# 与其他启动器的实现对照清单

> 对照对象：**PCL**（Meloong-Git/PCL）、**HMCL**（HMCL-dev/HMCL）、**PrismLauncher**（PrismLauncher/PrismLauncher）、
> **ATLauncher**（ATLauncher/ATLauncher）、**Modrinth App**（modrinth/code）。
>
> 每条格式：**别的启动器怎么做** → **Starcloud 现状** → **本轮处置**。
> 「已落地」指本轮已改并有测试覆盖；「保留现状」指 Starcloud 已优于对方或该场景不适用。

---

## 1. Forge / NeoForge 安装流程

| 可借鉴点 | 别的启动器怎么做 | Starcloud 现状 | 处置 |
|---|---|---|---|
| 安装进度 | PCL `ForgelikeInjectorLine` 按安装器 stdout 的字面前缀映射到 0.04–0.98 的固定进度点（`Extracting json`→0.27、`Building Processors`→0.38、`Remapping jar... 50%`→0.9…） | 原来把 stdout 逐行原样当进度，用户看到的是一堆英文日志 | **已落地**：`ForgeInstallerService.DescribeInstallStage` 把输出翻译成中文阶段（解压版本信息 / 下载依赖库 / 构建处理器 / 合并映射 / 重映射最终 jar（50%）/ 写入版本配置…） |
| 成功判定 | PCL 不看 exit code，查输出最后 5 行有没有字面 `true` | 原来只看 exit code | **已落地**：输出末尾出现 `true` 也算成功（exit code 在部分路径下不可靠） |
| 失败清理 | PCL `McInstallFailedClearFolder` 失败后删版本目录；但目录里已有 `saves/` 说明用户用过，就不删 | 原来失败后留下半成品版本目录，列表里显示成一个坏版本 | **已落地**：失败时清掉半成品 `versions/<id>` 目录（按安装器文件名推断目标版本名） |
| 自己实现 processor | HMCL `ForgeNewInstallTask` / ATLauncher `Processor` 完全自己跑 processor（拿 `outputs` 的 SHA-1 校验）；PCL 走注入自研安装器 | 跑官方 `--installClient` | **保留现状**：官方安装器路径更稳、跟随上游变化；阶段化进度与失败清理已把主要痛点解决。outputs 校验可后续补 |
| Java 版本要求 | PCL 用 `VersionRange.AtLeast(...)`（≥ 即可） | 主版本精确匹配 | **保留现状**：精确匹配更安全（避免 21 跑在 17 上出问题），代价是可能多下一个运行时 |
| zlib 差异降级 | HMCL：非标准压缩导致 SHA-1 不符但文件是合法 ZIP 时，降级为只做 ZIP 完整性校验 | 只做 SHA-1 | 保留现状（待办，影响面小） |

---

## 2. 版本安装 / 管理 / 卸载

| 可借鉴点 | 别的启动器怎么做 | Starcloud 现状 | 处置 |
|---|---|---|---|
| 安装清单（ManagedFiles） | PCL `McInstallManifest` 记录每个文件的 `相对路径 + Sha1 + 修改时间 + 来源组件`，卸载/改版靠清单差分，只动自己带来的文件 | 无清单概念，删除是整目录递归删 | 保留现状（待办）：需要改安装器与删除流程，范围较大 |
| 删除进回收站 | HMCL `removeInstanceFromDisk`：先移到 draft 私有存储 → 发布新索引 → 再删；孤儿目录走「改名 `_removed` → 移回收站 → 失败才逐个删」 | 直接 `Directory.Delete(recursive)` 硬删 | 保留现状（待办） |
| 路径越界防护 | HMCL / PCL 都对版本 id 做字符校验 | **已落地**：`InstalledVersionManager.Remove` 校验 `IsValidVersionId` + `GetFullPath` 前缀检查（此前 Nova 的 `delete_version` 能把 `../../..` 拼进删除路径） |
| 依赖版本处理 | PrismLauncher 把加载器当「组件」，不落 `versions/<id>/<id>.json`；HMCL 用 patch 优先级模型，继承链在加载时展平 | `VersionVisibility` 三种依赖判定（被引用 / `.starcloud-dependency` 标记 / 纯加载器形态且无自有内容），只隐藏不删 | **保留现状**：Starcloud 的隐藏策略更细，且保留文件对 Forge 启动是必要的 |
| 共享实例内容残留 | PCL / HMCL 都区分隔离与共享 | 原来共享实例删除后 `mods/` 全部残留，但对话框不说 | **已落地**：删除确认框明确说明「共享实例的 mods / 配置 / 存档在 .minecraft 根目录，不会一并删除」 |
| 复制 / 改名 | PrismLauncher `InstanceCopyTask` 可选复制哪些目录；HMCL 改名是事务（任一步失败回滚） | 有改名 / 复制，未做事务 | 保留现状（待办） |

---

## 3. 整合包导入

| 可借鉴点 | 别的启动器怎么做 | Starcloud 现状 | 处置 |
|---|---|---|---|
| 整包解压 | PCL / HMCL 都是「整包解压到临时目录 → 再把 overrides 拷到实例」——同一份数据写两遍磁盘，几百 MB 的包要几分钟 | 原来同样如此 | **已落地**：`PackZip.ExtractPrefixes` 只解压 overrides / client-overrides 前缀，直接落到目标目录；预览也改成直接读 zip 内索引（`ReadIndex`），全程不解整包 |
| 格式嗅探 | PCL `ModpackInstall` 按根目录特征文件嗅探 7 种格式，都不匹配就当普通压缩包 | `ModPackDetector.Detect` 已有 7 种格式判定 | **保留现状**（已实现） |
| 未知加载器 | PCL `NotifyIncompatibleLoader` 弹窗问「不装该加载器继续吗」 | 原来静默忽略 | **已落地**：导入预览里对不认识的依赖键给 InfoBar 警告（当前版本暂不支持，可能无法启动） |
| 可选文件 | PCL：`env.client == "optional"` 弹窗问是否下载；`"unsupported"` 直接跳过 | `unsupported` 已跳过 | **已落地**：CurseForge 侧也跳过 `required=false` 的可选文件 |
| 哈希算法 | PrismLauncher 用 `hashes.sha512`；Modrinth App / PCL 用 `sha1` | 只认 sha1 | **已落地**：`MrPackHashes.Sha512` + 下载后二次校验（`DownloadService.VerifySha512`），校验不过就删文件 |
| 增量更新 | PrismLauncher 读旧 `modrinth.index.json`，按 hash 相同剔除已装文件；旧 overrides 先删再拷 | 每次全量重下（虽有「已存在则跳过」） | 保留现状（待办） |
| 覆盖优先级 | PCL / Modrinth App 都是 `overrides` 先、`client-overrides` 后（后者覆盖前者） | 一致 | 保留现状（已实现） |
| `server-overrides` | Modrinth App 明确不装 | 已不装 | 保留现状（已实现） |
| CurseForge 文件解析 | ATLauncher 遍历 `manifest.files`，`downloadUrl == null` 的跳过 | 已跳过 | 保留现状（已实现） |
| 并发解析 | — | 原来逐个串行 `GetFileAsync` 兜底（几百次往返） | **已落地**：改成 8 路并发兜底 |

---

## 4. 启动指令生成

| 可借鉴点 | 别的启动器怎么做 | Starcloud 现状 | 处置 |
|---|---|---|---|
| 库去重 | HMCL `uniqueLibraries` 按 `groupId:artifactId` 分组，规则相同则比版本留高的；PCL / PrismLauncher 同思路 | 原来只按路径去重，同 artifact 的多版本会同时进 classpath，靠 JVM「先找到的赢」 | **已落地**：`GameLauncher.DedupeLibrariesByArtifact`（规则不同的两条都留，规则相同比版本留高的） |
| 模块化参数 | PrismLauncher：`--add-opens java.base/java.net=ALL-UNNAMED`；PCL：`--add-exports cpw.mods.bootstraplauncher/...=ALL-UNNAMED` | 全仓无命中 | **已落地**：换皮肤时加 `--add-opens`；Forge/NeoForge 1.17+ 加 `--add-exports`；Java 24+ 加 `--sun-misc-unsafe-memory-access=allow` |
| BootstrapLauncher ignoreList | HMCL `rewriteUnsafeBootstrapLauncherIgnoreList`：老版按全路径子串匹配，目录名含 `asm` 就会误伤整片库 | 完全没处理 | **已落地**：`GameLauncher.FixBootstrapIgnoreList`（补主 jar 文件名） |
| GC 按 Java 版本 | PCL `SetupType`：21+ 分代 ZGC、15–20 非分代 ZGC、14 以下 G1GC；加参数前先移除已有 GC 参数 | 固定 `-XX:+UseG1GC` | **已落地**：`BuiltinGcArgs` 按主版本分档 |
| log4j2 防御 | PCL / HMCL 都加 `-Dlog4j2.formatMsgNoLookups=true`（HMCL 允许用户用 `=false` 关掉） | 无 | **已落地** |
| 命令行长度 | PrismLauncher 全程逐项传参、从不拼字符串；HMCL 有 `CommandTooLongException` | 拼成字符串再切回去 | **部分落地**：已改用 `ProcessStartInfo.ArgumentList` 逐项传参；argfile 方案保留现状（待办） |
| 库路径编码 | PrismLauncher：Windows 下 classpath 每项过本地编码 | 原样 | 保留现状（待办） |
| 路径过长 | PCL：>260 字符时用 8.3 短路径，且必须先建目录 | 无 | 保留现状（待办） |
| Forge 老版本 classpath | HMCL 也处理 `legacyClassPath` | 原来不认 → Forge 报「找不到主类」 | **已落地** |
| 官方日志参数 | Mojang 1.16+ 的 `logging.client.argument` | 原来忽略 | **已落地** |
| JVM 参数去重 | PCL `DeduplicateJavaArguments` 对键值对处理很细 | 只按整串去重 | **已落地**（本轮之前的改动）：`DedupeJvmArgs` 按参数名去重，`-XX:` 整体保留 |

---

## 5. 下载引擎

| 可借鉴点 | 别的启动器怎么做 | Starcloud 现状 | 处置 |
|---|---|---|---|
| Retry-After | PrismLauncher `Request::downloadError`：429 时优先用响应头给的 `Retry-After`（支持秒数与 HTTP 日期两种格式） | 冷却时间全靠自己猜（30/60/120s…） | **已落地**：`RetryAfterParser` + `ParallelGovernor.ReportRetryAfter`（服务端说的时长优先） |
| 404 不重试 | PrismLauncher：`404` 明确「没有重试的意义」，直接排除 | 所有失败一律重试，404 白等满退避 | **已落地**：`PermanentHttpException`（404/410），换源即放弃，不进退避 |
| 源健康度 | PCL / HMCL / PrismLauncher 都有镜像质量与排序机制 | 原来只看「是否降级」 | **已落地**（本轮之前的改动）：吞吐 EMA × 5 分钟半衰期 − 失败 − 限流，慢源自动靠后 |
| 多候选 URL | HMCL `DownloadProvider.getDownloadCandidates` 返回官方 + 各镜像依次尝试；PCL `NetFile(List<String> Urls, …)` | 有多源，但分配后钉死 | **已落地**（部分）：源池按健康度排序 + 并发度按实际用的源取 |
| 分片与续传 | PCL / HMCL 无分片；PrismLauncher 无分片 | 已分片 + `.parts` 续传 | **保留现状（优于对方）** |
| 完整性兜底 | HMCL `ZIP_INTEGRITY_CHECK_HANDLER`；PCL `FileChecker.MinSize` | 只做 SHA-1 | 部分落地：sha512 已补；ZIP 完整性检查待办 |
| 大小文件分池 | ATLauncher `pool.downsize()`：大文件少并发、小文件多并发 | 统一并发度 | **已落地**（部分）：批量按大小降序，大文件先拿额度 |
| 全局哈希缓存 | HMCL `CacheRepository` 跨实例复用 | `LocalFileReuse` 只在同一 `.minecraft` 内 | 保留现状（待办） |

---

## 6. 启动性能

| 可借鉴点 | 别的启动器怎么做 | Starcloud 现状 | 处置 |
|---|---|---|---|
| 冷启动后台化 | PCL `McLaunchPrerun` 把重活推到启动前；Modrinth App 全 Rust 异步 | 首页版本扫描在 UI 线程读每个版本的 JSON | **已落地**（本轮之前的改动）：扫描后台化 + 首屏导航延后到窗口显示之后 |
| 日志异步 | — | 每条日志同步开文件追加 | **已落地**（本轮之前的改动）：后台写队列 |
| 编码乱码防护 | PCL 自研 JavaWrapper 解决 JDK-8272352（非 GBK 区域设置下参数乱码） | 直接设 `StandardOutputEncoding = UTF8` | 保留现状（待办） |
| 进程 detach | PrismLauncher `setDetachable(true)`：关启动器窗口后游戏继续跑 | 已有运行态监控 | 保留现状 |

---

## 7. 结论

**本轮落地（11 项）**

1. 整合包导入不再整包解压两次（Modrinth 预览与安装）
2. CurseForge 逐个文件解析改 8 路并发
3. 库按 `groupId:artifactId` 去重并保留高版本
4. JPMS `--add-opens` / `--add-exports` / `--sun-misc-unsafe-memory-access`
5. BootstrapLauncher ignoreList 修复
6. GC 参数按 Java 版本分档 + log4j2 防御
7. Forge/NeoForge 安装阶段化进度 + `true` 输出判定 + 失败清理
8. 下载感知 `Retry-After`
9. 404/410 不再重试
10. mrpack 支持 sha512 校验
11. 整合包未知加载器依赖给出警告

**Starcloud 已优于对方、明确保留现状（7 项）**

- 分片下载 + 分片级断点续传（PCL / HMCL / PrismLauncher 都没有）
- 源健康度评分（比三家的机制都细）
- 按通道记忆 Range 能力
- 整合包只读 zip 内索引、不整包解压
- classpath 指纹缓存
- 磁盘空间预检（PCL / HMCL 都没有）
- 依赖版本的三种判定与隐藏策略

**明确待办（按价值排序）**

1. `@argfile`：Windows 命令行 32 KB 上限（重度整合包会撞）
2. 安装清单（ManagedFiles）+ 删除进回收站（PCL / HMCL）
3. 整合包增量更新（按 hash 剔除已装文件）
4. 全局哈希缓存仓库（HMCL `CacheRepository`）
5. 路径过长时的 8.3 短路径回退（PCL）
6. 非 GBK 区域设置下的参数编码防护（PCL JavaWrapper）
7. ZIP 完整性兜底校验（HMCL / PCL）
8. 实例复制 / 改名的事务化（HMCL）
