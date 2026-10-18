// 投影资源切片：把 deepslate 渲染要用的资源从本机整合到一份可传输的包里。
//
// deepslate（MIT，第三方）负责模型继承、元素旋转、多部件、UV、透明纹理、网格与剔除；
// 它需要的只是**原始资源文件**：blockstates JSON、models JSON（含 parent 链）、textures PNG。
// 这里按投影实际用到的方块种类，从客户端 jar / 资源包 / 模组 jar 里取出这一小撮文件，
// 通常几百 KB ~ 几 MB，而不是塞整个 40MB 的客户端。

namespace Starcloud.Core.Services.Lab;

/// <summary>deepslate 需要的资源切片（键名与它要求的 Identifier 字符串一致）</summary>
public sealed record ProjectionAssets(
    IReadOnlyDictionary<string, string> BlockStates,   // "minecraft:piston" → blockstates JSON 原文
    IReadOnlyDictionary<string, string> Models,        // "minecraft:block/piston" → model JSON 原文
    IReadOnlyDictionary<string, byte[]> Textures)      // "minecraft:block/piston_top" → PNG 字节
{
    public int FileCount => BlockStates.Count + Models.Count + Textures.Count;
}

public static class ProjectionAssetCollector
{
    /// <summary>方块种类上限：极少数投影会用到成百上千种方块，超过就只收前 N 种，避免资源爆炸</summary>
    public const int MaxBlockTypes = 1500;

    private const int MaxDepth = 8;
    private const string DefaultNamespace = "minecraft";

    /// <summary>
    /// 收集这些方块状态（含属性的完整字符串）用到的全部资源。
    /// 资源缺失时跳过（页面会给缺失纹理显示洋红占位，属于 deepslate 的既定行为）。
    /// </summary>
    public static ProjectionAssets Collect(IEnumerable<string> blockStates, Func<string, byte[]?> readAsset)
    {
        var statesOut = new Dictionary<string, string>(StringComparer.Ordinal);
        var modelsOut = new Dictionary<string, string>(StringComparer.Ordinal);
        var texturePaths = new Dictionary<string, string>(StringComparer.Ordinal);   // id → 资源路径

        var seenBlocks = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in blockStates)
        {
            var blockId = StripState(raw);
            if (!seenBlocks.Add(blockId)) continue;
            if (seenBlocks.Count > MaxBlockTypes) break;

            var (ns, path) = SplitLocation(blockId);
            var stateAsset = $"{ns}/blockstates/{path}.json";
            var stateJson = ReadText(readAsset, stateAsset);
            if (stateJson is null) continue;
            statesOut[$"{ns}:{path}"] = stateJson;

            foreach (var modelRef in ModelRefs(stateJson))
                WalkModel(modelRef, readAsset, modelsOut, texturePaths);
        }

        // 只读取真正落地成功的纹理
        var texturesOut = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var (id, assetPath) in texturePaths)
        {
            var bytes = readAsset(assetPath);
            if (bytes is { Length: > 0 }) texturesOut[id] = bytes;
        }

        return new ProjectionAssets(statesOut, modelsOut, texturesOut);
    }

    // ── blockstate → 模型引用 ──

    private static IEnumerable<string> ModelRefs(string blockStateJson)
    {
        var refs = new List<string>();
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(blockStateJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("variants", out var variants) && variants.ValueKind == System.Text.Json.JsonValueKind.Object)
            {
                foreach (var variant in variants.EnumerateObject())
                    CollectFromApply(variant.Value, refs);
            }
            if (root.TryGetProperty("multipart", out var multipart) && multipart.ValueKind == System.Text.Json.JsonValueKind.Array)
            {
                foreach (var part in multipart.EnumerateArray())
                    if (part.TryGetProperty("apply", out var apply)) CollectFromApply(apply, refs);
            }
        }
        catch
        {
            // 坏 JSON：当作没有模型
        }
        return refs;
    }

    private static void CollectFromApply(System.Text.Json.JsonElement element, List<string> refs)
    {
        if (element.ValueKind == System.Text.Json.JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray()) CollectFromApply(item, refs);
            return;
        }
        if (element.ValueKind != System.Text.Json.JsonValueKind.Object) return;
        if (element.TryGetProperty("model", out var model) &&
            model.GetString() is { Length: > 0 } reference)
            refs.Add(reference);
    }

    // ── 模型链（含 parent） + 纹理 ──

    private static void WalkModel(string modelRef, Func<string, byte[]?> readAsset,
        Dictionary<string, string> modelsOut, Dictionary<string, string> texturePaths)
    {
        var current = modelRef;
        for (var depth = 0; depth < MaxDepth && current is not null; depth++)
        {
            var (ns, path) = SplitLocation(current);
            var modelPath = NormalizeModelPath(path);
            var id = $"{ns}:{modelPath}";
            if (modelsOut.ContainsKey(id)) return;   // 这条链已经走过

            var json = ReadText(readAsset, $"{ns}/models/{modelPath}.json");
            if (json is null) return;
            modelsOut[id] = json;

            string? parent = null;
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (root.TryGetProperty("textures", out var textures) &&
                    textures.ValueKind == System.Text.Json.JsonValueKind.Object)
                {
                    foreach (var item in textures.EnumerateObject())
                    {
                        if (item.Value.ValueKind != System.Text.Json.JsonValueKind.String) continue;
                        var value = item.Value.GetString() ?? "";
                        if (value.Length == 0 || value.StartsWith('#')) continue;   // 引用交给页面解
                        var (tns, tpath) = SplitLocation(value);
                        if (!tpath.Contains('/')) tpath = "block/" + tpath;
                        texturePaths[$"{tns}:{tpath}"] = $"{tns}/textures/{tpath}.png";
                    }
                }
                if (root.TryGetProperty("parent", out var p)) parent = p.GetString();
            }
            catch
            {
                return;
            }
            current = parent;
        }
    }

    private static string NormalizeModelPath(string path)
    {
        if (path.StartsWith("models/", StringComparison.Ordinal)) path = path["models/".Length..];
        return path.StartsWith("block/", StringComparison.Ordinal) || path.Contains('/')
            ? path
            : "block/" + path;
    }

    private static string StripState(string blockState)
    {
        var bracket = blockState.IndexOf('[');
        return bracket < 0 ? blockState : blockState[..bracket];
    }

    private static (string Namespace, string Path) SplitLocation(string value)
    {
        var colon = value.IndexOf(':');
        return colon < 0 ? (DefaultNamespace, value) : (value[..colon], value[(colon + 1)..]);
    }

    private static string? ReadText(Func<string, byte[]?> readAsset, string assetPath)
    {
        var bytes = readAsset(assetPath);
        return bytes is { Length: > 0 } ? System.Text.Encoding.UTF8.GetString(bytes) : null;
    }
}
