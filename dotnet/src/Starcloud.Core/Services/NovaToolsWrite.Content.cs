// Nova 写操作工具实现（资源 / 整合包 / 资源库 / 存档备份 / 设置 / Java）。

using System.Text.Json;

namespace Starcloud.Core.Services;

public sealed partial class NovaToolRegistry
{
    private async Task<string> InstallContentAsync(JsonElement args, CancellationToken ct)
    {
        var projectId = Require(Str(args, "project_id"), "project_id");
        var source = Str(args, "source") ?? ContentSourceIds.Modrinth;
        var s = S;
        var target = Str(args, "target_version") ?? s.LastVersion;
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("没有目标实例：请指定 target_version 或先选中一个实例");
        var kind = Str(args, "kind") ?? "mod";
        var wantNumber = Str(args, "version_number");

        var loader = DetectLoader(target);
        var gameVersion = RootMcVersion(s.DotMinecraft, target);
        var fromCurseForge = source == ContentSourceIds.CurseForge;
        if (fromCurseForge && _c.CurseForge is null)
            throw new InvalidOperationException("当前宿主没有接入 CurseForge");

        var versions = fromCurseForge
            ? await _c.CurseForge!.ListVersionsAsync(projectId, loader, gameVersion, ct)
            : await _c.Mods.ListVersionsAsync(projectId, loader, gameVersion, ct);
        if (versions.Count == 0 && (loader is not null || gameVersion is not null))
        {
            // 放宽过滤兜底：版本/加载器对不上也列出全部，让模型自己判断
            versions = fromCurseForge
                ? await _c.CurseForge!.ListVersionsAsync(projectId, null, null, ct)
                : await _c.Mods.ListVersionsAsync(projectId, null, null, ct);
        }
        var chosen = wantNumber is { Length: > 0 } wn
            ? versions.FirstOrDefault(v => v.VersionNumber == wn) ?? versions.FirstOrDefault()
            : versions.FirstOrDefault();
        if (chosen?.PrimaryFile is not { } file)
            return $"在 {ContentSourceIds.Display(source)} 上没找到 {projectId} 的可用版本"
                + (gameVersion is null ? "" : $"（{gameVersion}/{loader ?? "任意"}）") + "。";

        var title = fromCurseForge
            ? await _c.CurseForge!.GetProjectTitleAsync(projectId, ct) ?? file.Filename
            : await _c.Mods.GetProjectTitleAsync(projectId, ct) ?? file.Filename;
        var info = new LibraryAdoptInfo(
            title, null, null, chosen.ProjectId, chosen.Id, chosen.VersionNumber,
            chosen.GameVersions, chosen.Loaders, file.Url, source);
        var entry = await _c.Library.DownloadAndAdoptAsync(kind, file.Url, file.Filename, file.Sha1, info, ct);
        var dest = await _c.Library.InstallToVersionAsync(entry, target, ct);
        return $"已安装 {title} {chosen.VersionNumber} 到实例 {target}（{Path.GetFileName(dest)}）。";
    }

