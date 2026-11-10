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

    private static async Task<object?> HandlePluginsListAsync(TextWriter stdout, JsonElement p)
    {
            var state = LoadPluginState();
            var installed = ScanPlugins();
            return new
            {
                plugins = installed.Select(p =>
                    PluginPayload(p.Manifest, p.Dir, p.Official, state)),
                pageTipShown = state.PageTipShown,
            };
    }

    private static async Task<object?> HandlePluginsInstallAsync(TextWriter stdout, JsonElement p)
    {
            var path = p.GetProperty("path").GetString()!;
            var (plugin, errors) = InstallPlugin(path);
            if (plugin is null)
                throw new Exception(string.Join("；", errors));
            var state = LoadPluginState();
            return new
            {
                plugin = PluginPayload(plugin.Value.Manifest, plugin.Value.Dir,
                    plugin.Value.Official, state),
            };
    }

    private static async Task<object?> HandlePluginsUninstallAsync(TextWriter stdout, JsonElement p)
    {
            var id = SafePluginId(p.GetProperty("id").GetString()!);
            var dir = Path.Combine(LauncherPaths.PluginUnpackedDir, id);
            if (Directory.Exists(dir))
            {
                try { Directory.Delete(dir, recursive: true); }
                catch (Exception e)
                {
                    throw new Exception($"卸载失败：{e.Message}");
                }
            }
            SetPluginState(id, _ => { }, remove: true);
            return new { ok = true };
    }

    private static async Task<object?> HandlePluginsSetEnabledAsync(TextWriter stdout, JsonElement p)
    {
            var id = SafePluginId(p.GetProperty("id").GetString()!);
            var enabled = p.GetProperty("enabled").GetBoolean();
            var acceptDisclaimer = p.TryGetProperty("acceptDisclaimer", out var ad) &&
                                   ad.ValueKind == JsonValueKind.True;
            var installed = ScanPlugins();
            var target = installed.FirstOrDefault(x => x.Manifest.Id == id);
            if (target.Manifest is null)
                throw new Exception($"插件未安装：{id}");
            var current = LoadPluginState().Plugins.TryGetValue(id, out var entry)
                ? entry
                : null;
            if (enabled)
            {
                if (!target.Official && !acceptDisclaimer &&
                    (current?.DisclaimerAccepted ?? false) == false)
                    throw new Exception("第三方插件需要先确认免责声明");
                // 依赖闸门：把本插件视为待启用，缺依赖/依赖未启用/版本不满足即拒绝。
                var resolution = PluginDependencyResolver.Resolve(
                    installed.Select(x => x.Manifest),
                    other => other == id || IsPluginEnabled(other));
                var reason = PluginDependencyResolver.BlockReason(resolution, id);
                if (reason is not null) throw new Exception(reason);
                // 真正启动运行时（JS/Python/声明式）；失败则不落启用状态。
                EnsurePluginStarted(id, allowDisabled: true);
            }
            else
            {
                _pluginHost.Stop(id);
            }
            SetPluginState(id, s =>
            {
                s.Enabled = enabled;
                if (acceptDisclaimer) s.DisclaimerAccepted = true;
            });
            return new
            {
                ok = true,
                enabled,
                runtimeStarted = enabled && _pluginHost.IsLoaded(id),
            };
    }

    private static async Task<object?> HandlePluginsPanelsAsync(TextWriter stdout, JsonElement p)
    {
            var id = SafePluginId(p.GetProperty("id").GetString()!);
            EnsurePluginStarted(id);
            return new
            {
                panels = _pluginHost.Panels
                    .Where(panel => panel.PluginId == id)
                    .Select(panel => new
                    {
                        id = panel.PanelId,
                        title = panel.Title,
                        declarative = panel.Declarative,
                        entry = panel.EntryPath,
                    }),
            };
    }

    private static async Task<object?> HandlePluginsViewAsync(TextWriter stdout, JsonElement p)
    {
            var id = SafePluginId(p.GetProperty("id").GetString()!);
            var panelId = p.GetProperty("panelId").GetString()!;
            EnsurePluginStarted(id);
            var panel = _pluginHost.Panels.FirstOrDefault(panel =>
                panel.PluginId == id && panel.PanelId == panelId)
                ?? throw new Exception($"面板不存在：{panelId}");
            if (!PluginViewParser.TryParseFile(
                    panel.EntryPath, out var root, out var errors) || root is null)
            {
                return new
                {
                    ok = false,
                    errors,
                    view = (object?)null,
                    bindings = new Dictionary<string, object?>(),
                };
            }
            var keys = new HashSet<string>(StringComparer.Ordinal);
            CollectBindings(root, keys);
            var bindings = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var key in keys)
            {
                var parts = key.Split('.', 2);
                if (parts.Length != 2 || parts[0] != "storage") continue;
                var result = _pluginHost.InvokeAction(
                    id, "storage.get",
                    JsonSerializer.Serialize(new object?[] { parts[1] }));
                if (!result.Ok || result.ResultJson is null)
                {
                    bindings[key] = null;
                    continue;
                }
                try
                {
                    bindings[key] = JsonSerializer.Deserialize<JsonElement>(
                        result.ResultJson);
                }
                catch
                {
                    bindings[key] = result.ResultJson;
                }
            }
            return new
            {
                ok = true,
                errors = Array.Empty<string>(),
                view = root,
                bindings,
            };
    }

    private static async Task<object?> HandlePluginsInvokeAsync(TextWriter stdout, JsonElement p)
    {
            var id = SafePluginId(p.GetProperty("id").GetString()!);
            var api = p.GetProperty("api").GetString()!;
            var args = p.TryGetProperty("args", out var argsElement)
                ? argsElement.GetRawText()
                : "[]";
            EnsurePluginStarted(id);
            var result = _pluginHost.InvokeAction(id, api, args);
            return new
            {
                ok = result.Ok,
                result = result.ResultJson,
                error = result.Error,
            };
    }

    private static async Task<object?> HandlePluginsOpenFolderAsync(TextWriter stdout, JsonElement p)
    {
            var id = SafePluginId(p.GetProperty("id").GetString()!);
            var dir = Path.Combine(LauncherPaths.PluginUnpackedDir, id);
            if (!Directory.Exists(dir)) throw new Exception($"插件目录不存在：{id}");
            OpenFolder(dir);
            return new { ok = true, path = dir };
    }

}