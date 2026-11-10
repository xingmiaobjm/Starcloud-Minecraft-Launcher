using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core;
using Starcloud.Core.Models;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Multiplayer;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Bridge;

public static partial class Program
{

    private static async Task<object?> HandleCatalogVersionsAsync(TextWriter stdout, JsonElement p)
    {
            var force = p.TryGetProperty("force", out var f) && f.GetBoolean();
            var manifest = await _manifest.GetManifestAsync(force);
            var installed = _manifest.ListInstalled(_settings.DotMinecraft);
            var installedIds = installed.Select(i => i.Id).ToHashSet();
            return new
            {
                remote = manifest.Versions.Select(v => new
                {
                    v.Id,
                    v.Type,
                    v.Url,
                    v.ReleaseTime,
                    installed = installedIds.Contains(v.Id),
                }),
                installed = installed.Select(i => new { i.Id, i.SizeBytes }),
            };
    }

    private static async Task<object?> HandleCatalogInstalledAsync(TextWriter stdout, JsonElement p)
    {
            var instances = new InstanceSettingsStore(_settings.DotMinecraft);
            return new
            {
                installed = _manifest
                    .ListInstalled(_settings.DotMinecraft)
                    .Select(i => DescribeInstalled(i, instances)),
            };
    }

    private static object VersionSettingsPayload(string versionId)
    {
        if (!InstanceLayout.IsValidVersionId(versionId))
            throw new Exception("非法版本 id");
        var dotMc = _settings.DotMinecraft;
        var profile = new InstanceSettingsStore(dotMc).Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _settings);
        var gameRoot = InstanceLayout.GameRoot(dotMc, versionId, isolated);
        var eff = InstanceLayout.Resolve(
            dotMc, versionId, profile, _settings, MemoryAdvisor.Probe(gameRoot));
        return new
        {
            versionId,
            isolation = profile.Isolation,
            memoryMode = profile.MemoryMode ?? "global",
            memoryMinMb = profile.MemoryMinMb,
            memoryMaxMb = profile.MemoryMaxMb,
            javaMode = profile.JavaMode ?? "global",
            javaPath = profile.JavaPath,
            windowMode = profile.WindowMode ?? "global",
            windowWidth = profile.WindowWidth,
            windowHeight = profile.WindowHeight,
            fullscreen = profile.Fullscreen,
            serverAddress = profile.ServerAddress,
            extraJvmArgs = profile.ExtraJvmArgs,
            extraGameArgs = profile.ExtraGameArgs,
            effective = new
            {
                gameRoot,
                isolated,
                minMem = eff.MinMem,
                maxMem = eff.MaxMem,
                java = eff.Java,
                windowW = eff.WindowW,
                windowH = eff.WindowH,
                fullscreen = eff.Fullscreen,
            },
        };
    }
    /// <summary>版本列表条目：目录/JSON/jar/依赖/默认/隔离状态一次给全，UI 不再自己猜。</summary>

    private static object DescribeInstalled(
        InstalledVersionInfo info, InstanceSettingsStore instances)
    {
        var dir = Path.Combine(_settings.DotMinecraft, "versions", info.Id);
        var json = Path.Combine(dir, info.Id + ".json");
        string? inheritsFrom = null;
        string? versionType = null;
        // 整合包的版本 JSON 常没有 inheritsFrom，加载器信息藏在 libraries 里
        // （net.neoforged:neoforge:...），所以按 Win 端口径扫描全文。
        string? jsonText = null;
        var jarExists = false;
        try
        {
            if (File.Exists(json))
            {
                var text = File.ReadAllText(json);
                jsonText = text;
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("inheritsFrom", out var inh) &&
                    inh.ValueKind == JsonValueKind.String)
                    inheritsFrom = inh.GetString();
                // 版本类型（release / snapshot / old_beta / old_alpha）：
                // 壳端据此选图标（草方块 / 紫水晶 / 圆石 / 泥土），与 Win 端口径一致。
                if (doc.RootElement.TryGetProperty("type", out var tp) &&
                    tp.ValueKind == JsonValueKind.String)
                    versionType = tp.GetString();
            }
            // 与 GameLauncher.ResolveClientJar 的回退口径一致：同名 jar 或目录内唯一客户端 jar。
            jarExists = File.Exists(Path.Combine(dir, info.Id + ".jar")) ||
                Directory.EnumerateFiles(dir, "*.jar").Any(f =>
                    !f.Contains("-sources", StringComparison.OrdinalIgnoreCase) &&
                    !f.Contains("-javadoc", StringComparison.OrdinalIgnoreCase) &&
                    !f.Contains("-dev", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            // 单个版本读失败不影响整个列表
        }

        return new
        {
            info.Id,
            sizeBytes = info.SizeBytes,
            directory = dir,
            jsonPath = json,
            jarExists,
            inheritsFrom,
            loader = DetectLoader(info.Id, inheritsFrom, jsonText),
            type = versionType ?? InferVersionType(info.Id),
            lastPlayedAt = instances.Get(info.Id).LastPlayedAt?.ToString("O"),
            isDefault = _settings.LastVersion == info.Id,
            isolated = _settings.VersionIsolated,
            favorite = instances.Get(info.Id).Favorite,
            pinned = instances.Get(info.Id).Pinned,
            iconPath = instances.Get(info.Id).IconPath ?? "",
        };
    }

    /// <summary>从版本 id 推断加载器（顺序敏感：neoforge 必须先于 forge）。</summary>

    private static string DetectLoader(string id, string? inheritsFrom, string? jsonText)
    {
        var lower = (id + " " + (inheritsFrom ?? "") + " " + (jsonText ?? "")).ToLowerInvariant();
        if (lower.Contains("neoforge")) return "neoforge";
        if (lower.Contains("forge")) return "forge";
        if (lower.Contains("fabric")) return "fabric";
        if (lower.Contains("quilt")) return "quilt";
        return "vanilla";
    }

    /// <summary>版本 JSON 缺 type 时的兜底推断。</summary>

    private static string InferVersionType(string id)
    {
        var lower = id.ToLowerInvariant();
        if (lower.Contains("-rc") || lower.Contains("-pre") || lower.Contains("-snapshot"))
            return "snapshot";
        return "release";
    }
}
