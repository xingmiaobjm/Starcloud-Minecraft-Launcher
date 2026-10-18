// Nova Agent 工具注册表：LLM 通过 function calling 真正动手配置启动器。
//
// 信任边界（不可跳过）：
//   · 只读工具（ReadOnly=true）查询类立即执行；
//   · 写操作工具只生成参数，由 NovaAgentLoop 攒成 NovaPlan 确认清单，用户点头后才执行。
// 全部复用现有服务，工具本身不做新业务逻辑；删文件、装东西、启动游戏一律走既有服务。
//
// 工具面覆盖启动器的全部功能（版本 / 资源 / 实例 / 存档 / 备份 / 诊断 / 联机 / 录屏 / 插件 / 外观），
// 拆成 NovaToolsRead.cs 与 NovaToolsWrite.cs 两块，注册表本体只负责装配与统一收口。
// 设计：docs/designs/2026-09-14-nova-agent-and-quilt.md。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>Nova 可调用的一个工具</summary>
public sealed record NovaTool(
    string Name,
    string DisplayName,
    string Description,
    string ParametersJson,
    bool ReadOnly,
    Func<JsonElement, CancellationToken, Task<string>> ExecuteAsync);

public sealed partial class NovaToolRegistry
{
    /// <summary>单个工具返回给模型的字符上限（防 token 爆炸）</summary>
    private const int MaxResultChars = 900;

    private readonly NovaToolContext _c;

    /// <summary>全部注册工具（顺序即发给 LLM 的顺序：先只读，后写操作）</summary>
    public IReadOnlyList<NovaTool> Tools { get; }

    /// <param name="readOnlyOnly">
    /// 只注册只读工具（跨端最小实现用：没有 UI 确认清单执行入口时，避免模型提出
    /// 无法落地的写操作）。默认 false，与 Windows 端行为一致。
    /// </param>
    public NovaToolRegistry(NovaToolContext context, bool readOnlyOnly = false)
    {
        _c = context;
        Tools = readOnlyOnly
            ? [.. BuildReadTools()]
            : [.. BuildReadTools(), .. BuildWriteTools()];
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

    private static double? Num(JsonElement args, string key) =>
        args.ValueKind == JsonValueKind.Object &&
        args.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) ? d : null;

    private static bool? Bool(JsonElement args, string key)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(key, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => null,
        };
    }

    private static List<string> StrList(JsonElement args, string key)
    {
        var list = new List<string>();
        if (args.ValueKind != JsonValueKind.Object ||
            !args.TryGetProperty(key, out var v) || v.ValueKind != JsonValueKind.Array)
            return list;
        foreach (var item in v.EnumerateArray())
            if (item.ValueKind == JsonValueKind.String && item.GetString() is { Length: > 0 } s)
                list.Add(s);
        return list;
    }

    private static string Require(string? value, string what) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException($"缺少 {what}")
            : value.Trim();

    // ── 路径 / 版本解析 ──

    private LauncherSettings S => _c.Settings();

    private string DotMc => S.DotMinecraft;

    /// <summary>目标版本：显式参数优先，其次当前选中版本</summary>
    private string? TargetVersion(JsonElement args) =>
        Str(args, "version_id") ?? Str(args, "target_version") ?? S.LastVersion;

    private List<string> InstalledVersions()
    {
        var result = new List<string>();
        var dir = Path.Combine(DotMc, "versions");
        if (!Directory.Exists(dir)) return result;
        foreach (var d in Directory.EnumerateDirectories(dir))
        {
            var id = Path.GetFileName(d);
            if (File.Exists(Path.Combine(d, id + ".json"))) result.Add(id);
        }
        return result.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>实例生效游戏目录（按隔离档位）</summary>
    private string GameRootOf(string versionId)
    {
        var profile = new InstanceSettingsStore(DotMc).Get(versionId);
        return InstanceLayout.GameRoot(DotMc, versionId, InstanceLayout.IsIsolated(profile, S));
    }

    /// <summary>文件尾部若干行（文件不存在返回空串）</summary>
    private static string TailLines(string path, int lines)
    {
        try
        {
            if (!File.Exists(path)) return "";
            var all = File.ReadAllLines(path);
            return string.Join('\n', all.TakeLast(Math.Max(1, lines)));
        }
        catch (Exception ex) { return $"（读取失败：{ex.Message}）"; }
    }

    /// <summary>游戏目录下最新的崩溃报告文件（没有返回 null）</summary>
    private static string? LatestCrashReport(string gameRoot)
    {
        var dir = Path.Combine(gameRoot, "crash-reports");
        if (!Directory.Exists(dir)) return null;
        return new DirectoryInfo(dir).GetFiles("*.txt")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    private static string OnOff(bool value) => value ? "开" : "关";

    private static string Cut(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    // ── 加载器安装辅助（与下载页走同一套规则） ──

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

    /// <summary>
    /// 实例 → 原版版本号，沿 inheritsFrom 找根并校验格式；
    /// 目录名是用户起的名字（不是版本号）时返回 null —— 调用方按「不筛选版本」处理。
    /// </summary>
    private static string? RootMcVersion(string dotMc, string versionId) =>
        InstanceVersion.BaseGameVersion(dotMc, versionId);

    /// <summary>Fabric/Quilt 缺省构建：第一个稳定版，没有就第一个</summary>
    private async Task<string> PickLoaderBuildAsync(string mc, string loader, CancellationToken ct)
    {
        if (_c.Fabric is null) throw new InvalidOperationException("当前宿主没有 Fabric 服务");
        var list = loader == "fabric"
            ? await _c.Fabric.ListLoadersAsync(mc, ct)
            : await _c.Fabric.ListQuiltLoadersAsync(mc, ct);
        return list.FirstOrDefault(l => l.Loader?.Stable != false)?.Version
            ?? list.FirstOrDefault()?.Version
            ?? throw new InvalidOperationException($"{mc} 没有 {loader} 可用构建");
    }

    /// <summary>Forge/NeoForge 缺省构建：推荐 &gt; 最新 &gt; 第一个</summary>
    private async Task<string> PickForgeBuildAsync(string mc, string loader, CancellationToken ct)
    {
        if (_c.Forge is null) throw new InvalidOperationException("当前宿主没有 Forge 服务");
        var list = loader == "forge"
            ? await _c.Forge.ListForgeBuildsAsync(mc, ct)
            : await _c.Forge.ListNeoForgeBuildsAsync(mc, ct);
        return list.FirstOrDefault(b => b.Note == "推荐")?.Version
            ?? list.FirstOrDefault()?.Version
            ?? throw new InvalidOperationException($"{mc} 没有 {loader} 可用构建");
    }

    /// <summary>加载器安装所需的 Java 大版本：已装原版取版本 JSON，否则按年份启发式</summary>
    private async Task<int> RequireJavaMajorAsync(string mc, CancellationToken ct)
    {
        try
        {
            var v = await _c.Installer.LoadVersionAsync(S.DotMinecraft, mc, ct);
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
