// 模组冲突扫描与自动处理：解析 mods 目录 jar 内的 mod 元数据
// （fabric.mod.json / quilt.mod.json / META-INF/mods.toml / mcmod.info），
// 检测重复 modId、加载器混装、多加载器混合包、损坏文件；
// 处理冲突 = 将同 modId 的旧版本停用（改名 .jar.disabled）。

using System.IO.Compression;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>mods 目录中的单个模组 jar（Loader: fabric/quilt/forge/unknown，多加载器混合包含 "+"）</summary>
public sealed record ModFileInfo(
    string FileName, string FullPath, long SizeBytes, DateTime ModifiedAt,
    string? ModId, string? ModName, string? ModVersion, string Loader,
    string? GameVersion = null, string? Error = null);

/// <summary>一个冲突（Files 为涉及的文件名）</summary>
public sealed record ModConflict(string Kind, string Detail, string Suggestion, List<string> Files);

/// <summary>模组扫描汇总</summary>
public sealed record ModConflictReport(
    int ScannedCount, List<ModConflict> Conflicts, IReadOnlyList<ModFileInfo> Mods);

public sealed class ModConflictService
{
    /// <summary>扫描 .minecraft/mods 下全部 jar</summary>
    public async Task<ModConflictReport> ScanAsync(string dotMc, CancellationToken ct = default)
        => await Task.Run(() => Scan(dotMc, ct), ct);

    /// <summary>
    /// 自动处理冲突：对「重复安装」保留最新文件（按修改时间），其余停用为 .jar.disabled。
    /// 返回处理摘要；无法自动处理的冲突（混装等）一并说明。
    /// </summary>
    public async Task<string> ResolveAsync(string dotMc, CancellationToken ct = default)
        => await Task.Run(async () =>
        {
            var report = Scan(dotMc, ct);
            var disabled = new List<string>();
            foreach (var conflict in report.Conflicts.Where(c => c.Kind == "重复安装"))
            {
                var group = report.Mods
                    .Where(m => conflict.Files.Contains(m.FileName))
                    .OrderByDescending(m => m.ModifiedAt).ThenBy(m => m.FileName)
                    .ToList();
                foreach (var old in group.Skip(1))
                {
                    var target = old.FullPath + ".disabled";
                    if (File.Exists(target))
                        File.Delete(target); // 之前的停用残留，覆盖
                    File.Move(old.FullPath, target);
                    disabled.Add(old.FileName);
                }
            }

            if (report.Conflicts.Count == 0)
                return $"模组处理完成：{report.ScannedCount} 个模组，没有需要处理的冲突。";

            var sb = new System.Text.StringBuilder();
            sb.AppendLine(disabled.Count > 0
                ? $"已自动停用 {disabled.Count} 个重复的旧版本模组："
                : "没有可自动停用的重复模组。");
            foreach (var f in disabled) sb.AppendLine($"• {f} → 已停用（.jar.disabled）");

            var manual = report.Conflicts.Where(c => c.Kind != "重复安装").ToList();
            if (manual.Count > 0)
            {
                sb.AppendLine("以下冲突需要手动确认，无法自动处理：");
                foreach (var c in manual)
                    sb.AppendLine($"• [{c.Kind}] {c.Detail}");
            }
            return sb.ToString().TrimEnd();
        }, ct);

    // ── 扫描实现 ──
    private static ModConflictReport Scan(string dotMc, CancellationToken ct)
    {
        var modsDir = Path.Combine(dotMc, "mods");
        var mods = new List<ModFileInfo>();
        if (!Directory.Exists(modsDir)) return new ModConflictReport(0, [], mods);

        foreach (var file in Directory.EnumerateFiles(modsDir, "*.jar")
                     .Where(f => f.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)))
        {
            ct.ThrowIfCancellationRequested();
            mods.Add(Read(file));
        }

