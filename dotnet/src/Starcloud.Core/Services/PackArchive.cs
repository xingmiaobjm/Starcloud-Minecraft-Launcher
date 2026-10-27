// 整合包归档识别与路径安全：Modrinth .mrpack / CurseForge .zip / MultiMC·Prism 实例包
//
// 导入侧共用：先嗅探归档属于哪种格式（并容忍外层多套一层目录），再统一用 PackPathGuard
// 校验包内相对路径，避免 JSON 或归档中的 ".." / 盘符把文件写到目标目录之外。

using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>支持的整合包归档格式</summary>
public enum ModPackFormat
{
    Modrinth,
    CurseForge,
    MultiMc,
    /// <summary>packwiz（pack.toml + index）：mod 由索引里的 URL 引用，不是打包在 zip 里</summary>
    Packwiz,
    /// <summary>ATLauncher（instance.json）：结构接近 MultiMC，按裸内容铺入</summary>
    AtLauncher,
    /// <summary>HMCL 旧导出格式（modpack.json）：按裸内容铺入</summary>
    Hmcl,
    /// <summary>没有清单文件的通用 zip：按 .minecraft / mods / config 结构直接铺进实例</summary>
    Raw,
}

/// <summary>归档识别结果（EntryRoot 为包内根目录，根级包为 null）</summary>
public sealed record ModPackProbe(ModPackFormat Format, string? EntryRoot)
{
    public string FormatName => Format switch
    {
        ModPackFormat.Modrinth => "Modrinth",
        ModPackFormat.CurseForge => "CurseForge",
        _ => "MultiMC / Prism",
    };

    /// <summary>包内根目录（posix 相对路径，根级包为空串）</summary>
    public string Root => EntryRoot ?? "";

    /// <summary>把包内相对路径拼成归档内的完整条目名</summary>
    public string Entry(string relative) =>
        string.IsNullOrEmpty(EntryRoot) ? relative : $"{EntryRoot}/{relative}";
}

/// <summary>
/// 整合包 zip 的统一打开方式：
///   · 未打 UTF-8 标志的中文包按 GBK(936) 回退解码，避免 overrides 里的中文文件名变成乱码；
///   · 其余照常用 UTF-8（.NET 默认）。
/// </summary>
public static class PackZip
{
    static PackZip() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    public static ZipArchive Open(string path)
    {
        var utf8 = ZipFile.Open(path, ZipArchiveMode.Read);
        try
        {
            if (!utf8.Entries.Any(e => e.FullName.Contains('�'))) return utf8;
        }
        catch
        {
            return utf8;
        }
        utf8.Dispose();
        try
        {
            return ZipFile.Open(path, ZipArchiveMode.Read, Encoding.GetEncoding(936));
        }
        catch
        {
            return ZipFile.Open(path, ZipArchiveMode.Read);
        }
    }

    /// <summary>解压全部条目到目录（逐条过 PackPathGuard，拒绝 zip-slip）。</summary>
    public static void ExtractTo(string archivePath, string destDir)
    {
        Directory.CreateDirectory(destDir);
        using var zip = Open(archivePath);
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue;
            var dest = PackPathGuard.Resolve(destDir, entry.FullName);
            if (dest is null)
            {
                LauncherLog.Write("pack", $"跳过非法压缩包路径：{entry.FullName}");
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            entry.ExtractToFile(dest, overwrite: true);
        }
    }
}

/// <summary>
/// 包内 JSON 读取：很多中文整合包的 JSON 带 UTF-8 BOM（记事本/Windows 工具产出），
/// System.Text.Json 会把它当非法起始值。统一用 StreamReader 去 BOM 后再解析。
/// </summary>
public static class PackJson
{
    public static string ReadText(Stream stream)
    {
        using var reader = new StreamReader(stream, Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true, bufferSize: 4096, leaveOpen: true);
        return reader.ReadToEnd().TrimStart('﻿');
    }

    public static string ReadFileText(string path)
    {
        using var stream = File.OpenRead(path);
        return ReadText(stream);
    }
}

/// <summary>按内容嗅探归档属于哪种整合包格式（不看扩展名，容忍嵌套根目录）</summary>
public static class ModPackDetector
{
    /// <summary>扩展名是否可能是整合包归档</summary>
    public static bool IsArchive(string path)
    {
        var ext = Path.GetExtension(path);
        return ext.Equals(".mrpack", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".zip", StringComparison.OrdinalIgnoreCase);
    }

