// SC 实验室 · Minecraft 资源读取：投影 3D 预览的**真实方块纹理**来源。
//
// 纹理不随启动器打包（几百 MB 不现实），直接读用户本机 .minecraft，按原版资源包优先级分层
// （高优先级在前，逐层命中即返回）：
//   ① 启用的资源包（options.txt 的 resourcePacks，列表越靠后优先级越高）
//   ② 客户端 jar（versions/<id>/<id>.jar：原版 blockstates / models / textures 都在这里）
//   ③ 该实例的模组 jar（mods/*.jar 里的 assets/<modid>/**，模组方块纹理；索引懒建）
//   ④ assets 索引 + objects（老布局兜底）
//
// 路径约定：对外一律用**资源包相对路径**（文件位于 assets/<该路径>），
// 如 "minecraft/blockstates/tnt.json"、"create/textures/block/andesite_casing.png"。
//
// 关键教训：现代版本的 assets/indexes 里**没有** blockstates/models（实测索引 34 只有
// sounds/lang 和 7 张纹理），所以"索引优先、索引缺失才回退 jar"永远拿不到方块纹理——
// 必须 jar 优先。这是"预览只有近似色"的真正原因。

using System.IO.Compression;
using System.Text.Json;

namespace Starcloud.Core.Services.Lab;

public sealed class MinecraftAssets
{
    private readonly Func<LauncherSettings> _settings;
    private readonly Dictionary<string, byte[]?> _cache = new(StringComparer.Ordinal);

    /// <summary>已打开的 jar 句柄缓存：一个方块的解析要读 4~6 个资源，
    /// 每次都重开 26MB 的客户端 jar 会把预览拖到好几秒（实测 8370 项调色板 7.6s → 缓存后 &lt;1s）</summary>
    private readonly Dictionary<string, ZipArchive> _zips = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _zipGate = new();

    /// <summary>资源层（高优先级在前）</summary>
    private readonly List<Layer> _layers = [];
    private bool _ready;

    /// <summary>资源来源说明（界面显示"真实（N 张 · 机械动力航空学整合包 + 141 模组）"）</summary>
    public string? SourceLabel { get; private set; }

    public MinecraftAssets(Func<LauncherSettings> settings) => _settings = settings;

    /// <summary>本机有没有可用资源（拿不到就退回近似色渲染）</summary>
    public bool Available
    {
        get
        {
            EnsureReady();
            return _layers.Count > 0;
        }
    }

    /// <summary>读一个资源（路径相对 assets/，如 "minecraft/blockstates/tnt.json"）</summary>
    public byte[]? Read(string assetPath)
    {
        EnsureReady();
        if (_cache.TryGetValue(assetPath, out var hit)) return hit;

        byte[]? data = null;
        foreach (var layer in _layers)
        {
            try
            {
                data = layer.Read(assetPath);
            }
            catch
            {
                data = null; // 单个资源读取失败按缺失处理，继续往下层找
            }
            if (data is not null) break;
        }
        _cache[assetPath] = data;
        return data;
    }

    private sealed record Layer(Func<string, byte[]?> Read);

    private void EnsureReady()
    {
        if (_ready) return;
        _ready = true;

        var settings = _settings();
        var dotMc = settings.DotMinecraft;
        var versionId = string.IsNullOrWhiteSpace(settings.LastVersion) ? null : settings.LastVersion;
        var gameDir = versionId is null
            ? dotMc
            : InstanceLayout.GameRoot(dotMc, versionId, settings.VersionIsolated);

        var parts = new List<string>();

        // ① 启用的资源包（越靠后优先级越高 → 反序入层）
        var packs = EnabledResourcePacks(gameDir, dotMc);
        foreach (var pack in Enumerable.Reverse(packs))
        {
            var path = pack;
            var isZip = File.Exists(path);
            _layers.Add(new Layer(rel => isZip
                ? ReadFromZip(path, "assets/", rel)
                : ReadFromFolder(Path.Combine(path, "assets"), rel)));
        }
        if (packs.Count > 0) parts.Add($"{packs.Count} 资源包");

        // ② 客户端 jar（原版方块外观的唯一完整来源）
        //    排在模组 jar 之前是有意的：投影绝大多数是原版方块，先命中就不必为每次预览
        //    扫描上百个模组 jar；模组方块会穿透这一层、在模组层命中
        var jars = VersionJars(dotMc, versionId);
        foreach (var jar in jars)
        {
            var path = jar;
            _layers.Add(new Layer(rel => ReadFromZip(path, "assets/", rel)));
        }
        if (jars.Count > 0) parts.Add(Path.GetFileNameWithoutExtension(jars[0]));

        // ③ 模组 jar：条目多，索引懒建（只有真出现原版层拿不到的资源才扫一遍）
        var modJars = ModJars(gameDir, dotMc);
        if (modJars.Count > 0)
        {
            Dictionary<string, string>? index = null;
            _layers.Add(new Layer(rel =>
            {
                index ??= BuildModAssetIndex(modJars);
                return index.TryGetValue(rel, out var jar) ? ReadFromZip(jar, "assets/", rel) : null;
            }));
            parts.Add($"{modJars.Count} 模组");
        }

        // ④ assets 索引 + objects（老版本 / jar 缺失时兜底）
        var objects = TryLoadObjectsIndex(dotMc);
        if (objects is { } obj)
        {
            _layers.Add(new Layer(rel =>
                obj.Map.TryGetValue(rel, out var hash) && hash.Length >= 2
                    ? ReadFile(Path.Combine(obj.Dir, hash[..2], hash))
                    : null));
            parts.Add($"assets 索引 {obj.Name}");
        }

        SourceLabel = parts.Count == 0 ? null : string.Join(" + ", parts);
    }

