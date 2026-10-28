// Forge / NeoForge 支持：构建列表查询 + 官方安装器静默运行（java -jar installer --installClient）

using System.Diagnostics;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>加载器构建（Version 为原始构建号，Note 为推荐标记如 "推荐"/"最新稳定版"）</summary>
public sealed record LoaderBuild(string Version, string? Note = null, bool Stable = true);

public sealed class ForgeInstallerService
{
    private readonly DownloadService _downloader;
    private readonly JavaService _java;

    public ForgeInstallerService(DownloadService downloader, JavaService java)
    {
        _downloader = downloader;
        _java = java;
    }

    // ── 构建列表 ──

    /// <summary>Forge 构建列表：maven-metadata 全量构建（新→旧），promotions 提供"推荐/最新"标记</summary>
    public async Task<List<LoaderBuild>> ListForgeBuildsAsync(string mcVersion, CancellationToken ct = default)
    {
        var meta = await _downloader.GetStringAsync(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml", ct);
        var promosJson = await _downloader.GetStringAsync(
            "https://files.minecraftforge.net/net/minecraftforge/forge/promotions_slim.json", ct);

        var prefix = $"{mcVersion}-";
        var builds = new List<string>();
        foreach (var tag in SplitVersions(meta))
        {
            if (tag.StartsWith(prefix, StringComparison.Ordinal) && IsForgeBuildSuffix(tag[prefix.Length..]))
                builds.Add(tag);
        }
        // maven-metadata 顺序不保证按时间，按构建号数字排序（新→旧）
        builds.Sort((a, b) => CompareForgeBuilds(b[prefix.Length..], a[prefix.Length..]));

        string? recommended = null, latest = null;
        using (var doc = JsonDocument.Parse(promosJson))
        {
            var promos = doc.RootElement.GetProperty("promos");
            if (promos.TryGetProperty($"{mcVersion}-recommended", out var rec))
                recommended = rec.GetString();
            if (promos.TryGetProperty($"{mcVersion}-latest", out var lat))
                latest = lat.GetString();
        }

        return builds
            .Select(v => new LoaderBuild(v[prefix.Length..],
                v == $"{prefix}{recommended}" ? "推荐"
                : v == $"{prefix}{latest}" ? "最新" : null))
            .ToList();
    }

    /// <summary>
    /// Forge 构建号判定：现代形如 "47.4.5"（含点号），旧版形如 "10.13.4.1614-1.7.10" / "10.13.3.1401-1710ls"（带后缀）。
    /// 原先用 int.TryParse 判定，现代构建号必然解析失败，导致列表恒为空。
    /// </summary>
    private static bool IsForgeBuildSuffix(string s)
    {
        if (s.Length == 0 || !char.IsAsciiDigit(s[0])) return false;
        foreach (var c in s)
            if (!char.IsAsciiDigit(c) && !char.IsAsciiLetter(c) && c != '.' && c != '-') return false;
        return true;
    }

    /// <summary>Forge 构建号比较：忽略 "-" 后缀，逐段按数字比较，段数多者视为更新</summary>
    private static int CompareForgeBuilds(string x, string y)
    {
        static int[] Key(string v) =>
            v.Split('-')[0].Split('.').Select(p => int.TryParse(p, out var n) ? n : 0).ToArray();
        var xs = Key(x);
        var ys = Key(y);
        for (var i = 0; i < Math.Min(xs.Length, ys.Length); i++)
            if (xs[i] != ys[i]) return xs[i].CompareTo(ys[i]);
        return xs.Length.CompareTo(ys.Length);
    }

    /// <summary>从 maven-metadata.xml 提取 &lt;version&gt; 标签内容</summary>
    private static IEnumerable<string> SplitVersions(string xml)
    {
        const string open = "<version>", close = "</version>";
        var i = 0;
        while ((i = xml.IndexOf(open, i, StringComparison.Ordinal)) >= 0)
        {
            var start = i + open.Length;
            var end = xml.IndexOf(close, start, StringComparison.Ordinal);
            if (end < 0) yield break;
            yield return xml[start..end].Trim();
            i = end;
        }
    }

    /// <summary>
    /// NeoForge 构建列表（按 MC 版本前缀过滤，新→旧；不适配的版本返回空）。
    /// 1.20.1 的产物发布在 legacy 的 forge 构件下（版本号形如 1.20.1-47.1.106），其余在 neoforge 构件下。
    /// </summary>
    public async Task<List<LoaderBuild>> ListNeoForgeBuildsAsync(string mcVersion, CancellationToken ct = default)
    {
        var prefix = NeoForgePrefix(mcVersion);
        if (prefix is null) return [];

        var legacy = mcVersion == "1.20.1";
        var artifact = legacy ? "forge" : "neoforge";
        var json = await _downloader.GetStringAsync(
            $"https://maven.neoforged.net/api/maven/versions/releases/net/neoforged/{artifact}", ct);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        // API 返回格式兼容：字符串数组，或含 "versions" 数组的对象
        var items = root.ValueKind == JsonValueKind.Array
            ? root.EnumerateArray()
            : root.TryGetProperty("versions", out var arr) && arr.ValueKind == JsonValueKind.Array
                ? arr.EnumerateArray()
                : default;

        if (items.Equals(default)) return [];
        // 1.20.1 前缀为 "1.20.1-47.1."（含连字符），1.x 时代前缀 "21.1." 不含构建号需剥离；
        // 新命名（如 26.2）构件版本号形如 26.2.0.57，MC 版本本身就是前缀，不剥离
        var oldNaming = mcVersion.StartsWith("1.", StringComparison.Ordinal);
        var skip = legacy ? mcVersion.Length + 1 : oldNaming ? prefix!.Length : 0;
        var all = items
            .Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : null)
            .OfType<string>()
            .Where(v => v.StartsWith(prefix, StringComparison.Ordinal))
            .Select(v => v[skip..])
            .OrderByDescending(NeoForgeOrderKey)
            .ToList();
        bool stableLabeled = false;
        return all
            .Select((v, i) =>
            {
                var isBeta = v.EndsWith("-beta", StringComparison.Ordinal);
                string? label;
                if (isBeta) label = "Beta";
                else if (!stableLabeled) { label = "最新稳定版"; stableLabeled = true; }
                else label = null;
                return new LoaderBuild(v, i == 0 && !isBeta ? "最新稳定版" : label);
            })
            .ToList();
    }

