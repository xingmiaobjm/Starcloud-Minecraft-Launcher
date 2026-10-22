// MultiMC / Prism 实例包导入：解析 instance.cfg + mmc-pack.json，把游戏根内容铺到实例目录。
//
// 与 .mrpack / CurseForge 不同，MultiMC 实例包是**自包含**的（mod 全在包内，不需要按 id 回源下载），
// 所以安装就是「解压 + 落位」。安装结果同样复用 <see cref="MrPackInstallResult"/>，
// 下游的 MrPackFlow 联动逻辑一行不用改。
//
// 布局：
//   <root>/instance.cfg        INI，含 name=
//   <root>/mmc-pack.json       { "components": [ { "uid": "net.minecraft", "version": "1.20.1" } ] }
//   <root>/.minecraft/**       游戏内容（Prism）
//   <root>/minecraft/**        游戏内容（旧版 MultiMC）
//   <root>/**                  少数包直接把实例目录当游戏根

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>mmc-pack.json 中的组件（游戏本体 / 加载器 / API）</summary>
public sealed record MultiMcComponent(string Uid, string Version, bool Important);

/// <summary>MultiMC / Prism 实例包预览信息</summary>
public sealed record MultiMcPackInfo(
    string Name,
    string GameVersion,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyList<MultiMcComponent> Components,
    int FileCount,
    long TotalBytes);

