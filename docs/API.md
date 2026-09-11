# API.md — Starcloud.Core API 参考（C#）

> 本文档描述 `dotnet/src/Starcloud.Core` 公开 API。所有异步方法返回 `Task` /
> `Task<T>`，均接受可选 `CancellationToken`；进度通过 `IProgress<T>` 汇报。
> 旧 Rust API（tauri commands）已随 Tauri 架构弃用。

## 1. 路径与设置 — `LauncherPaths` / `LauncherSettings`

`namespace Starcloud.Core.Services`，文件 `Services/LauncherPaths.cs`。

```csharp
public static class LauncherPaths
{
    public static string DataDir { get; }          // %APPDATA%/starcloud-launcher
    public static string SettingsFile { get; }     // launcher.settings.json
    public static string AccountsFile { get; }     // accounts.json
    public static string ManifestCacheFile { get; } // version_manifest_cache.json
    public static string JavaDir { get; }          // java/
    public static string LogsDir { get; }          // logs/
    public static void EnsureDataDir();
}

public sealed class LauncherSettings
{
    public string DotMinecraft { get; set; }        // 游戏目录，默认 %APPDATA%/.minecraft
    public string Language { get; set; }            // 默认 zh_CN
    public int DefaultMinMemoryMb { get; set; }     // 默认 512
    public int DefaultMaxMemoryMb { get; set; }     // 默认 4096
    public string? JavaExecutable { get; set; }     // 为空时使用 PATH 上的 java
    public List<string> ExtraJvmArgs { get; set; }
    public bool UseBuiltinJvmArgs { get; set; }     // 内置 G1GC 优化，默认 true
    public int DownloadConcurrency { get; set; }    // 默认 16
    public int DownloadMaxRetries { get; set; }     // 默认 3
    public bool VersionIsolated { get; set; }       // 默认 true
    public string? LastUsername { get; set; }
    public string? LastVersion { get; set; }
    public string Theme { get; set; }               // system / light / dark

    public static LauncherSettings Load();
    public void Save();
}
```

JSON 字段为蛇形命名（`dot_minecraft`、`download_concurrency`…），
与旧版配置文件兼容。

## 2. 下载引擎 — `DownloadService`

文件 `Services/DownloadService.cs`。

```csharp
public sealed record BatchProgress(
    string Stage,               // 阶段名（如「下载游戏文件」）
    int DoneFiles, int TotalFiles,
    long DoneBytes, long TotalBytes,
    double SpeedBytesPerSec,
    double? EtaSecs)            // null = 暂无法估算
{
    public int Percentage { get; }   // 0-100
}

public sealed class DownloadService
{
    // 单文件下载：.sc-tmp 临时文件 + SHA1 校验 + 次线性退避重试
    public Task DownloadFileAsync(string url, string destFile,
        string? sha1 = null, int maxRetries = 3, CancellationToken ct = default);

    public Task<string> GetStringAsync(string url, CancellationToken ct = default);

    // 并发批量下载：同 Dest 去重、已存在同大小文件跳过、SemaphoreSlim 限流、进度回调
    public Task DownloadBatchAsync(
        IReadOnlyList<(string Url, string Dest, string? Sha1, long Size)> files,
        string stage, int concurrency, int maxRetries,
        IProgress<BatchProgress>? progress, CancellationToken ct = default);

    // Adoptium v3：返回 Java <majorVersion> windows x64 JDK 的 .zip 直链
    public Task<string> GetAdoptiumDownloadUrlAsync(int majorVersion,
        CancellationToken ct = default);
}
```

## 3. 版本清单 — `ManifestService`

文件 `Services/ManifestService.cs`。

```csharp
public sealed class ManifestService
{
    public bool LastLoadFromCache { get; }   // 上次 GetManifestAsync 是否命中缓存

    // 缓存优先（forceRefresh=false 且缓存存在）；失败/缺失回源 piston-meta
    public Task<MinecraftManifest> GetManifestAsync(bool forceRefresh,
        CancellationToken ct = default);

    // 扫描 <dotMinecraft>/versions/*/*/*.json
    public IReadOnlyList<InstalledVersionInfo> ListInstalled(string dotMinecraft);
}

public sealed record InstalledVersionInfo(string Id, long SizeBytes);

public static class InstalledVersionManager
{
    public static void Remove(string dotMinecraft, string versionId); // 递归删除版本目录
}
```

## 4. 安装器 — `InstallerService`

文件 `Services/InstallerService.cs`。

