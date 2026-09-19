// 插件环境常量：API 版本与官方构建标识
// 合规声明（plugin-spec.md §7）：插件规范仅提供游戏启动参数扩展、UI 扩展能力；
// 启动器本体不提供、不支持、不接管任何账号身份验证功能。

namespace Starcloud.Core.Plugins;

/// <summary>插件系统环境常量（官方国内发布版打包时可通过 CI 覆盖标识）</summary>
public static partial class PluginEnvironment
{
    /// <summary>当前插件 API 版本（与 plugin-spec.md 同步）</summary>
    public const string ApiVersion = "1.0";

    /// <summary>
    /// 是否官方国内发布版（requires_official_ai 插件仅此构建可启用）。
    /// 默认 false；官方国内渠道打包时置 true。
    /// </summary>
    public const bool OfficialChinaBuild = false;

    /// <summary>
    /// 官方插件签名公钥（ECDSA P-256，SubjectPublicKeyInfo 的 base64）。
    /// null = 未注入公钥，所有插件按「第三方未验证」标记。
    /// </summary>
    public const string? OfficialPublicKey = null;
}
