// 每版本实例档案（version-settings.json）+ 目录布局 + 启动层叠合并
// 设计：docs/designs/2026-09-09-version-management-design.md

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>版本级设置档案（字段全空 = 完全跟随全局，存量零迁移）</summary>
public sealed class VersionProfile
{
    /// <summary>隔离三态："shared" | "isolated" | null（跟随全局默认）</summary>
    [JsonPropertyName("isolation")]
    public string? Isolation { get; set; }

    [JsonPropertyName("memory_min_mb")]
    public int? MemoryMinMb { get; set; }

    [JsonPropertyName("memory_max_mb")]
    public int? MemoryMaxMb { get; set; }

    [JsonPropertyName("java_path")]
    public string? JavaPath { get; set; }

    [JsonPropertyName("window_width")]
    public int? WindowWidth { get; set; }

    [JsonPropertyName("window_height")]
    public int? WindowHeight { get; set; }

    [JsonPropertyName("fullscreen")]
    public bool? Fullscreen { get; set; }

    [JsonPropertyName("extra_jvm_args")]
    public List<string> ExtraJvmArgs { get; set; } = [];

    [JsonPropertyName("extra_game_args")]
    public List<string> ExtraGameArgs { get; set; } = [];

    /// <summary>启动后自动连接服务器地址（host 或 host:port）</summary>
    [JsonPropertyName("server_address")]
    public string? ServerAddress { get; set; }

    [JsonPropertyName("env_vars")]
    public Dictionary<string, string> EnvVars { get; set; } = [];

    [JsonPropertyName("pre_launch_command")]
    public string? PreLaunchCommand { get; set; }

    [JsonPropertyName("post_launch_command")]
    public string? PostLaunchCommand { get; set; }

    [JsonPropertyName("language_override")]
    public string? LanguageOverride { get; set; }

    /// <summary>自定义图标（相对 .minecraft/starcloud/icons/ 的文件名或绝对路径）</summary>
    [JsonPropertyName("icon_path")]
    public string? IconPath { get; set; }

    [JsonPropertyName("category")]
    public string? Category { get; set; }

    /// <summary>置顶（首页版本列表排最前）</summary>
    [JsonPropertyName("pinned")]
    public bool Pinned { get; set; }

    /// <summary>收藏（排在置顶之后、普通版本之前）</summary>
    [JsonPropertyName("favorite")]
    public bool Favorite { get; set; }

    [JsonPropertyName("note")]
    public string? Note { get; set; }

    /// <summary>上次成功启动时间（启动器写入）</summary>
    [JsonPropertyName("last_played_at")]
    public DateTime? LastPlayedAt { get; set; }

    // ── 三态档位（设计 §3.2）：null 等价 "global"；显式值 global|manual|auto ──
    /// <summary>内存档位：global（跟随全局设置）/ manual（自定义）/ auto（自动配置）</summary>
    [JsonPropertyName("memory_mode")]
    public string? MemoryMode { get; set; }

    /// <summary>Java 档位：global / manual / auto</summary>
    [JsonPropertyName("java_mode")]
    public string? JavaMode { get; set; }

    /// <summary>窗口档位：global / manual / auto</summary>
    [JsonPropertyName("window_mode")]
    public string? WindowMode { get; set; }
}

/// <summary>version-settings.json 存取（&lt;dotMc&gt;/starcloud/version-settings.json）</summary>
public sealed class InstanceSettingsStore
{
    private readonly string _file;
    private Dictionary<string, VersionProfile> _profiles = [];

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public InstanceSettingsStore(string dotMc)
    {
        _file = Path.Combine(dotMc, "starcloud", "version-settings.json");
        Load();
    }

    public string FilePath => _file;

    /// <summary>重新读取文件（损坏时静默清空，不阻塞启动）</summary>
    public void Load()
    {
        _profiles = [];
        try
        {
            if (!File.Exists(_file)) return;
            var root = JsonNode.Parse(File.ReadAllText(_file));
            if (root?["profiles"] is not JsonObject obj) return;
            foreach (var (id, node) in obj)
            {
                if (id is null) continue;
                var profile = node is null ? new VersionProfile() : node.Deserialize<VersionProfile>(Options);
                if (profile is not null) _profiles[id] = profile;
            }
        }
        catch
        {
            // 文件损坏 → 视为空档案（下次 Save 覆盖），不影响启动
            _profiles = [];
        }
    }

