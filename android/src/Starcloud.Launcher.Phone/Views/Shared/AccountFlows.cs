// 账户身份动作的公共实现：账户页（添加账户二级页）与启动前的「就地登录」弹窗共用同一段逻辑，
// 免得两处各写一遍、日后改一处漏一处。
//
// 合规：第三方密码只作请求参数（不落盘、不写日志）；鉴权 token 只进内存会话，绝不落盘。
// 移植自桌面 Starcloud.App.Views.Shared.AccountFlows.cs（纯 Core 调用，仅改命名空间）。

using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Services;

namespace Starcloud.Launcher.Phone.Views.Shared;

internal static class AccountFlows
{
    /// <summary>
    /// 第三方（Yggdrasil）账密登录：解析服务器（含别名跳转）→ 换票 → 写入账户存储与内存会话。
    /// 成功返回角色名；失败抛异常，由调用方翻译成界面文案。
    /// </summary>
    public static async Task<string> LoginExternalAsync(
        AppServices s, string serverUrl, string name, string password, CancellationToken ct = default)
    {
        var server = await s.Yggdrasil.ResolveServerAsync(serverUrl, ct);

        // clientToken 是设备标识（非鉴权 token）：同服务器同用户复用已存的，否则新生成
        var clientToken = s.Accounts.Accounts.FirstOrDefault(a =>
            a.Type == "external" && a.Username == name &&
            (a.ServerBase == server.Base || a.ServerAlias == serverUrl))?.ClientToken;

        var result = await s.Yggdrasil.AuthenticateAsync(server.Base, name, password,
            string.IsNullOrEmpty(clientToken) ? Guid.NewGuid().ToString() : AsUuid(clientToken!), ct);

        s.Accounts.AddOrUpdateExternal(result.ProfileId, result.ProfileName,
            server.Base, serverUrl, server.ServerName);
        s.Accounts.SetSession(result.ProfileId, new AccountSession(result.AccessToken));
        return result.ProfileName;
    }

    /// <summary>
    /// clientToken 按规范是 UUID。老版本生成的是不带连字符的 32 位十六进制串，
    /// 严格校验 UUID 的认证服务器（皮肤站一类）会挑刺，这里统一补上连字符；
    /// 认不出来的串原样返回（服务器自己会给一个合法的）。
    /// </summary>
    private static string AsUuid(string token)
    {
        var hex = token.Replace("-", "");
        if (hex.Length != 32 || !hex.All(Uri.IsHexDigit)) return token;
        return $"{hex[..8]}-{hex[8..12]}-{hex[12..16]}-{hex[16..20]}-{hex[20..]}";
    }
}
