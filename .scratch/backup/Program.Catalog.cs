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

    private static async Task<object?> HandleCatalogVersionsAsync(TextWriter stdout, JsonElement p)
    {
            var force = p.TryGetProperty("force", out var f) && f.GetBoolean();
            var manifest = await _manifest.GetManifestAsync(force);
            var installed = _manifest.ListInstalled(_settings.DotMinecraft);
            var installedIds = installed.Select(i => i.Id).ToHashSet();
            return new
            {
                remote = manifest.Versions.Select(v => new
                {
                    v.Id,
                    v.Type,
                    v.Url,
                    v.ReleaseTime,
                    installed = installedIds.Contains(v.Id),
                }),
                installed = installed.Select(i => new { i.Id, i.SizeBytes }),
            };
    }

    private static async Task<object?> HandleCatalogInstalledAsync(TextWriter stdout, JsonElement p)
    {
            var instances = new InstanceSettingsStore(_settings.DotMinecraft);
            return new
            {
                installed = _manifest
                    .ListInstalled(_settings.DotMinecraft)
                    .Select(i => DescribeInstalled(i, instances)),
            };
    }

    private static async Task<object?> HandleVersionsDeleteAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            if (!InstanceLayout.IsValidVersionId(versionId))
                throw new Exception("非法版本 id");
            if (_installTask is { IsCompleted: false })
                throw new Exception("安装进行中，暂不能删除版本");
            if (Sessions.Values.Any(s => s.VersionId == versionId && !s.HasExited))
                throw new Exception("该版本正在运行，请先关闭游戏");

            var versionsRoot = Path.Combine(_settings.DotMinecraft, "versions");
            var dir = Path.Combine(versionsRoot, versionId);
            if (!Directory.Exists(dir))
                throw new Exception($"版本目录不存在：{versionId}");

            // 被其它版本的 inheritsFrom 依赖 → 拒绝：删掉父版本会让加载器实例无法启动。
            foreach (var sub in Directory.EnumerateDirectories(versionsRoot))
            {
                var otherId = Path.GetFileName(sub);
                if (otherId == versionId) continue;
                var json = Path.Combine(sub, otherId + ".json");
                try
                {
                    using var doc = JsonDocument.Parse(File.ReadAllText(json));
                    if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh) &&
                        inh.ValueKind == JsonValueKind.String &&
                        inh.GetString() == versionId)
                        throw new Exception($"版本 {otherId} 依赖该版本（inheritsFrom），不能删除");
                }
                catch (JsonException) { /* 坏 JSON 不阻塞删除 */ }
                catch (IOException) { /* 读不到就跳过 */ }
            }

            long freed = 0;
            try
            {
                freed = new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch { /* 统计失败不影响删除 */ }

            Directory.Delete(dir, recursive: true);
            var wasDefault = _settings.LastVersion == versionId;
            if (wasDefault)
            {
                _settings.LastVersion = null;
                _settings.Save();
            }
            return new { deleted = true, freedBytes = freed, wasDefault };
    }

    private static async Task<object?> HandleVersionsOpenFolderAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            if (!InstanceLayout.IsValidVersionId(versionId))
                throw new Exception("非法版本 id");
            // 文件夹入口跟随实例根（Win 同款）：隔离实例各自持有一份，共享实例落在 .minecraft 下
            var kind = p.TryGetProperty("kind", out var ofKind) && ofKind.ValueKind == JsonValueKind.String
                ? ofKind.GetString()
                : null;
            string dir;
            if (kind is "saves" or "mods")
            {
                var store = new InstanceSettingsStore(_settings.DotMinecraft);
                var isolated = InstanceLayout.IsIsolated(store.Get(versionId), _settings);
                var root = InstanceLayout.GameRoot(_settings.DotMinecraft, versionId, isolated);
                dir = Path.Combine(root, kind);
            }
            else
            {
                dir = Path.Combine(_settings.DotMinecraft, "versions", versionId);
            }
            Directory.CreateDirectory(dir);
            OpenFolder(dir);
            return new { ok = true, path = dir };
    }

    private static async Task<object?> HandleVersionsSetDefaultAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.TryGetProperty("versionId", out var v) &&
                            v.ValueKind == JsonValueKind.String
                ? v.GetString()
                : null;
            if (!string.IsNullOrEmpty(versionId) &&
                !Directory.Exists(Path.Combine(_settings.DotMinecraft, "versions", versionId)))
                throw new Exception($"版本目录不存在：{versionId}");
            _settings.LastVersion = string.IsNullOrEmpty(versionId) ? null : versionId;
            _settings.Save();
            return new { ok = true, lastVersion = _settings.LastVersion };
    }

    private static async Task<object?> HandleVersionsSetMarksAsync(TextWriter stdout, JsonElement p)
    {
            // 置顶 / 收藏标记（与 Win 端主页版本卡的标记口径一致：存实例档案）。
            // 两个开关都可选：传了才改，没传保持原值。
            var versionId = p.GetProperty("versionId").GetString()!;
            if (!Directory.Exists(Path.Combine(_settings.DotMinecraft, "versions", versionId)))
                throw new Exception($"版本目录不存在：{versionId}");
            var store = new InstanceSettingsStore(_settings.DotMinecraft);
            var profile = store.Get(versionId);
            if (p.TryGetProperty("pinned", out var pin) && IsBool(pin))
                profile.Pinned = pin.GetBoolean();
            if (p.TryGetProperty("favorite", out var fav) && IsBool(fav))
                profile.Favorite = fav.GetBoolean();
            store.Set(versionId, profile);
            store.Save();
            return new { ok = true, pinned = profile.Pinned, favorite = profile.Favorite };
    }

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

    private static async Task<object?> HandleVersionDetailAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var ctx = VersionContentContext(versionId);
            return new
            {
                versionId,
                gameRoot = ctx.GameRoot,
                isolated = ctx.Isolated,
                mcVersion = ctx.McVersion,
                loader = ctx.Loader,
                loaderVersion = ctx.LoaderVersion,
                lastPlayedAt = new InstanceSettingsStore(_settings.DotMinecraft)
                    .Get(versionId).LastPlayedAt?.ToString("O"),
                mods = ContentFiles(ctx.GameRoot, "mod"),
                resourcepacks = ContentFiles(ctx.GameRoot, "resourcepack"),
                shaderpacks = ContentFiles(ctx.GameRoot, "shader"),
            };
    }

    private static async Task<object?> HandleVersionToggleContentAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var kind = NormalizeKind(p.GetProperty("kind").GetString());
            var fileName = SafeFileName(p.GetProperty("fileName").GetString()!);
            var enabled = p.GetProperty("enabled").GetBoolean();
            var ctx = VersionContentContext(versionId);
            var dir = Path.Combine(ctx.GameRoot, InstanceLayout.ContentDirFor(kind));
            var current = Path.Combine(dir, fileName);
            if (!File.Exists(current)) throw new Exception($"文件不存在：{fileName}");
            var disabled = current.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            if (enabled != disabled) return new { files = ContentFiles(ctx.GameRoot, kind) };
            var target = enabled ? current[..^".disabled".Length] : current + ".disabled";
            if (File.Exists(target)) throw new Exception("目标文件已存在，未做改名");
            File.Move(current, target);
            return new { files = ContentFiles(ctx.GameRoot, kind) };
    }

    private static async Task<object?> HandleVersionDeleteContentAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var kind = NormalizeKind(p.GetProperty("kind").GetString());
            var fileName = SafeFileName(p.GetProperty("fileName").GetString()!);
            var ctx = VersionContentContext(versionId);
            var path = Path.Combine(
                ctx.GameRoot, InstanceLayout.ContentDirFor(kind), fileName);
            if (!File.Exists(path)) throw new Exception($"文件不存在：{fileName}");
            File.Delete(path);
            return new { files = ContentFiles(ctx.GameRoot, kind) };
    }

    private static async Task<object?> HandleVersionOpenContentFolderAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var kind = NormalizeKind(p.GetProperty("kind").GetString());
            var ctx = VersionContentContext(versionId);
            var dir = Path.Combine(ctx.GameRoot, InstanceLayout.ContentDirFor(kind));
            Directory.CreateDirectory(dir);
            OpenFolder(dir);
            return new { ok = true, path = dir };
    }

    private static async Task<object?> HandleVersionsRenameAsync(TextWriter stdout, JsonElement p)
    {
            var oldId = p.GetProperty("versionId").GetString()!;
            var newId = p.GetProperty("newName").GetString()!.Trim();
            if (!InstanceLayout.IsValidVersionId(oldId) || !InstanceLayout.IsValidVersionId(newId))
                throw new Exception("版本名不能为空，且不能包含 < > : \" / \\ | ? *");
            if (newId != oldId)
            {
                InstanceLayout.RenameInstance(_settings.DotMinecraft, oldId, newId);
                new InstanceSettingsStore(_settings.DotMinecraft).RenameKey(oldId, newId);
            }
            return new { ok = true, versionId = newId };
    }

    private static async Task<object?> HandleVersionsCopyAsync(TextWriter stdout, JsonElement p)
    {
            var srcId = p.GetProperty("versionId").GetString()!;
            var newId = p.GetProperty("newId").GetString()!.Trim();
            if (!InstanceLayout.IsValidVersionId(srcId) || !InstanceLayout.IsValidVersionId(newId))
                throw new Exception("版本名不能为空，且不能包含 < > : \" / \\ | ? *");
            var srcStore = new InstanceSettingsStore(_settings.DotMinecraft);
            var copyContent = p.TryGetProperty("copyContent", out var cc) && cc.GetBoolean();
            InstanceLayout.CopyInstance(_settings.DotMinecraft, srcId, newId, copyContent);
            // 档案深拷贝（Win 同款）：类别/备注等沿用，时间与图标从零开始
            var copy = JsonSerializer.Deserialize<VersionProfile>(
                JsonSerializer.Serialize(srcStore.Get(srcId))) ?? new VersionProfile();
            copy.LastPlayedAt = null;
            copy.IconPath = null;
            srcStore.Set(newId, copy);
            srcStore.Save();
            return new { ok = true, versionId = newId };
    }

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

    private static async Task<object?> HandleVersionsBackupListAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.TryGetProperty("versionId", out var blEl) && blEl.ValueKind == JsonValueKind.String
                ? blEl.GetString()
                : null;
            return new { entries = _versionBackup.List(_settings.DotMinecraft, versionId) };
    }

    private static async Task<object?> HandleVersionsBackupCreateAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var store = new InstanceSettingsStore(_settings.DotMinecraft);
            var isolated = InstanceLayout.IsIsolated(store.Get(versionId), _settings);
            var dirs = new List<string>();
            if (p.TryGetProperty("dirs", out var bdEl) && bdEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in bdEl.EnumerateArray())
                {
                    var dirName = d.GetString();
                    if (!string.IsNullOrWhiteSpace(dirName)) dirs.Add(dirName);
                }
            }
            var includeSaves = !p.TryGetProperty("includeSaves", out var isEl) || isEl.GetBoolean();
            var backupName = p.TryGetProperty("name", out var bnEl) && bnEl.ValueKind == JsonValueKind.String
                && bnEl.GetString() is { Length: > 0 } bn ? bn : "备份";
            var status = new Progress<string>(st =>
                Emit(stdout, "modpack.progress", new { stage = st, percent = -1 }));
            var entry = await _versionBackup.BackupAsync(_settings.DotMinecraft,
                new VersionBackupRequest(versionId, isolated, backupName, dirs, includeSaves), status);
            return new { ok = true, entry };
    }

    private static async Task<object?> HandleVersionsBackupRestoreAsync(TextWriter stdout, JsonElement p)
    {
            var entryId = p.GetProperty("entryId").GetString()!;
            var versionId = p.TryGetProperty("versionId", out var brEl) ? brEl.GetString() : null;
            var entry = _versionBackup.List(_settings.DotMinecraft, versionId)
                .FirstOrDefault(e => e.Id == entryId)
                ?? throw new Exception("找不到这条备份（索引可能已重建）。");
            var store = new InstanceSettingsStore(_settings.DotMinecraft);
            var isolated = InstanceLayout.IsIsolated(store.Get(entry.VersionId), _settings);
            var status = new Progress<string>(s =>
                Emit(stdout, "modpack.progress", new { stage = s, percent = -1 }));
            await _versionBackup.RestoreAsync(_settings.DotMinecraft, entry, isolated, status);
            return new { ok = true, entryId };
    }

    private static async Task<object?> HandleVersionsBackupDeleteAsync(TextWriter stdout, JsonElement p)
    {
            _versionBackup.Delete(_settings.DotMinecraft, p.GetProperty("entryId").GetString()!);
            return new { ok = true };
    }

    private static async Task<object?> HandleVersionsBackupRebuildAsync(TextWriter stdout, JsonElement p)
    {
            return new { ok = true, entries = _versionBackup.RebuildIndex(_settings.DotMinecraft) };
    }

    /// <summary>版本实例设置：档案原值 + 层叠后的生效值。</summary>
    private static object VersionSettingsPayload(string versionId)
    {
        if (!InstanceLayout.IsValidVersionId(versionId))
            throw new Exception("非法版本 id");
        var dotMc = _settings.DotMinecraft;
        var profile = new InstanceSettingsStore(dotMc).Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _settings);
        var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
        var eff = InstanceLayout.Resolve(
            dotMc, versionId, profile, _settings, MemoryAdvisor.Probe(gameRoot));
        return new
        {
            versionId,
            isolation = profile.Isolation,
            memoryMode = profile.MemoryMode ?? "global",
            memoryMinMb = profile.MemoryMinMb,
            memoryMaxMb = profile.MemoryMaxMb,
            javaMode = profile.JavaMode ?? "global",
            javaPath = profile.JavaPath,
            windowMode = profile.WindowMode ?? "global",
            windowWidth = profile.WindowWidth,
            windowHeight = profile.WindowHeight,
            fullscreen = profile.Fullscreen,
            serverAddress = profile.ServerAddress,
            extraJvmArgs = profile.ExtraJvmArgs,
            extraGameArgs = profile.ExtraGameArgs,
            effective = new
            {
                gameRoot,
                isolated,
                minMem = eff.MinMem,
                maxMem = eff.MaxMem,
                java = eff.Java,
                windowW = eff.WindowW,
                windowH = eff.WindowH,
                fullscreen = eff.Fullscreen,
            },
        };
    }
    /// <summary>版本列表条目：目录/JSON/jar/依赖/默认/隔离状态一次给全，UI 不再自己猜。</summary>
    private static object DescribeInstalled(
        InstalledVersionInfo info, InstanceSettingsStore instances)
    {
        var dir = Path.Combine(_settings.DotMinecraft, "versions", info.Id);
        var json = Path.Combine(dir, info.Id + ".json");
        string? inheritsFrom = null;
        string? versionType = null;
        // 整合包的版本 JSON 常没有 inheritsFrom，加载器信息藏在 libraries 里
        // （net.neoforged:neoforge:...），所以按 Win 端口径扫描全文。
        string? jsonText = null;
        var jarExists = false;
        try
        {
            if (File.Exists(json))
            {
                var text = File.ReadAllText(json);
                jsonText = text;
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh) &&
                    inh.ValueKind == JsonValueKind.String)
                    inheritsFrom = inh.GetString();
                // 版本类型（release / snapshot / old_beta / old_alpha）：
                // 壳端据此选图标（草方块 / 紫水晶 / 圆石 / 泥土），与 Win 端口径一致。
                if (doc.RootElement.TryGetProperty("type", out var tp) &&
                    tp.ValueKind == JsonValueKind.String)
                    versionType = tp.GetString();
            }
            // 与 GameLauncher.ResolveClientJar 的回退口径一致：同名 jar 或目录内唯一客户端 jar。
            jarExists = File.Exists(Path.Combine(dir, info.Id + ".jar")) ||
                Directory.EnumerateFiles(dir, "*.jar").Any(f =>
                    !f.Contains("-sources", StringComparison.OrdinalIgnoreCase) &&
                    !f.Contains("-javadoc", StringComparison.OrdinalIgnoreCase) &&
                    !f.Contains("-dev", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // 单个版本读失败不影响整个列表
        }

        return new
        {
            info.Id,
            sizeBytes = info.SizeBytes,
            directory = dir,
            jsonPath = json,
            jarExists,
            inheritsFrom,
            loader = DetectLoader(info.Id, inheritsFrom, jsonText),
            type = versionType ?? InferVersionType(info.Id),
            lastPlayedAt = instances.Get(info.Id).LastPlayedAt?.ToString("O"),
            isDefault = _settings.LastVersion == info.Id,
            isolated = _settings.VersionIsolated,
            favorite = instances.Get(info.Id).Favorite,
            pinned = instances.Get(info.Id).Pinned,
            iconPath = instances.Get(info.Id).IconPath ?? "",
        };
    }

    /// <summary>从版本 id 推断加载器（顺序敏感：neoforge 必须先于 forge）。</summary>
    private static string DetectLoader(string id, string? inheritsFrom, string? jsonText)
    {
        var lower = (id + " " + (inheritsFrom ?? "") + " " + (jsonText ?? "")).ToLowerInvariant();
        if (lower.Contains("neoforge")) return "neoforge";
        if (lower.Contains("forge")) return "forge";
        if (lower.Contains("fabric")) return "fabric";
        if (lower.Contains("quilt")) return "quilt";
        return "vanilla";
    }

    /// <summary>版本 JSON 缺 type 时的兜底推断。</summary>
    private static string InferVersionType(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("-rc") || lower.Contains("-pre") || lower.Contains("-snapshot"))
            return "snapshot";
        return "release";
    }
    private static async Task RunInstallAsync(TextWriter stdout, int id, string versionId)
    {
        try
        {
            var entry = await FindManifestEntryAsync(versionId)
                        ?? throw new Exception($"版本 {versionId} 不在官方清单里");
            var progress = new Progress<BatchProgress>(b => Emit(stdout, "install.progress", new
            {
                versionId,
                stage = b.Stage,
                detail = $"{b.DoneFiles}/{b.TotalFiles}",
                current = b.DoneFiles,
                total = b.TotalFiles,
                percent = b.Percentage,
                speedBytesPerSec = b.SpeedBytesPerSec,
            }));
            await _installer.InstallVanillaAsync(versionId, entry.Url, _settings, progress);
            Emit(stdout, "install.done", new { versionId });
            Reply(stdout, id, ok: true, result: new { versionId, installed = true });
        }
        catch (Exception e)
        {
            Emit(stdout, "install.failed", new { versionId, error = e.Message });
            Reply(stdout, id, ok: false, error: e.Message);
        }
    }

    private static async Task<ManifestVersion?> FindManifestEntryAsync(string versionId)
    {
        var manifest = await _manifest.GetManifestAsync(forceRefresh: false);
        return manifest.Versions.FirstOrDefault(v => v.Id == versionId);
    }
}
