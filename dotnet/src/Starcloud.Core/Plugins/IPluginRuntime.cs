// 插件运行时抽象：加载入口脚本、调用生命周期钩子（规范 §3.1）

namespace Starcloud.Core.Plugins;

public interface IPluginRuntime : IDisposable
{
    /// <summary>入口脚本文件名（main.js / main.py）</summary>
    string EntryName { get; }

    /// <summary>
    /// 加载入口脚本并回调 onLoad。返回错误信息（null = 成功）；
    /// 插件未定义 onLoad 视为成功。
    /// </summary>
    string? Load(string entryPath, PluginContext ctx);

    /// <summary>调用生命周期钩子；插件未定义该钩子时为空操作，执行异常向上抛（宿主捕获记录）</summary>
    void InvokeHook(string hookName, PluginContext ctx);
}
