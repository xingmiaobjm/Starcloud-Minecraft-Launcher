// JS 插件运行时（Jint 沙箱引擎）：进程内执行 main.js，无任何宿主外 IO 能力，
// 插件可触达的仅限 ctx 注入的白名单能力对象（规范 §4 沙箱隔离）。

using Jint;
using Jint.Native;
namespace Starcloud.Core.Plugins;

public sealed class JsPluginRuntime : IPluginRuntime
{
    public string EntryName => "main.js";

    private readonly Engine _engine;

    public JsPluginRuntime()
    {
        _engine = new Engine(o => o
            .LimitMemory(48_000_000)              // 内存上限 48MB
            .TimeoutInterval(TimeSpan.FromSeconds(5)) // 单次脚本执行防死循环
            .LimitRecursion(256)
            .Strict());
    }

    public string? Load(string entryPath, PluginContext ctx)
    {
        try
        {
            _engine.Execute(File.ReadAllText(entryPath));
            InvokeHook("onLoad", ctx);
            return null;
        }
        catch (Exception ex)
        {
            return $"JS 加载失败：{ex.Message}";
        }
    }

    public void InvokeHook(string hookName, PluginContext ctx)
    {
        var fn = _engine.GetValue(hookName);
        if (fn is null || fn.IsUndefined()) return; // 未定义钩子 = 空操作
        _engine.Invoke(fn, ctx); // ctx 自动 FromObject；异常上抛，宿主捕获记录
    }

    public void Dispose() => _engine.Dispose();
}