        var conflicts = BuildConflicts(mods);
        return new ModConflictReport(mods.Count, conflicts, mods);
    }

    private static List<ModConflict> BuildConflicts(IReadOnlyList<ModFileInfo> mods)
    {
        var conflicts = new List<ModConflict>();

        // 1) 加载器混装：Fabric 与 Forge 模组不能共存（Quilt 兼容 Fabric 生态，归入 fabric）
        var fabric = mods.Where(m => PrimaryLoader(m.Loader) == "fabric").ToList();
        var forge = mods.Where(m => PrimaryLoader(m.Loader) == "forge").ToList();
        if (fabric.Count > 0 && forge.Count > 0)
            conflicts.Add(new ModConflict("加载器混装",
                $"mods 目录同时存在 Fabric（{fabric.Count} 个）与 Forge（{forge.Count} 个）模组，两套加载器互不兼容",
                "保留其中一种加载器的模组，把另一类移出 mods 目录",
                fabric.Concat(forge).Select(m => m.FileName).ToList()));

        // 2) 多加载器混合包：单个 jar 同时含 fabric.mod.json 与 Forge 元数据
        var mixed = mods.Where(m => m.Loader.Contains('+')).ToList();
        if (mixed.Count > 0)
            conflicts.Add(new ModConflict("多加载器混合包",
                $"{string.Join("、", mixed.Select(m => m.FileName))} 同时包含 Fabric 与 Forge 元数据，属于异常文件",
                "确认来源后重新下载（正常模组只面向一种加载器）",
                mixed.Select(m => m.FileName).ToList()));

        // 3) 重复 modId：同一模组装了多个版本
        foreach (var g in mods
                     .Where(m => !string.IsNullOrEmpty(m.ModId))
                     .GroupBy(m => m.ModId!, StringComparer.OrdinalIgnoreCase)
                     .Where(g => g.Count() > 1))
        {
            var ordered = g.OrderByDescending(m => m.ModifiedAt).ThenBy(m => m.FileName).ToList();
            conflicts.Add(new ModConflict("重复安装",
                $"模组「{(string.IsNullOrEmpty(ordered[0].ModName) ? g.Key : ordered[0].ModName)}」（{g.Key}）安装了 {ordered.Count} 个版本：{string.Join("、", ordered.Select(m => m.FileName))}",
                $"建议保留最新版本 {ordered[0].FileName}，其余停用（回复「处理冲突」可自动完成）",
                ordered.Select(m => m.FileName).ToList()));
        }

        // 4) 损坏文件：jar 无法读取
        var broken = mods.Where(m => m.Error is not null).ToList();
        if (broken.Count > 0)
            conflicts.Add(new ModConflict("文件损坏",
                $"{string.Join("、", broken.Select(m => m.FileName))} 无法读取（{broken[0].Error}）",
                "删除后重新下载这些模组",
                broken.Select(m => m.FileName).ToList()));

        return conflicts;
    }

    private static string PrimaryLoader(string loader)
    {
        var plus = loader.IndexOf('+');
        var primary = plus > 0 ? loader[..plus] : loader;
        return primary == "quilt" ? "fabric" : primary;
    }

    // ── jar 元数据解析 ──
    /// <summary>
    /// 读取单个 jar 的 mod 元数据。冲突扫描与「模组管理」列表共用：
    /// 后者要用 ModName / ModVersion / GameVersion 把文件名换成人能读的卡片。
    /// </summary>
    public static ModFileInfo Read(string path)
    {
        var fi = new FileInfo(path);
        try
        {
            using var jar = ZipFile.OpenRead(path);

            string? id = null, name = null, version = null, gameVersion = null;
            var loader = "unknown";
            var hasFabricMeta = jar.GetEntry("fabric.mod.json") is not null;
            var hasForgeMeta = jar.GetEntry("META-INF/mods.toml") is not null
                               || jar.GetEntry("mcmod.info") is not null;

            if (hasFabricMeta)
            {
                (id, name, version) = ParseFabricJson(jar.GetEntry("fabric.mod.json")!);
                gameVersion = FabricMinecraftRange(jar.GetEntry("fabric.mod.json")!);
                loader = hasForgeMeta ? "fabric+forge" : "fabric";
            }
            else if (jar.GetEntry("quilt.mod.json") is { } quilt)
            {
                (id, name, version) = ParseQuiltJson(quilt);
                gameVersion = QuiltMinecraftRange(quilt);
                loader = "quilt";
            }
            else if (hasForgeMeta)
            {
                (id, name, version) = jar.GetEntry("META-INF/mods.toml") is { } toml
                    ? ParseModsToml(toml)
                    : ParseMcModInfo(jar.GetEntry("mcmod.info")!);
                gameVersion = jar.GetEntry("META-INF/mods.toml") is { } toml2
                    ? TomlMinecraftRange(toml2)
                    : McModInfoGameVersion(jar.GetEntry("mcmod.info")!);
                loader = "forge";
            }

            return new ModFileInfo(fi.Name, path, fi.Length, fi.LastWriteTime,
                id, name, version, loader, gameVersion,
                id is null && !hasFabricMeta && !hasForgeMeta && jar.GetEntry("quilt.mod.json") is null
                    ? "未识别到 mod 元数据（可能是前置库）"
                    : null);
        }
        catch (Exception ex)
        {
            return new ModFileInfo(fi.Name, path, fi.Length, fi.LastWriteTime,
                null, null, null, "unknown", Error: ex.Message);
        }
    }

    /// <summary>fabric.mod.json 的 depends.minecraft（字符串或字符串数组）</summary>
    private static string? FabricMinecraftRange(ZipArchiveEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadEntryText(entry));
            return doc.RootElement.TryGetProperty("depends", out var dep) &&
                   dep.ValueKind == JsonValueKind.Object &&
                   dep.TryGetProperty("minecraft", out var mc)
                ? JoinVersions(mc)
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>quilt.mod.json 的 quilt_loader.depends 里 id=minecraft 的 versions</summary>
    private static string? QuiltMinecraftRange(ZipArchiveEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadEntryText(entry));
            if (!doc.RootElement.TryGetProperty("quilt_loader", out var ql) ||
                !ql.TryGetProperty("depends", out var deps) ||
                deps.ValueKind != JsonValueKind.Array)
                return null;
            foreach (var dep in deps.EnumerateArray())
            {
                if (dep.ValueKind == JsonValueKind.Object &&
                    dep.TryGetProperty("id", out var id) &&
                    string.Equals(id.GetString(), "minecraft", StringComparison.OrdinalIgnoreCase) &&
                    dep.TryGetProperty("versions", out var versions))
                    return JoinVersions(versions);
            }
            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>mods.toml / neoforge.mods.toml 里 [[dependencies.*]] 段中 modId="minecraft" 的 versionRange</summary>
    private static string? TomlMinecraftRange(ZipArchiveEntry entry)
    {
        try
        {
            using var sr = new StreamReader(entry.Open());
            var inDeps = false;
            string? modId = null, range = null;
            while (sr.ReadLine() is { } line)
            {
                var t = line.Trim();
                if (t.StartsWith("[["))
                {
                    if (inDeps && modId is not null && range is not null) break;
                    inDeps = t.Contains("dependencies", StringComparison.OrdinalIgnoreCase);
                    modId = null;
                    range = null;
                    continue;
                }
                if (!inDeps || t.StartsWith('#') || t.StartsWith('[')) continue;
                var eq = t.IndexOf('=');
                if (eq <= 0) continue;
                var key = t[..eq].Trim();
                var val = t[(eq + 1)..].Trim().Trim('"', '\'');
                if (key == "modId") modId = val;
                else if (key == "versionRange") range = val;
            }
            return modId is not null &&
                   string.Equals(modId, "minecraft", StringComparison.OrdinalIgnoreCase)
                ? range
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static string? McModInfoGameVersion(ZipArchiveEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadEntryText(entry));
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return null;
            var first = doc.RootElement[0];
            return first.TryGetProperty("mcversion", out var v) && v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>把 "1.20.1" / ["1.20","1.20.1"] 统一成一行可读文本</summary>
    private static string? JoinVersions(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Array => string.Join(" / ", element.EnumerateArray()
            .Where(v => v.ValueKind == JsonValueKind.String)
            .Select(v => v.GetString())
            .Where(v => !string.IsNullOrWhiteSpace(v))),
        _ => null,
    };

    private static (string? Id, string? Name, string? Version) ParseFabricJson(ZipArchiveEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadEntryText(entry));
            var root = doc.RootElement;
            return (
                root.TryGetProperty("id", out var i) ? i.GetString() : null,
                root.TryGetProperty("name", out var n) ? n.GetString() : null,
                root.TryGetProperty("version", out var v) ? v.GetString() : null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static (string? Id, string? Name, string? Version) ParseQuiltJson(ZipArchiveEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadEntryText(entry));
            if (!doc.RootElement.TryGetProperty("quilt_loader", out var ql)) return (null, null, null);
            var name = ql.TryGetProperty("metadata", out var md) &&
                       md.ValueKind == JsonValueKind.Object &&
                       md.TryGetProperty("name", out var mn)
                ? mn.GetString() : null;
            return (
                ql.TryGetProperty("id", out var i) ? i.GetString() : null,
                name,
                ql.TryGetProperty("version", out var v) ? v.GetString() : null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static (string? Id, string? Name, string? Version) ParseModsToml(ZipArchiveEntry entry)
    {
        try
        {
            using var sr = new StreamReader(entry.Open());
            string? id = null, name = null, version = null;
            var inMods = false;
            while (sr.ReadLine() is { } line)
            {
                var t = line.Trim();
                if (t.StartsWith("[["))
                {
                    if (inMods && !t.Contains("mods")) break; // 进入下一段，停止读取第一个 [[mods]]
                    inMods = t.Contains("mods");
                    continue;
                }
                if (!inMods || t.StartsWith('#')) continue;
                var eq = t.IndexOf('=');
                if (eq <= 0) continue;
                var key = t[..eq].Trim();
                var val = t[(eq + 1)..].Trim().Trim('"', '\'');
                if (key == "modId" && id is null) id = val;
                else if (key == "displayName" && name is null) name = val;
                else if (key == "version" && version is null && !val.Contains('$')) version = val;
            }
            return (id, name, version);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static (string? Id, string? Name, string? Version) ParseMcModInfo(ZipArchiveEntry entry)
    {
        try
        {
            using var doc = JsonDocument.Parse(ReadEntryText(entry));
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                return (null, null, null);
            var first = doc.RootElement[0];
            return (
                first.TryGetProperty("modid", out var i) ? i.GetString() : null,
                first.TryGetProperty("name", out var n) ? n.GetString() : null,
                first.TryGetProperty("version", out var v) &&
                v.ValueKind == JsonValueKind.String ? v.GetString() : null);
        }
        catch
        {
            return (null, null, null);
        }
    }

    private static string ReadEntryText(ZipArchiveEntry entry)
    {
        using var sr = new StreamReader(entry.Open());
        return sr.ReadToEnd();
    }
}
