// 堆分级：按设备总内存推荐 -Xms/-Xmx。
// 安卓上 JVM 与系统共享物理内存，堆给太大反而触发低内存杀手（LMK）杀进程，按档位保守给。

namespace Starcloud.Android.Core.Memory;

public static class HeapSizing
{
    /// <summary>按设备总内存（MB）推荐堆大小：max = 档位上限，min = max 的一半</summary>
    public static (int MinMb, int MaxMb) Recommend(int totalMemoryMb)
    {
        var max = totalMemoryMb switch
        {
            <= 2048 => 512,
            <= 3072 => 768,
            <= 4096 => 1024,
            <= 6144 => 1536,
            <= 8192 => 2048,
            _ => 3072,
        };
        return (max / 2, max);
    }
}
