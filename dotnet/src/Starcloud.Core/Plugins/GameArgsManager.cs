// 插件启动参数管理器：仅 append / removeKey（作用于插件自身增量），无全量覆写（规范 §3.2）
// 安全不变量：身份相关参数在入口处拦截丢弃；本体核心参数不在本管理器内，插件无法触碰。

namespace Starcloud.Core.Plugins;

public sealed class GameArgsManager
{
    private readonly List<string> _jvm = [];
    private readonly List<string> _game = [];
    private readonly PluginLogger? _logger;

    /// <summary>插件追加的 JVM 参数增量</summary>
    public IReadOnlyList<string> Jvm => _jvm;

    /// <summary>插件追加的游戏参数增量</summary>
    public IReadOnlyList<string> Game => _game;

    public GameArgsManager(PluginLogger? logger = null) => _logger = logger;

    /// <summary>追加游戏参数（SDK 主入口）：ctx.args.append("-Xmx4G")</summary>
    public void Append(string arg) => AppendGame(arg);

    public void AppendGame(string arg) => Add(_game, arg, "game");

    public void AppendJvm(string arg) => Add(_jvm, arg, "jvm");

    /// <summary>
    /// 按键移除插件此前追加的参数（含其取值 token）。
    /// 仅作用于本管理器内的增量——本体核心身份参数不在其中，物理上不可被移除。
    /// </summary>
    public void RemoveKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key)) return;
        RemoveByPrefix(_game, key);
        RemoveByPrefix(_jvm, key);
    }

    private void RemoveByPrefix(List<string> list, string key)
    {
        for (var i = list.Count - 1; i >= 0; i--)
        {
            var item = list[i];
            var eq = item.IndexOf('=');
            var itemKey = eq > 0 ? item[..eq] : item;
            if (!string.Equals(itemKey, key, StringComparison.OrdinalIgnoreCase)) continue;
            // 成对参数：移除 key 后若下一个 token 不是 - 开头，一并移除（视为取值）
            list.RemoveAt(i);
            if (i < list.Count && !list[i].StartsWith('-'))
                list.RemoveAt(i);
        }
    }

    private void Add(List<string> list, string arg, string kind)
    {
        if (string.IsNullOrWhiteSpace(arg)) return;
        arg = arg.Trim();
        if (PluginPermissions.IsIdentityArg(arg, out var banned))
        {
            _logger?.Warn($"已拦截插件参数注入（{kind}）：\"{arg}\" 命中身份参数黑名单 {banned}");
            return;
        }
        list.Add(arg);
    }
}
