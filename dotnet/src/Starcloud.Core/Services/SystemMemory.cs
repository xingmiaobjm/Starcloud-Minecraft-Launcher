// 物理内存快照与「自动配置」上限计算（供全局/版本设置内存档位使用）
// Windows：GlobalMemoryStatusEx；Linux：/proc/meminfo；macOS：sysctl hw.memsize（可用量无简单
// 接口，UsedMb 返回 0 —— 自动上限只依赖总量，已用量仅 Windows 详情页展示）。

using System.Runtime.InteropServices;

namespace Starcloud.Core.Services;

/// <summary>物理内存快照与自动分配上限。</summary>
public static class SystemMemory
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MemoryStatusEx
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx buffer);

    /// <summary>返回 (总内存 MB, 已用内存 MB)；获取失败回退 (8192, 4096)。</summary>
    public static (long TotalMb, long UsedMb) Snapshot()
    {
        if (OperatingSystem.IsWindows()) return SnapshotWindows();
        if (OperatingSystem.IsLinux()) return SnapshotLinux();
        if (OperatingSystem.IsMacOS()) return SnapshotMacOS();
        return (8192, 4096);
    }

    private static (long TotalMb, long UsedMb) SnapshotWindows()
    {
        var s = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref s)) return (8192, 4096);
        var total = (long)(s.ullTotalPhys / 1024 / 1024);
        var avail = (long)(s.ullAvailPhys / 1024 / 1024);
        return (total, total - avail);
    }

    /// <summary>Linux：/proc/meminfo（kB 单位）；MemAvailable 缺失时已用量按 0 处理</summary>
    private static (long TotalMb, long UsedMb) SnapshotLinux()
    {
        try
        {
            long totalKb = 0, availKb = 0;
            foreach (var line in File.ReadLines("/proc/meminfo"))
            {
                if (line.StartsWith("MemTotal:", StringComparison.Ordinal)) totalKb = ParseKb(line);
                else if (line.StartsWith("MemAvailable:", StringComparison.Ordinal)) availKb = ParseKb(line);
            }
            if (totalKb <= 0) return (8192, 4096);
            var total = totalKb / 1024;
            return (total, Math.Max(0, total - availKb / 1024));
        }
        catch
        {
            return (8192, 4096);
        }
    }

    private static long ParseKb(string line)
    {
        var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 2 && long.TryParse(parts[1], out var kb) ? kb : 0;
    }

    /// <summary>macOS：sysctl -n hw.memsize（字节）；已用量返回 0（见文件头注释）</summary>
    private static (long TotalMb, long UsedMb) SnapshotMacOS()
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "sysctl",
                Arguments = "-n hw.memsize",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = System.Diagnostics.Process.Start(psi)!;
            var output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(2000);
            return long.TryParse(output, out var bytes) && bytes > 0
                ? ((long)(bytes / 1024 / 1024), 0)
                : (8192, 4096);
        }
        catch
        {
            return (8192, 4096);
        }
    }

    /// <summary>自动上限：委托 MemoryAdvisor（物理内存基线，512 对齐，下限 2048 MB）。</summary>
    public static int AutoMaxMb() => MemoryAdvisor.SuggestMaxMb(Snapshot().TotalMb, 0, false);
}
