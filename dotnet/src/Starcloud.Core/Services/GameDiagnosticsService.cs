// 游戏自检与纠错：版本 JSON / client jar / 依赖父版本 / 资源索引完整性检查。
// 原版与 Fabric 版本可自动修复（重装补齐文件）；Forge/NeoForge 引导到下载页重装。

using System.Security.Cryptography;
using System.Text.Json;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

/// <summary>单个诊断问题（AutoFixable 表示可通过 RepairAsync 自动修复）</summary>
public sealed record DiagIssue(string VersionId, string Kind, string Detail, bool AutoFixable);

/// <summary>诊断汇总</summary>
public sealed record DiagReport(int ScannedCount, List<DiagIssue> Issues);

public sealed class GameDiagnosticsService
{
    private readonly InstallerService _installer;
    private readonly ManifestService _manifest;

    public GameDiagnosticsService(InstallerService installer, ManifestService manifest)
    {
        _installer = installer;
        _manifest = manifest;
    }

    /// <summary>扫描 .minecraft/versions 下全部已安装版本的完整性</summary>
    public async Task<DiagReport> DiagnoseAsync(string dotMc, CancellationToken ct = default)
    {
        var issues = new List<DiagIssue>();
        var versionsDir = Path.Combine(dotMc, "versions");
        if (!Directory.Exists(versionsDir)) return new DiagReport(0, issues);

        var scanned = 0;
        foreach (var dir in Directory.EnumerateDirectories(versionsDir))
        {
            ct.ThrowIfCancellationRequested();
            var id = Path.GetFileName(dir);
            // 空目录/残留文件夹不算版本
            if (!File.Exists(_installer.VersionJsonPath(dotMc, id)) &&
                !File.Exists(_installer.VersionJarPath(dotMc, id)))
                continue;
            scanned++;
            await CheckVersionAsync(dotMc, id, issues, ct);
        }
        return new DiagReport(scanned, issues);
    }

    /// <summary>
    /// 自动修复：Fabric 版本按 loader 版本重装；原版从官方清单重装；
    /// Forge/NeoForge 不支持自动修复（引导用户到下载页）。
    /// </summary>
    public async Task<string> RepairAsync(
        string versionId, LauncherSettings settings, CancellationToken ct = default)
    {
        var dotMc = settings.DotMinecraft;
        const string fabricPrefix = "fabric-loader-";
        if (versionId.StartsWith(fabricPrefix, StringComparison.OrdinalIgnoreCase))
        {
            var tail = versionId[fabricPrefix.Length..].Split('-');
            if (tail.Length < 2)
                throw new InvalidOperationException($"无法从「{versionId}」识别 Fabric 版本号");
            await _installer.InstallFabricAsync(
                string.Join('-', tail[1..]), tail[0], settings, null, ct, targetId: versionId);
            return $"已重新安装 Fabric {tail[0]}（MC {string.Join('-', tail[1..])}），客户端与依赖文件已补齐";
        }

        if (versionId.Contains("forge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                "Forge/NeoForge 版本暂不支持自动修复，请到「下载」页重新安装");

        var manifest = await _manifest.GetManifestAsync(false, ct);
        var entry = manifest.Versions.FirstOrDefault(v => v.Id == versionId)
                    ?? throw new InvalidOperationException($"官方清单中找不到版本 {versionId}，无法自动修复");
        await _installer.InstallVanillaAsync(versionId, entry.Url, settings, null, ct);
        return $"已重新安装 {versionId}，客户端与依赖文件已补齐";
    }

    // ── 单版本检查 ──
    private async Task CheckVersionAsync(
        string dotMc, string id, List<DiagIssue> issues, CancellationToken ct)
    {
        var jsonPath = _installer.VersionJsonPath(dotMc, id);
        var jarPath = _installer.VersionJarPath(dotMc, id);

        if (!File.Exists(jsonPath))
        {
            issues.Add(new DiagIssue(id, "版本文件缺失", $"{id}.json 不存在", true));
            return;
        }

        // 继承的父版本（如 Fabric 依赖原版）：先查，缺失则父检查无意义
        if (ReadInheritsFrom(jsonPath) is { } parent && !_installer.IsInstalled(dotMc, parent))
        {
            issues.Add(new DiagIssue(id, "依赖版本缺失", $"继承的原版 {parent} 未安装", true));
            return;
        }

        MinecraftVersion version;
        try
        {
            version = await _installer.LoadVersionAsync(dotMc, id, ct);
        }
        catch (Exception ex)
        {
            issues.Add(new DiagIssue(id, "版本文件损坏", $"JSON 解析失败：{ex.Message}", true));
            return;
        }

        if (!File.Exists(jarPath))
            issues.Add(new DiagIssue(id, "客户端缺失", "client jar 不存在", true));
        else if (new FileInfo(jarPath).Length < 1024 * 1024)
            issues.Add(new DiagIssue(id, "客户端损坏", "client jar 体积异常（可能未下载完成）", true));
        else if (version.Downloads?.Client?.Sha1 is { Length: > 0 } sha &&
                 !string.Equals(await Sha1Async(jarPath, ct), sha, StringComparison.OrdinalIgnoreCase))
            issues.Add(new DiagIssue(id, "客户端校验失败", "client jar 与官方 SHA1 不一致", true));

        if (version.AssetIndex is { } index &&
            !File.Exists(Path.Combine(dotMc, "assets", "indexes", $"{index.Id}.json")))
            issues.Add(new DiagIssue(id, "资源索引缺失",
                $"assets/indexes/{index.Id}.json 不存在", true));
    }

    private static string? ReadInheritsFrom(string jsonPath)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jsonPath));
            return doc.RootElement.TryGetProperty("inheritsFrom", out var el) &&
                   el.ValueKind == JsonValueKind.String
                ? el.GetString()
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<string> Sha1Async(string path, CancellationToken ct)
    {
        await using var fs = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(fs, ct);
        return Convert.ToHexString(hash);
    }
}
