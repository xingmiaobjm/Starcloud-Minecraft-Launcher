// Nova 写操作工具实现（版本 / 加载器 / 启动 / 修复）。

using System.Text.Json;

namespace Starcloud.Core.Services;

public sealed partial class NovaToolRegistry
{
    private async Task<string> InstallMinecraftAsync(JsonElement args, CancellationToken ct)
    {
        var versionId = Require(Str(args, "version_id"), "version_id");
        var manifest = await _c.Manifest.GetManifestAsync(false, ct);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
            ?? throw new InvalidOperationException($"官方列表里没有 {versionId}，先用 list_versions 查");
        await _c.Installer.InstallVanillaAsync(versionId, entry.Url, S, null, ct);
        return $"原版 {versionId} 安装完成。";
    }

    private async Task<string> InstallLoaderAsync(JsonElement args, CancellationToken ct)
    {
        var loader = Require(Str(args, "loader"), "loader");
        var mc = Require(Str(args, "mc_version"), "mc_version");
        var build = Str(args, "build");
        var s = S;

        switch (loader)
        {
            case "fabric" or "quilt":
            {
                build ??= await PickLoaderBuildAsync(mc, loader, ct);
                if (loader == "fabric")
                {
                    await _c.Installer.InstallFabricAsync(mc, build, s, null, ct);
                    return $"Fabric {build} @ {mc} 安装完成。";
                }
                await _c.Installer.InstallQuiltAsync(mc, build, s, null, ct);
                return $"Quilt {build} @ {mc} 安装完成。";
            }
            case "forge" or "neoforge":
            {
                if (_c.Forge is null) throw new InvalidOperationException("当前宿主没有 Forge 安装器");
                // Forge/NeoForge 官方安装器要求原版已安装 + 匹配的 Java，缺啥补啥
                if (!_c.Installer.IsInstalled(s.DotMinecraft, mc))
                    await InstallMinecraftAsync(
                        JsonDocument.Parse($$"""{"version_id":"{{mc}}"}""").RootElement, ct);

                build ??= await PickForgeBuildAsync(mc, loader, ct);
                var javaMajor = await RequireJavaMajorAsync(mc, ct);
                var url = loader == "forge"
                    ? ForgeInstallerService.ForgeInstallerUrl(mc, build)
                    : ForgeInstallerService.NeoForgeInstallerUrl(mc, build);
                await _c.Forge.InstallViaInstallerAsync(url, mc, javaMajor, s, null, ct);
                return $"{(loader == "forge" ? "Forge" : "NeoForge")} {build} @ {mc} 安装完成。";
            }
            default:
                throw new InvalidOperationException($"不支持的加载器 {loader}");
        }
    }

    /// <summary>删除实例：游戏运行中拒绝；连带清掉实例设置档案</summary>
    private Task<string> DeleteVersionAsync(JsonElement args, CancellationToken ct)
    {
        var id = Require(Str(args, "version_id"), "version_id");
        // 版本 id 会拼进删除路径：非法字符（含路径分隔符与 ..）一律拒绝
        if (!InstanceLayout.IsValidVersionId(id))
            throw new InvalidOperationException("版本 ID 含有非法字符");
        if (GameSessionGuard.IsAnyGameAlive(DotMc))
            throw new InvalidOperationException("有游戏实例正在运行，先关掉游戏再删");

        var dir = Path.Combine(DotMc, "versions", id);
        var existed = Directory.Exists(dir);
        if (existed) InstalledVersionManager.Remove(DotMc, id);
        new InstanceSettingsStore(DotMc).Remove(id);
        if (S.LastVersion == id) { S.LastVersion = null; _c.SaveSettings?.Invoke(); }
        return Task.FromResult(existed
            ? $"已删除实例 {id}（目录与实例设置一并移除）。"
            : $"没有找到实例 {id} 的目录，已清理它的设置档案。");
    }

    private Task<string> RenameVersionAsync(JsonElement args, CancellationToken ct)
    {
        var id = Require(Str(args, "version_id"), "version_id");
        var newId = Require(Str(args, "new_id"), "new_id");
        if (!InstanceLayout.IsValidVersionId(newId))
            throw new InvalidOperationException("新 ID 含有非法字符");
        if (GameSessionGuard.IsAnyGameAlive(DotMc))
            throw new InvalidOperationException("有游戏实例正在运行，先关掉游戏再改名");
        InstanceLayout.RenameInstance(DotMc, id, newId);
        if (S.LastVersion == id) { S.LastVersion = newId; _c.SaveSettings?.Invoke(); }
        return Task.FromResult($"实例 {id} 已改名为 {newId}。");
    }

