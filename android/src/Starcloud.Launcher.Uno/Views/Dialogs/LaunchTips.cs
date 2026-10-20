// 启动页小贴士静态池：进覆盖层取一条，窗口存活期内不重复（取空重洗）；
// Nova 生成的动态贴士走 Enqueue 入队尾，下一次启动才显示（设计：launch-overlay-design.md §3.5）

using Starcloud.Launcher.Uno.Localization;

namespace Starcloud.Launcher.Uno.Views.Dialogs;

internal static class LaunchTips
{
    public static readonly string[] All =
    [
        // 启动器功能
        Loc.T("右键版本列表里的版本，可以「问 Nova」或删除此版本。"),
        Loc.T("Nova 页可以开启游戏内浮窗，局内帮你盯帧数和崩溃。"),
        Loc.T("版本设置里可调内存分配，整合包一般 4-8G 就够。"),
        Loc.T("启动前的自检会自动补全缺失文件，不用手动下载。"),
        Loc.T("Nova 能帮你找整合包和模组，直接说需求就行。"),
        Loc.T("启动出错别慌，错误原因会直接写在启动页上。"),
        Loc.T("删错版本没关系，去下载页重新安装即可恢复。"),
        Loc.T("在「外置」模式填入你自己的第三方认证服务器即可登录。"),

        // MC 游玩小知识
        Loc.T("床可以记录重生点，但在下界会爆炸——可以当 TNT 用。"),
        Loc.T("铁傀儡不怕摔，但会溺水，别让它掉进水里。"),
        Loc.T("苦力怕怕猫：在家附近养猫可以吓退它。"),
        Loc.T("末影人碰水会受伤，也不会传送进水里。"),
        Loc.T("用命名牌在铁砧上改名的生物不会自然消失。"),
        Loc.T("蜂蜜块会减速并减轻摔落伤害，适合做缓冲垫。"),
        Loc.T("灵魂沙水下气泡会把你往上推，是天然电梯。"),
        Loc.T("村民交易会刷新职业等级，升级解锁更好的商品。"),

        // 常见问题预防
        Loc.T("内存并非越大越流畅，分配过多反而可能更卡。"),
        Loc.T("Java 版本会按整合包要求自动匹配，无需手动选。"),
        Loc.T("游戏崩溃时先看 .minecraft/logs 里的日志，看不懂问 Nova。"),
        Loc.T("窗口创建失败多半是显卡驱动问题，先更新驱动。"),
        Loc.T("装光影后启动失败，通常是驱动或内存不足导致。"),
        Loc.T("模组多时首次加载较慢，属于正常现象，耐心等待。"),
        Loc.T("大版本更新前记得备份存档，以防万一。"),
        Loc.T("整合包报「模组冲突」可以让 Nova 帮你定位。"),
    ];

    private static readonly object _lock = new();
    private static Queue<string> _queue = ShuffleAll();

    /// <summary>取一条：洗牌队列出队，取空重洗（窗口存活期内不重复）</summary>
    public static string Next()
    {
        lock (_lock)
        {
            if (_queue.Count == 0) _queue = ShuffleAll();
            return _queue.Dequeue();
        }
    }

    /// <summary>Nova 生成的动态贴士入队尾（下一次启动才显示）</summary>
    public static void Enqueue(string tip)
    {
        if (string.IsNullOrWhiteSpace(tip)) return;
        lock (_lock) _queue.Enqueue(tip.Trim());
    }

    private static Queue<string> ShuffleAll() =>
        new(All.OrderBy(_ => Random.Shared.Next()));
}
