// Nova 工具 · 只读部分：查询状态、读日志、搜资源、列清单。
// 这些工具立即执行、不落盘，也不改任何设置（信任边界的第一层）。

using System.Text;
using System.Text.Json;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

public sealed partial class NovaToolRegistry
{
    private IEnumerable<NovaTool> BuildReadTools()
    {
        // ── 启动器状态 ──

        yield return new("get_settings", "读取启动器设置",
            "读取全局设置：游戏目录、当前版本、内存、Java、下载源、版本隔离、语言。",
            """{"type":"object","properties":{}}""",
            true, GetSettingsAsync);

        yield return new("get_launcher_status", "读取启动器状态",
            "一屏看清当前状态：在跑的游戏、进行中的任务、联机、录屏、更新。回答「现在能玩吗 / 在忙什么」先看这里。",
            """{"type":"object","properties":{}}""",
            true, GetLauncherStatusAsync);

        yield return new("list_versions", "列出游戏版本",
            "列出已安装版本与官方最新版本。不要臆造版本号，装版本前先看这里。",
            """{"type":"object","properties":{}}""",
            true, ListVersionsAsync);

        yield return new("read_version_detail", "查看版本详情",
            "查看某个已安装版本的详情：Java 要求、实例设置、已装 mod 列表。",
            """{"type":"object","properties":{"version_id":{"type":"string"}},"required":["version_id"]}""",
            true, ReadVersionDetailAsync);

        yield return new("get_instance_settings", "读取实例设置",
            "读取某个版本的实例级覆盖设置（内存档位、Java 档位、窗口、自动连服、隔离、备注）。",
            """{"type":"object","properties":{"version_id":{"type":"string"}},"required":["version_id"]}""",
            true, GetInstanceSettingsAsync);

        yield return new("list_java", "列出本机 Java",
            "列出已安装 / 已下载的 Java 运行时及其主版本，用于判断某版本能不能启动。",
            """{"type":"object","properties":{}}""",
            true, ListJavaAsync);

        yield return new("advise_memory", "内存建议",
            "按本机物理内存与实例的模组/光影数量给出建议内存上限，并对比当前设置。",
            """{"type":"object","properties":{"version_id":{"type":"string","description":"缺省用当前选中版本"}}}""",
            true, AdviseMemoryAsync);

        yield return new("read_game_log", "读取游戏日志",
            "读取某个实例最近的游戏日志（latest.log 尾部）与崩溃报告文件路径，用于定位崩溃。",
            """{"type":"object","properties":{"version_id":{"type":"string"},"lines":{"type":"integer","description":"读取行数，默认 40，最多 200"}}}""",
            true, ReadGameLogAsync);

        // ── 资源搜索 ──

        yield return new("search_modrinth", "搜索 Modrinth 资源",
            "在 Modrinth 搜索 mod/资源包/光影/数据包/整合包，返回标题、项目 ID、下载量和简介。安装用 install_content。",
            """{"type":"object","properties":{"query":{"type":"string","description":"搜索关键词"},"kind":{"type":"string","enum":["mod","resourcepack","shader","datapack","modpack"],"description":"资源类型，默认 mod"},"game_version":{"type":"string","description":"MC 版本过滤，如 1.21.1"},"loader":{"type":"string","enum":["fabric","forge","quilt","neoforge"]}},"required":["query"]}""",
            true, SearchModrinthAsync);

        yield return new("search_curseforge", "搜索 CurseForge 资源",
            "在 CurseForge 搜索 mod/资源包/光影/整合包。装 CurseForge 的 mod 用 install_content 并带 source=curseforge。",
            """{"type":"object","properties":{"query":{"type":"string"},"kind":{"type":"string","enum":["mod","resourcepack","shader","datapack","modpack"],"description":"默认 mod"},"game_version":{"type":"string"}},"required":["query"]}""",
            true, SearchCurseForgeAsync);

        yield return new("translate_query", "翻译搜索词",
            "把中文搜索词翻成英文项目名（Modrinth / CurseForge 只有英文名）。用户用中文说资源名时先调这个再搜。",
            """{"type":"object","properties":{"query":{"type":"string"}},"required":["query"]}""",
            true, TranslateQueryAsync);

        // ── 资源库 / 存档 / 备份 ──

        yield return new("list_content_library", "列出资源库",
            "列出本地资源库里的 mod/资源包/光影/数据包/整合包（可按类型筛选或按名字过滤），返回 file_name 供 enable_content 使用。",
            """{"type":"object","properties":{"kind":{"type":"string","enum":["mod","resourcepack","shader","datapack","modpack"]},"query":{"type":"string","description":"按标题/文件名过滤"}}}""",
            true, ListLibraryAsync);

        yield return new("check_content_updates", "检查资源更新",
            "扫描某个实例已装载的资源，列出可更新项（新版本号）。",
            """{"type":"object","properties":{"version_id":{"type":"string","description":"缺省用当前选中版本"}}}""",
            true, CheckContentUpdatesAsync);

        yield return new("list_saves", "列出存档",
            "列出所有实例的存档（名称、所属版本、大小、上次游玩）。",
            """{"type":"object","properties":{}}""",
            true, (_, _) => Task.FromResult(ListSavesText()));

        yield return new("list_backups", "列出备份",
            "列出实例备份（可备份整包或指定内容目录），返回 id 供 restore_backup / delete_backup 使用。",
            """{"type":"object","properties":{"version_id":{"type":"string"}}}""",
            true, ListBackupsAsync);

        yield return new("list_accounts", "列出游戏账户",
            "列出已添加的游戏账户（用户名、类型、是否默认）。不返回任何令牌。",
            """{"type":"object","properties":{}}""",
            true, ListAccountsAsync);

        // ── 任务 / 通知 ──

        yield return new("list_tasks", "列出下载任务",
            "列出任务中心的进行中与已完成任务（进度、速度、结果）。",
            """{"type":"object","properties":{}}""",
            true, ListTasksAsync);

        yield return new("list_notifications", "列出通知",
            "列出启动器最近的通知（错误、警告、提示）。",
            """{"type":"object","properties":{}}""",
            true, ListNotificationsAsync);

        // ── 体检 / 诊断 ──

        yield return new("diagnose_instance", "游戏体检",
            "扫描全部已安装版本的文件完整性（版本 JSON / client jar / 依赖父版本 / 资源索引），列出问题。",
            """{"type":"object","properties":{}}""",
            true, DiagnoseAsync);

        yield return new("analyze_crash", "分析崩溃",
            "按崩溃报告 / 游戏日志做离线诊断，给出结论、证据与修复建议（不依赖云端）。",
            """{"type":"object","properties":{"version_id":{"type":"string"},"exit_code":{"type":"integer","description":"游戏退出码，缺省 -1"}}}""",
            true, AnalyzeCrashAsync);

        yield return new("check_mod_conflicts", "检查模组冲突",
            "扫描 mods 目录：重复 modId、加载器混装、损坏文件。处理用 resolve_mod_conflicts。",
            """{"type":"object","properties":{}}""",
            true, CheckModConflictsAsync);

        yield return new("check_environment", "启动环境检查",
            "检查某版本能不能启动：Java 是否匹配、内存是否越界、路径是否合法、账号是否就绪。",
            """{"type":"object","properties":{"version_id":{"type":"string","description":"缺省用当前选中版本"}}}""",
            true, CheckEnvironmentAsync);

        yield return new("check_updates", "检查启动器更新",
            "检查启动器本身是否有新版本，返回远端版本号与更新说明。",
            """{"type":"object","properties":{}}""",
            true, CheckUpdatesAsync);

        // ── 联机 / 录屏 / 插件 ──

        if (_c.Multiplayer is not null)
            yield return new("multiplayer_status", "联机状态",
                "读取联机状态：当前提供方、房间号、玩家数、节点地区、可用提供方清单。",
                """{"type":"object","properties":{}}""",
                true, MultiplayerStatusAsync);

        if (_c.Host is not null)
        {
            yield return new("recording_status", "录屏状态",
                "读取录屏状态：是否在录、硬件编码器、输出目录。",
                """{"type":"object","properties":{}}""",
                true, (_, _) => Task.FromResult(_c.Host!.RecordingStatus() ?? "这台机器不支持硬件录屏。"));

            yield return new("list_plugins", "列出插件",
                "列出已安装插件（名称、版本、来源、启用状态）。",
                """{"type":"object","properties":{}}""",
                true, (_, _) => Task.FromResult(_c.Host!.ListPlugins() ?? "当前宿主没提供插件能力。"));
        }
    }