    /// <summary>
    /// NeoForge 版本号前缀：1.20.1 → "1.20.1-47.1."（legacy forge 构件）；
    /// 1.x[.y] → "x.y."（如 1.20.4 → "20.4."、1.21 → "21.0."）；
    /// 新命名（如 26.2）→ "26.2."（NeoForge 2026 起直接以 MC 版本为构件版本前缀，形如 26.2.0.57）。
    /// </summary>
    private static string? NeoForgePrefix(string mcVersion)
    {
        if (mcVersion == "1.20.1") return "1.20.1-47.1.";
        var seg = mcVersion.StartsWith("1.", StringComparison.Ordinal) ? mcVersion[2..] : mcVersion;

        var parts = seg.Split('.');
        if (parts.Length is < 2 or > 3) return null;
        if (!int.TryParse(parts[0], out _) || !int.TryParse(parts[1], out _)) return null;
        if (parts.Length == 3 && !int.TryParse(parts[2], out _)) return null;
        return $"{seg}.";
    }

    private static (long Major, long Minor, long Patch) NeoForgeOrderKey(string v)
    {
        var parts = v.Split('.');
        static long P(string[] a, int i) => i < a.Length && long.TryParse(a[i], out var n) ? n : 0;
        // 支持四段号（26.2.0.57）：第 3、4 段折叠进 patch 位参与排序
        return (P(parts, 0), P(parts, 1), P(parts, 2) * 1_000_000 + P(parts, 3));
    }

    // ── 安装器直链 ──

    public static string ForgeInstallerUrl(string mcVersion, string build) =>
        $"https://maven.minecraftforge.net/net/minecraftforge/forge/{mcVersion}-{build}/forge-{mcVersion}-{build}-installer.jar";

    /// <summary>
    /// NeoForge 安装器直链。1.20.1 产物在 legacy 的 forge 构件下（版本号形如 1.20.1-47.1.106），
    /// 其余在 neoforge 构件下；build 若带 MC 前缀会自动剥离。
    /// </summary>
    public static string NeoForgeInstallerUrl(string mcVersion, string build)
    {
        var ver = build.StartsWith($"{mcVersion}-", StringComparison.Ordinal)
            ? build[(mcVersion.Length + 1)..] : build;
        return mcVersion == "1.20.1"
            ? $"https://maven.neoforged.net/releases/net/neoforged/forge/{mcVersion}-{ver}/forge-{mcVersion}-{ver}-installer.jar"
            : $"https://maven.neoforged.net/releases/net/neoforged/neoforge/{ver}/neoforge-{ver}-installer.jar";
    }

    // ── 静默安装 ──

