// Nova Agent 工具注册表：LLM 通过 function calling 真正动手配置启动器。
// 信任边界（不可跳过）：只读查询类工具立即执行；写操作类工具只生成参数，
// 由 NovaAgentLoop 攒成 NovaPlan 确认清单，用户点头后才真正执行（NovaPlan.ExecuteAsync 委托）。
// 全部复用现有服务，工具本身不做新业务逻辑。
// 设计：.trae/documents/2026-09-14-nova-agent-and-quilt.md 阶段三。

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

/// <summary>Nova 可调用的一个工具</summary>
public sealed record NovaTool(
    string Name,
    string DisplayName,
    string Description,
    string ParametersJson,
    bool ReadOnly,
    Func<JsonElement, CancellationToken, Task<string>> ExecuteAsync);

public sealed class NovaToolRegistry
{
    private const int MaxResultChars = 900;

    private readonly ManifestService _manifest;
    private readonly InstallerService _installer;
    private readonly ForgeInstallerService _forge;
    private readonly FabricService _fabric;
    private readonly ModrinthService _mods;
    private readonly ContentLibraryService _library;
    private readonly Func<LauncherSettings> _settings;
    private readonly Action? _saveSettings;

    /// <summary>全部注册工具（顺序即发给 LLM 的顺序）</summary>
    public IReadOnlyList<NovaTool> Tools { get; }

    public NovaToolRegistry(
        ManifestService manifest, InstallerService installer,
        ForgeInstallerService forge, FabricService fabric,
        ModrinthService mods, ContentLibraryService library,
        Func<LauncherSettings> settings, Action? saveSettings = null)
    {
        _manifest = manifest;
        _installer = installer;
        _forge = forge;
        _fabric = fabric;
        _mods = mods;
        _library = library;
        _settings = settings;
        _saveSettings = saveSettings;

        Tools =
        [
            new("search_modrinth", "搜索 Modrinth 资源",
                "在 Modrinth 搜索 mod/资源包/光影/数据包/整合包，返回标题、项目 ID、下载量和简介。安装用 install_content。",
                """{"type":"object","properties":{"query":{"type":"string","description":"搜索关键词"},"kind":{"type":"string","enum":["mod","resourcepack","shader","datapack","modpack"],"description":"资源类型，默认 mod"},"game_version":{"type":"string","description":"MC 版本过滤，如 1.21.1"},"loader":{"type":"string","enum":["fabric","forge","quilt","neoforge"]}},"required":["query"]}""",
                true, SearchModrinthAsync),

            new("list_versions", "列出游戏版本",
                "列出已安装的 Minecraft 版本和官方最新版本。不要臆造版本号，装版本前先看这里。",
                """{"type":"object","properties":{}}""",
                true, ListVersionsAsync),

            new("get_settings", "读取启动器设置",
                "读取启动器关键设置（游戏目录、内存、下载源、当前版本等）。",
                """{"type":"object","properties":{}}""",
                true, GetSettingsAsync),

            new("read_version_detail", "查看版本详情",
                "查看某个已安装版本的详情：Java 要求、实例设置、已装的 mod 列表。",
                """{"type":"object","properties":{"version_id":{"type":"string"}},"required":["version_id"]}""",
                true, ReadVersionDetailAsync),

            new("install_content", "安装内容",
                "把 Modrinth 资源（mod/资源包/光影/数据包）下载并装载到指定版本。需要 project_id（search_modrinth 的结果里拿）。",
                """{"type":"object","properties":{"project_id":{"type":"string","description":"Modrinth 项目 ID 或 slug"},"kind":{"type":"string","enum":["mod","resourcepack","shader","datapack"],"description":"资源类型，默认 mod"},"version_number":{"type":"string","description":"指定资源版本号，缺省取最新兼容版"},"target_version":{"type":"string","description":"装载到的游戏版本 ID，缺省为当前选中版本"}},"required":["project_id"]}""",
                false, InstallContentAsync),

            new("install_minecraft", "安装原版",
                "安装一个 Minecraft 原版版本。版本号必须来自 list_versions 的官方列表。",
                """{"type":"object","properties":{"version_id":{"type":"string","description":"官方版本 ID，如 1.21.1"}},"required":["version_id"]}""",
                false, InstallMinecraftAsync),

            new("install_loader", "安装加载器",
                "给指定 MC 版本安装 Forge/NeoForge/Fabric/Quilt。缺省构建号时自动选推荐版；Forge/NeoForge 会先补装原版。",
                """{"type":"object","properties":{"loader":{"type":"string","enum":["forge","neoforge","fabric","quilt"]},"mc_version":{"type":"string","description":"MC 版本，如 1.21.1"},"build":{"type":"string","description":"加载器构建号，缺省自动选推荐/最新稳定版"}},"required":["loader","mc_version"]}""",
                false, InstallLoaderAsync),

            new("update_settings", "修改启动器设置",
                "修改启动器设置（白名单字段）。只改用户明确同意的项。",
                """{"type":"object","properties":{"download_source":{"type":"string","enum":["auto","official","bmclapi","fastmirror","rainyun","custom"],"description":"下载源"},"default_min_memory_mb":{"type":"integer","description":"默认最小内存 MB"},"default_max_memory_mb":{"type":"integer","description":"默认最大内存 MB"},"last_version":{"type":"string","description":"切换当前选中的游戏版本"},"version_isolated":{"type":"boolean","description":"版本隔离开关"}}}""",
                false, UpdateSettingsAsync),
        ];
    }

