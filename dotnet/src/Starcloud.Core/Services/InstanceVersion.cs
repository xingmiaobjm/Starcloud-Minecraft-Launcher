// 实例 → 原版 Minecraft 版本号。
//
// 实例目录名是用户自己起的（「Create Aero：New Horizons」），不能当版本号用。
// 版本 JSON 里才有真话：`id` 是官方版本 id，`inheritsFrom` 指向原版（加载器实例）。
// 这里沿 inheritsFrom 链找到根，再用 GameVersionFilter 验一遍，验不过就返回 null
// （调用方按「不筛选版本」处理，绝不把实例名发给 Modrinth / CurseForge）。

using System.Text.Json;

namespace Starcloud.Core.Services;

public static class InstanceVersion
{
    private const int MaxDepth = 6;

    /// <summary>实例对应的原版版本号；确定不了返回 null</summary>
    public static string? BaseGameVersion(string dotMinecraft, string? versionId)
    {
        var id = versionId?.Trim();
        if (string.IsNullOrEmpty(id)) return null;

        var candidates = new List<string>();
        var current = id;
        for (var depth = 0; depth < MaxDepth && !string.IsNullOrEmpty(current); depth++)
        {
            if (ReadRefs(dotMinecraft, current) is not { } refs) break;

            if (!string.IsNullOrEmpty(refs.InheritsFrom))
            {
                candidates.Add(refs.InheritsFrom!);   // 父版本（原版）优先
                current = refs.InheritsFrom!;
                continue;
            }

            if (!string.IsNullOrEmpty(refs.BaseVersion))
                candidates.Add(refs.BaseVersion!);    // 改名时留下的原版号（见 InstanceSettings.PatchJsonId）
            if (!string.IsNullOrEmpty(refs.Id))
                candidates.Add(refs.Id!);             // 原版实例：JSON 里的 id 就是版本号
            break;
        }

        candidates.Add(id);                        // 兜底：目录名本身可能就是个版本号
        foreach (var candidate in candidates)
            if (GameVersionFilter.OrNull(candidate) is { } valid)
                return valid;
        return null;
    }

    /// <summary>版本 JSON 里的 (id, inheritsFrom, starcloudBaseVersion)；读不到/损坏返回 null</summary>
    private static (string? Id, string? InheritsFrom, string? BaseVersion)? ReadRefs(
        string dotMinecraft, string versionId)
    {
        try
        {
            var path = Path.Combine(dotMinecraft, "versions", versionId, versionId + ".json");
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            return (Text(root, "id"), Text(root, "inheritsFrom"), Text(root, "starcloudBaseVersion"));
        }
        catch
        {
            return null; // 版本 JSON 损坏 / 读不到：当作「版本未知」，不阻断上层
        }
    }

    private static string? Text(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
