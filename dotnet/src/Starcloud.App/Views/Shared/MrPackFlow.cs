// 整合包安装后的联动：缺失的原版 + 加载器（fabric-loader / forge / neoforge）
//
// 这些版本是「依赖」而非用户要玩的版本：本次新装的统一打上依赖标记，
// 版本列表里就不会再为一次导入凭空多出两三个条目（见 VersionVisibility）。

using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views.Shared;

internal static class MrPackFlow
{
    /// <summary>按整合包依赖装原版与加载器；返回中文摘要</summary>
    public static async Task<string> EnsureGameAndLoaderAsync(
        MrPackInstallResult result, AppServices s, IProgress<BatchProgress>? progress)
    {
        var game = result.GameVersion;
        var parts = new List<string>();
        var dotMc = s.Settings.DotMinecraft;
        if (!string.IsNullOrEmpty(game))
        {
            if (!s.Installer.IsInstalled(dotMc, game))
            {
                var entry = (await s.Manifest.GetManifestAsync(false)).Versions
                    .FirstOrDefault(v => v.Id == game);
                if (entry is not null)
                    await s.Installer.InstallVanillaAsync(game, entry.Url, s.Settings, progress);
                // 本次为整合包新装的原版：标记为依赖，列表里不单独占一条
                VersionVisibility.MarkDependency(dotMc, game);
            }
            parts.Add($"Minecraft {game}");

            var javaMajor = 8;
            try
            {
                var v = await s.Installer.LoadVersionAsync(dotMc, game);
                javaMajor = JavaRequirement.Resolve(game, v.JavaVersion);
            }
            catch { /* 读取失败按 Java 8 兜底，安装器会自动补齐运行时 */ }

            if (result.Dependencies.TryGetValue("fabric-loader", out var fab))
            {
                var id = $"fabric-loader-{fab}-{game}";
                var existed = s.Installer.IsInstalled(dotMc, id);
                await s.Installer.InstallFabricAsync(game, fab, s.Settings, progress);
                if (!existed) VersionVisibility.MarkDependency(dotMc, id);
                parts.Add($"Fabric {fab}");
            }
            if (result.Dependencies.TryGetValue("quilt-loader", out var quilt))
            {
                var id = $"quilt-loader-{quilt}-{game}";
                var existed = s.Installer.IsInstalled(dotMc, id);
                await s.Installer.InstallQuiltAsync(game, quilt, s.Settings, progress);
                if (!existed) VersionVisibility.MarkDependency(dotMc, id);
                parts.Add($"Quilt {quilt}");
            }
            if (result.Dependencies.TryGetValue("forge", out var forge))
            {
                var id = $"{game}-forge-{forge}";
                var existed = s.Installer.IsInstalled(dotMc, id);
                await s.Forge.InstallViaInstallerAsync(
                    ForgeInstallerService.ForgeInstallerUrl(game, forge), game, javaMajor, s.Settings, progress);
                if (!existed) VersionVisibility.MarkDependency(dotMc, id);
                parts.Add($"Forge {forge}");
            }
            if (result.Dependencies.TryGetValue("neoforge", out var neo))
            {
                var id = $"neoforge-{neo}";
                var existed = s.Installer.IsInstalled(dotMc, id);
                await s.Forge.InstallViaInstallerAsync(
                    ForgeInstallerService.NeoForgeInstallerUrl(game, neo), game, javaMajor, s.Settings, progress);
                if (!existed) VersionVisibility.MarkDependency(dotMc, id);
                parts.Add($"NeoForge {neo}");
            }
        }

        return parts.Count > 0
            ? Loc.T("已联动安装 ") + string.Join(" + ", parts)
            : Loc.T("共下载 {0} 个文件", result.DownloadedFiles);
    }
}