public sealed class MultiMcPackService
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>MultiMC 组件 uid → MrPackFlow 认识的依赖键</summary>
    private static readonly Dictionary<string, string> UidToDependency = new(StringComparer.OrdinalIgnoreCase)
    {
        ["net.minecraft"] = "minecraft",
        ["net.fabricmc.fabric-loader"] = "fabric-loader",
        ["org.quiltmc.quilt-loader"] = "quilt-loader",
        ["net.minecraftforge"] = "forge",
        ["net.neoforged"] = "neoforge",
    };

    /// <summary>游戏内容根候选（按优先级）</summary>
    private static readonly string[] ContentRoots = [".minecraft", "minecraft"];

    /// <summary>内容根即实例根时需剔除的元文件</summary>
    private static readonly HashSet<string> MetaFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "instance.cfg", "mmc-pack.json", "pack.json", "patch.json",
        "mmc-icon.png", "icon.png", ".packignore",
    };

    /// <summary>读取实例包元信息（不落任何游戏文件）</summary>
    public MultiMcPackInfo ReadPackageInfo(string archivePath)
    {
        using var zip = PackZip.Open(archivePath);
        var probe = RequireMultiMc(zip);
        var components = ReadComponents(zip, probe);
        var contentRoot = ResolveContentRoot(zip, probe);
        var (count, bytes) = MeasureContent(zip, contentRoot);
        return new MultiMcPackInfo(
            ReadInstanceName(zip, probe) ?? Path.GetFileNameWithoutExtension(archivePath),
            GameVersion(components),
            BuildDependencies(components),
            components,
            count,
            bytes);
    }

    /// <summary>安装实例包（installRoot 为空时落 .minecraft 根；支持装进隔离实例目录）</summary>
    public async Task<MrPackInstallResult> InstallAsync(
        string archivePath, LauncherSettings settings, string? installRoot = null,
        IProgress<string>? status = null, IProgress<BatchProgress>? progress = null,
        CancellationToken ct = default)
    {
        using var zip = PackZip.Open(archivePath);
        var probe = RequireMultiMc(zip);
        var contentRoot = ResolveContentRoot(zip, probe);

        var dotMc = installRoot ?? settings.DotMinecraft;
        Directory.CreateDirectory(dotMc);

        var entries = new List<(ZipArchiveEntry Entry, string Rel)>();
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue; // 目录项
            if (RelativePath(contentRoot, entry) is { } rel) entries.Add((entry, rel));
        }

        var total = entries.Count;
        var totalBytes = entries.Sum(x => Math.Max(x.Entry.Length, 0));
        status?.Report($"正在应用实例文件（{total} 个）…");

        var done = 0;
        long doneBytes = 0;
        await Task.Run(() =>
        {
            foreach (var (entry, rel) in entries)
            {
                ct.ThrowIfCancellationRequested();
                var dest = PackPathGuard.Resolve(dotMc, rel);
                if (dest is null)
                {
                    LauncherLog.Write("multimc", $"跳过非法路径条目：{entry.FullName}");
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                done++;
                doneBytes += Math.Max(entry.Length, 0);
                if (done % 32 == 0 || done == total)
                    progress?.Report(new BatchProgress("实例文件", done, total, doneBytes, totalBytes, 0, null));
            }
        }, ct);

        status?.Report("实例文件已就绪");
        var components = ReadComponents(zip, probe);
        return new MrPackInstallResult(
            GameVersion(components), BuildDependencies(components), done, doneBytes);
    }

    private static ModPackProbe RequireMultiMc(ZipArchive zip)
    {
        var probe = ModPackDetector.Detect(zip)
            ?? throw new InvalidOperationException("无法识别整合包格式（既不是 Modrinth / CurseForge，也没有 MultiMC 实例结构）");
        if (probe.Format != ModPackFormat.MultiMc)
            throw new InvalidOperationException($"不是 MultiMC / Prism 实例包（实际识别为 {probe.FormatName}）");
        return probe;
    }

    /// <summary>定位游戏内容根：优先 `<root>/.minecraft`，其次 `<root>/minecraft`，兜底实例根</summary>
    private static string ResolveContentRoot(ZipArchive zip, ModPackProbe probe)
    {
        foreach (var sub in ContentRoots)
        {
            var candidate = probe.Root.Length == 0 ? sub : $"{probe.Root}/{sub}";
            if (zip.Entries.Any(e => EntryPath(e).StartsWith(candidate + "/", StringComparison.OrdinalIgnoreCase)))
                return candidate;
        }
        return probe.Root;
    }

    /// <summary>条目相对内容根的路径；不属于内容根或是元文件时返回 null</summary>
    private static string? RelativePath(string contentRoot, ZipArchiveEntry entry)
    {
        var path = EntryPath(entry);
        string rel;
        if (contentRoot.Length == 0)
            rel = path;
        else if (path.StartsWith(contentRoot + "/", StringComparison.OrdinalIgnoreCase))
            rel = path[(contentRoot.Length + 1)..];
        else
            return null;

        if (rel.Length == 0 || rel.EndsWith('/')) return null;
        // 内容根即实例根时，顶层元文件不能铺进游戏目录
        if (!rel.Contains('/') && MetaFiles.Contains(rel)) return null;
        return rel;
    }

    private static (int Count, long Bytes) MeasureContent(ZipArchive zip, string contentRoot)
    {
        var count = 0;
        long bytes = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue;
            if (RelativePath(contentRoot, entry) is null) continue;
            count++;
            bytes += Math.Max(entry.Length, 0);
        }
        return (count, bytes);
    }

    private static IReadOnlyList<MultiMcComponent> ReadComponents(ZipArchive zip, ModPackProbe probe)
    {
        var entry = FindEntry(zip, probe, "mmc-pack.json");
        if (entry is null) return [];
        using var stream = entry.Open();
        var dto = JsonSerializer.Deserialize<MmcPackFile>(PackJson.ReadText(stream), JsonOpts);
        if (dto?.Components is null) return [];
        return dto.Components
            .Where(c => !string.IsNullOrWhiteSpace(c.Uid))
            .Select(c => new MultiMcComponent(c.Uid!, c.Version ?? "", c.Important))
            .ToList();
    }

    private static string? ReadInstanceName(ZipArchive zip, ModPackProbe probe)
    {
        var entry = FindEntry(zip, probe, "instance.cfg");
        if (entry is null) return null;
        using var reader = new StreamReader(entry.Open());
        while (reader.ReadLine() is { } line)
        {
            var text = line.Trim();
            if (!text.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) continue;
            var value = text[5..].Trim();
            if (value.Length > 0) return value;
        }
        return null;
    }

    private static string GameVersion(IReadOnlyList<MultiMcComponent> components) =>
        components.FirstOrDefault(c => c.Uid.Equals("net.minecraft", StringComparison.OrdinalIgnoreCase))?.Version ?? "";

    private static IReadOnlyDictionary<string, string> BuildDependencies(IReadOnlyList<MultiMcComponent> components)
    {
        var deps = new Dictionary<string, string>();
        foreach (var c in components)
        {
            if (!UidToDependency.TryGetValue(c.Uid, out var key)) continue;
            deps[key] = c.Version;
        }
        return deps;
    }

    private static string EntryPath(ZipArchiveEntry entry) => entry.FullName.Replace('\\', '/');

    private static ZipArchiveEntry? FindEntry(ZipArchive zip, ModPackProbe probe, string name)
    {
        var wanted = probe.Entry(name);
        return zip.Entries.FirstOrDefault(e =>
            EntryPath(e).Equals(wanted, StringComparison.OrdinalIgnoreCase));
    }
}

// ── mmc-pack.json DTO ──

file sealed class MmcPackFile
{
    [JsonPropertyName("formatVersion")] public int FormatVersion { get; set; }
    [JsonPropertyName("components")] public List<MmcComponent>? Components { get; set; }
}

file sealed class MmcComponent
{
    [JsonPropertyName("uid")] public string? Uid { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("important")] public bool Important { get; set; }
}
