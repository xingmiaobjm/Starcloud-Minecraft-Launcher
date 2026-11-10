using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;

namespace Starcloud.Bridge;

/// <summary>外部插件（v2 沙箱）宿主桥：状态文件、扫描、安装、UI 能力降级实现。</summary>
public static partial class Program
{
    // ── 外部插件（v2 沙箱）：清单/权限/安装/启停状态 ─────────────────────────

    private static readonly object PluginGate = new();

    private static readonly JsonSerializerOptions PluginStateJsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private sealed class PluginStateFile
    {
        [JsonPropertyName("plugins")]
        public Dictionary<string, PluginEntryState> Plugins { get; set; } = new();

        [JsonPropertyName("pageTipShown")]
        public bool PageTipShown { get; set; }
    }

    private sealed class PluginEntryState
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("disclaimerAccepted")] public bool DisclaimerAccepted { get; set; }
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("panelTipShown")] public bool PanelTipShown { get; set; }
    }

    private static PluginStateFile LoadPluginState()
    {
        try
        {
            if (File.Exists(LauncherPaths.PluginStateFile))
                return JsonSerializer.Deserialize<PluginStateFile>(
                    File.ReadAllText(LauncherPaths.PluginStateFile),
                    PluginStateJsonOpts) ?? new PluginStateFile();
        }
        catch
        {
            // 损坏按全新状态处理（与 Windows 端一致）
        }
        return new PluginStateFile();
    }

    private static void SavePluginState(PluginStateFile state)
    {
        Directory.CreateDirectory(LauncherPaths.PluginsDir);
        var tmp = LauncherPaths.PluginStateFile + ".tmp";
        File.WriteAllText(tmp, JsonSerializer.Serialize(state, PluginStateJsonOpts));
        File.Move(tmp, LauncherPaths.PluginStateFile, overwrite: true);
    }

    private static void SetPluginState(
        string id, Action<PluginEntryState> mutate, bool remove = false)
    {
        lock (PluginGate)
        {
            var state = LoadPluginState();
            if (remove)
            {
                state.Plugins.Remove(id);
            }
            else
            {
                if (!state.Plugins.TryGetValue(id, out var entry))
                    state.Plugins[id] = entry = new PluginEntryState();
                mutate(entry);
            }
            SavePluginState(state);
        }
    }

    private static bool IsPluginEnabled(string id) =>
        LoadPluginState().Plugins.TryGetValue(id, out var entry) && entry.Enabled;

    private static string SafePluginId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id is "." or ".." ||
            id.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new Exception("非法插件 id");
        return id;
    }

    private static List<(PluginManifest Manifest, string Dir, bool Official)> ScanPlugins()
    {
        var root = LauncherPaths.PluginUnpackedDir;
        var result = new List<(PluginManifest, string, bool)>();
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            var manifest = PluginManifest.ParseFile(manifestPath);
            if (manifest is null || manifest.Validate(LauncherVersion).Count > 0) continue;
            result.Add((manifest, dir, IsOfficialPlugin(manifest, dir, manifestPath)));
        }
        return result
            .OrderBy(p => p.Item1.Name, StringComparer.CurrentCulture)
            .ToList();
    }

    private static bool IsOfficialPlugin(
        PluginManifest manifest, string dir, string manifestPath)
    {
        if (!manifest.IsApi2) return manifest.VerifyOfficialSignature(manifestPath);
        var integrity = PluginIntegrity.ParseFile(
            Path.Combine(dir, PluginIntegrity.FileName));
        if (integrity is null || integrity.VerifyFiles(dir).Count > 0) return false;
        return integrity.Verify(out _) == PluginTrustState.Verified;
    }

    private static object PluginPayload(
        PluginManifest manifest, string dir, bool official, PluginStateFile state)
    {
        state.Plugins.TryGetValue(manifest.Id, out var entry);
        return new
        {
            id = manifest.Id,
            name = manifest.Name,
            version = manifest.Version,
            apiVersion = manifest.ApiVersion,
            author = manifest.Author,
            description = manifest.Description,
            permissions = manifest.Permissions,
            optionalPermissions = manifest.OptionalPermissions,
            runtime = manifest.Runtime,
            official,
            enabled = entry?.Enabled ?? false,
            disclaimerAccepted = entry?.DisclaimerAccepted ?? false,
            loaded = _pluginHost.IsLoaded(manifest.Id),
            panelCount = manifest.Contributes?.Panels?.Length ?? 0,
            dir,
            dependencies = manifest.Dependencies,
            conflicts = manifest.Conflicts,
            homepage = manifest.Homepage,
            license = manifest.License,
            keywords = manifest.Keywords,
            api2 = manifest.IsApi2,
        };
    }

    /// <summary>
    /// 安装 .mclplug（zip）：防穿越 → 解压到 staging → 校验 manifest/integrity →
    /// 原子替换 unpacked/&lt;id&gt;/。与 Windows 端 PluginService 同一套规则；
    /// 区别是不启动运行时（Linux 端暂无 v2 视图宿主）。
    /// </summary>
    private static ((PluginManifest Manifest, string Dir, bool Official)? Plugin,
        List<string> Errors) InstallPlugin(string mclplugPath)
    {
        var errors = new List<string>();
        if (!File.Exists(mclplugPath))
        {
            errors.Add($"安装包不存在：{mclplugPath}");
            return (null, errors);
        }
        Directory.CreateDirectory(LauncherPaths.PluginPacksDir);
        Directory.CreateDirectory(LauncherPaths.PluginUnpackedDir);

        var archivePath = Path.Combine(
            LauncherPaths.PluginPacksDir,
            $"{Path.GetFileNameWithoutExtension(mclplugPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        try { File.Copy(mclplugPath, archivePath, overwrite: true); }
        catch { /* 归档失败不阻塞安装 */ }

        var staging = Path.Combine(
            LauncherPaths.PluginUnpackedDir,
            ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var archive = ZipFile.OpenRead(mclplugPath))
            {
                foreach (var entry in archive.Entries)
                {
                    if (string.IsNullOrEmpty(entry.FullName)) continue;
                    var norm = entry.FullName.Replace('\\', '/');
                    if (Path.IsPathRooted(norm) || norm.Split('/').Contains(".."))
                    {
                        errors.Add($"zip 条目路径非法：{entry.FullName}");
                        return (null, errors);
                    }
                }
                if (!archive.Entries.Any(e =>
                        e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)))
                {
                    errors.Add("包内缺少 manifest.json（拒绝加载）");
                    return (null, errors);
                }
                archive.ExtractToDirectory(staging);
            }

            var manifestPath = Path.Combine(staging, "manifest.json");
            var manifest = PluginManifest.ParseFile(manifestPath);
            if (manifest is null)
            {
                errors.Add("manifest.json 解析失败");
                return (null, errors);
            }
            errors = manifest.Validate(LauncherVersion);
            if (errors.Count > 0) return (null, errors);

            if (manifest.IsApi2)
            {
                var integrity = PluginIntegrity.ParseFile(
                    Path.Combine(staging, PluginIntegrity.FileName));
                if (integrity is null)
                {
                    errors.Add($"2.0 插件包缺少 {PluginIntegrity.FileName}（整包摘要与签名）");
                    return (null, errors);
                }
                if (!string.Equals(integrity.Id, manifest.Id, StringComparison.Ordinal) ||
                    !string.Equals(integrity.Version, manifest.Version, StringComparison.Ordinal))
                {
                    errors.Add("integrity.json 的 id/version 与 manifest 不一致");
                    return (null, errors);
                }
                var mismatches = integrity.VerifyFiles(staging);
                if (mismatches.Count > 0)
                {
                    errors.AddRange(mismatches);
                    return (null, errors);
                }
                if (integrity.Verify(out var detail) == PluginTrustState.Invalid)
                {
                    errors.Add($"插件签名无效（拒绝安装）：{detail}");
                    return (null, errors);
                }
            }

            var target = Path.Combine(LauncherPaths.PluginUnpackedDir, manifest.Id);
            if (Directory.Exists(target))
            {
                var trash = target + ".old-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(target, trash);
                try { Directory.Delete(trash, recursive: true); }
                catch { /* 旧目录删除失败不阻塞新装 */ }
            }
            Directory.Move(staging, target);
            staging = target;

            SetPluginState(manifest.Id, s => s.Version = manifest.Version);
            var installed = (manifest, target,
                IsOfficialPlugin(manifest, target,
                    Path.Combine(target, "manifest.json")));
            return (installed, errors);
        }
        catch (Exception e)
        {
            errors.Add($"安装失败：{e.Message}");
            return (null, errors);
        }
        finally
        {
            try
            {
                if (Directory.Exists(staging) && staging.Contains(".staging-"))
                    Directory.Delete(staging, recursive: true);
            }
            catch { /* 清理失败忽略 */ }
        }
    }

    private static void EnsurePluginStarted(string id, bool allowDisabled = false)
    {
        if (!allowDisabled && !IsPluginEnabled(id))
            throw new Exception("插件未启用，请先启用（第三方插件需要免责确认）");
        if (_pluginHost.IsLoaded(id)) return;
        var installed = ScanPlugins();
        var target = installed.FirstOrDefault(x => x.Manifest.Id == id);
        if (target.Manifest is null) throw new Exception($"插件未安装：{id}");
        var error = _pluginHost.Start(target.Manifest, target.Dir);
        if (error is not null) throw new Exception(error.Message);
    }

    /// <summary>插件宿主 SDK（Python 需要 starcloud_host.py；JS 由 Jint 注入）。</summary>
    private static string PluginSdkPath()
    {
        var env = Environment.GetEnvironmentVariable("STARCLOUD_PLUGIN_SDK");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env)) return env;
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", "pluginsdk"),
            Path.Combine(AppContext.BaseDirectory, "..", "dotnet", "src", "Starcloud.App",
                "Assets", "pluginsdk"),
        };
        return candidates.Select(Path.GetFullPath).FirstOrDefault(Directory.Exists) ?? "";
    }

    private static void CollectBindings(PluginViewNode node, HashSet<string> keys)
    {
        if (node.Value.Bind is { } valueBind)
            keys.Add($"{valueBind.Scope}.{valueBind.Key}");
        if (node.Bind is { } bind)
            keys.Add($"{bind.Scope}.{bind.Key}");
        if (node.ItemsBind is { } itemsBind)
            keys.Add($"{itemsBind.Scope}.{itemsBind.Key}");
        foreach (var child in node.Children) CollectBindings(child, keys);
    }

    /// <summary>只有 App 宿主才有的 UI 能力：桥接进程没有窗口，确认/剪贴板按“不可用”处理。</summary>
    private sealed class BridgePluginHostBridge : IPluginHostBridge
    {
        public bool? Confirm(string title, string message) => null;

        public bool? ShowInfo(string title, string message) => null;

        public void OpenPage(string page)
        {
            // 无 UI 页面栈；插件调用只作为提示忽略。
        }

        public string OpenUrl(string url) => OpenExternal(url);

        public string OpenPath(string path) => OpenExternal(path);

        public string? ClipboardText() => null;

        public void SetClipboardText(string text)
        {
            // 无剪贴板访问
        }

        private static string OpenExternal(string target)
        {
            try
            {
                if (OperatingSystem.IsWindows())
                    Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
                else if (OperatingSystem.IsMacOS())
                    Process.Start(new ProcessStartInfo("open", $"\"{target}\""));
                else
                    Process.Start(new ProcessStartInfo("xdg-open", $"\"{target}\""));
                return "已打开";
            }
            catch (Exception e)
            {
                return e.Message;
            }
        }
    }
}
