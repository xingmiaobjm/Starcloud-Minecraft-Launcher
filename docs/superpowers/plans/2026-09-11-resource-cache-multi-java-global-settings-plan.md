# 资源缓存 · 多 Java · 全局设置实现计划

日期：2026-09-11
设计文档：docs/superpowers/specs/2026-09-11-resource-cache-multi-java-global-settings-design.md（commit fef724b）

构建命令：先 `Stop-Process -Name Starcloud.App -Force -ErrorAction SilentlyContinue`，
再 `dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo`
（每个任务完成后构建验证，目标 0 警告 0 错误）

计划期微修（相对 spec 的实现决定，不改变设计与验收）：

- `ResourceCacheService` 构造依赖 `Func<LauncherSettings>`（与 `LaunchAccountService` 同源惯例），
  `Enabled` 不缓存布尔快照，而是**每次读写时读 `settings().ResourceCacheEnabled`**，
  保证设置页开关即时生效、无需重启。
- 缓存 key 中的 `null` 段统一落为字面量 `any`（如 `modrinth.search.mod.any.1.21.1`）：
  `SearchAsync` 的 `loader` / `gameVersion` 本就是 `string?`，用 `?? "any"` 生成稳定 key。
- `ModrinthService.SearchAsync` / `ManifestService.GetManifestAsync` **不改签名**，
  仅在方法内部加缓存读写；`_cache` 通过构造函数可选参数注入（默认 `null`），
  使既有 6 处 `GetManifestAsync` 调用点与所有 `SearchAsync` 调用点零改动。
- 内存占用条不做轮询：`OnNavigatedTo` 时取一次 `SystemMemory.Snapshot()`。

前置确认（已核实，实现时不再重复探索）：

- `LauncherPaths`（LauncherPaths.cs）：`DataDir`(L10–12)、`ManifestCacheFile`(L33)、
  `EnsureDataDir()`(L39–44，目前只建 `LogsDir`/`JavaDir`/`DataDir`)；**新增 `CacheDir` 于 L33 附近**
- `LauncherSettings`（LauncherPaths.cs:66 起）：`DefaultMinMemoryMb=512`(L75)、`DefaultMaxMemoryMb=4096`(L78)、
  `JavaExecutable`(L81)、`ExtraJvmArgs`(L84)、`UseBuiltinJvmArgs=true`(L87)、
  `DownloadConcurrency=16`(L90)、`DownloadMaxRetries=3`(L93)、`VersionIsolated=true`(L96)、
  `Theme="system"`(L105)、`NovaOverlayEnabled`(L132)、`NovaOverlayX/Y`(L135–139)、
  `Options`(L141–145)、`Load()`(L147–162)、`Save()`(L177–181)；
  **新字段追加点：`NovaOverlayY`(L138–139) 之后、`Options`(L141) 之前**
- `VersionProfile`（InstanceSettings.cs:11–70）：`MemoryMinMb`(L17)、`MemoryMaxMb`(L20)、`JavaPath`(L23)、
  `WindowWidth`(L26)、`WindowHeight`(L29)、`Fullscreen`(L32)、`LastPlayedAt`(L67–69)；
  **档位字段追加点：`LastPlayedAt` 之后、类结束（L70）之前**
- `InstanceLayout.Resolve(dotMc, versionId, profile, settings)`（InstanceSettings.cs:201–222）返回
  `EffectiveLaunch`（L163–179，字段序 `GameRoot, Isolated, MinMem, MaxMem, Java, WindowW, WindowH,
  Fullscreen, ServerAddress, Language, LanguageOverride, ExtraJvmArgs, ExtraGameArgs, PreCmd, PostCmd,
  EnvVars`）；**`Resolve` 全仓库仅 2 处调用点**：`HomePage.xaml.cs:510`、`SavesPage.xaml.cs:397`
- `ManifestService`（89 行）：`ManifestUrl`(L11)、`_downloader`(L13)、`LastLoadFromCache`(L15)、
  ctor(L17)、`GetManifestAsync(bool forceRefresh, ct)`(L20–47)、`ListInstalled(string)`(L50–66)；
  **`GetManifestAsync` 6 处调用点**：`GameDiagnosticsService.cs:72`、`LaunchPreflightService.cs:68`、
  `NovaInviteService.cs:260`、`DownloadPage.xaml.cs:143/572/845`
