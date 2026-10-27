// 裸内容整合包（packwiz / ATLauncher / HMCL / 通用 zip）没有逐文件清单，也就没有现成的
// 游戏版本。这里从包内线索推断，供导入对话框预填（用户仍可改）：
//   ① packwiz 的 pack.toml → [versions]
//   ② mod jar 元数据：fabric.mod.json / quilt.mod.json / META-INF/mods.toml
//   ③ mod 文件名里的版本号（如 xxx-1.20.1-2.3.jar）
// 全程只读，任何异常都退化成"没识别到"，不影响导入本身。

using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Starcloud.Core.Services;

/// <summary>从包内容推断出的版本信息（Source 仅用于 UI 说明，如 "pack.toml"）</summary>
public sealed record PackVersionInfo(string? Minecraft, string? Loader, string? LoaderVersion, string Source)
{
    public bool Found => !string.IsNullOrWhiteSpace(Minecraft);

    public static readonly PackVersionInfo Empty = new(null, null, null, "");
}

public static class PackVersionSniffer
{
    /// <summary>最多抽查多少个 jar：几十个足够投票，避免大包全量解压拖慢导入</summary>
    private const int MaxJars = 24;

    /// <summary>mod 文件名里的 MC 版本：xxx-1.20.1-fabric.jar / xxx_1.19.2.jar</summary>
    private static readonly Regex FileVersion = new(
        @"(?:^|[-_ ])(1\.\d{1,2}(?:\.\d{1,2})?)(?:[-_ .]|$)", RegexOptions.Compiled);

