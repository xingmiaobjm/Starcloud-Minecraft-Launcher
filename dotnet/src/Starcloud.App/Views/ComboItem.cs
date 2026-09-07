// ComboBox 条目（Value/Label，列表显示用 Label）

namespace Starcloud.App.Views;

public sealed record ComboItem(string Value, string Label)
{
    public override string ToString() => Label;
}
