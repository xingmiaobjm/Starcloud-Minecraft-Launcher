// 崩溃诊断：离线确定性规则匹配（不依赖 LLM，秒级出结果），配合一键修复
// 设计：docs/designs/2026-09-11-launch-reliability-design.md §7

namespace Starcloud.Core.Services;

/// <summary>崩溃诊断结果</summary>
public sealed record CrashDiagnosis(
    string Title,
    string Summary,
    string Evidence,
    string Source,
    List<string> Suggestions,
    CrashFixKind Fix,
    string? FixLabel,
    int? SuggestMaxMemoryMb);

/// <summary>一键修复动作类型</summary>
public enum CrashFixKind
{
    None,
    OpenCrashReport,
    SetMemory,
    ReinstallVersion,
    OpenModsFolder,
    ReinstallJavaHint,
}

/// <summary>崩溃诊断（纯本地规则匹配；Nova 的 CrashTriageAsync 保留为「深层分析」互补入口）</summary>
public sealed class CrashAnalysisService
{
    private const int MaxEvidenceChars = 200;

    private readonly InstallerService _installer;

    public CrashAnalysisService(InstallerService installer) => _installer = installer;

    /// <summary>诊断一局崩溃：证据回退链 crash-reports → logs/latest.log → 本局输出</summary>
    public async Task<CrashDiagnosis> AnalyzeAsync(string dotMc, string versionId, int exitCode,
        DateTime? startedAt, IReadOnlyList<string>? sessionTail, string gameRoot,
        CancellationToken ct = default)
    {
        var (modCount, hasShaders) = MemoryAdvisor.Probe(gameRoot);
        var suggestMb = MemoryAdvisor.SuggestMaxMb(SystemMemory.Snapshot().TotalMb, modCount, hasShaders);
        var (source, lines) = CollectEvidence(gameRoot, startedAt, sessionTail);

        // 证据为空/极短：进程启动即失败
        if (lines.Count == 0)
        {
            return new CrashDiagnosis(
                "进程启动即失败",
                $"游戏进程刚启动就退出了（退出码 {exitCode}），且没有产生任何日志。",
                "",
                "（无日志）",
                ["检查 Java 是否可用（设置页 → 启动）", "尝试重新安装该版本", "确认游戏路径没有特殊字符"],
                CrashFixKind.ReinstallVersion,
                "重新安装该版本",
                null);
        }

        var text = string.Join('\n', lines);

        if (ContainsAny(text, "OutOfMemoryError", "Java heap space", "heap space",
                "0xC00000FD", "insufficient memory", "Failed to allocate")
            || exitCode == unchecked((int)0xC00000FD))
        {
            return new CrashDiagnosis(
                "内存不足",
                $"游戏因内存不足而崩溃（退出码 {exitCode}）。当前分配偏小或模组占用过高。",
                Snippet(lines, ["OutOfMemoryError", "heap space", "0xC00000FD"]),
                source,
                [
                    $"把该版本最大内存调整到 {suggestMb} MB",
                    "关闭光影或降低渲染距离",
                    "减少同时加载的模组",
                ],
                CrashFixKind.SetMemory,
                $"调整内存为 {suggestMb} MB",
                suggestMb);
        }

        if (ContainsAny(text, "UnsupportedClassVersionError", "class file version",
                "Unsupported major.minor", "requires at least Java"))
        {
            var required = await RequiredJavaMajorAsync(dotMc, versionId, ct).ConfigureAwait(false);
            return new CrashDiagnosis(
                "Java 版本不匹配",
                $"游戏加载的类需要更高版本的 Java（退出码 {exitCode}）。该版本建议使用 Java {required}。",
                Snippet(lines, ["UnsupportedClassVersionError", "class file version", "Unsupported major.minor"]),
                source,
                [
                    $"到设置页把该版本的 Java 切换到 Java {required}",
                    "或在版本设置里选择「自动」让启动器匹配 Java",
                ],
                CrashFixKind.ReinstallJavaHint,
                "去设置页选择 Java",
                null);
        }

        if (ContainsAny(text, "NoSuchMethodError", "NoClassDefFoundError", "ModResolutionException",
                "Incompatible mod set", "Missing or unsupported mandatory dependencies",
                "Mod loading has failed"))
        {
            return new CrashDiagnosis(
                "模组依赖冲突",
                $"模组之间依赖不一致导致加载失败（退出码 {exitCode}）。启动器不会自动删除模组。",
                Snippet(lines, ["NoSuchMethodError", "NoClassDefFoundError", "ModResolutionException",
                    "Incompatible mod set", "Mod loading has failed"]),
                source,
                [
                    "在「内容」页运行模组冲突扫描",
                    "检查最近新增/更新的模组是否与当前版本匹配",
                    "确认前置模组（Fabric API / 依赖库）已安装",
                ],
                CrashFixKind.OpenModsFolder,
                "打开模组目录",
                null);
        }

        if (ContainsAny(text, "GLFW error", "Failed to create window",
                "Pixel format not accelerated", "does not appear to support OpenGL"))
        {
            return new CrashDiagnosis(
                "显卡/窗口初始化失败",
                $"游戏无法创建渲染窗口（退出码 {exitCode}），通常是显卡驱动或光影问题。",
                Snippet(lines, ["GLFW error", "Failed to create window", "Pixel format not accelerated"]),
                source,
                [
                    "更新显卡驱动到最新版本",
                    "暂时关闭光影后再试",
                    "适当降低分配内存（过大也会导致初始化失败）",
                ],
                CrashFixKind.None,
                null,
                null);
        }

        if (ContainsAny(text, "AccessDeniedException", "Permission denied",
                "FileSystemException", "拒绝访问"))
        {
            return new CrashDiagnosis(
                "文件权限被拒",
                $"游戏写入文件时被系统拒绝（退出码 {exitCode}）。",
                Snippet(lines, ["AccessDeniedException", "Permission denied", "FileSystemException"]),
                source,
                [
                    "把游戏目录换到不含系统保护的位置",
                    "以管理员身份运行启动器",
                    "把游戏目录加入杀毒软件白名单",
                ],
                CrashFixKind.None,
                null,
                null);
        }

        if (ContainsAny(text, "The game crashed whilst", "Watching server thread"))
        {
            return new CrashDiagnosis(
                "游戏崩溃（详见崩溃报告）",
                $"游戏报告了一次崩溃（退出码 {exitCode}），本地规则未能匹配到已知原因。",
                Snippet(lines, ["The game crashed whilst"]),
                source,
                ["打开崩溃报告查看堆栈", "把崩溃报告交给 Nova 做深层分析"],
                CrashFixKind.OpenCrashReport,
                "打开崩溃报告",
                null);
        }

        return new CrashDiagnosis(
            "游戏异常退出",
            $"游戏以退出码 {exitCode} 结束，本地规则未匹配到已知原因。",
            Tail3(lines),
            source,
            ["打开日志确认最后一条错误", "让 Nova 做一次深层分析", "若刚更新过模组，尝试回退"],
            CrashFixKind.OpenCrashReport,
            "打开崩溃报告",
            null);
    }

