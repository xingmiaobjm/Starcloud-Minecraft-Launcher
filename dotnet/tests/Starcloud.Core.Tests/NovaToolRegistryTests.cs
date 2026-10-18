// Nova 工具表守卫：
//   · 工具名唯一、可被 OpenAI function calling 接受（snake_case）；
//   · 每个工具的 parameters 是合法 JSON Schema（object + properties）；
//   · 读 / 写标记不能标反 —— 写操作必须是 ReadOnly=false，才会走 NovaPlan 确认清单；
//   · 宿主没提供的能力（联机 / 录屏 / 插件 / 外观）不应注册成「必然失败的工具」；
//   · 系统提示词里的简短要求（用户明确要求的一条）必须在位。

using System.Text.Json;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Multiplayer;
using Xunit;

namespace Starcloud.Core.Tests;

public class NovaToolRegistryTests
{
    /// <summary>写操作前缀：这些工具必须只生成参数、等用户确认后才执行</summary>
    private static readonly string[] WritePrefixes =
    [
        "install_", "delete_", "rename_", "launch_", "repair_", "resolve_", "enable_",
        "remove_", "uninstall_", "export_", "backup_", "restore_", "update_", "download_",
        "cancel_", "clear_",
        "multiplayer_host", "multiplayer_join", "multiplayer_stop",
        "start_", "stop_", "set_",
    ];

    private static NovaToolContext Context(
        INovaHostActions? host = null, MultiplayerService? multiplayer = null)
    {
        var settings = new LauncherSettings { DotMinecraft = Path.Combine(Path.GetTempPath(), "sc-nova-tools") };
        var download = new DownloadService(() => settings);
        var cache = new ResourceCacheService(() => settings);
        var manifest = new ManifestService(download, cache);
        var mods = new ModrinthService(download, cache);
        return new NovaToolContext
        {
            Manifest = manifest,
            Installer = new InstallerService(download),
            Mods = mods,
            Library = new ContentLibraryService(download, mods, manifest, () => settings),
            Settings = () => settings,
            Host = host,
            Multiplayer = multiplayer,
        };
    }

    [Fact]
    public void 工具名唯一且是可调用的snake_case()
    {
        var tools = new NovaToolRegistry(Context()).Tools;

        Assert.NotEmpty(tools);
        var duplicates = tools.GroupBy(t => t.Name).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        Assert.True(duplicates.Count == 0, "工具名重复：" + string.Join("、", duplicates));

        foreach (var t in tools)
        {
            Assert.Matches("^[a-z][a-z0-9_]*$", t.Name);
            Assert.False(string.IsNullOrWhiteSpace(t.DisplayName), $"{t.Name} 没有显示名");
            Assert.False(string.IsNullOrWhiteSpace(t.Description), $"{t.Name} 没有描述");
            // 描述会整份发给模型：太长会挤掉上下文
            Assert.True(t.Description.Length <= 120, $"{t.Name} 的描述过长（{t.Description.Length} 字）");
        }
    }

    [Fact]
    public void 每个工具的参数都是合法的对象schema()
    {
        foreach (var t in new NovaToolRegistry(Context()).Tools)
        {
            using var doc = JsonDocument.Parse(t.ParametersJson);
            var root = doc.RootElement;
            Assert.Equal(JsonValueKind.Object, root.ValueKind);
            Assert.Equal("object", root.GetProperty("type").GetString());
            if (root.TryGetProperty("required", out var required))
            {
                var props = root.GetProperty("properties");
                foreach (var name in required.EnumerateArray())
                    Assert.True(props.TryGetProperty(name.GetString()!, out _),
                        $"{t.Name} 的 required 里 {name} 不在 properties 中");
            }
        }
    }

    [Fact]
    public void 写操作不能标成只读()
    {
        foreach (var t in new NovaToolRegistry(Context()).Tools)
        {
            var looksWrite = WritePrefixes.Any(p => t.Name.StartsWith(p, StringComparison.Ordinal));
            Assert.True(t.ReadOnly != looksWrite,
                $"{t.Name} 的只读标记可疑：ReadOnly={t.ReadOnly}，但名字像{(looksWrite ? "写" : "读")}操作");
        }
    }

    [Fact]
    public void 关键能力都在工具表里()
    {
        var names = new NovaToolRegistry(Context()).Tools.Select(t => t.Name).ToHashSet();

        // 启动器每条主线功能都得有对应工具，缺一个用户就会问「Nova 为什么不会」
        foreach (var required in new[]
                 {
                     "get_settings", "list_versions", "read_version_detail", "get_instance_settings",
                     "list_java", "advise_memory", "read_game_log",
                     "search_modrinth", "search_curseforge", "translate_query",
                     "list_content_library", "list_saves", "list_backups", "list_accounts",
                     "list_tasks", "list_notifications", "diagnose_instance", "analyze_crash",
                     "check_mod_conflicts", "check_environment", "check_updates",
                     "install_minecraft", "install_loader", "install_content", "install_modpack",
                     "delete_version", "rename_version", "launch_game", "repair_instance",
                     "export_modpack", "backup_version", "restore_backup", "backup_save",
                     "update_settings", "update_instance_settings", "download_java",
                     // 任务 / 通知 / 账户 / 自更新：同样是启动器主界面上的功能
                     "cancel_task", "clear_tasks", "clear_notifications",
                     "set_default_account", "remove_account", "install_update",
                 })
            Assert.Contains(required, names);
    }

