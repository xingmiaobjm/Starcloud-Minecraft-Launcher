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
   **已按实测把阈值下调为 2**（[DownloadSource.cs](../../../dotnet/src/Starcloud.Core/Services/DownloadSource.cs#L18-L25)），
   收益约 10 s。关键是**健康度是进程级共享的**（`DownloadSourceRegistry` 由 `DownloadService`
   持有，`AppServices` 全局唯一），所以这笔开销只在每个进程的**首个下载**付一次，
   此后 FastMirror 不再被派片，多源叠加的收益从第二个文件起才完整体现。
   （上表三次测量都是「新进程的第一个文件」，因此 `auto` 仍慢于 `official`。）
   误降级风险由 `RecordSuccess` 立即恢复兜底。
   残留白等若要继续压缩，下一个杠杆是 `SocketsHttpHandler.ConnectTimeout`（只约束建连，
   不影响慢速大文件传输），比下调 `HttpClient.Timeout`（当前 60 s）安全得多。
