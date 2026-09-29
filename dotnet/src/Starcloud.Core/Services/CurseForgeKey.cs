// CurseForge API Key 解析：**用户自备优先，留空用构建时注入的内置密钥**。
//
// ── 为什么需要内置密钥 ──────────────────────────────────────────────────────
//   1. 官方 API 不带 key 一律 403（`API Key missing or invalid`）；
//   2. 2026-07-16 起官方文件 CDN（edge.forgecdn.net / mediafilez.forgecdn.net）
//      也强制要求有效 key，否则 401（见 blog.curseforge.com 的公告）——
//      表现就是「搜索能用、一下载就 401」。
//   于是官方分发版本内置一把共用密钥，省掉每个用户自己去申请。
//
// ── 密钥怎么进构建（都不进 GitHub）─────────────────────────────────────────
//   dotnet/src/Starcloud.Core/Starcloud.Core.csproj 在**求值期**按优先级取：
//     ① MSBuild 属性      dotnet build -p:CurseForgeApiKey=xxx
//     ② 环境变量          STARCLOUD_CF_KEY=xxx
//     ③ 仓库内配置文件    secrets/curseforge.key（.gitignore 已忽略）
//   三者都没有 → 不生成 AssemblyMetadata → BuiltIn 为空串，
//   运行期一切照旧走「用户自备密钥 → MCIM 镜像」路径。
//   仓库里只有 secrets/README.md（说明用），密钥本体永远不入库。

using System.Reflection;

namespace Starcloud.Core.Services;

public static class CurseForgeKey
{
    private static readonly Lazy<string> BuiltInLazy = new(ReadBuiltIn, isThreadSafe: true);

    /// <summary>构建时注入的内置密钥；未注入为空串（不是 null，便于拼接判断）</summary>
    public static string BuiltIn => BuiltInLazy.Value;

    /// <summary>本构建是否内置了密钥</summary>
    public static bool HasBuiltIn => BuiltIn.Length > 0;

    /// <summary>
    /// 当前生效的密钥：设置里填了就用自己的，留空用内置的，都没有 → null。
    /// <para>自备优先是刻意的：用户既然填了，就该尊重他的 key（也便于自建/CI 调试）。</para>
    /// </summary>
    public static string? Effective(LauncherSettings settings)
    {
        var own = settings.CurseForgeApiKey?.Trim();
        if (!string.IsNullOrEmpty(own)) return own;
        return HasBuiltIn ? BuiltIn : null;
    }

    /// <summary>从程序集元数据读内置密钥（由 csproj 的 AssemblyMetadata 注入）</summary>
    private static string ReadBuiltIn()
    {
        try
        {
            return typeof(CurseForgeKey).Assembly
                       .GetCustomAttributes<AssemblyMetadataAttribute>()
                       .FirstOrDefault(a => a.Key == "CurseForgeApiKey")?.Value
                       ?.Trim()
                   ?? "";
        }
        catch
        {
            return "";
        }
    }
}