    /// <summary>取「命中行 + 前一行」，各行截断到 200 字符</summary>
    private static string Snippet(List<string> lines, string[] keys)
    {
        var idx = lines.FindIndex(l => keys.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase)));
        if (idx < 0) return Tail3(lines);
        var start = Math.Max(0, idx - 1);
        var take = lines.Skip(start).Take(idx - start + 1).ToList();
        return string.Join('\n', take.Select(Trim));
    }

    private static string Tail3(List<string> lines) =>
        string.Join('\n', lines.TakeLast(3).Select(Trim));

    private static string Trim(string line) =>
        line.Length <= MaxEvidenceChars ? line : line[..MaxEvidenceChars] + "…";

    private static bool ContainsAny(string text, params string[] keys) =>
        keys.Any(k => text.Contains(k, StringComparison.OrdinalIgnoreCase));

    private async Task<int> RequiredJavaMajorAsync(string dotMc, string versionId, CancellationToken ct)
    {
        try
        {
            var version = await _installer.LoadVersionAsync(dotMc, versionId, ct).ConfigureAwait(false);
            return JavaRequirement.Resolve(versionId, version.JavaVersion);
        }
        catch { return 8; }
    }

    /// <summary>证据回退链：crash-reports（本局之后生成）→ logs/latest.log 尾 200 行 → 本局输出尾 60 行</summary>
    private static (string Source, List<string> Lines) CollectEvidence(string gameRoot,
        DateTime? startedAt, IReadOnlyList<string>? sessionTail)
    {
        try
        {
            var dir = Path.Combine(gameRoot, "crash-reports");
            if (Directory.Exists(dir))
            {
                var latest = Directory.EnumerateFiles(dir, "*.txt")
                    .Select(f => new FileInfo(f))
                    .Where(f => !startedAt.HasValue || f.LastWriteTime >= startedAt.Value)
                    .OrderByDescending(f => f.LastWriteTime)
                    .FirstOrDefault();
                if (latest is not null)
                {
                    var all = ReadTail(latest.FullName, 200);
                    if (all.Count > 0) return (latest.FullName, all);
                }
            }
        }
        catch { }

        try
        {
            var log = Path.Combine(gameRoot, "logs", "latest.log");
            if (File.Exists(log))
            {
                var all = ReadTail(log, 200);
                if (all.Count > 0) return (log, all);
            }
        }
        catch { }

        if (sessionTail is { Count: > 0 })
            return ("本局输出", sessionTail.TakeLast(60).ToList());

        return ("", []);
    }

    private static List<string> ReadTail(string file, int count)
    {
        try
        {
            var lines = File.ReadAllLines(file);
            return lines.Length <= count ? [.. lines] : lines[^count..].ToList();
        }
        catch { return []; }
    }
}
