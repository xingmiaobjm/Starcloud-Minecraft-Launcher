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

    private static async Task RunInstallAsync(TextWriter stdout, int id, JsonElement p)
    {
        var versionId = p.GetProperty("versionId").GetString()!;
        try
        {
            var entry = await FindManifestEntryAsync(versionId)
                        ?? throw new Exception($"版本 {versionId} 不在官方清单里");
            var progress = new Progress<BatchProgress>(b => Emit(stdout, "install.progress", new
            {
                versionId,
                stage = b.Stage,
                detail = $"{b.DoneFiles}/{b.TotalFiles}",
                current = b.DoneFiles,
                total = b.TotalFiles,
                percent = b.Percentage,
                speedBytesPerSec = b.SpeedBytesPerSec,
            }));
            await _installer.InstallVanillaAsync(versionId, entry.Url, _settings, progress);
            Emit(stdout, "install.done", new { versionId });
            Reply(stdout, id, ok: true, result: new { versionId, installed = true });
        }
        catch (Exception e)
        {
            Emit(stdout, "install.failed", new { versionId, error = e.Message });
            Reply(stdout, id, ok: false, error: e.Message);
        }
    }

    private static async Task<ManifestVersion?> FindManifestEntryAsync(string versionId)
    {
        var manifest = await _manifest.GetManifestAsync(forceRefresh: false);
        return manifest.Versions.FirstOrDefault(v => v.Id == versionId);
    }
}