    /// <summary>整合包导入：新建隔离实例 → 落文件 → 补原版与加载器 → 写版本骨架</summary>
    private async Task<string> InstallModpackAsync(JsonElement args, CancellationToken ct)
    {
        var path = Require(Str(args, "path"), "path");
        if (!File.Exists(path)) throw new InvalidOperationException($"找不到文件：{path}");
        var s = S;
        var dotMc = s.DotMinecraft;
        var requested = Str(args, "name");
        var fromCurseForge = !Path.GetExtension(path).Equals(".mrpack", StringComparison.OrdinalIgnoreCase);

        MrPackPackageInfo? mrInfo = null;
        CurseForgePackInfo? cfInfo = null;
        if (fromCurseForge)
        {
            if (_c.CurseForgePacks is null) throw new InvalidOperationException("当前宿主没有 CurseForge 整合包安装器");
            cfInfo = _c.CurseForgePacks.ReadPackageInfo(path);
        }
        else
        {
            if (_c.MrPack is null) throw new InvalidOperationException("当前宿主没有 mrpack 安装器");
            mrInfo = _c.MrPack.ReadPackageInfo(path);
        }

        var packName = requested ?? mrInfo?.Name ?? cfInfo?.Name ?? Path.GetFileNameWithoutExtension(path);
        var gameVersion = Str(args, "target_version") ?? mrInfo?.GameVersion ?? cfInfo?.GameVersion ?? "";
        var targetId = _c.Installer.UniqueVersionId(dotMc, InstanceLayout.SafeFileName(packName));
        var installRoot = Path.Combine(dotMc, "versions", targetId);
        Directory.CreateDirectory(installRoot);

        var result = fromCurseForge
            ? await _c.CurseForgePacks!.InstallAsync(path, s, installRoot, null, null, ct)
            : await _c.MrPack!.InstallAsync(path, s, installRoot, null, null, ct);

        var ensured = await EnsureGameAndLoaderAsync(result, gameVersion, ct);

        // 版本骨架：父版本优先指向刚装好的加载器，否则退回首层原版
        var parent = LoaderVersionId(gameVersion, result.Dependencies) ?? gameVersion;
        var jsonPath = Path.Combine(installRoot, $"{targetId}.json");
        if (!File.Exists(jsonPath) && parent.Length > 0)
            File.WriteAllText(jsonPath,
                $"{{\n  \"id\": \"{targetId}\",\n  \"inheritsFrom\": \"{parent}\",\n  \"type\": \"release\"\n}}");

        var store = new InstanceSettingsStore(dotMc);
        store.Set(targetId, new VersionProfile { Isolation = "isolated", Note = $"整合包 {packName}" });
        store.Save();

        return $"整合包「{packName}」已安装为实例 {targetId}"
             + (ensured.Length > 0 ? $"（{ensured}）" : "")
             + $"，共 {result.DownloadedFiles} 个文件。";
    }

    /// <summary>整合包依赖联动：缺原版就装原版，缺加载器就装加载器</summary>
    private async Task<string> EnsureGameAndLoaderAsync(
        MrPackInstallResult result, string gameVersion, CancellationToken ct)
    {
        if (gameVersion.Length == 0) return "";
        var s = S;
        var parts = new List<string>();

        if (!_c.Installer.IsInstalled(s.DotMinecraft, gameVersion))
        {
            var entry = (await _c.Manifest.GetManifestAsync(false, ct)).Versions
                .FirstOrDefault(v => v.Id == gameVersion);
            if (entry is not null)
                await _c.Installer.InstallVanillaAsync(gameVersion, entry.Url, s, null, ct);
        }
        parts.Add($"Minecraft {gameVersion}");

        var javaMajor = 8;
        try
        {
            var v = await _c.Installer.LoadVersionAsync(s.DotMinecraft, gameVersion, ct);
            javaMajor = JavaRequirement.Resolve(gameVersion, v.JavaVersion);
        }
        catch { /* 读取失败按 Java 8 兜底，安装器会自行补齐运行时 */ }

        if (result.Dependencies.TryGetValue("fabric-loader", out var fabric))
        {
            await _c.Installer.InstallFabricAsync(gameVersion, fabric, s, null, ct);
            parts.Add($"Fabric {fabric}");
        }
        if (result.Dependencies.TryGetValue("quilt-loader", out var quilt))
        {
            await _c.Installer.InstallQuiltAsync(gameVersion, quilt, s, null, ct);
            parts.Add($"Quilt {quilt}");
        }
        if (result.Dependencies.TryGetValue("forge", out var forge) && _c.Forge is not null)
        {
            await _c.Forge.InstallViaInstallerAsync(
                ForgeInstallerService.ForgeInstallerUrl(gameVersion, forge), gameVersion, javaMajor, s, null, ct);
            parts.Add($"Forge {forge}");
        }
        if (result.Dependencies.TryGetValue("neoforge", out var neo) && _c.Forge is not null)
        {
            await _c.Forge.InstallViaInstallerAsync(
                ForgeInstallerService.NeoForgeInstallerUrl(gameVersion, neo), gameVersion, javaMajor, s, null, ct);
            parts.Add($"NeoForge {neo}");
        }
        return string.Join(" + ", parts);
    }