    /// <summary>启动游戏：解析身份 → 补全文件 → 拉起进程（多开：同版本也允许再开一局）</summary>
    private async Task<string> LaunchGameAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Launcher is null || _c.Preflight is null || _c.Accounts is null)
            throw new InvalidOperationException("当前宿主没有启动能力");
        var id = TargetVersion(args) ?? throw new InvalidOperationException("没有目标实例，也没选中实例");
        if (!_c.Installer.IsInstalled(DotMc, id))
            throw new InvalidOperationException($"实例 {id} 没装好，先用 install_minecraft / install_loader");

        // 身份：凭证只在内存会话里，缺凭证要用户回到账户页重新登录（不静默失败）
        ResolvedPlayer player;
        try
        {
            player = await _c.Accounts.ResolveAsync();
        }
        catch (AccountLoginRequiredException ex)
        {
            return $"启动需要重新登录：{ex.Message}。请到「账户」页重新登录后再试。";
        }

        var profile = new InstanceSettingsStore(DotMc).Get(id);
        var eff = InstanceLayout.Resolve(DotMc, id, profile, S, MemoryAdvisor.Probe(GameRootOf(id)));
        var options = new LaunchOptions(
            id, player.Name, eff.GameRoot, DotMc,
            eff.MinMem, eff.MaxMem, eff.Java, eff.ExtraJvmArgs, S.UseBuiltinJvmArgs,
            player.Credentials, Language: eff.Language, LanguageOverride: eff.LanguageOverride,
            WindowWidth: eff.WindowW, WindowHeight: eff.WindowH, Fullscreen: eff.Fullscreen,
            EnvVars: eff.EnvVars, PreLaunchCommand: eff.PreCmd, PostLaunchCommand: eff.PostCmd,
            PlayerSkinUrl: player.SkinUrl);

        // 预检补全 → 拉起；实例注册与运行监控由 GameRuntimeMonitor 接管
        var preflight = await _c.Preflight.CheckAsync(DotMc, id, ct);
        if (preflight.NeedsRepair)
        {
            if (preflight.VersionJsonBroken && !preflight.CanAutoRestoreJson)
                throw new InvalidOperationException("版本 JSON 缺失或损坏且无法自动恢复，需要重新安装这个版本");
            await _c.Preflight.EnsureReadyAsync(DotMc, id, preflight, S, null, ct);
        }

        var session = await _c.Launcher.LaunchVersionAsync(DotMc, id, options, ct);

        // 运行状态监控：上下文取自本次已解析的 eff（不再重复 Resolve）
        var gameRoot = eff.GameRoot;
        var ctx = GameLaunchContextStore.Capture(id, eff,
            GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "mods")),
            GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "shaderpacks")),
            GameLaunchContextStore.CountFiles(Path.Combine(gameRoot, "resourcepacks")));
        GameRuntimeMonitor.Instance.Attach(session, ctx, session.CommandLine);

        _c.Host?.OnGameLaunched(id, session.Pid);

        S.LastVersion = id;
        new InstanceSettingsStore(DotMc).TouchPlayed(id);
        _c.SaveSettings?.Invoke();
        return $"已启动 {id}（pid {session.Pid}，玩家 {player.Name}）。窗口出现前有几秒加载时间。";
    }

    private async Task<string> RepairInstanceAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Preflight is null) throw new InvalidOperationException("当前宿主没有预检服务");
        var id = TargetVersion(args) ?? throw new InvalidOperationException("没有目标实例，也没选中实例");
        var report = await _c.Preflight.CheckAsync(DotMc, id, ct);
        if (!report.NeedsRepair) return $"实例 {id} 文件完整，不需要修复。";
        await _c.Preflight.EnsureReadyAsync(DotMc, id, report, S, null, ct);
        return $"实例 {id} 已补全：处理了 {report.Issues.Count} 项缺失 / 损坏文件。";
    }
}
