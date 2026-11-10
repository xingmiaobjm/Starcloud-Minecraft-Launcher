using System.IO.Compression;
using System.Text.RegularExpressions;
using System.Text.Json;
using Starcloud.Core;
using Starcloud.Core.Models;
using Starcloud.Core.Services;

namespace Starcloud.Bridge;

/// <summary>整合包导入引擎：格式探测、依赖推断、原版/加载器联动安装、链接解析。</summary>
public static partial class Program
{
    /// <summary>整合包导入核心（文件导入与「下载页一键安装整合包」共用）。</summary>
    private static async Task<object> ImportPackCoreAsync(
        TextWriter stdout, string packPath, string targetId, bool isolated,
        string? mcVersion, string? contentRoot)
    {
        var probe = ModPackDetector.Detect(packPath)
            ?? throw new Exception("未能识别整合包格式。");
        var effective = probe.Format == ModPackFormat.Raw
            ? packPath
            : ModPackArchive.FlattenIfNested(packPath, probe);
        try
        {
            targetId = targetId.Trim();
            if (!InstanceLayout.IsValidVersionId(targetId))
                throw new Exception("版本名不能为空，且不能包含 < > : \" / \\ | ? *");
            var renamed = false;
            if (isolated && Directory.Exists(Path.Combine(_settings.DotMinecraft, "versions", targetId)))
            {
                targetId = NextVersionName(_settings.DotMinecraft, targetId);
                renamed = true;
            }
            var installRoot = isolated
                ? Path.Combine(_settings.DotMinecraft, "versions", targetId)
                : _settings.DotMinecraft;
            var progress = new Progress<BatchProgress>(b =>
                Emit(stdout, "modpack.progress", new { stage = b.Stage, percent = b.Percentage }));
            if (isolated) Directory.CreateDirectory(installRoot);
            var result = probe.Format switch
            {
                ModPackFormat.CurseForge => await _cfPacks.InstallAsync(effective, _settings, installRoot, null, progress),
                ModPackFormat.MultiMc => await _multiMcPacks.InstallAsync(effective, _settings, installRoot, null, progress),
                ModPackFormat.Raw => await InstallPackRawAsync(effective, contentRoot ?? "", installRoot, mcVersion ?? "", progress),
                _ => await _mrPack.InstallAsync(effective, _settings, installRoot, null, progress),
            };
            var (summary, installedLoaderId) = await EnsurePackGameAndLoaderAsync(result, progress);
            if (isolated)
            {
                // 骨架父版本用**实际装出的加载器目录名**：旧版 Forge 安装器装出的是
                // <mc>-Forge<build>-<mc>（如 1.7.10-Forge10.13.4.1614-1.7.10），
                // 按依赖拼出来的名字对不上（issue #11）
                var parent = installedLoaderId
                    ?? PackLoaderVersionId(_settings.DotMinecraft, result.GameVersion, result.Dependencies)
                    ?? result.GameVersion;
                var jsonPath = Path.Combine(installRoot, $"{targetId}.json");
                if (!File.Exists(jsonPath) && parent.Length > 0)
                {
                    var skeleton = new { id = targetId, inheritsFrom = parent, type = "release" };
                    File.WriteAllText(jsonPath, JsonSerializer.Serialize(skeleton, JsonOpts));
                }
                var store = new InstanceSettingsStore(_settings.DotMinecraft);
                store.Set(targetId, new VersionProfile { Isolation = "isolated" });
                store.Save();
            }
            return new { ok = true, targetId, renamed, isolated, summary, gameVersion = result.GameVersion };
        }
        finally
        {
            if (!ReferenceEquals(effective, packPath))
            {
                try { File.Delete(effective); } catch { }
            }
        }
    }

    // ── 整合包 / 备份辅助（对齐 Win 端 ModPackDialogs / MrPackFlow） ──────

    private static IProgress<BatchProgress> PackProgress(TextWriter stdout) =>
        new Progress<BatchProgress>(b =>
            Emit(stdout, "modpack.progress", new { stage = b.Stage, percent = b.Percentage }));

