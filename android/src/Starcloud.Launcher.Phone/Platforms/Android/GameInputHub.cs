// 输入枢纽：物理/虚拟键盘与游戏区触摸在此汇聚，经 JavaBridge 投递给游戏进程（设计稿 §6.4）。
// MainActivity.DispatchKeyEvent（键盘）与 GameSurfaceHost（触摸）统一走本类；
// Enabled 由游戏页在可见期置位；JavaBridge 未初始化（游戏未启动）时事件放行系统处理。
// 由 MAUI 壳原样迁移（仅改命名空间与视图类型来源）。

using global::Android.Views;
using Starcloud.Android.Core.Input;
using Starcloud.Android.Host.Bridging;
using Starcloud.Launcher.Phone.Views;

namespace Starcloud.Launcher.Phone.Platforms.Android;

internal static class GameInputHub
{
    /// <summary>游戏页可见时置位；仅投递期内消费输入事件</summary>
    public static volatile bool Enabled;

    /// <summary>游戏窗口内最后一次触摸坐标（Moved 事件增量计算用）</summary>
    private static float _lastX, _lastY;

    /// <summary>键盘事件入口；返回 true 表示已投递给游戏（宿主不再处理）</summary>
    public static bool HandleKey(KeyEvent? e)
    {
        if (e is null || !Enabled || !JavaBridge.IsInitialized)
            return false;

        // 多字符合成事件（老输入法）无法映射为单键，吞掉防止回落成杂音按键
        if (e.Action == KeyEventActions.Multiple)
            return true;

        bool isDown = e.Action == KeyEventActions.Down;
        int? glfw = InputMapper.AndroidKeyToGlfw((int)e.KeyCode);
        if (glfw is null)
            return false; // 音量键 / 电源键等无游戏语义的键放行

        // 字符仅按下事件携带（抬起时 UnicodeChar 语义不稳定）；负值为 dead-key 组合，钳为 0
        char keyChar = isDown ? (char)Math.Max(e.UnicodeChar, 0) : '\0';
        JavaBridge.SetModifiers(glfw.Value, isDown);
        JavaBridge.SendKeycode((int)e.KeyCode, keyChar, e.ScanCode, MetaToGlfw(e.MetaState), isDown);
        return true;
    }

    /// <summary>游戏区触摸入口（视图坐标即游戏窗口坐标）</summary>
    public static void HandleTouch(SurfaceTouchEventArgs e)
    {
        if (!Enabled || !JavaBridge.IsInitialized)
            return;

        switch (e.Action)
        {
            case SurfaceTouchAction.Pressed:
                JavaBridge.SendCursorPos(e.X, e.Y);
                JavaBridge.SendMouseButton(Glfw.MouseButtonLeft, true);
                break;
            case SurfaceTouchAction.Released:
                JavaBridge.SendMouseButton(Glfw.MouseButtonLeft, false);
                break;
            case SurfaceTouchAction.Moved:
                // 抓取（准星）模式投相对位移，否则投绝对坐标
                if (JavaBridge.IsGrabbing)
                    JavaBridge.SendCursorDelta(e.X - _lastX, e.Y - _lastY);
                else
                    JavaBridge.SendCursorPos(e.X, e.Y);
                break;
            case SurfaceTouchAction.Cancelled:
                JavaBridge.ResetInputState();
                break;
        }
        _lastX = e.X;
        _lastY = e.Y;
    }

    /// <summary>Android META_* → GLFW GLFW_MOD_*（两套值域不同，逐位映射）</summary>
    private static int MetaToGlfw(MetaKeyStates meta)
    {
        int mods = 0;
        if ((meta & MetaKeyStates.ShiftOn) != 0) mods |= 0x01;      // GLFW_MOD_SHIFT
        if ((meta & MetaKeyStates.CtrlOn) != 0) mods |= 0x02;       // GLFW_MOD_CONTROL
        if ((meta & MetaKeyStates.AltOn) != 0) mods |= 0x04;        // GLFW_MOD_ALT
        if ((meta & MetaKeyStates.MetaOn) != 0) mods |= 0x08;       // GLFW_MOD_SUPER
        if ((meta & MetaKeyStates.CapsLockOn) != 0) mods |= 0x10;   // GLFW_MOD_CAPS_LOCK
        if ((meta & MetaKeyStates.NumLockOn) != 0) mods |= 0x20;    // GLFW_MOD_NUM_LOCK
        return mods;
    }
}
