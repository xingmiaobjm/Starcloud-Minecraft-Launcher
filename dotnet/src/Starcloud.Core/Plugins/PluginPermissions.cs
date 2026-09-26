// 插件权限白名单 / 黑名单 + 身份参数拦截（规范 §2 §4）
// 安全不变量：插件无权修改游戏认证、UUID、登录校验逻辑；
// 身份相关权限从接口层面锁死（本体不暴露对应钩子），身份相关命令行参数直接拦截丢弃。

namespace Starcloud.Core.Plugins;

public static class PluginPermissions
{
    // ── ✅ 允许开放给插件的能力（白名单） ──

    //  基础（v1.0 起）
    public const string GameArguments = "launcher:game_arguments"; // 追加游戏 JVM/启动参数（仅增量）
    public const string UiPanel = "launcher:ui_panel";             // 新增 UI 面板 / 挂载区 / 确认框
    public const string Notification = "launcher:notification";    // 弹窗通知
    public const string FileAccess = "launcher:file_access";       // 读写插件自身 data 目录

    //  实例（v1.1 起）：读 = 查清单/详情/文件；写 = 安装/复制/改名/删除/补全
    public const string InstancesRead = "launcher:instances_read";
    public const string InstancesWrite = "launcher:instances_write";

    //  游戏进程：按当前账户启动实例（身份仍由本体解析，插件拿不到凭证）
    public const string GameLaunch = "launcher:game_launch";

    //  社区资源：读 = 搜索/版本列表/已装清单；写 = 安装/删除实例内的 mod 等
    public const string ContentRead = "launcher:content_read";
    public const string ContentWrite = "launcher:content_write";

    //  下载与网络：download = 走本体下载引擎写文件；network = 插件自己的 HTTP 请求
    public const string Download = "launcher:download";
    public const string Network = "launcher:network";

    //  设置 / 账户（只读身份，永不暴露令牌）/ 日志读取
    public const string SettingsRead = "launcher:settings_read";
    public const string SettingsWrite = "launcher:settings_write";
    public const string AccountsRead = "launcher:accounts_read";
    public const string LogsRead = "launcher:logs_read";

    //  存档与备份：读 = 存档/备份清单；写 = 备份/还原/删除/导出整合包
    public const string SavesRead = "launcher:saves_read";
    public const string SavesWrite = "launcher:saves_write";

    //  联机：内置陶瓦联机与第三方联机 SDK（开房 / 加入 / 结束 / 装核心）
    public const string Multiplayer = "launcher:multiplayer";

    //  外观：主题 / 背景 / 毛玻璃遮罩
    public const string Appearance = "launcher:appearance";

    //  自更新：只允许查询（安装更新由用户自己在设置里点）
    public const string Updates = "launcher:updates";

    //  宿主交互：事件订阅 / 剪贴板 / 打开链接与目录 / 插件私有键值存储 / 任务中心
    public const string Events = "launcher:events";
    public const string Clipboard = "launcher:clipboard";
    public const string Shell = "launcher:shell";
    public const string Storage = "launcher:storage";
    public const string Tasks = "launcher:tasks";

    /// <summary>允许声明的权限全集</summary>
    public static readonly string[] Allowed =
    [
        GameArguments, UiPanel, Notification, FileAccess,
        InstancesRead, InstancesWrite, GameLaunch,
        ContentRead, ContentWrite, Download, Network,
        SettingsRead, SettingsWrite, AccountsRead, LogsRead,
        SavesRead, SavesWrite, Multiplayer, Appearance, Updates,
        Events, Clipboard, Shell, Storage, Tasks,
    ];

    // ── ❌ 禁止开放给插件的权限（黑名单，出现即拒绝加载） ──
    public static readonly string[] Forbidden =
    [
        "launcher:account_verify", // 账号身份校验
        "launcher:auth_override",  // 认证流程接管
        "launcher:uuid_override",  // UUID 覆写
    ];

    // ── ⛔ 本体自管、不开放给插件的能力（不在白名单 = 声明即被拒，这里写明理由） ──
    // · 插件管理（启用 / 停用 / 安装 / 卸载其他插件）：插件不应决定同类的生死，
    //   否则一个插件就能把用户依赖的插件停掉，或把自己装成"被信任的"那一份。
    // · 启动器自更新安装：更新包会覆盖本体二进制，只能由用户在设置页手动点。
    // · 账户写操作（移除账户 / 改默认账户）：身份相关操作一律留在本体界面里。

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
