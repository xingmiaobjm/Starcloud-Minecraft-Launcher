# 桥接扩展指南（Bridge Extension）

> 更新：2026-11-09　适用版本：桌面端 0.4.0
> 用途：给「要在桥接里加一个新方法」的人（或 AI agent）一份照着做就能过的流程。
> 读这一份之前先看 `PROJECT-MAP.md` 了解整体分层，本文只讲 `Starcloud.Bridge`。

---

## 1. 桥接是什么

```
   Electron / WinUI / Android
            │  stdin 一行一个 JSON 请求
            ▼
   ┌──────────────────────┐
   │  Starcloud.Bridge    │  只做三件事：解析 → 分派 → 回包/发事件
   │  （net8.0 控制台）    │  不含业务逻辑
   └──────────┬───────────┘
              │  直接调用
              ▼
   ┌──────────────────────┐
   │  Starcloud.Core      │  业务逻辑全在这里
   └──────────────────────┘
```

桥接进程由壳端拉起（Linux 端见 `src/main/index.ts` 的 `bridgeCandidates`），
数据根通过环境变量 `STARCLOUD_DATA_DIR` 注入。**桥接里不写业务逻辑**，
新增功能先问一句「这段逻辑该在内核里吗」——答案是几乎总是「是」。

### 1.1 协议

| 方向 | 形状 |
|---|---|
| 请求 | `{"id":1,"method":"catalog.versions","params":{}}` |
| 成功 | `{"id":1,"ok":true,"result":{...}}` |
| 失败 | `{"id":1,"ok":false,"error":"人类可读的原因"}` |
| 事件 | `{"event":"install.progress","params":{...}}` |

- `id` 由调用方给，**必须**原样回传；同一个 `id` 可能收到两条响应（见 §3.3 Streaming）。
- `params` 缺省视为 `{}`。
- 单条请求的异常只影响这一条：解析失败发 `bridge.error` 事件，方法内抛异常回 `ok:false`。
- 输出编码固定 UTF-8 无 BOM，一行一个 JSON，禁止换行。

---

## 2. 目录与职责

`dotnet/src/Starcloud.Bridge/` 下每个文件对应一个领域，**文件名就是领域名**：

| 文件 | 领域 | 成员数 |
|---|---|---|
| `Program.cs` | 入口：服务装配、stdin 主循环、父进程看门狗 | — |
| `MethodRegistry.cs` | 方法表类型（`BridgeMethod` / `BridgeMethodTable` / `BridgeMethodKind`） | — |
| `Program.Methods.cs` | **方法表：全部方法的登记处**（145 条登记） | 4 |
| `Program.Protocol.cs` | `Reply` / `Emit` / `WriteLine` 三个输出原语 | 4 |
| `Program.Catalog.cs` | 版本清单、实例列表、`DescribeInstalled` 等序列化器 | 6 |
| `Program.VersionSettings.cs` | 版本级设置、检查更新、依赖检查 | 5 |
| `Program.VersionContent.cs` | 版本内文件（mods/资源包/光影）增删查 | 5 |
| `Program.VersionOps.cs` | 删除 / 打开目录 / 设默认 / 标记 / 重命名 / 复制 | 6 |
| `Program.VersionPack.cs` | 整合包导出 / 预览 / 导入 | 6 |
| `Program.VersionBackup.cs` | 版本备份的增删查与重建 | 5 |
| `Program.Install.cs` | `install.vanilla` 流式安装 | 2 |
| `Program.ImportPack.cs` | 整合包导入引擎（URL 解析 / 下载 / 解包 / 依赖补齐） | 12 |
| `Program.Content.cs` | 内容库搜索与安装、翻译、整合包安装 | 4 |
| `Program.Skins.cs` | 皮肤导入 / 站内皮肤 / BlessingSkin 6 | 14 |
| `Program.Accounts.cs` | 账户列表、微软与第三方登录 | 15 |
| `Program.Settings.cs` | 全局设置读写与缓存清理 | 5 |
| `Program.Friends.cs` | 好友列表 / 消息 / 群组 / 文件 + SSE 事件循环 | 30 |
| `Program.Multiplayer.cs` | 联机房间、主机、节点 | 13 |
| `Program.Nova.cs` | Nova 对话与计划执行 | 5 |
| `Program.Lab.cs` | SC 实验室：投影、调色板、世界吞噬者 | 13 |
| `Program.Plugins.cs` | 插件列表 / 启停 / 安装 / 卸载 | 8 |
| `Program.PluginHost.cs` | 插件宿主桥（`IPluginHostBridge` 的降级实现） | 17 |
| `Program.Saves.cs` | 存档列表、备份、导入导出 | 10 |
| `Program.Launch.cs` | 启动游戏、运行态采样、Java 列表 | 11 |
| `Program.Runtime.cs` | 运行态 payload 构造 | 3 |
| `Program.Helpers.cs` | 跨领域小工具（路径、类型名、布尔解析） | 11 |
| `Program.Misc.cs` | `ping`、`app.paths`、`download.*`、`shell.openPath` | 5 |

