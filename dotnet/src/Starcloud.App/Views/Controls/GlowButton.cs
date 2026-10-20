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
// 「均衡」档起的全局鼠标光照也收在这里：模板里另有两层——
//   · EdgeLight（1px 内缘的径向渐变）：朝向光标那侧被点亮（液态玻璃的玻璃缘）；
//   · ShadowHost（按钮底下的软阴影层）：朝背离光标的方向平移（阴影跟随鼠标）。
// 悬停时按钮还朝光标方向微微隆起（引力形变，见 PointerLightBehavior）。
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
    /// <summary>模板里那层边缘光（全局鼠标光照的「液态玻璃缘」，PointerLightBehavior 驱动）</summary>
    private const string EdgePartName = "EdgeLight";
    /// <summary>模板里那层玻璃高光面（液态玻璃材质，较少档起静态呈现）</summary>
    private const string GlassPartName = "GlassSurface";
    /// <summary>模板里那层软阴影（阴影跟随鼠标的载体，PointerLightBehavior 驱动）</summary>
    private const string ShadowPartName = "ShadowHost";

    /// <summary>
    /// 是否保留悬停反馈（跟手光斑 + 微上浮），默认 true。
    /// <para>关掉它的地方是「本身就是一张卡」的瓦片（下载页的两枚最新版本）：整块被光斑染暗、
    /// 还被抬起来，比完全不动更糟。配套用 <c>ScGlowCardButtonTemplate</c>（没有四态换色）
    /// 才能连 Fluent 的悬停底色一并去掉。</para>
    /// </summary>
    public static readonly DependencyProperty HoverEffectEnabledProperty =
        DependencyProperty.Register(
            nameof(HoverEffectEnabled), typeof(bool), typeof(GlowButton),
            new PropertyMetadata(true, OnHoverEffectEnabledChanged));

    public bool HoverEffectEnabled
    {
        get => (bool)GetValue(HoverEffectEnabledProperty);
        set => SetValue(HoverEffectEnabledProperty, value);
    }

    private static void OnHoverEffectEnabledChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        // 样式可能晚于模板解析才设值，所以两个方向都要重接一次
        if (d is GlowButton button) button.WireHoverEffect();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        WireHoverEffect();
    }

    /// <summary>把模板里的光斑层认成载体并打开悬停行为；不需要悬停反馈时不接（见 HoverEffectEnabled）。</summary>
    private void WireHoverEffect()
    {
        if (!HoverEffectEnabled)
        {
            // 关掉时把行为一并停掉：值可能在模板之后才设进来，这里兜住。
            // 静态瓦片要的就是"一点反馈都不要"，全局光照也不进。
            HoverShadowBehavior.SetIsEnabled(this, false);
            PointerLightBehavior.SetMode(this, PointerLightMode.None);
            return;
        }

        // 与 HoverShadowBehavior 自建层的判定同源：纯透明底的图标按钮本来就不该有光斑，
        // 否则迁移过来会凭空多出一层深色渐变与 -1px 上浮。
        // 但「底色不可见」还不够 —— 底色由样式给的按钮（VersionDetailPage 的分区导航在
        // NavButtonStyle 里设 Transparent、选中态才在代码里换成 accent）属于有意发光，
        // 本地值查不到，不能一并跳过；只有调用点在自己身上写死透明底的才算明确不要光斑。
        if (!HoverShadowBehavior.HasVisibleBackground(this) &&
            ReadLocalValue(BackgroundProperty) != DependencyProperty.UnsetValue)
        {
            // 光斑行为必须一并关掉：只把 PointerLight 设成 None 的话，HoverShadowBehavior
            // 还挂着「启用」开关却没有载体可认，会自己建一层光斑贴在按钮背后。
            // 透明底按钮没有可承载光斑的表面，那层会在按钮边缘被裁掉一截 ——
            // 就是返回类按钮上看到的「残缺阴影」。
            HoverShadowBehavior.SetIsEnabled(this, false);
            PointerLightBehavior.SetMode(this, PointerLightMode.None);
            return;
        }

        // 光只照边缘：按钮不再铺表面光斑（那团晕会盖住原生悬停底色，读作"照亮毛玻璃"），
        // 悬停反馈由边缘光（EdgeLight）承担；列表行/卡片仍保留表面光斑。
        HoverShadowBehavior.SetSurfaceGlow(this, false);

        // 顺序要紧：先把载体认下来，再开行为开关。反过来的话第一次接线时还没有载体，
        // 行为会先自建一层光斑，等载体到位再拆掉重建 —— 白白闪一帧。
        if (GetTemplateChild(GlowPartName) is FrameworkElement glow)
            HoverShadowBehavior.SetCarrier(this, glow);

        // 全局鼠标光照 + 液态玻璃材质：把模板里的边缘光、玻璃面与阴影层认下来交给
        // PointerLightBehavior。行为内部按动效等级分档——极少全灭、较少摆静态玻璃质感、
        // 均衡起全动态（边缘光跟手、阴影跟随、引力形变）。
        if (GetTemplateChild(EdgePartName) is Border edge)
            PointerLightBehavior.SetEdgePart(this, edge);
        if (GetTemplateChild(GlassPartName) is Border glass)
            PointerLightBehavior.SetGlassPart(this, glass);
        if (GetTemplateChild(ShadowPartName) is FrameworkElement shadow)
            PointerLightBehavior.SetShadowPart(this, shadow);
        PointerLightBehavior.SetMode(this, PointerLightMode.Button);

        HoverShadowBehavior.SetIsEnabled(this, true);
    }
}