    /// <summary>原子写（temp + File.Move overwrite）</summary>
    public void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_file)!);
        var root = new JsonObject
        {
            ["version"] = 1,
            ["profiles"] = new JsonObject(_profiles.ToDictionary(
                kv => kv.Key, kv => (JsonNode?)JsonSerializer.SerializeToNode(kv.Value, Options))),
        };
        var temp = _file + ".tmp";
        File.WriteAllText(temp, root.ToJsonString(Options));
        File.Move(temp, _file, overwrite: true);
    }

    /// <summary>取版本档案；无则返回空档案（不写入字典）</summary>
    public VersionProfile Get(string id) =>
        _profiles.TryGetValue(id, out var p) ? p : new VersionProfile();

    public bool Has(string id) => _profiles.ContainsKey(id);

    public void Set(string id, VersionProfile profile) => _profiles[id] = profile;

    /// <summary>启动成功后记录上次启动时间</summary>
    public void TouchPlayed(string id)
    {
        var p = Get(id);
        p.LastPlayedAt = DateTime.Now;
        Set(id, p);
        Save();
    }

    /// <summary>重命名版本后同步档案键（无档案则忽略）</summary>
    public void RenameKey(string oldId, string newId)
    {
        if (!_profiles.Remove(oldId, out var p)) return;
        _profiles[newId] = p;
        Save();
    }

    /// <summary>删除版本后移除档案</summary>
    public void Remove(string id)
    {
        if (_profiles.Remove(id)) Save();
    }
}

/// <summary>启动参数层叠合并结果（全局设置 + 全局默认隔离 + 版本覆盖）</summary>
public sealed record EffectiveLaunch(
    string GameRoot,
    bool Isolated,
    int MinMem,
    int MaxMem,
    string? Java,
    int? WindowW,
    int? WindowH,
    bool? Fullscreen,
    string? ServerAddress,
    string Language,
    string? LanguageOverride,
    List<string> ExtraJvmArgs,
    List<string> ExtraGameArgs,
    string? PreCmd,
    string? PostCmd,
    IReadOnlyDictionary<string, string>? EnvVars);

/// <summary>版本实例布局：隔离判定、GameRoot 解析、层叠合并、内容迁移与重命名/复制</summary>
public static class InstanceLayout
{
    /// <summary>隔离时随版本独立的 5 个内容目录（libraries/assets 等永远共享）</summary>
    public static readonly string[] ContentDirs = ["mods", "saves", "config", "resourcepacks", "shaderpacks"];

    /// <summary>解析隔离三态：null = 跟随全局默认</summary>
    public static bool IsIsolated(VersionProfile profile, LauncherSettings settings) =>
        profile.Isolation switch
        {
            "shared" => false,
            "isolated" => true,
            _ => settings.VersionIsolated,
        };

    /// <summary>版本 GameRoot：独立 = versions/&lt;id&gt;/，共享 = .minecraft 根</summary>
    public static string GameRoot(string dotMc, string versionId, bool isolated) =>
        isolated ? Path.Combine(dotMc, "versions", versionId) : dotMc;

    /// <summary>档位推断：显式档位优先；否则有旧值按 manual，无旧值按 global（旧数据零迁移）</summary>
    private static string Mode(string? explicitMode, bool hasLegacyValue)
        => explicitMode ?? (hasLegacyValue ? "manual" : "global");

    /// <summary>层叠合并：全局 + 版本覆盖 → EffectiveLaunch；autoMemContext 供 auto 档位算智能内存</summary>
    public static EffectiveLaunch Resolve(
        string dotMc, string versionId, VersionProfile profile, LauncherSettings settings,
        (int ModCount, bool HasShaders)? autoMemContext = null)
    {
        var isolated = IsIsolated(profile, settings);

        // 内存档位：global / manual / auto（最小固定 512）
        var (minMem, maxMem) = Mode(profile.MemoryMode, profile.MemoryMaxMb is not null) switch
        {
            "auto" => (512, autoMemContext is { } ctx
                ? MemoryAdvisor.SuggestMaxMb(SystemMemory.Snapshot().TotalMb, ctx.ModCount, ctx.HasShaders)
                : SystemMemory.AutoMaxMb()),
            "manual" => (profile.MemoryMinMb ?? 512,
                         profile.MemoryMaxMb ?? settings.DefaultMaxMemoryMb),
            _ => (settings.DefaultMinMemoryMb, settings.DefaultMaxMemoryMb),
        };

        // Java 档位：auto ⇒ 置空，强制走版本 JSON 推荐
        var java = Mode(profile.JavaMode, profile.JavaPath is not null) switch
        {
            "auto" => null,
            "manual" => profile.JavaPath,
            _ => settings.JavaExecutable,
        };

        // 窗口档位：global / manual / auto（auto = 854×480 窗口化）
        var (winW, winH, fullscreen) = Mode(profile.WindowMode, profile.WindowWidth is not null) switch
        {
            "auto" => ((int?)854, (int?)480, (bool?)false),
            "manual" => (profile.WindowWidth, profile.WindowHeight, profile.Fullscreen),
            _ => (settings.DefaultWindowWidth, settings.DefaultWindowHeight,
                  (bool?)settings.DefaultFullscreen),
        };

        // 环境变量：全局为底，版本级覆盖
        var env = new Dictionary<string, string>(settings.GlobalEnvVars);
        foreach (var kv in profile.EnvVars) env[kv.Key] = kv.Value;

        return new EffectiveLaunch(
            GameRoot(dotMc, versionId, isolated),
            isolated,
            minMem,
            maxMem,
            java,
            winW,
            winH,
            fullscreen,
            profile.ServerAddress,
            settings.Language,
            profile.LanguageOverride,
            [.. settings.ExtraJvmArgs, .. profile.ExtraJvmArgs],
            profile.ExtraGameArgs,
            profile.PreLaunchCommand,
            profile.PostLaunchCommand,
            env.Count > 0 ? env : null);
    }

