// packwiz 整合包安装：mod 不在 zip 里，由 index.toml 列出各 *.pw.toml 元数据，
// 元数据里的 [download].url 指向真实 jar。这里按索引把 mod 下载进实例对应目录，
// 让「导入整合包」一次到位，不必再进游戏由 packwiz 拉取。
//
// 支持范围（够覆盖主流导出）：
//   · index.toml 的 [[files]] 逐行形式与 files = [ {file = "..."} ] 内联形式
//   · pack.toml 未带 index.toml 时直接扫包内 *.pw.toml
//   · pw.toml 的 [download].url / filename / side，以及 [[option]] 可选组（默认不启用则跳过）
// 不支持：packwiz 的 metafile 校验（hash）与自建索引服务器——失败项会回报给调用方。
using System.IO.Compression;
using System.Text.RegularExpressions;

namespace Starcloud.Core.Services;

public static class PackwizInstaller
{
    public sealed record Result(int Downloaded, int Skipped, int Failed, IReadOnlyList<string> Errors);

    public static async Task<Result> InstallModsAsync(
        string packPath, string? contentRoot, string installRoot,
        DownloadService downloader, IProgress<BatchProgress>? progress, CancellationToken ct)
    {
        var root = string.IsNullOrEmpty(contentRoot) ? "" : contentRoot.TrimEnd('/') + "/";
        var skipped = 0;
        var failed = 0;
        var errors = new List<string>();

        using var zip = PackZip.Open(packPath);

        var indexText = ReadEntry(zip, root + "index.toml") ?? "";
        var metaPaths = PackwizToml.IndexFiles(indexText);
        if (metaPaths.Count == 0)
        {
            // 没 index：扫包内所有 *.pw.toml（很多导出工具只打元数据）
            metaPaths = zip.Entries
                .Select(e => e.FullName.Replace('\\', '/'))
                .Where(n => n.StartsWith(root, StringComparison.OrdinalIgnoreCase)
                            && n.EndsWith(".pw.toml", StringComparison.OrdinalIgnoreCase))
                .Select(n => n[root.Length..])
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        var jobs = new List<(string Url, string Dest)>();
        foreach (var rel in metaPaths)
        {
            ct.ThrowIfCancellationRequested();
            var text = ReadEntry(zip, root + rel);
            if (text is null)
            {
                failed++;
                if (errors.Count < 10) errors.Add(LocPack($"缺少元数据：{rel}"));
                continue;
            }

            var meta = PackwizToml.ParseMetafile(text);
            if (meta.Optional) { skipped++; continue; }                              // 可选组且默认未启用
            if (string.Equals(meta.Side, "server", StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
            if (string.IsNullOrWhiteSpace(meta.Url)) { skipped++; continue; }        // 由游戏内其他机制提供

            var target = PackwizToml.TargetPath(rel, meta.FileName, meta.Url);
            var dest = PackPathGuard.Resolve(installRoot, target, sanitize: true);
            if (dest is null) { skipped++; continue; }
            jobs.Add((meta.Url!, dest));
        }

        var total = jobs.Count;
        var done = 0;
        var processed = 0;
        foreach (var (url, dest) in jobs)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                await downloader.DownloadFileAsync(url, dest, null, 2, ct);
                done++;
            }
            catch (Exception ex)
            {
                failed++;
                if (errors.Count < 10) errors.Add($"{Path.GetFileName(dest)}：{ex.Message}");
            }
            processed++;
            progress?.Report(new BatchProgress("packwiz 模组", processed, total, 0, 0, 0, null));
        }

        if (skipped > 0 || failed > 0)
            LauncherLog.Write("pack",
                $"packwiz：下载 {done} 个 mod，跳过 {skipped}，失败 {failed}（共 {total} 个索引项）");
        return new Result(done, skipped, failed, errors);
    }

    private static string? ReadEntry(ZipArchive zip, string name)
    {
        var entry = zip.Entries.FirstOrDefault(e =>
            e.FullName.Replace('\\', '/').Equals(name, StringComparison.OrdinalIgnoreCase));
        if (entry is null) return null;
        try
        {
            using var stream = entry.Open();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch
        {
            return null;
        }
    }

    private static string LocPack(string s) => s;
}

/// <summary>packwiz 用到的 TOML 子集解析（定向提取，不引入完整 TOML 依赖）</summary>
internal static class PackwizToml
{
    private static readonly Regex FileKey =
        new("file\\s*=\\s*\"([^\"]+\\.pw\\.toml)\"", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>index.toml 里的元数据相对路径（两种写法都覆盖）</summary>
    public static List<string> IndexFiles(string text) =>
        string.IsNullOrEmpty(text)
            ? []
            : FileKey.Matches(text)
                .Select(m => m.Groups[1].Value.Replace('\\', '/'))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

    public sealed record Meta(string? Url, string? FileName, string? Side, bool Optional);

    /// <summary>解析单个 *.pw.toml：取 [download].url、filename、side、可选组默认值</summary>
    public static Meta ParseMetafile(string text)
    {
        string? url = null, fileName = null, side = null;
        var section = "";
        var hasOption = false;
        var optionDefault = false;

        foreach (var raw in text.Split('\n'))
        {
            var line = StripComment(raw).Trim();
            if (line.Length == 0) continue;

            if (line.StartsWith("[[", StringComparison.Ordinal) && line.EndsWith("]]", StringComparison.Ordinal))
            {
                section = line.Trim('[', ']').Trim();
                if (section.Equals("option", StringComparison.OrdinalIgnoreCase)) hasOption = true;
                continue;
            }
            if (line.StartsWith("[", StringComparison.Ordinal) && line.EndsWith("]", StringComparison.Ordinal))
            {
                section = line.Trim('[', ']').Trim();
                continue;
            }

            var (key, value) = SplitKeyValue(line);
            if (key is null) continue;
            switch (key)
            {
                case "url" when section.Equals("download", StringComparison.OrdinalIgnoreCase):
                    url = value;
                    break;
                case "filename":
                    fileName = value;
                    break;
                case "side":
                    side = value;
                    break;
                case "default":
                    optionDefault = value.Equals("true", StringComparison.OrdinalIgnoreCase);
                    break;
            }
        }

        // 有 [[option]] 可选组且默认没启用：不装（与 packwiz 的默认语义一致）
        return new Meta(url, fileName, side, hasOption && !optionDefault);
    }

    /// <summary>
    /// 目标相对路径：元数据所在目录 + jar 文件名。
    /// 文件名优先 filename 字段，其次 URL 最后一段（去 query），最后回退元数据同名 .jar。
    /// </summary>
    public static string TargetPath(string metaRel, string? fileName, string? url)
    {
        var slash = metaRel.LastIndexOf('/');
        var dir = slash < 0 ? "" : metaRel[..slash];

        var name = fileName?.Trim();
        if (string.IsNullOrEmpty(name) && !string.IsNullOrWhiteSpace(url))
        {
            var path = url.Split('?')[0].Split('#')[0].TrimEnd('/');
            name = path[(path.LastIndexOf('/') + 1)..];
        }
        if (string.IsNullOrEmpty(name))
        {
            var leaf = slash < 0 ? metaRel : metaRel[(slash + 1)..];
            name = Path.GetFileNameWithoutExtension(leaf) + ".jar";
        }
        return dir.Length == 0 ? name : dir + "/" + name;
    }

    private static string StripComment(string line)
    {
        var inQuote = false;
        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];
            if (c == '"') inQuote = !inQuote;
            else if (c == '#' && !inQuote) return line[..i];
        }
        return line;
    }

    private static (string? Key, string Value) SplitKeyValue(string line)
    {
        var eq = line.IndexOf('=');
        if (eq <= 0) return (null, "");
        var key = line[..eq].Trim().Trim('"');
        var value = line[(eq + 1)..].Trim();
        if (value.Length >= 2 && (value[0] == '"' || value[0] == '\'') && value[^1] == value[0])
            value = value[1..^1];
        return (key, value);
    }
}