    [Fact]
    public void 宿主没提供的能力不注册成必然失败的工具()
    {
        var bare = new NovaToolRegistry(Context()).Tools.Select(t => t.Name).ToHashSet();

        Assert.DoesNotContain("recording_status", bare);
        Assert.DoesNotContain("start_recording", bare);
        Assert.DoesNotContain("list_plugins", bare);
        Assert.DoesNotContain("set_appearance", bare);
        Assert.DoesNotContain("install_plugin", bare);
        Assert.DoesNotContain("open_page", bare);
        Assert.DoesNotContain("multiplayer_host", bare);
        Assert.DoesNotContain("multiplayer_status", bare);
    }

    [Fact]
    public void 接上宿主后对应的工具就出现()
    {
        var names = new NovaToolRegistry(Context(host: new FakeHost()))
            .Tools.Select(t => t.Name).ToHashSet();

        Assert.Contains("recording_status", names);
        Assert.Contains("start_recording", names);
        Assert.Contains("stop_recording", names);
        Assert.Contains("list_plugins", names);
        Assert.Contains("set_plugin_enabled", names);
        Assert.Contains("install_plugin", names);
        Assert.Contains("uninstall_plugin", names);
        Assert.Contains("set_appearance", names);
        Assert.Contains("open_page", names);
    }

    [Fact]
    public void 导出给模型的tools是合法function_calling格式()
    {
        var registry = new NovaToolRegistry(Context());
        using var doc = JsonDocument.Parse(registry.ToOpenAiJson());

        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(registry.Tools.Count, items.Count);
        foreach (var item in items)
        {
            Assert.Equal("function", item.GetProperty("type").GetString());
            var fn = item.GetProperty("function");
            Assert.False(string.IsNullOrWhiteSpace(fn.GetProperty("name").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(fn.GetProperty("description").GetString()));
            Assert.Equal("object", fn.GetProperty("parameters").GetProperty("type").GetString());
        }
    }

    [Fact]
    public async Task 未知工具与坏参数都给出可读错误()
    {
        var registry = new NovaToolRegistry(Context());

        var unknown = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.ExecuteAsync("no_such_tool", "{}"));
        Assert.Contains("未知工具", unknown.Message);

        var badJson = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.ExecuteAsync("get_settings", "{不是 JSON"));
        Assert.Contains("合法 JSON", badJson.Message);
    }

    [Fact]
    public async Task 缺参数时报错而不是静默返回()
    {
        var registry = new NovaToolRegistry(Context());
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => registry.ExecuteAsync("install_minecraft", "{}"));
        Assert.Contains("version_id", ex.Message);
    }

    // ── 提示词：简短要求（用户明确要求补的一条） ──

    [Fact]
    public void 系统提示词包含简短回复要求()
    {
        Assert.Contains("1~2 句", NovaService.SystemPrompt);
        Assert.Contains("120 字", NovaService.SystemPrompt);
        Assert.Contains("客套", NovaService.SystemPrompt);
    }

    [Fact]
    public void Agent提示词要求收敛工具输出()
    {
        Assert.Contains("1~2 句", NovaAgentLoop.AgentPromptSuffix);
        Assert.Contains("不要贴给用户", NovaAgentLoop.AgentPromptSuffix);
    }

    [Fact]
    public async Task 实验室工具能生成命令与彩色文字()
    {
        var registry = new NovaToolRegistry(Context());

        // 五个实验室工具都在（只读）
        foreach (var name in new[] { "list_projections", "read_projection", "format_colored_text",
                                     "list_command_templates", "generate_minecraft_command" })
        {
            var tool = registry.Find(name);
            Assert.NotNull(tool);
            Assert.True(tool!.ReadOnly, $"{name} 是纯查询/纯计算，应为只读");
        }

        var command = await registry.ExecuteAsync("generate_minecraft_command",
            """{"template":"传送","params":{"target":"@p","x":"1","y":"2","z":"3"}}""");
        Assert.Contains("tp @p 1 2 3", command);

        var colored = await registry.ExecuteAsync("format_colored_text",
            """{"text":"ab","mode":"solid","from":"#FF0000"}""");
        Assert.Contains("§x§F§F§0§0§0§0ab", colored);

        var templates = await registry.ExecuteAsync("list_command_templates", "{}");
        Assert.Contains("给予物品", templates);
    }

    /// <summary>宿主能力替身：只用来验证工具注册与否，不实际执行</summary>
    private sealed class FakeHost : INovaHostActions
    {
        public string? RecordingStatus() => "未在录屏";
        public Task<string> StartRecordingAsync(CancellationToken ct = default) => Task.FromResult("ok");
        public Task<string> StopRecordingAsync(CancellationToken ct = default) => Task.FromResult("ok");
        public string? ListPlugins() => "没有安装任何插件。";
        public string SetPluginEnabled(string id, bool enabled) => "ok";
        public string InstallPlugin(string path) => "ok";
        public string UninstallPlugin(string id) => "ok";
        public string SetAppearance(string? theme = null, string? backgroundMode = null,
            string? backgroundImagePath = null, double? opacity = null, int? blurRadius = null) => "ok";
        public string OpenPage(string page) => "ok";
        public void RestartForUpdate(string artifactPath, string version) { }
        public void OnGameLaunched(string versionId, int gamePid) { }
    }
}