- `ModrinthService`（161 行）：`ApiBase`(L10)、`_downloader`(L12)、`_categoriesCache`(L16)、
  `SearchAsync(query, projectType, loader, gameVersion, offset=0, limit=20, index="relevance",
  category=null, ct)`(L32–48)
- `JavaService`：`JavaRuntime(Executable, MajorVersion, VersionString, Is64Bit, Source)`(L8–9)、
  `ListRuntimesAsync(ct, officialRuntimeDir=null)`(L18)、`ProbeAsync(exe, ct)`(L113)
- `GameLauncher`：窗口注入(L176–186，`ModernWindowArgs(versionId)` L247–252)、
  `var java = ... ? await ResolveJavaAsync(version, dotMc, ct) : options.JavaExecutable!`(L188–190)、
  `ResolveJavaAsync`(L198–214，`required = version.JavaVersion?.MajorVersion ?? 8`)
- `AppServices`（72 行）：字段(L11–35)、ctor(L37–67)；`Settings = LauncherSettings.Load()`(L40)、
  `Manifest = new ManifestService(Download)`(L42)、`Java = new JavaService(Download)`(L45)、
  `Mods = new ModrinthService(Download)`(L47)、`ReloadSettings()`(L69)、`SaveSettings()`(L71)；
  **`new ManifestService(` / `new ModrinthService(` / `new JavaService(` 全仓库各只有 1 处，均在此文件**
- `SettingsPage.xaml.cs`（140 行）：`_s`(L13)、ctor(L17–40)、`Category_SelectionChanged`(L42–51，
  按 `Tag` 切 `SectionGame/SectionJava/SectionGeneral` 三组)、`DetectJavaAsync`(L53–66)、
  `DownloadJavaAsync(int major)`(L73–93)、`Save_Click`(L124–139)
- `SettingsPage.xaml`（206 行）：根 `Grid MaxWidth="1280" RowSpacing="16"`(L8)；
  `CategoryList`(L33) 含 3 个 `ListViewItem`：`Tag="game"`(L42)/`Tag="java"`(L48)/`Tag="general"`(L54)；
  `SavedBar`(L72)/`ErrorBar`(L73)；`ScrollViewer`(L76)；`SectionGame`(L80，`DotMcBox` L86、
  `MinMemBox` L104、`MaxMemBox` L108、`IsolationDefaultRadio` L122)；
  `SectionJava`(L132，`JavaBox` L140、`JavaProgress` L146、`JavaListText` L147)；
  `SectionGeneral`(L154，`ConcurrencyBox` L162、`ThemeCombo` L166、`OverlaySwitch` L190)；
  底部保存按钮(L201)。**无 `ExtraJvmArgs`/`UseBuiltinJvmArgs`/`DownloadMaxRetries`/默认窗口/环境变量/缓存开关入口**
- `DownloadPage.xaml.cs`（900+ 行）：`PageSize=20`(L21)、`LoadVersionsAsync()`(L139–152)、
  `ApplyVersionFilter()`(L164–198)、`InitCommunityAsync()`(L557–582)、`ResolveLoader()`(L624–632)、
  `SearchAsync(bool reset)`(L688–742)、`SetBusy(bool)`(L744–750)
- `DownloadPage.xaml`：分页 `Grid`(L729–740)，含 `ResultCountText`(L734–735)、`PrevBtn`(L737)、`NextBtn`(L738)
- `VersionDetailPage.xaml.cs`（1067 行）：覆盖态缓存(L37–44)、`LoadSettingsCard`(L114–166，
  内存 L125–130 / Java L132–135 / 窗口 L137–144)、`RefreshHints`(L169–177)、
  `ApplyIsolation`(L535–544)、`Memory_ValueChanged`(L568–575)、`MemoryReset_Click`(L577–592)、
  `ValidateMemory`(L594–607)、`PopulateJavaCombo`(L629–654)、`UpdateJavaHint`(L656–659)、
  `JavaCombo_SelectionChanged`(L661–668)、`JavaReset_Click`(L670–679)、`Window_ValueChanged`(L727–734)、
  `FullscreenRadio_SelectionChanged`(L736–748)、`WindowReset_Click`(L750–767)、`ValidateWindow`(L769–785)、
  `ScheduleSave`(L1002–1006)、`FlushSave()`(L1008–1051)