所有文件都是 `public static partial class Program`，共用同一批 `private static` 服务字段
（在 `Program.cs` 顶部声明）。**跨文件直接用字段名即可，不需要传参。**

---

## 3. 加一个方法（主流程）

以「查询某个版本的崩溃报告列表」`version.crashReports` 为例，四步。

### 3.1 写实现

挑一个语义最接近的领域文件；都不合适才新建 `Program.<领域>.cs`。实现签名必须**完全**如下：

```csharp
private static async Task<object?> HandleVersionCrashReportsAsync(TextWriter stdout, JsonElement p)
{
    var versionId = p.GetProperty("versionId").GetString()!;
    var dir = Path.Combine(_settings.DotMinecraft, "versions", versionId, "crash-reports");
    if (!Directory.Exists(dir)) return new { reports = Array.Empty<object>() };

    return new
    {
        reports = Directory.EnumerateFiles(dir, "*.txt")
            .Select(f => new { name = Path.GetFileName(f), size = new FileInfo(f).Length })
            .ToArray(),
    };
}
```

约定：

- **名字**：`Handle` + `PascalCase(方法名按 `.` 和 `_` 分段)` + `Async`。这条规则由 §5 的校验脚本强制。
- **参数**：只从 `p` 取；缺参数用 `TryGetProperty` 兜默认值，别让 `GetProperty` 抛
  `The given key was not present in the dictionary.`（这条错误信息对壳端毫无用处）。
- **返回值**：匿名对象即可，字段名自动转 camelCase（`JsonOpts`）。
- **异常**：直接 `throw new Exception("中文原因")`，主循环负责回 `ok:false`。
  异常信息是给用户看的，要写清「哪里不对 + 该怎么办」。
- **输出**：需要中间进度就用 `Emit(stdout, "事件名", payload)`，不要自己写 stdout。

### 3.2 登记到方法表

只改 `Program.Methods.cs` 一个文件，找到对应分组加一行：

```csharp
t.Request("version.crashReports", HandleVersionCrashReportsAsync);
```

**选哪个 Kind**（这是唯一需要判断的地方）：

| Kind | 适用 | 行为 |
|---|---|---|
| `Request` | 毫秒级：纯内存、纯本地小文件读 | 占着主循环同步执行 |
| `Concurrent` | 会阻塞：网络、扫盘、起进程、解压 | `Task.Run` 后台跑，主循环继续收请求 |
| `Streaming` | 单飞长任务：安装 / 联机核心 / Nova 对话 | 先回 `{accepted:true}`，进度与终态走事件；同一时刻只允许一个 |

判断口诀：**会不会让主循环停住超过一帧？会 → 不是 `Request`。**
`java.list` 扫一遍 JDK 要几百毫秒、`content.search` 要走网络，都必须是 `Concurrent`；
`ping`、`version.settings` 这种纯内存读取用 `Request`。

> 历史教训：拆分前这三个语义分别散落在 `DispatchAsync` 的 switch、`IsLongRunning`
> 的 60 条方法名清单、以及各方法体里。清单不同步就会出现「实现了但调不到」或
> 「该并发的方法阻塞了主循环」这类静默故障，编译器完全帮不上忙。现在只有一处。

### 3.3 Streaming 的额外要求

```csharp
t.Streaming("install.vanilla", "已有安装任务在进行", RunInstallAsync);
```

三个参数：方法名、**占用中时的错误文案**、启动器。启动器签名是
`Task (TextWriter stdout, int id, JsonElement p)`，自己负责发事件和补终态：

```csharp
private static async Task RunXxxAsync(TextWriter stdout, int id, JsonElement p)
{
    try
    {
        // …干活，过程中 Emit(stdout, "xxx.progress", ...)
        Emit(stdout, "xxx.done", new { });
        Reply(stdout, id, ok: true, result: new { done = true });
    }
    catch (Exception e)
    {
        Emit(stdout, "xxx.failed", new { error = e.Message });
        Reply(stdout, id, ok: false, error: e.Message);
    }
}
```

调用方视角：先收到 `{"id":1,"ok":true,"result":{"accepted":true}}`，
然后一串 `xxx.progress` 事件，最后 `xxx.done` 事件 **加上** 同 `id` 的终态响应。
**两条响应同 id 是设计如此**，壳端要能容忍。

单飞状态由方法表维护（`entry.Running` + `entry.Gate`），实现里不要自己加锁。

### 3.4 壳端接入