    // ── 实现 ──

    private Task<string> GetSettingsAsync(JsonElement args, CancellationToken ct)
    {
        var s = S;
        return Task.FromResult(string.Join("\n",
            $"游戏目录：{s.DotMinecraft}",
            $"当前版本：{s.LastVersion ?? "未选择"}",
            $"内存：{s.DefaultMinMemoryMb}~{s.DefaultMaxMemoryMb} MB",
            $"Java：{s.JavaExecutable ?? "自动选择"}",
            $"下载源：{s.DownloadSource}",
            $"版本隔离：{OnOff(s.VersionIsolated)}",
            $"语言：{s.Language}",
            $"主题：{s.Theme}，自动更新：{OnOff(s.AutoUpdateEnabled)}"));
    }

    private Task<string> GetLauncherStatusAsync(JsonElement args, CancellationToken ct)
    {
        var running = GameSessionGuard.IsAnyGameAlive(DotMc);
        var tasks = TaskCenterService.Instance;
        var lines = new List<string>
        {
            $"游戏：{(running ? "有实例正在运行" : "未运行")}",
            $"当前版本：{S.LastVersion ?? "未选择"}",
            $"进行中任务：{tasks.RunningCount} 个（共 {tasks.Snapshot().Count} 条记录）",
        };
        if (_c.Multiplayer is { Snapshot: { } mp })
            lines.Add($"联机：{mp.State.Phase}（{mp.ProviderId}）");
        lines.Add($"录屏：{_c.Host?.RecordingStatus() ?? "不支持"}");
        return Task.FromResult(string.Join("\n", lines));
    }