```csharp
public sealed class InstallerService
{
    public InstallerService(DownloadService downloader);

    public string VersionDir(string dotMinecraft, string id);
    public string VersionJsonPath(string dotMinecraft, string id);
    public string VersionJarPath(string dotMinecraft, string id);
    public bool IsInstalled(string dotMinecraft, string id);

    // 原版安装：版本 JSON + client jar + 库 + natives + 全量资源
    public Task InstallVanillaAsync(string versionId, string versionUrl,
        LauncherSettings settings, IProgress<BatchProgress>? progress,
        CancellationToken ct = default);

    // Fabric 安装：profile 合并保存为 fabric-loader-<mc>-<loader>，再装全量内容
    // 要求原版 <mcVersion> 已安装
    public Task InstallFabricAsync(string mcVersion, string loaderVersion,
        LauncherSettings settings, IProgress<BatchProgress>? progress,
        CancellationToken ct = default);

    // 加载版本 JSON；inheritsFrom 存在时递归加载父版本并合并
    public Task<MinecraftVersion> LoadVersionAsync(string dotMinecraft, string id,
        CancellationToken ct = default);

    // 子版本合并到父版本：mainClass 覆盖 / 库追加 / arguments game 追加 + jvm 前插
    public static MinecraftVersion MergeVersions(MinecraftVersion parent, MinecraftVersion child);

    // 库规则判定：os.name == windows（空规则视为允许；allow/deny 顺序生效）
    public static bool RulesAllow(List<OsRule>? rules);
}
```

## 5. 游戏启动 — `GameLauncher`

文件 `Services/GameLauncher.cs`。

```csharp
public sealed record LaunchOptions(
    string VersionId,
    string PlayerName,
    string GameDir,             // 工作目录（版本隔离时为 versions/<id>）
    int MinMemoryMb, int MaxMemoryMb,
    string? JavaExecutable,     // null → "java"（依赖 PATH）
    List<string> ExtraJvmArgs,
    bool UseBuiltinJvmArgs);

public sealed class GameLauncher
{
    public GameLauncher(InstallerService installer);

    // 离线 UUID：MD5("OfflinePlayer:"+name)，按 UUID v3 打补丁（与 Java 一致）
    public static Guid OfflineUuid(string playerName);

    // 构建 "java.exe <jvm args> <mainClass> <game args>" 完整命令行
    // 支持新旧两代参数格式、${var} 替换、内置 G1GC、classpath 拼接
    public Task<string> BuildCommandLineAsync(MinecraftVersion version,
        LaunchOptions options, CancellationToken ct = default);

    // 启动进程（CreateNoWindow，stdout/stderr 重定向并落盘），返回游戏会话
    // GameSession：Pid / LogFile / HasExited / ExitCode / Uptime / Tail(n)
    //             Kill(entireTree) / WaitForExitAsync() / event OutputLine / event Exited(int)
    public GameSession Launch(string commandLine, string workingDirectory, string versionId,
        IReadOnlyDictionary<string, string>? envVars = null);

    // 便捷入口：LoadVersionAsync → BuildCommandLineAsync → Launch
    public Task<GameSession> LaunchVersionAsync(string dotMinecraft, string versionId,
        LaunchOptions options, CancellationToken ct = default);
}
```

## 6. 账户 — `AccountStore` / `MicrosoftAuthService`

文件 `Services/AccountService.cs`。

```csharp
public sealed class StoredAccount
{
    public string Uuid { get; set; }        // 离线：OfflineUuid；微软：profile id
    public string Username { get; set; }
    public string Type { get; set; }        // "offline" | "microsoft"
    public string? AccessToken { get; set; }
    public string? RefreshToken { get; set; }
    public bool IsDefault { get; set; }
}

public sealed class AccountStore
{
    public IReadOnlyList<StoredAccount> Accounts { get; }
    public StoredAccount? Default { get; }

    public static AccountStore Load();
    public void Save();

    public StoredAccount AddOffline(string username);       // 同名去重
    public StoredAccount AddOrUpdateMicrosoft(string uuid,  // 按 uuid 去重，登录后设默认
        string username, string accessToken, string? refreshToken);
    public void Remove(string uuid);                        // 自动迁移默认账户
    public void SetDefault(string uuid);
}

public sealed class MicrosoftAuthService
{
    public record DeviceCodeInfo(string UserCode, string DeviceCode,
        string VerificationUri, int Interval);
    public record LoginResult(string Username, string Uuid,
        string AccessToken, string? RefreshToken);

    // 设备码申请（client_id = 官方启动器 00000000402b5328）
    public Task<DeviceCodeInfo> StartDeviceCodeAsync(CancellationToken ct = default);

    // 单次轮询：State ∈ "pending" | "success" | "error"；success 时 Result 非空
    // 内部完成 XBL → XSTS → login_with_xbox → profile 全链路
    public Task<(string State, string Message, LoginResult? Result)> PollAsync(
        DeviceCodeInfo code, CancellationToken ct = default);
}
```

