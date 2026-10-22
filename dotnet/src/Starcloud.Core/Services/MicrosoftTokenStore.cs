// 正版登录凭证的本地保管：存 OAuth refresh_token 与（可选的）access_token，不存密码，
// 也不在 UI 展示。access_token 是否落盘由设置「加密保存正版登录令牌」控制（默认开）。
//
// 规则：
//   · Windows 上用 DPAPI（CurrentUser）加密后落盘；其他平台退化为 Base64（桌面主战场是 Windows）
//   · 有效期 30 天（从最近一次完整登录算起）；过期条目在读取时惰性清理
//   · 每次刷新只轮换 token，不改 saved_at —— 30 天是从“最后一次手动登录”起算
//   · 删除账户 / 刷新失败时立即清掉，避免拿废 token 反复重试

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

internal sealed class MicrosoftTokenEntry
{
    [JsonPropertyName("token")]
    public string Token { get; set; } = "";

    /// <summary>access_token（DPAPI 加密串）；关闭「加密保存令牌」时为空</summary>
    [JsonPropertyName("access_token")]
    public string? AccessToken { get; set; }

    /// <summary>access_token 过期时间（UTC）；空 = 未保存或不可用</summary>
    [JsonPropertyName("access_expires_at")]
    public DateTime? AccessExpiresAt { get; set; }

    /// <summary>Xbox 用户 id（在线功能与皮肤用；随 access_token 一起落盘）</summary>
    [JsonPropertyName("xuid")]
    public string? Xuid { get; set; }

    /// <summary>UTC；用于 30 天有效期</summary>
    [JsonPropertyName("saved_at")]
    public DateTime SavedAt { get; set; }
}

public static class MicrosoftTokenStore
{
    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(30);

    /// <summary>Minecraft 服务令牌约 24 小时有效；按 23 小时记，留出刷新余量。</summary>
    public static readonly TimeSpan AccessLifetime = TimeSpan.FromHours(23);

    /// <summary>access_token 的过期时刻（UTC）</summary>
    public static DateTime AccessExpiry() => UtcNow().Add(AccessLifetime);

    /// <summary>测试钩子：替换当前时间 / 指向临时文件</summary>
    internal static Func<DateTime> UtcNow = () => DateTime.UtcNow;
    internal static string? FilePathOverride;

    private static readonly object Gate = new();
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private static string PathOf => FilePathOverride ?? LauncherPaths.MicrosoftTokensFile;

    /// <summary>取有效 refresh_token；不存在 / 过期返回 false（过期条目顺手删除）。</summary>
    public static bool TryGet(string uuid, out string refreshToken)
    {
        refreshToken = "";
        if (string.IsNullOrWhiteSpace(uuid)) return false;
        lock (Gate)
        {
            var all = Load();
            if (!all.TryGetValue(uuid, out var entry)) return false;
            if (UtcNow() - entry.SavedAt > Lifetime)
            {
                all.Remove(uuid);
                Save(all);
                return false;
            }
            var token = Unprotect(entry.Token);
            if (string.IsNullOrEmpty(token))
            {
                all.Remove(uuid);
                Save(all);
                return false;
            }
            refreshToken = token;
            return true;
        }
    }

    public static bool HasValid(string uuid) => TryGet(uuid, out _);

