// 第三方（Yggdrasil / 皮肤站）登录令牌持久化：DPAPI 加密落盘，重启后免重登。
//
// 与 MicrosoftTokenStore 同一套保护级别（CurrentUser DPAPI），但保护对象是
// 皮肤站的 access_token：第三方登录本无 refresh_token，令牌由皮肤站签发、
// 有效期各站不一（常见 14~30 天），过期后仍需重新登录一次。
//
// 生命周期由 AccountService 接线：外部账号线程的 SetSession 时写入、删除账号时清除、
// 内存会话缺失时惰性恢复（GetSession）。

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Services;

public static class ExternalTokenStore
{
    private const int LifetimeDays = 14;

    private sealed record Entry(string Token, DateTime SavedAt);

    private static readonly object Gate = new();
    private static string PathOf => LauncherPaths.ExternalTokensFile;

    /// <summary>取有效令牌；不存在 / 超期 / 解密失败返回 false（失效条目顺手删除）。</summary>
    public static bool TryGet(string uuid, out string accessToken)
    {
        accessToken = "";
        if (string.IsNullOrWhiteSpace(uuid)) return false;
        lock (Gate)
        {
            var all = Load();
            if (!all.TryGetValue(uuid, out var entry)) return false;
            if (DateTime.UtcNow - entry.SavedAt > TimeSpan.FromDays(LifetimeDays))
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
            accessToken = token;
            return true;
        }
    }

    /// <summary>登录 / 换票成功：写入并重新计时。</summary>
    public static void SaveNew(string uuid, string accessToken)
    {
        if (string.IsNullOrWhiteSpace(uuid) || string.IsNullOrWhiteSpace(accessToken)) return;
        lock (Gate)
        {
            var all = Load();
            all[uuid] = new Entry(Protect(accessToken), DateTime.UtcNow);
            Save(all);
        }
    }

    public static void Remove(string uuid)
    {
        lock (Gate)
        {
            var all = Load();
            if (all.Remove(uuid)) Save(all);
        }
    }

    private static Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(PathOf)) return [];
            return JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(PathOf))
                ?? [];
        }
        catch
        {
            return [];   // 损坏视为无令牌
        }
    }

    private static void Save(Dictionary<string, Entry> all)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(PathOf)!);
        File.WriteAllText(PathOf, JsonSerializer.Serialize(all,
            new JsonSerializerOptions { WriteIndented = true }));
    }

    // ── 加密（Windows: DPAPI；其他平台: 仅编码） ──

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
                // DPAPI 只在 Windows 上存在：跨平台构建时这里必须有显式守卫，
                // 否则 CA1416 会报警（读取端原来漏了，写入端有）。
                if (!OperatingSystem.IsWindows()) return null;
                var bytes = Convert.FromBase64String(stored["dpapi:".Length..]);
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(bytes, null, DataProtectionScope.CurrentUser));
            }
            if (stored.StartsWith("plain:", StringComparison.Ordinal))
                return Encoding.UTF8.GetString(Convert.FromBase64String(stored["plain:".Length..]));
            return null;
        }
        catch
        {
            return null;
        }
    }
}
