// 加载器构建列表本地缓存：API 拉取成功后写入，下次进入详情先显示缓存（离线可用）

using System.IO;
using System.Text.Json;

namespace Starcloud.Core.Services;

public static class LoaderBuildCache
{
    private static string PathFor(string dotMinecraft, string kind, string mcVersion) =>
        Path.Combine(dotMinecraft, "cache", "loaders", $"{kind}-{mcVersion}.json");

    /// <summary>读取缓存（不存在或损坏返回 null）</summary>
    public static List<LoaderBuild>? Read(string dotMinecraft, string kind, string mcVersion)
    {
        try
        {
            var json = File.ReadAllText(PathFor(dotMinecraft, kind, mcVersion));
            return JsonSerializer.Deserialize<List<LoaderBuild>>(json) is { Count: > 0 } b ? b : null;
        }
        catch { return null; }
    }

    public static void Write(string dotMinecraft, string kind, string mcVersion, List<LoaderBuild> builds)
    {
        try
        {
            var path = PathFor(dotMinecraft, kind, mcVersion);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(builds));
        }
        catch { /* 缓存写入失败不影响主流程 */ }
    }
}