    /// <summary>推断 MC 版本与加载器依赖（JSON inheritsFrom/libraries → 目录名兜底）。</summary>
    private static (string GameVersion, Dictionary<string, string> Deps) ResolvePackDeps(
        string dotMc, string versionId)
    {
        var found = new Dictionary<string, string>();
        var game = versionId;
        var jsonPath = Path.Combine(dotMc, "versions", versionId, $"{versionId}.json");
        string? raw = null;
        try { if (File.Exists(jsonPath)) raw = File.ReadAllText(jsonPath); } catch { }
        if (raw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var rootEl = doc.RootElement;
                if (rootEl.TryGetProperty("inheritsFrom", out var inh) &&
                    inh.ValueKind == JsonValueKind.String && inh.GetString() is { Length: > 0 } parent)
                    game = parent;
                else if (rootEl.TryGetProperty("id", out var idEl) &&
                         idEl.ValueKind == JsonValueKind.String && idEl.GetString() is { Length: > 0 } self)
                    game = self;
                if (rootEl.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lib in libs.EnumerateArray())
                    {
                        if (!lib.TryGetProperty("name", out var nameEl)) continue;
                        var name = nameEl.GetString();
                        if (string.IsNullOrEmpty(name)) continue;
                        var parts = name.Split(':');
                        if (parts.Length < 3) continue;
                        var (group, artifact, ver) = (parts[0], parts[1], parts[2]);
                        if (group == "net.fabricmc" && artifact == "fabric-loader") found["fabric-loader"] = ver;
                        else if (group == "org.quiltmc" && artifact == "quilt-loader") found["quilt-loader"] = ver;
                        else if (group == "net.minecraftforge" && artifact == "forge")
                            found["forge"] = TrimMcVersion(ver, game);
                        else if (group == "net.neoforged" && artifact == "neoforge") found["neoforge"] = ver;
                    }
                }
            }
            catch { }
        }
        if (found.Count == 0)
        {
            var m = Regex.Match(versionId, @"^(fabric-loader|quilt-loader)-(.+?)-(\d[\w.]*)$",
                RegexOptions.IgnoreCase);
            if (m.Success) found[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;
            else if ((m = Regex.Match(versionId, @"-forge-(.+)$", RegexOptions.IgnoreCase)).Success)
                found["forge"] = m.Groups[1].Value;
            else if ((m = Regex.Match(versionId, @"neoforge-(.+)$", RegexOptions.IgnoreCase)).Success)
                found["neoforge"] = m.Groups[1].Value;
        }
        var deps = new Dictionary<string, string> { ["minecraft"] = game };
        foreach (var kv in found) deps[kv.Key] = kv.Value;
        return (game, deps);
    }

    private static string TrimMcVersion(string version, string mc)
    {
        var prefix = mc + "-";
        return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? version[prefix.Length..]
            : version;
    }

    private static string DescribePackLoader(IReadOnlyDictionary<string, string> deps)
    {
        var parts = new List<string>();
        if (deps.TryGetValue("fabric-loader", out var f)) parts.Add($"Fabric {f}");
        if (deps.TryGetValue("quilt-loader", out var q)) parts.Add($"Quilt {q}");
        if (deps.TryGetValue("forge", out var fo)) parts.Add($"Forge {fo}");
        if (deps.TryGetValue("neoforge", out var n)) parts.Add($"NeoForge {n}");
        return string.Join(" · ", parts);
    }

    /// <summary>实例根下的顶层内容目录（排除运行期/公共目录），带文件数与体积。</summary>
    private static object EnumerateContentDirs(string gameRoot)
    {
        var standard = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "mods", "config", "resourcepacks", "shaderpacks", "kubejs", "scripts", "defaultconfigs" };
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { "logs", "crash-reports", "caches", ".mixin.out", ".fabric", ".quilt" };
        if (!Directory.Exists(gameRoot)) return Array.Empty<object>();
        return Directory.EnumerateDirectories(gameRoot)
            .Select(d => new DirectoryInfo(d))
            .Where(d => !d.Name.StartsWith("."))
            .Where(d => !skip.Contains(d.Name))
            .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
            .Select(d =>
            {
                var files = 0; var bytes = 0L;
                try
                {
                    foreach (var f in Directory.EnumerateFiles(d.FullName, "*", SearchOption.AllDirectories))
                    {
                        files++;
                        try { bytes += new FileInfo(f).Length; } catch { }
                    }
                }
                catch { }
                return new { name = d.Name, fileCount = files, totalBytes = bytes, standard = standard.Contains(d.Name) };
            })
            .ToArray();
    }

    /// <summary>把粘贴的链接解析成整合包直链（Modrinth / CurseForge 页面或压缩包直链）。</summary>
    private static async Task<(string Url, string FileName)> ResolvePackUrlAsync(
        string link, CancellationToken ct)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new Exception("请输入完整的 http(s) 链接。");

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host.EndsWith("modrinth.com", StringComparison.Ordinal))
        {
            var index = Array.FindIndex(segments, x => x.Equals("modpack", StringComparison.OrdinalIgnoreCase) ||
                                                       x.Equals("modpacks", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= segments.Length)
                throw new Exception("不是有效的 Modrinth 整合包链接。");
            var versions = await _modrinth.ListVersionsAsync(segments[index + 1], null, null, ct);
            if (versions.Count == 0) throw new Exception("该整合包没有可下载的文件。");
            var pick = versions[0];
            if (index + 3 < segments.Length && segments[index + 2].Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                var want = segments[index + 3];
                pick = versions.FirstOrDefault(v =>
                           string.Equals(v.VersionNumber, want, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(v.Id, want, StringComparison.OrdinalIgnoreCase)) ?? pick;
            }
            var file = pick.PrimaryFile ?? throw new Exception("该整合包没有可下载的文件。");
            return (file.Url, file.Filename);
        }

        if (host.EndsWith("curseforge.com", StringComparison.Ordinal))
        {
            var index = Array.FindIndex(segments, x => x.Equals("modpacks", StringComparison.OrdinalIgnoreCase) ||
                                                       x.Equals("modpack", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= segments.Length)
                throw new Exception("不是有效的 CurseForge 整合包链接。");
            var slug = segments[index + 1];
            var hits = (await _curseForge.SearchAsync(slug, "modpack", null, null, 0, 20, "relevance", ct)).Hits;
            var hit = hits.FirstOrDefault(h => string.Equals(h.Slug, slug, StringComparison.OrdinalIgnoreCase))
                      ?? hits.FirstOrDefault()
                      ?? throw new Exception("未找到该整合包。");
            var fileId = index + 3 < segments.Length &&
                         segments[index + 2].Equals("files", StringComparison.OrdinalIgnoreCase)
                ? segments[index + 3]
                : null;
            var version = fileId is null
                ? (await _curseForge.ListVersionsAsync(hit.ProjectId, null, null, ct)).FirstOrDefault()
                : await _curseForge.GetFileAsync(hit.ProjectId, fileId, ct);
            var cfFile = version?.PrimaryFile ?? throw new Exception("该整合包没有可下载的文件。");
            return (cfFile.Url, cfFile.Filename);
        }

        var ext = Path.GetExtension(uri.AbsolutePath);
        if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
            throw new Exception("无法识别的链接。请粘贴 Modrinth / CurseForge 整合包页面或压缩包直链。");
        return (uri.ToString(), Path.GetFileName(uri.AbsolutePath));
    }

    /// <summary>统计裸内容包在指定内容根下的文件数 / 字节数。</summary>
    private static (int Count, long Bytes) MeasurePackPrefix(string packPath, string root)
    {
        var prefix = string.IsNullOrEmpty(root) ? "" : root + "/";
        using var zip = PackZip.Open(packPath);
        var count = 0;
        long bytes = 0;
        foreach (var entry in zip.Entries)
        {
            if (entry.Name.Length == 0) continue;
            var name = entry.FullName.Replace('\\', '/');
            if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            count++;
            bytes += Math.Max(entry.Length, 0);
        }
        return (count, bytes);
    }

    /// <summary>裸内容包：把内容根下的文件原样铺进实例目录（不联网）。</summary>
    private static Task<MrPackInstallResult> InstallPackRawAsync(
        string packPath, string contentRoot, string installRoot, string mcVersion,
        IProgress<BatchProgress>? progress)
    {
        return Task.Run(() =>
        {
            var prefix = string.IsNullOrEmpty(contentRoot) ? "" : contentRoot + "/";
            using var zip = PackZip.Open(packPath);
            var done = 0;
            long doneBytes = 0;
            var (total, totalBytes) = (0, 0L);
            foreach (var entry in zip.Entries)
            {
                if (entry.Name.Length == 0) continue;
                var name = entry.FullName.Replace('\\', '/');
                if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                total++;
                totalBytes += Math.Max(entry.Length, 0);
            }
            foreach (var entry in zip.Entries)
            {
                if (entry.Name.Length == 0) continue;
                var name = entry.FullName.Replace('\\', '/');
                if (prefix.Length > 0 && !name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
                var rel = prefix.Length == 0 ? name : name[prefix.Length..];
                var dest = PackPathGuard.Resolve(installRoot, rel);
                if (dest is null)
                {
                    continue;
                }
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                entry.ExtractToFile(dest, overwrite: true);
                done++;
                doneBytes += Math.Max(entry.Length, 0);
                if (done % 32 == 0 || done == total)
                    progress?.Report(new BatchProgress("实例文件", done, total, doneBytes, totalBytes, 0, null));
            }
            return new MrPackInstallResult(mcVersion, new Dictionary<string, string>(), done, doneBytes);
        });
    }

    /// <summary>按整合包依赖联动安装原版与加载器；返回中文摘要（Win MrPackFlow 同款）。</summary>
    private static async Task<(string Summary, string? LoaderVersionId)> EnsurePackGameAndLoaderAsync(
        MrPackInstallResult result, IProgress<BatchProgress>? progress)
    {
        var game = result.GameVersion;
        var parts = new List<string>();
        string? installedLoaderId = null;
        if (!string.IsNullOrEmpty(game))
        {
            if (!_installer.IsInstalled(_settings.DotMinecraft, game))
            {
                var entry = await FindManifestEntryAsync(game);
                if (entry is not null)
                    await _installer.InstallVanillaAsync(game, entry.Url, _settings, progress);
            }
            parts.Add($"Minecraft {game}");

            var javaMajor = 8;
            try
            {
                var v = await _installer.LoadVersionAsync(_settings.DotMinecraft, game);
                javaMajor = JavaRequirement.Resolve(game, v.JavaVersion);
            }
            catch { }

            if (result.Dependencies.TryGetValue("fabric-loader", out var fab))
            {
                await _installer.InstallFabricAsync(game, fab, _settings, progress);
                parts.Add($"Fabric {fab}");
            }
            if (result.Dependencies.TryGetValue("quilt-loader", out var quilt))
            {
                await _installer.InstallQuiltAsync(game, quilt, _settings, progress);
                parts.Add($"Quilt {quilt}");
            }
            if (result.Dependencies.TryGetValue("forge", out var forge))
            {
                // 安装出的真实目录名以返回值为准：旧版 Forge 安装器是 <mc>-Forge<build>-<mc>（issue #11）
                var installed = await _forge.InstallViaInstallerAsync(
                    ForgeInstallerService.ForgeInstallerUrl(game, forge), game, javaMajor, _settings, progress);
                installedLoaderId ??= installed;
                parts.Add($"Forge {forge}");
            }
            if (result.Dependencies.TryGetValue("neoforge", out var neo))
            {
                var installed = await _forge.InstallViaInstallerAsync(
                    ForgeInstallerService.NeoForgeInstallerUrl(game, neo), game, javaMajor, _settings, progress);
                installedLoaderId ??= installed;
                parts.Add($"NeoForge {neo}");
            }
        }

        return (parts.Count > 0
            ? "已联动安装 " + string.Join(" + ", parts)
            : $"共下载 {result.DownloadedFiles} 个文件", installedLoaderId);
    }

    /// <summary>名称冲突时递增 " (n)"。</summary>
    private static string NextVersionName(string dotMc, string baseName)
    {
        if (!Directory.Exists(Path.Combine(dotMc, "versions", baseName))) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!Directory.Exists(Path.Combine(dotMc, "versions", candidate))) return candidate;
        }
    }

    /// <summary>联动安装后实际存在的加载器版本目录名。</summary>
    private static string? PackLoaderVersionId(string dotMc, string mc, IReadOnlyDictionary<string, string> deps)
    {
        string? id = null;
        if (deps.TryGetValue("fabric-loader", out var fab) && fab.Length > 0) id = $"fabric-loader-{fab}-{mc}";
        else if (deps.TryGetValue("quilt-loader", out var quilt) && quilt.Length > 0) id = $"quilt-loader-{quilt}-{mc}";
        else if (deps.TryGetValue("forge", out var forge) && forge.Length > 0) id = $"{mc}-forge-{forge}";
        else if (deps.TryGetValue("neoforge", out var neo) && neo.Length > 0) id = $"neoforge-{neo}";
        return id is not null && File.Exists(Path.Combine(dotMc, "versions", id, $"{id}.json")) ? id : null;
    }
}
