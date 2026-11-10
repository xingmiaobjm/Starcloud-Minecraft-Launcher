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