    /// <summary>整合包实例的父版本：找到刚装好的加载器版本 ID</summary>
    private string? LoaderVersionId(string gameVersion, IReadOnlyDictionary<string, string> deps)
    {
        var installed = InstalledVersions();
        string? Find(string prefix, string? version) => installed.FirstOrDefault(v =>
            v.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) &&
            v.Contains(gameVersion, StringComparison.OrdinalIgnoreCase) &&
            (version is null || v.Contains(version, StringComparison.OrdinalIgnoreCase)));

        if (deps.TryGetValue("fabric-loader", out var fabric) && Find("fabric-loader-", fabric) is { } f) return f;
        if (deps.TryGetValue("quilt-loader", out var quilt) && Find("quilt-loader-", quilt) is { } q) return q;
        if (deps.ContainsKey("forge") && Find("forge-", null) is { } fo) return fo;
        if (deps.ContainsKey("neoforge") && Find("neoforge", null) is { } nf) return nf;
        return null;
    }

    private async Task<string> EnableContentAsync(JsonElement args, CancellationToken ct)
    {
        var fileName = Require(Str(args, "file_name"), "file_name");
        var kind = Str(args, "kind") ?? "mod";
        var target = TargetVersion(args) ?? throw new InvalidOperationException("没有目标实例，也没选中实例");
        var entry = _c.Library.FindByFileName(kind, fileName)
            ?? throw new InvalidOperationException($"资源库里没有 {kind}「{fileName}」，先用 list_content_library 查");
        var dest = await _c.Library.InstallToVersionAsync(entry, target, ct);
        return $"已把 {entry.Title} 装载到实例 {target}（{Path.GetFileName(dest)}）。";
    }

    private Task<string> RemoveFromLibraryAsync(JsonElement args, CancellationToken ct)
    {
        var fileName = Require(Str(args, "file_name"), "file_name");
        var kind = Str(args, "kind") ?? "mod";
        var entry = _c.Library.FindByFileName(kind, fileName)
            ?? throw new InvalidOperationException($"资源库里没有 {kind}「{fileName}」");
        _c.Library.Remove(entry.Id);
        return Task.FromResult($"已从资源库删除 {entry.Title}（已装载到实例的副本不受影响）。");
    }

    private async Task<string> ExportModpackAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Export is null) throw new InvalidOperationException("当前宿主没有导出服务");
        var id = TargetVersion(args) ?? throw new InvalidOperationException("没有目标实例，也没选中实例");
        var gameRoot = GameRootOf(id);
        var dirs = _c.Export.DiscoverDirs(gameRoot)
            .Where(d => d.FileCount > 0)
            .Select(d => d.Name)
            .ToList();
        if (dirs.Count == 0) return $"实例 {id} 没有可导出的内容目录。";

        var name = Str(args, "name") ?? id;
        var outDir = Path.Combine(LauncherPaths.DataDir, "exports");
        Directory.CreateDirectory(outDir);
        var zipPath = Path.Combine(outDir, InstanceLayout.SafeFileName(name) + ".mrpack");

        var deps = new Dictionary<string, string>();
        var mc = ContentLibraryService.ResolveMc(id);
        if (mc.Length > 0) deps["minecraft"] = mc;
        var loader = ContentLibraryService.ResolveLoader(id, out var loaderVersion);
        if (loaderVersion is { Length: > 0 })
            deps[loader switch { "fabric" => "fabric-loader", "quilt" => "quilt-loader", _ => loader }] = loaderVersion;

        var result = await _c.Export.ExportAsync(
            new ExportRequest(gameRoot, zipPath, name, mc, deps, dirs), null, ct);
        return $"已导出 {result.DownloadableFiles + result.OverriddenFiles} 个文件到 {zipPath}"
             + (result.LoaderWarning is { Length: > 0 } w ? $"（提示：{w}）" : "");
    }

    private async Task<string> BackupVersionAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Backups is null) throw new InvalidOperationException("当前宿主没有备份服务");
        var id = TargetVersion(args) ?? throw new InvalidOperationException("没有目标实例，也没选中实例");
        var profile = new InstanceSettingsStore(DotMc).Get(id);
        var dirs = new List<string> { "version" };
        dirs.AddRange(InstanceLayout.ContentDirs.Where(d => d != "saves"));
        var name = Str(args, "name") ?? $"{id} 备份 {DateTime.Now:MM-dd HH:mm}";
        var entry = await _c.Backups.BackupAsync(DotMc, new VersionBackupRequest(
            id, InstanceLayout.IsIsolated(profile, S), name, dirs, Bool(args, "include_saves") ?? true), null, ct);
        return $"已备份实例 {id}：{entry.Name}（id {entry.Id}，{entry.SizeBytes / 1024 / 1024} MB，{entry.FileCount} 个文件）。";
    }

    private async Task<string> RestoreBackupAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Backups is null) throw new InvalidOperationException("当前宿主没有备份服务");
        var backupId = Require(Str(args, "backup_id"), "backup_id");
        var entry = _c.Backups.List(DotMc).FirstOrDefault(b => b.Id == backupId)
            ?? throw new InvalidOperationException($"没有 id 为 {backupId} 的备份，先用 list_backups 查");
        if (GameSessionGuard.IsAnyGameAlive(DotMc))
            throw new InvalidOperationException("有游戏实例正在运行，先关掉游戏再还原");
        var profile = new InstanceSettingsStore(DotMc).Get(entry.VersionId);
        await _c.Backups.RestoreAsync(DotMc, entry, InstanceLayout.IsIsolated(profile, S), null, ct);
        return $"已把备份「{entry.Name}」还原到实例 {entry.VersionId}（覆盖同名文件）。";
    }

    private Task<string> DeleteBackupAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Backups is null) throw new InvalidOperationException("当前宿主没有备份服务");
        var backupId = Require(Str(args, "backup_id"), "backup_id");
        var entry = _c.Backups.List(DotMc).FirstOrDefault(b => b.Id == backupId)
            ?? throw new InvalidOperationException($"没有 id 为 {backupId} 的备份");
        _c.Backups.Delete(DotMc, backupId);
        return Task.FromResult($"已删除备份「{entry.Name}」（id {backupId}）。");
    }

    private Task<string> BackupSaveAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Saves is null) throw new InvalidOperationException("当前宿主没有存档服务");
        var name = Require(Str(args, "save_name"), "save_name");
        var save = FindSave(TargetVersion(args), name)
            ?? throw new InvalidOperationException($"没找到存档「{name}」，先用 list_saves 查");
        var backupDir = Path.Combine(LauncherPaths.DataDir, "backups", "saves");
        Directory.CreateDirectory(backupDir);
        var path = _c.Saves.Backup(save.GameRoot, backupDir, save.FolderName);
        return Task.FromResult($"已备份存档「{save.LevelName}」到 {path}。");
    }

    private Task<string> DeleteSaveAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Saves is null) throw new InvalidOperationException("当前宿主没有存档服务");
        var name = Require(Str(args, "save_name"), "save_name");
        var save = FindSave(TargetVersion(args), name)
            ?? throw new InvalidOperationException($"没找到存档「{name}」");
        _c.Saves.Delete(save.GameRoot, save.FolderName);
        return Task.FromResult($"已删除存档「{save.LevelName}」（目录 {save.FolderName}）。");
    }

    /// <summary>按实例（或全部实例）找存档：先精确匹配文件夹名，再按存档显示名模糊匹配</summary>
    private SaveInfo? FindSave(string? versionId, string name)
    {
        if (_c.SaveLibrary is null) return null;
        var all = _c.SaveLibrary.ListAll(DotMc, S)
            .Where(s => versionId is null || s.VersionId.Equals(versionId, StringComparison.OrdinalIgnoreCase))
            .ToList();
        return all.FirstOrDefault(s => s.FolderName.Equals(name, StringComparison.OrdinalIgnoreCase))
            ?? all.FirstOrDefault(s => s.LevelName.Contains(name, StringComparison.OrdinalIgnoreCase));
    }

    private Task<string> UpdateSettingsAsync(JsonElement args, CancellationToken ct)
    {
        var s = S;
        var changed = new List<string>();
        if (Str(args, "download_source") is { } src) { s.DownloadSource = src; changed.Add($"下载源={src}"); }
        if (Int(args, "default_min_memory_mb") is { } min) { s.DefaultMinMemoryMb = min; changed.Add($"最小内存={min}MB"); }
        if (Int(args, "default_max_memory_mb") is { } max) { s.DefaultMaxMemoryMb = max; changed.Add($"最大内存={max}MB"); }
        if (Str(args, "last_version") is { } ver) { s.LastVersion = ver; changed.Add($"当前实例={ver}"); }
        if (Bool(args, "version_isolated") is { } iso) { s.VersionIsolated = iso; changed.Add($"版本隔离={OnOff(iso)}"); }
        if (Str(args, "java_executable") is { } java)
        {
            s.JavaExecutable = java.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : java;
            changed.Add($"Java={s.JavaExecutable ?? "自动选择"}");
        }
        if (Str(args, "theme") is { } theme) { s.Theme = theme; changed.Add($"主题={theme}"); }
        if (Str(args, "language") is { } lang) { s.Language = lang; changed.Add($"游戏语言={lang}"); }
        if (Bool(args, "auto_update_enabled") is { } auto) { s.AutoUpdateEnabled = auto; changed.Add($"自动更新={OnOff(auto)}"); }
        if (Bool(args, "launch_auto_retry") is { } retry) { s.LaunchAutoRetry = retry; changed.Add($"失败自动重试={OnOff(retry)}"); }
        if (Bool(args, "show_game_log_on_launch") is { } showLog) { s.ShowGameLogOnLaunch = showLog; changed.Add($"启动时显示日志={OnOff(showLog)}"); }
        if (Str(args, "background_mode") is { } bg) { s.BackgroundMode = bg; changed.Add($"背景模式={bg}"); }
        if (Num(args, "background_image_opacity") is { } op)
        {
            s.BackgroundImageOpacity = Math.Clamp(op, 0.2, 1);
            changed.Add($"背景不透明度={s.BackgroundImageOpacity:F2}");
        }
        if (Int(args, "background_blur_radius") is { } blur)
        {
            s.BackgroundBlurRadius = Math.Clamp(blur, 0, 64);
            changed.Add($"背景模糊={s.BackgroundBlurRadius}");
        }
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("extra_jvm_args", out _))
        {
            s.ExtraJvmArgs = StrList(args, "extra_jvm_args");
            changed.Add($"附加 JVM 参数 {s.ExtraJvmArgs.Count} 项");
        }
        if (args.ValueKind == JsonValueKind.Object &&
            args.TryGetProperty("global_env_vars", out var env) && env.ValueKind == JsonValueKind.Object)
        {
            s.GlobalEnvVars = env.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.String)
                .ToDictionary(p => p.Name, p => p.Value.GetString() ?? "");
            changed.Add($"环境变量 {s.GlobalEnvVars.Count} 项");
        }
        if (changed.Count == 0) return Task.FromResult("没有可识别的设置项，什么都没改。");
        _c.SaveSettings?.Invoke();
        return Task.FromResult("已修改并保存：" + string.Join("，", changed));
    }

    private Task<string> UpdateInstanceSettingsAsync(JsonElement args, CancellationToken ct)
    {
        var id = Require(Str(args, "version_id"), "version_id");
        var store = new InstanceSettingsStore(DotMc);
        var profile = store.Get(id);
        var changed = new List<string>();

        if (Str(args, "memory_mode") is { } mm) { profile.MemoryMode = mm; changed.Add($"内存档位={mm}"); }
        if (Int(args, "memory_max_mb") is { } max) { profile.MemoryMaxMb = max; changed.Add($"内存上限={max}MB"); }
        if (Int(args, "memory_min_mb") is { } min) { profile.MemoryMinMb = min; changed.Add($"内存下限={min}MB"); }
        if (Str(args, "java_mode") is { } jm) { profile.JavaMode = jm; changed.Add($"Java 档位={jm}"); }
        if (Str(args, "java_path") is { } jp)
        {
            profile.JavaPath = jp.Equals("auto", StringComparison.OrdinalIgnoreCase) ? null : jp;
            changed.Add($"Java={profile.JavaPath ?? "按版本推荐"}");
        }
        if (Str(args, "window_mode") is { } wm) { profile.WindowMode = wm; changed.Add($"窗口档位={wm}"); }
        if (Int(args, "window_width") is { } ww) { profile.WindowWidth = ww; changed.Add($"窗口宽={ww}"); }
        if (Int(args, "window_height") is { } wh) { profile.WindowHeight = wh; changed.Add($"窗口高={wh}"); }
        if (Bool(args, "fullscreen") is { } fs) { profile.Fullscreen = fs; changed.Add($"全屏={OnOff(fs)}"); }
        if (Bool(args, "isolation") is { } iso)
        {
            profile.Isolation = iso ? "isolated" : "shared";
            changed.Add($"版本隔离={OnOff(iso)}");
        }
        if (Bool(args, "clear_server_address") == true)
        {
            profile.ServerAddress = null;
            changed.Add("已清除自动连服");
        }
        else if (Str(args, "server_address") is { } addr)
        {
            profile.ServerAddress = addr;
            changed.Add($"自动连服={addr}");
        }
        if (Str(args, "language_override") is { } lang) { profile.LanguageOverride = lang; changed.Add($"语言覆盖={lang}"); }
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("extra_jvm_args", out _))
        {
            profile.ExtraJvmArgs = StrList(args, "extra_jvm_args");
            changed.Add($"附加 JVM 参数 {profile.ExtraJvmArgs.Count} 项");
        }
        if (Str(args, "pre_launch_command") is { } pre) { profile.PreLaunchCommand = pre; changed.Add("已设置启动前命令"); }
        if (Str(args, "post_launch_command") is { } post) { profile.PostLaunchCommand = post; changed.Add("已设置退出后命令"); }
        if (Str(args, "note") is { } note) { profile.Note = note; changed.Add("已更新备注"); }

        if (changed.Count == 0) return Task.FromResult($"实例 {id} 没有可识别的设置项，什么都没改。");
        store.Set(id, profile);
        store.Save();
        return Task.FromResult($"已更新实例 {id}：" + string.Join("，", changed));
    }

    private async Task<string> DownloadJavaAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Java is null) throw new InvalidOperationException("当前宿主没有 Java 服务");
        var major = Int(args, "major_version") ?? throw new InvalidOperationException("缺少 major_version");
        var path = await _c.Java.DownloadRuntimeAsync(major, null, ct);
        return $"Java {major} 已安装到 {path}。";
    }

    // ── 任务 / 通知 / 账户 / 自更新 ──

    private static Task<string> CancelTaskAsync(JsonElement args, CancellationToken ct)
    {
        var center = TaskCenterService.Instance;
        var tasks = center.Snapshot();
        var id = Str(args, "task_id");
        var cancelAll = Bool(args, "all") == true || string.IsNullOrWhiteSpace(id);

        var targets = cancelAll
            ? tasks.Where(t => t.State == AppTaskState.Running).ToList()
            : tasks.Where(t => t.Id == id).ToList();
        if (targets.Count == 0)
            return Task.FromResult(cancelAll
                ? "现在没有进行中的任务。"
                : $"没有找到 id 为 {id} 的任务（可用 list_tasks 看最新列表）。");

        foreach (var t in targets) center.Cancel(t);
        return Task.FromResult(cancelAll
            ? $"已取消 {targets.Count} 个进行中的任务。"
            : $"已取消任务「{targets[0].Title}」。");
    }

    private static Task<string> ClearTasksAsync(JsonElement args, CancellationToken ct)
    {
        var center = TaskCenterService.Instance;
        foreach (var t in center.Snapshot().Where(t => t.State == AppTaskState.Running).ToList())
            center.Cancel(t);
        center.ClearAll();
        return Task.FromResult("已清空任务中心列表。");
    }

    private Task<string> SetDefaultAccountAsync(JsonElement args, CancellationToken ct)
    {
        var store = RequireStore();
        var uuid = Require(Str(args, "uuid"), "uuid");
        var account = store.Accounts.FirstOrDefault(a => a.Uuid == uuid)
            ?? throw new InvalidOperationException($"没有 uuid 为 {uuid} 的账户（可用 list_accounts 查）");
        store.SetDefault(uuid);
        store.Save();
        return Task.FromResult($"已把 {account.Username} 设为默认账户。");
    }

    private Task<string> RemoveAccountAsync(JsonElement args, CancellationToken ct)
    {
        var store = RequireStore();
        var uuid = Require(Str(args, "uuid"), "uuid");
        var account = store.Accounts.FirstOrDefault(a => a.Uuid == uuid)
            ?? throw new InvalidOperationException($"没有 uuid 为 {uuid} 的账户（可用 list_accounts 查）");
        store.Remove(uuid);
        store.Save();
        return Task.FromResult($"已从启动器移除账户 {account.Username}（上游账号不受影响）。");
    }

    private AccountStore RequireStore()
    {
        if (_c.Store is not null) return _c.Store;
        // 宿主没传运行中的账户表（例如纯 Core 场景）：退回从磁盘加载
        try { return AccountStore.Load(); }
        catch (Exception ex) { throw new InvalidOperationException($"读取账户表失败：{ex.Message}"); }
    }

    private async Task<string> InstallUpdateAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Updates is null) throw new InvalidOperationException("当前宿主没有自更新服务");
        if (_c.Host is null) throw new InvalidOperationException("当前宿主不支持自动安装更新，请在设置页手动更新");
        if (!OperatingSystem.IsWindows() || SelfUpdateService.Capability != UpdateCapability.SilentInstall)
            throw new InvalidOperationException("当前平台不支持静默升级，请到设置页手动下载安装包");

        var current = SelfUpdateService.CurrentVersion();
        var manifest = await _c.Updates.FetchManifestAsync(ct);
        if (!SelfUpdateService.IsNewerVersion(manifest.Version, current))
            return $"当前已是最新版本 {current}，不需要更新。";

        var form = SelfUpdateService.DetectInstallForm();
        var artifact = form == UpdateInstallForm.NsisInstaller ? manifest.Setup : manifest.Portable;
        if (artifact is null)
            throw new InvalidOperationException("更新清单缺少对应形态的下载文件");
        if (form == UpdateInstallForm.Portable &&
            !SelfUpdateService.IsAppDirWritable(AppContext.BaseDirectory))
            throw new InvalidOperationException("程序目录不可写，无法就地更新，请改用安装版或到设置页手动下载");

        var path = await _c.Updates.DownloadArtifactAsync(artifact, null, ct);
        if (form == UpdateInstallForm.Portable) await _c.Updates.ExtractPortableAsync(path, ct);
        _c.Host.RestartForUpdate(path, manifest.Version);
        return $"更新 {current} → {manifest.Version} 已就绪，启动器正在重启安装。";
    }
}