    public static ModPackProbe? Detect(string archivePath)
    {
        if (!File.Exists(archivePath)) return null;
        using var zip = PackZip.Open(archivePath);
        var probe = Detect(zip);
        if (probe is not null) return probe;
        return DetectNested(zip, archivePath);
    }

    /// <summary>
    /// 嵌套包：外层 zip 只是把内层 .mrpack / .zip 又包了一层（不少站点的下载就是这种形态）。
    /// 外层识别不出任何结构时，若包内恰好有唯一的内层归档（且 ≤2GB 防呆），抽出来再识别一次。
    /// </summary>
    private static ModPackProbe? DetectNested(ZipArchive outer, string outerPath)
    {
        try
        {
            var candidates = outer.Entries
                .Where(e => !e.FullName.EndsWith('/')
                            && (e.FullName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase)
                                || e.FullName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                            && e.Length > 0 && e.Length <= 2L * 1024 * 1024 * 1024)
                .ToList();
            if (candidates.Count != 1) return null;

            var nestedPath = Path.Combine(Path.GetTempPath(), "starcloud-nested",
                Guid.NewGuid().ToString("N") + Path.GetExtension(candidates[0].FullName));
            Directory.CreateDirectory(Path.GetDirectoryName(nestedPath)!);
            try
            {
                candidates[0].ExtractToFile(nestedPath, overwrite: true);
                using var inner = PackZip.Open(nestedPath);
                return Detect(inner);
            }
            finally
            {
                try { File.Delete(nestedPath); } catch { }
            }
        }
        catch
        {
            // 嵌套识别只是多一次机会：失败按「识别不了」走原报错路径
            return null;
        }
    }

    public static ModPackProbe? Detect(ZipArchive zip)
    {
        // 每种格式只保留深度最浅的候选目录（外层被多套一层时取最外层）
        var best = new Dictionary<ModPackFormat, (string Root, int Depth)>();
        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            var slash = name.LastIndexOf('/');
            var dir = slash < 0 ? "" : name[..slash];
            var leaf = slash < 0 ? name : name[(slash + 1)..];
            var depth = slash < 0 ? 0 : dir.Count(c => c == '/') + 1;

            ModPackFormat? format = leaf.ToLowerInvariant() switch
            {
                "modrinth.index.json" => ModPackFormat.Modrinth,
                "manifest.json" => LooksLikeCurseForge(entry) ? ModPackFormat.CurseForge : null,
                "mmc-pack.json" or "instance.cfg" => ModPackFormat.MultiMc,
                "pack.toml" => LooksLikePackwiz(entry) ? ModPackFormat.Packwiz : null,
                "instance.json" => LooksLikeAtLauncher(entry) ? ModPackFormat.AtLauncher : null,
                "modpack.json" => LooksLikeHmcl(entry) ? ModPackFormat.Hmcl : null,
                _ => null,
            };
            if (format is null) continue;

            if (!best.TryGetValue(format.Value, out var cur) || depth < cur.Depth)
                best[format.Value] = (dir, depth);
        }

        ModPackFormat[] priority =
        [
            ModPackFormat.Modrinth, ModPackFormat.CurseForge, ModPackFormat.MultiMc,
            ModPackFormat.Packwiz, ModPackFormat.AtLauncher, ModPackFormat.Hmcl,
        ];
        foreach (var format in priority)
        {
            if (best.TryGetValue(format, out var hit))
                return new ModPackProbe(format, string.IsNullOrEmpty(hit.Root) ? null : hit.Root);
        }

