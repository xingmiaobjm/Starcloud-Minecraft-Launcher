// 启动账户解析模块：主页与存档页共用的「正版 / 第三方 / 离线」身份解析。
// 凭证走内存会话（AccountStore.GetSession），不落盘；在线账户会话内未登录时抛
// AccountLoginRequiredException，由 UI 就地登录（微软设备码 / 第三方密码）。
// 离线账户无需登录：用名字派生 UUID + 占位令牌启动，只能玩单人 / 局域网与离线服务器。

namespace Starcloud.Core.Services;

/// <summary>
/// 一次启动要使用的玩家身份（ExternalBase 仅第三方账户有值）。
/// SkinUrl = 自定义皮肤的纹理 URL（本机回环服务，离线与正版账户都可能带），null 表示不注入。
/// </summary>
public sealed record ResolvedPlayer(string Name, LaunchCredentials? Credentials,
    string? ExternalBase = null, string? SkinUrl = null);

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

    // ── 设置里 LastUsername 的模式前缀（跨启动记住上次用的身份类型） ──

    public const string MicrosoftPrefix = "ms:";
    public const string ExternalPrefix = "ext:";
    public const string OfflinePrefix = "off:";

    /// <summary>按模式取 LastUsername 前缀</summary>
    public static string PrefixFor(string? mode) => mode switch
    {
        "external" => ExternalPrefix,
        "offline" => OfflinePrefix,
        _ => MicrosoftPrefix,
    };

    /// <summary>从 LastUsername 反推模式；无记录默认正版</summary>
    public static string ModeOfLastUsername(string? lastUsername) => lastUsername switch
    {
        { } u when u.StartsWith(MicrosoftPrefix) => "microsoft",
        { } u when u.StartsWith(ExternalPrefix) => "external",
        { } u when u.StartsWith(OfflinePrefix) => "offline",
        _ => "microsoft",
    };

    /// <summary>"microsoft" | "external" | "offline"；未显式设置时按上次玩家名前缀推断，默认正版</summary>
    public string Mode
    {
        get => _mode ??= ModeOfLastUsername(settings().LastUsername);
        set => _mode = value;
    }

    /// <summary>
    /// 解析启动身份。离线模式直接返回派生身份（不需要任何凭证）；
    /// 在线模式凭证缺失抛 AccountLoginRequiredException（由 UI 就地登录），无账户抛普通异常。
    /// </summary>
    public Task<ResolvedPlayer> ResolveAsync()
    {
        if (Mode == "offline")
        {
            var off = accounts.Default;
            if (off is null || off.Type != AccountStore.OfflineType)
                throw new InvalidOperationException("尚无离线账户，请先到账户页添加一个离线账户");
            // 离线账户：没有会话服务器可问皮肤，改用本机回环服务 + userProperties 注入
            return Task.FromResult(new ResolvedPlayer(off.Username,
                new LaunchCredentials(off.Uuid, "0", "legacy"),
                SkinUrl: OfflineSkins.UrlFor(off.Uuid)));
        }

        var external = Mode == "external";
        var acc = accounts.Default;
        if (acc is null || acc.Type != (external ? "external" : "microsoft"))
            throw new InvalidOperationException(external
                ? "尚无第三方账户，请先到账户页登录"
                : "尚无微软账户，请先到账户页登录");

        var session = accounts.GetSession(acc.Uuid);
        if (session is null || string.IsNullOrEmpty(session.AccessToken))
            throw new AccountLoginRequiredException(Mode, acc.Uuid, acc.Username);

        // 正版账户：本机选过皮肤时同样带上本机注入（与离线账户同一条通道）。
        // 它不是"假按钮"——注入的 textures 属性和离线账户一样交给客户端；但按项目既有边界，
        // 未签名的属性在 1.19.2 及更早版本会被客户端直接采用，1.19.3 起不保证生效
        // （见 docs/designs/2026-09-23-skin-library-design.md §3.1），所以界面上每次更换都会先警告。
        return Task.FromResult(external
            ? new ResolvedPlayer(acc.Username,
                new LaunchCredentials(acc.Uuid, session.AccessToken, "mojang"), acc.ServerBase)
            : new ResolvedPlayer(acc.Username,
                new LaunchCredentials(acc.Uuid, session.AccessToken, "msa", session.Xuid),
                SkinUrl: OfflineSkins.UrlFor(acc.Uuid)));
    }
}
