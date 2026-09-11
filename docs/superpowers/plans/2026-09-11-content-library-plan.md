# 统一资源库 · 下载装载 · 资源管理 · 版本内容管理 实现计划

日期：2026-09-11
设计文档：docs/superpowers/specs/2026-09-11-content-library-design.md（commit 26070db）
所属：PCL2 平替第 A 组（内容管理）

构建命令（每个任务完成后执行）：

```powershell
Stop-Process -Name Starcloud.App -Force -ErrorAction SilentlyContinue
dotnet build dotnet\src\Starcloud.App\Starcloud.App.csproj -v q -nologo
```

目标：0 警告 0 错误。

## 计划期微修（相对 spec 的实现决定，不改变设计与验收）

- **POST 能力**：spec §5.2 需要 `POST /v2/version_files`，但 [DownloadService.cs](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.Core/Services/DownloadService.cs) 现有 `GetStringAsync`(L69–75) 仅支持 GET，且 `_http` 为 private。决定：在 `DownloadService` 新增 `PostJsonAsync(string url, string jsonBody, CancellationToken ct)`（T4），`ContentLibraryService` 通过构造注入的 `DownloadService` 调用。
- **「无法解析」判定放宽**：[ModConflictService.cs](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.Core/Services/ModConflictService.cs) 的 `ReadJar`(L144–186) 对「前置库 / 无元数据 jar」也会置 `Error`（L177–179）。决定：内容卡片状态优先判 `.disabled`，再判 `Error` 非空 → 文案统一为「无法解析」，接受对前置库的轻微误报（spec §6.2 原意即「解析不出身份的 jar 标为无法解析」）。
- **枚举范围**：`ModConflictService.Scan`(L73–88) 只枚举 `*.jar`，**不含 `.jar.disabled`**。内容卡片须自行 `Directory.EnumerateFiles` 全量枚举再按扩展名判定状态，不复用 `Scan` 结果作为列表数据源。
- **加载器/ MC 版本推断**：`NovaInviteService.ResolveLoader`(L313–329) 与 `ResolveMc`(L331–342) 为 private。决定：在 `ContentLibraryService` 内实现等价的私有静态方法，不引用 Nova 服务（避免 Core 内跨服务耦合）。
- **`ContentDirFor` 落点**：spec §4 L190–197 的映射函数放入 `InstanceLayout`（[InstanceSettings.cs](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.Core/Services/InstanceSettings.cs)），与既有 `GameRoot` / `ContentDirs` 同处，供 App 与 Core 共用。

## 前置确认（已核实，实现时不再重复探索）