    // ── 各层读取 ──

    /// <summary>
    /// 打开（并缓存）一个 zip。用 FileShare.ReadWrite|Delete 打开：
    /// 句柄会留到会话结束，不能挡住别的进程更新/删除版本 jar。
    /// </summary>
    private ZipArchive? GetZip(string path)
    {
        lock (_zipGate)
        {
            if (_zips.TryGetValue(path, out var hit)) return hit;
            if (!File.Exists(path)) return null;
            try
            {
                var zip = new ZipArchive(
                    new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete),
                    ZipArchiveMode.Read);
                _zips[path] = zip;
                return zip;
            }
            catch
            {
                return null;   // 坏 jar / 被占用：按缺失处理
            }
        }
    }

    private byte[]? ReadFromZip(string zipPath, string entryPrefix, string rel)
    {
        var zip = GetZip(zipPath);
        var entry = zip?.GetEntry(entryPrefix + rel);
        if (entry is null) return null;
        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private static byte[]? ReadFromFolder(string assetsRoot, string rel) =>
        ReadFile(Path.Combine(assetsRoot, rel.Replace('/', Path.DirectorySeparatorChar)));

    private static byte[]? ReadFile(string path) => File.Exists(path) ? File.ReadAllBytes(path) : null;

    // ── 来源发现 ──

    /// <summary>options.txt 里已启用的资源包（返回绝对路径，列表顺序 = 优先级从低到高）</summary>
    private static List<string> EnabledResourcePacks(string gameDir, string dotMc)
    {
        var roots = new[] { Path.Combine(gameDir, "resourcepacks"), Path.Combine(dotMc, "resourcepacks") };
        var result = new List<string>();

        foreach (var options in new[] { Path.Combine(gameDir, "options.txt"), Path.Combine(dotMc, "options.txt") })
        {
            if (!File.Exists(options)) continue;
            string? line = null;
            try
            {
                line = File.ReadLines(options).FirstOrDefault(l => l.StartsWith("resourcePacks:", StringComparison.Ordinal));
            }
            catch
            {
                continue;
            }
            if (line is null) continue;

            List<string>? names = null;
            try
            {
                names = JsonSerializer.Deserialize<List<string>>(line["resourcePacks:".Length..].Trim());
            }
            catch
            {
                // 选项行格式异常：按没有启用资源包处理
            }
            if (names is null) continue;

            foreach (var raw in names)
            {
                // "vanilla" / "programmer_art" 等内置包不在磁盘上，跳过
                var name = raw.StartsWith("file/", StringComparison.Ordinal) ? raw["file/".Length..] : raw;
                if (name.Length == 0 || name.Contains("programmer_art", StringComparison.OrdinalIgnoreCase)) continue;
                var candidate = Path.IsPathRooted(name) ? name : null;
                var found = candidate is not null && (File.Exists(candidate) || Directory.Exists(candidate))
                    ? candidate
                    : roots.Select(r => Path.Combine(r, name))
                        .FirstOrDefault(p => File.Exists(p) || Directory.Exists(p));
                if (found is not null && !result.Contains(found, StringComparer.OrdinalIgnoreCase))
                    result.Add(found);
            }
            if (result.Count > 0) break; // 隔离实例的 options.txt 优先
        }
        return result;
    }

    /// <summary>该实例的模组 jar（隔离实例读 versions/&lt;id&gt;/mods，共享则读根 mods）</summary>
    private static List<string> ModJars(string gameDir, string dotMc)
    {
        var jars = new List<string>();
        var dirs = new[] { Path.Combine(gameDir, "mods"), Path.Combine(dotMc, "mods") };
        foreach (var dir in dirs.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(dir)) continue;
            try
            {
                jars.AddRange(Directory.EnumerateFiles(dir, "*.jar")
                    .Where(p => !p.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase)));
            }
            catch
            {
                // 目录不可读：跳过
            }
        }
        return jars.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>客户端 jar：当前实例的优先，再按最近修改补两个兜底</summary>
    private static List<string> VersionJars(string dotMc, string? versionId)
    {
        var versionsDir = Path.Combine(dotMc, "versions");
        var result = new List<string>();
        if (!Directory.Exists(versionsDir)) return result;

        if (versionId is not null)
        {
            var dir = Path.Combine(versionsDir, versionId);
            if (Directory.Exists(dir))
            {
                try
                {
                    result.AddRange(Directory.EnumerateFiles(dir, "*.jar")
                        .Where(p => Path.GetFileNameWithoutExtension(p)
                            .Equals(versionId, StringComparison.OrdinalIgnoreCase)));
                }
                catch
                {
                    // 目录不可读：走兜底
                }
            }
        }

        try
        {
            foreach (var jar in Directory.EnumerateFiles(versionsDir, "*.jar", SearchOption.AllDirectories)
                         .Where(p => Path.GetFileNameWithoutExtension(p) == Path.GetFileName(Path.GetDirectoryName(p)))
                         .OrderByDescending(File.GetLastWriteTimeUtc))
            {
                if (result.Count >= 3) break;
                if (!result.Contains(jar, StringComparer.OrdinalIgnoreCase)) result.Add(jar);
            }
        }
        catch
        {
            // 版本目录扫描失败：只用已找到的
        }
        return result;
    }

    /// <summary>模组 jar 的资源索引：路径 → jar（先扫到的模组优先）</summary>
    private Dictionary<string, string> BuildModAssetIndex(IReadOnlyList<string> jars)
    {
        var map = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var jar in jars)
        {
            try
            {
                var zip = GetZip(jar);
                if (zip is null) continue;
                foreach (var entry in zip.Entries)
                {
                    var name = entry.FullName;
                    // 只要方块外观三件套，避免把 lang/sounds 全塞进内存
                    if (!name.StartsWith("assets/", StringComparison.Ordinal) ||
                        name.EndsWith('/') ||
                        (!name.Contains("/blockstates/") && !name.Contains("/models/") && !name.Contains("/textures/")))
                        continue;
                    map.TryAdd(name["assets/".Length..], jar);
                }
            }
            catch
            {
                // 坏 jar：跳过
            }
        }
        return map;
    }

    private sealed record ObjectsIndex(string Name, Dictionary<string, string> Map, string Dir);

    /// <summary>assets 索引 + objects（取版本号最大的那份）</summary>
    private static ObjectsIndex? TryLoadObjectsIndex(string dotMc)
    {
        try
        {
            var indexesDir = Path.Combine(dotMc, "assets", "indexes");
            if (!Directory.Exists(indexesDir)) return null;
            var best = Directory.EnumerateFiles(indexesDir, "*.json")
                .Select(p => (Path: p, Ver: ParseIndexVersion(Path.GetFileNameWithoutExtension(p))))
                .OrderByDescending(t => t.Ver)
                .FirstOrDefault();
            if (best.Path is null) return null;

            using var doc = JsonDocument.Parse(File.ReadAllBytes(best.Path));
            if (!doc.RootElement.TryGetProperty("objects", out var objects) ||
                objects.ValueKind != JsonValueKind.Object)
                return null;

            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in objects.EnumerateObject())
                if (item.Value.TryGetProperty("hash", out var h) && h.GetString() is { Length: > 0 } hash)
                    map[item.Name] = hash;
            if (map.Count == 0) return null;

            return new ObjectsIndex(Path.GetFileNameWithoutExtension(best.Path), map,
                Path.Combine(dotMc, "assets", "objects"));
        }
        catch
        {
            return null;
        }
    }

    private static double ParseIndexVersion(string name) =>
        double.TryParse(name, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : 0;
}
