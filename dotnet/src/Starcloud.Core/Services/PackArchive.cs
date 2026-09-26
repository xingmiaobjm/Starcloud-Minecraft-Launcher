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
        using var zip = ZipFile.OpenRead(archivePath);
        return Detect(zip);
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
                _ => null,
            };
            if (format is null) continue;

            if (!best.TryGetValue(format.Value, out var cur) || depth < cur.Depth)
                best[format.Value] = (dir, depth);
        }

        ModPackFormat[] priority = [ModPackFormat.Modrinth, ModPackFormat.CurseForge, ModPackFormat.MultiMc];
        foreach (var format in priority)
        {
            if (best.TryGetValue(format, out var hit))
                return new ModPackProbe(format, string.IsNullOrEmpty(hit.Root) ? null : hit.Root);
        }
        return null;
    }

    /// <summary>CurseForge 的 manifest.json 必须带 minecraft 字段，否则只是同名文件</summary>
    private static bool LooksLikeCurseForge(ZipArchiveEntry entry)
    {
        try
        {
            using var stream = entry.Open();
            using var doc = JsonDocument.Parse(stream);
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

        using (var src = ZipFile.OpenRead(archivePath))
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
    /// <summary>规范化包内相对路径为 posix 形式；非法（绝对路径、盘符、含 ..）返回 null</summary>
    public static string? Normalize(string? relative)
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
            if (seg.Contains(':')) return null;
            if (seg.IndexOfAny(invalid) >= 0) return null;
            parts.Add(seg);
        }
        return parts.Count == 0 ? null : string.Join('/', parts);
    }

    /// <summary>把包内相对路径解析为根目录下的绝对路径；越界或非法返回 null</summary>
    public static string? Resolve(string root, string? relative)
    {
        var norm = Normalize(relative);
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
