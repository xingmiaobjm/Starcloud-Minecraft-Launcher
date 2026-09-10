// 物理内存快照与「自动配置」上限计算（供全局/版本设置内存档位使用）

using System.Runtime.InteropServices;

namespace Starcloud.Core.Services;

/// <summary>物理内存快照（GlobalMemoryStatusEx）与自动分配上限。</summary>
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

    /// <summary>返回 (总内存 MB, 已用内存 MB)；调用失败回退 (8192, 4096)。</summary>
    public static (long TotalMb, long UsedMb) Snapshot()
    {
        var s = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
        if (!GlobalMemoryStatusEx(ref s)) return (8192, 4096);
        var total = (long)(s.ullTotalPhys / 1024 / 1024);
        var avail = (long)(s.ullAvailPhys / 1024 / 1024);
        return (total, total - avail);
    }

    /// <summary>自动上限：clamp(物理/4, 2048, 物理*3/4)，向下取整到 512 倍数（MB）。</summary>
    public static int AutoMaxMb()
    {
        var total = Snapshot().TotalMb;
        var raw = Math.Clamp(total / 4, 2048, total * 3 / 4);
        return (int)(raw / 512 * 512);
    }
}
