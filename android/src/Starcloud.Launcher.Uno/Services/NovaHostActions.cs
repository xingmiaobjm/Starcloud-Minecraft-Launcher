// Nova 宿主能力适配器：把「只有 UI 层才有」的启动器功能（录屏 / 插件 / 外观 / 启动后收尾）
// 通过 Core 定义的 INovaHostActions 交给工具层。接口在 Core、实现在这里：
// Core 的单元测试不依赖 WinUI；缺了这份实现，对应工具自动不注册（不会给模型一个必然失败的工具）。
//
// 录屏 / 插件 / 外观都是 **UI 线程亲和** 的操作（采集 API、插件面板注册、窗口根元素主题），
// 而工具是在线程池上跑的，所以这里统一用 RunOnUi 兜一层再调用。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Starcloud.Core.Services;

namespace Starcloud.Launcher.Uno.Services;

internal sealed class NovaHostActions(AppServices s) : INovaHostActions
{
    public string? RecordingStatus()
    {
        if (!RecordingService.IsSupported) return null;
        var status = s.Recording.Snapshot();
        return status.Active
            ? status.Describe()
            : $"未在录屏；输出目录 {RecordingService.OutputDirectory}"
              + (s.Recording.HardwareEncoders.Count > 0
                  ? $"（硬件编码器 {s.Recording.HardwareEncoders.Count} 个）"
                  : "（无硬件编码器，将用软件编码）");
    }

    public Task<string> StartRecordingAsync(CancellationToken ct = default) =>
        Task.FromResult(RunOnUi(() =>
        {
            if (s.Recording.IsRecording) return "已经在录屏了。";
            // 许可门在 RecordingService 内部：没授权直接拒，并记入操作日志
            return s.Recording.StartManual(out var message)
                ? $"已开始录屏，输出目录 {RecordingService.OutputDirectory}。"
                : message;
        }));

    public async Task<string> StopRecordingAsync(CancellationToken ct = default)
    {
        if (!s.Recording.IsRecording) return "当前没在录屏。";
        var path = await s.Recording.StopAsync();
        return path.Length > 0 ? $"录屏已保存到 {path}。" : "录屏已停止，但没有拿到文件路径。";
    }

    public string? ListPlugins()
    {
        var plugins = s.Plugins.Scan();
        if (plugins.Count == 0) return "没有安装任何插件。";
        var lines = new List<string> { $"已安装 {plugins.Count} 个插件：" };
        foreach (var p in plugins.Take(12))
            lines.Add($"· {p.Manifest.Name} {p.Manifest.Version}"
                + $"（id: {p.Manifest.Id}，{(p.Official ? "官方" : "第三方")}，"
                + $"{(s.Plugins.IsEnabled(p.Manifest.Id) ? "已启用" : "已停用")}）");
        return string.Join('\n', lines);
    }

    public string SetPluginEnabled(string id, bool enabled) => RunOnUi(() =>
    {
        var plugin = s.Plugins.Find(id)
            ?? throw new InvalidOperationException($"没有 id 为 {id} 的插件");
        if (!enabled)
        {
            s.Plugins.Disable(id);
            return $"已停用插件 {plugin.Manifest.Name}。";
        }

        // 第三方插件首次启用要过免责确认；用户是在 Nova 的确认清单里点头的，这里补记
        if (!plugin.Official && !s.Plugins.IsDisclaimerAccepted(id))
            s.Plugins.AcceptDisclaimer(id, plugin.Manifest.Version);
        var error = s.Plugins.Enable(plugin);
        return error is null ? $"已启用插件 {plugin.Manifest.Name}。" : $"启用失败：{error}";
    });

    public string InstallPlugin(string path) => RunOnUi(() =>
    {
        if (!File.Exists(path)) throw new InvalidOperationException($"找不到插件包：{path}");
        var (plugin, errors) = s.Plugins.Install(path);
        if (plugin is null)
            throw new InvalidOperationException(errors.Count > 0 ? string.Join("；", errors) : "插件安装失败");
        return $"已安装插件 {plugin.Manifest.Name} {plugin.Manifest.Version}"
            + $"（{(plugin.Official ? "官方" : "第三方")}，默认停用，需要时再启用）。";
    });

    public string UninstallPlugin(string id) => RunOnUi(() =>
    {
        var plugin = s.Plugins.Find(id)
            ?? throw new InvalidOperationException($"没有 id 为 {id} 的插件");
        var name = plugin.Manifest.Name;
        s.Plugins.Uninstall(id);
        return $"已卸载插件 {name}。";
    });

