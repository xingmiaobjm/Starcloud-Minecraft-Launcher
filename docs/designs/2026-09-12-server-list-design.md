# 服务器列表与状态查询设计

日期：2026-09-12
状态：待用户确认
所属：PCL2 平替（服务器列表与状态查询）

## 1. 背景与问题

用户诉求：

> 服务器列表与状态查询：收藏 + 状态 + 一键进服；服务器可绑版本、直接启动；
> 只覆盖 SRV 解析（不做 legacy 回退、不做基岩版）。

现状问题（探查结论）：

1. **代码库中没有任何服务器相关实现**。`Starcloud.Core` / `Starcloud.App` 全无
   ServerList / Server List Ping / SRV 相关代码，唯一与「进服」沾边的字段是
   [InstanceSettings.cs#L43](../../../dotnet/src/Starcloud.Core/Services/InstanceSettings.cs#L43)
   的 `ServerAddress`。
2. **现有「进服参数」实际不生效（既有缺陷）**。版本 JSON 里 `--quickPlayMultiplayer`
   参数项带规则 `{ "action": "allow", "features": { "is_quick_play_multiplayer": true } }`，
   而 [InstallerService.cs](../../../dotnet/src/Starcloud.Core/Services/InstallerService.cs)
   的 `RulesAllow` 对**任何带 `features` 的规则一律 `continue`**（本机 `.minecraft/versions`
   下的 `机械动力航空学整合包` / `Create Aero：New Horizons` / `26.2` 实测确认），
   于是该参数项被整条过滤 → `LaunchOptions.QuickPlayMultiplayer`
   （[GameLauncher.cs#L106](../../../dotnet/src/Starcloud.Core/Services/GameLauncher.cs#L106)）
   填了值也进不去服务器。修法见 §3.5。
3. **每版本「进服地址」已落盘但未接通真实注入**。`InstanceSettings.ServerAddress`
   → `EffectiveLaunch.ServerAddress`（[InstanceSettings.cs#L185](../../../dotnet/src/Starcloud.Core/Services/InstanceSettings.cs#L185)）
   → [HomePage.xaml.cs](../../../dotnet/src/Starcloud.App/Views/HomePage.xaml.cs) 已把它塞进
   `LaunchOptions.QuickPlayMultiplayer`。**本次复用这条链路**，不另建模型，只补齐 §3.5 的注入。
4. **`Starcloud.Core` 零依赖**（纯 `net8.0`、无 `PackageReference`）。.NET 既无 Server List Ping
   也无 SRV 查询 API，第三方库不可引入 → **SLP 与最小 DNS SRV 必须自实现**。
5. **UI 接入点齐备**：导航项（[MainWindow.xaml#L90-L129](../../../dotnet/src/Starcloud.App/MainWindow.xaml#L90-L129)）、
   路由 switch（[MainWindow.xaml.cs](../../../dotnet/src/Starcloud.App/MainWindow.xaml.cs)）、
   空状态组件（[EmptyStateView.xaml.cs](../../../dotnet/src/Starcloud.App/Views/EmptyStateView.xaml.cs)）、
   本地化约定（[Loc.cs](../../../dotnet/src/Starcloud.App/Localization/Loc.cs)）均已确认可复用。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| 功能范围 | **收藏 + 状态查询 + 一键进服** |
| 启动方式 | 服务器可绑 `default_version_id`；未绑则回落 `LauncherSettings.LastVersion` |
| 地址解析 | **只做 `_minecraft._tcp` 的 SRV 解析**；显式带端口 / IPv6 字面量一律直连不走 SRV |
| 协议覆盖 | 只做 Java 版（现代 SLP）；**不做 legacy `0xFE` 回退**、不做基岩版 |
| 第三方依赖 | **零新依赖**，自实现 SLP + 最小 DNS SRV 查询 |
| 状态刷新 | **进页并发查询一次**（并发上限 4），不做定时轮询、不做后台常驻探测 |
| 存储 | `%APPDATA%\starcloud-launcher\servers.json` |

### 与既有实现的偏离（需记录）

1. **`NovaInviteService` 的「拉人即玩」改为总是把地址交给 `GameLauncher`**。原先它自己做
   `SupportsQuickPlay(token.Mc)` 判定，1.20 以下直接放弃进服；版本分派收归 §3.5 后，
   `--server` + `--port` 路径对旧版同样生效。
2. **`InstanceSettings.ServerAddress` 语义从「原样透传」改为「解析后规范化再注入」**
   （无端口自动补 25565，IPv6 走方括号形式）。用户可见行为不变，只是真正生效。

## 3. 组件设计

### 3.1 新增 `ServerEntry.cs`（`Starcloud.Core/Models/`）

```csharp
public sealed class ServerEntry
{
    [JsonPropertyName("id")]                 public string Id { get; set; } = Guid.NewGuid().ToString("N");
    [JsonPropertyName("name")]               public string Name { get; set; } = "";
    [JsonPropertyName("address")]            public string Address { get; set; } = "";   // 用户原始输入：host 或 host:port
    [JsonPropertyName("favored")]            public bool Favored { get; set; }           // 收藏 → 列表置顶
    [JsonPropertyName("default_version_id")] public string? DefaultVersionId { get; set; }
}
```

命名与 [LauncherSettings](../../../dotnet/src/Starcloud.Core/Services/LauncherPaths.cs#L108) 一致用 snake_case。

### 3.2 新增 `ServerListStore.cs`（`Starcloud.Core/Services/`）

```csharp
public sealed class ServerListStore
{
    public ServerListStore(string? filePath = null);   // 默认 LauncherPaths.ServersFile

    public IReadOnlyList<ServerEntry> All { get; }     // 快照：收藏置顶，组内保持写入顺序
    public ServerEntry Add(string name, string address, string? defaultVersionId = null);
    public void Update(ServerEntry entry);
    public void Remove(string id);
    public void Save();
}
```

- 「收藏置顶」由 `All` 排序实现（`Favored` 降序 + 原顺序稳定），不引入显式 `order` 字段。
- 持久化约定照抄 [LauncherSettings](../../../dotnet/src/Starcloud.Core/Services/LauncherPaths.cs#L108)：
  `WriteIndented = true` + `UnsafeRelaxedJsonEscaping`；`Load` 文件不存在/损坏 → 空列表（不抛）。
- 只在 UI 线程访问，内部 `lock` 保护 `List<ServerEntry>` + `Save()` 写入即可，不做并发队列。

### 3.3 新增 `ServerAddress.cs`（`Starcloud.Core/Services/`）

```csharp
public readonly record struct ServerAddress(string Host, int Port)
{
    public const int DefaultPort = 25565;

    /// <summary>解析 host / host:port / [::1]:port；剥掉协议前缀与路径；非法返回 false</summary>
    public static bool TryParse(string? input, out ServerAddress address);

    /// <summary>是否显式带端口（决定要不要走 SRV 解析）</summary>
    public static bool HasExplicitPort(string? input);

    public override string ToString();   // host:port；IPv6 → [host]:port
}
```

解析规则：

1. `Trim()` → 去掉 `minecraft://` / `mc://` 前缀 → 截断第一个 `/` 之后的路径。
2. `[...]` 开头视为 IPv6 字面量，取 `]` 内内容，`]` 之后可选 `:port`。
3. 其余按**最后一个** `:` 切分：右侧能解析成端口则视为端口，否则整串都是 host。
4. 端口范围 `1..65535`；host 非空且不含空白字符，否则 `false`。

### 3.4 新增 `SrvResolver.cs`（`Starcloud.Core/Services/`）

```csharp
public readonly record struct SrvRecord(ushort Priority, ushort Weight, int Port, string Target);

public sealed class SrvResolver
{
    public SrvResolver(TimeSpan? timeout = null, IReadOnlyList<IPAddress>? dnsServers = null);

    /// <summary>查询 _minecraft._tcp.&lt;host&gt;；无记录 / 超时 / 解析失败返回 null</summary>
    public Task<SrvRecord?> ResolveAsync(string host, CancellationToken ct = default);

    /// <summary>DNS 服务器：系统网卡 DHCP DNS → 兜底 223.5.5.5 / 8.8.8.8</summary>
    internal static IReadOnlyList<IPAddress> SystemDnsServers();
}
```

实现要点（手写最小 DNS 报文，UDP only）：

- **查询报文**：12 字节 header（随机 ID、`flags = 0x0100`（RD）、`QDCOUNT = 1`，
  其余计数 0）+ question（host 按 `.` 拆 label、`QTYPE = 33`（SRV）、`QCLASS = 1`（IN））。
- **响应解析**：跳过 header 与 question 后遍历 `ANCOUNT` 条 answer；跳过 NAME（**必须处理
  `0xC0` 压缩指针**）、`TYPE(2) CLASS(2) TTL(4) RDLENGTH(2)`，`TYPE = 33` 时读
  `Priority(2) Weight(2) Port(2) Target(name，可能带压缩指针)`。
- **择优**：取 `Priority` 最小者；同优先级内按 `Weight` 最大的取一个（不做权重轮盘，够用）。
- `Target` 为 `"."`（服务不可用）→ 返回 `null`。
- 响应若置了 `TC`（截断）位 → 不重试 TCP，直接返回 `null`（见 §9 风险 4）。

### 3.5 `GameLauncher` 进服参数显式注入（修 §1.2 的缺陷）

在 [GameLauncher.cs#L177-L186](../../../dotnet/src/Starcloud.Core/Services/GameLauncher.cs#L177-L186)
窗口参数之后追加显式注入，**不再依赖版本 JSON 的 `arguments.game`**：

```csharp
// 一键进服：显式注入（版本 JSON 里的 --quickPlayMultiplayer 项带 features 规则，
// 会被 RulesAllowValue 整条过滤，必须自己加）
if (!string.IsNullOrWhiteSpace(options.QuickPlayMultiplayer)
    && ServerAddress.TryParse(options.QuickPlayMultiplayer, out var join))
{
    if (SupportsQuickPlay(versionId))
    {
        // ≥1.20：quickPlay 需要 host:port 形式（含端口）
        if (!has("--quickPlayMultiplayer"))
        {
            gameArgs.Add("--quickPlayMultiplayer");
            gameArgs.Add(join.ToString());
        }
    }
    else
    {
        // <1.20：退回 --server / --port
        if (!has("--server"))
        {
            gameArgs.Add("--server"); gameArgs.Add(join.Host);
            gameArgs.Add("--port");   gameArgs.Add(join.Port.ToString());
        }
    }
}

/// <summary>版本是否支持 quickPlay 进服（≥1.20）</summary>
public static bool SupportsQuickPlay(string versionId)
{
    var m = System.Text.RegularExpressions.Regex.Match(versionId, @"^1\.(\d+)");
    return m.Success && int.TryParse(m.Groups[1].Value, out var minor) && minor >= 20;
}
```

配套改动：

- 现有局部函数 `has`（[GameLauncher.cs#L161](../../../dotnet/src/Starcloud.Core/Services/GameLauncher.cs#L161)）
  只在「补全必需参数」的 `if` 块内可见 → **提升到方法级**，供注入段共用。
- [NovaInviteService.cs](../../../dotnet/src/Starcloud.Core/Services/NovaInviteService.cs)
  删除本地 `SupportsQuickPlay` 判定，改为**总是**把 `token.Server` 传给
  `LaunchOptions.QuickPlayMultiplayer`，版本分派交给 `GameLauncher`（见 §2 偏离 1）。
- `QuickPlayMultiplayer` 字段语义保持「host 或 host:port 的原始字符串」，不改类型，
  HomePage / NovaInviteService 的调用点无需变动。

### 3.6 新增 `ServerPingService.cs` + `ServerStatus`（`Starcloud.Core/Services/`）

```csharp
public sealed record ServerStatus(
    bool Online,
    string? Motd,
    int OnlinePlayers,
    int MaxPlayers,
    string? VersionName,
    int? Protocol,
    long LatencyMs,
    string? FaviconBase64,   // 已剥掉 "data:image/png;base64," 前缀
    string? Error)           // 中文原文，App 层用 Loc.TCore 反解
{
    public static ServerStatus Offline(string error) =>
        new(false, null, 0, 0, null, null, 0, null, error);
}

public sealed class ServerPingService
{
    public ServerPingService(SrvResolver? srv = null, int timeoutMs = 5000);

    public Action<string>? Log { get; set; }

    /// <summary>地址 → SRV（无显式端口时）→ TCP 握手 + 状态请求 → 解析 JSON → ping/pong 测延迟</summary>
    public Task<ServerStatus> PingAsync(string rawAddress, CancellationToken ct = default);

    /// <summary>批量并发探测，并发上限 4</summary>
    public Task<IReadOnlyList<ServerStatus>> PingManyAsync(
        IEnumerable<string> addresses, CancellationToken ct = default);
}
```

SLP 字节级约定（放 `SlpCodec.cs`，`internal static`，已被 `InternalsVisibleTo` 覆盖 → 可单测）：

```csharp
internal static class SlpCodec
{
    public const int NextStateStatus = 1;
    public const int HandshakeProtocol = -1;   // 「未知协议」：状态查询服务端不校验

    internal static void WriteVarInt(Stream s, int value);
    internal static Task<int> ReadVarIntAsync(Stream s, CancellationToken ct);
    internal static void WriteString(Stream s, string value);          // VarInt 长度 + UTF-8
    internal static Task<string> ReadStringAsync(Stream s, CancellationToken ct);
}
```

一次查询的报文顺序：

1. TCP 连接最终 `host:port`；`timeoutMs`（默认 5s）为建连 + 每次读取的上限。
2. 发 Handshake：`0x00` + VarInt(`HandshakeProtocol`) + String(host) + UInt16(port) + VarInt(`1`)。
3. 发 Status Request：`0x00`（长度 1，无载荷）。
4. 读响应：VarInt 长度 → VarInt 包 ID（应为 `0x00`）→ String(JSON)。
5. 发 `0x01` + long（当前 tick），读回 pong `0x01` + long；**往返耗时即 `LatencyMs`**。
6. Ping 失败但状态已拿到 → 仍算 `Online`，`LatencyMs` 用步骤 3→4 的耗时兜底。

JSON → `ServerStatus` 映射：`version.name` / `version.protocol` / `players.online` /
`players.max` / `description`（走 §3.7）/ `favicon`（剥前缀）。缺字段一律取默认值，不抛。

`HandshakeProtocol` 用 `-1` 的首轮若**握手后立即读到 0 字节**（少数代理按协议号白名单拒连），
用 `767`（1.21.1）重试一次；仍失败才判离线（§9 风险 1）。

### 3.7 新增 `ChatText.cs`（`Starcloud.Core/Services/`）

```csharp
public static class ChatText
{
    /// <summary>把 description（字符串 / chat 组件对象 / 数组）摊平成纯文本；忽略颜色与格式</summary>
    public static string Flatten(JsonElement element);
}
```

规则：`String` 取原文；`Array` 逐项摊平拼接；`Object` 取 `text`（无则取 `translate` 键名）
后再拼 `extra[]`；最后**去掉传统 `§x` 颜色码**（`§` + 后一字符）。`with` 参数不做替换（§9 风险 2）。

### 3.8 路径与服务接线

[LauncherPaths.cs#L31](../../../dotnet/src/Starcloud.Core/Services/LauncherPaths.cs#L31) 之后新增：

```csharp
/// <summary>服务器收藏列表</summary>
public static string ServersFile => Path.Combine(DataDir, "servers.json");
```

[AppServices.cs#L27](../../../dotnet/src/Starcloud.App/Services/AppServices.cs#L27) 附近新增两个只读属性：

```csharp
public ServerListStore Servers { get; }
public ServerPingService ServerPing { get; }
```

构造（[AppServices.cs#L70](../../../dotnet/src/Starcloud.App/Services/AppServices.cs#L70) 附近）：

```csharp
Servers = new ServerListStore();
ServerPing = new ServerPingService();
ServerPing.Log = msg => LauncherLog.Write("server", msg);   // 对齐 Download.Log 的写法
```

### 3.9 UI：新增 `ServersPage`（`Starcloud.App/Views/`）

导航项插在「存档」之后（[MainWindow.xaml#L101](../../../dotnet/src/Starcloud.App/MainWindow.xaml#L101)），
图标沿用同款 `FontIcon` 写法：

```xml
<NavigationViewItem loc:Loc.Text="服务器" Tag="servers">
    <NavigationViewItem.Icon>
        <FontIcon Glyph="&#xE839;" />
    </NavigationViewItem.Icon>
</NavigationViewItem>
```

路由 switch（[MainWindow.xaml.cs](../../../dotnet/src/Starcloud.App/MainWindow.xaml.cs)）加
`"servers" => typeof(ServersPage),`。

页面结构（`Grid` 三行）：

- **标题栏**：`服务器` 标题 + `添加服务器` 按钮 + `刷新全部` 按钮。
- **列表**：`ListView`，每项一张卡片 = 图标（favicon 解码 → `BitmapImage`，失败回落
  `FontIcon`）× 名称 + 地址 / 状态行 × 延迟 + 人数 + 版本 × `启动` 按钮 + 更多菜单。
- **空状态**：无服务器时显示 `EmptyStateView`（`Glyph` / `Title` / `Subtitle` / `ActionText`
  + `ActionClick` 事件，直接复用，不新造控件）。

状态行三态：`查询中`（`ProgressRing`）/ `在线`（绿点 + `{n}/{m} 人在线` + 版本名 + `{latency}ms`）
/ `离线`（灰点 + 错误文案）。

- 行视图模型 `ServerRow : INotifyPropertyChanged`（App 层私有类），承载
  `Entry` / `Status` / `IsPinging`；`Status` 变更即刷新状态行。
- 更多菜单：`编辑` / `收藏`（`取消收藏`）/ `复制地址` / `删除`。
- 添加/编辑用 `ContentDialog`，字段：`名称`（留空默认取地址）/ `地址` /
  `默认版本`（`ComboBox`：`不指定（跟随上次启动）` + 已安装版本 id 列表）。
- **进页刷新**：`OnNavigatedTo` 触发 `PingManyAsync`（并发上限 4），页面级
  `CancellationTokenSource` 在 `OnNavigatedFrom` 取消；**不做定时轮询**。
- 排序：收藏置顶，其余保持 `ServerListStore.All` 顺序。

### 3.10 本地化

中文原文即 key，只需补 [Strings/en-US.json](../../../dotnet/src/Starcloud.App/Strings/en-US.json)：

`服务器`、`添加服务器`、`编辑服务器`、`服务器名称`、`服务器地址`、`默认版本`、
`不指定（跟随上次启动）`、`刷新全部`、`查询中`、`在线`、`离线`、`{0}/{1} 人在线`、
`{0} ms`、`收藏`、`取消收藏`、`复制地址`、`编辑`、`删除`、`启动`、
`还没有服务器`、`点「添加服务器」把常玩的服务器收藏到这里`、
`地址格式不正确`、`名称不能为空`、`无法解析地址`、`连接超时`、`服务器未响应`、`读取超时`。

Core 侧返回的中文错误文案（`ServerStatus.Error`）在 App 层统一过 `Loc.TCore()` 反解。

## 4. 一键进服流程与数据流

```
ServersPage「启动」
  └─ ServerEntry.DefaultVersionId ?? Settings.LastVersion       ← 无则提示选版本
  └─ LaunchAccountService.ResolveAsync(...)                     ← 复用现有身份解析
  └─ InstanceLayout.Resolve(...)                                ← 复用 HomePage 的落地参数
  └─ GameSessionGuard.TryAcquire                                 ← 复用防双开
  └─ Preflight.EnsureReadyAsync(...)                             ← 复用启动前补全
  └─ new LaunchOptions(..., QuickPlayMultiplayer: entry.Address, ...)
       └─ GameLauncher.BuildCommandLineAsync
            └─ ServerAddress.TryParse → ≥1.20 ? --quickPlayMultiplayer host:port
                                              : --server host --port N
  └─ LaunchVersionAsync → Launch
```

服务器**不落盘到版本目录**：`QuickPlayMultiplayer` 只是本次启动参数，
不写回 `InstanceSettings.ServerAddress`（那是「按版本固定进服地址」的语义，由版本设置页维护）。

状态查询数据流：

```
OnNavigatedTo
  └─ ServerPingService.PingManyAsync(entries.Select(e => e.Address))   ← 闸门 4
       └─ PingAsync：ServerAddress.TryParse
            ├─ 有显式端口 → 直连
            └─ 无显式端口 → SrvResolver.ResolveAsync(_minecraft._tcp.host)
                 ├─ 命中 → TcpClient(target:port)
                 └─ 未命中/失败 → 直连 host:25565
       └─ SlpCodec 握手 + 状态请求 + JSON → ServerStatus
  └─ 逐项回填 ServerRow.Status（UI 线程）
```

## 5. 错误处理与一致性

- **地址非法**：`ServerAddress.TryParse` 返回 `false` → 该项直接 `Offline("地址格式不正确")`，
  **不发任何网络请求**；添加对话框在提交时就校验并内联报错。
- **SRV 失败**：任何异常（超时、无记录、报文解析失败、`TC` 置位）一律静默返回 `null`
  → 回落直连 `host:25565`。SRV 只是优化，不构成失败原因。
- **网络错误映射**：`SocketException`/`IOException` → `连接超时` 或 `服务器未响应`；
  读取阶段取消/超时 → `读取超时`。文案在 Core 里生成中文原文，App 层 `Loc.TCore()` 反解。
- **单点失败不影响整页**：`PingManyAsync` 内部对每项 `try/catch`，单项异常不冒泡；
  整页只有 `Task.WhenAll` 的取消会中断。
- **取消**：`PingManyAsync` 的 `CancellationToken` 贯穿建连与读取；离开页面即取消，
  已完成的项保留结果。
- **JSON 缺字段/非法**：`ServerStatus` 各字段取默认值，不抛；`favicon` 解码失败 → `null`。
- **存储**：`servers.json` 损坏 → `Load` 回落空列表并**不覆盖原文件**（等用户下次
  显式增删再写），避免误清空用户数据。
- **并发**：`ServerListStore` 仅 UI 线程访问；ping 结果回填统一 `DispatcherQueue` 派发。

## 6. 不做的事（YAGNI）

- **不做 legacy `0xFE` 握手回退**（1.6 以前的服务器/部分老代理），按用户裁定。
- **不做基岩版（RakNet unconnected ping）状态查询**。
- **不做定时轮询 / 后台常驻刷新 / 状态历史曲线**。
- **不导入其它启动器（PCL2/HMCL 等）的服务器列表**。
- **不做公共推荐服务器源 / 服务器广场**。
- **不做服务器分组、标签、拖拽排序**（只保留「收藏置顶」）。
- **不做进程内 DNS 缓存**（一次进页最多一次 SRV 查询，收益不足）。
- **不做 SRV 的 TCP 截断重试**（§9 风险 4）。
- **不做进服前的协议版本强校验**（§9 风险 8）。

## 7. 单元测试

加入既有 `dotnet/tests/Starcloud.Core.Tests`（xUnit，`Starcloud.Core` 是纯 `net8.0` 可直接引用），
`internal` 成员可测（`InternalsVisibleTo` 已就位）：

1. `ServerAddress.TryParse`：`host` / `host:port` / `minecraft://host:port` / `host/path` /
   `[::1]:25565` / `[::1]` / 端口 `0` 与 `65536` 越界 / 空串与纯空白。
2. `ServerAddress.HasExplicitPort`：有端口 / 无端口 / IPv6 字面量。
3. `SlpCodec` VarInt：`0` / `127` / `128` / `255` / `2147483647` / `-1`（应 5 字节）编码 → 解码往返。
4. `SlpCodec.WriteString` / `ReadString`：长度前缀 + UTF-8（含中文 host）。
5. `ChatText.Flatten`：纯字符串 / `{text}` / `{text,extra[]}` / 数组 / `translate` /
   内嵌 `§a` 颜色码 / 非法 `JsonElement` 类型。
6. **状态 JSON 解析**：真实样例（含 `favicon` data URI）→ `ServerStatus` 各字段，含前缀剥离与缺字段默认值。
7. **`SrvResolver` 报文**：构造含 `0xC0` 压缩指针的假响应 → 得到
   `Priority/Weight/Port/Target`；多答案选最小 `Priority`；`Target = "."` → `null`。
8. `ServerListStore`：`Add` / `Update` / `Remove` / 收藏置顶排序 / 落盘再读回 /
   文件损坏 → 空列表且原文件未被改写。
9. **`ServerPingService` 回环端到端**：`TcpListener` 绑 `127.0.0.1:0` 按 SLP 回包 →
   断言 `Online` / `Motd` / 人数 / 延迟；服务端不回包 → 超时 → `Offline("读取超时")`。
10. **假 DNS**：`UdpClient` 绑回环应答 SRV → `SrvResolver` 注入该地址 →
    断言解析出的 `Port/Target`；注入一个不应答的地址 → 超时 → `null`。

## 8. 实机验收

1. 添加 `mc.hypixel.net` → 状态行出现 MOTD / 在线人数 / 延迟，服务器图标正常显示。
2. 添加一个**依赖 SRV** 的公开服务器（用 `nslookup -type=SRV _minecraft._tcp.<host>` 交叉验证
   其 SRV 端口 ≠ 25565）→ 仍能查到状态，说明 SRV 解析生效。
3. 添加一个不存在的域名 → 显示 `无法解析地址` 离线态，UI 不卡不崩。
4. 绑定已安装的 1.21.x → 点「启动」→ 游戏直接进入该服务器；启动器日志/进程命令行含
   `--quickPlayMultiplayer host:port`。
5. 绑定一个 1.19.x → 点「启动」→ 命令行含 `--server host --port N`，同样进服成功。
6. 重启启动器 → `servers.json` 内容保留，收藏项仍置顶。
7. 断网后进「服务器」页 → 全部 `连接超时`，页面仍可滚动/编辑（4 并发不阻塞 UI）。
8. `dotnet build dotnet\Starcloud.slnx` 0 警告 0 错误；`dotnet test` 全绿。

## 9. 风险与未决

1. **Handshake 协议号 `-1` 可能被少数代理拒连**（按协议白名单过滤的实现）。
   缓解：首轮 `-1` 失败（握手后立即 0 字节）时用 `767` 重试一次（§3.6）；仍失败判离线。
   若实测普遍被拒，回落方案是直接固定用 `767`。
2. **`translate` + `with` 的 MOTD 会显示翻译键名**（§3.7 不做 `with` 替换），属显示差异，
   不影响可用性；后续可补 `%s` 顺序替换。
3. **DNS 服务器枚举可能拿不到**（VPN/虚拟网卡/无 DHCP DNS）。缓解：兜底
   `223.5.5.5` / `8.8.8.8`；两者都不通则放弃 SRV 直连 `host:25565`，依赖 SRV 的服务器会连不上。
4. **UDP DNS 响应 `TC` 截断**（少见，SRV 记录多时）→ 直接放弃 SRV，不重试 TCP。
5. **favicon 非 PNG / 超大**（个别服务器塞入异常 data URI）→ 解码失败回落默认图标，
   不阻塞列表渲染。
6. **服务器绑定版本与服务器协议不匹配会被踢**（§6 不做强校验）。缓解：状态查询里拿到的
   `Protocol` 可作为 UI 提示（后续）；本期靠用户自行绑对版本。
7. **`--server` / `--port` 在 1.20+ 仍可用**，但官方推荐 quickPlay；本期 ≥1.20 一律走
   quickPlay，仅在 quickPlay 注入被占用（`has("--quickPlayMultiplayer")`）时不动手。
8. **进页瞬间 4 并发 × 5s 超时的最坏耗时 ≈ 30s**（服务器数 / 4 × 5s）。
   缓解：每项完成即回填（不做整批等待），用户无需等到全部结束。
