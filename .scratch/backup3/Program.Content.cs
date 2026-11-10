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

    private static async Task<object?> HandleSkinsListAsync(TextWriter stdout, JsonElement p)
    {
            return new { skins = _skins.List().Select(SkinPayload) };
    }

    private static async Task<object?> HandleSkinsImportPathAsync(TextWriter stdout, JsonElement p)
    {
            var path = p.GetProperty("path").GetString()!;
            string? name = p.TryGetProperty("name", out var nm) &&
                           nm.ValueKind == JsonValueKind.String
                ? nm.GetString()
                : null;
            var (entry, error) = _skins.ImportPath(path, name);
            if (entry is null) throw new Exception(error ?? "导入失败");
            return new { skin = SkinPayload(entry) };
    }

    private static async Task<object?> HandleSkinsImportUrlAsync(TextWriter stdout, JsonElement p)
    {
            var url = p.GetProperty("url").GetString()!;
            string? name = p.TryGetProperty("name", out var nm) &&
                           nm.ValueKind == JsonValueKind.String
                ? nm.GetString()
                : null;
            var (entry, error) = await _skins.ImportUrlAsync(url, name, _download);
            if (entry is null) throw new Exception(error ?? "导入失败");
            return new { skin = SkinPayload(entry) };
    }

    private static async Task<object?> HandleSkinsRenameAsync(TextWriter stdout, JsonElement p)
    {
            var id = p.GetProperty("id").GetString()!;
            var name = p.GetProperty("name").GetString()!;
            var (ok, error) = _skins.Rename(id, name);
            if (!ok) throw new Exception(error ?? "重命名失败");
            return new { skin = SkinPayload(_skins.Find(id)!) };
    }

    private static async Task<object?> HandleSkinsSetModelAsync(TextWriter stdout, JsonElement p)
    {
            var id = p.GetProperty("id").GetString()!;
            var model = SkinModels.Normalize(p.GetProperty("model").GetString());
            if (!_skins.SetModel(id, model))
                throw new Exception("皮肤不存在或型号非法");
            return new { skin = SkinPayload(_skins.Find(id)!) };
    }

    private static async Task<object?> HandleSkinsDeleteAsync(TextWriter stdout, JsonElement p)
    {
            var id = p.GetProperty("id").GetString()!;
            if (!_skins.Delete(id)) throw new Exception("皮肤不存在");
            // 账户上指向这张皮肤的引用一并清掉（与 Windows 端一致）。
            _accounts.ClearSkinReference(id);
            return new { ok = true };
    }

    private static async Task<object?> HandleSkinsStationListAsync(TextWriter stdout, JsonElement p)
    {
            var uuid = p.GetProperty("uuid").GetString()!;
            var account = _accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid)
                ?? throw new Exception("账户不存在");
            if (string.IsNullOrEmpty(account.ServerBase))
                throw new Exception("该账户没有认证服务器地址");
            var token = _accounts.GetSession(uuid)?.AccessToken
                ?? throw new Exception("第三方账户登录已过期，请重新登录");
            var list = await _skinStation.ListAsync(account.ServerBase, token);
            return new
            {
                skins = list.Select(s => new
                {
                    s.Id,
                    s.Name,
                    s.Model,
                    s.Url,
                    s.Slim,
                }),
            };
    }

    private static async Task<object?> HandleSkinsStationApplyAsync(TextWriter stdout, JsonElement p)
    {
        {
            var uuid = p.GetProperty("uuid").GetString()!;
            var skinId = p.GetProperty("skinId").GetString()!;
            var account = _accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid)
                ?? throw new Exception("账户不存在");
            if (string.IsNullOrEmpty(account.ServerBase))
                throw new Exception("该账户没有认证服务器地址");
            var token = _accounts.GetSession(uuid)?.AccessToken
                ?? throw new Exception("第三方账户登录已过期，请重新登录");
            var list = await _skinStation.ListAsync(account.ServerBase, token);
            var skin = list.FirstOrDefault(s => s.Id == skinId)
                ?? throw new Exception("皮肤站衣柜里没有这张皮肤");
            var message = await _skinStation.ApplyAsync(
                account.ServerBase, token, skin);
            return new { message };
        }

        // ── Blessing Skin 6 衣柜（Win 0.4.0：OAuth2 + PKCE，LittleSkin 一键授权） ──
        // serverBase 由壳端传入（账户的认证服务器），uuid 只是令牌存档键。
    }

    private static async Task<object?> HandleSkinsBs6StatusAsync(TextWriter stdout, JsonElement p)
    {
            var uuid = p.GetProperty("uuid").GetString()!;
            var account = _accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid)
                ?? throw new Exception("账户不存在");
            var root = SkinStationService.SiteRootOf(account.ServerBase ?? "");
            var major = await new BlessingSkin6Service().ProbeMajorAsync(root);
            var tokens = Bs6TokenStore.Get(_settings.DotMinecraft, uuid);
            return new { root, major, hasTokens = tokens is not null };
    }

    private static async Task<object?> HandleSkinsBs6AuthorizeAsync(TextWriter stdout, JsonElement p)
    {
            var uuid = p.GetProperty("uuid").GetString()!;
            var account = _accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid)
                ?? throw new Exception("账户不存在");
            var root = SkinStationService.SiteRootOf(account.ServerBase ?? "");
            var tokens = await new BlessingSkin6Service().AuthorizeAsync(root,
                BlessingSkin6Service.LittleSkinClientId,
                BlessingSkin6Service.LittleSkinClientSecret,
                uri => Process.Start(new ProcessStartInfo(uri.ToString())
                {
                    UseShellExecute = true,
                }));
            Bs6TokenStore.Set(_settings.DotMinecraft, uuid, tokens);
            return new { ok = true };
    }

    private static async Task<object?> HandleSkinsBs6ClosetAsync(TextWriter stdout, JsonElement p)
    {
            var uuid = p.GetProperty("uuid").GetString()!;
            var account = _accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid)
                ?? throw new Exception("账户不存在");
            var root = SkinStationService.SiteRootOf(account.ServerBase ?? "");
            var category = p.TryGetProperty("category", out var bcat) &&
                           bcat.ValueKind == JsonValueKind.String
                ? bcat.GetString()!
                : "skin";
            var tokens = await EnsureBs6TokensAsync(uuid, root);
            var items = await new BlessingSkin6Service().ListClosetAsync(
                root, tokens.AccessToken, category);
            return new
            {
                items = items.Select(s => new { s.Id, s.Name, s.Model, s.Url, s.Kind }),
            };
    }

    private static async Task<object?> HandleSkinsBs6ApplyAsync(TextWriter stdout, JsonElement p)
    {
            var uuid = p.GetProperty("uuid").GetString()!;
            var account = _accounts.Accounts.FirstOrDefault(a => a.Uuid == uuid)
                ?? throw new Exception("账户不存在");
            var root = SkinStationService.SiteRootOf(account.ServerBase ?? "");
            var tid = p.GetProperty("tid").GetString()!;
            var kind = p.TryGetProperty("kind", out var bkind) &&
                       bkind.ValueKind == JsonValueKind.String
                ? bkind.GetString()!
                : "skin";
            var tokens = await EnsureBs6TokensAsync(uuid, root);
            var bs6 = new BlessingSkin6Service();
            var players = await bs6.ListPlayersAsync(root, tokens.AccessToken);
            var picked = players.FirstOrDefault();
            if (picked.Name is null || picked.Pid is null)
                throw new Exception("该账号在皮肤站上还没有角色（player）");
            if (kind == "cape")
                await bs6.SetCapeAsync(root, tokens.AccessToken, picked.Pid!, tid);
            else
                await bs6.SetSkinAsync(root, tokens.AccessToken, picked.Pid!, tid);
            return new { player = picked.Name };
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

    private static object SkinPayload(SkinEntry entry) => new
    {
        entry.Id,
        entry.Name,
        entry.Model,
        entry.FileName,
        path = _skins.PathOf(entry),
        entry.Source,
        entry.SourceUrl,
        entry.Width,
        entry.Height,
        addedAt = entry.AddedAt.ToString("O"),
    };
    /// <summary>列出实例内容目录里的 jar/zip（`.disabled` 视为已停用）。</summary>
    private static object[] ContentFiles(string gameRoot, string kind)
    {
        var dir = Path.Combine(gameRoot, InstanceLayout.ContentDirFor(kind));
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir)
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                return n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                       n.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) ||
                       n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                       n.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase);
            })
            .Select(f =>
            {
                var name = Path.GetFileName(f);
                var disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                return new
                {
                    fileName = name,
                    displayName = disabled ? name[..^".disabled".Length] : name,
                    enabled = !disabled,
                    sizeBytes = new FileInfo(f).Length,
                    path = f,
                };
            })
            .OrderBy(x => x.displayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    /// <summary>
    /// 取 BS6 令牌：本地有未过期令牌直接用；有刷新令牌就续期；都没有抛可读错误
    /// （与 Win AccountDetailPage.EnsureBs6TokensAsync 同策略）。
    /// </summary>
    private static async Task<Bs6Tokens> EnsureBs6TokensAsync(string uuid, string root)
    {
        var bs6 = new BlessingSkin6Service();
        var tokens = Bs6TokenStore.Get(_settings.DotMinecraft, uuid);
        if (tokens is not null && tokens.ExpiresAt > DateTime.UtcNow.AddMinutes(1)) return tokens;

        if (!string.IsNullOrEmpty(tokens?.RefreshToken))
        {
            try
            {
                tokens = await bs6.RefreshAsync(root,
                    BlessingSkin6Service.LittleSkinClientId,
                    BlessingSkin6Service.LittleSkinClientSecret, tokens.RefreshToken);
                Bs6TokenStore.Set(_settings.DotMinecraft, uuid, tokens);
                return tokens;
            }
            catch
            {
                // 刷新令牌失效：走重新授权
            }
        }

        throw new InvalidOperationException("还没有连接皮肤站：请先在皮肤站卡片点「连接皮肤站」完成授权");
    }
}
