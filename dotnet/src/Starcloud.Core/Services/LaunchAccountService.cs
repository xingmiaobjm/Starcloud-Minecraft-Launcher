// 启动账户解析模块：主页与存档页共用的「正版 / 离线」身份解析（含正版凭据续期）
// 设计：docs/superpowers/specs/2026-09-10-saves-cross-version-design.md §3.4

namespace Starcloud.Core.Services;

/// <summary>一次启动要使用的玩家身份（离线时 Credentials 为 null）</summary>
public sealed record ResolvedPlayer(string Name, LaunchCredentials? Credentials);

/// <summary>解析当前应使用的账户：模式惰性跟随上次使用的账户类型</summary>
public sealed class LaunchAccountService(
    AccountStore accounts,
    MicrosoftAuthService auth,
    Func<LauncherSettings> settings)
{
    private string? _mode;

    /// <summary>"microsoft" | "offline"；未显式设置时按上次玩家名的 ms: 前缀推断</summary>
    public string Mode
    {
        get => _mode ??= settings().LastUsername is { } u && u.StartsWith("ms:")
            ? "microsoft"
            : "offline";
        set => _mode = value;
    }

    /// <summary>离线模式下手输的玩家名（无可用账户时使用）</summary>
    public string OfflineName { get; set; } = "";

    /// <summary>解析启动身份；失败抛异常，由调用方展示</summary>
    public async Task<ResolvedPlayer> ResolveAsync()
    {
        if (Mode == "microsoft")
        {
            var acc = accounts.Default;
            if (acc is null || acc.Type != "microsoft")
                throw new InvalidOperationException("尚无微软账户，请先到账户页登录");

            if (!string.IsNullOrEmpty(acc.RefreshToken))
            {
                try
                {
                    var r = await auth.RefreshAsync(acc.RefreshToken);
                    if (r is not null)
                    {
                        accounts.AddOrUpdateMicrosoft(r.Uuid, r.Username, r.AccessToken, r.RefreshToken);
                        return new ResolvedPlayer(r.Username,
                            new LaunchCredentials(r.Uuid, r.AccessToken, "msa", r.Xuid));
                    }
                }
                catch
                {
                    // 网络异常：降级用存量 accessToken
                }
            }

            if (!string.IsNullOrEmpty(acc.AccessToken))
                return new ResolvedPlayer(acc.Username, new LaunchCredentials(acc.Uuid, acc.AccessToken, "msa"));

            throw new InvalidOperationException($"{acc.Username} 的 Microsoft 登录已过期，请到账户页重新登录");
        }

        var name = accounts.Default?.Username;
        if (string.IsNullOrWhiteSpace(name)) name = OfflineName;
        if (string.IsNullOrWhiteSpace(name))
            name = settings().LastUsername is { } last
                ? (last.StartsWith("ms:") ? last[3..] : last)
                : null;
        if (string.IsNullOrWhiteSpace(name))
            throw new InvalidOperationException("请输入离线玩家名或选择账户");

        return new ResolvedPlayer(name.Trim(), null);
    }
}
