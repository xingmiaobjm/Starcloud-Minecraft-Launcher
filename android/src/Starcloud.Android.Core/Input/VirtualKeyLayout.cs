// 虚拟键位布局：横屏归一化坐标（左上原点，x/y/size ∈ [0,1]，与屏幕实际分辨率无关），
// 持久化为游戏数据目录下的 JSON，用户可自行调整。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Android.Core.Input;

public enum VirtualAction
{
    Forward,
    Back,
    Left,
    Right,
    Jump,
    Sneak,
    Inventory,
    Chat,
    Perspective,
    Keyboard,
    MouseLeft,
    MouseRight,
}

public sealed class VirtualKeyButton
{
    [JsonPropertyName("action")] public VirtualAction Action { get; set; }
    [JsonPropertyName("label")] public string Label { get; set; } = "";
    /// <summary>归一化坐标：左上原点，x 向右 / y 向下</summary>
    [JsonPropertyName("x")] public double X { get; set; }
    [JsonPropertyName("y")] public double Y { get; set; }
    [JsonPropertyName("size")] public double Size { get; set; } = 0.09;
}

public sealed class VirtualKeyLayout
{
    [JsonPropertyName("buttons")] public List<VirtualKeyButton> Buttons { get; set; } = [];
    [JsonPropertyName("global_opacity")] public double GlobalOpacity { get; set; } = 0.4;
    [JsonPropertyName("show_keyboard_by_default")] public bool ShowKeyboardByDefault { get; set; }

    /// <summary>出厂默认布局：左手方向键区 + 右手跳跃/潜行 + 右缘功能列（对标 FCL 的横屏排布）</summary>
    public static VirtualKeyLayout Default() => new()
    {
        ShowKeyboardByDefault = false,
        Buttons =
        [
            new() { Action = VirtualAction.Forward, Label = "前进", X = 0.10, Y = 0.58 },
            new() { Action = VirtualAction.Left, Label = "左移", X = 0.025, Y = 0.68 },
            new() { Action = VirtualAction.Right, Label = "右移", X = 0.175, Y = 0.68 },
            new() { Action = VirtualAction.Back, Label = "后退", X = 0.10, Y = 0.78 },
            new() { Action = VirtualAction.Jump, Label = "跳跃", X = 0.875, Y = 0.62 },
            new() { Action = VirtualAction.Sneak, Label = "潜行", X = 0.875, Y = 0.76 },
            new() { Action = VirtualAction.Inventory, Label = "背包", X = 0.955, Y = 0.50 },
            new() { Action = VirtualAction.Chat, Label = "聊天", X = 0.955, Y = 0.26 },
            new() { Action = VirtualAction.Perspective, Label = "视角", X = 0.955, Y = 0.38 },
            new() { Action = VirtualAction.Keyboard, Label = "键盘", X = 0.955, Y = 0.14 },
        ],
    };

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public string ToJson() => JsonSerializer.Serialize(this, JsonOpts);

    public static VirtualKeyLayout FromJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<VirtualKeyLayout>(json, JsonOpts)
                   ?? throw new InvalidOperationException("键位布局为空");
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException($"键位布局解析失败：{ex.Message}", ex);
        }
    }
}
