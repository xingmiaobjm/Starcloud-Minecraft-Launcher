// 输入映射测试：Android 键码 → GLFW 键码 / 触摸手势 → 鼠标 / 虚拟动作 → 游戏按键

using Starcloud.Android.Core.Input;
using Xunit;

namespace Starcloud.Android.Core.Tests;

public class InputMapperTests
{
    [Fact]
    public void 轻点映射左键长按映射右键()
    {
        Assert.Equal((Glfw.MouseButtonLeft, Glfw.Press),
            InputMapper.TouchToMouse(TouchGesture.Tap, press: true));
        Assert.Equal((Glfw.MouseButtonLeft, Glfw.Release),
            InputMapper.TouchToMouse(TouchGesture.Tap, press: false));
        Assert.Equal((Glfw.MouseButtonRight, Glfw.Press),
            InputMapper.TouchToMouse(TouchGesture.LongPress, press: true));
        Assert.Equal((Glfw.MouseButtonRight, Glfw.Release),
            InputMapper.TouchToMouse(TouchGesture.LongPress, press: false));
    }

    [Fact]
    public void 字母与数字键码区间连续平移()
    {
        Assert.Equal(Glfw.KeyA, InputMapper.AndroidKeyToGlfw(AndroidKey.KeycodeA));
        Assert.Equal(87, InputMapper.AndroidKeyToGlfw(51)); // W：区间平移（KeycodeW = 51 → GLFW 87）
        Assert.Equal(Glfw.KeyZ, InputMapper.AndroidKeyToGlfw(AndroidKey.KeycodeZ));
        Assert.Equal(Glfw.KeyD0, InputMapper.AndroidKeyToGlfw(AndroidKey.Digit0));
        Assert.Equal(Glfw.KeyD9, InputMapper.AndroidKeyToGlfw(AndroidKey.Digit9));
    }

    [Fact]
    public void 功能键与控制键映射()
    {
        Assert.Equal(Glfw.KeySpace, InputMapper.AndroidKeyToGlfw(AndroidKey.Space));
        Assert.Equal(Glfw.KeyEnter, InputMapper.AndroidKeyToGlfw(AndroidKey.Enter));
        Assert.Equal(Glfw.KeyTab, InputMapper.AndroidKeyToGlfw(AndroidKey.Tab));
        Assert.Equal(Glfw.KeyEscape, InputMapper.AndroidKeyToGlfw(AndroidKey.Escape));
        Assert.Equal(Glfw.KeyBackspace, InputMapper.AndroidKeyToGlfw(AndroidKey.Del));
        Assert.Equal(Glfw.KeyDelete, InputMapper.AndroidKeyToGlfw(AndroidKey.ForwardDel));
        Assert.Equal(Glfw.KeyUp, InputMapper.AndroidKeyToGlfw(AndroidKey.DpadUp));
        Assert.Equal(Glfw.KeyDown, InputMapper.AndroidKeyToGlfw(AndroidKey.DpadDown));
        Assert.Equal(Glfw.KeyLeft, InputMapper.AndroidKeyToGlfw(AndroidKey.DpadLeft));
        Assert.Equal(Glfw.KeyRight, InputMapper.AndroidKeyToGlfw(AndroidKey.DpadRight));
        Assert.Equal(Glfw.KeyLeftShift, InputMapper.AndroidKeyToGlfw(AndroidKey.ShiftLeft));
        Assert.Equal(Glfw.KeyLeftShift, InputMapper.AndroidKeyToGlfw(AndroidKey.ShiftRight));
        Assert.Equal(Glfw.KeyLeftControl, InputMapper.AndroidKeyToGlfw(AndroidKey.CtrlLeft));
        Assert.Equal(Glfw.KeyLeftControl, InputMapper.AndroidKeyToGlfw(AndroidKey.CtrlRight));
        Assert.Equal(Glfw.KeyLeftAlt, InputMapper.AndroidKeyToGlfw(AndroidKey.AltLeft));
        Assert.Equal(Glfw.KeyLeftAlt, InputMapper.AndroidKeyToGlfw(AndroidKey.AltRight));
        Assert.Equal(Glfw.KeyF1, InputMapper.AndroidKeyToGlfw(AndroidKey.F1));
        Assert.Equal(Glfw.KeyF5, InputMapper.AndroidKeyToGlfw(AndroidKey.F1 + 4));
        Assert.Equal(Glfw.KeyF12, InputMapper.AndroidKeyToGlfw(AndroidKey.F12));
    }

    [Fact]
    public void 无对应的系统键返回空()
    {
        Assert.Null(InputMapper.AndroidKeyToGlfw(24));  // 音量上
        Assert.Null(InputMapper.AndroidKeyToGlfw(26));  // 电源键
        Assert.Null(InputMapper.AndroidKeyToGlfw(164)); // 静音
    }

    [Fact]
    public void 虚拟动作映射移动跳跃与界面按键()
    {
        Assert.Equal((int)'W', InputMapper.VirtualActionKey(VirtualAction.Forward));
        Assert.Equal((int)'S', InputMapper.VirtualActionKey(VirtualAction.Back));
        Assert.Equal((int)'A', InputMapper.VirtualActionKey(VirtualAction.Left));
        Assert.Equal((int)'D', InputMapper.VirtualActionKey(VirtualAction.Right));
        Assert.Equal(Glfw.KeySpace, InputMapper.VirtualActionKey(VirtualAction.Jump));
        Assert.Equal(Glfw.KeyLeftShift, InputMapper.VirtualActionKey(VirtualAction.Sneak));
        Assert.Equal((int)'E', InputMapper.VirtualActionKey(VirtualAction.Inventory));
        Assert.Equal((int)'T', InputMapper.VirtualActionKey(VirtualAction.Chat));
        Assert.Equal(Glfw.KeyF5, InputMapper.VirtualActionKey(VirtualAction.Perspective));
    }

    [Fact]
    public void 界面语义动作不映射游戏按键()
    {
        Assert.Null(InputMapper.VirtualActionKey(VirtualAction.Keyboard));
        Assert.Null(InputMapper.VirtualActionKey(VirtualAction.MouseLeft));
        Assert.Null(InputMapper.VirtualActionKey(VirtualAction.MouseRight));
    }
}
