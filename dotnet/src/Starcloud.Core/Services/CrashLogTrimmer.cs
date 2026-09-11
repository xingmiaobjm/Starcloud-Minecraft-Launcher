// 崩溃日志文本裁剪 —— 发送给 Nova（云端 / 用户自填 LLM）前丢弃与病因无关的段落。
//
// 依据官网规格 docs/superpowers/specs/2026-09-11-launcher-crash-log-truncation-design.md：
//   全保留：崩溃头部（Description / Time / 签名行）/ Caused by:（可多层）/ -- System Details -- / Suspected Mods
//   保留前 N：at ... 调用栈（HeadLines=300）/ -- Affected level --（AffectedLevelLines=40）
//   丢弃：-- Mod List -- / Loaded Mods / -- Fabric Mods -- / -- GC -- / -- JVM -- / -- Memory --
//   兜底：命中签名但无任何已知段落标题 → 前 200 行 + 占位 + 后 100 行
//
// 硬约束（规格 §3.5）：纯函数（不读文件 / 不发网络 / 无全局状态）；不改变换行风格（CRLF 保持 CRLF）；
// 绝不抛异常（任何内部错误降级为「原样返回 + applied = false」）；非崩溃文本逐字节原样透传。
//
// 段落关键词表是「可维护常量」：上游格式变化时只改下面这批正则，不改算法。

using System.Text.RegularExpressions;

namespace Starcloud.Core.Services;

/// <summary>裁剪策略：crash-report（按段落裁剪）/ crash-report-fallback（前 200 + 后 100 兜底）/ none（未裁剪）</summary>
public enum TrimStrategy
{
    None,
    CrashReport,
    CrashReportFallback,
}

/// <summary>裁剪参数：调用栈保留行数 / Affected level 保留行数</summary>
public sealed record TrimOptions(int HeadLines = 300, int AffectedLevelLines = 40);

/// <summary>
/// 裁剪结果。<see cref="Text"/> 可直接发送；<see cref="DroppedLineCount"/> 供 UI 提示「已忽略 N 行」。
/// </summary>
public sealed record TrimOutcome(
    string Text,
    bool Applied,
    TrimStrategy Strategy,
    int OriginalChars,
    int TrimmedChars,
    IReadOnlyList<string> DroppedSections,
    int DroppedLineCount);

/// <summary>崩溃日志裁剪（纯函数，绝不抛异常）。</summary>
public static partial class CrashLogTrimmer
{
    /// <summary>原样返回（未裁剪）：非崩溃文本，或用户显式要求发送完整日志。</summary>
    public static TrimOutcome Passthrough(string? text)
    {
        var s = text ?? string.Empty;
        return new TrimOutcome(s, false, TrimStrategy.None, s.Length, s.Length, [], 0);
    }

    /// <summary>是否命中崩溃报告签名；普通提问 / 代码片段 / latest.log 一律不裁剪。</summary>
    public static bool IsCrashReport(string? text) =>
        !string.IsNullOrEmpty(text) && SignatureRegex().IsMatch(text);

    /// <summary>裁剪崩溃日志：保留完整因果链，丢弃与病因无关的段落。任何异常都降级为原样返回。</summary>
    public static TrimOutcome TrimCrashLog(string? text, TrimOptions? options = null)
    {
        var s = text ?? string.Empty;
        var none = Passthrough(s);
        if (s.Length == 0) return none;

        try
        {
            if (!IsCrashReport(s)) return none;

            var headLines = Math.Max(options?.HeadLines ?? 300, 0);
            var affectedLevelLines = Math.Max(options?.AffectedLevelLines ?? 40, 0);
            var eol = s.Contains("\r\n") ? "\r\n" : "\n"; // 不改变换行风格
            var lines = LineSplitRegex().Split(s);

            var kept = new List<string>(lines.Length);
            var dropped = new List<string>();
            var droppedLines = 0;
            var matchedAnyTitle = false;
            var i = 0;

            while (i < lines.Length)
            {
                var line = lines[i];
                var head = line.Trim();

                if (TitleRegex().IsMatch(head))
                {
                    matchedAnyTitle = true;

                    // 丢弃整段：mod 名单 / GC / JVM / 内存明细
                    if (IsDropTitle(head))
                    {
                        dropped.Add(head);
                        i++;
                        while (i < lines.Length && !TitleRegex().IsMatch(lines[i].Trim()))
                        {
                            droppedLines++;
                            i++;
                        }
                        continue;
                    }

                    // -- Affected level --：只保留前 affectedLevelLines 行
                    if (AffectedLevelRegex().IsMatch(head))
                    {
                        kept.Add(line);
                        i++;
                        var seen = 0;
                        while (i < lines.Length && !TitleRegex().IsMatch(lines[i].Trim()))
                        {
                            if (seen < affectedLevelLines) kept.Add(lines[i]);
                            else droppedLines++;
                            seen++;
                            i++;
                        }
                        if (seen > affectedLevelLines)
                            kept.Add($"... [trimmed {seen - affectedLevelLines} lines] ...");
                        continue;
                    }

                    // -- System Details -- 及其它标题：整段保留
                    kept.Add(line);
                    i++;
                    continue;
                }

                // Caused by: / Suspected Mods：单行全保留（最关键的诊断线索）
                if (CausedByRegex().IsMatch(head) || SuspectedRegex().IsMatch(head))
                {
                    kept.Add(line);
                    i++;
                    continue;
                }

                // 崩溃头部 / at ... 调用栈：前 (headLines + affectedLevelLines) 行保留
                kept.Add(line);
                i++;
                if (kept.Count >= headLines + affectedLevelLines)
                {
                    AppendRemainingBudget(kept, lines, i, ref droppedLines);
                    break;
                }
            }

            if (!matchedAnyTitle)
            {
                // 兜底：命中签名但一个已知段落标题都没匹配到（自定义日志 / 已被裁剪过的日志）
                var fallback = BuildHeadTail(lines, 200, 100, eol, out var fallbackDropped);
                if (fallback.Length >= s.Length) return none; // 裁不动就不裁（绝不增大体积）
                return new TrimOutcome(fallback, true, TrimStrategy.CrashReportFallback,
                    s.Length, fallback.Length, [], fallbackDropped);
            }

            var trimmed = string.Join(eol, kept);
            if (string.IsNullOrWhiteSpace(trimmed)) return none;
            return new TrimOutcome(trimmed, true, TrimStrategy.CrashReport,
                s.Length, trimmed.Length, dropped, droppedLines);
        }
        catch
        {
            return none; // 裁剪是优化：绝不能把一次正常请求变成失败
        }
    }

