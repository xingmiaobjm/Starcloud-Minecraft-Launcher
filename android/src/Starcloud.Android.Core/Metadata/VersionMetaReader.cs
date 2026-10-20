// 版本行元数据读取（移植桌面端 VersionRowHelpers 的纯逻辑部分）：
// 类型标签 / 图标文件名 / 上次启动时间与大小格式化。
// 首页版本卡与桌面端共用同一套判定，保证「所有端 UI 一致」——
// 标签与图标读同一份缓存结果，不会出现「标签写 Fabric、图标却是草方块」的两套判定打架。

using System.IO;
using System.Text.Json;

namespace Starcloud.Android.Core.Metadata;

/// <summary>版本行元数据辅助：加载器 / 类型推断（缓存）、图标文件名、时间与大小格式化。</summary>
public static class VersionMetaReader
{
    /// <summary>版本类型 → 内置图标文件名（正式版草方块 / 快照紫水晶 / 远古版圆石与泥土）</summary>
    private static readonly Dictionary<string, string> VersionIconFiles = new()
    {
        ["release"] = "natural_grass_block.png",
        ["snapshot"] = "block_of_amethyst.png",
        ["old_beta"] = "masonry_cobblestone.png",
        ["old_alpha"] = "natural_dirt.png",
    };

    // 顺序敏感：neoforge 必须先于 forge 匹配（id 中 "neoforge" 包含 "forge" 子串）
    private static readonly (string Key, string File)[] LoaderIconFiles =
    [
        ("neoforge", "neoforge.png"),
        ("forge", "forge.png"),
        ("fabric", "fabric.png"),
        ("quilt", "quilt.png"),
    ];

    /// <summary>版本 JSON 派生信息：类型与命中的加载器（同一次读取；缓存按文件写入时间失效）</summary>
    private readonly record struct Meta(string Type, string? LoaderKey);

    private static readonly Dictionary<string, (DateTime Stamp, Meta Value)> MetaCache = [];
    private static readonly object MetaGate = new();

    private static string JsonPath(string dotMc, string versionId) =>
        Path.Combine(dotMc, "versions", versionId, versionId + ".json");

    private static Meta ReadMeta(string dotMc, string versionId)
    {
        var path = JsonPath(dotMc, versionId);
        DateTime stamp;
        try { stamp = File.GetLastWriteTimeUtc(path); }
        catch { stamp = DateTime.MinValue; }

        lock (MetaGate)
        {
            if (MetaCache.TryGetValue(path, out var hit) && hit.Stamp == stamp) return hit.Value;
        }

        string json;
        try { json = File.ReadAllText(path); }
        catch { json = ""; } // 读不到就只按 id 兜底

        var meta = new Meta(TypeOf(json), LoaderKeyOf(json, versionId));
        lock (MetaGate) { MetaCache[path] = (stamp, meta); }
        return meta;
    }

    /// <summary>
    /// 加载器判定：先看版本 JSON 里的加载器痕迹，再看版本 id 关键字兜底。
    /// 只看 id 不行——整合包 / 自建实例的名字往往与加载器无关，会一路落到原版草方块。
    /// </summary>
    private static string? LoaderKeyOf(string json, string versionId)
    {
        foreach (var (key, _) in LoaderIconFiles)
        {
            if (json.Contains(key, StringComparison.OrdinalIgnoreCase) ||
                versionId.Contains(key, StringComparison.OrdinalIgnoreCase))
                return key;
        }
        return null;
    }

    /// <summary>版本 JSON 的 type 字段（release / snapshot / old_beta / old_alpha）；缺失或损坏按正式版。</summary>
    private static string TypeOf(string json)
    {
        if (json.Length == 0) return "release";
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
                return type.GetString() ?? "release";
        }
        catch
        {
            // 解析失败按正式版
        }
        return "release";
    }

    /// <summary>版本类型标签：NeoForge / Forge / Fabric / Quilt / Beta 版 / Alpha 版 / 快照版 / 正式版。</summary>
    public static string DetectKind(string dotMc, string id)
    {
        var meta = ReadMeta(dotMc, id);
        return meta.LoaderKey switch
        {
            "neoforge" => "NeoForge",
            "forge" => "Forge",
            "fabric" => "Fabric",
            "quilt" => "Quilt",
            _ => meta.Type switch
            {
                "old_beta" => "Beta 版",
                "old_alpha" => "Alpha 版",
                "snapshot" => "快照版",
                _ => "正式版",
            },
        };
    }

    /// <summary>行图标文件名：加载器图标优先，原版按类型选取；文件名对应 APK 内置资源（Images/）。</summary>
    public static string IconFileOf(string dotMc, string versionId)
    {
        var meta = ReadMeta(dotMc, versionId);
        return meta.LoaderKey is { } loader
            ? LoaderIconFiles.First(f => f.Key == loader).File
            : VersionIconFiles.GetValueOrDefault(meta.Type, VersionIconFiles["release"]);
    }

    /// <summary>上次启动时间文案：今天 HH:mm / 昨天 HH:mm / N 天前 / N 个月前 / yyyy-MM-dd / 从未启动。</summary>
    public static string FormatLastPlayed(DateTime? time)
    {
        if (time is not { } t) return "从未启动";
        var days = (DateTime.Now.Date - t.Date).Days;
        return days switch
        {
            <= 0 => $"今天 {t:HH:mm}",
            1 => $"昨天 {t:HH:mm}",
            < 30 => $"{days} 天前",
            < 365 => $"{days / 30} 个月前",
            _ => $"{t:yyyy-MM-dd}",
        };
    }

    /// <summary>字节数格式化（GB / MB / KB / B）。</summary>
    public static string FormatSize(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / 1073741824.0:F1} GB",
        >= 1L << 20 => $"{bytes / 1048576.0:F0} MB",
        >= 1L << 10 => $"{bytes / 1024.0:F0} KB",
        _ => $"{bytes} B",
    };
}