    /// <summary>手动登录成功：写入并重新开始 30 天计时。</summary>
    public static void SaveNew(string uuid, string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(refreshToken)) return;
        lock (Gate)
        {
            var all = Load();
            all[uuid] = new MicrosoftTokenEntry
            {
                Token = Protect(refreshToken),
                SavedAt = UtcNow(),
            };
            Save(all);
        }
    }

    /// <summary>
    /// 手动登录成功：写入 refresh_token 并（按设置）同时加密保存 access_token。
    /// <paramref name="accessExpiresAt"/> 为 null 时只保存 refresh_token。
    /// </summary>
    public static void SaveSession(string uuid, string refreshToken,
        string? accessToken = null, DateTime? accessExpiresAt = null, string? xuid = null)
    {
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(refreshToken)) return;
        lock (Gate)
        {
            var all = Load();
            all[uuid] = new MicrosoftTokenEntry
            {
                Token = Protect(refreshToken),
                AccessToken = string.IsNullOrEmpty(accessToken) ? null : Protect(accessToken),
                AccessExpiresAt = string.IsNullOrEmpty(accessToken) ? null : accessExpiresAt,
                Xuid = string.IsNullOrEmpty(accessToken) ? null : xuid,
                SavedAt = UtcNow(),
            };
            Save(all);
        }
    }

    /// <summary>刷新后轮换 access_token（refresh 用 <see cref="Update"/> 单独处理）。</summary>
    public static void UpdateAccess(string uuid, string accessToken, DateTime? expiresAt, string? xuid = null)
    {
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(accessToken)) return;
        lock (Gate)
        {
            var all = Load();
            if (all.TryGetValue(uuid, out var entry))
            {
                entry.AccessToken = Protect(accessToken);
                entry.AccessExpiresAt = expiresAt;
                if (!string.IsNullOrEmpty(xuid)) entry.Xuid = xuid;
                Save(all);
            }
        }
    }

    /// <summary>取落盘且未过期的 access_token（免一次网络刷新）；失效/未保存返回 false。</summary>
    public static bool TryGetAccess(string uuid, out string accessToken) =>
        TryGetAccess(uuid, out accessToken, out _);

    /// <summary>同 <see cref="TryGetAccess(string, out string)"/>，并带回 xuid。</summary>
    public static bool TryGetAccess(string uuid, out string accessToken, out string? xuid)
    {
        accessToken = "";
        xuid = null;
        if (string.IsNullOrWhiteSpace(uuid)) return false;
        lock (Gate)
        {
            var all = Load();
            if (!all.TryGetValue(uuid, out var entry)) return false;
            if (UtcNow() - entry.SavedAt > Lifetime) return false;
            if (string.IsNullOrEmpty(entry.AccessToken)) return false;
            // 提前 60 秒视为过期，避免边界上刚好失效
            if (entry.AccessExpiresAt is { } exp && UtcNow() >= exp.AddSeconds(-60)) return false;
            var token = Unprotect(entry.AccessToken);
            if (string.IsNullOrEmpty(token)) return false;
            accessToken = token;
            xuid = entry.Xuid;
            return true;
        }
    }

    /// <summary>自动刷新成功：只轮换 token，保留原来的 30 天起点。</summary>
    public static void Update(string uuid, string refreshToken)
    {
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(refreshToken)) return;
        lock (Gate)
        {
            var all = Load();
            if (all.TryGetValue(uuid, out var entry))
            {
                entry.Token = Protect(refreshToken);   // access 由 UpdateAccess 单独轮换
            }
            else
            {
                all[uuid] = new MicrosoftTokenEntry { Token = Protect(refreshToken), SavedAt = UtcNow() };
            }
            Save(all);
        }
    }

    public static void Remove(string uuid)
    {
        if (string.IsNullOrWhiteSpace(uuid)) return;
        lock (Gate)
        {
            var all = Load();
            if (all.Remove(uuid)) Save(all);
        }
    }

    // ── 持久化 ──

    private static Dictionary<string, MicrosoftTokenEntry> Load()
    {
        try
        {
            var path = PathOf;
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            var json = File.ReadAllText(path);
            var data = JsonSerializer.Deserialize<Dictionary<string, MicrosoftTokenEntry>>(json, Json);
            return data is null
                ? new(StringComparer.Ordinal)
                : new Dictionary<string, MicrosoftTokenEntry>(data, StringComparer.Ordinal);
        }
        catch
        {
            return new(StringComparer.Ordinal); // 损坏就当作没有，下一次保存会重写
        }
    }

    private static void Save(Dictionary<string, MicrosoftTokenEntry> all)
    {
        try
        {
            var path = PathOf;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(all, Json),
                new UTF8Encoding(false));
        }
        catch (Exception ex)
        {
            LauncherLog.Write("auth", $"保存正版登录凭证失败：{ex.Message}");
        }
    }

    // ── 加密（Windows: DPAPI；其他平台: 仅编码，便于跨平台编译/运行） ──

    private static string Protect(string plain)
    {
        var bytes = Encoding.UTF8.GetBytes(plain);
        if (OperatingSystem.IsWindows())
        {
            var encrypted = ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser);
            return "dpapi:" + Convert.ToBase64String(encrypted);
        }
        return "plain:" + Convert.ToBase64String(bytes);
    }

    private static string? Unprotect(string stored)
    {
        try
        {
            if (stored.StartsWith("dpapi:", StringComparison.Ordinal))
            {
                if (!OperatingSystem.IsWindows()) return null;
                var bytes = ProtectedData.Unprotect(
                    Convert.FromBase64String(stored[6..]), null, DataProtectionScope.CurrentUser);
                return Encoding.UTF8.GetString(bytes);
            }
            if (stored.StartsWith("plain:", StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored[6..]));
        }
        catch (Exception ex)
        {
            LauncherLog.Write("auth", $"读取正版登录凭证失败：{ex.Message}");
        }
        return null;
    }
}