- **路径与设置**：[LauncherPaths.cs](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.Core/Services/LauncherPaths.cs) `DataDir`(L10–12) / `ManifestCacheFile`(L33) / **`CacheDir`(L35–36)** / `JavaDir`(L38) / `LogsDir`(L40)；`EnsureDataDir()`(L42–48) 末尾为 `Directory.CreateDirectory(CacheDir);`(L47)。→ **新增 `LibraryDir` / `LibraryIndexFile` 于 `CacheDir`(L36) 之后、`JavaDir`(L38) 之前；`Directory.CreateDirectory(LibraryDir);` 追加在 L47 之后。**
- **InstanceLayout**：`ContentDirs`(L198) / `IsIsolated`(L201–207) / `GameRoot`(L210–211) / `NextAvailablePath`(L305–316，`public static`，`stem (n)` 从 n=2 递增，`File.Exists || Directory.Exists` 判存在，**入库重名直接复用**) / `RenameInstance`(L319)；`ContentDirFor` **不存在** → 在 L319 之前新增。
- **JSON 惯例**：全仓库无 source generation。`InstanceSettingsStore.Options`(L91–95) = `WriteIndented + UnsafeRelaxedJsonEscaping`；`Save()`(L129–141) 原子写 = 写 `_file + ".tmp"` 后 `File.Move(temp, _file, overwrite: true)`。`library.json` 沿用此两者。
- **Modrinth**：[ModrinthService.cs](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.Core/Services/ModrinthService.cs) `ApiBase = "https://api.modrinth.com/v2"`(L10)；`ListVersionsAsync`(L77–90) 返回顺序 = 发布时间倒序（spec §5.3 依赖）；`ModrinthVersion`(L144–168) **无 `dependencies`**，`Files`(L162–163) 之后、`PrimaryFile`(L165) 之前为追加点；`ModrinthFile`(L170–186) 为文件末尾（`Sha1` `string?` L184–186）；新类 `ModrinthDependency` 追加在 L186 之后。
- **下载**：`DownloadService.DownloadFileAsync(url, destFile, sha1, maxRetries, ct)`(L37–67) 内含 SHA1 校验与 `.sc-tmp` 原子写；`GetStringAsync`(L69–75)；`_http`(L26–34) `SocketsHttpHandler` + 60s 超时。`DownloadService.cs` 无 `using System.Text;` 且 ImplicitUsings **不含** System.Text → POST 实现须显式 `using System.Text;` 或用 `System.Text.Encoding.UTF8`。
- **冲突扫描**：`ModConflictService.ScanAsync(dotMc, ct)`(L26–27) / `ResolveAsync`(L33–71) / records `ModFileInfo`(含 `Error`) / `ModConflict` / `ModConflictReport`(L11–24)。
- **存档**：`SaveService.ListSaves(gameRoot, versionId, isolated)`(L22) 扫 `<gameRoot>/saves/`，仅列含 `level.dat` 的目录 → `SaveInfo`(L9–17) 含 `FolderName` / `VersionId` / `GameRoot`。
- **已装版本**：`ManifestService.ListInstalled(dotMinecraft)`(L67–83) → `InstalledVersionInfo(Id, SizeBytes)`(L87)。
- **服务容器**：[AppServices.cs](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.App/Services/AppServices.cs) 字段 L11–36，ctor L40–69；`Download = new DownloadService()`(L42)、`ResourceCache`(L43)、`Manifest = new ManifestService(Download, ResourceCache)`(L44)、**`Mods = new ModrinthService(Download, ResourceCache)`(L49)**、`Launcher`(L50)、`Saves`(L24)、`ModConflicts = new ModConflictService()`(L60)。→ **`ContentLibrary` 注册在 L49 之后、L50 之前。**
- **行模型**：[PageRows.cs](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.App/Views/PageRows.cs) 命名空间 `Starcloud.App.Views`(L9)，`using` 含 `Microsoft.UI.Xaml.Media` / `Microsoft.UI.Xaml.Media.Imaging`(L5–6)；`AccountRow`(L75–80) 之后、`VersionRowTemplateSelector`(L83) 之前为追加点；`record` 位置参数风格已被 `SaveRow`(L45–52) / `LocalVersionRow`(L33–42) 使用，可行。
- **下载页**：[DownloadPage.xaml.cs](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.App/Views/DownloadPage.xaml.cs) `_s = AppServices.Instance`(L25) / `PageSize = 20`(L21) / `Sections` 表(L52–59) / `SectionInfo`(L567) / `Install_Click`(L796–847) / `Icon` 构造惯例 `string.IsNullOrEmpty(h.IconUrl) ? null : new BitmapImage(new Uri(h.IconUrl))`(L739) / `CacheBadge` 控制(L747–756)；[DownloadPage.xaml](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.App/Views/DownloadPage.xaml) 已有 `xmlns:local`(L6)，页头 `StackPanel`(L15–20)，主体 `Grid`(L23–27)，主体右侧容器 `Grid Grid.Column="1"`(L87–757)，`ModRow` 模板(L665–721)。
- **版本详情**：[VersionDetailPage.xaml](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.App/Views/VersionDetailPage.xaml) **无 `xmlns:local`**（L1–7 仅 `x:Class`/`xmlns`/`xmlns:x`/`NavigationCacheMode`）；内容卡片 L113–124；[VersionDetailPage.xaml.cs](file:///c:/Users/xingmiao/Documents/trae_projects/starcloud-minecraft-launcher/dotnet/src/Starcloud.App/Views/VersionDetailPage.xaml.cs) `_store = new InstanceSettingsStore(dotMc)`(L77) / `BuildContentRow(gameRoot, dir)`(L181–209) / 内容填充 L101–104 / `FlushSave()`(L1008–1051)。

---

## T1：Core · 路径与目录映射

文件：`dotnet/src/Starcloud.Core/Services/LauncherPaths.cs`、`dotnet/src/Starcloud.Core/Services/InstanceSettings.cs`

1. `LauncherPaths.cs` 新增属性（放在 `CacheDir`(L35–36) 之后、`JavaDir`(L38) 之前）：

```csharp
/// <summary>启动器统一资源库目录</summary>
public static string LibraryDir => Path.Combine(DataDir, "library");

public static string LibraryIndexFile => Path.Combine(LibraryDir, "library.json");
```

2. `EnsureDataDir()`(L42–48) 末尾（`Directory.CreateDirectory(CacheDir);`(L47) 之后）追加：

```csharp
Directory.CreateDirectory(LibraryDir);
```

3. `InstanceSettings.cs` 在 `NextAvailablePath`(L305–316) 之后、`RenameInstance`(L319) 之前，于 `InstanceLayout` 类内新增：

```csharp
/// <summary>资源类别 → 版本内容目录名</summary>
public static string ContentDirFor(string kind) => kind switch
{
    "mod" => "mods",
    "resourcepack" => "resourcepacks",
    "shader" => "shaderpacks",
    "datapack" => "datapacks",
    _ => "mods",
};
```

验证：构建（0 警告 0 错误）。

---

## T2：Core · ContentLibraryService 基础

文件：新增 `dotnet/src/Starcloud.Core/Services/ContentLibraryService.cs`

1. 文件头与命名空间（沿用 `// 中文说明` + `namespace Starcloud.Core.Services;` 惯例）。需要 `using System.Security.Cryptography;`（SHA1）、`using System.Text.Json;`、`using System.Text.Json.Serialization;`。

2. 数据模型（照抄 spec §3.2）：

```csharp
/// <summary>资源库条目</summary>
public sealed class LibraryEntry
{
    [JsonPropertyName("id")]                  public string Id { get; set; } = Guid.NewGuid().ToString("N")[..8];
    /// <summary>mod | resourcepack | shader | datapack | modpack</summary>
    [JsonPropertyName("kind")]                public string Kind { get; set; } = "";
    [JsonPropertyName("file_name")]           public string FileName { get; set; } = "";
    [JsonPropertyName("sha1")]                public string Sha1 { get; set; } = "";
    [JsonPropertyName("size_bytes")]          public long SizeBytes { get; set; }
    [JsonPropertyName("title")]               public string Title { get; set; } = "";
    [JsonPropertyName("icon_url")]            public string? IconUrl { get; set; }
    [JsonPropertyName("author")]              public string? Author { get; set; }
    [JsonPropertyName("project_id")]          public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")]          public string? ModrinthVersionId { get; set; }
    [JsonPropertyName("version_number")]      public string? VersionNumber { get; set; }
    [JsonPropertyName("game_versions")]       public List<string> GameVersions { get; set; } = [];
    [JsonPropertyName("loaders")]             public List<string> Loaders { get; set; } = [];
    [JsonPropertyName("source_url")]          public string? SourceUrl { get; set; }
    [JsonPropertyName("downloaded_at")]       public DateTime DownloadedAt { get; set; } = DateTime.Now;
}

/// <summary>资源库索引（library.json）</summary>
public sealed class LibraryIndex
{
    [JsonPropertyName("entries")] public List<LibraryEntry> Entries { get; set; } = [];
}

/// <summary>入库元信息（来自 Modrinth 或本地文件）</summary>
public sealed record LibraryAdoptInfo(
    string Title, string? IconUrl, string? Author, string? ProjectId,
    string? VersionId, string? VersionNumber, List<string> GameVersions, List<string> Loaders,
    string? SourceUrl);
```

3. 类骨架：

```csharp
/// <summary>启动器统一资源库：下载缓存、入库去重、装载到版本/存档</summary>
public sealed class ContentLibraryService
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly Dictionary<string, string> KindDirs = new()
    {
        ["mod"] = "mods",
        ["resourcepack"] = "resourcepacks",
        ["shader"] = "shaderpacks",
        ["datapack"] = "datapacks",
        ["modpack"] = "modpacks",
    };

    private readonly DownloadService _downloader;
    private readonly ModrinthService _mods;
    private readonly ManifestService _manifest;
    private readonly object _gate = new();
    private LibraryIndex _index;

    public ContentLibraryService(DownloadService downloader, ModrinthService mods, ManifestService manifest)
    {
        _downloader = downloader;
        _mods = mods;
        _manifest = manifest;
        _index = LoadIndex();
    }
```

4. 索引读写（私有无参/带参方法，**所有写操作在 `lock (_gate)` 内**）：

```csharp
/// <summary>读取索引；损坏或缺失时静默重置为空索引（不阻塞使用）</summary>
private static LibraryIndex LoadIndex()
{
    try
    {
        var file = LauncherPaths.LibraryIndexFile;
        if (!File.Exists(file)) return new LibraryIndex();
        return JsonSerializer.Deserialize<LibraryIndex>(File.ReadAllText(file), Options) ?? new LibraryIndex();
    }
    catch { return new LibraryIndex(); }
}

/// <summary>原子写入索引（.tmp + File.Move overwrite）</summary>
private void SaveIndex()
{
    Directory.CreateDirectory(LauncherPaths.LibraryDir);
    var temp = LauncherPaths.LibraryIndexFile + ".tmp";
    File.WriteAllText(temp, JsonSerializer.Serialize(_index, Options));
    File.Move(temp, LauncherPaths.LibraryIndexFile, overwrite: true);
}
```

5. 查询与目录：

```csharp
/// <summary>资源类别 → 库内目录绝对路径（library/&lt;folder&gt;）</summary>
public string DirFor(string kind)
{
    var folder = KindDirs.TryGetValue(kind, out var f) ? f : "mods";
    var dir = Path.Combine(LauncherPaths.LibraryDir, folder);
    Directory.CreateDirectory(dir);
    return dir;
}

/// <summary>列出库内条目（按入库时间倒序）</summary>
public Task<IReadOnlyList<LibraryEntry>> ListAsync(string? kind = null, CancellationToken ct = default)
{
    lock (_gate)
    {
        var list = _index.Entries
            .Where(e => kind is null || e.Kind == kind)
            .OrderByDescending(e => e.DownloadedAt)
            .ToList();
        return Task.FromResult<IReadOnlyList<LibraryEntry>>(list);
    }
}

/// <summary>按 SHA1 查条目</summary>
public LibraryEntry? FindBySha1(string sha1)
{
    if (string.IsNullOrEmpty(sha1)) return null;
    lock (_gate)
        return _index.Entries.FirstOrDefault(e =>
            e.Sha1.Equals(sha1, StringComparison.OrdinalIgnoreCase));
}

/// <summary>按类别 + 文件名查条目</summary>
public LibraryEntry? FindByFileName(string kind, string fileName)
{
    lock (_gate)
        return _index.Entries.FirstOrDefault(e =>
            e.Kind == kind && e.FileName.Equals(fileName, StringComparison.OrdinalIgnoreCase));
}
```

6. `Remove`（仅删库文件 + 索引，不影响已装载副本）：

```csharp
/// <summary>从资源库删除条目（只删库文件与索引，不影响已装载副本）</summary>
public void Remove(string entryId)
{
    lock (_gate)
    {
        var entry = _index.Entries.FirstOrDefault(e => e.Id == entryId);
        if (entry is null) return;
        var path = Path.Combine(DirFor(entry.Kind), entry.FileName);
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 占用中则保留文件，仅摘索引 */ }
        _index.Entries.Remove(entry);
        SaveIndex();
    }
}
```

7. `AdoptAsync`（入库：算 SHA1 → 去重 → 落库）：

```csharp
/// <summary>把已下载的文件收进资源库（去重：同 SHA1 返回既有条目，同名不同 SHA1 加 " (n)"）</summary>
public async Task<LibraryEntry> AdoptAsync(
    string kind, string sourcePath, LibraryAdoptInfo info, CancellationToken ct = default)
{
    var sha1 = await Task.Run(() =>
        Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(sourcePath))), ct);

    if (FindBySha1(sha1) is { } existing)
    {
        try { File.Delete(sourcePath); } catch { }
        return existing;
    }

    var dir = DirFor(kind);
    var target = Path.Combine(dir, Path.GetFileName(sourcePath));
    if (File.Exists(target))
        target = InstanceLayout.NextAvailablePath(target);

    await Task.Run(() => File.Copy(sourcePath, target, overwrite: false), ct);

    var entry = new LibraryEntry
    {
        Kind = kind,
        FileName = Path.GetFileName(target),
        Sha1 = sha1,
        SizeBytes = new FileInfo(target).Length,
        Title = info.Title,
        IconUrl = info.IconUrl,
        Author = info.Author,
        ProjectId = info.ProjectId,
        ModrinthVersionId = info.VersionId,
        VersionNumber = info.VersionNumber,
        GameVersions = info.GameVersions,
        Loaders = info.Loaders,
        SourceUrl = info.SourceUrl,
        DownloadedAt = DateTime.Now,
    };

    lock (_gate) { _index.Entries.Add(entry); SaveIndex(); }

    try { File.Delete(sourcePath); } catch { }
    return entry;
}
```

8. `RescanAsync`（索引不同步的修复入口：扫库目录补齐缺失条目 / 摘除已消失条目）：

```csharp
/// <summary>重扫库目录，修复索引与实际文件的不一致</summary>
public Task<IReadOnlyList<LibraryEntry>> RescanAsync(CancellationToken ct = default)
{
    foreach (var (kind, folder) in KindDirs)
    {
        var dir = Path.Combine(LauncherPaths.LibraryDir, folder);
        if (!Directory.Exists(dir)) continue;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            ct.ThrowIfCancellationRequested();
            var name = Path.GetFileName(file);
            lock (_gate)
            {
                if (_index.Entries.Any(e => e.Kind == kind &&
                        e.FileName.Equals(name, StringComparison.OrdinalIgnoreCase)))
                    continue;
                var sha1 = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(file)));
                _index.Entries.Add(new LibraryEntry
                {
                    Kind = kind,
                    FileName = name,
                    Sha1 = sha1,
                    SizeBytes = new FileInfo(file).Length,
                    Title = Path.GetFileNameWithoutExtension(name),
                    DownloadedAt = File.GetCreationTime(file),
                });
            }
        }
    }

    lock (_gate)
    {
        _index.Entries.RemoveAll(e =>
            !File.Exists(Path.Combine(LauncherPaths.LibraryDir,
                KindDirs.TryGetValue(e.Kind, out var f) ? f : "mods", e.FileName)));
        SaveIndex();
        return Task.FromResult<IReadOnlyList<LibraryEntry>>(_index.Entries.ToList());
    }
}
```

验证：构建（0 警告 0 错误）。

---

## T3：Core · 装载到版本 / 存档

文件：`dotnet/src/Starcloud.Core/Services/ContentLibraryService.cs`

1. 版本 id → 加载器 / MC 版本推断（等价 `NovaInviteService.ResolveLoader`(L313–329) 与 `ResolveMc`(L331–342) 的私有静态方法）：

```csharp
/// <summary>从版本 id 推断加载器：fabric / forge / neoforge / vanilla</summary>
private static string ResolveLoader(string versionId, out string? loaderVersion)
{
    loaderVersion = null;
    if (versionId.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
    {
        var parts = versionId.Split('-');
        if (parts.Length >= 3) loaderVersion = parts[2];
        return "fabric";
    }
    if (versionId.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)) return "forge";
    if (versionId.StartsWith("neoforge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
    return "vanilla";
}

/// <summary>从版本 id 推断 MC 版本</summary>
private static string ResolveMc(string versionId)
{
    if (versionId.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) ||
        versionId.StartsWith("neoforge", StringComparison.OrdinalIgnoreCase))
    {
        var parts = versionId.Split('-');
        return parts.Length >= 2 ? parts[1] : versionId;
    }
    return versionId;
}
```

2. 装载到版本（求 GameRoot → 目标目录 → 同名先删再复制 → 返回落点绝对路径）：

```csharp
/// <summary>把库内条目装载到指定版本的对应内容目录，返回落点绝对路径</summary>
public async Task<string> InstallToVersionAsync(
    LibraryEntry entry, string versionId, CancellationToken ct = default)
{
    var dotMc = AppSettings.Current.DotMinecraft;
    var store = new InstanceSettingsStore(dotMc);
    var profile = store.Get(versionId);
    var isolated = InstanceLayout.IsIsolated(profile, AppSettings.Current);
    var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
    var dir = Path.Combine(gameRoot, InstanceLayout.ContentDirFor(entry.Kind));
    Directory.CreateDirectory(dir);

    var dest = Path.Combine(dir, entry.FileName);
    var src = Path.Combine(DirFor(entry.Kind), entry.FileName);
    await Task.Run(() =>
    {
        if (File.Exists(dest)) File.Delete(dest);
        File.Copy(src, dest, overwrite: true);
    }, ct);
    return dest;
}
```

> 注：`AppSettings` 若不存在，则改为 ctor 注入 `Func<LauncherSettings>`（与 `ResourceCacheService(() => Settings)`(AppServices L43) 同风格）。**实现时以仓库现状为准，优先沿用 `Func<LauncherSettings>` 注入**——修改 ctor 签名为
> `ContentLibraryService(DownloadService downloader, ModrinthService mods, ManifestService manifest, Func<LauncherSettings> settings)`，并把 T2 中所有 `AppSettings.Current` 替换为 `_settings()`。T6 注册时传 `() => Settings`（与 `ResourceCache` 一致）。

3. 装载到存档（数据包 → `<GameRoot>/saves/<存档文件夹>/datapacks/`）：

```csharp
/// <summary>把库内数据包装载到指定版本的某个存档，返回落点绝对路径</summary>
public async Task<string> InstallToSaveAsync(
    LibraryEntry entry, string versionId, string saveFolderName, CancellationToken ct = default)
{
    var dotMc = _settings().DotMinecraft;
    var store = new InstanceSettingsStore(dotMc);
    var profile = store.Get(versionId);
    var isolated = InstanceLayout.IsIsolated(profile, _settings());
    var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
    var dir = Path.Combine(gameRoot, "saves", saveFolderName, "datapacks");
    Directory.CreateDirectory(dir);

    var dest = Path.Combine(dir, entry.FileName);
    var src = Path.Combine(DirFor(entry.Kind), entry.FileName);
    await Task.Run(() =>
    {
        if (File.Exists(dest)) File.Delete(dest);
        File.Copy(src, dest, overwrite: true);
    }, ct);
    return dest;
}
```

验证：构建（0 警告 0 错误）。

---

## T4：Core · Modrinth 依赖模型与哈希反查

文件：`dotnet/src/Starcloud.Core/Services/DownloadService.cs`、`dotnet/src/Starcloud.Core/Services/ModrinthService.cs`

1. `DownloadService.cs` 顶部补 `using System.Text;`；在 `GetStringAsync`(L69–75) 之后新增：

```csharp
/// <summary>POST JSON 并返回响应文本</summary>
public async Task<string> PostJsonAsync(string url, string jsonBody, CancellationToken ct = default)
{
    using var content = new StringContent(jsonBody, Encoding.UTF8, "application/json");
    using var resp = await _http.PostAsync(url, content, ct);
    resp.EnsureSuccessStatusCode();
    return await resp.Content.ReadAsStringAsync(ct);
}
```

2. `ModrinthService.cs`：`ModrinthVersion` 在 `Files`(L162–163) 之后、`PrimaryFile`(L165) 之前新增：

```csharp
[JsonPropertyName("dependencies")] public List<ModrinthDependency> Dependencies { get; set; } = [];
```

3. `ModrinthService.cs` 文件末尾（`ModrinthFile`(L170–186) 之后）新增：

```csharp
/// <summary>Modrinth 版本依赖项</summary>
public sealed class ModrinthDependency
{
    [JsonPropertyName("project_id")]      public string? ProjectId { get; set; }
    [JsonPropertyName("version_id")]      public string? VersionId { get; set; }
    [JsonPropertyName("file_name")]       public string? FileName { get; set; }
    /// <summary>required | optional | incompatible | embedded</summary>
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "";
}
```

4. 在 `ListVersionsAsync`(L77–90) 之后新增批量哈希反查：

```csharp
/// <summary>按 SHA1 批量反查版本（分片 100/批；未命中的哈希不出现在结果字典）</summary>
public async Task<Dictionary<string, ModrinthVersion>> GetVersionsByHashesAsync(
    IReadOnlyCollection<string> sha1s, CancellationToken ct = default)
{
    var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
    if (sha1s.Count == 0) return result;

    foreach (var chunk in sha1s.Chunk(100))
    {
        ct.ThrowIfCancellationRequested();
        var body = JsonSerializer.Serialize(new
        {
            hashes = chunk,
            algorithm = "sha1",
        });
        var json = await _downloader.PostJsonAsync($"{ApiBase}/version_files", body, ct);
        var map = JsonSerializer.Deserialize<Dictionary<string, ModrinthVersion>>(json);
        if (map is null) continue;
        foreach (var (hash, version) in map) result[hash] = version;
    }
    return result;
}
```

> 单点备用 `GET /v2/version_file/{sha1}?algorithm=sha1` 本次不实现（批量接口已覆盖 §5.2 主路径；仅在需要时按同名风格增补）。

验证：构建（0 警告 0 错误）。

---

## T5：Core · 身份反查 / 更新检查 / 依赖检查

文件：`dotnet/src/Starcloud.Core/Services/ContentLibraryService.cs`

1. 追加 records（与 T2 的 records 同处）：

```csharp
/// <summary>Modrinth 身份（哈希反查结果）</summary>
public sealed record ModrinthIdentity(string ProjectId, string VersionId, string VersionNumber,
    List<string> GameVersions, List<string> Loaders);

/// <summary>更新检查结果</summary>
public sealed record UpdateCheckResult(string FileName, string CurrentVersion, string LatestVersion,
    string LatestFileName, string LatestUrl, string? LatestSha1);

/// <summary>依赖检查结果</summary>
public sealed record DependencyReport(List<MissingDependency> Required, List<ModrinthDependency> Optional,
    List<string> IncompatiblePresent);

/// <summary>缺失的必需依赖</summary>
public sealed record MissingDependency(string ProjectId, string? VersionId, string Title, string? FileName);
```

2. 进程内身份缓存（含负缓存）：

```csharp
private readonly Dictionary<string, ModrinthIdentity?> _identityCache = new(StringComparer.OrdinalIgnoreCase);
```

3. `ResolveIdentitiesAsync`：

```csharp
/// <summary>批量反查 SHA1 → Modrinth 身份（进程内缓存，含负缓存）</summary>
public async Task<IReadOnlyDictionary<string, ModrinthIdentity>> ResolveIdentitiesAsync(
    IEnumerable<string> sha1s, CancellationToken ct = default)
{
    var result = new Dictionary<string, ModrinthIdentity>(StringComparer.OrdinalIgnoreCase);
    var pending = new List<string>();
    foreach (var sha1 in sha1s.Distinct(StringComparer.OrdinalIgnoreCase))
    {
        if (string.IsNullOrEmpty(sha1)) continue;
        if (_identityCache.TryGetValue(sha1, out var cached))
        {
            if (cached is not null) result[sha1] = cached;
        }
        else pending.Add(sha1);
    }
    if (pending.Count == 0) return result;

    var found = await _mods.GetVersionsByHashesAsync(pending, ct);
    foreach (var sha1 in pending)
    {
        if (found.TryGetValue(sha1, out var version))
        {
            var identity = new ModrinthIdentity(
                version.ProjectId, version.Id, version.VersionNumber,
                version.GameVersions, version.Loaders);
            _identityCache[sha1] = identity;
            result[sha1] = identity;
        }
        else _identityCache[sha1] = null;
    }
    return result;
}
```

> 需在 `ModrinthVersion` 上确认是否存在 `ProjectId`（`SearchAsync` 的 `ModrinthHit.ProjectId` 有，`ModrinthVersion` 未必有）。若 `ModrinthVersion` 无 `project_id`，T4 中为其补 `[JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";`。

4. `CheckUpdateAsync`（复用 `ListVersionsAsync` 的发布时间倒序，取首个非 alpha，只有一个则取该个）：

```csharp
/// <summary>检查单个文件是否有更新（按 SHA1 反查项目 → 取最新兼容版本）</summary>
public async Task<UpdateCheckResult?> CheckUpdateAsync(
    string filePath, string? gameVersion, string? loader, CancellationToken ct = default)
{
    if (!File.Exists(filePath)) return null;
    var sha1 = await Task.Run(() =>
        Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(filePath))), ct);

    var identities = await ResolveIdentitiesAsync([sha1], ct);
    if (!identities.TryGetValue(sha1, out var identity)) return null;

    var versions = await _mods.ListVersionsAsync(identity.ProjectId, loader, gameVersion, ct);
    if (versions.Count == 0) return null;

    var latest = versions.FirstOrDefault(v =>
        !string.Equals(v.VersionType, "alpha", StringComparison.OrdinalIgnoreCase)) ?? versions[0];
    if (latest.PrimaryFile is not { } file) return null;
    if (string.Equals(latest.VersionNumber, identity.VersionNumber, StringComparison.OrdinalIgnoreCase))
        return null;

    return new UpdateCheckResult(
        Path.GetFileName(filePath), identity.VersionNumber, latest.VersionNumber,
        file.Filename, file.Url, file.Sha1);
}
```

5. `CheckDependenciesAsync`：

```csharp
/// <summary>检查某个 Modrinth 版本在当前版本实例中的依赖满足情况</summary>
public async Task<DependencyReport> CheckDependenciesAsync(
    ModrinthVersion version, string versionId, CancellationToken ct = default)
{
    var required = new List<MissingDependency>();
    var optional = new List<ModrinthDependency>();
    var incompatible = new List<string>();

    var dotMc = _settings().DotMinecraft;
    var store = new InstanceSettingsStore(dotMc);
    var profile = store.Get(versionId);
    var isolated = InstanceLayout.IsIsolated(profile, _settings());
    var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
    var modsDir = Path.Combine(gameRoot, "mods");

    // 已装文件（含 .disabled）→ SHA1 → 身份集合
    var present = new Dictionary<string, ModrinthIdentity>(StringComparer.OrdinalIgnoreCase);
    var projectIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    if (Directory.Exists(modsDir))
    {
        var hashes = new List<(string Path, string Sha1)>();
        foreach (var file in Directory.EnumerateFiles(modsDir, "*.jar*"))
        {
            ct.ThrowIfCancellationRequested();
            var sha1 = await Task.Run(() =>
                Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(file))), ct);
            hashes.Add((file, sha1));
        }
        var identities = await ResolveIdentitiesAsync(hashes.Select(h => h.Sha1), ct);
        foreach (var (_, sha1) in hashes)
        {
            if (identities.TryGetValue(sha1, out var identity))
            {
                present[sha1] = identity;
                projectIds.Add(identity.ProjectId);
            }
        }
    }

    foreach (var dep in version.Dependencies)
    {
        ct.ThrowIfCancellationRequested();
        switch (dep.DependencyType)
        {
            case "required":
                var satisfied = dep.ProjectId is not null && projectIds.Contains(dep.ProjectId);
                if (!satisfied)
                {
                    var title = dep.ProjectId is null ? (dep.FileName ?? "未知依赖")
                        : await TryGetProjectTitleAsync(dep.ProjectId, ct);
                    required.Add(new MissingDependency(dep.ProjectId ?? "", dep.VersionId, title, dep.FileName));
                }
                break;
            case "optional":
                optional.Add(dep);
                break;
            case "incompatible":
                if (dep.ProjectId is not null && projectIds.Contains(dep.ProjectId))
                {
                    var title = await TryGetProjectTitleAsync(dep.ProjectId, ct);
                    incompatible.Add(title);
                }
                break;
        }
    }

    return new DependencyReport(required, optional, incompatible);
}

/// <summary>尽力获取项目名（失败回退为 projectId）</summary>
private async Task<string> TryGetProjectTitleAsync(string projectId, CancellationToken ct)
{
    try
    {
        var hit = await _mods.SearchAsync(projectId, "mod", null, null, 0, 1, "relevance", null, ct);
        return hit.Hits.FirstOrDefault()?.Title ?? projectId;
    }
    catch { return projectId; }
}
```

> 若 `ModrinthService` 已有按 projectId 取项目详情的方法，优先复用，避免多余搜索请求。

验证：构建（0 警告 0 错误）。

---

## T6：App · 注册服务与行模型

文件：`dotnet/src/Starcloud.App/Services/AppServices.cs`、`dotnet/src/Starcloud.App/Views/PageRows.cs`

1. `AppServices.cs` 字段区（`Mods`(L17) 之后）新增：

```csharp
/// <summary>统一资源库</summary>
public ContentLibraryService ContentLibrary { get; }
```

2. ctor 中 `Mods = new ModrinthService(Download, ResourceCache);`(L49) 之后、`Launcher = ...`(L50) 之前新增：

```csharp
ContentLibrary = new ContentLibraryService(Download, Mods, Manifest, () => Settings);
```

3. `PageRows.cs` 在 `AccountRow`(L75–80) 之后、`VersionRowTemplateSelector`(L83) 之前新增：

```csharp
/// <summary>资源管理列表行</summary>
public sealed record LibraryRow(
    string Id, string Kind, string Title, string FileName, string SubText,
    string SizeText, string TimeText, ImageSource? Icon, string KindLabel);

/// <summary>版本内容分区的单个文件行</summary>
public sealed record ContentFileRow(
    string FileName, string SizeText, string StatusText, bool Enabled, string Kind, string FullPath)
{
    public string ToggleVerb => Enabled ? "停用" : "启用";
}
```

验证：构建（0 警告 0 错误）。

---

## T7：App · 下载页装载流程与资源管理面板

文件：`dotnet/src/Starcloud.App/Views/DownloadPage.xaml`、`dotnet/src/Starcloud.App/Views/DownloadPage.xaml.cs`

1. `DownloadPage.xaml` 页头(L15–20) `StackPanel` 改为两列 `Grid`：左列保留原两行文案（`Grid.Column="0"`），右列新增：

```xml
<Button x:Name="LibraryBtn" Grid.Column="1" Content="资源管理"
        HorizontalAlignment="Right" VerticalAlignment="Center" Click="Library_Click" />
```

2. 在主体 `Grid`(L23–27) 内末尾（`Grid.Column="1"` 容器(L87–757) 之后）新增覆盖层：

```xml
<!-- 资源管理覆盖层 -->
<Border x:Name="LibraryPanel" Grid.ColumnSpan="2" Visibility="Collapsed"
        Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
        BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
        BorderThickness="1" CornerRadius="16" Padding="16">
    <Grid RowSpacing="12">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto" />
            <RowDefinition Height="Auto" />
            <RowDefinition Height="*" />
        </Grid.RowDefinitions>
        <Grid Grid.Row="0">
            <TextBlock Text="资源管理" FontSize="20" FontWeight="SemiBold" />
            <Button x:Name="LibraryCloseBtn" Content="关闭" HorizontalAlignment="Right"
                    Click="LibraryClose_Click" />
        </Grid>
        <StackPanel Grid.Row="1" Orientation="Horizontal" Spacing="8">
            <Button x:Name="LibKindAllBtn" Content="全部" Click="LibKind_Click" Tag="" />
            <Button x:Name="LibKindModBtn" Content="Mod" Click="LibKind_Click" Tag="mod" />
            <Button x:Name="LibKindRpBtn" Content="资源包" Click="LibKind_Click" Tag="resourcepack" />
            <Button x:Name="LibKindShaderBtn" Content="光影" Click="LibKind_Click" Tag="shader" />
            <Button x:Name="LibKindDpBtn" Content="数据包" Click="LibKind_Click" Tag="datapack" />
            <Button x:Name="LibKindMpBtn" Content="整合包" Click="LibKind_Click" Tag="modpack" />
            <Button x:Name="LibraryRescanBtn" Content="重扫" Click="LibraryRescan_Click"
                    HorizontalAlignment="Right" />
        </StackPanel>
        <ListView x:Name="LibraryList" Grid.Row="2" SelectionMode="None">
            <ListView.ItemTemplate>
                <DataTemplate x:DataType="local:LibraryRow">
                    <Grid ColumnSpacing="12" Padding="8,4">
                        <Grid.ColumnDefinitions>
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="*" />
                            <ColumnDefinition Width="Auto" />
                            <ColumnDefinition Width="Auto" />
                        </Grid.ColumnDefinitions>
                        <Image Grid.Column="0" Source="{x:Bind Icon}" Width="32" Height="32" />
                        <StackPanel Grid.Column="1">
                            <TextBlock Text="{x:Bind Title}" FontSize="14" FontWeight="SemiBold" />
                            <TextBlock Text="{x:Bind SubText}" FontSize="12" Opacity="0.6" />
                        </StackPanel>
                        <StackPanel Grid.Column="2" VerticalAlignment="Center">
                            <TextBlock Text="{x:Bind KindLabel}" FontSize="12" />
                            <TextBlock Text="{x:Bind SizeText}" FontSize="12" Opacity="0.6" />
                        </StackPanel>
                        <StackPanel Grid.Column="3" Orientation="Horizontal" Spacing="8"
                                    VerticalAlignment="Center">
                            <Button Content="装载" Tag="{x:Bind Id}" Click="LibraryInstall_Click"
                                    MinWidth="72" />
                            <Button Content="删除" Tag="{x:Bind Id}" Click="LibraryRemove_Click"
                                    MinWidth="72" />
                        </StackPanel>
                    </Grid>
                </DataTemplate>
            </ListView.ItemTemplate>
        </ListView>
    </Grid>
</Border>
```

3. `DownloadPage.xaml.cs` 新增状态字段与面板开关：

```csharp
private string _libKind = "";
private bool _libLoaded;

private async void Library_Click(object sender, RoutedEventArgs e)
{
    LibraryPanel.Visibility = Visibility.Visible;
    if (!_libLoaded) { await RefreshLibraryAsync(); _libLoaded = true; }
}

private void LibraryClose_Click(object sender, RoutedEventArgs e)
    => LibraryPanel.Visibility = Visibility.Collapsed;

private async void LibKind_Click(object sender, RoutedEventArgs e)
{
    _libKind = (sender as Button)?.Tag as string ?? "";
    await RefreshLibraryAsync();
}

private async void LibraryRescan_Click(object sender, RoutedEventArgs e)
{
    await _s.ContentLibrary.RescanAsync();
    await RefreshLibraryAsync();
}

private async Task RefreshLibraryAsync()
{
    var entries = await _s.ContentLibrary.ListAsync(_libKind);
    LibraryList.ItemsSource = entries.Select(e => new LibraryRow(
        e.Id, e.Kind, string.IsNullOrEmpty(e.Title) ? e.FileName : e.Title,
        e.FileName,
        e.Author is null ? e.FileName : $"{e.Author} · {e.FileName}",
        FormatSize(e.SizeBytes), FormatRelative(e.DownloadedAt),
        string.IsNullOrEmpty(e.IconUrl) ? null : new BitmapImage(new Uri(e.IconUrl)),
        KindLabel(e.Kind))).ToList();
}

private static string KindLabel(string kind) => kind switch
{
    "mod" => "Mod", "resourcepack" => "资源包", "shader" => "光影",
    "datapack" => "数据包", "modpack" => "整合包", _ => kind,
};
```

> `FormatSize` / `FormatRelative` / `FormatCount` 若页面内已有同类辅助，直接复用（`FormatCount`/`FormatRelative` 已存在于 L739 区块附近）；`FormatSize` 若无则新增按 B/KB/MB/GB 的简单格式化。

4. `LibraryInstall_Click`：版本选择弹窗（多选已安装版本 + 「暂不装载（仅入库）」= 返回**空列表**；取消 = `null`）：

```csharp
private async void LibraryInstall_Click(object sender, RoutedEventArgs e)
{
    if (sender is not Button btn || btn.Tag is not string id) return;
    var entry = (await _s.ContentLibrary.ListAsync()).FirstOrDefault(x => x.Id == id);
    if (entry is null) return;

    if (entry.Kind == "modpack")
    {
        OkBar.Message = "整合包请在下载页使用「下载」按钮走新建版本流程。";
        OkBar.IsOpen = true;
        return;
    }

    var versions = await PickVersionsAsync(entry.Kind, string.IsNullOrEmpty(entry.Title) ? entry.FileName : entry.Title);
    if (versions is null) return;   // 取消
    if (versions.Count == 0)
    {
        OkBar.Message = $"{entry.FileName} 已保留在资源库（未装载）。";
        OkBar.IsOpen = true;
        return;
    }

    foreach (var versionId in versions)
    {
        if (entry.Kind == "datapack")
        {
            var save = await PickSaveAsync(versionId);
            if (save is null) continue;
            await _s.ContentLibrary.InstallToSaveAsync(entry, versionId, save);
        }
        else
        {
            await _s.ContentLibrary.InstallToVersionAsync(entry, versionId);
        }
    }
    OkBar.Message = $"{entry.FileName} 已装载到 {versions.Count} 个版本。";
    OkBar.IsOpen = true;
    await RefreshLibraryAsync();
}
```

5. `LibraryRemove_Click`：

```csharp
private async void LibraryRemove_Click(object sender, RoutedEventArgs e)
{
    if (sender is not Button btn || btn.Tag is not string id) return;
    _s.ContentLibrary.Remove(id);
    await RefreshLibraryAsync();
}
```

6. `PickVersionsAsync(kind, title)`：`ContentDialog` + 多选 `ListView`（数据源 `_s.Manifest.ListInstalled(_s.Settings.DotMinecraft)`）+ 三个按钮「装载」（Primary，返回选中列表）/「暂不装载（仅入库）」（Secondary，返回空列表）/「取消」（Close，返回 null）。返回类型 `Task<List<string>?> `。

7. `PickSaveAsync(versionId)`：`ContentDialog` + 单选 `ListView`，数据源按 `InstanceLayout` 求 GameRoot 后 `_s.Saves.ListSaves(gameRoot, versionId, isolated)`；返回 `Task<string?>`（选中 `SaveInfo.FolderName`，取消 `null`）。无存档时给出提示并返回 `null`。

8. `Install_Click`(L796–847) 改为 7 步流程：
   1. 取版本（`ListVersionsAsync` 首个非 alpha，只有一个则取该个），校验 `PrimaryFile`；
   2. 下载到库临时目录 `Path.Combine(LauncherPaths.LibraryDir, ".tmp", file.Filename)`（`DownloadFileAsync`，带 `file.Sha1`）；
   3. `.mrpack` → `AdoptAsync("modpack", tmp, ...)` 归档 → `InstallMrPackAsync(库内路径)` → 完成；
   4. 其余类别 → 用 `h.Title` / `h.IconUrl` / `h.Author` / `row.ProjectId` / `version.Id` / `version.VersionNumber` / `version.GameVersions` / `version.Loaders` / `file.Url` 组装 `LibraryAdoptInfo` → `AdoptAsync(info.Tag 对应 kind, tmp, info)`；
   5. `PickVersionsAsync(kind, title)`：取消 → 结束（文件已在库）；空列表 → 提示「已入库，未装载」；
   6. 选中列表非空 → `datapack` 走 `PickSaveAsync` + `InstallToSaveAsync`，其余走 `InstallToVersionAsync`；
   7. 完成后（Mod 类别）触发 T9 的依赖检查提示。

9. 下载的 `.tmp` 目录在 `AdoptAsync` 后被删空（`AdoptAsync` 内部删除源文件）→ 无需额外清理。

验证：构建（0 警告 0 错误）；手动执行 spec §10 的入库去重 / 多版本装载 / 暂不装载 / 数据包四条。

---

## T8：App · 版本内容卡片升级

文件：`dotnet/src/Starcloud.App/Views/VersionDetailPage.xaml`、`dotnet/src/Starcloud.App/Views/VersionDetailPage.xaml.cs`

1. `VersionDetailPage.xaml` L1 追加命名空间（照抄 `DownloadPage.xaml` L6）：

```xml
xmlns:local="using:Starcloud.App.Views"
```

2. 内容卡片(L113–124) 整块替换为：卡片头 + 「检查更新」「冲突扫描」按钮 + 三个分区的行容器 + 拖拽支持：

```xml
<Border Background="{ThemeResource CardBackgroundFillColorDefaultBrush}"
        BorderBrush="{ThemeResource CardStrokeColorDefaultBrush}"
        BorderThickness="1" CornerRadius="16" Padding="16"
        AllowDrop="True" DragOver="Content_DragOver" Drop="Content_Drop">
    <StackPanel Spacing="12">
        <Grid>
            <TextBlock Text="内容" FontSize="16" FontWeight="SemiBold" />
            <StackPanel Orientation="Horizontal" Spacing="8" HorizontalAlignment="Right">
                <Button x:Name="CheckUpdateBtn" Content="检查更新" FontSize="12"
                        Padding="10,2,10,2" Click="CheckUpdate_Click" />
                <Button x:Name="ScanConflictBtn" Content="冲突扫描" FontSize="12"
                        Padding="10,2,10,2" Click="ScanConflict_Click" />
            </StackPanel>
        </Grid>
        <TextBlock FontSize="12" Foreground="{ThemeResource TextFillColorSecondaryBrush}"
                   Text="可直接把 mods / 资源包 / 光影 / 数据包文件拖到此处安装；停用仅改名为 .disabled，可随时还原。" />
        <InfoBar x:Name="ContentInfoBar" IsOpen="False" IsClosable="True" />
        <StackPanel x:Name="ModSection" Spacing="4" />
        <StackPanel x:Name="PackSection" Spacing="4" />
        <StackPanel x:Name="SaveSection" Spacing="4" />
    </StackPanel>
</Border>
```

3. `VersionDetailPage.xaml.cs` 替换 L101–104 的内容填充：

```csharp
// 内容：Mod / 资源包光影 / 存档三分区
ContentPanelFill(gameRoot);
```

新增方法组（约 120 行）：

```csharp
/// <summary>填充三个内容分区</summary>
private void ContentPanelFill(string gameRoot)
{
    ModSection.Children.Clear();
    PackSection.Children.Clear();
    SaveSection.Children.Clear();

    // Mods（含 .disabled）
    var modsDir = Path.Combine(gameRoot, "mods");
    ModSection.Children.Add(SectionHeader("Mod 模组", modsDir));
    foreach (var row in BuildFileRows(modsDir, "mod")) ModSection.Children.Add(BuildFileRow(row));
    ModSection.Children.Add(OpenDirRow(modsDir));

    // 资源包 + 光影
    foreach (var (dir, kind, label) in new[]
    {
        ("resourcepacks", "resourcepack", "资源包"),
        ("shaderpacks", "shader", "光影包"),
    })
    {
        var path = Path.Combine(gameRoot, dir);
        PackSection.Children.Add(SectionHeader(label, path));
        foreach (var row in BuildFileRows(path, kind)) PackSection.Children.Add(BuildFileRow(row));
        PackSection.Children.Add(OpenDirRow(path));
    }

    // 存档（数据包）
    var savesDir = Path.Combine(gameRoot, "saves");
    SaveSection.Children.Add(SectionHeader("存档", savesDir));
    if (Directory.Exists(savesDir))
    {
        foreach (var save in Directory.EnumerateDirectories(savesDir))
        {
            var dpDir = Path.Combine(save, "datapacks");
            var header = new TextBlock
            {
                Text = Path.GetFileName(save), FontSize = 13, FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 6, 0, 0),
            };
            SaveSection.Children.Add(header);
            foreach (var row in BuildFileRows(dpDir, "datapack")) SaveSection.Children.Add(BuildFileRow(row));
        }
    }
    SaveSection.Children.Add(OpenDirRow(savesDir));
}
```

`BuildFileRows(dir, kind)`：`Directory.Exists(dir)` 为假返回空；否则 `Directory.EnumerateFiles(dir, "*", TopDirectoryOnly)`，跳过目录，按扩展名/`.disabled` 判定：

```csharp
/// <summary>枚举目录内文件并生成行模型（含 .disabled）</summary>
private static List<ContentFileRow> BuildFileRows(string dir, string kind)
{
    var rows = new List<ContentFileRow>();
    if (!Directory.Exists(dir)) return rows;
    foreach (var file in Directory.EnumerateFiles(dir))
    {
        var name = Path.GetFileName(file);
        var enabled = !name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
        var display = enabled ? name : name[..^".disabled".Length];
        var size = new FileInfo(file).Length;
        rows.Add(new ContentFileRow(
            display, FormatSize(size),
            enabled ? "启用" : "已停用", enabled, kind, file));
    }
    return rows.OrderBy(r => r.FileName, StringComparer.OrdinalIgnoreCase).ToList();
}
```

`BuildFileRow(ContentFileRow row)`：动态构造横向 `StackPanel`（`FontIcon` `\uE8B7` + 文件名 + 大小 + 状态 + 「启用/停用」按钮 + 「删除」按钮）；`ToggleVerb` 由行模型提供。停用/启用 = 对 `FullPath` 改名（追加/去掉 `.disabled`，目标已存在则先删）：

```csharp
/// <summary>停用 / 启用（改名 .disabled，不删除）</summary>
private void ToggleFile(string fullPath, bool enabled)
{
    var target = enabled ? fullPath + ".disabled" : fullPath[..^".disabled".Length];
    if (File.Exists(target)) File.Delete(target);
    File.Move(fullPath, target, overwrite: true);
    RefreshContentPanel();
}
```

「无法解析」状态：Mod 类别调用一次 `_s.ModConflicts.ScanAsync(dotMc)` 得到 `ModFileInfo` 列表，按 `FileName` 匹配；仅当匹配到且 `Error` 非空时覆盖状态文案为「无法解析」（未匹配到（如 `.disabled`）保持「启用/已停用」）。

`CheckUpdate_Click`：遍历 `ModSection`/`PackSection`/`SaveSection` 的行模型（缓存到 `_contentRows`），对每个 `Enabled` 行 `await _s.ContentLibrary.CheckUpdateAsync(fullPath, mcVersion, loader)`，非空结果汇总到 `ContentInfoBar.Message`（「N 个文件可更新：a、b…」）。

`ScanConflict_Click`：`await _s.ModConflicts.ScanAsync(dotMc)` → `ContentInfoBar.Message` 显示 `Conflicts.Count` 与首要冲突 `Detail`；`Conflicts.Count == 0` 时显示「未发现冲突」。

`Content_DragOver` / `Content_Drop`：`e.AcceptedOperation = DataPackageOperation.Copy`；`Drop` 中取 `e.DataView.GetStorageItemsAsync()`，对每个 `StorageFile` 按扩展名映射 kind（`.jar`→mod；`.zip` 且源目录含 `datapacks`→datapack，否则 resourcepack；`.mrpack`→modpack 提示走下载页），再 `AdoptAsync(kind, path, 兜底 info)` + `InstallToVersionAsync`（数据包 → 提示先选存档或装到 `.minecraft/saves` 下同名存档，简化实现：`.zip` 一律按 resourcepack 处理，`.jar` 按 mod，其余忽略）。

验证：构建（0 警告 0 错误）；手动执行 spec §10 的启禁还原 / 冲突扫描 / 拖拽安装 / 更新检查四条。

---

## T9：App · 智能依赖提示

文件：`dotnet/src/Starcloud.App/Views/DownloadPage.xaml.cs`

1. 在 T7 第 8 步的第 7 点位置（Mod 装载完成后）接入：

```csharp
/// <summary>Mod 装载后检查缺失依赖，汇总到 InfoBar 提示一键补全</summary>
private async Task CheckDependenciesAfterInstallAsync(ModrinthVersion version, string versionId)
{
    var report = await _s.ContentLibrary.CheckDependenciesAsync(version, versionId);
    if (report.Required.Count == 0 && report.IncompatiblePresent.Count == 0) return;

    var parts = new List<string>();
    if (report.Required.Count > 0)
        parts.Add($"缺少 {report.Required.Count} 个前置：{string.Join("、", report.Required.Select(d => d.Title))}");
    if (report.IncompatiblePresent.Count > 0)
        parts.Add($"与已装不兼容：{string.Join("、", report.IncompatiblePresent)}");
    OkBar.Message = string.Join("；", parts);
    OkBar.IsOpen = true;
}
```

2. 「一键补全」：`ContentDialog` 列出 `report.Required`，确认后对每项按 `ProjectId` 去重（限深 2 层）执行「取最新兼容版本 → 下载到 `.tmp` → `AdoptAsync("mod", ...)` → `InstallToVersionAsync`」，并在补全后对每个新依赖再调一次 `CheckDependenciesAsync`（深度 ≤ 2 即停）。

验证：构建（0 警告 0 错误）；手动执行 spec §10 的依赖补全一条。

---

## T10：文档

文件：`docs/USER_MANUAL.md`

1. 新增章节「资源库与装载」：路径 `%APPDATA%/starcloud-launcher/library/`、入库去重规则（同 SHA1 复用、同名加 ` (n)`）、「暂不装载（仅入库）」、「资源管理」入口（下载页右上角）、类别筛选、重扫。
2. 新增章节「版本内容管理」：三分区（Mod / 资源包光影 / 存档数据包）、停用 = `.jar.disabled`、检查更新、冲突扫描、拖拽安装。
3. 顺带修正设置页旧描述（「最小内存 512 / 最大内存 4096」→ 与三态档位一致的实际行为）。

验证：人工通读，无非事实描述。

---

## 任务依赖与顺序

T1 → T2 → T3 → T4 → T5 为纯 Core 增量，可依次构建保持绿灯；
T6 依赖 T5（注册需完整 ctor 签名）；T7 依赖 T6 与 T4/T5；T8 依赖 T6 与 T5；T9 依赖 T7；
T10 依赖 T1–T9 全部落地。

> 提示：T7 与 T8 均修改不同文件，可并行；T9 直接扩展 T7 新建的方法，须在 T7 之后。

## 验收对照

spec §10 十条验收映射：

1. 入库去重（同 SHA1 复用、同名加 ` (n)`）→ T2 `AdoptAsync` / T7 第 8 步
2. 多选已安装版本装载 → T3 `InstallToVersionAsync` / T7 `PickVersionsAsync`
3. 「暂不装载（仅入库）」→ T7 `PickVersionsAsync` 空列表分支
4. 数据包装到存档 `datapacks/` → T3 `InstallToSaveAsync` / T7 `PickSaveAsync`
5. 依赖补全 → T5 `CheckDependenciesAsync` / T9
6. 更新检查 → T5 `CheckUpdateAsync` / T8 `CheckUpdate_Click`
7. 启禁还原（`.disabled`）→ T8 `ToggleFile`
8. 冲突扫描 → T8 `ScanConflict_Click`
9. 拖拽安装 → T8 `Content_Drop`
10. 索引损坏静默重置 + 重扫修复 → T2 `LoadIndex` / `RescanAsync` / T7 `LibraryRescan_Click`