- `VersionDetailPage.xaml`：内存块(L160–182，`MemoryReset` L164、`MinMemBox` L170–173、`MaxMemBox`
  L174–177、`MemErrorText` L179–181)；Java 块(L184–201)；窗口块(L203–230)
- `HomePage.xaml.cs` 启动链路（L510–522）构造 `LaunchOptions` 时逐字段消费 `EffectiveLaunch`，
  三态改造后**该处零改动**（`Resolve` 内部求值，外部接口不变）

---

## T1：Core · LauncherPaths 新增缓存目录与设置字段

文件：`dotnet/src/Starcloud.Core/Services/LauncherPaths.cs`

1. 新增缓存目录属性（放在 `ManifestCacheFile`(L33) 之后）：

```csharp
public static string CacheDir => Path.Combine(DataDir, "cache");
```

2. `LauncherSettings` 新字段，追加在 `NovaOverlayY`(L138–139) 之后、`Options`(L141) 之前：

```csharp
[JsonPropertyName("resource_cache_enabled")] public bool ResourceCacheEnabled { get; set; } = true;
[JsonPropertyName("default_window_width")]  public int? DefaultWindowWidth  { get; set; } = 854;
[JsonPropertyName("default_window_height")] public int? DefaultWindowHeight { get; set; } = 480;
[JsonPropertyName("default_fullscreen")]    public bool DefaultFullscreen   { get; set; } = false;
[JsonPropertyName("global_env_vars")]       public Dictionary<string, string> GlobalEnvVars { get; set; } = new();
```

3. `EnsureDataDir()`(L39–44) 追加创建缓存目录：

```csharp
Directory.CreateDirectory(CacheDir);
```

验证：构建（0 警告 0 错误）。

## T2：Core · SystemMemory（物理内存快照）

新文件：`dotnet/src/Starcloud.Core/Services/SystemMemory.cs`

```csharp
using System.Runtime.InteropServices;

namespace Starcloud.Core.Services;

/// 物理内存快照与「自动配置」上限计算。
public static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile,
            ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// 返回 (总内存 MB, 已用内存 MB)；调用失败回退 (8192, 4096)。
    public static (long TotalMb, long UsedMb) Snapshot()
    {
        var s = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref s)) return (8192, 4096);
        var total = (long)(s.ullTotalPhys / 1024 / 1024);
        var avail = (long)(s.ullAvailPhys / 1024 / 1024);
        return (total, total - avail);
    }

    /// 自动上限：clamp(物理/4, 2048, 物理*3/4)，向下取整到 512 倍数（MB）。
    public static int AutoMaxMb()
    {
        var total = Snapshot().TotalMb;
        var raw = Math.Clamp(total / 4, 2048, total * 3 / 4);
        return (int)(raw / 512 * 512);
    }
}
```

验证：构建（0 警告 0 错误）。

## T3：Core · ResourceCacheService（磁盘缓存）

新文件：`dotnet/src/Starcloud.Core/Services/ResourceCacheService.cs`

1. 依赖与开关（每次读写实时读设置）：

```csharp
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// 资源列表磁盘缓存：一 key 一文件，文件名 = key 的 SHA1。
public sealed class ResourceCacheService(Func<LauncherSettings> settings)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed record Envelope(DateTime SavedAt, JsonElement Payload);

    public bool Enabled
    {
        get => settings().ResourceCacheEnabled;
        set { /* 开关存在 LauncherSettings，本处不持有副本 */ }
    }
```

> 注：spec §3.6 列出 `Enabled { get; set; }` 仅为语义说明；实现期确认开关只由
> `settings().ResourceCacheEnabled` 决定，故 `Enabled` 只读，`set` 留空以保持接口表面一致。

2. 读写 API：