    /// <summary>迁移 5 个内容目录（隔离切换）；源存在且非空才移动；失败回滚已移动项后 rethrow</summary>
    public static Task MoveContentDirsAsync(string fromRoot, string toRoot, Action<string>? log = null) =>
        Task.Run(() =>
        {
            Directory.CreateDirectory(toRoot);
            var moved = new List<(string Dst, string Src)>();
            try
            {
                foreach (var dir in ContentDirs)
                {
                    var src = Path.Combine(fromRoot, dir);
                    if (!Directory.Exists(src) || !Directory.EnumerateFileSystemEntries(src).Any()) continue;
                    var dst = Path.Combine(toRoot, dir);
                    if (Directory.Exists(dst) || File.Exists(dst)) dst = NextAvailablePath(dst);
                    Directory.Move(src, dst);
                    moved.Add((dst, src));
                    log?.Invoke(dir == Path.GetFileName(dst)
                        ? $"已迁移 {dir}"
                        : $"已迁移 {dir} → {Path.GetFileName(dst)}（原目标重名，双方保留）");
                }
            }
            catch
            {
                // 回滚：已移动项原样移回，保持源目录完整
                foreach (var (dst, src) in moved)
                {
                    try { Directory.Move(dst, src); } catch { /* 回滚失败时保留现状，由重试兜底 */ }
                }
                throw;
            }
        });