    public string SetAppearance(
        string? theme = null, string? backgroundMode = null,
        string? backgroundImagePath = null, double? opacity = null, int? blurRadius = null)
    {
        var changed = new List<string>();
        if (theme is { Length: > 0 }) { s.Settings.Theme = theme; changed.Add($"主题={theme}"); }
        if (backgroundMode is { Length: > 0 })
        {
            s.Settings.BackgroundMode = backgroundMode;
            changed.Add($"背景模式={backgroundMode}");
        }
        if (backgroundImagePath is { Length: > 0 })
        {
            if (!File.Exists(backgroundImagePath))
                throw new InvalidOperationException($"找不到背景图：{backgroundImagePath}");
            s.Settings.BackgroundImagePath = backgroundImagePath;
            changed.Add("已换背景图");
        }
        if (opacity is { } op)
        {
            s.Settings.BackgroundImageOpacity = Math.Clamp(op, 0.2, 1);
            changed.Add($"背景不透明度={s.Settings.BackgroundImageOpacity:F2}");
        }
        if (blurRadius is { } blur)
        {
            s.Settings.BackgroundBlurRadius = Math.Clamp(blur, 0, 64);
            changed.Add($"背景模糊={s.Settings.BackgroundBlurRadius}");
        }
        if (changed.Count == 0) return "没有可识别的外观项，什么都没改。";

        s.SaveSettings();
        // 安卓端无窗口背景概念（Uno 单窗口，无 SystemBackdrop / 背景宿主）：
        // 仅保留主题 / 壁纸设置状态，主题由壳内页面进入时按 Settings.Theme 落地，无需重画
        return "已更新外观：" + string.Join("，", changed) + "。";
    }

    /// <summary>导航到启动器的某个页面（登录 / 皮肤这类只有界面能做的步骤靠它引导）</summary>
    public string OpenPage(string page)
    {
        var tag = (page ?? "").Trim().ToLowerInvariant();
        var name = tag switch
        {
            "home" => "首页",
            "versions" => "版本",
            "download" => "下载",
            "saves" => "存档",
            "multiplayer" => "联机",
            "accounts" => "账户",
            "plugins" => "插件",
            "ai" => "Nova",
            "logs" => "启动器日志",
            "settings" => "设置",
            _ => throw new InvalidOperationException($"没有「{page}」这个页面"),
        };
        RunOnUi(() =>
        {
            AppNavigation.Navigate(tag);
            return true;
        });
        return $"已打开{name}页面。";
    }

    /// <summary>更新包已就绪：拉起更新脚本后退出（脚本会在覆盖完成后重启启动器）。
    /// 安卓端无脚本自更新链路（APK 整包升级），保留方法签名，实现降级为播报 + 记日志。</summary>
    public void RestartForUpdate(string artifactPath, string version) => RunOnUi(() =>
    {
        // 桌面的 LaunchUpdaterScript / Application.Current.Exit 均为桌面概念，安卓端不执行
        NotificationCenter.Instance.Info($"收到更新包 {version}：安卓端请重新安装 APK 完成更新。");
        LauncherLog.Write("update", $"安卓端忽略自更新请求：{artifactPath}（{version}）");
        return true;
    });

    public void OnGameLaunched(string versionId, int gamePid)
    {
        // Nova 时刻的观察会话 + 运行态标记（与主页启动走同一条收尾）
        try { s.NovaRuntime.OnGameLaunched(versionId, gamePid); } catch { /* 非阻塞 */ }
    }

    /// <summary>
    /// 把操作切到 UI 线程执行（工具跑在线程池上，而录屏 / 插件 / 窗口主题都要求 UI 线程）。
    /// 拿不到调度器（无头场景）时原地执行，保证行为可预期而不是静默丢失。
    /// </summary>
    private static T RunOnUi<T>(Func<T> work)
    {
        var queue = UiThread.Queue;
        if (queue is null || queue.HasThreadAccess) return work();

        T result = default!;
        Exception? failure = null;
        using var done = new ManualResetEventSlim(false);
        if (!queue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                try { result = work(); }
                catch (Exception ex) { failure = ex; }
                finally { done.Set(); }
            }))
            return work(); // 入队失败：退化为直呼（调用方自己承担线程要求）

        if (!done.Wait(TimeSpan.FromSeconds(30)))
            throw new TimeoutException("等待界面线程执行超时");
        if (failure is not null) throw failure;
        return result;
    }
}
