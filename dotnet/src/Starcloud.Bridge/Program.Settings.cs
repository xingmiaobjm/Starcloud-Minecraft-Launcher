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

    private static async Task<object?> HandleSettingsGetAsync(TextWriter stdout, JsonElement p)
    {
            return SnapshotSettings();
    }

    private static async Task<object?> HandleSettingsApplyAsync(TextWriter stdout, JsonElement p)
    {
            if (p.TryGetProperty("gameDir", out var gd))
                _settings.DotMinecraft = gd.GetString() ?? _settings.DotMinecraft;
            if (p.TryGetProperty("javaExecutable", out var je))
                _settings.JavaExecutable = je.GetString() ?? _settings.JavaExecutable;
            if (p.TryGetProperty("maxMemoryMb", out var mm) && mm.TryGetInt32(out var mb))
                _settings.DefaultMaxMemoryMb = mb;
            if (p.TryGetProperty("downloadSource", out var ds))
                _settings.DownloadSource = ds.GetString() ?? _settings.DownloadSource;
            if (p.TryGetProperty("minMemoryMb", out var mn) && mn.TryGetInt32(out var mnv))
                _settings.DefaultMinMemoryMb = mnv;
            if (p.TryGetProperty("downloadConcurrency", out var dc) && dc.TryGetInt32(out var dcv))
                _settings.DownloadConcurrency = Math.Clamp(dcv, 1, 64);
            if (p.TryGetProperty("downloadMaxRetries", out var dr) && dr.TryGetInt32(out var drv))
                _settings.DownloadMaxRetries = Math.Clamp(drv, 0, 10);
            if (p.TryGetProperty("downloadCustomMirrorUrl", out var dm))
                _settings.DownloadCustomMirrorUrl = dm.GetString() ?? "";
            if (p.TryGetProperty("versionIsolated", out var vi) &&
                (vi.ValueKind == JsonValueKind.True || vi.ValueKind == JsonValueKind.False))
                _settings.VersionIsolated = vi.GetBoolean();
            if (p.TryGetProperty("useBuiltinJvmArgs", out var uj) &&
                (uj.ValueKind == JsonValueKind.True || uj.ValueKind == JsonValueKind.False))
                _settings.UseBuiltinJvmArgs = uj.GetBoolean();
            if (p.TryGetProperty("extraJvmArgs", out var ea) && ea.ValueKind == JsonValueKind.Array)
                _settings.ExtraJvmArgs = ea.EnumerateArray()
                    .Where(x => x.ValueKind == JsonValueKind.String)
                    .Select(x => x.GetString()!)
                    .Where(x => x.Length > 0)
                    .ToList();
            if (p.TryGetProperty("language", out var lg))
                _settings.Language = lg.GetString() ?? _settings.Language;
            if (p.TryGetProperty("theme", out var th))
                _settings.Theme = th.GetString() ?? _settings.Theme;
            if (p.TryGetProperty("accountWalkAnimation", out var aw) &&
                (aw.ValueKind == JsonValueKind.True || aw.ValueKind == JsonValueKind.False))
                _settings.AccountWalkAnimation = aw.GetBoolean();
            if (p.TryGetProperty("microsoftLoginEmbedded", out var ml) &&
                (ml.ValueKind == JsonValueKind.True || ml.ValueKind == JsonValueKind.False))
                _settings.MicrosoftLoginEmbedded = ml.GetBoolean();
            if (p.TryGetProperty("curseforgeApiKey", out var cf))
                _settings.CurseForgeApiKey = cf.GetString() ?? "";
            if (p.TryGetProperty("windowWidth", out var ww) && ww.TryGetInt32(out var wwv))
                _settings.DefaultWindowWidth = Math.Clamp(wwv, 320, 7680);
            if (p.TryGetProperty("windowHeight", out var wh) && wh.TryGetInt32(out var whv))
                _settings.DefaultWindowHeight = Math.Clamp(whv, 240, 4320);
            if (p.TryGetProperty("fullscreen", out var fs2) && IsBool(fs2))
                _settings.DefaultFullscreen = fs2.GetBoolean();
            if (p.TryGetProperty("globalEnvVars", out var gev) && gev.ValueKind == JsonValueKind.Object)
            {
                var env = new Dictionary<string, string>(StringComparer.Ordinal);
                foreach (var kv in gev.EnumerateObject())
                    if (kv.Value.ValueKind == JsonValueKind.String)
                        env[kv.Name] = kv.Value.GetString() ?? "";
                _settings.GlobalEnvVars = env;
            }
            if (p.TryGetProperty("launchAutoRetry", out var lar) && IsBool(lar))
                _settings.LaunchAutoRetry = lar.GetBoolean();
            if (p.TryGetProperty("showGameLogOnLaunch", out var sgl) && IsBool(sgl))
                _settings.ShowGameLogOnLaunch = sgl.GetBoolean();
            if (p.TryGetProperty("novaOverlayEnabled", out var noe) && IsBool(noe))
                _settings.NovaOverlayEnabled = noe.GetBoolean();
            if (p.TryGetProperty("resourceCacheEnabled", out var rce) && IsBool(rce))
                _settings.ResourceCacheEnabled = rce.GetBoolean();
            if (p.TryGetProperty("motionLevel", out var mvl))
                _settings.UiMotionLevel = mvl.GetString() ?? "";
            if (p.TryGetProperty("backgroundMode", out var bgm))
                _settings.BackgroundMode = bgm.GetString() ?? "none";
            if (p.TryGetProperty("backgroundBuiltinId", out var bgi))
                _settings.BackgroundBuiltinId = bgi.GetString() ?? "";
            if (p.TryGetProperty("backgroundImagePath", out var bgp))
                _settings.BackgroundImagePath = bgp.GetString() ?? "";
            if (p.TryGetProperty("backgroundImageOpacity", out var bgo) && bgo.TryGetDouble(out var bgov))
                _settings.BackgroundImageOpacity = Math.Clamp(bgov, 0, 1);
            if (p.TryGetProperty("backgroundBlurRadius", out var bgb) && bgb.TryGetDouble(out var bgbv))
                _settings.BackgroundBlurRadius = Math.Clamp(bgbv, 0, 64);
            if (p.TryGetProperty("backgroundMaskOpacity", out var bgk) && bgk.TryGetDouble(out var bgkv))
                _settings.BackgroundMaskOpacity = Math.Clamp(bgkv, 0, 1);
            if (p.TryGetProperty("backgroundFollowTheme", out var bgf) && IsBool(bgf))
                _settings.BackgroundFollowTheme = bgf.GetBoolean();
            if (p.TryGetProperty("deviceLinkEnabled", out var dle) && IsBool(dle))
                _settings.DeviceLinkEnabled = dle.GetBoolean();
            if (p.TryGetProperty("deviceName", out var dvn))
                _settings.DeviceName = dvn.GetString() ?? "";
            if (p.TryGetProperty("p2pDownloadEnabled", out var p2p) && IsBool(p2p))
                _settings.P2pDownloadEnabled = p2p.GetBoolean();
            if (p.TryGetProperty("p2pInternetEnabled", out var p2pi) && IsBool(p2pi))
                _settings.P2pInternetEnabled = p2pi.GetBoolean();
            if (p.TryGetProperty("p2pInternetUploadMbps", out var p2pu) &&
                p2pu.TryGetInt32(out var p2puv))
                _settings.P2pInternetUploadMbps = Math.Clamp(p2puv, 1, 32);
            if (p.TryGetProperty("deviceP2pCrossAccount", out var p2px) && IsBool(p2px))
                _settings.DeviceP2PCrossAccount = p2px.GetBoolean();
            if (p.TryGetProperty("microsoftTokenPersist", out var mtp) && IsBool(mtp))
                _settings.MicrosoftTokenPersist = mtp.GetBoolean();
            if (p.TryGetProperty("autoUpdateEnabled", out var aue) && IsBool(aue))
                _settings.AutoUpdateEnabled = aue.GetBoolean();
            if (p.TryGetProperty("updateManifestUrl", out var umu))
                _settings.UpdateManifestUrl = umu.GetString() ?? "";
            _settings.Save();
            return SnapshotSettings();
    }

    private static async Task<object?> HandleSettingsClearCacheAsync(TextWriter stdout, JsonElement p)
    {
            _resourceCache.ClearAll();
            return new { ok = true };
    }

    private static async Task<object?> HandleSettingsCheckUpdateAsync(TextWriter stdout, JsonElement p)
    {
            var manifest = await _selfUpdate.FetchManifestAsync();
            return new
            {
                version = manifest.Version,
                notes = manifest.Notes,
                notesEn = manifest.NotesEn,
                publishedAt = manifest.PublishedAt,
                capability = SelfUpdateService.Capability.ToString(),
                current = LauncherVersion,
                hasUpdate = !string.IsNullOrEmpty(manifest.Version) &&
                             !string.Equals(manifest.Version, LauncherVersion, StringComparison.Ordinal),
                downloadUrl = _selfUpdate.ManifestUrl,
            };
    }

    private static object SnapshotSettings() => new
    {
        gameDir = _settings.DotMinecraft,
        javaExecutable = _settings.JavaExecutable,
        minMemoryMb = _settings.DefaultMinMemoryMb,
        maxMemoryMb = _settings.DefaultMaxMemoryMb,
        downloadSource = _settings.DownloadSource,
        downloadConcurrency = _settings.DownloadConcurrency,
        downloadMaxRetries = _settings.DownloadMaxRetries,
        downloadCustomMirrorUrl = _settings.DownloadCustomMirrorUrl ?? "",
        versionIsolated = _settings.VersionIsolated,
        useBuiltinJvmArgs = _settings.UseBuiltinJvmArgs,
        extraJvmArgs = _settings.ExtraJvmArgs,
        language = _settings.Language,
        theme = _settings.Theme,
        accountWalkAnimation = _settings.AccountWalkAnimation,
        microsoftLoginEmbedded = _settings.MicrosoftLoginEmbedded,
        curseforgeApiKey = _settings.CurseForgeApiKey,
        windowWidth = _settings.DefaultWindowWidth ?? 854,
        windowHeight = _settings.DefaultWindowHeight ?? 480,
        fullscreen = _settings.DefaultFullscreen,
        globalEnvVars = _settings.GlobalEnvVars,
        launchAutoRetry = _settings.LaunchAutoRetry,
        showGameLogOnLaunch = _settings.ShowGameLogOnLaunch,
        novaOverlayEnabled = _settings.NovaOverlayEnabled,
        resourceCacheEnabled = _settings.ResourceCacheEnabled,
        motionLevel = _settings.UiMotionLevel,
        backgroundMode = _settings.BackgroundMode,
        backgroundBuiltinId = _settings.BackgroundBuiltinId ?? "",
        backgroundImagePath = _settings.BackgroundImagePath ?? "",
        backgroundImageOpacity = _settings.BackgroundImageOpacity,
        backgroundBlurRadius = _settings.BackgroundBlurRadius,
        backgroundMaskOpacity = _settings.BackgroundMaskOpacity ?? 0,
        backgroundFollowTheme = _settings.BackgroundFollowTheme,
        deviceLinkEnabled = _settings.DeviceLinkEnabled,
        deviceName = _settings.DeviceName,
        p2pDownloadEnabled = _settings.P2pDownloadEnabled,
        p2pInternetEnabled = _settings.P2pInternetEnabled,
        p2pInternetUploadMbps = _settings.P2pInternetUploadMbps,
        deviceP2pCrossAccount = _settings.DeviceP2PCrossAccount,
        microsoftTokenPersist = _settings.MicrosoftTokenPersist,
        autoUpdateEnabled = _settings.AutoUpdateEnabled,
        updateManifestUrl = _settings.UpdateManifestUrl ?? "",
    };
}
