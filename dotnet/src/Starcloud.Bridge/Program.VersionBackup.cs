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

    private static async Task<object?> HandleVersionsBackupListAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.TryGetProperty("versionId", out var blEl) && blEl.ValueKind == JsonValueKind.String
                ? blEl.GetString()
                : null;
            return new { entries = _versionBackup.List(_settings.DotMinecraft, versionId) };
    }

    private static async Task<object?> HandleVersionsBackupCreateAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var store = new InstanceSettingsStore(_settings.DotMinecraft);
            var isolated = InstanceLayout.IsIsolated(store.Get(versionId), _settings);
            var dirs = new List<string>();
            if (p.TryGetProperty("dirs", out var bdEl) && bdEl.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in bdEl.EnumerateArray())
                {
                    var dirName = d.GetString();
                    if (!string.IsNullOrWhiteSpace(dirName)) dirs.Add(dirName);
                }
            }
            var includeSaves = !p.TryGetProperty("includeSaves", out var isEl) || isEl.GetBoolean();
            var backupName = p.TryGetProperty("name", out var bnEl) && bnEl.ValueKind == JsonValueKind.String
                && bnEl.GetString() is { Length: > 0 } bn ? bn : "备份";
            var status = new Progress<string>(st =>
                Emit(stdout, "modpack.progress", new { stage = st, percent = -1 }));
            var entry = await _versionBackup.BackupAsync(_settings.DotMinecraft,
                new VersionBackupRequest(versionId, isolated, backupName, dirs, includeSaves), status);
            return new { ok = true, entry };
    }

    private static async Task<object?> HandleVersionsBackupRestoreAsync(TextWriter stdout, JsonElement p)
    {
            var entryId = p.GetProperty("entryId").GetString()!;
            var versionId = p.TryGetProperty("versionId", out var brEl) ? brEl.GetString() : null;
            var entry = _versionBackup.List(_settings.DotMinecraft, versionId)
                .FirstOrDefault(e => e.Id == entryId)
                ?? throw new Exception("找不到这条备份（索引可能已重建）。");
            var store = new InstanceSettingsStore(_settings.DotMinecraft);
            var isolated = InstanceLayout.IsIsolated(store.Get(entry.VersionId), _settings);
            var status = new Progress<string>(s =>
                Emit(stdout, "modpack.progress", new { stage = s, percent = -1 }));
            await _versionBackup.RestoreAsync(_settings.DotMinecraft, entry, isolated, status);
            return new { ok = true, entryId };
    }

    private static async Task<object?> HandleVersionsBackupDeleteAsync(TextWriter stdout, JsonElement p)
    {
            _versionBackup.Delete(_settings.DotMinecraft, p.GetProperty("entryId").GetString()!);
            return new { ok = true };
    }

    private static async Task<object?> HandleVersionsBackupRebuildAsync(TextWriter stdout, JsonElement p)
    {
            return new { ok = true, entries = _versionBackup.RebuildIndex(_settings.DotMinecraft) };
    }

    /// <summary>版本实例设置：档案原值 + 层叠后的生效值。</summary>
}
