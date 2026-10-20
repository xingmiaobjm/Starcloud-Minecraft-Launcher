// 输入映射：Android KeyEvent 键码 → GLFW 键码（LWJGL 侧消费 GLFW 码表），
// 触摸手势 → 鼠标按键（轻点=左键、长按=右键）。码表与 glfw3.h / android.view.KeyEvent 一致。

namespace Starcloud.Android.Core.Input;

/// <summary>GLFW 键码 / 鼠标键 / 动作常量（glfw3.h）</summary>
public static class Glfw
{
    public const int Release = 0;
    public const int Press = 1;
    public const int Repeat = 2;

    public const int MouseButtonLeft = 0;
    public const int MouseButtonRight = 1;
    public const int MouseButtonMiddle = 2;

    public const int KeySpace = 32;
    public const int KeyD0 = 48; // 数字键连续：D9 = 57
    public const int KeyD9 = 57;
    public const int KeyA = 65;  // 字母键连续：Z = 90
    public const int KeyZ = 90;
    public const int KeyEscape = 256;
    public const int KeyEnter = 257;
    public const int KeyTab = 258;
    public const int KeyBackspace = 259;
    public const int KeyInsert = 260;
    public const int KeyDelete = 261;
    public const int KeyRight = 262;
    public const int KeyLeft = 263;
    public const int KeyDown = 264;
    public const int KeyUp = 265;
    public const int KeyF1 = 290;  // 功能键连续：F12 = 301
    public const int KeyF5 = 294;
    public const int KeyF12 = 301;
    public const int KeyLeftShift = 340;
    public const int KeyLeftControl = 341;
    public const int KeyLeftAlt = 342;
}

/// <summary>Android KeyEvent 键码（android.view.KeyEvent）</summary>
public static class AndroidKey
{
    public const int KeycodeA = 29; // 字母连续：Z = 54
    public const int KeycodeZ = 54;
    public const int Digit0 = 7;    // 数字连续：9 = 16
    public const int Digit9 = 16;
    public const int AltLeft = 57;
    public const int AltRight = 58;
    public const int ShiftLeft = 59;
    public const int ShiftRight = 60;
    public const int Tab = 61;
    public const int Space = 62;
    public const int Enter = 66;
    public const int Del = 67;         // 退格
    public const int DpadUp = 19;
    public const int DpadDown = 20;
    public const int DpadLeft = 21;
    public const int DpadRight = 22;
    public const int Escape = 111;
    public const int ForwardDel = 112; // Delete
    public const int CtrlLeft = 113;
    public const int CtrlRight = 114;
    public const int F1 = 131;         // 功能键连续：F12 = 142
    public const int F12 = 142;
}

/// <summary>触摸手势语义</summary>
public enum TouchGesture
{
    Tap,
    LongPress,
}

public static class InputMapper
{
    /// <summary>触摸手势 → GLFW 鼠标事件：轻点=左键、长按=右键；press 决定按下还是抬起</summary>
    public static (int Button, int Action) TouchToMouse(TouchGesture gesture, bool press) =>
        (gesture == TouchGesture.Tap ? Glfw.MouseButtonLeft : Glfw.MouseButtonRight,
         press ? Glfw.Press : Glfw.Release);

    /// <summary>Android 键码 → GLFW 键码；无对应（音量键、电源键等）返回 null</summary>
    public static int? AndroidKeyToGlfw(int keycode)
    {
        if (keycode is >= AndroidKey.KeycodeA and <= AndroidKey.KeycodeZ)
            return Glfw.KeyA + (keycode - AndroidKey.KeycodeA); // 字母区间连续平移
        if (keycode is >= AndroidKey.Digit0 and <= AndroidKey.Digit9)
            return Glfw.KeyD0 + (keycode - AndroidKey.Digit0); // 数字区间连续平移
        return keycode switch
        {
            AndroidKey.Space => Glfw.KeySpace,
            AndroidKey.Enter => Glfw.KeyEnter,
            AndroidKey.Tab => Glfw.KeyTab,
            AndroidKey.Escape => Glfw.KeyEscape,
            AndroidKey.Del => Glfw.KeyBackspace,
            AndroidKey.ForwardDel => Glfw.KeyDelete,
            AndroidKey.DpadRight => Glfw.KeyRight,
            AndroidKey.DpadLeft => Glfw.KeyLeft,
            AndroidKey.DpadDown => Glfw.KeyDown,
            AndroidKey.DpadUp => Glfw.KeyUp,
            AndroidKey.ShiftLeft or AndroidKey.ShiftRight => Glfw.KeyLeftShift,
            AndroidKey.CtrlLeft or AndroidKey.CtrlRight => Glfw.KeyLeftControl,
            AndroidKey.AltLeft or AndroidKey.AltRight => Glfw.KeyLeftAlt,
            >= AndroidKey.F1 and <= AndroidKey.F12 => Glfw.KeyF1 + (keycode - AndroidKey.F1),
            _ => null,
        };
    }

    /// <summary>虚拟按键 → 游戏按键；UI 语义键（键盘切换 / 鼠标左右键）返回 null 由 UI 层自行处理</summary>
    public static int? VirtualActionKey(VirtualAction action) => action switch
    {
        VirtualAction.Forward => 'W',
        VirtualAction.Back => 'S',
        VirtualAction.Left => 'A',
        VirtualAction.Right => 'D',
        VirtualAction.Jump => Glfw.KeySpace,
        VirtualAction.Sneak => Glfw.KeyLeftShift,
        VirtualAction.Inventory => 'E',
        VirtualAction.Chat => 'T',
        VirtualAction.Perspective => Glfw.KeyF5,
        _ => null,
    };
}