```csharp
    private static string PathFor(string key)
    {
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(key))).ToLowerInvariant();
        return Path.Combine(LauncherPaths.CacheDir, hash + ".json");
    }

    public bool TryRead<T>(string key, out T? value)
    {
        value = default;
        if (!Enabled) return false;
        var path = PathFor(key);
        if (!File.Exists(path)) return false;
        try
        {
            var env = JsonSerializer.Deserialize<Envelope>(File.ReadAllText(path), Options);
            if (env is null) return false;
            value = env.Payload.Deserialize<T>(Options);
            return value is not null;
        }
        catch { return false; }
    }

    public void Write<T>(string key, T value)
    {
        if (!Enabled) return;
        Directory.CreateDirectory(LauncherPaths.CacheDir);
        var env = new Envelope(DateTime.Now, JsonSerializer.SerializeToElement(value, Options));
        var json = JsonSerializer.Serialize(env, Options);
        var path = PathFor(key);
        var tmp = path + ".tmp";
        File.WriteAllText(tmp, json);
        File.Move(tmp, path, overwrite: true);
    }

    public void ClearAll()
    {
        if (!Directory.Exists(LauncherPaths.CacheDir)) return;
        foreach (var f in Directory.EnumerateFiles(LauncherPaths.CacheDir, "*.json"))
        {
            try { File.Delete(f); } catch { /* 忽略占用 */ }
        }
    }
```

3. 旧版清单缓存迁移（一次）：供 ManifestService 调用。

```csharp
    /// 旧 version_manifest_cache.json → 新 manifest.vanilla 缓存，迁移后删除旧文件。
    public void MigrateLegacyManifest()
    {
        var legacy = LauncherPaths.ManifestCacheFile;
        if (!Enabled || !File.Exists(legacy)) return;
        try
        {
            var json = File.ReadAllText(legacy);
            using var doc = JsonDocument.Parse(json);
            Write("manifest.vanilla", doc.RootElement.Clone());
            File.Delete(legacy);
        }
        catch { /* 迁移失败保留旧文件，下次再试 */ }
    }
```

验证：构建（0 警告 0 错误）。

## T4：Core · VersionProfile 档位字段 + InstanceLayout.Resolve 三态

文件：`dotnet/src/Starcloud.Core/Services/InstanceSettings.cs`

1. `VersionProfile` 新增档位字段，追加在 `LastPlayedAt`(L67–69) 之后、类结束（L70）之前：

```csharp
[JsonPropertyName("memory_mode")] public string? MemoryMode { get; set; }  // global | manual | auto
[JsonPropertyName("java_mode")]   public string? JavaMode   { get; set; }
[JsonPropertyName("window_mode")] public string? WindowMode { get; set; }
```

2. `InstanceLayout` 新增档位推断助手（放在 `Resolve` 之前）：

```csharp
private static string Mode(string? explicitMode, bool hasLegacyValue)
    => explicitMode ?? (hasLegacyValue ? "manual" : "global");
```

3. 重写 `Resolve`(L201–222)：

```csharp
public static EffectiveLaunch Resolve(
    string dotMc, string versionId, VersionProfile profile, LauncherSettings settings)
{
    var isolated = IsIsolated(profile, settings);

    // 内存：global / manual / auto
    var memMode = Mode(profile.MemoryMode, profile.MemoryMaxMb is not null);
    var (minMem, maxMem) = memMode switch
    {
        "auto"   => (512, SystemMemory.AutoMaxMb()),
        "manual" => (profile.MemoryMinMb ?? 512,
                     profile.MemoryMaxMb ?? settings.DefaultMaxMemoryMb),
        _        => (settings.DefaultMinMemoryMb, settings.DefaultMaxMemoryMb),
    };

    // Java：global / manual / auto（auto ⇒ 置空，强制走版本推荐）
    var javaMode = Mode(profile.JavaMode, profile.JavaPath is not null);
    var java = javaMode switch
    {
        "auto"   => null,
        "manual" => profile.JavaPath,
        _        => settings.JavaExecutable,
    };

    // 窗口：global / manual / auto
    var winMode = Mode(profile.WindowMode, profile.WindowWidth is not null);
    var (winW, winH, fullscreen) = winMode switch
    {
        "auto"   => (854, 480, false),
        "manual" => (profile.WindowWidth, profile.WindowHeight, profile.Fullscreen),
        _        => (settings.DefaultWindowWidth, settings.DefaultWindowHeight, settings.DefaultFullscreen),
    };

    return new EffectiveLaunch(
        GameRoot(dotMc, versionId, isolated),
        isolated,
        minMem, maxMem, java, winW, winH, fullscreen,
        profile.ServerAddress,
        settings.Language,
        profile.LanguageOverride,
        [.. settings.ExtraJvmArgs, .. profile.ExtraJvmArgs],
        profile.ExtraGameArgs,
        profile.PreLaunchCommand,
        profile.PostLaunchCommand,
        profile.EnvVars.Count > 0 ? profile.EnvVars : null);
}
```

