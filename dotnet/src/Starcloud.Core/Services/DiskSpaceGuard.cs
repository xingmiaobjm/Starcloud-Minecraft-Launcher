// 安装前的磁盘空间预检：下载中途磁盘满是最常见的失败原因之一，
// 而且失败得很晚（下载到一半才报），用户已经等了很久。
//
// 原来只有「环境体检」这个独立工具会查磁盘（而且阈值是固定的 512 MB / 2 GB），
// 安装与导入流程从不调用。这里做成可复用的静态工具：按「预计需要的字节数」估空间。

namespace Starcloud.Core.Services;

public static class DiskSpaceGuard
{
    /// <summary>安装过程本身的额外开销（解包、临时文件、下载中的 .sc-tmp 与 .parts 副本）。</summary>
    private const double HeadroomFactor = 1.6;

    /// <summary>最小余量（MB）：低于这个值就别开始了。</summary>
    private const long MinFreeMb = 512;

    /// <summary>
    /// 检查目标目录所在盘的剩余空间是否够用。
    /// <para>预计占用 = 下载量 × 1.6（解包与临时文件余量），
    /// 而不是简单地拿下载量比 —— 只比下载量会在解包阶段才失败。</para>
    /// </summary>
    /// <param name="targetDir">安装目标目录</param>
    /// <param name="expectedBytes">预计要写入的字节数</param>
    /// <param name="freeBytes">实际剩余字节数</param>
    /// <param name="message">空间不足时的提示（成功时为 null）</param>
    /// <returns>true = 空间足够（或探测不出剩余空间时按通过处理）</returns>
    public static bool Check(string targetDir, long expectedBytes,
        out long freeBytes, out string? message)
    {
        freeBytes = -1;
        message = null;

        long free;
        try
        {
            var root = Path.GetPathRoot(Path.GetFullPath(targetDir));
            if (string.IsNullOrEmpty(root)) return true;      // 探测不出盘符：不拦
            var drive = new DriveInfo(root);
            if (!drive.IsReady) return true;                  // 盘不可读（比如未挂载的网络盘）：不拦
            free = drive.AvailableFreeSpace;
        }
        catch
        {
            return true;   // 探测失败不该阻断安装
        }

        freeBytes = free;
        var need = (long)(Math.Max(0, expectedBytes) * HeadroomFactor);
        if (free >= need) return true;

        var freeMb = free / 1024 / 1024;
        var needMb = need / 1024 / 1024;
        message = $"目标磁盘剩余 {freeMb} MB，本次安装预计需要 {needMb} MB（含解包与临时文件余量）。请先清理磁盘空间再试。";
        return false;
    }

    /// <summary>供 UI 直接使用的简短结论：空间是否足够。</summary>
    public static bool Enough(string targetDir, long expectedBytes) =>
        Check(targetDir, expectedBytes, out _, out _);
}