    public NovaTool? Find(string name) =>
        Tools.FirstOrDefault(t => t.Name == name);

    /// <summary>执行一个工具并把结果截断到安全长度（防 token 爆炸）</summary>
    public async Task<string> ExecuteAsync(string name, string argumentsJson, CancellationToken ct = default)
    {
        var tool = Find(name) ?? throw new InvalidOperationException($"未知工具 {name}");
        JsonElement args = default;
        var trimmed = (argumentsJson ?? "").Trim();
        if (trimmed.Length > 0)
        {
            try { args = JsonDocument.Parse(trimmed).RootElement.Clone(); }
            catch { throw new InvalidOperationException("参数不是合法 JSON"); }
        }
        var result = await tool.ExecuteAsync(args, ct);
        return result.Length <= MaxResultChars ? result : result[..MaxResultChars] + "…（已截断）";
    }

    /// <summary>序列化成 OpenAI tools 参数（function calling 格式）</summary>
    public string ToOpenAiJson() =>
        JsonSerializer.Serialize(Tools.Select(t => new
        {
            type = "function",
            function = new
            {
                name = t.Name,
                description = t.Description,
                parameters = JsonSerializer.Deserialize<JsonElement>(t.ParametersJson),
            },
        }), JsonOpts);

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ── 参数读取小工具 ──

