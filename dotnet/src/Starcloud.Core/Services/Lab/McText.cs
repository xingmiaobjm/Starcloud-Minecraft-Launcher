// SC 实验室 · 彩色文字生成器：把普通文本转成 Minecraft 的 § 格式代码文本
// （聊天 / 告示牌 / 标题命令里可直接粘贴）。
//
// 两种输出模式：
//   · 逐字符精确渐变（1.16+ 的 §x§r§r§g§g§b§b 十六进制语法）——颜色最顺滑，文本最长；
//   · 分段渐变（每 N 个字符取一个色，落到 16 原版色）——短、兼容旧版本与告示牌字数限制。
// 样式代码（粗体/斜体/下划线/删除线/随机）按整段前缀插入。

using System.Globalization;

namespace Starcloud.Core.Services.Lab;

/// <summary>Minecraft 16 原版聊天色（含 hex 与其标准色码）</summary>
public sealed record LegacyColor(char Code, string Name, string Hex);

public static class McText
{
    public const char Section = '§';

    /// <summary>原版 16 色（顺序即色码表）</summary>
    public static readonly LegacyColor[] LegacyColors =
    [
        new('0', "黑", "#000000"), new('1', "深蓝", "#0000AA"), new('2', "深绿", "#00AA00"),
        new('3', "深青", "#00AAAA"), new('4', "深红", "#AA0000"), new('5', "深紫", "#AA00AA"),
        new('6', "金", "#FFAA00"), new('7', "灰", "#AAAAAA"), new('8', "深灰", "#555555"),
        new('9', "蓝", "#5555FF"), new('a', "绿", "#55FF55"), new('b', "青", "#55FFFF"),
        new('c', "红", "#FF5555"), new('d', "粉", "#FF55FF"), new('e', "黄", "#FFFF55"),
        new('f', "白", "#FFFFFF"),
    ];

    /// <summary>样式代码（加在整段文本前，作用于其后全部字符）</summary>
    public static string StyleCodes(bool bold = false, bool italic = false, bool underline = false,
        bool strikethrough = false, bool obfuscated = false)
    {
        var sb = new System.Text.StringBuilder();
        if (obfuscated) sb.Append(Section).Append('k');
        if (bold) sb.Append(Section).Append('l');
        if (strikethrough) sb.Append(Section).Append('m');
        if (underline) sb.Append(Section).Append('n');
        if (italic) sb.Append(Section).Append('o');
        return sb.ToString();
    }

    /// <summary>整段单色：十六进制精确色（§x 语法）或最近的 16 原版色</summary>
    public static string Solid(string text, string hexColor, bool legacyPalette = false, string styleCodes = "")
    {
        var prefix = legacyPalette
            ? $"{Section}{NearestLegacy(hexColor).Code}"
            : HexPrefix(hexColor);
        return styleCodes + prefix + text;
    }

    /// <summary>
    /// 渐变文本。exact=true 用逐字符十六进制（1.16+，颜色最顺滑）；
    /// exact=false 每 <paramref name="segmentLength"/> 个字符取一个原版色（兼容旧版本 / 告示牌）。
    /// </summary>
    public static string Gradient(string text, string fromHex, string toHex,
        bool exact = true, int segmentLength = 1, string styleCodes = "")
    {
        if (text.Length == 0) return "";
        var (r1, g1, b1) = ParseHex(fromHex);
        var (r2, g2, b2) = ParseHex(toHex);
        var sb = new System.Text.StringBuilder(styleCodes);

        for (var i = 0; i < text.Length; i++)
        {
            var t = text.Length == 1 ? 0 : (double)i / (text.Length - 1);
            var r = (byte)Math.Round(r1 + (r2 - r1) * t);
            var g = (byte)Math.Round(g1 + (g2 - g1) * t);
            var b = (byte)Math.Round(b1 + (b2 - b1) * t);
            var hex = $"#{r:X2}{g:X2}{b:X2}";

            if (exact)
            {
                sb.Append(HexPrefix(hex));
            }
            else if (i % Math.Max(1, segmentLength) == 0)
            {
                sb.Append(Section).Append(NearestLegacy(hex).Code);
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>彩虹渐变（按色相环均分，逐字符）</summary>
    public static string Rainbow(string text, bool exact = true, int segmentLength = 1, string styleCodes = "")
    {
        if (text.Length == 0) return "";
        var sb = new System.Text.StringBuilder(styleCodes);
        for (var i = 0; i < text.Length; i++)
        {
            var hue = 360.0 * i / Math.Max(1, text.Length);
            var hex = HueToHex(hue);
            if (exact)
            {
                sb.Append(HexPrefix(hex));
            }
            else if (i % Math.Max(1, segmentLength) == 0)
            {
                sb.Append(Section).Append(NearestLegacy(hex).Code);
            }
            sb.Append(text[i]);
        }
        return sb.ToString();
    }

    /// <summary>去掉全部 § 代码（校验 / 字数统计用）；§x 十六进制组按 6 组 §+位 整段吃掉</summary>
    public static string Strip(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != Section)
            {
                sb.Append(text[i]);
                continue;
            }
            if (i + 1 < text.Length && (text[i + 1] is 'x' or 'X'))
            {
                i++; // 吃掉 'x'
                for (var k = 0; k < 6 && i + 2 < text.Length; k++)
                {
                    if (text[i + 1] != Section) break;
                    i += 2; // 吃掉 § + 一位 hex
                }
                continue;
            }
            if (i + 1 < text.Length) i++; // 普通色码 / 样式码 §c
        }
        return sb.ToString();
    }

    /// <summary>十六进制色 → 最近的 16 原版色（RGB 距离最小）</summary>
    public static LegacyColor NearestLegacy(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        LegacyColor best = LegacyColors[^1];
        var bestDistance = long.MaxValue;
        foreach (var color in LegacyColors)
        {
            var (cr, cg, cb) = ParseHex(color.Hex);
            long dr = r - cr, dg = g - cg, db = b - cb;
            var distance = dr * dr + dg * dg + db * db;
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = color;
        }
        return best;
    }

    /// <summary>
    /// 1.16+ 十六进制色前缀：§x§r§r§g§g§b§b —— <b>每一位 hex 字符都各带一个 §</b>
    /// （写成 §x§FF§55§FF 游戏不认，这是实测确认的语法要求）。
    /// </summary>
    public static string HexPrefix(string hex)
    {
        var (r, g, b) = ParseHex(hex);
        var digits = $"{r:X2}{g:X2}{b:X2}";
        var sb = new System.Text.StringBuilder(digits.Length * 2 + 2);
        sb.Append(Section).Append('x');
        foreach (var c in digits) sb.Append(Section).Append(c);
        return sb.ToString();
    }

    public static (byte R, byte G, byte B) ParseHex(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length == 3)
            s = $"{s[0]}{s[0]}{s[1]}{s[1]}{s[2]}{s[2]}";
        if (s.Length != 6 || !int.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v))
            return (0xFF, 0xFF, 0xFF);
        return ((byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    private static string HueToHex(double hue)
    {
        var h = ((hue % 360) + 360) % 360;
        var c = 1.0;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return $"#{(byte)Math.Round(r * 255):X2}{(byte)Math.Round(g * 255):X2}{(byte)Math.Round(b * 255):X2}";
    }
}
