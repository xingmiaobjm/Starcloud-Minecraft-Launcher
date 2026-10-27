// 版本加载器检测：从版本 JSON 判断 fabric/quilt/forge/neoforge，而不是只看版本 id 前缀。
//
// 为什么需要它：整合包实例的 id 是包名（「机械动力航空学整合包」），ResolveLoader 只能按
// fabric-loader- / forge- 这类前缀判断，会把它当原版——于是「下载 Mod」被拦下说没有加载器。
// 这里沿 inheritsFrom 链读 JSON：libraries 里的加载器坐标最准，mainClass 兜底，
// 顺便把链上第一个真正的 MC 版本号（1.21.1）取出来。

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Starcloud.Core.Services;

public static class LoaderDetector
{
    /// <summary>返回 (加载器, MC 版本)；识别不出加载器时 Loader = "vanilla"。</summary>
    public static (string Loader, string? McVersion) Detect(string dotMc, string versionId)
    {
        var loader = "vanilla";
        string? mc = null;
        var current = versionId;

        for (var depth = 0; depth < 4 && !string.IsNullOrWhiteSpace(current); depth++)
        {
            // id 本身就是 1.21.1 这种 → 就是这一层的 MC 版本
            if (mc is null && Regex.IsMatch(current, @"^\d+(\.\d+)+$")) mc = current;

            // id 前缀能直接认出的（安装器生成的版本名），顺带把 MC 版本抠出来
            var byPrefix = ContentLibraryService.ResolveLoader(current, out _);
            if (byPrefix != "vanilla")
            {
                if (loader == "vanilla") loader = byPrefix;
                if (mc is null)
                {
                    var byPrefixMc = ContentLibraryService.ResolveMc(current);
                    if (Regex.IsMatch(byPrefixMc, @"^\d+(\.\d+)+$")) mc = byPrefixMc;
                }
            }

            var jsonPath = Path.Combine(dotMc, "versions", current, current + ".json");
            if (!File.Exists(jsonPath)) break;

            try
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
                var root = doc.RootElement;

                // 合并式版本 JSON（PCL 导出/整合包）没有 inheritsFrom，但带 clientVersion
                if (mc is null &&
                    root.TryGetProperty("clientVersion", out var cv) &&
                    cv.ValueKind == JsonValueKind.String)
                    mc = cv.GetString();

                if (root.TryGetProperty("libraries", out var libs) &&
                    libs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lib in libs.EnumerateArray())
                    {
                        if (!lib.TryGetProperty("name", out var n) ||
                            n.ValueKind != JsonValueKind.String) continue;
                        var name = n.GetString() ?? "";
                        // 按「命名空间」判，而不是某一门具体坐标：
                        // 合并式整合包只留 fancymodloader / accesstransformers 这些子模块，
                        // 没有 net.neoforged:neoforge 本体。
                        if (name.StartsWith("net.fabricmc:fabric-loader", StringComparison.OrdinalIgnoreCase))
                        { loader = "fabric"; break; }
                        if (name.StartsWith("org.quiltmc:quilt-loader", StringComparison.OrdinalIgnoreCase))
                        { loader = "quilt"; break; }
                        if (name.StartsWith("net.neoforged", StringComparison.OrdinalIgnoreCase))
                        { loader = "neoforge"; break; }
                        // net.minecraftforge:srgutils 这类工具库 NeoForge 包也有，不能当 Forge 证据
                        if (name.StartsWith("net.minecraftforge:forge", StringComparison.OrdinalIgnoreCase) ||
                            name.StartsWith("net.minecraftforge.fml", StringComparison.OrdinalIgnoreCase))
                        { loader = "forge"; break; }
                    }
                }

                if (loader == "vanilla" &&
                    root.TryGetProperty("mainClass", out var mainEl) &&
                    mainEl.ValueKind == JsonValueKind.String)
                {
                    var main = mainEl.GetString() ?? "";
                    if (main.Contains("KnotClient", StringComparison.OrdinalIgnoreCase)) loader = "fabric";
                    else if (main.Contains("quilt", StringComparison.OrdinalIgnoreCase)) loader = "quilt";
                    else if (main.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) loader = "neoforge";
                    else if (main.Contains("forge", StringComparison.OrdinalIgnoreCase)) loader = "forge";
                }

                if (root.TryGetProperty("inheritsFrom", out var inherited) &&
                    inherited.ValueKind == JsonValueKind.String)
                {
                    current = inherited.GetString();
                    continue;
                }
            }
            catch
            {
                // JSON 坏了就按已识别的结果返回
            }
            break;
        }

        return (loader, mc);
    }
}