- `EffectiveLaunch` 元组字段顺序**不变**，`HomePage.xaml.cs:510` / `SavesPage.xaml.cs:397` 零改动。
- 隐式推断保证旧数据（只有 `MemoryMaxMb` / `JavaPath` / `WindowWidth` 而无 mode 字段）自动按 `manual` 解释，无需迁移。

验证：构建（0 警告 0 错误）；`Resolve` 两处调用点无需修改。

## T5：Core · ManifestService 接入缓存

文件：`dotnet/src/Starcloud.Core/Services/ManifestService.cs`

1. 构造函数加可选缓存依赖：

```csharp
private readonly ResourceCacheService? _cache;

public ManifestService(DownloadService downloader, ResourceCacheService? cache = null)
{
    _downloader = downloader;
    _cache = cache;
}
```

2. 改造 `GetManifestAsync(bool forceRefresh, ct)`(L20–47)：

- `EnsureDataDir()` 之后、读文件之前，先做一次旧缓存迁移：

```csharp
_cache?.MigrateLegacyManifest();
```

- 非强制分支改为优先读 `_cache`（缓存类型即 `MinecraftManifest`，L27 现有类型）：

```csharp
if (!forceRefresh && _cache is not null
    && _cache.TryRead<MinecraftManifest>("manifest.vanilla", out var cached)
    && cached is { Versions.Count: > 0 })
{
    LastLoadFromCache = true;
    return cached;
}
```

- 网络成功分支在写旧文件的同时写新缓存：

```csharp
File.WriteAllText(LauncherPaths.ManifestCacheFile, json);
_cache?.Write("manifest.vanilla", manifest);
LastLoadFromCache = false;
return manifest;
```

- 保留既有 `LauncherPaths.ManifestCacheFile` 读写作为降级路径（`_cache is null` 时行为与现在一致）。

验证：构建（0 警告 0 错误）；6 处调用点不改动。

## T6：Core · ModrinthService 接入缓存

文件：`dotnet/src/Starcloud.Core/Services/ModrinthService.cs`

1. 构造函数加可选缓存依赖：

```csharp
private readonly ResourceCacheService? _cache;

public ModrinthService(DownloadService downloader, ResourceCacheService? cache = null)
{
    _downloader = downloader;
    _cache = cache;
}
```

2. `SearchAsync`(L32–48) 内**仅当默认列表**（`offset == 0 && query == "" && category == null
&& index == "relevance"`）时读写缓存：

```csharp
var cacheable = _cache is not null && offset == 0 && string.IsNullOrEmpty(query)
                && category is null && index == "relevance";
var cacheKey = cacheable
    ? $"modrinth.search.{projectType}.{loader ?? "any"}.{gameVersion ?? "any"}"
    : null;

if (cacheKey is not null && _cache!.TryRead<ModrinthSearchResult>(cacheKey, out var cached)
    && cached is not null)
{
    LastSearchFromCache = true;
    return cached;
}

// ...既有 facets / url / GetStringAsync / Deserialize 逻辑不变...
var result = JsonSerializer.Deserialize<ModrinthSearchResult>(json) ?? new();
LastSearchFromCache = false;
if (cacheKey is not null) _cache!.Write(cacheKey, result);
return result;
```

3. 新增公开属性（供下载页显示「缓存」角标）：

```csharp
public bool LastSearchFromCache { get; private set; }
```

验证：构建（0 警告 0 错误）。

## T7：App · AppServices 注册 ResourceCacheService

文件：`dotnet/src/Starcloud.App/Services/AppServices.cs`

1. 字段区（L11–35）新增：

```csharp
public ResourceCacheService ResourceCache { get; }
```

2. ctor 内，在 `Download = new DownloadService()`(L41) 之后、`Manifest = ...`(L42) 之前插入：

```csharp
ResourceCache = new ResourceCacheService(() => Settings);
```

3. 把 `Manifest` / `Java` / `Mods` 三处构造改为注入 `ResourceCache`：

```csharp
Manifest = new ManifestService(Download, ResourceCache);   // L42
Java     = new JavaService(Download);                      // L45 不变（JavaService 不接缓存）
Mods     = new ModrinthService(Download, ResourceCache);   // L47
```

> `Settings` 在 L40 已赋值，`ResourceCache` 的 `() => Settings` 闭包安全。