    /// <summary>
    /// 下载官方安装器并以 --installClient 静默安装。
    /// 前置：原版版本 JSON 已存在于游戏目录；requiredJavaMajor 来自版本 JSON 的 javaVersion。
    /// </summary>
    public async Task InstallViaInstallerAsync(
        string installerUrl, string mcVersion, int requiredJavaMajor,
        LauncherSettings settings, IProgress<BatchProgress>? progress, CancellationToken ct = default)
    {
        // 1. 确定可用的 Java（不满足则自动下载 Adoptium 运行时）
        var javaExe = await EnsureJavaAsync(requiredJavaMajor, progress, ct);

        // 2. 下载安装器
        var tempDir = Path.Combine(Path.GetTempPath(), "starcloud-installer");
        Directory.CreateDirectory(tempDir);
        var name = Path.GetFileName(new Uri(installerUrl).LocalPath);
        var installerPath = Path.Combine(tempDir, name);
        Report(progress, $"下载 {name}…");
        await _downloader.DownloadFileAsync(installerUrl, installerPath, null,
            settings.DownloadMaxRetries, ct);

        // Forge 官方安装器会先读游戏目录里的 launcher_profiles.json，缺了就直接报
        // “There is no minecraft launcher profile...” 并退出码 1。PCL/HMCL 都在跑安装器前
        // 自动补这个文件；这里保持一致（只缺时才建，不覆盖用户已有的配置）。
        EnsureLauncherProfiles(settings.DotMinecraft);

        // 3. 运行官方安装器（--installClient 会写入 versions/<id>-forge-<build> 并补齐库文件）
        Report(progress, "正在运行官方安装器（可能需要数分钟）…");
        var psi = new ProcessStartInfo
        {
            FileName = javaExe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            WorkingDirectory = tempDir,
        };
        psi.ArgumentList.Add("-jar");
        psi.ArgumentList.Add(installerPath);
        psi.ArgumentList.Add("--installClient");
        psi.ArgumentList.Add(settings.DotMinecraft);

        // 安装失败时要清掉的版本目录：官方安装器半途失败会留下残缺的 versions/<id>，
        // 留着会变成一个「装了但启动就崩」的坏版本（PCL 的 McInstallFailedClearFolder 同思路）。
        var targetVersionId = GuessForgeVersionId(mcVersion, installerUrl);
        var targetVersionDir = string.IsNullOrEmpty(targetVersionId)
            ? null
            : Path.Combine(settings.DotMinecraft, "versions", targetVersionId);

        using var p = Process.Start(psi) ?? throw new InvalidOperationException("无法启动 Java 进程");
        var stdErrTask = p.StandardError.ReadToEndAsync(ct);
        string? lastLine = null;
        // 安装器最后几行里会打印 "true" 表示成功（PCL 靠这个判定而不是 exit code：
        // 官方安装器在部分路径下 exit code 不可靠）
        var successMarker = false;
        var recentLines = new Queue<string>();
        var logTask = Task.Run(async () =>
        {
            while (await p.StandardOutput.ReadLineAsync(ct) is { } line)
            {
                if (line.Length == 0) continue;
                lastLine = line;
                recentLines.Enqueue(line);
                while (recentLines.Count > 8) recentLines.Dequeue();
                if (line.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)) successMarker = true;
                Report(progress, DescribeInstallStage(line));
            }
        }, ct);
        await p.WaitForExitAsync(ct);
        try { await logTask; } catch (OperationCanceledException) { }