- TS 类型契约在 Linux 项目的 `src/renderer/src/bridge.ts`（约 40KB，一个方法一段）。
- 调用处通常再包一层 `bridge-content.ts` / `bridge-friends.ts` 之类的领域封装。
- **核对清单**：改完跑一次 `bridge.methods`，确认新方法出现在返回里（§5）。

---

## 4. 加一个领域（新文件）

新建 `Program.<领域>.cs`，骨架固定：

```csharp
using System.Text.Json;
// …按需补 using（网络要 System.Net.Http，压缩要 System.IO.Compression）

namespace Starcloud.Bridge;

public static partial class Program
{
    private static async Task<object?> HandleXxxAsync(TextWriter stdout, JsonElement p)
    {
        await Task.CompletedTask;
        return new { };
    }
}
```

- 命名空间必须是 `Starcloud.Bridge`，类必须是 `public static partial class Program`。
- 服务字段在 `Program.cs` 声明；新领域需要的 Core 服务也在那里 `new` 一次
  （`Main` 里已有的实例优先复用，别重复 `new`）。
- 然后在 `Program.Methods.cs` 里新开一个分组：

```csharp
// ── 崩溃报告（新领域）────────────────────────────────────────
t.Request("version.crashReports", HandleVersionCrashReportsAsync);
```

分组的顺序 = `bridge.methods` 的输出顺序 = 本文档 §2 表格顺序，保持一致便于对照。

---

## 5. 校验（每次改完都跑）

### 5.1 编译

```bash
dotnet build dotnet/src/Starcloud.Bridge/Starcloud.Bridge.csproj -c Debug
```

### 5.2 方法表一致性

```bash
python3 tools/verify-bridge-methods.py
```

输出必须全部为空：重复方法名、登记了但找不到实现、有实现但未登记，末行应为
`✅ 方法表与实现一一对应`。脚本读 `Program.Methods.cs` 的登记与所有 `Program*.cs`
的方法定义做交叉比对，退出码非 0 即失败（可直接挂 CI）。

### 5.3 冒烟

```bash
bash tools/smoke-bridge.sh
```

基线：`ok=19 / fail=3（全为预期：未知方法 + 2 个缺参数）/ event=4`，
回包 id 覆盖 1-18 与 23。**新增方法记得往脚本里加一条**，否则这次改动等于没测。

单条调试可以直接喂 stdin（脚本内部就是这么做的）：

```bash
export STARCLOUD_DATA_DIR="$PWD/.scratch/data"   # 只读家目录下必须，见 §6
echo '{"id":1,"method":"version.crashReports","params":{"versionId":"1.20.1"}}' \
  | dotnet dotnet/src/Starcloud.Bridge/bin/Debug/net8.0/Starcloud.Bridge.dll
```

### 5.4 自省核对

```bash
echo '{"id":1,"method":"bridge.methods","params":{}}' | dotnet <bridge.dll>
```

返回 `{apiVersion, count, methods[{name, kind}]}`。壳端 CI 可以拿这个和 TS 侧的
类型声明做集合比对，取代人工数 switch 分支。

---

## 6. 已踩过的坑

| 坑 | 现象 | 正确做法 |
|---|---|---|
| `/tmp` 在命令间被清空 | 脚本写的临时文件下一条命令读不到 | 临时文件放工作区 `.scratch/` |
| 只读家目录 | 启动即 `IOException: Read-only file system`，退出码 134 | 设 `STARCLOUD_DATA_DIR` |
| NuGet 缓存只读 | restore 失败 / NU1900 警告刷屏 | `export NUGET_PACKAGES=$PWD/.scratch/nuget` |
| `Concurrent` 方法没等回包 | 壳端短超时误判失败 | 已在 `Main` 加 EOF 收尾窗口（最多等 10s）；新增 `Concurrent` 方法无需额外处理 |
| 缺 `using System.IO.Compression` | `ZipArchiveEntry 未包含 ExtractToFile 的定义` | 解包相关文件补 using |
| `params` 里没键 | 返回 `The given key was not present in the dictionary.` | 用 `TryGetProperty` 兜默认值，或抛中文异常 |
| 参数名撞 lambda 变量 | 编译期 `CS0136` | 长任务启动器参数统一叫 `parameters` |

---

## 7. 设计边界

- **桥接不写业务**：文件系统布局、版本解析、下载策略都在 `Starcloud.Core`。
  桥接只做「取参数 → 调 Core → 拼 payload」。
- **payload 是壳端的契约**：字段改名等于破坏兼容，壳端要同步改；
  加字段安全（`DefaultIgnoreCondition = WhenWritingNull`，null 不输出）。
- **不要绕过方法表**：主循环只认 `Methods`，任何「临时 if (method == "xxx")」
  都是把老问题重新引进来。
- **事件名用 `领域.动作`**：`install.progress`、`friends.event`、`multiplayer.changed`。
  同一领域的事件前缀保持一致，壳端才好按前缀订阅。
