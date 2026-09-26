// Python 插件运行时（进程外沙箱）：python.exe 子进程跑 main.py，
// 宿主与子进程走 stdin/stdout 行式 JSON 协议（host SDK：Assets/pluginsdk/starcloud_host.py）。
// 子进程无法直接触达启动器内存：
//   · 参数 / 通知 / 面板通过响应载荷**声明式上报**，由宿主过滤后落地；
//   · 其余 ctx 能力（版本 / 实例 / 资源 / 设置 / 存储…）走 host_call 回调，
//     宿主在等响应的间隙处理这类请求，权限校验与 JS 插件完全同一套代码。

using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Plugins;

public sealed class PyPluginRuntime : IPluginRuntime
{
    public string EntryName => "main.py";

    private readonly string _pluginDir;
    private readonly string _hostSdkPath;
    private readonly string _pluginId;
    private readonly Func<string, string, string, string> _hostCall;
    private Process? _proc;

    /// <summary>最近一次通信失败的原始原因（诊断用，避免只看到"无响应"）</summary>
    private string? _lastError;

    public PyPluginRuntime(string pluginDir, string hostSdkPath, string pluginId,
        Func<string, string, string, string>? hostCall = null)
    {
        _pluginDir = pluginDir;
        _hostSdkPath = hostSdkPath;
        _pluginId = pluginId;
        _hostCall = hostCall ?? ((_, _, _) => "{\"ok\":false,\"error\":\"当前宿主没有提供回调\"}");
    }

