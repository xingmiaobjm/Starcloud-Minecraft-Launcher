// 启动账户解析模块：主页与存档页共用的「正版 / 第三方」身份解析。
// 合规：凭证走内存会话（AccountStore.GetSession），不落盘；
// 会话内未登录时抛 AccountLoginRequiredException，由 UI 就地登录（微软设备码 / 第三方密码）。

namespace Starcloud.Core.Services;

/// <summary>一次启动要使用的玩家身份（ExternalBase 仅第三方账户有值）</summary>
public sealed record ResolvedPlayer(string Name, LaunchCredentials? Credentials, string? ExternalBase = null);

/// <summary>会话内缺少登录凭证，需要用户就地重新登录（不可自动恢复，token 不落盘）</summary>
public sealed class AccountLoginRequiredException(string kind, string uuid, string username)
    : Exception($"{username} 的登录凭证已失效，本次会话需要重新登录")
{
    /// <summary>"microsoft" | "external"</summary>
    public string Kind { get; } = kind;
    public string Uuid { get; } = uuid;
    public string Username { get; } = username;
}

/// <summary>解析当前应使用的账户：模式惰性跟随上次使用的账户类型</summary>
public sealed class LaunchAccountService(
    AccountStore accounts,
    Func<LauncherSettings> settings)
{
    private string? _mode;

    /// <summary>"microsoft" | "external"；未显式设置时按上次玩家名前缀推断，默认正版</summary>
    public string Mode
    {
        get => _mode ??= settings().LastUsername switch
        {
            { } u when u.StartsWith("ms:") => "microsoft",
            { } u when u.StartsWith("ext:") => "external",
            _ => "microsoft",
        };
        set => _mode = value;
    }

    /// <summary>解析启动身份；凭证缺失抛 AccountLoginRequiredException（由 UI 就地登录），无账户抛普通异常</summary>
    public Task<ResolvedPlayer> ResolveAsync()
    {
        var external = Mode == "external";
        var acc = accounts.Default;
        if (acc is null || acc.Type != (external ? "external" : "microsoft"))
            throw new InvalidOperationException(external
                ? "尚无第三方账户，请先到账户页登录"
                : "尚无微软账户，请先到账户页登录");

        var session = accounts.GetSession(acc.Uuid);
        if (session is null || string.IsNullOrEmpty(session.AccessToken))
            throw new AccountLoginRequiredException(Mode, acc.Uuid, acc.Username);

        return Task.FromResult(external
            ? new ResolvedPlayer(acc.Username,
                new LaunchCredentials(acc.Uuid, session.AccessToken, "mojang"), acc.ServerBase)
            : new ResolvedPlayer(acc.Username,
                new LaunchCredentials(acc.Uuid, session.AccessToken, "msa", session.Xuid)));
    }
}