    public static PackVersionInfo Sniff(string packPath, string root)
    {
        var prefix = string.IsNullOrEmpty(root) ? "" : root + "/";
        try
        {
            using var zip = PackZip.Open(packPath);
            var entries = zip.Entries.Where(e => e.Name.Length > 0).ToList();

            // ① packwiz：pack.toml 的 [versions] 最权威
            var packToml = Find(entries, prefix + "pack.toml");
            if (packToml is not null)
            {
                var fromToml = FromPackToml(packToml);
                if (fromToml.Found) return fromToml;
            }

            // ② mod jar 元数据投票
            var jars = entries
                .Where(e => e.FullName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase))
                .Take(MaxJars)
                .ToList();
            if (jars.Count > 0)
            {
                var versionVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var loaderVotes = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var loaderVersions = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
                var nameHits = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

                foreach (var jar in jars)
                {
                    var meta = ReadJarMeta(jar);
                    if (!string.IsNullOrEmpty(meta.Minecraft))
                        versionVotes[meta.Minecraft] = versionVotes.GetValueOrDefault(meta.Minecraft) + 1;
                    if (!string.IsNullOrEmpty(meta.Loader))
                        loaderVotes[meta.Loader] = loaderVotes.GetValueOrDefault(meta.Loader) + 1;
                    if (!string.IsNullOrEmpty(meta.LoaderVersion))
                        loaderVersions[meta.LoaderVersion] = loaderVersions.GetValueOrDefault(meta.LoaderVersion) + 1;

                    var stem = Path.GetFileNameWithoutExtension(jar.Name);
                    foreach (Match m in FileVersion.Matches(stem))
                    {
                        var v = m.Groups[1].Value;
                        nameHits[v] = nameHits.GetValueOrDefault(v) + 1;
                    }
                }

                // 文件名只作补充：元数据一条都没拿到时才用，且至少两个 jar 互证
                if (versionVotes.Count == 0 && nameHits.Count > 0)
                {
                    var best = nameHits.OrderByDescending(kv => kv.Value).First();
                    if (best.Value >= 2) versionVotes[best.Key] = best.Value;
                }

                if (versionVotes.Count > 0)
                {
                    var mc = Top(versionVotes);
                    var loader = Top(loaderVotes);
                    var loaderVer = loader is null ? null : Top(loaderVersions);
                    return new PackVersionInfo(mc, loader, loaderVer, "mod 元数据");
                }
            }

            return PackVersionInfo.Empty;
        }
        catch
        {
            return PackVersionInfo.Empty;
        }
    }

    /// <summary>pack.toml：[versions] 段里的 minecraft / fabric / forge / neoforge / quilt</summary>
    private static PackVersionInfo FromPackToml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var inVersions = false;
        string? mc = null, loader = null, loaderVer = null;

        for (var i = 0; i < 200 && reader.ReadLine() is { } line; i++)
        {
            var t = line.Trim();
            if (t.StartsWith('['))
            {
                inVersions = t.Equals("[versions]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inVersions || t.Length == 0 || t.StartsWith('#')) continue;

            var eq = t.IndexOf('=');
            if (eq <= 0) continue;
            var key = t[..eq].Trim().ToLowerInvariant();
            var value = t[(eq + 1)..].Trim().Trim('"');
            if (value.Length == 0) continue;

            switch (key)
            {
                case "minecraft": mc = value; break;
                case "fabric": loader = "fabric"; loaderVer = value; break;
                case "forge": loader = "forge"; loaderVer = value; break;
                case "neoforge": loader = "neoforge"; loaderVer = value; break;
                case "quilt": loader = "quilt"; loaderVer = value; break;
            }
        }
        return string.IsNullOrEmpty(mc)
            ? PackVersionInfo.Empty
            : new PackVersionInfo(mc, loader, loaderVer, "pack.toml");
    }

    private sealed record JarMeta(string? Minecraft, string? Loader, string? LoaderVersion);

    /// <summary>读单个 jar 的加载器元数据（只解压需要的那个小文件到内存）</summary>
    private static JarMeta ReadJarMeta(ZipArchiveEntry jarEntry)
    {
        try
        {
            using var copy = new MemoryStream();
            using (var src = jarEntry.Open()) src.CopyTo(copy);
            copy.Position = 0;
            using var jar = new ZipArchive(copy, ZipArchiveMode.Read);

            if (jar.GetEntry("fabric.mod.json") is { } fabric)
                return new JarMeta(ReadFabricLike(fabric, "depends"), "fabric", null);

            if (jar.GetEntry("quilt.mod.json") is { } quilt)
                return new JarMeta(ReadQuilt(quilt), "quilt", null);

            if (jar.GetEntry("META-INF/neoforge.mods.toml") is { } neo)
                return new JarMeta(ReadModsToml(neo), "neoforge", null);

            if (jar.GetEntry("META-INF/mods.toml") is { } forge)
                return new JarMeta(ReadModsToml(forge), "forge", null);
        }
        catch
        {
            // 损坏的 jar 跳过，不影响其余样本
        }
        return new JarMeta(null, null, null);
    }

    /// <summary>fabric.mod.json 的 depends.minecraft，可能是字符串或数组</summary>
    private static string? ReadFabricLike(ZipArchiveEntry entry, string section)
    {
        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(PackJson.ReadText(stream));
        if (!doc.RootElement.TryGetProperty(section, out var depends)) return null;
        if (!depends.TryGetProperty("minecraft", out var mc)) return null;

        return mc.ValueKind switch
        {
            JsonValueKind.String => Normalize(mc.GetString()),
            JsonValueKind.Array => FirstVersion(mc),
            _ => null,
        };
    }

    /// <summary>quilt.mod.json 的 quilt_loader.depends 是 [{id:"minecraft", versions:"..."}]</summary>
    private static string? ReadQuilt(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var doc = JsonDocument.Parse(PackJson.ReadText(stream));
        if (!doc.RootElement.TryGetProperty("quilt_loader", out var ql)) return null;
        if (!ql.TryGetProperty("depends", out var deps) || deps.ValueKind != JsonValueKind.Array) return null;

        foreach (var dep in deps.EnumerateArray())
        {
            if (dep.ValueKind != JsonValueKind.Object) continue;
            if (!dep.TryGetProperty("id", out var id) ||
                !string.Equals(id.GetString(), "minecraft", StringComparison.OrdinalIgnoreCase)) continue;
            if (!dep.TryGetProperty("versions", out var versions)) return null;
            return versions.ValueKind == JsonValueKind.String
                ? Normalize(versions.GetString())
                : versions.ValueKind == JsonValueKind.Array ? FirstVersion(versions) : null;
        }
        return null;
    }

    /// <summary>Forge/NeoForge 的 mods.toml：找 modId="minecraft" 那块里的 versionRange</summary>
    private static string? ReadModsToml(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        var inBlock = false;
        string? range = null;

        for (var i = 0; i < 400 && reader.ReadLine() is { } line; i++)
        {
            var t = line.Trim();
            if (t.StartsWith('['))
            {
                // 新块开始：上一块若没抓到 minecraft 就继续找；抓到就结束
                if (range is not null) break;
                inBlock = t.Contains("dependencies", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inBlock) continue;

            var id = ExtractTomlValue(t, "modId");
            if (id is not null &&
                string.Equals(id, "minecraft", StringComparison.OrdinalIgnoreCase))
            {
                // versionRange 可能在同一行或紧随其后
                range = ExtractTomlValue(t, "versionRange");
                if (range is null)
                {
                    // 往后看几行
                    for (var j = 0; j < 6 && reader.ReadLine() is { } next; j++)
                    {
                        var nv = ExtractTomlValue(next.Trim(), "versionRange");
                        if (nv is not null) { range = nv; break; }
                    }
                }
                if (range is not null) break;
            }
        }
        return Normalize(range);
    }

    /// <summary>取 TOML 行里的 key = "value"（值可能是带引号的字符串）</summary>
    private static string? ExtractTomlValue(string line, string key)
    {
        var idx = line.IndexOf(key, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;
        var eq = line.IndexOf('=', idx + key.Length);
        if (eq < 0) return null;
        var value = line[(eq + 1)..].Trim();
        var quote = value.IndexOf('"');
        if (quote >= 0)
        {
            var end = value.IndexOf('"', quote + 1);
            if (end > quote) return value[(quote + 1)..end];
        }
        var comment = value.IndexOf('#');
        if (comment >= 0) value = value[..comment];
        return value.Trim().Trim('\'');
    }

    private static string? FirstVersion(JsonElement array)
    {
        foreach (var item in array.EnumerateArray())
        {
            var v = item.ValueKind == JsonValueKind.String ? Normalize(item.GetString()) : null;
            if (!string.IsNullOrEmpty(v)) return v;
        }
        return null;
    }

    /// <summary>
    /// 版本范围转具体版本：fabric 的 "&gt;=1.20 &lt;1.21"、Forge 的 "[1.20.1,1.21)" 都取
    /// 下界（含端点），因为整合包要装的是它起步的那个版本。通配符/为空返回 null。
    /// </summary>
    internal static string? Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = raw.Trim();
        if (text is "*" or "any" or "x") return null;

        // 取第一个 1.x[.x] 形态的版本号
        var m = Regex.Match(text, @"1\.\d{1,2}(?:\.\d{1,2})?");
        if (!m.Success) return null;
        var version = m.Value;
        // "1.20.*" / "1.20.x" → 1.20
        return version;
    }

    private static ZipArchiveEntry? Find(List<ZipArchiveEntry> entries, string name) =>
        entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));

    private static string? Top(Dictionary<string, int> votes) =>
        votes.Count == 0
            ? null
            : votes.OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal).First().Key;
}
