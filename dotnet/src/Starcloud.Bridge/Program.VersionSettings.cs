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

    private static async Task<object?> HandleVersionSettingsAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            return VersionSettingsPayload(versionId);
    }

    private static async Task<object?> HandleVersionSaveSettingsAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            if (!InstanceLayout.IsValidVersionId(versionId))
                throw new Exception("非法版本 id");
            var store = new InstanceSettingsStore(_settings.DotMinecraft);
            var profile = store.Get(versionId);
            if (p.TryGetProperty("isolation", out var iso))
            {
                profile.Isolation = iso.ValueKind == JsonValueKind.String &&
                                    iso.GetString() is "shared" or "isolated"
                    ? iso.GetString()
                    : null;
            }
            if (p.TryGetProperty("memoryMode", out var mm))
                profile.MemoryMode = NormalizeMode(mm);
            if (p.TryGetProperty("memoryMinMb", out var mn))
                profile.MemoryMinMb = ReadNullableInt(mn);
            if (p.TryGetProperty("memoryMaxMb", out var mx))
                profile.MemoryMaxMb = ReadNullableInt(mx);
            if (p.TryGetProperty("javaMode", out var jm))
                profile.JavaMode = NormalizeMode(jm);
            if (p.TryGetProperty("javaPath", out var jp))
                profile.JavaPath = jp.ValueKind == JsonValueKind.String
                    ? jp.GetString()
                    : null;
            if (p.TryGetProperty("windowMode", out var wm))
                profile.WindowMode = NormalizeMode(wm);
            if (p.TryGetProperty("windowWidth", out var ww))
                profile.WindowWidth = ReadNullableInt(ww);
            if (p.TryGetProperty("windowHeight", out var wh))
                profile.WindowHeight = ReadNullableInt(wh);
            if (p.TryGetProperty("fullscreen", out var fs))
                profile.Fullscreen = ReadNullableBool(fs);
            store.Set(versionId, profile);
            store.Save();
            return VersionSettingsPayload(versionId);
    }

    private static async Task<object?> HandleVersionCheckUpdatesAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var ctx = VersionContentContext(versionId);
            var modsDir = Path.Combine(ctx.GameRoot, "mods");
            if (!Directory.Exists(modsDir)) return new { updates = Array.Empty<object>() };
            var files = Directory.EnumerateFiles(modsDir)
                .Where(f => f.Contains(".jar", StringComparison.OrdinalIgnoreCase))
                .Take(200)
                .ToList();
            var updates = new List<object>();
            var gate = new SemaphoreSlim(4);
            var tasks = files.Select(async file =>
            {
                await gate.WaitAsync();
                try
                {
                    var result = await _contentLibrary.CheckUpdateAsync(
                        file, ctx.McVersion, ctx.Loader);
                    if (result is null) return;
                    lock (updates)
                    {
                        updates.Add(new
                        {
                            result.FileName,
                            result.CurrentVersion,
                            result.LatestVersion,
                            result.LatestFileName,
                            result.LatestUrl,
                            result.LatestSha1,
                        });
                    }
                }
                catch
                {
                    // 单个文件检查失败不阻塞其它更新
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);
            return new { updates };
    }

    private static async Task<object?> HandleVersionUpdateModAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var fileName = SafeFileName(p.GetProperty("fileName").GetString()!);
            var ctx = VersionContentContext(versionId);
            var modsDir = Path.Combine(ctx.GameRoot, "mods");
            var oldPath = Path.Combine(modsDir, fileName);
            if (!File.Exists(oldPath)) throw new Exception($"文件不存在：{fileName}");
            var update = await _contentLibrary.CheckUpdateAsync(
                oldPath, ctx.McVersion, ctx.Loader)
                ?? throw new Exception("该模组没有可用更新");
            Emit(stdout, "content.progress",
                new { projectId = "", stage = $"下载 {update.LatestFileName}" });
            var entry = await _contentLibrary.DownloadAndAdoptAsync(
                "mod", update.LatestUrl, update.LatestFileName, update.LatestSha1,
                new LibraryAdoptInfo(
                    Path.GetFileNameWithoutExtension(update.LatestFileName),
                    null, null, null, null, update.LatestVersion,
                    new List<string>(), new List<string>(), null,
                    ContentSourceIds.Modrinth));
            var newPath = await _contentLibrary.InstallToVersionAsync(entry, versionId);
            // 原本停用的模组更新后保持停用。
            if (fileName.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase) &&
                !newPath.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase))
            {
                var disabledPath = newPath + ".disabled";
                File.Move(newPath, disabledPath, overwrite: true);
                newPath = disabledPath;
            }
            if (!string.Equals(newPath, oldPath, StringComparison.OrdinalIgnoreCase))
            {
                try { File.Delete(oldPath); } catch { /* 占用中则保留旧文件 */ }
            }
            return new
            {
                ok = true,
                fileName = Path.GetFileName(newPath),
                oldFileName = fileName,
                latestVersion = update.LatestVersion,
            };
    }

    private static async Task<object?> HandleVersionCheckDependenciesAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var ctx = VersionContentContext(versionId);
            var modsDir = Path.Combine(ctx.GameRoot, "mods");
            var missing = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var optional = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            var incompatible = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (!Directory.Exists(modsDir))
            {
                return new
                {
                    missing = Array.Empty<object>(),
                    optional = Array.Empty<object>(),
                    incompatible = Array.Empty<string>(),
                    truncated = false,
                };
            }

            var files = Directory.EnumerateFiles(modsDir)
                .Where(f => f.Contains(".jar", StringComparison.OrdinalIgnoreCase))
                .Take(200)
                .ToList();
            var hashes = await Task.Run(() => files.Select(f =>
                Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(f)))).ToList());
            var identities = await _contentLibrary.ResolveIdentitiesAsync(hashes);
            var installedProjects = identities.Values
                .Select(i => i.ProjectId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            var titles = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var inspected = 0;

            var gate = new SemaphoreSlim(4);
            var tasks = identities.Values.Select(async identity =>
            {
                await gate.WaitAsync();
                try
                {
                    var versions = await _modrinth.ListVersionsAsync(
                        identity.ProjectId, ctx.Loader, ctx.McVersion);
                    var version = versions.FirstOrDefault(v => v.Id == identity.VersionId);
                    if (version is null)
                    {
                        versions = await _modrinth.ListVersionsAsync(
                            identity.ProjectId, null, null);
                        version = versions.FirstOrDefault(v => v.Id == identity.VersionId);
                    }
                    if (version is null) return;
                    Interlocked.Increment(ref inspected);

                    foreach (var dep in version.Dependencies)
                    {
                        if (dep.ProjectId is null) continue;
                        switch (dep.DependencyType)
                        {
                            case "required":
                                if (installedProjects.Contains(dep.ProjectId)) break;
                                var title = await TitleOf(dep.ProjectId, titles);
                                lock (missing)
                                {
                                    missing[dep.ProjectId] = new
                                    {
                                        projectId = dep.ProjectId,
                                        title,
                                        versionId = dep.VersionId,
                                        fileName = dep.FileName,
                                    };
                                }
                                break;
                            case "optional":
                                var optionalTitle = await TitleOf(dep.ProjectId, titles);
                                lock (optional)
                                {
                                    optional[dep.ProjectId] = new
                                    {
                                        projectId = dep.ProjectId,
                                        title = optionalTitle,
                                    };
                                }
                                break;
                            case "incompatible":
                                if (!installedProjects.Contains(dep.ProjectId)) break;
                                var incompatibleTitle = await TitleOf(dep.ProjectId, titles);
                                lock (incompatible)
                                {
                                    incompatible.Add(incompatibleTitle);
                                }
                                break;
                        }
                    }
                }
                catch
                {
                    // 单个模组解析失败不影响整体报告
                }
                finally
                {
                    gate.Release();
                }
            });
            await Task.WhenAll(tasks);
            return new
            {
                missing = missing.Values,
                optional = optional.Values,
                incompatible = incompatible.ToArray(),
                truncated = files.Count >= 200,
                checkedMods = files.Count,
                resolvedMods = identities.Count,
                inspectedVersions = inspected,
            };
    }
}
