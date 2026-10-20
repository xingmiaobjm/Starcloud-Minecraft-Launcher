// 键位布局测试：默认布局 / JSON 往返 / 注释与尾逗号容忍 / 坏输入报错

using Starcloud.Android.Core.Input;
using Xunit;

namespace Starcloud.Android.Core.Tests;

public class VirtualKeyLayoutTests
{
    [Fact]
    public void 默认布局含全部操作键且坐标在屏幕范围内()
    {
        var layout = VirtualKeyLayout.Default();

        Assert.Equal(0.4, layout.GlobalOpacity);
        Assert.False(layout.ShowKeyboardByDefault);
        Assert.Equal(10, layout.Buttons.Count);
        Assert.Equal(
        [
            VirtualAction.Forward, VirtualAction.Left, VirtualAction.Right, VirtualAction.Back,
            VirtualAction.Jump, VirtualAction.Sneak,
            VirtualAction.Inventory, VirtualAction.Chat, VirtualAction.Perspective, VirtualAction.Keyboard,
        ],
            layout.Buttons.Select(b => b.Action).ToList());
        Assert.All(layout.Buttons, b =>
        {
            Assert.InRange(b.X, 0.0, 1.0);
            Assert.InRange(b.Y, 0.0, 1.0);
            Assert.InRange(b.Size, 0.01, 0.5);
            Assert.False(string.IsNullOrWhiteSpace(b.Label));
        });
    }

    [Fact]
    public void 序列化往返保持布局不变()
    {
        var layout = VirtualKeyLayout.Default();

        var restored = VirtualKeyLayout.FromJson(layout.ToJson());

        Assert.Equal(layout.GlobalOpacity, restored.GlobalOpacity);
        Assert.Equal(layout.ShowKeyboardByDefault, restored.ShowKeyboardByDefault);
        Assert.Equal(layout.Buttons.Count, restored.Buttons.Count);
        for (var i = 0; i < layout.Buttons.Count; i++)
        {
            Assert.Equal(layout.Buttons[i].Action, restored.Buttons[i].Action);
            Assert.Equal(layout.Buttons[i].Label, restored.Buttons[i].Label);
            Assert.Equal(layout.Buttons[i].X, restored.Buttons[i].X);
            Assert.Equal(layout.Buttons[i].Y, restored.Buttons[i].Y);
        }
    }

    [Fact]
    public void 解析用户自定义布局并容忍注释与尾逗号()
    {
        const string json = """
            // 用户键位（允许注释）
            {
                "buttons": [
                    { "action": "Forward", "label": "前进", "x": 0.08, "y": 0.60, "size": 0.10 },
                    { "action": "Jump", "label": "跳", "x": 0.90, "y": 0.60, "size": 0.10 },
                ],
                "global_opacity": 0.6,
                "show_keyboard_by_default": true,
            }
            """;

        var layout = VirtualKeyLayout.FromJson(json);

        Assert.Equal(0.6, layout.GlobalOpacity);
        Assert.True(layout.ShowKeyboardByDefault);
        Assert.Equal(2, layout.Buttons.Count);
        Assert.Equal(VirtualAction.Forward, layout.Buttons[0].Action);
        Assert.Equal(VirtualAction.Jump, layout.Buttons[1].Action);
    }

    [Fact]
    public void 损坏的布局文件抛出中文异常()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => VirtualKeyLayout.FromJson("{ buttons: ["));
        Assert.Contains("键位布局解析失败", ex.Message);
    }
}
