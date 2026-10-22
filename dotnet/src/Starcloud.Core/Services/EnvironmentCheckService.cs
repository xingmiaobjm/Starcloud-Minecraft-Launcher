// 启动前环境体检：轻量本地检查（不联网、不枚举杀软、不做 SHA1）
// 设计：docs/designs/2026-09-11-launch-reliability-design.md §6

namespace Starcloud.Core.Services;

/// <summary>体检项级别：Block 会阻断启动，Warn 提示可继续，Ok 无问题</summary>
public enum EnvLevel { Ok, Warn, Block }

/// <summary>单个体检项</summary>
public sealed record EnvIssue(
    string Code,
    EnvLevel Level,
    string Title,
    string Detail,
    bool AutoFixable,
    string? FixHint);

/// <summary>体检汇总</summary>
public sealed record EnvironmentReport(
    List<EnvIssue> Issues,
    long FreeDiskMb,
    int ModCount,
    bool HasShaders)
{
    public EnvLevel Worst => Issues.Count == 0 ? EnvLevel.Ok : Issues.Max(i => i.Level);
    public bool NeedsAttention => Issues.Count > 0;
}

/// <summary>启动前环境体检；单项失败不影响整体（各自 try/catch）</summary>
public sealed class EnvironmentCheckService
{
    private const long DiskBlockMb = 512;
    private const long DiskWarnMb = 2048;
    private const int PathLengthWarn = 200;
    private const int ModsManyThreshold = 150;

    private readonly InstallerService _installer;
    private readonly JavaService _java;

    public EnvironmentCheckService(InstallerService installer, JavaService java)
    {
        _installer = installer;
        _java = java;
    }

    public async Task<EnvironmentReport> CheckAsync(string dotMc, string versionId,
        LaunchOptions options, LauncherSettings settings, CancellationToken ct = default)
    {
        var gameRoot = options.GameDir;
        var (modCount, hasShaders) = MemoryAdvisor.Probe(gameRoot);
        var issues = new List<EnvIssue>();
        var freeDiskMb = CheckDisk(gameRoot, issues);

        CheckPath(dotMc, gameRoot, issues);
        CheckWritable(gameRoot, issues);
        CheckMemory(options, issues);
        CheckModsMany(modCount, issues);

        await CheckJavaAsync(dotMc, versionId, options, ct, issues).ConfigureAwait(false);

        return new EnvironmentReport(issues, freeDiskMb, modCount, hasShaders);
    }

