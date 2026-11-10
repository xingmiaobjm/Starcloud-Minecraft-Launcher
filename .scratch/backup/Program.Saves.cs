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

    private static async Task<object?> HandleSavesIconAsync(TextWriter stdout, JsonElement p)
    {
            // 世界缩略图是本地文件，渲染层（Electron）读不到文件系统，
            // 这里读成 data URL 回传，避免为图片放开 file:// CSP。
            var iconPath = p.GetProperty("path").GetString()!;
            var full = LabSafePath(iconPath);
            var bytes = File.ReadAllBytes(full);
            var mime = full.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
                ? "image/png" : "image/jpeg";
            return new { dataUrl = $"data:{mime};base64,{Convert.ToBase64String(bytes)}" };
    }

    private static async Task<object?> HandleSavesListAsync(TextWriter stdout, JsonElement p)
    {
            var list = _saveLibrary.ListAll(_settings.DotMinecraft, _settings);
            return new
            {
                saves = list.Select(s => new
                {
                    s.FolderName,
                    s.LevelName,
                    lastPlayed = s.LastPlayed?.ToString("O"),
                    s.SizeBytes,
                    s.IconPath,
                    s.VersionId,
                    s.Isolated,
                    s.GameRoot,
                }),
            };
    }

    private static async Task<object?> HandleSavesOpenFolderAsync(TextWriter stdout, JsonElement p)
    {
            var (root, folder) = SaveTarget(p);
            var dir = Path.Combine(root, "saves", folder);
            if (!Directory.Exists(dir))
                throw new Exception($"存档目录不存在：{folder}");
            OpenFolder(dir);
            return new { ok = true, path = dir };
    }

    private static async Task<object?> HandleSavesBackupAsync(TextWriter stdout, JsonElement p)
    {
            var (root, folder) = SaveTarget(p);
            var backupsDir = Path.Combine(_settings.DotMinecraft, "backups");
            var zip = _saves.Backup(root, backupsDir, folder);
            return new { ok = true, path = zip, sizeBytes = new FileInfo(zip).Length };
    }

    private static async Task<object?> HandleSavesDeleteAsync(TextWriter stdout, JsonElement p)
    {
            var (root, folder) = SaveTarget(p);
            if (Sessions.Values.Any(s => !s.HasExited))
                throw new Exception("游戏正在运行，请先关闭游戏再删除存档");
            var dir = Path.Combine(root, "saves", folder);
            if (!Directory.Exists(dir))
                throw new Exception($"存档目录不存在：{folder}");
            long freed = 0;
            try
            {
                freed = new DirectoryInfo(dir)
                    .EnumerateFiles("*", SearchOption.AllDirectories)
                    .Sum(f => f.Length);
            }
            catch { /* 统计失败不影响删除 */ }
            _saves.Delete(root, folder);
            return new { ok = true, freedBytes = freed };
    }

    private static async Task<object?> HandleSavesExportShareAsync(TextWriter stdout, JsonElement p)
    {
            var (shareRoot, shareFolder) = SaveTarget(p);
            var dest = p.GetProperty("destPath").GetString()!;
            var zip = _saves.ExportForShare(shareRoot, shareFolder, dest);
            return new { ok = true, path = zip, sizeBytes = new FileInfo(zip).Length };
    }

    private static async Task<object?> HandleSavesImportShareAsync(TextWriter stdout, JsonElement p)
    {
            var zip = p.GetProperty("zipPath").GetString()!;
            if (!SaveService.IsValidSharePackage(zip))
                throw new Exception("这不是有效的世界分享包（zip 根目录缺少 level.dat）");
            var importRoot = p.TryGetProperty("gameRoot", out var irEl) &&
                            irEl.ValueKind == JsonValueKind.String
                ? irEl.GetString()!
                : _settings.DotMinecraft;
            var folder = _saves.ImportShared(zip, importRoot);
            return new { ok = true, folderName = folder };
    }

    private static async Task<object?> HandleSavesCopyToVersionAsync(TextWriter stdout, JsonElement p)
    {
            var (copyFromRoot, copyFolder) = SaveTarget(p);
            var targetVersion = p.GetProperty("targetVersionId").GetString()!;
            if (!InstanceLayout.IsValidVersionId(targetVersion))
                throw new Exception("非法版本 id");
            var store = new InstanceSettingsStore(_settings.DotMinecraft);
            var targetIsolated = InstanceLayout.IsIsolated(store.Get(targetVersion), _settings);
            var targetRoot = InstanceLayout.GameRoot(_settings.DotMinecraft, targetVersion, targetIsolated);
            if (string.Equals(targetRoot, copyFromRoot, StringComparison.OrdinalIgnoreCase))
                throw new Exception("目标版本与源世界使用同一游戏目录，无需复制");
            var srcDir = Path.Combine(copyFromRoot, "saves", copyFolder);
            if (!Directory.Exists(srcDir))
                throw new Exception($"存档目录不存在：{copyFolder}");
            var targetSaves = Path.Combine(targetRoot, "saves");
            Directory.CreateDirectory(targetSaves);
            var finalName = NextAvailableDir(targetSaves, $"{copyFolder}-来自 {targetVersion}");
            var dstDir = Path.Combine(targetSaves, finalName);
            try
            {
                CopyDirectory(srcDir, dstDir);
            }
            catch
            {
                try { if (Directory.Exists(dstDir)) Directory.Delete(dstDir, true); } catch { }
                throw;
            }
            return new
            {
                ok = true,
                folderName = finalName,
                gameRoot = targetRoot,
                versionId = targetVersion,
            };
    }

    /// <summary>校验存档操作目标：根目录必须在游戏目录内，目录名不得含路径符号。</summary>
    private static (string Root, string Folder) SaveTarget(JsonElement p)
    {
        var root = NormalizeGameRoot(p.GetProperty("gameRoot").GetString()!);
        var folder = p.GetProperty("folderName").GetString()!;
        if (string.IsNullOrWhiteSpace(folder) || folder is "." or ".." ||
            folder.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new Exception("非法存档目录名");
        return (root, folder);
    }

    private static string NormalizeGameRoot(string gameRoot)
    {
        var full = Path.GetFullPath(gameRoot);
        var dotMc = Path.GetFullPath(_settings.DotMinecraft);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!string.Equals(full, dotMc, comparison) &&
            !full.StartsWith(dotMc + Path.DirectorySeparatorChar, comparison))
            throw new Exception("存档根目录不在游戏目录内");
        return full;
    }
}
