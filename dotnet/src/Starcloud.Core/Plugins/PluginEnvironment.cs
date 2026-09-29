// 插件环境常量：API 版本、官方构建标识、官方签名公钥
// 合规声明（plugin-spec.md §7）：插件规范仅提供游戏启动参数扩展、UI 扩展能力；
// 启动器本体不提供、不支持、不接管任何账号身份验证功能。

namespace Starcloud.Core.Plugins;

/// <summary>插件系统环境常量（官方国内发布版打包时可通过 CI 覆盖标识）</summary>
public static partial class PluginEnvironment
{
    /// <summary>1.x API 版本（现行运行时）</summary>
    public const string ApiVersion = "1.0";

    /// <summary>2.0 API 版本（规范草案；清单与权限模型、整包签名已实现，运行时与声明式 UI 未实现）</summary>
    public const string ApiVersion2 = "2.0";

    /// <summary>本体接受（可通过清单校验）的 API 版本全集</summary>
    public static readonly string[] SupportedApiVersions = [ApiVersion, ApiVersion2];

    /// <summary>
    /// 本体能够实际执行的 API 版本。
    /// 2.0 以「声明式优先」方式加载：清单与能力桥接、整包签名、声明式 UI 已实现；
    /// 但 async 钩子与预算、ctx.tasks.spawn、WASM 运行时、HTML 面板仍未实现，
    /// 这些能力在桥接时一律不给（fail-closed），并在日志里说明原因。
    /// </summary>
    public static readonly string[] RuntimeApiVersions = [ApiVersion, ApiVersion2];

    /// <summary>
    /// 是否官方国内发布版（requires_official_ai 插件仅此构建可启用）。
    /// 默认 false；官方国内渠道打包时置 true。
    /// </summary>
    public const bool OfficialChinaBuild = false;

    /// <summary>
    /// 官方插件签名公钥（1.x：ECDSA P-256，SubjectPublicKeyInfo 的 base64）。
    /// null = 未注入公钥，所有插件按「第三方未验证」标记。
    /// </summary>
    public const string? OfficialPublicKey = null;

    /// <summary>
    /// 2.0 官方签名公钥表：keyId → ECDSA P-256 SubjectPublicKeyInfo（base64），用于密钥轮换。
    /// 空表 = 未注入公钥，2.0 签名一律按「未签名」降级（不阻断安装，启用时逐个免责确认）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> OfficialPublicKeys =
        new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>该 API 版本是否可通过清单校验</summary>
    public static bool IsSupportedApi(string apiVersion) => SupportedApiVersions.Contains(apiVersion);

    /// <summary>该 API 版本能否在当前本体上执行</summary>
    public static bool CanRunApi(string apiVersion) => RuntimeApiVersions.Contains(apiVersion);
}