    /// <summary>其余行按「前 200 + 占位 + 后 100」压入（不重叠，保证输出不变长）。</summary>
    private static void AppendRemainingBudget(List<string> kept, string[] lines, int from, ref int droppedLines)
    {
        var rest = lines[from..];
        if (rest.Length == 0) return;

        var headCount = Math.Min(200, rest.Length);
        var tailStart = Math.Max(rest.Length - 100, headCount);
        for (var k = 0; k < headCount; k++) kept.Add(rest[k]);

        var trimmedCount = tailStart - headCount;
        if (trimmedCount > 0)
        {
            kept.Add($"... [trimmed {trimmedCount} lines] ...");
            droppedLines += trimmedCount;
        }
        for (var k = tailStart; k < rest.Length; k++) kept.Add(rest[k]);
    }

    /// <summary>前 N 行 + 占位 + 后 M 行（不重叠）。</summary>
    private static string BuildHeadTail(string[] lines, int headLines, int tailLines, string eol, out int droppedLines)
    {
        var headCount = Math.Min(headLines, lines.Length);
        var tailStart = Math.Max(lines.Length - tailLines, headCount);
        droppedLines = tailStart - headCount;

        var outLines = new List<string>(headCount + tailLines + 1);
        for (var k = 0; k < headCount; k++) outLines.Add(lines[k]);
        if (droppedLines > 0) outLines.Add($"... [trimmed {droppedLines} lines] ...");
        for (var k = tailStart; k < lines.Length; k++) outLines.Add(lines[k]);
        return string.Join(eol, outLines);
    }

    private static bool IsDropTitle(string line) =>
        ModListRegex().IsMatch(line) || LoadedModsRegex().IsMatch(line) ||
        FabricModsRegex().IsMatch(line) || GcRegex().IsMatch(line) ||
        JvmRegex().IsMatch(line) || MemoryRegex().IsMatch(line);

    // ── 段落关键词表（可维护常量） ──

    // 签名：`---- Minecraft Crash Report ----` 或任意以 `---- ` 开头且含 `Crash Report` 的行
    [GeneratedRegex(@"^(-{2,}\s*Minecraft Crash Report\s*-{2,}|-{2,}\s.*Crash Report.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex SignatureRegex();

    [GeneratedRegex(@"\r?\n")]
    private static partial Regex LineSplitRegex();

    [GeneratedRegex(@"^-{2,}\s+.+\s+-{2,}$")]
    private static partial Regex TitleRegex();

    [GeneratedRegex(@"^-{2,}\s*Affected level\s*-{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex AffectedLevelRegex();

    [GeneratedRegex(@"^-{2,}\s*Mod List\s*-{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex ModListRegex();

    [GeneratedRegex(@"^-{2,}\s*Loaded Mods\s*-{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex LoadedModsRegex();

    [GeneratedRegex(@"^-{2,}\s*Fabric Mods\s*-{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex FabricModsRegex();

    [GeneratedRegex(@"^-{2,}\s*GC\s*-{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex GcRegex();

    [GeneratedRegex(@"^-{2,}\s*JVM\s*-{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex JvmRegex();

    [GeneratedRegex(@"^-{2,}\s*Memory\s*-{2,}$", RegexOptions.IgnoreCase)]
    private static partial Regex MemoryRegex();

    [GeneratedRegex(@"^Suspected Mods", RegexOptions.IgnoreCase)]
    private static partial Regex SuspectedRegex();

    [GeneratedRegex(@"^Caused by:", RegexOptions.IgnoreCase)]
    private static partial Regex CausedByRegex();
}
