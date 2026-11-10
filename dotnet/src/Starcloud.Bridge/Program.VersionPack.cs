using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core;
using Starcloud.Core.Models;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Multiplayer;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Bridge;

public static partial class Program
{

    private static async Task<object?> HandleVersionsExportProbeAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var (gameVersion, deps) = ResolvePackDeps(_settings.DotMinecraft, versionId);
            var isolated = InstanceLayout.IsIsolated(
                new InstanceSettingsStore(_settings.DotMinecraft).Get(versionId), _settings);
            var gameRoot = InstanceLayout.GameRoot(_settings.DotMinecraft, versionId, isolated);
            return new
            {
                versionId,
                gameVersion,
                dependencies = deps,
                loader = DescribePackLoader(deps),
                dirs = EnumerateContentDirs(gameRoot),
            };
    }

    private static async Task<object?> HandleVersionsExportModpackAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var store = new InstanceSettingsStore(_settings.DotMinecraft);
            var isolated = InstanceLayout.IsIsolated(store.Get(versionId), _settings);
            var gameRoot = InstanceLayout.GameRoot(_settings.DotMinecraft, versionId, isolated);
            var outPath = p.GetProperty("outPath").GetString()!;
            var format = p.TryGetProperty("format", out var fmt) && fmt.ValueKind == JsonValueKind.String
                ? fmt.GetString()
                : "modrinth";
            var deps = new Dictionary<string, string>();
            if (p.TryGetProperty("dependencies", out var depEl) && depEl.ValueKind == JsonValueKind.Object)
            {
                foreach (var kv in depEl.EnumerateObject())
                    deps[kv.Name] = kv.Value.ValueKind == JsonValueKind.String ? kv.Value.GetString() ?? "" : kv.Value.ToString();
            }
            if (deps.Count == 0)
            {
                var (_, auto) = ResolvePackDeps(_settings.DotMinecraft, versionId);
                deps = new Dictionary<string, string>(auto);
            }
            var dirs = new List<string>();
            if (p.TryGetProperty("dirs", out var dirsEl) && dirsEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in dirsEl.EnumerateArray())
                {
                    var name = d.GetString();
                    if (!string.IsNullOrWhiteSpace(name)) dirs.Add(name);
                }
            }
            if (dirs.Count == 0) throw new Exception("请至少勾选一个内容目录。");
            var exclude = new List<string>();
            if (p.TryGetProperty("excludeRules", out var exEl) && exEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var e in exEl.EnumerateArray())
                {
                    var r = e.GetString();
                    if (!string.IsNullOrWhiteSpace(r)) exclude.Add(r);
                }
            }
            var request = new ExportRequest(
                gameRoot, outPath,
                p.TryGetProperty("name", out var nEl) && nEl.ValueKind == JsonValueKind.String && nEl.GetString() is { Length: > 0 } nm ? nm : versionId,
                p.TryGetProperty("gameVersion", out var gvEl) && gvEl.ValueKind == JsonValueKind.String && gvEl.GetString() is { Length: > 0 } gv ? gv : deps.GetValueOrDefault("minecraft", ""),
                deps, dirs)
            {
                Format = format == "curseforge" ? ModPackExportFormat.CurseForge
                    : format == "multimc" ? ModPackExportFormat.MultiMc
                    : ModPackExportFormat.Modrinth,
                VersionId = p.TryGetProperty("packageVersion", out var pvEl) && pvEl.ValueKind == JsonValueKind.String && pvEl.GetString() is { Length: > 0 } pv ? pv : "1.0.0",
                Author = p.TryGetProperty("author", out var aEl) ? aEl.GetString() ?? "" : "",
                Description = p.TryGetProperty("description", out var dEl) ? dEl.GetString() ?? "" : "",
                ExcludeRules = exclude,
                IncludeGameSettings = p.TryGetProperty("includeGameSettings", out var igs) && igs.GetBoolean(),
                UnresolvedAsOverrides = !p.TryGetProperty("unresolvedAsOverrides", out var uao) || uao.GetBoolean(),
            };
            var exportStatus = new Progress<string>(st =>
                Emit(stdout, "modpack.progress", new { stage = st, percent = -1 }));
            var result = await _modPackExport.ExportAsync(request, exportStatus);
            return new
            {
                ok = true,
                path = outPath,
                result.DownloadableFiles,
                result.OverriddenFiles,
                result.SkippedFiles,
                result.TotalBytes,
                result.LoaderWarning,
            };
    }

    private static async Task<object?> HandleVersionsResolvePackLinkAsync(TextWriter stdout, JsonElement p)
    {
            var link = p.GetProperty("url").GetString() ?? "";
            var (url, fileName) = await ResolvePackUrlAsync(link, CancellationToken.None);
            return new { url, fileName };
    }

    private static async Task<object?> HandleVersionsDownloadPackAsync(TextWriter stdout, JsonElement p)
    {
            var url = p.GetProperty("url").GetString()!;
            var fileName = Path.GetFileName(p.TryGetProperty("fileName", out var fnEl)
                ? fnEl.GetString() ?? "" : new Uri(url).AbsolutePath);
            if (fileName.Length == 0) fileName = "modpack.zip";
            var ext = fileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ? ".mrpack" : ".zip";
            var dir = Path.Combine(Path.GetTempPath(), "starcloud-pack");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");
            Emit(stdout, "modpack.progress", new { stage = $"正在下载 {fileName}…", percent = -1 });
            await _download.DownloadFileAsync(url, dest, null, 3);
            return new { path = dest, fileName };
    }

    private static async Task<object?> HandleVersionsPackPreviewAsync(TextWriter stdout, JsonElement p)
    {
            var packPath = p.GetProperty("packPath").GetString()!;
            var probe = ModPackDetector.Detect(packPath)
                ?? throw new Exception("未能识别整合包格式。支持 Modrinth（.mrpack）、CurseForge（manifest.json）与 MultiMC / Prism（mmc-pack.json）。");
            string name; string gameVersion = "";
            var deps = new Dictionary<string, string>();
            int fileCount; long totalBytes; var contentRoot = probe.Root;
            switch (probe.Format)
            {
                case ModPackFormat.CurseForge:
                {
                    var info = _cfPacks.ReadPackageInfo(packPath);
                    name = info.Name; gameVersion = info.GameVersion;
                    if (!string.IsNullOrWhiteSpace(info.GameVersion)) deps["minecraft"] = info.GameVersion;
                    if (CurseForgePackInstaller.ParseLoader(info.LoaderId) is var (lname, lver) && lname is not null)
                        deps[lname] = lver ?? "";
                    fileCount = info.FileCount + info.OverrideFileCount;
                    totalBytes = info.TotalBytes;
                    break;
                }
                case ModPackFormat.MultiMc:
                {
                    var info = _multiMcPacks.ReadPackageInfo(packPath);
                    name = info.Name; gameVersion = info.GameVersion;
                    foreach (var kv in info.Dependencies) deps[kv.Key] = kv.Value;
                    fileCount = info.FileCount; totalBytes = info.TotalBytes;
                    break;
                }
                case ModPackFormat.Raw:
                {
                    var (count, bytes) = MeasurePackPrefix(packPath, probe.Root);
                    name = Path.GetFileNameWithoutExtension(packPath);
                    fileCount = count; totalBytes = bytes;
                    break;
                }
                default:
                {
                    var info = _mrPack.ReadPackageInfo(packPath);
                    name = info.Name; gameVersion = info.GameVersion;
                    foreach (var kv in info.Dependencies) deps[kv.Key] = kv.Value;
                    fileCount = info.DownloadFileCount + info.OverrideFileCount;
                    totalBytes = info.TotalBytes;
                    break;
                }
            }
            return new
            {
                format = probe.Format.ToString().ToLowerInvariant(),
                name, gameVersion, dependencies = deps,
                loader = DescribePackLoader(deps),
                fileCount, totalBytes, contentRoot,
            };
    }

    private static async Task<object?> HandleVersionsImportPackAsync(TextWriter stdout, JsonElement p)
    {
            var packPath = p.GetProperty("packPath").GetString()!;
            var targetId = p.GetProperty("targetId").GetString()!;
            var isolated = !p.TryGetProperty("isolated", out var isoEl) || isoEl.GetBoolean();
            var contentRoot = p.TryGetProperty("contentRoot", out var crEl) ? crEl.GetString() : null;
            var mcVersion = p.TryGetProperty("mcVersion", out var mcEl) ? mcEl.GetString() : null;
            return await ImportPackCoreAsync(stdout, packPath, targetId, isolated, mcVersion, contentRoot);
    }
}