## 7. Java — `JavaService`

文件 `Services/JavaService.cs`。

```csharp
public sealed record JavaRuntime(
    string Executable,      // java.exe 绝对路径
    int MajorVersion,       // 21 / 17 / 8（兼容 1.8 旧格式）
    string VersionString,   // java -version 首行
    bool Is64Bit,
    string Source);         // "launcher" | "system"

public sealed class JavaService
{
    public JavaService(DownloadService downloader);

    // 启动器已下载（java/<major>/bin/java.exe）+ 系统 PATH/JAVA_HOME 检测
    public Task<List<JavaRuntime>> ListRuntimesAsync(CancellationToken ct = default);

    // 下载 Adoptium JDK 并解压到 java/<major>/（含子目录上提），返回 java.exe
    public Task<string> DownloadRuntimeAsync(int majorVersion,
        IProgress<BatchProgress>? progress, CancellationToken ct = default);

    public static void RemoveRuntime(int majorVersion);
}
```

## 8. Fabric — `FabricService`

文件 `Services/FabricService.cs`。

```csharp
public sealed class FabricService
{
    public FabricService(DownloadService downloader);

    public static string DownloadSourceUrl { get; } // https://meta.fabricmc.net/v2/versions/loader

    // 查询某 MC 版本可用的 loader 版本（列表首项通常为最新）
    public Task<List<FabricLoaderEntry>> ListLoadersAsync(string mcVersion,
        CancellationToken ct = default);
}

public sealed class FabricLoaderEntry
{
    public FabricLoaderVersion Loader { get; set; }
    public string Version { get; }  // 便捷：Loader.Version
}

public sealed class FabricLoaderVersion
{
    public string Version { get; set; }
    public bool Stable { get; set; }
}
```

## 9. 数据模型 — `Starcloud.Core.Models`

文件 `Models/Minecraft.cs`。全部映射 Mojang / Fabric 官方 JSON（蛇形命名）：

| 模型 | 用途 | 关键成员 |
| --- | --- | --- |
| `MinecraftManifest` | version_manifest_v2 | `Latest.Release/Snapshot`、`Versions` |
| `ManifestVersion` | 清单项 | `Id/Type/Url/ReleaseTime`、`Installed`（UI 标记，不序列化） |
| `MinecraftVersion` | 版本 JSON（含 Fabric 合并结果） | `Id/MainClass/MinecraftArguments/Arguments/AssetIndex/Downloads/JavaVersion/Libraries/InheritsFrom` |
| `VersionArguments` / `ArgumentItem` | 新代参数 | `Game`/`Jvm`；`ArgumentItem.Value` 为 `JsonElement`（字符串或数组）+ `Rules` |
| `OsRule` / `OsSpec` | 规则 | `Action`、`Os.Name/Arch`、`Features` |
| `AssetIndexRef` / `AssetIndexFile` / `AssetObject` | 资源 | 索引引用与 objects 字典（`Hash/Size`） |
| `VersionDownloads` / `DownloadableFile` | client jar 与映射 | `Client/ClientMappings`；`Url/Sha1/Size/Path` |
| `Library` / `LibraryDownloads` / `ExtractSpec` | 库 | `Artifact/Classifiers`（natives-windows）、`Extract.Exclude` |
| `JavaVersionRef` | 版本 Java 要求 | `Component`、`MajorVersion` |
| `FabricProfile` | meta.fabricmc.net profile | `Id/InheritsFrom/MainClass/Arguments/Libraries` |

## 10. 错误处理约定

- 网络失败在 `DownloadFileAsync` 内重试后仍失败 → 抛出原始异常（含 SHA1 校验失败消息）。
- JSON 解析失败 → `InvalidOperationException`（中文消息）。
- 找不到必需字段（如 `downloads.client`）→ `InvalidOperationException`。
- UI 层统一 catch 并以 InfoBar 呈现 `ex.Message`。
