// 整合包安装后的联动：缺失的原版 + 加载器（fabric-loader / forge / neoforge）
// 移植自桌面 Starcloud.App.Views.Shared.MrPackFlow（ModPackDialogs 依赖）；
// 桌面与安卓共用 Starcloud.Core 的安装器 API，此处仅命名空间映射。

using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Core.Services;

namespace Starcloud.Launcher.Uno.Views.Shared;

internal static class MrPackFlow
{
    /// <summary>按整合包依赖装原版与加载器；返回中文摘要</summary>
    public static async Task<string> EnsureGameAndLoaderAsync(
        MrPackInstallResult result, AppServices s, IProgress<BatchProgress>? progress)
    {
        var game = result.GameVersion;
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(game))
        {
            if (!s.Installer.IsInstalled(s.Settings.DotMinecraft, game))
            {
                var entry = (await s.Manifest.GetManifestAsync(false)).Versions
                    .FirstOrDefault(v => v.Id == game);
                if (entry is not null)
                    await s.Installer.InstallVanillaAsync(game, entry.Url, s.Settings, progress);
            }
            parts.Add($"Minecraft {game}");

            var javaMajor = 8;
            try
            {
                var v = await s.Installer.LoadVersionAsync(s.Settings.DotMinecraft, game);
                javaMajor = v.JavaVersion?.MajorVersion ?? 8;
            }
            catch { /* 读取失败按 Java 8 兜底，安装器会自动补齐运行时 */ }

            if (result.Dependencies.TryGetValue("fabric-loader", out var fab))
            {
                await s.Installer.InstallFabricAsync(game, fab, s.Settings, progress);
                parts.Add($"Fabric {fab}");
            }
            if (result.Dependencies.TryGetValue("quilt-loader", out var quilt))
            {
                await s.Installer.InstallQuiltAsync(game, quilt, s.Settings, progress);
                parts.Add($"Quilt {quilt}");
            }
            if (result.Dependencies.TryGetValue("forge", out var forge))
            {
                await s.Forge.InstallViaInstallerAsync(
                    ForgeInstallerService.ForgeInstallerUrl(game, forge), game, javaMajor, s.Settings, progress);
                parts.Add($"Forge {forge}");
            }
            if (result.Dependencies.TryGetValue("neoforge", out var neo))
            {
                await s.Forge.InstallViaInstallerAsync(
                    ForgeInstallerService.NeoForgeInstallerUrl(game, neo), game, javaMajor, s.Settings, progress);
                parts.Add($"NeoForge {neo}");
            }
        }

        return parts.Count > 0
            ? Loc.T("已联动安装 ") + string.Join(" + ", parts)
            : Loc.T("共下载 {0} 个文件", result.DownloadedFiles);
    }
}
