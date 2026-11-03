// 启动参数整理：版本 JSON 多层继承（原版 ← Forge ← lwjgl3ify/Cleanroom…）拼出来的
// 参数列表里，同一个键可能重复出现多次，而重复的启动参数不会"后者生效"，
// 而是**两个一起失效**（--width / --uuid 这类关键参数重复直接导致游戏异常）。
//
// 规则（按参数形态分两类）：
//   · 单参数（-XX:+UseG1GC 这种只有键的）：键重复就丢弃后面的。
//   · 键值对（--width 1920 / --versionType release）：游戏参数用**后值覆盖前值**
//     （子层想要的效果就是覆盖父层），JVM 参数只在键和值都相同时去重
//     （-D 属性允许同名不同值共存，覆盖反而会破坏加载器追加的设定）。
//
// 例外：--tweakClass 允许重复——OptiFine + Forge 组合要靠多个 tweakClass 一起生效。

namespace Starcloud.Core.Services;

public static class LaunchArgumentSanitizer
{
    /// <summary>允许重复出现的键（多个 tweakClass 是合法配置）。</summary>
    private static readonly HashSet<string> KeepDuplicates = new(StringComparer.Ordinal)
    {
        "--tweakClass",
    };

    /// <summary>
    /// 整理参数列表：单参数去重、键值对按语义覆盖或去重。
    /// </summary>
    /// <param name="isJvm">true = JVM 参数（-D/-X 那一段），false = 游戏参数</param>
    public static List<string> Normalize(IReadOnlyList<string> args, bool isJvm)
    {
        var result = new List<string>(args.Count);
        // 键 → 值在 result 里的位置（只有键值对才登记）
        var valueIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        var seenKeys = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < args.Count; i++)
        {
            var key = args[i] ?? "";
            if (key.Length == 0) continue;

            if (!LooksLikeFlag(key) || i + 1 >= args.Count || LooksLikeFlag(args[i + 1]))
            {
                // 单参数
                if (!KeepDuplicates.Contains(key) && !seenKeys.Add(key)) continue;
                result.Add(key);
                continue;
            }

            // 键值对
            var value = args[i + 1];
            i++;

            if (KeepDuplicates.Contains(key))
            {
                result.Add(key);
                result.Add(value);
                continue;
            }

            if (valueIndex.TryGetValue(key, out var at))
            {
                // 游戏参数：后值覆盖前值；JVM：值相同才算重复
                if (!isJvm || string.Equals(result[at], value, StringComparison.Ordinal))
                    result[at] = value;
                else
                {
                    result.Add(key);
                    result.Add(value);
                    valueIndex[key] = result.Count - 1;
                }
                continue;
            }

            result.Add(key);
            result.Add(value);
            valueIndex[key] = result.Count - 1;
        }
        return result;
    }

    /// <summary>
    /// 是否是"开关型"参数：以 - 开头，但排除 -1 / -50 这类**负数取值**
    /// （--xPos -50 里的 -50 是值，不是参数）。
    /// </summary>
    private static bool LooksLikeFlag(string token)
    {
        if (token.Length < 2 || token[0] != '-') return false;
        if (token.Length > 1 && (char.IsDigit(token[1]) || token[1] == '.')) return false;
        return true;
    }
}