    private async Task<string> ListVersionsAsync(JsonElement args, CancellationToken ct)
    {
        var installed = InstalledVersions();
        var manifest = await _c.Manifest.GetManifestAsync(false, ct);
        var sb = new StringBuilder();
        sb.Append($"官方最新：正式版 {manifest.Latest.Release}，快照 {manifest.Latest.Snapshot}。");
        sb.AppendLine(installed.Count > 0
            ? $"已安装（{installed.Count}）：{string.Join("、", installed.Take(40))}"
            : "还没有已安装版本。");
        var recent = manifest.Versions
            .Where(v => v.Type == "release" && !installed.Contains(v.Id))
            .Take(8).Select(v => v.Id).ToList();
        if (recent.Count > 0)
            sb.Append($"近期正式版（未安装）：{string.Join("、", recent)}");
        return sb.ToString();
    }

    private async Task<string> ReadVersionDetailAsync(JsonElement args, CancellationToken ct)
    {
        var id = Str(args, "version_id");
        if (string.IsNullOrWhiteSpace(id)) return "缺少 version_id。先用 list_versions 看看装了哪些。";
        var s = S;
        var dotMc = s.DotMinecraft;
        var sb = new StringBuilder();

        try
        {
            var v = await _c.Installer.LoadVersionAsync(dotMc, id, ct);
            sb.AppendLine($"版本 {id}：mainClass={v.MainClass}，Java={v.JavaVersion?.MajorVersion.ToString() ?? "自动"}");
        }
        catch
        {
            sb.AppendLine($"版本 {id}：JSON 不存在或已损坏（可能没装完）");
        }

        var profile = new InstanceSettingsStore(dotMc).Get(id);
        var gameRoot = GameRootOf(id);
        foreach (var (kind, dir) in new[]
                 {
                     ("mod", "mods"), ("资源包", "resourcepacks"),
                     ("光影", "shaderpacks"), ("存档", "saves"),
                 })
        {
            var path = Path.Combine(gameRoot, dir);
            if (!Directory.Exists(path)) continue;
            var files = Directory.GetFiles(path).Where(f => !f.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase)).ToArray();
            sb.AppendLine($"{kind} {files.Length} 个：{string.Join("、", files.Select(Path.GetFileNameWithoutExtension).Take(12))}");
        }
        if (profile.Note is { Length: > 0 } note) sb.AppendLine($"备注：{note}");
        return sb.ToString();
    }

    private Task<string> GetInstanceSettingsAsync(JsonElement args, CancellationToken ct)
    {
        var id = Str(args, "version_id");
        if (string.IsNullOrWhiteSpace(id)) return Task.FromResult("缺少 version_id。");
        var profile = new InstanceSettingsStore(DotMc).Get(id);
        var eff = InstanceLayout.Resolve(DotMc, id, profile, S, MemoryAdvisor.Probe(GameRootOf(id)));
        return Task.FromResult(string.Join("\n",
            $"版本：{id}",
            $"内存档位：{profile.MemoryMode ?? "跟随全局"} → 生效 {eff.MinMem}~{eff.MaxMem} MB",
            $"Java 档位：{profile.JavaMode ?? "跟随全局"} → {eff.Java ?? "按版本 JSON 推荐"}",
            $"窗口：{(eff.Fullscreen == true ? "全屏" : $"{eff.WindowW}×{eff.WindowH}")}",
            $"版本隔离：{OnOff(eff.Isolated)}",
            $"自动连服：{eff.ServerAddress ?? "未设置"}",
            $"附加 JVM 参数：{(eff.ExtraJvmArgs.Count == 0 ? "无" : string.Join(" ", eff.ExtraJvmArgs))}",
            $"游戏目录：{eff.GameRoot}"));
    }

    private async Task<string> ListJavaAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Java is null) return "当前宿主没有 Java 服务。";
        var list = await _c.Java.ListRuntimesAsync(ct);
        if (list.Count == 0) return "没检测到 Java 运行时。可以用 download_java 下载（设置页也能下载）。";
        var sb = new StringBuilder($"检测到 {list.Count} 个 Java：");
        foreach (var r in list.Take(12))
        {
            sb.AppendLine();
            sb.Append($"· Java {r.MajorVersion}（{r.Source}）{r.Executable}");
        }
        return sb.ToString();
    }

    private Task<string> AdviseMemoryAsync(JsonElement args, CancellationToken ct)
    {
        var id = TargetVersion(args);
        var (totalMb, usedMb) = SystemMemory.Snapshot();
        var gameRoot = id is null ? DotMc : GameRootOf(id);
        var (modCount, hasShaders) = MemoryAdvisor.Probe(gameRoot);
        var suggest = MemoryAdvisor.SuggestMaxMb(totalMb, modCount, hasShaders);
        return Task.FromResult(string.Join("\n",
            $"物理内存：{totalMb} MB（已用 {usedMb} MB）",
            $"实例：{id ?? "（未选版本，按共享目录统计）"}，mod {modCount} 个，光影 {(hasShaders ? "有" : "无")}",
            $"建议上限：{suggest} MB；当前全局上限 {S.DefaultMaxMemoryMb} MB",
            MemoryAdvisor.Explain(totalMb, modCount, hasShaders)));
    }

    private Task<string> ReadGameLogAsync(JsonElement args, CancellationToken ct)
    {
        var id = TargetVersion(args);
        if (id is null) return Task.FromResult("没有目标版本，也没选中版本。");
        var gameRoot = GameRootOf(id);
        var lines = Math.Clamp(Int(args, "lines") ?? 40, 1, 200);
        var latest = Path.Combine(gameRoot, "logs", "latest.log");
        var crash = LatestCrashReport(gameRoot);
        var sb = new StringBuilder($"实例 {id} 游戏目录：{gameRoot}");
        sb.AppendLine();
        sb.AppendLine($"崩溃报告：{crash ?? "无"}");
        sb.AppendLine($"latest.log 尾部 {lines} 行：");
        var tail = TailLines(latest, lines);
        sb.Append(tail.Length == 0 ? "（没有日志文件）" : Cut(tail, 600));
        return Task.FromResult(sb.ToString());
    }

    private async Task<string> SearchModrinthAsync(JsonElement args, CancellationToken ct)
    {
        var query = Str(args, "query") ?? "";
        var kind = Str(args, "kind") ?? "mod";
        var loader = Str(args, "loader");
        var gameVersion = Str(args, "game_version");
        var hits = (await _c.Mods.SearchAsync(query, kind, loader, gameVersion, 0, 6, ct: ct)).Hits;
        if (hits.Count == 0) return $"Modrinth 上没搜到「{query}」"
            + (gameVersion is null ? "" : $"（{gameVersion}{loader ?? ""}）") + "。试试去掉版本过滤再搜。";
        var sb = new StringBuilder();
        sb.AppendLine($"「{query}」搜索结果（{kind}{(gameVersion is null ? "" : $" / {gameVersion}")}）：");
        for (var i = 0; i < hits.Count; i++)
        {
            var h = hits[i];
            sb.AppendLine($"{i + 1}. {h.Title}（project_id: {h.ProjectId}，下载 {h.Downloads:N0}）— {h.Description}");
        }
        sb.Append("要安装请用 install_content + 对应 project_id。");
        return sb.ToString();
    }

    private async Task<string> SearchCurseForgeAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.CurseForge is null) return "当前宿主没有接入 CurseForge。";
        var query = Str(args, "query") ?? "";
        var kind = Str(args, "kind") ?? "mod";
        var gameVersion = Str(args, "game_version");
        var hits = (await _c.CurseForge.SearchAsync(query, kind, null, gameVersion, 0, 6, ct: ct)).Hits;
        if (hits.Count == 0) return $"CurseForge 上没搜到「{query}」。";
        var sb = new StringBuilder($"「{query}」的 CurseForge 结果：");
        for (var i = 0; i < hits.Count; i++)
            sb.AppendLine().Append($"{i + 1}. {hits[i].Title}（project_id: {hits[i].ProjectId}，下载 {hits[i].Downloads:N0}）");
        sb.Append("\n安装用 install_content 且 source=curseforge。");
        return sb.ToString();
    }

    private async Task<string> TranslateQueryAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Translation is null) return "当前宿主没有接入搜索翻译。";
        var query = Str(args, "query") ?? "";
        if (query.Length == 0) return "缺少 query。";
        var t = await _c.Translation.TranslateAsync(query, ct);
        return t.Translated
            ? $"「{t.Original}」→ 英文检索词「{t.Effective}」（来源：{t.SourceLabel}）"
            : $"「{t.Original}」本身就是可用的英文检索词。";
    }

    private async Task<string> ListLibraryAsync(JsonElement args, CancellationToken ct)
    {
        var kind = Str(args, "kind");
        var query = Str(args, "query");
        var entries = await _c.Library.ListAsync(kind, ct);
        var filtered = entries
            .Where(e => query is null
                || e.Title.Contains(query, StringComparison.OrdinalIgnoreCase)
                || e.FileName.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (filtered.Count == 0) return query is null ? "资源库是空的。" : $"资源库里没有匹配「{query}」的条目。";
        var sb = new StringBuilder($"资源库 {filtered.Count} 条：");
        foreach (var e in filtered.Take(15))
        {
            sb.AppendLine();
            sb.Append($"· [{e.Kind}] {e.Title}（file_name: {e.FileName}，{e.SizeBytes / 1024 / 1024} MB，来源 {ContentSourceIds.Display(e.Source)}）");
        }
        if (filtered.Count > 15) sb.Append($"\n（还有 {filtered.Count - 15} 条，用 query 缩小范围）");
        return sb.ToString();
    }

    private async Task<string> CheckContentUpdatesAsync(JsonElement args, CancellationToken ct)
    {
        var id = TargetVersion(args);
        if (id is null) return "没有目标版本，也没选中版本。";
        var gameRoot = GameRootOf(id);
        var modsDir = Path.Combine(gameRoot, "mods");
        if (!Directory.Exists(modsDir)) return $"实例 {id} 没有 mods 目录。";
        var loader = ContentLibraryService.ResolveLoader(id, out _);
        var mc = ContentLibraryService.ResolveMc(id);
        var updates = new List<string>();
        foreach (var file in Directory.GetFiles(modsDir, "*.jar").Take(20))
        {
            ct.ThrowIfCancellationRequested();
            var r = await _c.Library.CheckUpdateAsync(file, mc, loader, ct);
            if (r is not null)
                updates.Add($"{r.FileName}：{r.CurrentVersion} → {r.LatestVersion}");
        }
        return updates.Count == 0
            ? $"实例 {id} 的 mod 都是最新的（检查了前 20 个）。"
            : $"实例 {id} 有 {updates.Count} 个可更新：\n" + string.Join("\n", updates);
    }

    private string ListSavesText()
    {
        if (_c.SaveLibrary is null) return "当前宿主没有存档服务。";
        var saves = _c.SaveLibrary.ListAll(DotMc, S);
        if (saves.Count == 0) return "没有任何存档。";
        var sb = new StringBuilder($"共 {saves.Count} 个存档：");
        foreach (var s in saves.Take(20))
        {
            sb.AppendLine();
            sb.Append($"· {s.LevelName}（版本 {(s.VersionId.Length > 0 ? s.VersionId : "共享根")}，{s.SizeBytes / 1024 / 1024} MB，最后游玩 {s.LastPlayed:yyyy-MM-dd}）");
        }
        return sb.ToString();
    }

    private Task<string> ListBackupsAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Backups is null) return Task.FromResult("当前宿主没有备份服务。");
        var id = Str(args, "version_id");
        var list = _c.Backups.List(DotMc, id);
        if (list.Count == 0) return Task.FromResult(id is null ? "还没有任何备份。" : $"版本 {id} 还没有备份。");
        var sb = new StringBuilder($"备份 {list.Count} 条：");
        foreach (var b in list.Take(15))
        {
            sb.AppendLine();
            sb.Append($"· [{b.Id}] {b.Name}（版本 {b.VersionId}，{b.SizeBytes / 1024 / 1024} MB，{b.FileCount} 个文件，{b.CreatedAt:yyyy-MM-dd HH:mm}）");
        }
        return Task.FromResult(sb.ToString());
    }

    private Task<string> ListAccountsAsync(JsonElement args, CancellationToken ct)
    {
        var store = AccountStore.Load();
        var accounts = store.Accounts;
        if (accounts.Count == 0) return Task.FromResult("还没有添加游戏账户。");
        var sb = new StringBuilder($"共 {accounts.Count} 个账户（不显示令牌）：");
        foreach (var a in accounts.Take(10))
        {
            sb.AppendLine();
            sb.Append($"· {a.Username}（{a.Type}{(a.IsDefault ? "，默认" : "")}{(a.ServerName is { Length: > 0 } sn ? $"，认证服 {sn}" : "")}）");
        }
        return Task.FromResult(sb.ToString());
    }

    private Task<string> ListTasksAsync(JsonElement args, CancellationToken ct)
    {
        var tasks = TaskCenterService.Instance.Snapshot();
        if (tasks.Count == 0) return Task.FromResult("任务中心是空的。");
        var sb = new StringBuilder($"任务 {tasks.Count} 条（进行中 {TaskCenterService.Instance.RunningCount}）：");
        foreach (var t in tasks.Take(12))
        {
            sb.AppendLine();
            sb.Append($"· {t.Title} — {t.State}{(t.Progress is { } p ? $" {p * 100:F0}%" : "")} {t.Detail}");
        }
        return Task.FromResult(sb.ToString());
    }

    private Task<string> ListNotificationsAsync(JsonElement args, CancellationToken ct)
    {
        var notices = NotificationCenter.Instance.Snapshot();
        if (notices.Count == 0) return Task.FromResult("没有通知。");
        var sb = new StringBuilder($"通知 {notices.Count} 条：");
        foreach (var n in notices.Take(12))
        {
            sb.AppendLine();
            sb.Append($"· [{n.Level}] {n.Message}");
        }
        return Task.FromResult(sb.ToString());
    }

    private async Task<string> DiagnoseAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Diagnostics is null) return "当前宿主没有体检服务。";
        var report = await _c.Diagnostics.DiagnoseAsync(DotMc, ct);
        if (report.Issues.Count == 0) return $"扫描 {report.ScannedCount} 个版本，没有发现问题。";
        var sb = new StringBuilder($"扫描 {report.ScannedCount} 个版本，发现 {report.Issues.Count} 个问题：");
        foreach (var i in report.Issues.Take(10))
        {
            sb.AppendLine();
            sb.Append($"· {i.VersionId}｜{i.Kind}：{i.Detail}{(i.AutoFixable ? "（可自动修复，用 repair_instance）" : "")}");
        }
        return sb.ToString();
    }

    private async Task<string> AnalyzeCrashAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Crash is null) return "当前宿主没有崩溃分析服务。";
        var id = TargetVersion(args);
        if (id is null) return "没有目标版本，也没选中版本。";
        var exitCode = Int(args, "exit_code") ?? -1;
        var gameRoot = GameRootOf(id);
        var diag = await _c.Crash.AnalyzeAsync(DotMc, id, exitCode, DateTime.Now.AddHours(-24), null, gameRoot, ct);
        var sb = new StringBuilder();
        sb.AppendLine($"结论：{diag.Title}");
        sb.AppendLine(diag.Summary);
        if (diag.Evidence.Length > 0) sb.AppendLine($"证据：{Cut(diag.Evidence, 200)}");
        if (diag.Suggestions.Count > 0) sb.AppendLine("建议：" + string.Join("；", diag.Suggestions.Take(3)));
        if (diag.FixLabel is { Length: > 0 } fix) sb.Append($"可一键执行：{fix}");
        return sb.ToString();
    }

    private async Task<string> CheckModConflictsAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Conflicts is null) return "当前宿主没有冲突检测服务。";
        var report = await _c.Conflicts.ScanAsync(DotMc, ct);
        if (report.Conflicts.Count == 0) return $"扫描 {report.ScannedCount} 个模组，没有发现冲突。";
        var sb = new StringBuilder($"扫描 {report.ScannedCount} 个模组，发现 {report.Conflicts.Count} 处冲突：");
        foreach (var c in report.Conflicts.Take(8))
        {
            sb.AppendLine();
            sb.Append($"· {c.Kind}：{c.Detail}（涉及 {c.Files.Count} 个文件）建议：{c.Suggestion}");
        }
        return sb.ToString();
    }

    private async Task<string> CheckEnvironmentAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.EnvCheck is null || _c.Preflight is null) return "当前宿主没有环境检查服务。";
        var id = TargetVersion(args);
        if (id is null) return "没有目标版本，也没选中版本。";
        var profile = new InstanceSettingsStore(DotMc).Get(id);
        var eff = InstanceLayout.Resolve(DotMc, id, profile, S, MemoryAdvisor.Probe(GameRootOf(id)));
        var options = new LaunchOptions(id, "（仅检查）", eff.GameRoot, DotMc,
            eff.MinMem, eff.MaxMem, eff.Java, eff.ExtraJvmArgs, S.UseBuiltinJvmArgs,
            Language: eff.Language, LanguageOverride: eff.LanguageOverride,
            WindowWidth: eff.WindowW, WindowHeight: eff.WindowH, Fullscreen: eff.Fullscreen,
            EnvVars: eff.EnvVars, PreLaunchCommand: eff.PreCmd, PostLaunchCommand: eff.PostCmd);

        var sb = new StringBuilder($"实例 {id} 启动环境：");
        var env = await _c.EnvCheck.CheckAsync(DotMc, id, options, S, ct);
        sb.AppendLine(env.NeedsAttention
            ? $"发现 {env.Issues.Count} 个环境问题（最高级别 {env.Worst}）"
            : "环境检查通过");
        foreach (var i in env.Issues.Take(5))
        {
            sb.AppendLine();
            sb.Append($"· [{i.Level}] {i.Title}：{i.Detail}");
        }

        var pre = await _c.Preflight.CheckAsync(DotMc, id, ct);
        sb.AppendLine();
        sb.Append(pre.NeedsRepair
            ? $"文件检查：缺 {pre.Issues.Count} 项，需要补全（用 repair_instance 修）"
            : "文件检查：完整");
        return sb.ToString();
    }

    private async Task<string> CheckUpdatesAsync(JsonElement args, CancellationToken ct)
    {
        if (_c.Updates is null) return "当前宿主没有自更新服务。";
        var manifest = await _c.Updates.FetchManifestAsync(ct);
        var current = SelfUpdateService.CurrentVersion();
        if (!SelfUpdateService.IsNewerVersion(manifest.Version, current))
            return $"已是最新版本（当前 {current}，远端 {manifest.Version}）。";
        return $"有新版本：{manifest.Version}（当前 {current}）"
             + (manifest.Notes is { Length: > 0 } n ? $"\n更新说明：{Cut(n, 300)}" : "")
             + "\n安装位置与方式由启动器自更新流程处理（设置页 → 检查更新）。";
    }

    private Task<string> MultiplayerStatusAsync(JsonElement args, CancellationToken ct)
    {
        var mp = _c.Multiplayer!;
        var sb = new StringBuilder();
        sb.AppendLine($"当前提供方：{mp.Active.Id}（可用：{string.Join("、", mp.Providers.Select(p => p.Id))}）");
        if (mp.Snapshot is { } snap)
        {
            sb.AppendLine($"状态：{snap.State.Phase}"
                + (snap.State.IsBusy ? "（进行中）" : ""));
            sb.AppendLine($"核心：{(snap.Core.Installed ? $"已安装 {snap.Core.Version}" : "未安装（用 install_multiplayer_core 装）")}"
                + $"{(snap.Core.SessionRunning ? "，会话在跑" : "")}");
            if (snap.State.Room is { Length: > 0 } room)
                sb.AppendLine($"房间：{room}（{snap.State.Players?.Count ?? 0} 人）");
            if (snap.State.Address is { Length: > 0 } addr) sb.AppendLine($"虚拟局域网地址：{addr}");
            if (snap.State.Error is { Length: > 0 } err) sb.AppendLine($"上次错误：{err}");
        }
        else sb.Append("还没有会话，用 multiplayer_host 建房或 multiplayer_join 加入。");
        return Task.FromResult(sb.ToString());
    }
}