验证：构建（0 警告 0 错误）。

## T8：App · SettingsPage 扩为 4 组（PCL 风格）

文件：`dotnet/src/Starcloud.App/Views/SettingsPage.xaml`、`SettingsPage.xaml.cs`

1. XAML 左导航 `CategoryList`(L33) 由 3 项扩为 4 项（顺序：游戏 / Java / 下载与资源 / 通用）：

```xml
<ListViewItem Tag="game"    IsSelected="True">游戏</ListViewItem>
<ListViewItem Tag="java">Java</ListViewItem>
<ListViewItem Tag="download">下载与资源</ListViewItem>
<ListViewItem Tag="general">通用</ListViewItem>
```

2. XAML 右侧内容区（`ScrollViewer` L76 内）改造：

- `SectionGame`(L80)：
  - 保留 `DotMcBox`(L86)、`IsolationDefaultRadio`(L122)。
  - 内存：`MinMemBox`(L104) + `MaxMemBox`(L108) 合并为**单滑块** `MaxMemorySlider`
    （整数 GB，`Minimum=1`、`Maximum` 运行时设为 `SystemMemory.AutoMaxMb()/1024`），
    上方标签「最大内存」，右侧数值文本「N GB」。
  - 新增「默认窗口」：`DefaultWidthBox`、`DefaultHeightBox`、`DefaultFullscreenSwitch`。
  - 新增「启动参数」：`ExtraJvmArgsBox`（多行）、`UseBuiltinJvmSwitch`。
  - 新增「全局环境变量」：`GlobalEnvPanel`（`StackPanel`）+ `GlobalEnvAddBtn`。
- 新建 `SectionDownload`（Collapsed）：
  - `ConcurrencyBox`(L162 迁入)、`RetriesBox`（新）、`ResourceCacheSwitch`（新）、
    `ClearCacheBtn`（新）。
- `SectionJava`(L132)：改为圆点单选 `JavaModeRadio`（「自动选择（按版本推荐）」/「手动指定」）+
  蓝色信息条 `JavaWillUseText` + 已检测运行时列表 `JavaListText`(L147) + `JavaBox`(L140，手动模式可见) +
  `DetectJavaBtn` / `BrowseJavaBtn` / `DownloadJavaBtn` / `DownloadJava8Btn`（复用 `JavaProgress` L146）。
- `SectionGeneral`(L154)：保留 `ThemeCombo`(L166)、`OverlaySwitch`(L190)。
- 样式：白卡片 + 1px 描边 + 微阴影；小节标题 13px 700 蓝色；标签左 86px / 控件右；
  胶囊开关 34×19；蓝色左竖线信息条；圆点单选（15px 圆环 + 7px 蓝点）。

3. `SettingsPage.xaml.cs`：

- ctor(L17–40) 回显新增控件：`MaxMemorySlider`（`= s0.DefaultMaxMemoryMb / 1024`）、
  `DefaultWidthBox` / `DefaultHeightBox` / `DefaultFullscreenSwitch`、`ExtraJvmArgsBox`、
  `UseBuiltinJvmSwitch`、`RetriesBox`、`ResourceCacheSwitch`、`GlobalEnvPanel`（按 `GlobalEnvVars` 建行）；
  `JavaModeRadio.SelectedIndex = string.IsNullOrWhiteSpace(s0.JavaExecutable) ? 0 : 1`。
- `Category_SelectionChanged`(L42–51)：`if (SectionGame is null) return;` 保留，新增
  `SectionDownload.Visibility` 分支（`Tag=="download"`）。
- `ClearCache_Click`（新）：`_s.ResourceCache.ClearAll(); SavedBar.Title = "缓存已清空"; SavedBar.IsOpen = true;`
- `Save_Click`(L124–139)：

