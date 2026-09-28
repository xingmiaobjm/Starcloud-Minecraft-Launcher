// 可复用元素：带「跟随光标内阴影」的按钮。
//
// 此前每个用到跟手阴影的按钮，调用点都要自己写三样东西：
//   1) 一层光斑载体（内联径向渐变的 Border，x:Name="Glow"）；
//   2) 把它挂成载体 —— beh:HoverShadowBehavior.Carrier="{Binding ElementName=…}"；
//   3) 自己开开关 —— beh:HoverShadowBehavior.IsEnabled="True"。
// 其中第 1 步的圆角是个手写魔数：它必须与「按钮表面真正的圆角」一致，
// 但按钮表面圆角往往来自主题资源（ControlCornerRadius）或样式，两边没有联动，
// 于是出现了「阴影比按钮更方、四个角上溢出一圈」的错配。
//
// 现在把这三步收进控件与模板：按钮自己的模板里就有那层光斑（在内容之下），
// 圆角用 {TemplateBinding CornerRadius} 与按钮表面读同一个值 —— 结构上不可能再错配。
// 调用点只写一个标签：
//     <ctrl:GlowButton Content="确定" Click="…"/>
//     <ctrl:GlowButton Padding="0" HorizontalAlignment="Stretch" HorizontalContentAlignment="Stretch">…</ctrl:GlowButton>
// 需要限制圆角时照旧设按钮自己的 CornerRadius，光斑会跟着走。
//
// 模板见 App.xaml 的 ScGlowButtonTemplate（普通按钮，保留 Fluent 四态换色）
// 与 ScGlowCardButtonTemplate（卡片式按钮，底色自带、悬停不换色）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Views.Behaviors;

namespace Starcloud.App.Views.Controls;

/// <summary>带跟手内阴影的按钮：光斑层写在控件模板里，圆角与按钮表面共用同一个 TemplateBinding。</summary>
public sealed class GlowButton : Button
{
    /// <summary>模板里那层光斑的部件名（ScGlowButtonTemplate / ScGlowCardButtonTemplate 里都叫 Glow）。</summary>
    private const string GlowPartName = "Glow";

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // 与 HoverShadowBehavior 自建层的判定同源：纯透明底的图标按钮本来就不该有光斑，
        // 否则迁移过来会凭空多出一层深色渐变与 -1px 上浮。
        // 但「底色不可见」还不够 —— 底色由样式给的按钮（VersionDetailPage 的分区导航在
        // NavButtonStyle 里设 Transparent、选中态才在代码里换成 accent）属于有意发光，
        // 本地值查不到，不能一并跳过；只有调用点在自己身上写死透明底的才算明确不要光斑。
        if (!HoverShadowBehavior.HasVisibleBackground(this) &&
            ReadLocalValue(BackgroundProperty) != DependencyProperty.UnsetValue) return;

        // 顺序要紧：先把载体认下来，再开行为开关。反过来的话第一次接线时还没有载体，
        // 行为会先自建一层光斑，等载体到位再拆掉重建 —— 白白闪一帧。
        if (GetTemplateChild(GlowPartName) is FrameworkElement glow)
            HoverShadowBehavior.SetCarrier(this, glow);

        HoverShadowBehavior.SetIsEnabled(this, true);
    }
}
