// Python 插件协议响应载荷解析扩展（行式 JSON → 强类型集合，全部宽容解析）

using System.Text.Json;

namespace Starcloud.Core.Plugins;

public static class PluginJsonExtensions
{
    public static string[] TryGetStringArray(this JsonElement obj, string prop)
    {
        try
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return [];
            return arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString() ?? "")
                .Where(s => s.Length > 0)
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>日志条目：[[level, text], ...]</summary>
    public static (string level, string text)[] TryGetLogArray(this JsonElement obj, string prop)
    {
        try
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return [];
            return arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Array)
                .Select(e => e.EnumerateArray().ToArray())
                .Where(pairs => pairs.Length == 2 &&
                                pairs.All(p => p.ValueKind == JsonValueKind.String))
                .Select(pairs => (pairs[0].GetString()!, pairs[1].GetString()!))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>面板注册：[{id, title, entry}, ...]</summary>
    public static (string id, string title, string entry)[] TryGetPanelArray(
        this JsonElement obj, string prop)
    {
        try
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return [];
            return arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => (
                    id: e.TryGetProperty("id", out var i) ? i.GetString() : null,
                    title: e.TryGetProperty("title", out var t) ? t.GetString() : null,
                    entry: e.TryGetProperty("entry", out var en) ? en.GetString() : null))
                .Where(p => !string.IsNullOrWhiteSpace(p.id) &&
                            !string.IsNullOrWhiteSpace(p.title) &&
                            !string.IsNullOrWhiteSpace(p.entry))
                .Select(p => (p.id!, p.title!, p.entry!))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>读取 Python 握手响应中的 slots 数组：(id, entry, height)。缺字段 / 类型不对的项丢弃。</summary>
    public static (string id, string entry, int height)[] TryGetSlotArray(
        this JsonElement obj, string prop)
    {
        try
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return [];
            return arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => (
                    id: e.TryGetProperty("id", out var i) ? i.GetString() : null,
                    entry: e.TryGetProperty("entry", out var en) ? en.GetString() : null,
                    height: e.TryGetProperty("height", out var h) && h.ValueKind == JsonValueKind.Number
                        ? h.GetInt32() : 96))
                .Where(p => !string.IsNullOrWhiteSpace(p.id) && !string.IsNullOrWhiteSpace(p.entry))
                .Select(p => (p.id!, p.entry!, p.height))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }

    /// <summary>通知：[[title, message], ...]</summary>
    public static (string title, string message)[] TryGetNotifyArray(
        this JsonElement obj, string prop)
    {
        try
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(prop, out var arr) ||
                arr.ValueKind != JsonValueKind.Array)
                return [];
            return arr.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.Array)
                .Select(e => e.EnumerateArray().ToArray())
                .Where(pairs => pairs.Length == 2 &&
                                pairs.All(p => p.ValueKind == JsonValueKind.String))
                .Select(pairs => (pairs[0].GetString()!, pairs[1].GetString()!))
                .ToArray();
        }
        catch
        {
            return [];
        }
    }
}
