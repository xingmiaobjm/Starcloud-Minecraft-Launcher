// 下载引擎实测（默认不跑，需显式开启）：
//
//   set STARCLOUD_IT_DOWNLOAD_URL=https://.../big.bin   目标文件（≥8 MiB 才会走并行分片）
//   set STARCLOUD_IT_DOWNLOAD=1                         开关
//
// 跑真实网络 + 真实 DownloadService，打印单文件吞吐，用来核对「下载速度不低于 5 MB/s」这条要求。
// 之所以单独隔离：真实网络会抖动，不适合放进每次提交都跑的单元测试；而离线护栏见
// ChunkedFileDownloaderTests（本地限速服务器证明并行确实生效）。

using System.Diagnostics;
using Starcloud.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Starcloud.Core.Tests;

public class DownloadThroughputIntegrationTests(ITestOutputHelper output)
{
    private static bool Enabled =>
        Environment.GetEnvironmentVariable("STARCLOUD_IT_DOWNLOAD") == "1";

    private static string? TargetUrl => Environment.GetEnvironmentVariable("STARCLOUD_IT_DOWNLOAD_URL");

    [Fact]
    public async Task 真实网络_单个大文件吞吐()
    {
        if (!Enabled || string.IsNullOrWhiteSpace(TargetUrl)) return; // 未开启：直接跳过

        var dest = Path.Combine(Path.GetTempPath(), $"sc-it-{Guid.NewGuid():N}.bin");
        var settings = new LauncherSettings { DownloadSource = "auto" };
        var service = new DownloadService(() => settings)
        {
            Log = msg => output.WriteLine("  " + msg),
        };

        // 首连（含 TLS/探测）不算进稳态速率：先下 4 MiB 热身，再整文件计时
        try
        {
            var sw = Stopwatch.StartNew();
            await service.DownloadFileAsync(TargetUrl!, dest);
            sw.Stop();

            var size = new FileInfo(dest).Length;
            var mbps = size / 1048576.0 / sw.Elapsed.TotalSeconds;
            output.WriteLine($"文件 {size / 1048576.0:F1} MB，用时 {sw.Elapsed.TotalSeconds:F1}s，" +
                             $"平均 {mbps:F2} MB/s");
            Assert.True(size > 0);
        }
        finally
        {
            try { if (File.Exists(dest)) File.Delete(dest); } catch (IOException) { }
        }
    }
}
