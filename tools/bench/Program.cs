// 下载引擎 A/B 实测工具：只官方源 vs 自动多源竞速，跑真实 DownloadService。
//
// 用途：给发布文案提供可复现的数字（速度提升 / 重试减少 / 稳定度提升）。
// 用法：
//   dotnet run --project tools/bench -c Release -- [--version 1.20.1] [--runs 3] [--max-files 40]
// 说明：
//   · 每组都用全新的临时目录下载，避免"文件已存在就跳过"
//   · 每轮之间不清全局缓存，但下载目标是随机临时目录，不会命中上一轮
//   · 重试次数从引擎日志里统计（日志含"第 N 次尝试"）

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Starcloud.Core.Services;

namespace Starcloud.Bench;

internal static class Program
{
    private sealed record Target(string Url, string? Sha1, long Size);

    private static async Task<int> Main(string[] args)
    {
        // 加载器构建列表耗时：--loaders <mcVersion> 分别打四家耗时（安装页抽屉卡顿定位）
        var loadersMc = Arg(args, "--loaders");
        if (loadersMc is not null)
        {
            var dotMc2 = Path.Combine(Path.GetTempPath(), "sc-loaders-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dotMc2);
            var settings2 = new LauncherSettings { DotMinecraft = dotMc2, DownloadSource = "auto" };
            var download2 = new DownloadService(() => settings2);
            var installer = new InstallerService(download2);
            var forge = new ForgeInstallerService(download2, new JavaService(download2));
            var fabric = new FabricService(download2);

            async Task TimeIt(string label, Func<Task<int>> work)
            {
                var sw = Stopwatch.StartNew();
                try
                {
                    var n = await work();
                    Console.WriteLine($"{label,-10} {sw.ElapsedMilliseconds,6} ms  条目 {n}");
                }
                catch (Exception ex)
                {
                    Console.WriteLine($"{label,-10} {sw.ElapsedMilliseconds,6} ms  失败 {ex.GetType().Name}: {ex.Message}");
                }
            }

            await TimeIt("Forge", async () => (await forge.ListForgeBuildsAsync(loadersMc)).Count);
            await TimeIt("NeoForge", async () => (await forge.ListNeoForgeBuildsAsync(loadersMc)).Count);
            await TimeIt("Fabric", async () => (await fabric.ListLoadersAsync(loadersMc)).Count);
            await TimeIt("Quilt", async () => (await fabric.ListQuiltLoadersAsync(loadersMc)).Count);
            try { Directory.Delete(dotMc2, true); } catch { }
            return 0;
        }

        // 单文件排障：--fetch <url> 直接跑一次真实下载，打印引擎日志（含失败原因）
        var fetch = Arg(args, "--fetch");
        if (fetch is not null)
        {
            var dotMc = Path.Combine(Path.GetTempPath(), "sc-fetch-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(dotMc);
            var settings = new LauncherSettings { DotMinecraft = dotMc, DownloadSource = "auto" };
            var service = new DownloadService(() => settings) { Log = Console.WriteLine };
            var dest = Path.Combine(dotMc, Path.GetFileName(new Uri(fetch).LocalPath));
            try
            {
                await service.DownloadFileAsync(fetch, dest, null, ct: default);
                Console.WriteLine($"OK {new FileInfo(dest).Length} bytes -> {dest}");
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"FAIL {ex.GetType().Name}: {ex.Message}");
                return 1;
            }
            finally { try { Directory.Delete(dotMc, true); } catch { } }
        }

        var version = Arg(args, "--version") ?? "1.20.1";
        var runs = int.TryParse(Arg(args, "--runs"), out var r) ? Math.Max(1, r) : 3;
        var maxFiles = int.TryParse(Arg(args, "--max-files"), out var m) ? Math.Max(1, m) : 40;
        Verbose = args.Contains("--verbose");

        Console.WriteLine($"== Starcloud 下载引擎 A/B 实测 ==");
        Console.WriteLine($"版本 {version}，每种模式 {runs} 轮，最多 {maxFiles} 个文件");
        Console.WriteLine();

        var targets = await LoadTargetsAsync(version, maxFiles);
        var totalMb = targets.Sum(t => t.Size) / 1024.0 / 1024;
        Console.WriteLine($"目标文件 {targets.Count} 个，共 {totalMb:F1} MB");
        Console.WriteLine();

        var official = new List<RunResult>();
        var auto = new List<RunResult>();

        for (var i = 1; i <= runs; i++)
        {
            official.Add(await RunOnceAsync("official", "仅官方源", targets, i));
            auto.Add(await RunOnceAsync("auto", "自动（五源竞速）", targets, i));
        }

        PrintReport(official, auto);
        return 0;
    }

    private sealed record RunResult(double Seconds, int Failures, int Retries, int Ok, long Bytes);

    private static bool Verbose;

    private static async Task<RunResult> RunOnceAsync(
        string source, string label, IReadOnlyList<Target> targets, int round)
    {
        var dotMc = Path.Combine(Path.GetTempPath(), "sc-bench-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dotMc);

        var settings = new LauncherSettings
        {
            DotMinecraft = dotMc,
            DownloadSource = source,
            DownloadMaxRetries = 3,
            DownloadConcurrency = 8,
        };
        var service = new DownloadService(() => settings);
        var logs = new ConcurrentQueue<string>();
        service.Log = s => logs.Enqueue(s);

        var failures = 0;
        var ok = 0;
        long bytes = 0;
        var sw = Stopwatch.StartNew();

        // 走真实批量路径：只有 DownloadBatchAsync 才有「多文件分摊到多个源」的逻辑，
        // 自己并发调 DownloadFileAsync 会绕过分摊，测出来的是"每个文件各自抢最快源"。
        var files = targets
            .Select((t, idx) => (t.Url, Dest: Path.Combine(dotMc, $"f{idx:D3}.bin"), t.Sha1, t.Size))
            .ToList();
        try
        {
            await service.DownloadBatchAsync(files, "bench", settings.DownloadConcurrency,
                settings.DownloadMaxRetries, null, default);
        }
        catch (Exception ex)
        {
            failures++;
            logs.Enqueue($"[BENCH-FAIL] 批量失败：{ex.Message}");
        }
        finally
        {
            sw.Stop();
            foreach (var f in files)
            {
                if (!File.Exists(f.Dest)) continue;
                ok++;
                bytes += new FileInfo(f.Dest).Length;
            }
            failures = Math.Max(failures, files.Count - ok);
            try { Directory.Delete(dotMc, recursive: true); } catch { }
        }

        // 引擎日志里的重试痕迹（每次失败会记一行「第 N 次尝试」）
        var retries = logs.Count(l => l.Contains("第 ") && l.Contains(" 次尝试"));
        // 各源实际承担量：日志里每片会打「← <源名>」，统计它就知道多源有没有真的并行出力
        var perSource = logs
            .Where(l => l.Contains("← "))
            .Select(l => l[(l.LastIndexOf("← ", StringComparison.Ordinal) + 2)..].Trim())
            .GroupBy(s => s)
            .OrderByDescending(g => g.Count())
            .Take(4)
            .Select(g => $"{g.Key} {g.Count()}")
            .ToList();
        if (Verbose)
        {
            foreach (var line in logs) Console.WriteLine($"    · {line}");
        }
        // 关键事件单独统计，定位"慢在哪一步"
        var precheck = logs.Count(l => l.Contains("源预检"));
        var fallback = logs.Count(l => l.Contains("退化为整文件"));
        var result = new RunResult(sw.Elapsed.TotalSeconds, failures, retries, ok, bytes);
        Console.WriteLine($"[{label}] 第 {round} 轮：{result.Seconds:F1}s，" +
                          $"成功 {ok}/{targets.Count}，失败 {failures}，重试 {retries}，" +
                          $"{bytes / 1024.0 / 1024:F1} MB" +
                          (precheck > 0 || fallback > 0 ? $"（预检 {precheck}，退化整文件 {fallback}）" : ""));
        if (perSource.Any())
            Console.WriteLine($"    源使用分布：{string.Join(" / ", perSource)}");
        return result;
    }

    private static void PrintReport(List<RunResult> official, List<RunResult> auto)
    {
        static double Median(IEnumerable<double> xs)
        {
            var a = xs.OrderBy(x => x).ToArray();
            if (a.Length == 0) return 0;
            return a.Length % 2 == 1 ? a[a.Length / 2] : (a[a.Length / 2 - 1] + a[a.Length / 2]) / 2;
        }

        var oSec = Median(official.Select(x => x.Seconds));
        var aSec = Median(auto.Select(x => x.Seconds));
        var oRetry = Median(official.Select(x => (double)x.Retries));
        var aRetry = Median(auto.Select(x => (double)x.Retries));
        var oOk = Median(official.Select(x => (double)x.Ok));
        var aOk = Median(auto.Select(x => (double)x.Ok));

        Console.WriteLine();
        Console.WriteLine("== 结果（中位数）==");
        Console.WriteLine($"仅官方源       ：{oSec:F1}s，重试 {oRetry:F0} 次，成功 {oOk:F0} 个");
        Console.WriteLine($"自动五源竞速   ：{aSec:F1}s，重试 {aRetry:F0} 次，成功 {aOk:F0} 个");
        Console.WriteLine();

        if (oSec > 0)
            Console.WriteLine($"下载速度最高提升：{(oSec - aSec) / oSec * 100:F0}%");
        if (oRetry > 0)
            Console.WriteLine($"失败重试最多减少：{(oRetry - aRetry) / oRetry * 100:F0}%");
        else
            Console.WriteLine("失败重试最多减少：本次官方源零重试，无法计算（可提高 --max-files 增加样本）");
    }

    /// <summary>从官方清单取一个版本的文件（库 + client jar），按大小降序取前 N 个（大文件更能体现差异）。</summary>
    private static async Task<IReadOnlyList<Target>> LoadTargetsAsync(string version, int maxFiles)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Starcloud-Bench");

        var manifestJson = await http.GetStringAsync(
            "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json");
        using var manifest = JsonDocument.Parse(manifestJson);
        var entry = manifest.RootElement.GetProperty("versions").EnumerateArray()
            .FirstOrDefault(v => v.GetProperty("id").GetString() == version);
        if (entry.ValueKind == JsonValueKind.Undefined)
            throw new InvalidOperationException($"官方清单里没有 {version}");

        var versionUrl = entry.GetProperty("url").GetString()!;
        var versionJson = await http.GetStringAsync(versionUrl);
        using var doc = JsonDocument.Parse(versionJson);
        var root = doc.RootElement;

        var all = new List<Target>();
        if (root.TryGetProperty("downloads", out var downloads) &&
            downloads.TryGetProperty("client", out var client) &&
            client.TryGetProperty("url", out var clientUrl))
        {
            all.Add(new Target(
                clientUrl.GetString()!,
                client.TryGetProperty("sha1", out var cs) ? cs.GetString() : null,
                client.TryGetProperty("size", out var csize) ? csize.GetInt64() : 0));
        }

        if (root.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
        {
            foreach (var lib in libs.EnumerateArray())
            {
                if (!lib.TryGetProperty("downloads", out var d)) continue;
                if (!d.TryGetProperty("artifact", out var art)) continue;
                if (!art.TryGetProperty("url", out var u)) continue;
                all.Add(new Target(
                    u.GetString()!,
                    art.TryGetProperty("sha1", out var s) ? s.GetString() : null,
                    art.TryGetProperty("size", out var sz) ? sz.GetInt64() : 0));
            }
        }

        return all.OrderByDescending(t => t.Size).Take(maxFiles).ToList();
    }

    private static string? Arg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == name) return args[i + 1];
        return null;
    }
}
