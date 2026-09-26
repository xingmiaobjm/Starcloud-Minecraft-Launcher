// 悬浮光斑层：跟随光标的内阴影「载体」。
//
// 跟手阴影（HoverShadowBehavior）自己不画东西：它只把「载体」这一层的合成不透明度
// 在 0/1 之间淡入淡出，并改写载体上那个径向渐变的圆心与半径，让光斑跟着光标走。
// 因此每处用到跟手阴影的地方都需要一个「带径向渐变的元素」——这段六档黑色渐变此前在
// 各处 XAML 里各抄了一份（模块内 30 处同款）。现在只留这一份：颜色深浅全在这里定，
// 各页面只管 <ctrl:GlowLayer x:Name="…"/> 挂一个名字给行为当载体。
//
// 为什么底色要分主题：光斑是「叠在卡片面上的一层」。浅色主题叠黑 = 压暗，读起来是阴影；
// 深色主题若继续叠黑，那一块只会比背景更暗——看着不是「浮起来」而是「塌进去」。
// 所以深色的底色翻成白：同一档透明度落地就是一层跟着光标走的淡灰高光。
// 标题栏那种近全黑的底（#161616）再靠 Peak 补一档。
//
// 为什么不写成 <RadialGradientBrush x:Key="…"> 资源：资源字典里的画刷是同一个实例，
// 而行为会按光标不断改写 Center/Radius——共享实例会让所有层的光斑一起动（上一行的
// 残影会跳到最后一次光标位置）。所以这里在构造时各建一份画刷，一个元素一份。
//
// 用法（放在内容之下，本身不吃命中测试）：
//     <Grid beh:HoverShadowBehavior.IsEnabled="True"
//           beh:HoverShadowBehavior.Carrier="{Binding ElementName=RowGlow}">
//         <ctrl:GlowLayer x:Name="RowGlow" CornerRadius="8" />
//         …内容…
//     </Grid>
// 圆角与宿主一致即可；尺寸由布局拉伸决定（载体允许大于宿主，见行为的 ApplyLight）。
//
// 基类选 Grid（而不是更贴切的 Border）：WinUI 3 里 Border 是密封类，不能派生；
// 行为解析载体的径向渐变时 Panel 与 Border 走同一分支，两者等价。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace Starcloud.App.Views.Controls;

/// <summary>跟随光标的内阴影载体：常态透明，悬停时由 HoverShadowBehavior 渐显并跟随指针。</summary>
public sealed class GlowLayer : Grid
{
    /// <summary>档位：中心最深（浅色下 16% 黑），向外递减到全透明。两套主题共用同一组透明度。</summary>
    private static readonly byte[] Alphas = [0x28, 0x1F, 0x16, 0x0D, 0x05];

    /// <summary>
    /// 光斑底色：浅色叠黑（压暗），深色叠白（提亮成淡灰）。
    /// <para>深色下不能沿用黑：深底上再叠黑只会让那一块比背景更暗，看着像塌进去；
    /// 换白之后同一档透明度就是「比背景浅一点」的高光。</para>
    /// </summary>
    internal static Color GlowTint(ElementTheme theme) =>
        theme == ElementTheme.Dark ? Color.FromArgb(255, 255, 255, 255) : Color.FromArgb(255, 0, 0, 0);

    /// <summary>各档透明度（自建光斑层要与这里保持一致）</summary>
    internal static byte[] GlowAlphas => Alphas;

    /// <summary>六档渐变的偏移（最后一档是全透明收尾，颜色固定，不参与主题切换）。</summary>
    private static readonly double[] Offsets = [0d, 0.18d, 0.40d, 0.62d, 0.82d];

    /// <summary>
    /// 强度倍率，1.0 = 上表原值。给「底下几乎没有可压暗面积」的小圆块用：
    /// 标题栏窗口按钮、Nova / 任务中心 / 头像（原先这几处写的是 1.55 倍那套 #3E 档，
    /// 倍率取 1.55 可以把浅色下的观感原样复现）。
    /// </summary>
    public static readonly DependencyProperty PeakProperty = DependencyProperty.Register(
        nameof(Peak), typeof(double), typeof(GlowLayer),
        new PropertyMetadata(1d, (d, _) => ((GlowLayer)d).ApplyAlphas()));

    public double Peak
    {
        get => (double)GetValue(PeakProperty);
        set => SetValue(PeakProperty, value);
    }

    private readonly RadialGradientBrush _brush;

    public GlowLayer()
    {
        Opacity = 0;              // 常态不可见：由行为在悬停时渐显
        IsHitTestVisible = false; // 只做画面，不参与命中测试
        _brush = CreateBrush();
        Background = _brush;
        // 主题切换要立刻换档：否则切一次外观就得重进页面，旧的光斑深浅会留在屏幕上
        ActualThemeChanged += (_, _) => ApplyAlphas();
        ApplyAlphas();
    }

    private static RadialGradientBrush CreateBrush()
    {
        var brush = new RadialGradientBrush
        {
            MappingMode = BrushMappingMode.RelativeToBoundingBox, // 0~1 相对坐标，与行为里的换算一致
            Center = new Point(0.5, 0.5),
            GradientOrigin = new Point(0.5, 0.5),
            RadiusX = 0.9,
            RadiusY = 0.9,
        };
        foreach (var offset in Offsets)
            brush.GradientStops.Add(new GradientStop { Offset = offset });
        brush.GradientStops.Add(new GradientStop { Offset = 1d, Color = Color.FromArgb(0, 0, 0, 0) });
        return brush;
    }

    /// <summary>把当前主题那一套写进画刷的各个渐变点（只改颜色，中心/半径留给行为改写）。</summary>
    private void ApplyAlphas()
    {
        var tint = GlowTint(ActualTheme);
        var peak = Peak <= 0 ? 1d : Peak;   // XAML 里漏写或写 0 时按原值，不至于把光斑抹掉
        for (var i = 0; i < Alphas.Length; i++)
        {
            var a = (byte)Math.Min(byte.MaxValue, Math.Round(Alphas[i] * peak));
            _brush.GradientStops[i].Color = Color.FromArgb(a, tint.R, tint.G, tint.B);
        }
    }
}