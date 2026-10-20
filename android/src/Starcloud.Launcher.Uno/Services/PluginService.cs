// 插件管理服务（App 层）：.mclplug 安装/卸载、解压根扫描、启用状态与免责确认持久化、
// 与 PluginHost 的启停编排。
// 合规声明（plugin-spec.md §7）：插件由第三方独立开发，启动器本体不对插件内容、合法性负责；
// 安装/启用第三方插件必须经用户手动免责确认。

using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;

namespace Starcloud.Launcher.Uno.Services;

public sealed class PluginService
{
    /// <summary>已安装插件（解压根含合法 manifest）</summary>
    public sealed record InstalledPlugin(PluginManifest Manifest, string Dir, bool Official);

    private sealed class PluginStateFile
    {
        [JsonPropertyName("plugins")]
        public Dictionary<string, PluginEntryState> Plugins { get; set; } = [];

        [JsonPropertyName("pageTipShown")]
        public bool PageTipShown { get; set; }
    }

    public sealed class PluginEntryState
    {
        [JsonPropertyName("enabled")] public bool Enabled { get; set; }
        [JsonPropertyName("disclaimerAccepted")] public bool DisclaimerAccepted { get; set; }
        [JsonPropertyName("version")] public string Version { get; set; } = "";
        [JsonPropertyName("panelTipShown")] public bool PanelTipShown { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public PluginHost Host { get; }
    private readonly object _gate = new();

    public PluginService(PluginHost host) => Host = host;

    // ── 扫描 ──

    /// <summary>扫描解压根目录，返回合法插件（含官方签名标记）</summary>
    public List<InstalledPlugin> Scan()
    {
        var root = LauncherPaths.PluginUnpackedDir;
        var result = new List<InstalledPlugin>();
        if (!Directory.Exists(root)) return result;
        foreach (var dir in Directory.GetDirectories(root))
        {
            var manifestPath = Path.Combine(dir, "manifest.json");
            if (!File.Exists(manifestPath)) continue; // 规范：缺 manifest 直接拒绝
            var manifest = PluginManifest.ParseFile(manifestPath);
            if (manifest is null || manifest.Validate(Host.LauncherVersion).Count > 0) continue;
            result.Add(new InstalledPlugin(manifest, dir, IsOfficial(manifest, dir, manifestPath)));
        }
        return result.OrderBy(p => p.Manifest.Name, StringComparer.CurrentCulture).ToList();
    }

    /// <summary>
    /// 官方签名标记：2.0 走整包签名（integrity.json，覆盖全部文件），1.x 走 manifest 签名。
    /// 逐文件摘要不一致时一律不算「已验证」。
    /// </summary>
    private static bool IsOfficial(PluginManifest manifest, string dir, string manifestPath)
    {
        if (!manifest.IsApi2) return manifest.VerifyOfficialSignature(manifestPath);
        var integrity = PluginIntegrity.ParseFile(Path.Combine(dir, PluginIntegrity.FileName));
        if (integrity is null || integrity.VerifyFiles(dir).Count > 0) return false;
        return integrity.Verify(out _) == PluginTrustState.Verified;
    }

    /// <summary>按 id 取已安装插件</summary>
    public InstalledPlugin? Find(string id) =>
        Scan().FirstOrDefault(p => p.Manifest.Id == id);

    // ── 安装 / 卸载 ──

    /// <summary>
    /// 安装 .mclplug（zip）：解压到 staging → 校验 manifest → 原子替换 unpacked/&lt;id&gt;/。
    /// 返回 (插件, 错误列表)：错误非空即失败。
    /// </summary>
    public (InstalledPlugin? Plugin, List<string> Errors) Install(string mclplugPath)
    {
        var errors = new List<string>();
        Directory.CreateDirectory(LauncherPaths.PluginPacksDir);
        Directory.CreateDirectory(LauncherPaths.PluginUnpackedDir);

        // 归档留存（规范 §5：安装包存档便于审计）
        var archivePath = Path.Combine(LauncherPaths.PluginPacksDir,
            $"{Path.GetFileNameWithoutExtension(mclplugPath)}-{DateTime.Now:yyyyMMdd-HHmmss}.zip");
        File.Copy(mclplugPath, archivePath, overwrite: true);

        var staging = Path.Combine(LauncherPaths.PluginUnpackedDir,
            ".staging-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            using (var archive = ZipFile.OpenRead(mclplugPath))
            {
                // 条目防穿越：拒绝绝对路径与 ..（规范 §5）
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
                var manifestEntry = archive.Entries.FirstOrDefault(e =>
                    e.FullName.Equals("manifest.json", StringComparison.OrdinalIgnoreCase));
                if (manifestEntry is null)
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
            errors = manifest.Validate(Host.LauncherVersion);
            if (errors.Count > 0) return (null, errors);

            // 2.0 包必须带 integrity.json：逐文件摘要 + 整包签名（规范 2.0 §8）
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

            // 同 id 原子替换（先停旧实例）
            var target = Path.Combine(LauncherPaths.PluginUnpackedDir, manifest.Id);
            Host.Stop(manifest.Id);
            if (Directory.Exists(target))
            {
                var trash = target + ".old-" + Guid.NewGuid().ToString("N")[..8];
                Directory.Move(target, trash);
                try { Directory.Delete(trash, recursive: true); }
                catch { /* 旧目录删除失败不阻塞新装 */ }
            }
            Directory.Move(staging, target);
            staging = target; // 已移动，清理阶段跳过

            var installed = new InstalledPlugin(manifest, target,
                IsOfficial(manifest, target, Path.Combine(target, "manifest.json")));
            SetVersion(manifest.Id, manifest.Version);
            return (installed, errors);
        }
        catch (Exception ex)
        {
            errors.Add($"安装失败：{ex.Message}");
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

    /// <summary>卸载：停实例 → 删目录 → 清状态</summary>
    public void Uninstall(string id)
    {
        Host.Stop(id);
        var dir = Path.Combine(LauncherPaths.PluginUnpackedDir, id);
        if (Directory.Exists(dir))
        {
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception ex)
            {
                LauncherLog.Write("plugin", $"[{id}] 卸载删除目录失败：{ex.Message}");
            }
        }
        lock (_gate)
        {
            var state = LoadState();
            state.Plugins.Remove(id);
            SaveState(state);
        }
    }

    // ── 启用 / 禁用 ──

    /// <summary>启用（启动插件实例）；失败返回错误信息（null = 成功）</summary>
    public string? Enable(InstalledPlugin plugin)
    {
        // 依赖闸门（规范 2.0 §8.3）：把本插件视为「待启用」，缺依赖 / 依赖未启用 / 版本不满足即拒绝
        var blocker = ResolveDependencies(plugin.Manifest.Id);
        if (blocker is not null) return blocker;

        var err = Host.Start(plugin.Manifest, plugin.Dir);
        if (err is null)
        {
            SetEnabled(plugin.Manifest.Id, true);
            return null;
        }
        return err.Message;
    }

    public void Disable(string id)
    {
        Host.Stop(id);
        SetEnabled(id, false);
    }

    /// <summary>应用启动时加载所有已启用插件（后台线程调）：按依赖序启动，被阻断的记日志</summary>
    public void LoadEnabledAtStartup()
    {
        var installed = Scan();
        var resolution = PluginDependencyResolver.Resolve(
            installed.Select(p => p.Manifest), IsEnabled);

        foreach (var id in resolution.LoadOrder)
        {
            var plugin = installed.FirstOrDefault(p => p.Manifest.Id == id);
            if (plugin is null) continue;
            var err = Host.Start(plugin.Manifest, plugin.Dir);
            if (err is not null)
                LauncherLog.Write("plugin", $"[{id}] 启动期自动加载失败：{err.Message}");
        }
        foreach (var (id, reason) in resolution.Blocked)
            LauncherLog.Write("plugin", $"[{id}] 启动期未加载：{reason}");

        // 全部插件就绪后再广播 launcher.ready：订阅方此时才能确定「其他插件也在了」
        Host.RaiseEvent("launcher.ready", new
        {
            version = Host.LauncherVersion,
            plugins = Host.LoadedCount,
        });
    }

    /// <summary>以「本插件将启用」为前提解析依赖，返回阻断原因（null = 可加载）</summary>
    private string? ResolveDependencies(string pluginId)
    {
        var resolution = PluginDependencyResolver.Resolve(
            Scan().Select(p => p.Manifest),
            id => id == pluginId || IsEnabled(id));
        return PluginDependencyResolver.BlockReason(resolution, pluginId);
    }

    // ── 免责确认（规范 §4：用户手动确认才能启用） ──

    public bool IsDisclaimerAccepted(string id)
    {
        lock (_gate) return LoadState().Plugins.TryGetValue(id, out var s) && s.DisclaimerAccepted;
    }

    public void AcceptDisclaimer(string id, string version)
    {
        lock (_gate)
        {
            var state = LoadState();
            if (!state.Plugins.TryGetValue(id, out var s))
                state.Plugins[id] = s = new PluginEntryState();
            s.DisclaimerAccepted = true;
            s.Version = version;
            SaveState(state);
        }
    }

    // ── 面板来源提示（只展示一次，持久化） ──

    public bool IsPanelTipShown(string id)
    {
        lock (_gate) return LoadState().Plugins.TryGetValue(id, out var s) && s.PanelTipShown;
    }

    public void MarkPanelTipShown(string id)
    {
        lock (_gate)
        {
            var state = LoadState();
            if (!state.Plugins.TryGetValue(id, out var s))
                state.Plugins[id] = s = new PluginEntryState();
            s.PanelTipShown = true;
            SaveState(state);
        }
    }

    // ── 插件页免责声明条（看过一次后不再常驻） ──

    public bool IsPageTipShown()
    {
        lock (_gate) return LoadState().PageTipShown;
    }

    public void MarkPageTipShown()
    {
        lock (_gate)
        {
            var state = LoadState();
            state.PageTipShown = true;
            SaveState(state);
        }
    }

    // ── 状态持久化 ──

    public bool IsEnabled(string id)
    {
        lock (_gate) return LoadState().Plugins.TryGetValue(id, out var s) && s.Enabled;
    }

    private void SetEnabled(string id, bool enabled)
    {
        lock (_gate)
        {
            var state = LoadState();
            if (!state.Plugins.TryGetValue(id, out var s))
                state.Plugins[id] = s = new PluginEntryState();
            s.Enabled = enabled;
            SaveState(state);
        }
    }

    private void SetVersion(string id, string version)
    {
        lock (_gate)
        {
            var state = LoadState();
            if (!state.Plugins.TryGetValue(id, out var s))
                state.Plugins[id] = s = new PluginEntryState();
            s.Version = version;
            SaveState(state);
        }
    }

    private static PluginStateFile LoadState()
    {
        try
        {
            if (File.Exists(LauncherPaths.PluginStateFile))
                return JsonSerializer.Deserialize<PluginStateFile>(
                    File.ReadAllText(LauncherPaths.PluginStateFile), JsonOpts) ?? new PluginStateFile();
        }
        catch
        {
            // 状态文件损坏时按全新状态处理
        }
        return new PluginStateFile();
    }

    private static void SaveState(PluginStateFile state)
    {
        try
        {
            Directory.CreateDirectory(LauncherPaths.PluginsDir);
            File.WriteAllText(LauncherPaths.PluginStateFile,
                JsonSerializer.Serialize(state, JsonOpts));
        }
        catch
        {
            // 状态写失败下次重建
        }
    }
}
