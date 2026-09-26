// 版本号过滤：**实例名 ≠ Minecraft 版本号**。
//
// Modrinth / CurseForge 的筛选参数只认官方版本 id（1.21.1 / 24w14a / 1.21-pre1…）。
// 把「Create Aero：New Horizons」这种实例名塞进 facets，服务端直接回 400，
// 而且报错是 HttpRequestException（连异常类型都不提版本），排查起来完全对不上号。
// 所以发请求前统一过这一层：不像版本号就当没有筛选——宁可放宽，也不要 400。

using System.Text.RegularExpressions;

namespace Starcloud.Core.Services;

public static partial class GameVersionFilter
{
    /// <summary>正式版 / 预发布：1.21、1.21.1、1.21.1-pre2、1.20.5-rc1</summary>
    [GeneratedRegex(@"^\d+(\.\d+)*(-[A-Za-z0-9._]+)*$")]
    private static partial Regex ReleasePattern();

    /// <summary>快照：24w14a、1.21.2-pre1 由上面那条覆盖</summary>
    [GeneratedRegex(@"^\d{2}w\d{2}[a-z]$")]
    private static partial Regex SnapshotPattern();

    /// <summary>是不是一个 Minecraft 版本号（中文实例名、空串、纯名字一律 false）</summary>
    public static bool IsValid(string? value)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v)) return false;
        return ReleasePattern().IsMatch(v) || SnapshotPattern().IsMatch(v);
    }

    /// <summary>合法就返回（去掉首尾空白），否则返回 null：调用方据此省略筛选参数</summary>
    public static string? OrNull(string? value) => IsValid(value) ? value!.Trim() : null;
}