```csharp
s0.DotMinecraft = ...;
s0.DefaultMaxMemoryMb = (int)MaxMemorySlider.Value * 1024;
s0.DefaultMinMemoryMb = Math.Min(s0.DefaultMinMemoryMb, s0.DefaultMaxMemoryMb);
s0.DefaultWindowWidth  = (int)DefaultWidthBox.Value;
s0.DefaultWindowHeight = (int)DefaultHeightBox.Value;
s0.DefaultFullscreen   = DefaultFullscreenSwitch.IsOn;
s0.JavaExecutable = JavaModeRadio.SelectedIndex == 1 && !string.IsNullOrWhiteSpace(JavaBox.Text)
    ? JavaBox.Text.Trim() : null;
s0.ExtraJvmArgs = /* SplitLines(ExtraJvmArgsBox.Text) */;
s0.UseBuiltinJvmArgs = UseBuiltinJvmSwitch.IsOn;
s0.DownloadConcurrency = (int)ConcurrencyBox.Value;
s0.DownloadMaxRetries  = (int)RetriesBox.Value;
s0.ResourceCacheEnabled = ResourceCacheSwitch.IsOn;
s0.GlobalEnvVars = /* ReadGlobalEnv() */;
s0.Theme = ...; s0.VersionIsolated = IsolationDefaultRadio.SelectedIndex == 0;
s0.Save(); SavedBar.IsOpen = true;
```

- 移除对已删除 `MinMemBox` 的引用；`MinMemBox` 不再存在于 XAML。

验证：构建（0 警告 0 错误）；手动打开设置页切换 4 组、保存后重启确认落盘。

## T9：App · DownloadPage 缓存角标

文件：`dotnet/src/Starcloud.App/Views/DownloadPage.xaml`、`DownloadPage.xaml.cs`

1. XAML 分页 `Grid`(L729–740) 内、`ResultCountText`(L734–735) 旁新增角标：

```xml
<Border x:Name="CacheBadge" Visibility="Collapsed" CornerRadius="9" Padding="8,2"
        Background="{ThemeResource AccentFillColorTertiaryBrush}" VerticalAlignment="Center">
    <TextBlock Text="缓存" FontSize="11" Foreground="{ThemeResource TextOnAccentFillColorPrimaryBrush}" />
</Border>
```

2. `SearchAsync(bool reset)`(L688–742) 成功后：

```csharp
CacheBadge.Visibility = _s.Mods.LastSearchFromCache ? Visible : Collapsed;
```

- 进入搜索前先隐藏：`CacheBadge.Visibility = Collapsed;`。
- 仅默认列表（`ResolveLoader` / `ResolveGameVersion` / `_offset == 0` / `index == "relevance"`
  / 无分类）可能命中缓存；分页与筛选请求 `LastSearchFromCache == false`，角标自然不显示。

3. `LoadVersionsAsync()`(L139–152)：`GetManifestAsync(false)` 命中缓存时
   （`_s.Manifest.LastLoadFromCache == true`）在版本列表上方显示同一 `CacheBadge`
   （或复用 `EmptyHint` 旁位置），后台拉到新内容后 `ApplyVersionFilter()` 时隐藏。

验证：构建（0 警告 0 错误）；断网/清缓存后首次进入下载页 → 角标出现，再次进入 → 仍显示缓存，
手动「清空缓存」后角标消失。

## T10：App · VersionDetailPage 三态 + 内存占用条（PCL 风格）

文件：`dotnet/src/Starcloud.App/Views/VersionDetailPage.xaml`、`VersionDetailPage.xaml.cs`

1. XAML 内存块(L160–182)：

- 删除 `MinMemBox`(L170–173)（最小内存固定 512 MB）。
- 新增 `MemoryModeRadio`：三项单选「跟随全局设置 / 自动配置 / 自定义」。
- 保留 `MaxMemBox`(L174–177)，仅「自定义」档位可见；「自动配置」档位显示
  「自动（约 X GB）」文本 `AutoMemText`。
- 新增内存占用条：双层 7px 圆角条，深蓝＝已用、浅蓝＝本版本分配；
  数值行「已使用 X GB / 总 Y GB」与「本版本 Z GB」，来自 `SystemMemory.Snapshot()` 与 `MaxMemBox.Value`。
- 保留 `MemoryReset`(L164，文案改「恢复跟随」) 与 `MemErrorText`(L179–181)。

2. XAML Java 块(L184–201)、窗口块(L203–230)：各新增 `JavaModeRadio` / `WindowModeRadio`
  （同三项命名），保留 `JavaCombo` / `WidthBox` / `HeightBox` / `FullscreenRadio`，
  仅在「自定义」档位可见；`JavaReset` / `WindowReset` 文案改「恢复跟随」。

3. `VersionDetailPage.xaml.cs`：

- `LoadSettingsCard`(L114–166)：