        if (p.ExitCode != 0 || !successMarker)
        {
            var err = await stdErrTask;
            // 失败清理：把半成品的版本目录删掉，别留一个坏版本在列表里
            if (targetVersionDir is not null && Directory.Exists(targetVersionDir))
            {
                try { Directory.Delete(targetVersionDir, recursive: true); } catch { /* 清理失败不影响报错 */ }
            }
            var detail = err.Length > 0 ? err : lastLine;
            throw new InvalidOperationException(
                $"安装器执行失败（exit {p.ExitCode}）：{detail}");
        }
        Report(progress, $"安装器执行完成（{mcVersion}）");
        // 原版只在列表里隐藏、不删文件：Forge 版本启动仍可能引用它的 JSON 与 client jar。
        // 列表里只留 Forge 版本，用户不会再误选原版。
        VersionVisibility.MarkDependency(settings.DotMinecraft, mcVersion);
    }

    /// <summary>
    /// 把安装器的原始输出翻译成阶段提示 + 百分比。
    /// <para>参照 PCL 的 ForgelikeInjectorLine：官方安装器输出的是
    /// 「Extracting json」「Building Processors」「Remapping jar... 50%」这类英文行，
    /// 直接甩给用户看等于没有进度。这里映射成「正在合并映射（50%）」这种。</para>
    /// </summary>
    internal static string DescribeInstallStage(string line)
    {
        var text = line.Trim();
        // 已经是百分比行（Remapping jar... 50%）：保留原样但换个说法
        if (text.StartsWith("Remapping jar", StringComparison.OrdinalIgnoreCase))
            return $"正在重映射最终 jar（{ExtractPercent(text)}）";
        if (text.Contains("Extracting json", StringComparison.OrdinalIgnoreCase))
            return "正在解压版本信息…";
        if (text.Contains("Downloading libraries", StringComparison.OrdinalIgnoreCase))
            return "正在下载依赖库…";
        if (text.Contains("Building Processors", StringComparison.OrdinalIgnoreCase))
            return "正在构建处理器…";
        if (text.Contains("DOWNLOAD_MOJMAPS", StringComparison.OrdinalIgnoreCase))
            return "正在下载官方映射…";
        if (text.Contains("MERGE_MAPPING", StringComparison.OrdinalIgnoreCase))
            return "正在合并映射…";
        if (text.Contains("Splitting:", StringComparison.OrdinalIgnoreCase))
            return "正在拆分映射…";
        if (text.Contains("Parameter Annotations", StringComparison.OrdinalIgnoreCase))
            return "正在处理参数注解…";
        if (text.Contains("Processing Complete", StringComparison.OrdinalIgnoreCase))
            return "处理完成，正在收尾…";
        if (text.Contains("Sorting", StringComparison.OrdinalIgnoreCase))
            return "正在排序库文件…";
        if (text.Contains("Injecting profile", StringComparison.OrdinalIgnoreCase))
            return "正在写入版本配置…";
        if (text.Contains("File exists", StringComparison.OrdinalIgnoreCase))
            return "校验已有文件…";
        // 其它行原样透传（错误信息等）
        return text;
    }

    /// <summary>从「Remapping jar... 50%」这类行里取出百分比。</summary>
    private static string ExtractPercent(string text)
    {
        var idx = text.LastIndexOf('%');
        if (idx <= 0) return "";
        var start = idx;
        while (start > 0 && char.IsDigit(text[start - 1])) start--;
        return text[start..(idx + 1)];
    }

    /// <summary>
    /// 猜安装器会写入的版本目录名（如 1.20.1-forge-47.2.0）。
    /// 猜不到就返回空（失败时不清理，但不影响主流程）。
    /// </summary>
    private static string GuessForgeVersionId(string mcVersion, string installerUrl)
    {
        // 安装器文件名通常是 forge-1.20.1-47.2.0-installer.jar / neoforge-…-installer.jar
        var file = Path.GetFileNameWithoutExtension(new Uri(installerUrl).LocalPath);
        foreach (var marker in new[] { "-installer", "installer" })
        {
            var i = file.LastIndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (i <= 0) continue;
            var stem = file[..i].Trim('-', '_');
            // stem 形如 forge-1.20.1-47.2.0 → 去掉 loader 前缀
            foreach (var prefix in new[] { "forge-", "neoforge-", "quilt-", "fabric-" })
            {
                if (stem.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    stem = stem[prefix.Length..];
                    break;
                }
            }
            if (stem.Length > 0) return $"{mcVersion}-{stem}";
        }
        return "";
    }

    /// <summary>
    /// 确保游戏目录里有官方启动器格式的 launcher_profiles.json（Forge/NeoForge 安装器的前置条件）。
    /// 已存在时不动；返回是否新建。内容用最小合法结构，官方启动器读得懂、空 profiles 也不影响启动。
    /// </summary>
    public static bool EnsureLauncherProfiles(string dotMinecraft)
    {
        var path = Path.Combine(dotMinecraft, "launcher_profiles.json");
        if (File.Exists(path)) return false;
        Directory.CreateDirectory(dotMinecraft);
        const string json = """
            {
              "profiles": {},
              "settings": {
                "enableSnapshots": false,
                "keepLauncherOpen": false,
                "showGameLog": false,
                "soundOn": false
              },
              "version": 3
            }
            """;
        // Forge 用 Gson 解析：带 BOM 会解析失败，必须 UTF-8 无 BOM
        File.WriteAllText(path, json, new System.Text.UTF8Encoding(false));
        LauncherLog.Write("install", "已创建 launcher_profiles.json（Forge 安装器前置条件）");
        return true;
    }

    private async Task<string> EnsureJavaAsync(int requiredMajor,
        IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var runtimes = await _java.ListRuntimesAsync(ct);
        // 先精确匹配：Forge/NeoForge 对 Java 大版本敏感——拿 25 跑要求 21 的 Forge，
        // 会在启动早期（Mixin 初始化后）无声卡死。规则是「主版本必须一致」，没有就下载。
        var exact = runtimes.Where(r => r.MajorVersion == requiredMajor)
            .OrderByDescending(r => r.Is64Bit).FirstOrDefault();
        if (exact is not null) return exact.Executable;

        Report(progress, $"未找到 Java {requiredMajor}，正在自动下载运行时…");
        return await _java.DownloadRuntimeAsync(requiredMajor, progress, ct);
    }

    private static void Report(IProgress<BatchProgress>? progress, string stage) =>
        progress?.Report(new BatchProgress(stage, 0, 0, 0, 0, 0, null));
}