    private static string? Str(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static bool? Bool(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.True ? true :
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(key, out var f) &&
        f.ValueKind == JsonValueKind.False ? false : null;

    // ── 只读工具 ──

    private async Task<string> SearchModrinthAsync(JsonElement args, CancellationToken ct)
    {
        var query = Str(args, "query") ?? "";
        var kind = Str(args, "kind") ?? "mod";
        var loader = Str(args, "loader");
        var gameVersion = Str(args, "game_version");
        var hits = (await _mods.SearchAsync(query, kind, loader, gameVersion, 0, 6, ct: ct)).Hits;
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

    private async Task<string> ListVersionsAsync(JsonElement args, CancellationToken ct)
    {
        var s = _settings();
        var installed = ListInstalled(s.DotMinecraft);
        var manifest = await _manifest.GetManifestAsync(false, ct);
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

    private async Task<string> GetSettingsAsync(JsonElement args, CancellationToken ct)
    {
        var s = _settings();
        return string.Join("\n",
            $"游戏目录：{s.DotMinecraft}",
            $"当前版本：{s.LastVersion ?? "未选择"}",
            $"内存：{s.DefaultMinMemoryMb}~{s.DefaultMaxMemoryMb} MB",
            $"Java：{s.JavaExecutable ?? "自动选择"}",
            $"下载源：{s.DownloadSource}",
            $"版本隔离：{(s.VersionIsolated ? "开" : "关")}",
            $"语言：{s.Language}");
    }

    private async Task<string> ReadVersionDetailAsync(JsonElement args, CancellationToken ct)
    {
        var id = Str(args, "version_id");
        if (string.IsNullOrWhiteSpace(id)) return "缺少 version_id。先用 list_versions 看看装了哪些。";
        var s = _settings();
        var dotMc = s.DotMinecraft;
        var sb = new StringBuilder();

        try
        {
            var v = await _installer.LoadVersionAsync(dotMc, id, ct);
            sb.AppendLine($"版本 {id}：mainClass={v.MainClass}，Java={v.JavaVersion?.MajorVersion.ToString() ?? "自动"}");
        }
        catch
        {
            sb.AppendLine($"版本 {id}：JSON 不存在或已损坏（可能没装完）");
        }

        var profile = new InstanceSettingsStore(dotMc).Get(id);
        {
            var bits = new List<string>();
            if (profile.Isolation is { } iso) bits.Add($"隔离={iso}");
            if (profile.MemoryMaxMb is { } mem) bits.Add($"内存上限={mem}MB");
            if (profile.JavaPath is { } java) bits.Add($"Java={java}");
            if (profile.ServerAddress is { } addr) bits.Add($"自动连服={addr}");
            if (bits.Count > 0) sb.AppendLine("实例设置：" + string.Join("，", bits));
        }

        var isolated = InstanceLayout.IsIsolated(profile, s);
        var modsDir = Path.Combine(InstanceLayout.GameRoot(dotMc, id, isolated), "mods");
        if (Directory.Exists(modsDir))
        {
            var files = Directory.GetFiles(modsDir, "*.jar");
            sb.AppendLine($"已装 mod（{files.Length} 个）：{string.Join("、", files.Select(Path.GetFileNameWithoutExtension).Take(20))}");
        }
        else
        {
            sb.Append("mods 目录为空或不存在。");
        }
        return sb.ToString();
    }

    // ── 写操作工具（只在 NovaPlan 确认后执行） ──

    private async Task<string> InstallContentAsync(JsonElement args, CancellationToken ct)
    {
        var projectId = Str(args, "project_id") ?? throw new InvalidOperationException("缺少 project_id");
        var s = _settings();
        var target = Str(args, "target_version") ?? s.LastVersion;
        if (string.IsNullOrWhiteSpace(target))
            throw new InvalidOperationException("没有目标版本：请指定 target_version 或先选中一个版本");
        var kind = Str(args, "kind") ?? "mod";
        var wantNumber = Str(args, "version_number");

        var loader = DetectLoader(target);
        var gameVersion = RootMcVersion(s.DotMinecraft, target);
        var versions = await _mods.ListVersionsAsync(projectId, loader, gameVersion, ct);
        if (versions.Count == 0 && (loader is not null || gameVersion is not null))
            versions = await _mods.ListVersionsAsync(projectId, null, null, ct); // 放宽过滤兜底
        var chosen = wantNumber is { Length: > 0 } wn
            ? versions.FirstOrDefault(v => v.VersionNumber == wn) ?? versions.FirstOrDefault()
            : versions.FirstOrDefault();
        if (chosen?.PrimaryFile is not { } file)
            return $"在 Modrinth 上没找到 {projectId} 的可用版本"
                + (gameVersion is null ? "" : $"（{gameVersion}/{loader ?? "任意"}）") + "。";

        var title = await _mods.GetProjectTitleAsync(projectId, ct) ?? file.Filename;
        var info = new LibraryAdoptInfo(
            title, null, null, chosen.ProjectId, chosen.Id, chosen.VersionNumber,
            chosen.GameVersions, chosen.Loaders, file.Url);
        var entry = await _library.DownloadAndAdoptAsync(kind, file.Url, file.Filename, file.Sha1, info, ct);
        var dest = await _library.InstallToVersionAsync(entry, target, ct);
        return $"已安装 {title} {chosen.VersionNumber} 到版本 {target}（{Path.GetFileName(dest)}）。"
             + "建议提醒用户：重启游戏前可在版本详情里确认 mod 已生效。";
    }

    private async Task<string> InstallMinecraftAsync(JsonElement args, CancellationToken ct)
    {
        var versionId = Str(args, "version_id") ?? throw new InvalidOperationException("缺少 version_id");
        var s = _settings();
        var manifest = await _manifest.GetManifestAsync(false, ct);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
            ?? throw new InvalidOperationException($"官方列表里没有 {versionId}，先用 list_versions 查");
        await _installer.InstallVanillaAsync(versionId, entry.Url, s, null, ct);
        return $"原版 {versionId} 安装完成。";
    }

    private async Task<string> InstallLoaderAsync(JsonElement args, CancellationToken ct)
    {
        var loader = Str(args, "loader") ?? throw new InvalidOperationException("缺少 loader");
        var mc = Str(args, "mc_version") ?? throw new InvalidOperationException("缺少 mc_version");
        var build = Str(args, "build");
        var s = _settings();

        switch (loader)
        {
            case "fabric" or "quilt":
            {
                build ??= await PickLoaderBuildAsync(mc, loader, ct);
                if (loader == "fabric")
                {
                    await _installer.InstallFabricAsync(mc, build, s, null, ct);
                    return $"Fabric {build} @ {mc} 安装完成。";
                }
                await _installer.InstallQuiltAsync(mc, build, s, null, ct);
                return $"Quilt {build} @ {mc} 安装完成。";
            }
            case "forge" or "neoforge":
            {
                // Forge/NeoForge 官方安装器要求原版已安装 + 匹配的 Java，缺啥补啥
                if (!_installer.IsInstalled(s.DotMinecraft, mc))
                    await InstallMinecraftAsync(JsonDocument.Parse(
                        $$"""{"version_id":"{{mc}}"}""").RootElement, ct);

                build ??= await PickForgeBuildAsync(mc, loader, ct);
                var javaMajor = await RequireJavaMajorAsync(mc, ct);
                var url = loader == "forge"
                    ? ForgeInstallerService.ForgeInstallerUrl(mc, build)
                    : ForgeInstallerService.NeoForgeInstallerUrl(mc, build);
                await _forge.InstallViaInstallerAsync(url, mc, javaMajor, s, null, ct);
                return $"{(loader == "forge" ? "Forge" : "NeoForge")} {build} @ {mc} 安装完成。";
            }
            default:
                throw new InvalidOperationException($"不支持的加载器 {loader}");
        }
    }

    private async Task<string> UpdateSettingsAsync(JsonElement args, CancellationToken ct)
    {
        var s = _settings();
        var changed = new List<string>();
        if (Str(args, "download_source") is { } src) { s.DownloadSource = src; changed.Add($"下载源={src}"); }
        if (Int(args, "default_min_memory_mb") is { } min) { s.DefaultMinMemoryMb = min; changed.Add($"最小内存={min}MB"); }
        if (Int(args, "default_max_memory_mb") is { } max) { s.DefaultMaxMemoryMb = max; changed.Add($"最大内存={max}MB"); }
        if (Str(args, "last_version") is { } ver) { s.LastVersion = ver; changed.Add($"当前版本={ver}"); }
        if (Bool(args, "version_isolated") is { } iso) { s.VersionIsolated = iso; changed.Add($"版本隔离={(iso ? "开" : "关")}"); }
        if (changed.Count == 0) return "没有可识别的设置项，什么都没改。";
        _saveSettings?.Invoke();
        return "已修改并保存：" + string.Join("，", changed);
    }

    // ── 辅助 ──

    private List<string> ListInstalled(string dotMc)
    {
        var result = new List<string>();
        var dir = Path.Combine(dotMc, "versions");
        if (!Directory.Exists(dir)) return result;
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            var id = Path.GetFileName(d);
            if (File.Exists(Path.Combine(d, id + ".json"))) result.Add(id);
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>从版本 ID 猜加载器（forge 命名在 neoforge 之后判，避免误命中）</summary>
    private static string? DetectLoader(string versionId)
    {
        var lower = versionId.ToLowerInvariant();
        if (lower.Contains("neoforge")) return "neoforge";
        if (lower.Contains("forge")) return "forge";
        if (lower.Contains("fabric")) return "fabric";
        if (lower.Contains("quilt")) return "quilt";
        return null;
    }

    /// <summary>版本 JSON 沿 inheritsFrom 找根原版号（找不到就原样返回）</summary>
    private string? RootMcVersion(string dotMc, string versionId)
    {
        try
        {
            var path = _installer.VersionJsonPath(dotMc, versionId);
            if (!File.Exists(path)) return versionId;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.TryGetProperty("inheritsFrom", out var p) &&
                p.ValueKind == JsonValueKind.String)
                return p.GetString() ?? versionId;
        }
        catch { /* 读不到就不过滤 */ }
        return versionId;
    }

    /// <summary>Fabric/Quilt 缺省构建：第一个稳定版，没有就第一个</summary>
    private async Task<string> PickLoaderBuildAsync(string mc, string loader, CancellationToken ct)
    {
        var list = loader == "fabric"
            ? await _fabric.ListLoadersAsync(mc, ct)
            : await _fabric.ListQuiltLoadersAsync(mc, ct);
        return list.FirstOrDefault(l => l.Loader?.Stable != false)?.Version
            ?? list.FirstOrDefault()?.Version
            ?? throw new InvalidOperationException($"{mc} 没有 {loader} 可用构建");
    }

    /// <summary>Forge/NeoForge 缺省构建：推荐 > 最新 > 第一个</summary>
    private async Task<string> PickForgeBuildAsync(string mc, string loader, CancellationToken ct)
    {
        var list = loader == "forge"
            ? await _forge.ListForgeBuildsAsync(mc, ct)
            : await _forge.ListNeoForgeBuildsAsync(mc, ct);
        return list.FirstOrDefault(b => b.Note == "推荐")?.Version
            ?? list.FirstOrDefault()?.Version
            ?? throw new InvalidOperationException($"{mc} 没有 {loader} 可用构建");
    }

    /// <summary>加载器安装所需的 Java 大版本：已装原版取版本 JSON，否则按年份启发式</summary>
    private async Task<int> RequireJavaMajorAsync(string mc, CancellationToken ct)
    {
        try
        {
            var v = await _installer.LoadVersionAsync(_settings().DotMinecraft, mc, ct);
            if (v.JavaVersion?.MajorVersion is int major && major > 0) return major;
        }
        catch { /* 原版未装或 JSON 损坏 → 启发式 */ }
        // 1.20.5+ → 21，1.17~1.20.4 → 17，更老 → 8
        if (Version.TryParse(TrimRelease(mc), out var ver))
        {
            if (ver >= new Version(1, 20, 5)) return 21;
            if (ver >= new Version(1, 17)) return 17;
        }
        return 8;
    }

    /// <summary>剥掉版本号尾巴（如 "1.21.1-pre2" → "1.21.1"）便于 Version.TryParse</summary>
    private static string TrimRelease(string id)
    {
        var i = id.IndexOf('-');
        return i > 0 ? id[..i] : id;
    }
}