```csharp
// 内存
var memMode = profile.MemoryMode ?? (profile.MemoryMaxMb is not null ? "manual" : "global");
MemoryModeRadio.SelectedIndex = memMode switch { "auto" => 1, "manual" => 2, _ => 0 };
MaxMemBox.Value = profile.MemoryMaxMb ?? settings.DefaultMaxMemoryMb;
MemoryReset.Visibility = memMode == "global" ? Collapsed : Visible;

// Java
var javaMode = profile.JavaMode ?? (profile.JavaPath is not null ? "manual" : "global");
JavaModeRadio.SelectedIndex = javaMode switch { "auto" => 1, "manual" => 2, _ => 0 };

// 窗口
var winMode = profile.WindowMode ?? (profile.WindowWidth is not null ? "manual" : "global");
WindowModeRadio.SelectedIndex = winMode switch { "auto" => 1, "manual" => 2, _ => 0 };
WidthBox.Value  = profile.WindowWidth  ?? settings.DefaultWindowWidth  ?? 1280;
HeightBox.Value = profile.WindowHeight ?? settings.DefaultWindowHeight ?? 720;
```

- 新增 `MemoryMode_SelectionChanged` / `JavaMode_SelectionChanged` / `WindowMode_SelectionChanged`：
  切换时控制子控件可见性、置脏标记（`_memDirty` / `_javaDirty` / `_winDirty`）、`ScheduleSave()`。
- 新增内存条刷新：`OnNavigatedTo`(L59–64) 中调用一次 `RefreshMemoryBar()`
  （`var (total, used) = SystemMemory.Snapshot();` + 更新条宽与文本），**不轮询**。
- `FlushSave()`(L1008–1051)：

```csharp
profile.MemoryMode = MemoryModeRadio.SelectedIndex switch { 1 => "auto", 2 => "manual", _ => null };
profile.JavaMode   = JavaModeRadio.SelectedIndex   switch { 1 => "auto", 2 => "manual", _ => null };
profile.WindowMode = WindowModeRadio.SelectedIndex switch { 1 => "auto", 2 => "manual", _ => null };

if (profile.MemoryMode == "manual" && _memDirty && ValidateMemory())
{ profile.MemoryMinMb = 512; profile.MemoryMaxMb = (int)MaxMemBox.Value; }
if (profile.MemoryMode == "auto") { profile.MemoryMinMb = null; profile.MemoryMaxMb = null; }
if (profile.MemoryMode is null)   { profile.MemoryMinMb = null; profile.MemoryMaxMb = null; }

if (profile.WindowMode == "manual" && _winDirty && ValidateWindow())
{ profile.WindowWidth = (int)WidthBox.Value; profile.WindowHeight = (int)HeightBox.Value; }
if (profile.WindowMode == "auto") { profile.WindowWidth = null; profile.WindowHeight = null; }
if (profile.WindowMode is null)   { profile.WindowWidth = null; profile.WindowHeight = null; }

// JavaMode=="auto" 时 JavaPath 保持 null，由 Resolve 置空 EffectiveLaunch.Java
if (profile.JavaMode != "manual") profile.JavaPath = null;
```

- `ValidateMemory`(L594–607)：因 `MinMemBox` 已删，改为只用 `MaxMemBox` + `MemErrorText`
  （校验范围 512–131072）。
- `UpdateJavaHint`(L656–659)：改为三态文案（跟随全局 / 自动匹配 / 已指定）。

验证：构建（0 警告 0 错误）；手动切换三档位 → 保存 → 重启应用确认 `version-settings.json` 落盘；
启动游戏确认内存 / Java / 窗口按档位生效。

---

## 任务依赖与顺序

T1 → T2 → T3 → T4 为纯 Core 增量，可依次构建保持绿灯；
T5 / T6 依赖 T3；T7 依赖 T3/T5/T6；T8 / T9 / T10 依赖 T1–T7。

## 验收对照

spec §7 十条验收标准逐条对应：缓存即时渲染 + 后台替换（T5/T6/T9）、缓存开关与清空（T1/T3/T8）、
自动推荐 Java + 手动指定（T4/T10）、游戏管理能力全局化（T8）、版本设置默认对齐全局（T4/T10）、
三态「跟随全局 / 自动 / 自定义」（T4/T8/T10）、内存占用条含已使用（T2/T10）、
PCL 风格设置界面（T8/T10）。
