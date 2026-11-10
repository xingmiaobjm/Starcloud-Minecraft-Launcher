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

    private static async Task<object?> HandleContentSearchAsync(TextWriter stdout, JsonElement p)
    {
            var kind = NormalizeKind(p.GetProperty("kind").GetString());
            // Modrinth 的 project_type 与我们的 kind 同名（mod/modpack/datapack/resourcepack/shader）
            var projectType = kind;
            var query = p.TryGetProperty("query", out var q) &&
                        q.ValueKind == JsonValueKind.String
                ? q.GetString() ?? ""
                : "";
            var gameVersion = p.TryGetProperty("gameVersion", out var gv) &&
                              gv.ValueKind == JsonValueKind.String
                ? gv.GetString()
                : null;
            var loader = p.TryGetProperty("loader", out var ld) &&
                         ld.ValueKind == JsonValueKind.String
                ? ld.GetString()
                : null;
            var offset = p.TryGetProperty("offset", out var off) && off.TryGetInt32(out var o)
                ? o
                : 0;
            var limit = p.TryGetProperty("limit", out var lim) && lim.TryGetInt32(out var l)
                ? l
                : 20;
            var index = p.TryGetProperty("index", out var idxEl) && idxEl.ValueKind == JsonValueKind.String
                ? idxEl.GetString() ?? "relevance"
                : "relevance";
            var category = p.TryGetProperty("category", out var catEl) && catEl.ValueKind == JsonValueKind.String
                ? catEl.GetString()
                : null;
            var force = p.TryGetProperty("forceRefresh", out var frEl) && frEl.GetBoolean();
            var result = await _modrinth.SearchAsync(
                query, projectType, loader, gameVersion, offset, limit, index, category,
                CancellationToken.None, force);
            return new
            {
                totalHits = result.TotalHits,
                hits = result.Hits.Select(h => new
                {
                    h.ProjectId,
                    h.Slug,
                    h.Title,
                    h.Description,
                    h.Author,
                    h.Downloads,
                    h.IconUrl,
                    h.DisplayCategories,
                    h.Loaders,
                    h.WebUrl,
                }),
            };
    }

    private static async Task<object?> HandleContentInstallAsync(TextWriter stdout, JsonElement p)
    {
            var kind = NormalizeKind(p.GetProperty("kind").GetString());
            var projectId = p.GetProperty("projectId").GetString()!;
            var title = p.TryGetProperty("title", out var t) &&
                        t.ValueKind == JsonValueKind.String
                ? t.GetString()
                : null;
            var targetVersionId = p.GetProperty("targetVersionId").GetString()!;
            var ctx = VersionContentContext(targetVersionId);
            var installed = new List<object>();
            var missing = new List<object>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            async Task InstallOneAsync(
                string pid, string? versionHint, string? displayTitle, int depth)
            {
                if (depth > 5 || !visited.Add(pid)) return;
                var versions = await _modrinth.ListVersionsAsync(
                    pid, ctx.Loader, ctx.McVersion);
                if (versions.Count == 0)
                    versions = await _modrinth.ListVersionsAsync(pid, ctx.Loader, null);
                if (versions.Count == 0)
                    versions = await _modrinth.ListVersionsAsync(pid, null, ctx.McVersion);
                if (versions.Count == 0)
                {
                    missing.Add(new { projectId = pid, reason = "没有兼容版本" });
                    return;
                }
                var version = versionHint is null
                    ? null
                    : versions.FirstOrDefault(v => v.Id == versionHint);
                version ??= versions[0];
                var file = version.PrimaryFile;
                if (file is null)
                {
                    missing.Add(new { projectId = pid, reason = "没有可下载文件" });
                    return;
                }
                var name = displayTitle ??
                           await _modrinth.GetProjectTitleAsync(pid) ?? pid;
                Emit(stdout, "content.progress",
                    new { projectId = pid, stage = $"下载 {file.Filename}" });
                var entry = await _contentLibrary.DownloadAndAdoptAsync(
                    kind, file.Url, file.Filename, file.Sha1,
                    new LibraryAdoptInfo(
                        name, null, null, pid, version.Id, version.VersionNumber,
                        version.GameVersions, version.Loaders,
                        $"https://modrinth.com/project/{pid}",
                        ContentSourceIds.Modrinth));
                var path = await _contentLibrary.InstallToVersionAsync(
                    entry, targetVersionId);
                installed.Add(new
                {
                    projectId = pid,
                    title = name,
                    fileName = entry.FileName,
                    versionNumber = entry.VersionNumber,
                    path,
                });
                foreach (var dep in version.Dependencies.Where(d =>
                             d.DependencyType == "required" &&
                             !string.IsNullOrEmpty(d.ProjectId)))
                {
                    await InstallOneAsync(dep.ProjectId!, dep.VersionId, null, depth + 1);
                }
            }

            await InstallOneAsync(projectId, null, title, 0);
            return new { installed, missing };
    }

    private static async Task<object?> HandleContentTranslateAsync(TextWriter stdout, JsonElement p)
    {
            var raw = p.TryGetProperty("query", out var qEl2) && qEl2.ValueKind == JsonValueKind.String
                ? qEl2.GetString() ?? ""
                : "";
            var translated = await _translation.TranslateAsync(raw);
            return new
            {
                original = translated.Original,
                effective = translated.Effective,
                translated = translated.Translated,
                source = translated.SourceLabel,
            };
    }

    private static async Task<object?> HandleContentInstallModpackAsync(TextWriter stdout, JsonElement p)
    {
            var projectId = p.GetProperty("projectId").GetString()!;
            var versions = await _modrinth.ListVersionsAsync(projectId, null, null);
            var pick = versions.FirstOrDefault(v =>
                           v.PrimaryFile is not null &&
                           v.PrimaryFile.Filename.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase))
                       ?? versions.FirstOrDefault(v => v.PrimaryFile is not null)
                       ?? throw new Exception("该整合包没有可下载的文件。");
            var file = pick.PrimaryFile!;
            var title = await _modrinth.GetProjectTitleAsync(projectId) ?? projectId;
            var targetId = p.TryGetProperty("targetId", out var tEl2) && tEl2.ValueKind == JsonValueKind.String &&
                           !string.IsNullOrWhiteSpace(tEl2.GetString())
                ? tEl2.GetString()!
                : InstanceLayout.SafeFileName(title);
            var dir = Path.Combine(Path.GetTempPath(), "starcloud-pack");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir, $"{Guid.NewGuid():N}.mrpack");
            Emit(stdout, "modpack.progress", new { stage = $"下载 {file.Filename}", percent = -1 });
            await _download.DownloadFileAsync(file.Url, dest, file.Sha1, 3);
            try
            {
                return await ImportPackCoreAsync(stdout, dest, targetId, isolated: true, mcVersion: null, contentRoot: null);
            }
            finally
            {
                try { File.Delete(dest); } catch { }
            }
    }
}