    /// <summary>路径被占用时递增 " (n)" 后缀（mods → mods (2)；file.txt → file (2).txt）</summary>
    public static string NextAvailablePath(string path)
    {
        var dir = Path.GetDirectoryName(path) ?? ".";
        var name = Path.GetFileName(path);
        var ext = Path.GetExtension(name);
        var stem = ext.Length > 0 ? name[..^ext.Length] : name;
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate) && !Directory.Exists(candidate)) return candidate;
        }
    }

    /// <summary>资源类别 → 版本内容目录名</summary>
    public static string ContentDirFor(string kind) => kind switch
    {
        "mod" => "mods",
        "resourcepack" => "resourcepacks",
        "shader" => "shaderpacks",
        "datapack" => "datapacks",
        _ => "mods",
    };

    /// <summary>版本备份根目录（不放进版本目录，避免递归）</summary>
    public static string BackupRoot(string dotMc) => Path.Combine(dotMc, "starcloud", "backups");

    /// <summary>备份索引文件</summary>
    public static string BackupIndexFile(string dotMc) =>
        Path.Combine(BackupRoot(dotMc), "backups.json");

    /// <summary>把任意名称变成可用文件名（去掉路径非法字符）</summary>
    public static string SafeFileName(string name)
    {
        var cleaned = string.Concat(name.Split(Path.GetInvalidFileNameChars())).Trim();
        return cleaned.Length > 0 ? cleaned : "backup";
    }

    /// <summary>重命名版本：versions/&lt;old&gt;/ 整体移动 + 内部 json/jar 同步改名 + json id 字段写回</summary>
    public static void RenameInstance(string dotMc, string oldId, string newId)
    {
        ValidateVersionId(newId);
        var oldDir = Path.Combine(dotMc, "versions", oldId);
        var newDir = Path.Combine(dotMc, "versions", newId);
        if (!Directory.Exists(oldDir)) throw new DirectoryNotFoundException($"版本目录不存在：{oldId}");
        if (Directory.Exists(newDir)) throw new IOException($"目标目录已存在：{newId}");

        Directory.Move(oldDir, newDir);
        var oldJson = Path.Combine(newDir, $"{oldId}.json");
        var newJson = Path.Combine(newDir, $"{newId}.json");
        if (File.Exists(oldJson))
        {
            File.Move(oldJson, newJson);
            PatchJsonId(newJson, newId);
        }
        var oldJar = Path.Combine(newDir, $"{oldId}.jar");
        if (File.Exists(oldJar))
            File.Move(oldJar, Path.Combine(newDir, $"{newId}.jar"));
    }

    /// <summary>复制版本：json 复制改 id + jar 复制（存在才拷）+ 可选复制隔离内容目录</summary>
    public static void CopyInstance(string dotMc, string oldId, string newId, bool copyContent)
    {
        ValidateVersionId(newId);
        var oldDir = Path.Combine(dotMc, "versions", oldId);
        var newDir = Path.Combine(dotMc, "versions", newId);
        if (!Directory.Exists(oldDir)) throw new DirectoryNotFoundException($"版本目录不存在：{oldId}");
        if (Directory.Exists(newDir)) throw new IOException($"目标目录已存在：{newId}");
        Directory.CreateDirectory(newDir);

        var oldJson = Path.Combine(oldDir, $"{oldId}.json");
        if (!File.Exists(oldJson)) throw new FileNotFoundException($"缺少版本定义文件：{oldId}.json");
        var newJson = Path.Combine(newDir, $"{newId}.json");
        File.Copy(oldJson, newJson);
        PatchJsonId(newJson, newId);

        var oldJar = Path.Combine(oldDir, $"{oldId}.jar");
        if (File.Exists(oldJar))
            File.Copy(oldJar, Path.Combine(newDir, $"{newId}.jar"));

        if (copyContent) CopyContentDirs(oldDir, newDir);
    }

    /// <summary>版本名是否合法（非空、不含路径非法字符、不是 . / ..）</summary>
    public static bool IsValidVersionId(string id) =>
        !string.IsNullOrWhiteSpace(id) && id is not ("." or "..") &&
        id.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) < 0;

    private static void ValidateVersionId(string id)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("版本名不能为空");
        if (!IsValidVersionId(id))
            throw new ArgumentException("版本名不能包含 < > : \" / \\ | ? * 或路径符号");
    }

    /// <summary>
    /// 把版本 JSON 的 id 改成目录名（改名、复制、同名多份安装共用）。
    /// <para>安装副本时目录是 <c>1.20.1-2</c>，下载到的 JSON 里 id 仍是 <c>1.20.1</c>；
    /// 不同步的话详情页与启动器读到的还是原版号，副本看起来就像「没有自己的名字」。</para>
    /// </summary>
    public static void PatchJsonId(string jsonFile, string newId)
    {
        try
        {
            if (JsonNode.Parse(File.ReadAllText(jsonFile)) is not JsonObject obj) return;
            // 改名会把 id 换成用户起的名字，原版版本号就丢了（整合包实例没有 inheritsFrom，
            // 之后没法再知道它是哪个 MC 版本）。改之前先把原版号记在旁边的字段里，留给
            // InstanceVersion 用：搜资源按版本筛、推荐整合包、判断加载器都靠它。
            var baseVersion = obj["starcloudBaseVersion"]?.GetValue<string>();
            baseVersion = GameVersionFilter.OrNull(baseVersion)
                ?? GameVersionFilter.OrNull(obj["inheritsFrom"]?.GetValue<string>())
                ?? GameVersionFilter.OrNull(obj["id"]?.GetValue<string>());
            if (baseVersion is not null) obj["starcloudBaseVersion"] = baseVersion;
            obj["id"] = newId;
            File.WriteAllText(jsonFile, obj.ToJsonString(new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            }));
        }
        catch
        {
            // id 字段改写失败不影响版本可用性（个别老版本 json 可能缺 id 或只读）
        }
    }

    private static void CopyContentDirs(string fromRoot, string toRoot)
    {
        Directory.CreateDirectory(toRoot);
        foreach (var dir in ContentDirs)
        {
            var src = Path.Combine(fromRoot, dir);
            if (!Directory.Exists(src) || !Directory.EnumerateFileSystemEntries(src).Any()) continue;
            var dst = Path.Combine(toRoot, dir);
            if (Directory.Exists(dst) || File.Exists(dst)) dst = NextAvailablePath(dst);
            CopyDirectory(src, dst);
        }
    }

    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }
}