    /// <summary>探测本机 Python（Windows：PATH python.exe / py 启动器；unix：PATH python3 / python）；未安装返回 null</summary>
    public static string? FindPython()
    {
        if (OperatingSystem.IsWindows())
        {
            foreach (var exe in new[] { "python.exe", "python3.exe" })
            {
                var path = FindOnPath(exe);
                if (path is not null) return path;
            }
            // py 启动器（Windows 官方安装包）
            var windir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe");
            return File.Exists(windir) ? windir : null;
        }

        // unix：python3 优先于 python（个别发行版 python 仍指向 python2 残留）
        foreach (var exe in new[] { "python3", "python" })
        {
            var path = FindOnPath(exe);
            if (path is not null) return path;
        }
        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var p = Path.Combine(dir.Trim('"'), fileName);
                if (File.Exists(p)) return p;
            }
            catch
            {
                // PATH 中含非法项时跳过
            }
        }
        return null;
    }

    public string? Load(string entryPath, PluginContext ctx)
    {
        var python = FindPython();
        if (python is null)
            return "未找到本机 Python（main.py 插件需要 Python 3.8+，请安装后重试）";

        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = python,
                Arguments = $"-X utf8 \"{_hostSdkPath}\" \"{_pluginDir}\"",
                UseShellExecute = false,
                WorkingDirectory = _pluginDir,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            _proc = Process.Start(psi);
            if (_proc is null) return "Python 子进程启动失败";

            // load 握手：传权限与启动器信息，host.py exec main.py 并调 onLoad
            var resp = Request(new Dictionary<string, object?>
            {
                ["op"] = "load",
                ["permissions"] = ctx.Permissions,
                ["launcher"] = new Dictionary<string, object?>
                {
                    ["version"] = ctx.Launcher.Version,
                    ["apiVersion"] = ctx.Launcher.ApiVersion,
                    ["officialBuild"] = ctx.Launcher.OfficialBuild,
                },
            });
            if (resp is not { } loadResp)
                return $"Python 插件无响应（{_lastError ?? $"退出码 {ExitCodeText()}"}）";
            if (!IsOk(loadResp, out var error)) return error;
            ApplyResponse(loadResp, ctx);
            return null;
        }
        catch (Exception ex)
        {
            return $"Python 插件加载失败：{ex.Message}";
        }
    }

    public void InvokeHook(string hookName, PluginContext ctx)
    {
        if (_proc is null || _proc.HasExited) return;
        var payload = new Dictionary<string, object?> { ["op"] = "call", ["hook"] = hookName };
        // 事件钩子：把 ctx.event 一起送过去（宿主 SDK 会组装成 ctx.event = {"name":…,"data":…}）
        if (ctx.Event is { } evt)
        {
            payload["event"] = new Dictionary<string, object?>
            {
                ["name"] = evt.Name,
                ["data"] = JsonDocument.Parse(evt.Data).RootElement,
            };
        }
        var resp = Request(payload);
        if (resp is not { } callResp)
            throw new InvalidOperationException("Python 插件无响应（子进程可能已退出）");
        if (!IsOk(callResp, out var error)) throw new InvalidOperationException(error);
        ApplyResponse(callResp, ctx);
    }

    // ── 响应载荷落地：append/removeKey 过身份黑名单，panels/notifications/logs 转交宿主 ──

    private void ApplyResponse(JsonElement resp, PluginContext ctx)
    {
        if (ctx.Args is { } args)
        {
            foreach (var a in resp.TryGetStringArray("append")) args.Append(a);
            foreach (var a in resp.TryGetStringArray("removeKey")) args.RemoveKey(a);
        }
        foreach (var log in resp.TryGetLogArray("logs"))
        {
            switch (log.level)
            {
                case "WARN": ctx.Logger.Warn(log.text); break;
                case "ERROR": ctx.Logger.Error(log.text); break;
                default: ctx.Logger.Info(log.text); break;
            }
        }
        foreach (var panel in resp.TryGetPanelArray("panels"))
            ctx.Ui?.RegisterPanel(panel.id, panel.title, panel.entry);
        foreach (var slot in resp.TryGetSlotArray("slots"))
            ctx.Ui?.RegisterSlot(slot.id, slot.entry, slot.height);
        foreach (var note in resp.TryGetNotifyArray("notifications"))
            ctx.Ui?.Notify(note.title, note.message);
    }

    private static bool IsOk(JsonElement resp, out string error)
    {
        error = "";
        if (resp.ValueKind == JsonValueKind.Object && resp.TryGetProperty("ok", out var ok) &&
            ok.ValueKind == JsonValueKind.True) return true;
        error = resp.ValueKind == JsonValueKind.Object &&
                resp.TryGetProperty("error", out var err)
            ? err.GetString() ?? "未知错误"
            : "响应格式错误";
        return false;
    }

    /// <summary>
    /// 同步请求-响应（行式 JSON）；超时 15s / 进程死亡返回 null。
    /// 期间子进程可能反过来发 host_call（ctx 能力调用），就地执行并回一行 host_reply。
    /// </summary>
    private JsonElement? Request(Dictionary<string, object?> payload)
    {
        var proc = _proc ?? throw new InvalidOperationException("Python 子进程未启动");
        try
        {
            proc.StandardInput.WriteLine(JsonSerializer.Serialize(payload));
            proc.StandardInput.Flush();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                var lineTask = proc.StandardOutput.ReadLineAsync(cts.Token).AsTask();
                lineTask.Wait(cts.Token);
                var line = lineTask.Result;
                if (line is null) return null;
                if (line.Length == 0) continue;

                JsonElement parsed;
                try
                {
                    parsed = JsonDocument.Parse(line).RootElement.Clone();
                }
                catch (JsonException)
                {
                    continue; // 子进程往 stdout 打了非协议内容：忽略，别把它当响应
                }
                if (parsed.ValueKind == JsonValueKind.Object &&
                    parsed.TryGetProperty("op", out var op) &&
                    op.ValueKind == JsonValueKind.String &&
                    op.GetString() == "host_call")
                {
                    HandleHostCall(proc, parsed);
                    continue; // 继续等真正的响应
                }
                return parsed;
            }
        }
        catch (Exception ex)
        {
            _lastError = "与 Python 子进程通信失败：" + ex;
            return null;
        }
    }

    /// <summary>子进程退出码文本；仍在运行时不去读 ExitCode（那会抛 "Process must exit..."）</summary>
    private string ExitCodeText()
    {
        try
        {
            return _proc is { HasExited: true } p ? $"退出码 {p.ExitCode}" : "子进程仍在运行";
        }
        catch
        {
            return "退出码未知";
        }
    }

    /// <summary>处理子进程的能力回调：{"op":"host_call","id":n,"api":"instances.list","args":[…]}</summary>
    private void HandleHostCall(Process proc, JsonElement call)
    {
        var id = call.TryGetProperty("id", out var idNode) && idNode.ValueKind == JsonValueKind.Number
            ? idNode.GetInt64()
            : 0L;
        var api = call.TryGetProperty("api", out var apiNode) ? apiNode.GetString() ?? "" : "";
        var args = call.TryGetProperty("args", out var argsNode) ? argsNode.GetRawText() : "[]";

        string reply;
        try
        {
            reply = _hostCall(_pluginId, api, args);
        }
        catch (Exception ex)
        {
            reply = "{\"ok\":false,\"error\":" + JsonSerializer.Serialize(ex.Message) + "}";
        }

        // 宿主回调返回 {"ok":…,"result":…}，补上 id 让子进程对上号
        var payload = new Dictionary<string, object?> { ["op"] = "host_reply", ["id"] = id };
        try
        {
            using var doc = JsonDocument.Parse(reply);
            var root = doc.RootElement;
            payload["ok"] = root.TryGetProperty("ok", out var ok) &&
                            ok.ValueKind == JsonValueKind.True;
            // Clone：JsonElement 绑定在 JsonDocument 的生命周期上，using 结束后序列化会抛 ObjectDisposed
            if (root.TryGetProperty("result", out var result)) payload["result"] = result.Clone();
            if (root.TryGetProperty("error", out var error)) payload["error"] = error.GetString();
        }
        catch (JsonException)
        {
            payload["ok"] = false;
            payload["error"] = "宿主回调返回了无法解析的结果";
        }
        proc.StandardInput.WriteLine(JsonSerializer.Serialize(payload));
        proc.StandardInput.Flush();
    }

    public void Dispose()
    {
        try
        {
            if (_proc is { HasExited: false } p)
            {
                try { p.StandardInput.WriteLine("{\"op\":\"unload\"}"); p.StandardInput.Flush(); } catch { }
                if (!p.WaitForExit(3000)) p.Kill(entireProcessTree: true);
            }
            _proc?.Dispose();
        }
        catch
        {
            // 进程清理失败忽略
        }
    }
}
