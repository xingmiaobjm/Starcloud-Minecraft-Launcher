// 插件权限白名单 / 黑名单 + 身份参数拦截（规范 §2 §4）
// 安全不变量：插件无权修改游戏认证、UUID、登录校验逻辑；
// 身份相关权限从接口层面锁死（本体不暴露对应钩子），身份相关命令行参数直接拦截丢弃。

namespace Starcloud.Core.Plugins;

public static class PluginPermissions
{
    // ── ✅ 允许开放给插件的能力（白名单） ──
    public const string GameArguments = "launcher:game_arguments"; // 追加游戏 JVM/启动参数（仅增量）
    public const string UiPanel = "launcher:ui_panel";             // 新增 UI 面板
    public const string Notification = "launcher:notification";    // 弹窗通知
    public const string FileAccess = "launcher:file_access";       // 读写插件自身 data 目录

    /// <summary>允许声明的权限全集</summary>
    public static readonly string[] Allowed =
        [GameArguments, UiPanel, Notification, FileAccess];

    // ── ❌ 禁止开放给插件的权限（黑名单，出现即拒绝加载） ──
    public static readonly string[] Forbidden =
    [
        "launcher:account_verify", // 账号身份校验
        "launcher:auth_override",  // 认证流程接管
        "launcher:uuid_override",  // UUID 覆写
    ];

    /// <summary>未知权限（不在白名单也不在黑名单）是否拒绝：白名单制，一律拒绝</summary>
    public static bool IsAllowed(string permission) => Allowed.Contains(permission);

    public static bool IsForbidden(string permission) => Forbidden.Contains(permission);

    // ── 身份相关命令行参数键（第二道防线：插件 append 的参数命中即丢弃） ──
    // 覆盖玩家名 / UUID / 访问令牌 / 用户类型 / 客户端标识 / 外置认证注入等核心身份校验参数
    private static readonly string[] IdentityArgKeys =
    [
        "--username", "--uuid", "--accessToken", "--userType", "--userProperties",
        "--clientId", "--clientid", "--xuid", "--authlib-injector",
        "--authServer", "--authserver",
    ];

    /// <summary>
    /// 判定插件想追加的参数是否命中身份校验类黑名单（忽略大小写）。
    /// 支持 「--key value」 的 key 形式与 「--key=value」 形式。
    /// </summary>
    public static bool IsIdentityArg(string arg, out string matchedKey)
    {
        matchedKey = "";
        if (string.IsNullOrWhiteSpace(arg) || !arg.StartsWith('-')) return false;
        var token = arg.Trim();
        // --key=value 拆出 key；裸 --key 或 「--key value」（空格分隔取值）取第一个空白段比对
        var eq = token.IndexOf('=');
        var key = eq > 0 ? token[..eq] : token.Split(' ', '\t')[0];
        foreach (var banned in IdentityArgKeys)
        {
            if (!string.Equals(key, banned, StringComparison.OrdinalIgnoreCase)) continue;
            matchedKey = banned;
            return true;
        }
        return false;
    }
}