    /// <summary>GameRoot 所在盘剩余空间；< 512 MB Block，< 2 GB Warn；探测失败跳过</summary>
    private static long CheckDisk(string gameRoot, List<EnvIssue> issues)
    {
        long freeMb = -1;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(gameRoot));
            if (!string.IsNullOrEmpty(root))
            {
                var drive = new DriveInfo(root);
                if (drive.IsReady) freeMb = drive.AvailableFreeSpace / 1024 / 1024;
            }
        }
        catch { return -1; }

        if (freeMb < 0) return freeMb;
        if (freeMb < DiskBlockMb)
        {
            issues.Add(new EnvIssue("disk_low", EnvLevel.Block,
                "磁盘剩余空间不足", $"游戏目录所在盘仅剩 {freeMb} MB，至少需要 {DiskBlockMb} MB", false, null));
        }
        else if (freeMb < DiskWarnMb)
        {
            issues.Add(new EnvIssue("disk_low", EnvLevel.Warn,
                "磁盘空间偏低", $"游戏目录所在盘剩余 {freeMb} MB，建议清理到 2 GB 以上", false, null));
        }
        return freeMb;
    }

    /// <summary>非 ASCII 路径（CJK 与空格除外）与超长路径：仅提示，不影响启动</summary>
    private static void CheckPath(string dotMc, string gameRoot, List<EnvIssue> issues)
    {
        try
        {
            if (HasRiskyNonAscii(dotMc) || HasRiskyNonAscii(gameRoot))
            {
                issues.Add(new EnvIssue("path_non_ascii", EnvLevel.Warn,
                    "游戏路径含特殊字符", $"路径中可能含非 ASCII 字符：{gameRoot}", false, null));
            }
            if (gameRoot.Length > PathLengthWarn)
            {
                issues.Add(new EnvIssue("path_too_long", EnvLevel.Warn,
                    "游戏路径过长", $"路径长度 {gameRoot.Length} 超过 {PathLengthWarn}，可能出现写入失败", false, null));
            }
        }
        catch { }
    }

    /// <summary>GameRoot 可写性：写临时文件失败 → Block（通常是权限或只读盘）</summary>
    private static void CheckWritable(string gameRoot, List<EnvIssue> issues)
    {
        try
        {
            Directory.CreateDirectory(gameRoot);
            var probe = Path.Combine(gameRoot, $".starcloud-write-test-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            issues.Add(new EnvIssue("readonly", EnvLevel.Block,
                "游戏目录不可写", $"{gameRoot} 无法写入（{ex.GetType().Name}），请换目录或以管理员身份运行", false, null));
        }
    }

    /// <summary>内存档位合法性：Min > Max 或 Max < 512 → Block；Max 超过物理 80% → Warn</summary>
    private static void CheckMemory(LaunchOptions options, List<EnvIssue> issues)
    {
        try
        {
            var suggest = MemoryAdvisor.SuggestMaxMb(SystemMemory.Snapshot().TotalMb, 0, false);
            if (options.MinMemoryMb > options.MaxMemoryMb || options.MaxMemoryMb < 512)
            {
                issues.Add(new EnvIssue("mem_invalid", EnvLevel.Block,
                    "内存设置不合法",
                    $"最小 {options.MinMemoryMb} MB / 最大 {options.MaxMemoryMb} MB，建议恢复为 {suggest} MB",
                    true, "恢复推荐值"));
                return;
            }
            var totalPhys = SystemMemory.Snapshot().TotalMb;
            if (totalPhys > 0 && options.MaxMemoryMb > totalPhys * 0.8)
            {
                issues.Add(new EnvIssue("mem_over_physical", EnvLevel.Warn,
                    "分配内存偏高",
                    $"最大内存 {options.MaxMemoryMb} MB 超过物理内存的 80%，建议下调到 {suggest} MB",
                    true, "下调到推荐值"));
            }
        }
        catch { }
    }

    /// <summary>模组数量过多：提示并可一键套用智能内存推荐值</summary>
    private static void CheckModsMany(int modCount, List<EnvIssue> issues)
    {
        if (modCount <= ModsManyThreshold) return;
        issues.Add(new EnvIssue("mods_many", EnvLevel.Warn,
            "模组数量较多", $"检测到 {modCount} 个模组，建议提高分配内存",
            true, "套用智能内存推荐值"));
    }

    /// <summary>Java 检查：显式指定的 Java 不存在 → Block；要求的版本无匹配运行时 → Warn（可自动下载）</summary>
    private async Task CheckJavaAsync(string dotMc, string versionId, LaunchOptions options,
        CancellationToken ct, List<EnvIssue> issues)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(options.JavaExecutable) && !File.Exists(options.JavaExecutable))
            {
                issues.Add(new EnvIssue("java_missing", EnvLevel.Block,
                    "指定的 Java 不存在", $"找不到 {options.JavaExecutable}，请到设置页重新选择 Java", false, null));
                return;
            }

            var required = 8;
            try
            {
                var version = await _installer.LoadVersionAsync(dotMc, versionId, ct).ConfigureAwait(false);
                required = JavaRequirement.Resolve(versionId, version.JavaVersion);
            }
            catch { /* 读不到版本 JSON 就按 8 处理 */ }

            var runtimes = await _java.ListRuntimesAsync(ct).ConfigureAwait(false);
            if (!runtimes.Any(r => r.MajorVersion >= required))
            {
                issues.Add(new EnvIssue("java_missing", EnvLevel.Warn,
                    "未找到匹配的 Java",
                    $"该版本需要 Java {required}，未检测到匹配运行时，将在启动时尝试自动下载", false, null));
            }
        }
        catch { }
    }

    /// <summary>是否含 CJK/空格以外的非 ASCII 字符</summary>
    private static bool HasRiskyNonAscii(string path)
    {
        foreach (var c in path)
        {
            if (c <= 127) continue;
            if (c is >= '\u4e00' and <= '\u9fff') continue;  // 汉字
            if (c is >= '\u3000' and <= '\u303f') continue;  // CJK 标点
            if (c is >= '\uff00' and <= '\uffef') continue;  // 全角字符
            return true;
        }
        return false;
    }
}
