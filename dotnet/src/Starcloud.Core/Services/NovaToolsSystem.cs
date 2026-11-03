// Nova 的「操控电脑 / 临时接管游戏」工具（写操作 → 走 NovaPlan 确认清单）。
//
// 权限门：每个工具执行前先查 NovaPermissionStore（computer / game），未授权就返回一句
// 「需要授权」而不是动作 —— 模型据此告诉用户去 Nova 页打开开关。
//
// 为什么都算写操作（ReadOnly=false）：鼠标键盘是"对外产生后果"的动作，必须先给用户
// 看清单（点了哪里、打了什么字）再执行，这与删文件、装整合包同一档信任级别。

using System.Text.Json;

namespace Starcloud.Core.Services;

public sealed partial class NovaToolRegistry
{
    /// <summary>权限没开时的统一回复（模型会把这句话转述给用户）。</summary>
    private string NeedPermission(string key)
    {
        var (title, boundary) = NovaPermissionStore.Describe(key);
        return $"没有「{title}」权限，这次没有执行。请到 Nova 页把「{title}」打开（授权后我会先列清单再动手）。"
            + (boundary.Length > 0 ? $"边界：{boundary}" : "");
    }

    private bool HasPermission(string key) => _c.Permissions?.IsOn(key) == true;

    /// <summary>该档是否允许不经确认直接执行（自主模式及以上）。</summary>
    private Func<bool>? AutoRunFor(string key) =>
        _c.Permissions is null ? null : () => _c.Permissions.AllowsAutoRun(key);

    /// <summary>该档是否完全访问（放开 computer_open 的路径白名单）。</summary>
    private bool FullAccess(string key) => _c.Permissions?.IsFullAccess(key) == true;