        // 没有任何清单：再看是不是“裸内容” zip（PCL/HMCL 的拖入安装、手工压的 .minecraft）
        return DetectRaw(zip);
    }

    /// <summary>裸内容候选根：优先 .minecraft / minecraft，其次 mods / config 等游戏目录</summary>
    private static ModPackProbe? DetectRaw(ZipArchive zip)
    {
        string? bestRoot = null;
        var bestDepth = int.MaxValue;
        var bestScore = 0;

        foreach (var entry in zip.Entries)
        {
            var name = entry.FullName.Replace('\\', '/');
            if (name.EndsWith('/')) continue;
            var lower = name.ToLowerInvariant();

            foreach (var (marker, score, includeInRoot) in RawMarkers)
            {
                var idx = lower.IndexOf("/" + marker, StringComparison.Ordinal);
                string prefix;
                if (idx >= 0)
                    prefix = name[..idx];
                else if (lower.StartsWith(marker, StringComparison.Ordinal))
                    prefix = "";
                else
                    continue;

                // .minecraft / minecraft 是内容根本身；mods、config 只是内容根下的子目录。
                // 标记表用小写匹配，但返回的根目录要保留包内实际大小写（PCL 导出是 Minecraft）
                var dir = marker.TrimEnd('/');
                var actualDir = idx >= 0
                    ? name.Substring(idx + 1, Math.Min(dir.Length, name.Length - idx - 1))
                    : name[..Math.Min(dir.Length, name.Length)];
                var root = includeInRoot
                    ? (prefix.Length == 0 ? actualDir : prefix + "/" + actualDir)
                    : prefix;
                var depth = root.Length == 0 ? 0 : root.Count(c => c == '/') + 1;
                // 先看“更像游戏根”的标记（.minecraft > minecraft > mods…），
                // 同分时取更深的那层：Outer/MyPack/.minecraft 应取后者，而不是外层包装目录
                if (bestRoot is null || score > bestScore || (score == bestScore && depth > bestDepth))
                {
                    bestRoot = root;
                    bestDepth = depth;
                    bestScore = score;
                }
                break;
            }
        }

        if (bestRoot is null)
        {
            // 包根就是一个版本文件夹：<id>/<id>.json（PCL / HMCL「只导出某个版本」常见）
            foreach (var entry in zip.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                var slash = name.LastIndexOf('/');
                if (slash <= 0) continue;
                var dir = name[..slash];
                var leaf = name[(slash + 1)..];
                if (dir.Contains('/')) continue;
                if (!leaf.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) continue;
                if (!Path.GetFileNameWithoutExtension(leaf).Equals(dir, StringComparison.OrdinalIgnoreCase)) continue;
                bestRoot = dir;
                break;
            }
        }

        if (bestRoot is null) return null;
        return new ModPackProbe(ModPackFormat.Raw, string.IsNullOrEmpty(bestRoot) ? null : bestRoot);
    }

    private static readonly (string Marker, int Score, bool IncludeInRoot)[] RawMarkers =
    [
        (".minecraft/", 30, true),
        ("minecraft/", 20, true),          // PCL 导出常用 Minecraft 大写目录（匹配用小写名）
        ("versions/", 14, false),          // 完整游戏目录：versions/<id>/<id>.json
        ("mods/", 15, false),
        ("pcl/", 12, false),               // PCL 导出带的配置目录（PCL/Setup.ini）
        ("setup.ini", 12, false),          // PCL 的安装配置
        ("minecraftversion.txt", 12, false), // PCL 记录的版本号
        ("libraries/", 9, false),          // 完整游戏目录：libraries + assets
        ("assets/", 7, false),
        ("config/", 10, false),
        ("options.txt", 10, false),
        ("servers.dat", 10, false),
        ("saves/", 8, false),
        ("resourcepacks/", 8, false),
        ("shaderpacks/", 8, false),
        ("kubejs/", 9, false),              // 技术型整合包常只有 kubejs 脚本而无 versions/libraries
        ("defaultconfigs/", 9, false),      // 服务器包常见：默认配置随包分发
    ];

    /// <summary>packwiz 的 pack.toml 必须是 TOML（含 [index] 段或 name/version 键），排除同名杂项</summary>
    private static bool LooksLikePackwiz(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            var text = reader.ReadToEnd();
            if (text.Length == 0 || text.Length > 256 * 1024) return false;
            return text.Contains("[index]", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("name =", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>HMCL 旧导出格式的 modpack.json：JSON 对象且含 version 或 modpackVersion 字段</summary>
    private static bool LooksLikeHmcl(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(PackJson.ReadText(stream));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return doc.RootElement.TryGetProperty("modpackVersion", out _)
                   || doc.RootElement.TryGetProperty("version", out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>ATLauncher 的 instance.json 是对象且含 launcher/id 之一；MultiMC 用的是 mmc-pack.json</summary>
    private static bool LooksLikeAtLauncher(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(PackJson.ReadText(stream));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            return doc.RootElement.TryGetProperty("launcher", out _)
                   || doc.RootElement.TryGetProperty("id", out _);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>CurseForge 的 manifest.json 必须带 minecraft 字段，否则只是同名文件</summary>
    private static bool LooksLikeCurseForge(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(PackJson.ReadText(stream));
            return doc.RootElement.ValueKind == JsonValueKind.Object
                   && doc.RootElement.TryGetProperty("minecraft", out _);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>整合包归档预处理</summary>
public static class ModPackArchive
{
    /// <summary>
    /// 外层多套了一层目录的包在安装前先重打成根级包——Modrinth / CurseForge 安装器只认根级入口文件。
    /// 根级包原样返回入参路径；否则返回临时包路径，由调用方负责删除。
    /// </summary>
    public static string FlattenIfNested(string archivePath, ModPackProbe probe)
    {
        if (string.IsNullOrEmpty(probe.EntryRoot)) return archivePath;

        var dir = Path.Combine(Path.GetTempPath(), "starcloud-pack");
        Directory.CreateDirectory(dir);
        var target = Path.Combine(dir, $"{Guid.NewGuid():N}.zip");
        var prefix = probe.EntryRoot + "/";

        using (var src = PackZip.Open(archivePath))
        using (var file = File.Create(target))
        using (var dst = new ZipArchive(file, ZipArchiveMode.Create))
        {
            foreach (var entry in src.Entries)
            {
                var name = entry.FullName.Replace('\\', '/');
                if (!name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = name[prefix.Length..];
                if (rel.Length == 0) continue;
                var copy = dst.CreateEntry(rel, CompressionLevel.Fastest);
                using var from = entry.Open();
                using var to = copy.Open();
                from.CopyTo(to);
            }
        }
        return target;
    }
}

/// <summary>整合包内路径安全：拒绝穿越与非法段，保证落点始终在目标根目录内</summary>
public static class PackPathGuard
{
    /// <summary>
    /// 规范化包内相对路径为 posix 形式；非法（绝对路径、盘符、含 ..）返回 null。
    /// sanitize = true 时把 Windows 非法字符替换成下划线而不是丢弃整条路径——
    /// 从 Linux 打的包经常带冒号、问号、星号，旧行为会静默少装文件。
    /// </summary>
    public static string? Normalize(string? relative, bool sanitize = false)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var s = relative.Replace('\\', '/').Trim();
        if (s.Length == 0) return null;
        if (s.StartsWith('/')) return null;             // 绝对路径
        if (s.Length >= 2 && s[1] == ':') return null;  // 盘符

        var invalid = Path.GetInvalidFileNameChars();
        var parts = new List<string>();
        foreach (var raw in s.Split('/'))
        {
            var seg = raw.Trim();
            if (seg.Length == 0 || seg == ".") continue;
            if (seg == "..") return null;
            if (!sanitize)
            {
                if (seg.Contains(':')) return null;
                if (seg.IndexOfAny(invalid) >= 0) return null;
                parts.Add(seg);
                continue;
            }
            // 清洗模式：非法字符换成下划线，保留文件而不是整条跳过
            var cleaned = new System.Text.StringBuilder(seg.Length);
            foreach (var c in seg)
                cleaned.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            var name = cleaned.ToString().Trim().TrimEnd('.');
            if (name.Length == 0 || name == "." || name == "..") continue;
            parts.Add(name);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>把包内相对路径解析为根目录下的绝对路径；越界或非法返回 null</summary>
    public static string? Resolve(string root, string? relative, bool sanitize = false)
    {
        var norm = Normalize(relative, sanitize);
        if (norm is null) return null;
        var rootFull = Path.GetFullPath(root);
        var full = Path.GetFullPath(Path.Combine(rootFull, norm.Replace('/', Path.DirectorySeparatorChar)));
        var prefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>把任意名称压成安全的单段文件名；无有效字符时回退 fallback</summary>
    public static string SafeFileName(string? name, string fallback = "pack")
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;

        // 只取最后一段：远程接口或用户给的名字可能带路径，压成单段才不会越界
        var parts = name.Replace('\\', '/').Split('/');
        string? last = null;
        for (var i = parts.Length - 1; i >= 0; i--)
        {
            if (parts[i].Trim().Length == 0) continue;
            last = parts[i];
            break;
        }
        if (last is null) return fallback;

        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(last.Length);
        foreach (var c in last.Trim())
        {
            if (Array.IndexOf(invalid, c) >= 0) continue;
            sb.Append(c);
        }
        var s = sb.ToString().Trim().TrimEnd('.');
        return s.Length == 0 ? fallback : s;
    }
}
