// APK 资产读取助手（安卓平台特有，桌面无对应物）：
// 安卓把 Assets/ 下的文件打进 APK，路径上不是普通磁盘文件——File API 读不到，
// 必须经 Application.Context.Assets 打流。需要把资产当磁盘文件用的场合
// （皮肤纹理渲染、引擎兜底图、Core 按路径读文件），先解包到数据目录再走常规文件路径。
//
// 服务层已有两处内联写了同样的解包逻辑（AppServices.ResolvePluginHostSdk / AgentJarPath），
// UI 层这批（steve.png / 预置皮肤）统一收口到这里，不再各自复制一遍。

using System.Security.Cryptography;

namespace Starcloud.Launcher.Uno.Views.Shared;

internal static class AppAssets
{
    /// <summary>
    /// 读取 APK 资产字节；缺失或读失败返回 null（调用方走各自的兜底）。
    /// </summary>
    public static byte[]? Read(string relative)
    {
        try
        {
            using var stream = global::Android.App.Application.Context.Assets!.Open(relative);
            using var ms = new MemoryStream();
            stream.CopyTo(ms);
            return ms.ToArray();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 把 APK 资产解包成数据目录里的真实文件（已存在且内容一致时直接复用），
    /// 返回文件绝对路径；资产缺失 / 解包失败返回 null。
    /// <para>皮肤纹理渲染、Core 按路径读文件这类「要一个真路径」的场合用它。
    /// 按 SHA1 判断是否需要重写：APK 升级换了资产内容时自动刷新。</para>
    /// </summary>
    public static string? ExtractToDataDir(string relative, string targetRelative)
    {
        try
        {
            var target = Path.Combine(global::Starcloud.Core.Services.LauncherPaths.DataDir, targetRelative);
            var bytes = Read(relative);
            if (bytes is null) return File.Exists(target) ? target : null;

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (File.Exists(target))
            {
                var same = Convert.ToHexString(SHA1.HashData(File.ReadAllBytes(target)))
                    .Equals(Convert.ToHexString(SHA1.HashData(bytes)), StringComparison.Ordinal);
                if (same) return target;
            }
            File.WriteAllBytes(target, bytes);
            return target;
        }
        catch
        {
            return null;
        }
    }
}
