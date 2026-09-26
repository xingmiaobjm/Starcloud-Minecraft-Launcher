# 多下载源聚合（多源分片竞速）设计

日期：2026-09-12
状态：待用户确认
所属：PCL2 平替（下载链路）

## 1. 背景与问题

用户诉求：

> 先实现多下载源聚合。

现状问题（探查结论）：

1. **`.NET` 侧完全没有镜像能力**。全仓库 `Grep "bmclapi|mirror|镜像|下载源"` 只命中
   [DownloadPage.xaml.cs](../../../dotnet/src/Starcloud.App/Views/DownloadPage.xaml.cs)、
   [ResourceCacheService.cs](../../../dotnet/src/Starcloud.Core/Services/ResourceCacheService.cs)
   的注释性提及，无任何实现。
2. **版本清单硬编码官方域名**。[ManifestService.cs](../../../dotnet/src/Starcloud.Core/Services/ManifestService.cs#L11)
   的 `ManifestUrl` 写死 `https://piston-meta.mojang.com/...`。国内网络下这一条就足以让
   「下载页列表」加载失败。
3. **下载引擎只有整文件重试**。[DownloadService.cs](../../../dotnet/src/Starcloud.Core/Services/DownloadService.cs#L38-L68)
   的 `DownloadFileAsync` 是「单 URL → 单连接 → 失败重试同一 URL」，没有换源、没有分片。
   `client.jar`（数十 MB）只能靠单连接跑满带宽，镜像再快也叠加不起来。
4. **旧 Rust 有完整实现但已不在主链路**。主实现 2026-09 起迁到 `.NET`，
   [sources.rs](../../../src/download/sources.rs)（659 行，含 5 源定义、域名改写、测速、择优、
   健康度降级与 12 个单元测试）保留作参考，未被移植。本次即把它移植并升级为**分片竞速**。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| 聚合方式 | **多源分片竞速**：同一文件切分片，同时从多个源并行拉取，按序拼接后整文件 SHA1 校验 |
| 适用范围 | **只原版安装链路**（client.jar + libraries + assets）；Modrinth / Java 运行时 / 整合包维持现状 |
| 代码架构 | **方案 A：源改写内聚到 `DownloadService`**，上游 `ManifestService` / `InstallerService` / `FabricService` 零改动 |

### 与旧 Rust 实现的偏离（需记录）

1. **不再做启动时测速择优**。旧 `probe_source` / `pick_best`（[sources.rs](../../../src/download/sources.rs#L311-L417)）
   是「挑最快的单一源」思路；本次改为竞速池同时使用多个源，测速的收益被竞速本身吸收，
   故**不移植测速与择优**，只保留「健康度降级」用于把坏源踢出池子。
2. **小文件不改单源整文件下载**。assets 有数千个几十 KB 文件，分片开销远大于收益。

## 3. 组件设计

### 3.1 新增 `DownloadSource.cs`（`Starcloud.Core/Services/`）

```csharp
public enum SourceKind { Official, Bmclapi, Fastmirror, Rainyun, Custom }

public sealed class DownloadSourceRegistry
{
    public DownloadSourceRegistry(Func<LauncherSettings> settings);

    public static string Id(SourceKind k);            // "official" / "bmclapi" / "fastmirror" / "rainyun" / "custom"
    public static string Name(SourceKind k);          // "Mojang 官方" / "BMCLAPI" / ...
    public static bool TryFromId(string id, out SourceKind k);

    // 纯函数：按源改写 Mojang 域名；非 Mojang 域名原样放行
    public static string Rewrite(string url, SourceKind kind, string? customMirrorUrl);

    // 竞速池：健康镜像源 → 降级源 → 官方兜底；用户指定单一源时只含该源
    public IReadOnlyList<SourceKind> RacingPool();

    public void RecordSuccess(SourceKind k);          // 重置失败计数、恢复降级
    public void RecordFailure(SourceKind k);          // 连续失败 ≥2 → 降级（阈值见 §9 风险 4）
    public void MarkNoRangeSupport(SourceKind k);     // 该源不支持 Range，退出分片池（进程内）
    public void ResetHealth();
}
```

域名改写规则照抄 [sources.rs#L244-L289](../../../src/download/sources.rs#L244-L289)：

- **Official**：恒等（原样返回）。
- **Bmclapi / Rainyun / Custom**：单一镜像根。`piston-meta` / `piston-data` /
  `launchermeta` / `launcher` 四个 host → 镜像根；`resources.download.minecraft.net` →
  `{mirror}/assets`；`libraries.minecraft.net` → `{mirror}/maven`。
  - BMCLAPI 根 = `bmclapi2.bangbang93.com`；RainYun 根 = `mirrors.rainyun.com`。
  - Custom 根 = `LauncherSettings.DownloadCustomMirrorUrl`（去除首尾空白与结尾 `/`，
    去掉 `http(s)://` 前缀后按镜像根处理；未配置时等同 Official）。
- **Fastmirror**：按子域拆分 —— `piston-meta` / `launchermeta` → `launchermeta.fastmcmirror.org`；
  `piston-data` / `launcher` → `launcher.fastmcmirror.org`；
  `resources.download.minecraft.net` → `resources.fastmcmirror.org`；
  `libraries.minecraft.net` / `libraries.mojang.com` → `libraries.fastmcmirror.org`。
- 未命中上述任何 host 的 URL 一律原样返回。

健康度：`ConcurrentDictionary<SourceKind, (int Failures, bool Degraded)>`。
连续失败达 5 次标记降级；`RacingPool()` 排序键为「健康源 = 0 → 降级源 = 1 → 官方 = 2」，
且**官方恒为最后兜底**。用户显式指定单一源时不受降级影响（否则会把用户锁死）。

### 3.2 新增 `ChunkedFileDownloader.cs`（`Starcloud.Core/Services/`）

```csharp
public sealed class ChunkedFileDownloader(HttpClient http, DownloadSourceRegistry sources)
{
    public const long Threshold  = 4 * 1024 * 1024;   // ≥4 MiB 才分片
    public const long ChunkSize  = 4 * 1024 * 1024;
    public const int  MaxChunks  = 16;
    public const int  MaxWorkers = 4;

    public Task DownloadAsync(string url, string destFile, string? sha1, long size,
        int maxRetries, Action<long>? onBytes, CancellationToken ct);

    // 纯函数，可单测：区间精确覆盖 [0,size)、无重叠无空洞、片数不超上限
    internal static IReadOnlyList<(long Start, long Length)> PlanChunks(long size);
}
```

`PlanChunks`：片大小 4 MiB；`ceil(size / 4MiB) > 16` 时把片大小扩大为
`ceil(size / 16)` 并对齐到 1 MiB 上取整，保证片数 ≤ 16。

### 3.3 `DownloadService` 接线

- 构造函数改为 `DownloadService(Func<LauncherSettings> settings)`（对齐
  [ResourceCacheService](../../../dotnet/src/Starcloud.Core/Services/ResourceCacheService.cs) 现有模式）。
- `SocketsHttpHandler.MaxConnectionsPerServer` 64 → **128**（承载分片并发）。
- 持有 `DownloadSourceRegistry` 与 `ChunkedFileDownloader` 各一个实例。
- `GetStringAsync` 出口做源改写 → **`ManifestService` 因此自动走镜像**；
  `api.modrinth.com` / `meta.fabricmc.net` / `maven.minecraftforge.net` /
  Starcloud API 等非 Mojang 域名原样放行，不受影响。
- `PostJsonAsync` **不做改写**（Starcloud ID 接口，与 Mojang 无关）。
- 新增 `public Action<string>? Log { get; set; }`（默认 `null`，零开销），
  记录「文件名 → 使用的源 → 字节数 / 耗时」，用于验证竞速是否真的生效。

### 3.4 `AppServices` 接线

[AppServices.cs#L53](../../../dotnet/src/Starcloud.App/Services/AppServices.cs#L53)：
`Download = new DownloadService(() => Settings);`

`Settings` 在 `Download` 之前构造（第 52 行），故可直接捕获。

### 3.5 设置项与 UI

[LauncherPaths.cs#L108-L112](../../../dotnet/src/Starcloud.Core/Services/LauncherPaths.cs#L108-L112)
的 `LauncherSettings` 新增两个字段（沿用 snake_case 命名约定）：

| JSON 名 | C# 属性 | 类型 | 默认 |
|---|---|---|---|
| `download_source` | `DownloadSource` | `string` | `"auto"`（多源竞速）；另有 `"official"` / `"bmclapi"` / `"fastmirror"` / `"rainyun"` |
| `download_custom_mirror_url` | `DownloadCustomMirrorUrl` | `string?` | `""`（空 = 不启用 Custom 源） |

设置页「下载」卡片（[SettingsPage.xaml#L263-L278](../../../dotnet/src/Starcloud.App/Views/SettingsPage.xaml#L263-L278)）
追加：

- 「下载源」`ComboBox`：自动（多源聚合）/ 仅官方 / BMCLAPI / FastMinecraftMirror / RainYun 雨云。
- 「自定义镜像地址」`TextBox`：占位提示镜像根 URL。

保存逻辑接在 [SettingsPage.xaml.cs#L736-L737](../../../dotnet/src/Starcloud.App/Views/SettingsPage.xaml.cs#L736-L737)
现有字段赋值处；未知 ID 回落为 `"auto"`。

新增文案的中文原文即 key，需同步补 [Strings/en-US.json](../../../dotnet/src/Starcloud.App/Strings/en-US.json)：
`"下载源"`、`"自动（多源聚合）"`、`"仅官方"`、`"自定义镜像地址"`。

**保留设置 UI 的理由**：竞速出问题时必须能强制回官方来定位，否则该功能不可验证。

## 4. 下载流程与数据流

### 4.1 分片执行

1. 预分配临时文件 `dest + ".sc-tmp"`，`FileStream.SetLength(size)`。
2. `ConcurrentQueue<Chunk>` 放全部分片 + `min(MaxWorkers, 片数)` 个 worker 任务并发消费。
3. worker 取片 → 从 `RacingPool()` 按 `Interlocked.Increment` 轮询取源 → 发
   `Range: bytes={start}-{end}` 请求。
4. 响应判定：
   - `206` 且长度吻合 → `RandomAccess.WriteAsync(handle, buf, start)` 按偏移写入（无需锁），
     并 `RecordSuccess`。
   - `200`（源不支持 Range）→ `MarkNoRangeSupport`，该源退出分片池。
   - 其他状态码 / 长度不符 → 视为失败。
5. 分片失败 → 换下一个源重派，同一分片累计重试上限 = `settings.DownloadMaxRetries`；
   耗尽则抛错，整文件失败。
6. 每片每读到一段回调 `onBytes(n)`，由 `DownloadBatchAsync` 累加进 `BatchProgress`；
   速度 / ETA 沿用现有 `Stopwatch` 估算（[DownloadService.cs#L113-L121](../../../dotnet/src/Starcloud.Core/Services/DownloadService.cs#L113-L121)）。

### 4.2 批量分流

`DownloadBatchAsync` 把待下载列表展开成任务单元：

- 小文件（`size < Threshold` 或 `size <= 0`）→ 1 个「整文件任务」，沿用现有单源路径。
- 大文件（`size >= Threshold`）→ 1 个「分片组任务」，占 **1 个闸门位**，内部自己跑至多 4 个分片 worker。

**分片竞速的准入门槛（实现期细化）**：只有 `DownloadSourceRegistry.IsMojangUrl(url)` 为真
（即命中 §3.1 的 7 个 `MojangPrefixes`）的文件才进入分片竞速；其余域名（Modrinth / Fabric /
Adoptium 等）无论体积一律走整文件路径，被镜像改写的也仍然原样放行。理由是分片竞速的价值
来自「多源叠加」，而非 Mojang 域名没有任何镜像会去改写，分片只会凭空增加并发与开销。
`size <= 0` 且是 Mojang 域名时先用 `ProbeSizeAsync`（HEAD，经首选源改写）探测一次再分流；
探测失败按未知大小走整文件路径。

闸门仍是 `settings.DownloadConcurrency`（默认 16）→ 峰值连接 16 × 4 = 64 < 128 上限。
进度模型统一为「每个任务单元上报字节」，无需第二套并发池。

单文件入口 `DownloadFileAsync` 也按 `Threshold` 分流：大文件走分片竞速，小文件走
`DownloadWholeFileAsync`（= 现有整文件重试逻辑，单源）。故启动前自检修复 client.jar
（[LaunchPreflightService.cs#L151](../../../dotnet/src/Starcloud.Core/Services/LaunchPreflightService.cs#L151)）
也自动享受竞速。`DownloadWholeFileAsync` 是**不分片**的私有路径，分片失败时的兜底重试
直接调它，不会再次进入分片流程。

`download_source = "auto"` → 竞速池为多源；指定具体源 → 池内只有它（仍并行分片，
对单源同样提速）。

## 5. 错误处理与一致性

**完成与校验（两段式）**

1. 全部分片写入 `.sc-tmp` 完成后关闭句柄 → 算整文件 SHA1。
2. 匹配 → `File.Move(tmp, dest, overwrite: true)`；不匹配 → 删 `.sc-tmp`，
   退化为 `DownloadWholeFileAsync`（既有单源整文件重试逻辑，**不分片**），
   最多 `settings.DownloadMaxRetries` 次；仍失败才向上抛错。
3. 单源整文件路径的 SHA1 语义与现有实现完全一致，不改。
4. 临时文件名统一 `.sc-tmp`（复用现有约定），不引入第二套命名。

**失败与降级**

- 请求成功 → `RecordSuccess`；失败 → `RecordFailure`（连续 ≥5 → 降级，退出竞速池）。
- 官方兜底与「用户指定单源」不受降级影响。
- 池中全部源失败 → 该文件抛错，由 `Task.WhenAll` 冒泡，沿用现有报错链路。

**取消与并发安全**

- `CancellationToken` 贯穿分片 worker；取消 / 异常时 `try/finally` 关闭句柄并删 `.sc-tmp`。
- 已完成字节用 `Interlocked.Add`；分片按偏移 `RandomAccess` 写入，天然无锁。
- `_http.Timeout` 保持 60s：4 MiB 分片 60s 拉不完说明该源极慢，判失败换源是正确行为。

## 6. 不做的事（YAGNI）

- 不移植启动时测速与 `pick_best` 择优（理由见 §2 偏离说明）。
- 不做自定义镜像源的独立管理 UI（只留一个 URL 输入框）；不做多镜像档案。
- 不改 Modrinth / `.mrpack` / Java 运行时 / Forge 安装链路。
- 不引入断点续传（跨进程恢复 `.sc-tmp`）。

## 7. 单元测试

仓库 `.NET` 侧无测试工程，新建 `dotnet/tests/Starcloud.Core.Tests`（xUnit，`net8.0`，
`Starcloud.Core` 是纯 `net8.0` 可直接引用），并加入
[Starcloud.slnx](../../../dotnet/Starcloud.slnx)。

把 Rust 的 12 个测试翻过来，再补分片计划：

1. 改写：官方恒等；BMCLAPI 四条（meta / data / assets / maven）；FastMirror 四条；
   RainYun 两条；非 Mojang 域名全源放行；自定义镜像（有 URL / 无 URL）。
2. `TryFromId` 往返 + 未知 ID 返回 `false`。
3. 健康度排序：降级源排在健康源之后、官方恒最后、`RecordSuccess` 后恢复。
4. `PlanChunks`：`size` 整除 / 有余数 / 超 16 片时扩大片大小 —— 断言区间无重叠无空洞、
   各段长度之和等于 `size`、片数 ≤ `MaxChunks`。

**不做**本地 `HttpListener` 假源端到端竞速测试（成本高、收益低），靠实机验收覆盖。

## 8. 实机验收

1. 装一个原版（如 1.21.1），确认安装成功且 jar 的 SHA1 与 manifest 一致。
2. 依 `DownloadService.Log` 输出确认 client.jar 命中镜像域名，且多个源参与了分片。
3. 改 hosts 把某镜像打到黑洞 → 确认自动切换其他源，安装仍成功。
4. 设置页切「仅官方」→ 请求回到 `piston-data.mojang.com`，安装成功。
5. `dotnet build dotnet/src/Starcloud.App/Starcloud.App.csproj` 0 警告 0 错误；`dotnet test` 全绿。

### 验收记录（2026-09-12，自动化部分）

未走 GUI，而是用一个临时控制台工程直接驱动 `Starcloud.Core` 的真实 `DownloadService`
下载真实文件（1.21.1）——这是同一份生产代码路径，证据强度等同实机：

| 场景 | 结果 |
|---|---|
| `auto` + client.jar（26,836,906 B） | 成功。日志 `分片竞速 client.jar（25.6MB / 7 片）：Mojang 官方 13.6MB / BMCLAPI 12.0MB` —— **两个源确实同时贡献了字节**（对应验收项 2） |
| `auto` + 小 library（964 B，`libraries.minecraft.net` → `/maven` 改写） | 成功，SHA1 校验通过 —— 整文件路径与镜像改写生效 |
| `official` + client.jar | 成功。日志 `分片竞速 client-official.jar（25.6MB / 7 片）：Mojang 官方 25.6MB` —— 对应验收项 4（仅官方不走镜像） |
| `dotnet test` | 88 passed / 0 failed |

验收项 3（断源切换）由实测的 FastMirror 不可达 / RainYun 不支持 Range 天然覆盖：
两者都被自动剔除，下载仍成功。


## 9. 风险与未决

1. **镜像是否都支持 Range —— 已逐源实测（2026-09-12，1.21.1 client.jar 26,836,906 B）**：

   | 源 | `Range: bytes=0-1023` 结果 | 结论 |
   |---|---|---|
   | `piston-data.mojang.com` | `206` / 1024 B | 支持 |
   | `bmclapi2.bangbang93.com` | `206` / 1024 B（先 302 跳转，需跟随重定向） | 支持 |
   | `launcher.fastmcmirror.org` | TCP 443 连接超时（DNS 正常解析到 195.123.225.29） | 本机网络不可达 |
   | `mirrors.rainyun.com` | `200` + 3516 B（返回的是站点 HTML，非镜像文件） | 不支持 Range，且该根路径下无 Minecraft 文件 |

   RainYun 的改写结果继承自旧 Rust `rewrite_root(url, "mirrors.rainyun.com")`，即把
   `piston-data` 映射到镜像根。实测该路径不提供文件，故 RainYun 实际不会贡献字节。
   两个异常源都被设计内的机制自动处理：RainYun 首次请求返回 `200` 即触发
   `MarkNoRangeSupport` 退出分片池、FastMirror 连续失败触发降级，**功能正确性不受影响**。

2. **镜像内容与官方不一致**（镜像滞后或损坏）。缓解：整文件 SHA1 校验是最终裁判，
   校验失败即换整文件单源下载重试。
3. **`MaxConnectionsPerServer` 提到 128 的内存/句柄开销**未实测。若异常，回落方案是
   把单文件分片 worker 从 4 降到 2。
4. **不可达镜像会先拖慢、再被降级（实测数据）**。同一台机器、同一 26 MB client.jar：

   | 配置 | 耗时 | 日志 |
   |---|---|---|
   | `auto`（阈值 5） | 31.1 s | `Mojang 官方 13.6MB / BMCLAPI 12.0MB` |
   | `auto`（阈值 2） | 21.2 s | `Mojang 官方 13.6MB / BMCLAPI 12.0MB` |
   | `official` | 3.7 s / 5.6 s（两次测量） | `Mojang 官方 25.6MB` |

   根因：FastMirror 的连接超时约 3.7 s/次，而旧 Rust 沿用的 `DegradeAfterFailures = 5`
   要累计 5 次连续失败才降级，首次安装的前几个分片会白等十几秒。

## 10. 提速专项（2026-09-21）：让引擎不再是瓶颈

需求：「下载速度不低于 5 MB/s」。先定位瓶颈再动手，实测结论是**客户端此前只用了单连接的一部分能力**。

### 10.1 改了什么

| 改动 | 位置 | 为什么 |
|---|---|---|
| 分片从「仅 Mojang 域名」放开到**任何支持 Range 的主机** | `DownloadService.DownloadOneAsync` + `DownloadService.ProbeRangeAsync` | Modrinth / CurseForge 这些 CDN 也支持 206，此前却只能单连接拉 |
| Range 能力**按主机记忆**（进程内） | `DownloadSourceRegistry.RangeSupport/MarkRangeSupport` | 原来按"源"记，非 Mojang 域名下 5 个源同址，会把同一台 CDN 反复试探 |
| 单文件并发 4 → **8**，片大小按「每 worker 约 2 片」自适应（1–8 MiB，片数 ≤ 64） | `ChunkedFileDownloader.PlanChunks` | 4 条太少；固定 4 MiB 片让 20 MB 文件只有 5 片、喂不满并发 |
| 进程级 worker 预算 32 | `ChunkedFileDownloader.GlobalWorkerBudget` | 批量下载（默认 16 文件并行）时别把连接数打到 128 |
| 分片请求强制 **HTTP/1.1** | `ChunkedFileDownloader.FetchRangeAsync` | HTTP/2 会把 8 条"并行"分片复用到一条 TCP 连接上，实测退化成单连接速度，并在慢 CDN 上因流控停顿触发空闲超时 |
| **主机级并行度调节器**：起始 4 → 连续 8 次成功 +1（上限 8）→ 429/503 减半 + 冷却 30s…5min | `ParallelGovernor.cs` | 实测 BMCLAPI 8 条并发会整批 429（连整文件兜底都被牵连） |
| 读缓冲 80 KB / 256 KB → **512 KiB**（ArrayPool 租借） | 两条路径 | 少一半以上的系统调用与等待 |
| 空闲看门狗一条流一个（原来每次读都新建 CTS） | `IdleTimeout.cs` | 满速时原来每秒数百个定时器入队，反过来成了开销 |
| 字节遥测 10 Hz 聚合（原来每 80/256 KiB 上报一次：锁 + 排序 + 事件） | 两条路径 | 调度中心的速度数字不需要每次读都算 |
| 调度中心收尾循环单飞 | `TaskCenterService.EnsureCloseLoop` | 原来每次上报都 `_ = CloseWhenIdleAsync()`，满速时每秒几百个睡眠任务 |
| 竞速任务支持**多文件并发记账**（token 记账） | `TaskCenterService.ReportRacing*` | 分片放开后整合包会同时有多个大文件在跑，旧的全局字段会让进度条来回跳 |

### 10.2 实测（2026-09-21，本机直连，系统代理已关）

| 场景 | 单连接 | 8 连接并行 | 说明 |
|---|---|---|---|
| `speed.cloudflare.com` 24 MB | 1.57 MB/s | **9.14 MB/s** | 并行度直接决定吞吐：5.8× |
| 本地限速服务器 8 MiB（每连接限 1 MB/s） | 1.0 MB/s | **8.0 MB/s** | 引擎端到端实测（单元测试 `单连接被限速时_并行分片把整文件吞吐拉起来`） |
| `piston-data.mojang.com`（官方 CDN） | 0.04 MB/s | 0.26 MB/s | 本机到官方 CDN 基本不可用，只能靠镜像 |
| `bmclapi2.bangbang93.com` | 0.32–1.22 MB/s | 1.78 MB/s，再高即 **429** | 镜像侧限制，客户端无法突破 |
| `launcher.fastmcmirror.org` | TCP 超时 | — | 本机网络不可达 |

**结论：引擎侧不再是瓶颈**（本地限速实验证明并行链路有效；Cloudflare 上 8 连接拿到 9 MB/s）。
实际速度由「源 + 链路」决定：本机可达的**国内镜像单连接只有 0.3–1.2 MB/s 且并发上限低**，
所以原版安装链路的实际速度主要取决于 BMCLAPI 当时的负载。

### 10.3 环境坑（复核速度时必须先看）

- **系统代理会截走一切**：本机一度开启 `127.0.0.1:26561` 的系统代理（`ProxyEnable=1`），
  连回环地址都会被代理接管——端到端测试因此拿到 404，测速也一直被压在 ~3.5 MB/s。
  脚本测速前先 `netsh winhttp show proxy` / 查 `HKCU\...\Internet Settings`。
- 关掉代理后，`github.com`、`cdn.modrinth.com` 在本机**直连不可达/长时间停顿**
  （需要代理才能访问国际 CDN），而国内镜像可直连——两种配置各有一半站点通。

### 10.4 回归护栏

- `ChunkedFileDownloaderTests`：分片计划（覆盖性 / 片数上限 / 按 worker 自适应）；
  **本地限速服务器证明并行分片把 1 MB/s 单连接拉到 ≥2.5 MB/s**；不支持 Range 的源自动整文件回退且结果正确。
- `DownloadSourceRegistryTests`：非 Mojang 域名池只留一项、Range 能力按主机记忆、并行度调节器行为（起步保守 / 限流减半冷却 / 按主机隔离 / 不会掉到 0）。
- `DownloadThroughputIntegrationTests`：真实网络实测（默认跳过，`STARCLOUD_IT_DOWNLOAD=1` + `STARCLOUD_IT_DOWNLOAD_URL=...` 手动跑）。
   **已按实测把阈值下调为 2**（[DownloadSource.cs](../../../dotnet/src/Starcloud.Core/Services/DownloadSource.cs#L18-L25)），
   收益约 10 s。关键是**健康度是进程级共享的**（`DownloadSourceRegistry` 由 `DownloadService`
   持有，`AppServices` 全局唯一），所以这笔开销只在每个进程的**首个下载**付一次，
   此后 FastMirror 不再被派片，多源叠加的收益从第二个文件起才完整体现。
   （上表三次测量都是「新进程的第一个文件」，因此 `auto` 仍慢于 `official`。）
   误降级风险由 `RecordSuccess` 立即恢复兜底。
   残留白等若要继续压缩，下一个杠杆是 `SocketsHttpHandler.ConnectTimeout`（只约束建连，
   不影响慢速大文件传输），比下调 `HttpClient.Timeout`（当前 60 s）安全得多。

## 11. 假死源专项（2026-09-22/23）：从「永远转圈」到「换源自救」

### 11.1 故障现象

用户点「下载 1.19.4」后界面一直转圈：**没有字节、没有进度、调度中心里连任务都不出现**。

### 11.2 定位过程（都是可复现的证据，不是猜测）

| 步骤 | 观察 |
|---|---|
| 看进程 | 启动器 CPU 空转（3 秒内 2.27 秒 CPU）、但一条下载连接都没有 |
| 探针跑真实代码路径（`InstallerService.InstallVanillaAsync`） | 卡在**第一个元数据请求**，直到 90 秒取消令牌生效才有反应 |
| curl 直测 BMCLAPI | 302 跳到 P2P 节点；同一 URL 连发 5 次，**3 次 25 秒零响应**、2 次 1.3–1.9 秒成功 |
| curl 测 2 MB `Range` | BMCLAPI 的 `/maven` 节点**一律忽略 Range 回 200**（客户端 jar 的节点则正常给 206） |

根因：元数据请求用「单个首选源 + `_http.Timeout` 无限」，撞上假死节点就永远等下去 ——
既没有字节也没有错误，用户看到的就是「一直转」。

### 11.3 修复

| 改动 | 位置 | 为什么 |
|---|---|---|
| 元数据请求改为**逐源尝试 + 12 秒单源超时**，官方兜底 | `DownloadService.GetStringAsync` / `MetadataTimeout` | 这是"永远转圈"的直接原因；假死源 12 秒内必须换掉 |
| `ProbeSizeAsync` 同样逐源 + 有界 | 同上 | HEAD 探测此前也是无限等 |
| `PostJsonAsync` 加 30 秒上限 | 同上 | 接口卡住不该把 Nova / 登录挂死 |
| 元数据遇到**站点 HTML**（首个非空白字符是 `<`）判为源故障换源 | 同上 | RainYun 这类"200 但不是数据"会把 JSON 解析炸在调用方 |
| 分片 / 整文件重试额度 **≥ 池内源数** | `ChunkedFileDownloader` / `DownloadWholeFileAsync` | 否则重试用完还没轮到官方兜底，整个大文件退化成单连接整文件下载 |
| **源质量预检**：首次分片前并行试 1 字节 Range（每源 10 秒封顶，进程内一次） | `ChunkedFileDownloader.WarmupAsync` | FastMirror 的 TLS 一直是坏的、RainYun 返回站点页面；不预检它们和好源一样靠前，每个大文件都要先白折腾一轮 |
| 「不支持 Range」改为**按「源 + 通道」计数（连续 3 次才判死）** | `DownloadSourceRegistry.RecordRangeIgnored` + `SourceChannel` | 镜像是 302 到随机 P2P 节点，个别节点不认 Range 属正常；一次判死会白丢一个快源（BMCLAPI 的 /maven 不认 Range，不代表它给客户端 jar 分片也不行） |
| 分片并发度取**候选源里最宽松**的历史容忍度 | `ChunkedFileDownloader.DownloadAsync` | 原来按池首（常是被 429 限过的 BMCLAPI = 1 条）收并发，而文件实际由官方 CDN 在扛 → 实测被压到 0.29 MB/s |
| RainYun 退出自动竞速池（仍可在设置里显式选择） | `DownloadSourceRegistry.MirrorKinds` | 它的镜像根下没有 Minecraft 文件，留在池里只会制造"200 但不是数据" |
| 全部命中本地复用时进度显示 7300/3650 → 修正 | `DownloadDownloadBatchAsync` 提前返回分支 | `done` 已含全部文件，`doneFiles` 又加了一遍 |

### 11.4 验收（2026-09-22/23，本机自动跑真实安装链路）

- 原版 `1.19.4` 全量安装：**3650 个文件 / 84 MB 下载完成**，
  `fastutil-8.5.9.jar` = 23,376,043 B、SHA1 `bb7ea75e…d91f8cc5` 与官方清单一致。
- 竞速日志证实两源同时贡献字节：`分片竞速 1.19.4.jar（22.4MB / 12 片 / 4 并发）：Mojang 官方 12.4MB / bmclapi2.bangbang93.com 12.2MB`。
- 换源日志可读：`BMCLAPI 元数据请求失败（12 秒无响应）→ 换下一个源`、
  `源预检：libraries.fastmcmirror.org 不可用（SSL…），本进程内靠后使用`。
- 回归护栏：`MetadataFetchTests`（假死镜像 2 秒内必须失败而不是永远等）、
  `DownloadSourceRegistryTests.某通道连续无视Range后该源只在该通道不能分片`。

**结论**：下载链路不再存在"无字节、无进度、无报错"的死等状态；
实际速度仍由"当刻能连上的源"决定（本机 FastMirror 完全不通、BMCLAPI 的库通道不支持分片且会 429、
官方 CDN 单连接约 0.26 MB/s），这部分不是客户端能改的。