    private IEnumerable<NovaTool> BuildSystemTools()
    {
        // ── 操控电脑 ──

        yield return new NovaTool(
            "computer_mouse", "移动/点击鼠标",
            "移动鼠标到屏幕坐标并点击。操作用户的鼠标，属于高影响动作：只在用户明确要求时调用。",
            """
            {"type":"object","properties":{
              "x":{"type":"integer","description":"屏幕横坐标（物理像素）"},
              "y":{"type":"integer","description":"屏幕纵坐标（物理像素）"},
              "action":{"type":"string","enum":["move","left","right","double","down","up"],
                        "description":"move=只移动；left/right=单击；double=双击；down/up=按下/抬起（拖拽用）"}},
             "required":["action"]}
            """,
            ReadOnly: false,
            CanAutoRun: AutoRunFor(NovaPermissionStore.Computer),
            ExecuteAsync: async (args, ct) =>
            {
                if (!HasPermission(NovaPermissionStore.Computer))
                    return NeedPermission(NovaPermissionStore.Computer);
                var action = args.TryGetProperty("action", out var a) ? a.GetString() ?? "left" : "left";
                var x = args.TryGetProperty("x", out var xe) ? xe.GetInt32() : 0;
                var y = args.TryGetProperty("y", out var ye) ? ye.GetInt32() : 0;
                var kind = action.ToLowerInvariant() switch
                {
                    "move" => NovaSystemInput.MouseAction.Move,
                    "right" => NovaSystemInput.MouseAction.Right,
                    "double" => NovaSystemInput.MouseAction.DoubleClick,
                    "down" => NovaSystemInput.MouseAction.Down,
                    "up" => NovaSystemInput.MouseAction.Up,
                    _ => NovaSystemInput.MouseAction.Left,
                };
                return await Task.FromResult(NovaSystemInput.Mouse(kind, x, y, out var err)
                    ? $"已{(kind == NovaSystemInput.MouseAction.Move ? "移动鼠标" : "点击")}（{x},{y}）"
                    : $"操作失败：{err}");
            });

        yield return new NovaTool(
            "computer_type", "模拟键盘打字",
            "把一段文字打进当前前台窗口（不经过输入法，中文也能打）。",
            """
            {"type":"object","properties":{
              "text":{"type":"string","description":"要输入的文字，最多 500 字"}},
             "required":["text"]}
            """,
            ReadOnly: false,
            CanAutoRun: AutoRunFor(NovaPermissionStore.Computer),
            ExecuteAsync: async (args, ct) =>
            {
                if (!HasPermission(NovaPermissionStore.Computer))
                    return NeedPermission(NovaPermissionStore.Computer);
                var text = args.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "";
                if (text.Length == 0) return "没有要输入的内容";
                if (text.Length > 500) text = text[..500];
                return await Task.FromResult(NovaSystemInput.TypeText(text, out var err)
                    ? $"已输入 {text.Length} 个字符"
                    : $"输入失败：{err}");
            });

        yield return new NovaTool(
            "computer_key", "按键 / 组合键",
            "按一组合键，例如 enter、esc、f3、ctrl+s、shift+tab。",
            """
            {"type":"object","properties":{
              "keys":{"type":"string","description":"按键名，用 + 连接组合键（ctrl+s）"}},
             "required":["keys"]}
            """,
            ReadOnly: false,
            CanAutoRun: AutoRunFor(NovaPermissionStore.Computer),
            ExecuteAsync: async (args, ct) =>
            {
                if (!HasPermission(NovaPermissionStore.Computer))
                    return NeedPermission(NovaPermissionStore.Computer);
                var keys = args.TryGetProperty("keys", out var k) ? k.GetString() ?? "" : "";
                return await Task.FromResult(NovaSystemInput.PressKeys(keys, out var err)
                    ? $"已按 {keys}"
                    : $"按键失败：{err}");
            });

        yield return new NovaTool(
            "computer_open", "打开网址或文件",
            "用系统默认程序打开一个 http/https 网址，或本机已存在的文件/目录。",
            """
            {"type":"object","properties":{
              "target":{"type":"string","description":"https://… 或 C:\\\\path\\\\to\\\\file"}},
             "required":["target"]}
            """,
            ReadOnly: false,
            CanAutoRun: AutoRunFor(NovaPermissionStore.Computer),
            ExecuteAsync: async (args, ct) =>
            {
                if (!HasPermission(NovaPermissionStore.Computer))
                    return NeedPermission(NovaPermissionStore.Computer);
                var target = args.TryGetProperty("target", out var t) ? t.GetString() ?? "" : "";
                var full = FullAccess(NovaPermissionStore.Computer);
                return await Task.FromResult(NovaSystemInput.Open(target, out var err, full)
                    ? $"已打开 {target}"
                    : $"打开失败：{err}");
            });

        yield return new NovaTool(
            "computer_window", "窗口操作",
            "切换（前台）/ 最小化 / 最大化 / 关闭指定窗口；不给标题时默认找 Minecraft 窗口。",
            """
            {"type":"object","properties":{
              "action":{"type":"string","enum":["focus","minimize","maximize","close"]},
              "title":{"type":"string","description":"窗口标题片段（可空 = Minecraft 窗口）"}},
             "required":["action"]}
            """,
            ReadOnly: false,
            CanAutoRun: AutoRunFor(NovaPermissionStore.Computer),
            ExecuteAsync: async (args, ct) =>
            {
                if (!HasPermission(NovaPermissionStore.Computer))
                    return NeedPermission(NovaPermissionStore.Computer);
                var action = args.TryGetProperty("action", out var a) ? a.GetString() ?? "focus" : "focus";
                var title = args.TryGetProperty("title", out var t) ? t.GetString() ?? "" : "";
                var kind = action.ToLowerInvariant() switch
                {
                    "minimize" => NovaSystemInput.WindowAction.Minimize,
                    "maximize" => NovaSystemInput.WindowAction.Maximize,
                    "close" => NovaSystemInput.WindowAction.Close,
                    _ => NovaSystemInput.WindowAction.Focus,
                };
                return await Task.FromResult(NovaSystemInput.Window(kind, title, out var err)
                    ? $"窗口操作完成（{action}）"
                    : $"窗口操作失败：{err}");
            });

        // ── 临时接管游戏 ──

        yield return new NovaTool(
            "game_focus", "把游戏切到前台",
            "找到 Minecraft 窗口并切换到前台，供后续按键/点击使用。",
            """{"type":"object","properties":{}}""",
            ReadOnly: false,
            CanAutoRun: AutoRunFor(NovaPermissionStore.GameControl),
            ExecuteAsync: async (args, ct) =>
            {
                if (!HasPermission(NovaPermissionStore.GameControl))
                    return NeedPermission(NovaPermissionStore.GameControl);
                var hwnd = NovaSystemInput.FindGameWindow();
                return await Task.FromResult(NovaSystemInput.Focus(hwnd, out var err)
                    ? "游戏窗口已切到前台"
                    : $"切换失败：{err}");
            });

        yield return new NovaTool(
            "game_key", "向游戏发送按键",
            "先聚焦 Minecraft 窗口，再按键（例如 esc 开菜单、e 开背包、f3 看坐标）。",
            """
            {"type":"object","properties":{
              "keys":{"type":"string","description":"按键名，用 + 连接（如 e、esc、f3、shift+tab）"}},
             "required":["keys"]}
            """,
            ReadOnly: false,
            CanAutoRun: AutoRunFor(NovaPermissionStore.GameControl),
            ExecuteAsync: async (args, ct) =>
            {
                if (!HasPermission(NovaPermissionStore.GameControl))
                    return NeedPermission(NovaPermissionStore.GameControl);
                var hwnd = NovaSystemInput.FindGameWindow();
                if (hwnd == IntPtr.Zero) return "没找到 Minecraft 窗口（游戏可能还没启动）";
                if (!NovaSystemInput.Focus(hwnd, out var focusErr)) return $"聚焦游戏失败：{focusErr}";
                var keys = args.TryGetProperty("keys", out var k) ? k.GetString() ?? "" : "";
                return await Task.FromResult(NovaSystemInput.PressKeys(keys, out var err)
                    ? $"已在游戏里按 {keys}"
                    : $"按键失败：{err}");
            });

        yield return new NovaTool(
            "game_click", "在游戏窗口内点击",
            "先聚焦 Minecraft 窗口，再在它的窗口内坐标点击（坐标相对游戏窗口左上角）。",
            """
            {"type":"object","properties":{
              "x":{"type":"integer","description":"相对游戏窗口客户区的横坐标"},
              "y":{"type":"integer","description":"相对游戏窗口客户区的纵坐标"},
              "button":{"type":"string","enum":["left","right"],"description":"默认 left"}},
             "required":["x","y"]}
            """,
            ReadOnly: false,
            CanAutoRun: AutoRunFor(NovaPermissionStore.GameControl),
            ExecuteAsync: async (args, ct) =>
            {
                if (!HasPermission(NovaPermissionStore.GameControl))
                    return NeedPermission(NovaPermissionStore.GameControl);
                var hwnd = NovaSystemInput.FindGameWindow();
                if (hwnd == IntPtr.Zero) return "没找到 Minecraft 窗口（游戏可能还没启动）";
                if (!NovaSystemInput.Focus(hwnd, out var focusErr)) return $"聚焦游戏失败：{focusErr}";
                if (!NovaSystemInput.GameClientOrigin(hwnd, out var ox, out var oy))
                    return "拿不到游戏窗口的位置";
                var x = args.TryGetProperty("x", out var xe) ? xe.GetInt32() : 0;
                var y = args.TryGetProperty("y", out var ye) ? ye.GetInt32() : 0;
                var right = args.TryGetProperty("button", out var b)
                    && string.Equals(b.GetString(), "right", StringComparison.OrdinalIgnoreCase);
                var ok = NovaSystemInput.Mouse(
                    right ? NovaSystemInput.MouseAction.Right : NovaSystemInput.MouseAction.Left,
                    ox + x, oy + y, out var err);
                return await Task.FromResult(ok
                    ? $"已在游戏窗口内点击（{x},{y}）"
                    : $"点击失败：{err}");
            });
    }
}
