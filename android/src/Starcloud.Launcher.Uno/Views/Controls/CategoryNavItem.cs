// 可复用元素：左侧分类导航项（下载页的分区菜单、设置页的分区菜单）。
//
// 此前这两处共 10 个条目，调用点里只写内容（一个 FontIcon + 一个 TextBlock），选中/悬停
// 反馈全靠框架 ListViewItem 自带的那层底色 —— 它是硬边直角矩形，和行内圆角、跟手光斑摞在
// 一起时四个角互相啃掉一块；想让底色换成圆角、或加一层光斑，也没有统一的落点。
//
// 现在收进控件与模板：选中底 / 悬停底 / 跟手光斑三层都在模板里，圆角统一读
// {TemplateBinding CornerRadius}，选中态改由 IsActive 表达；框架那层直角矩形在页面侧
// 的 ListView.Resources 里被置为 Transparent 让位。调用点只写一个标签：
//     <ctrl:CategoryNavItem x:Name="CatVanilla" IsActive="True" Glyph="&#xEC20;" loc:Loc.Text="原版游戏" />
// 标签走 Content：loc:Loc.Text 对 ContentControl 会把译文写进 Content（本地化与切语言照旧）。
// 需要自绘图标（设置页的 Java 咖啡杯）时改用 Icon 槽，它会盖掉 Glyph。
//
// 位移只有一个来源：上浮与按下反馈交给 RowHoverBehavior（它还要驱动悬停底色），
// 本控件把 HoverShadowBehavior 自己的上移关掉（SetLiftEnabled(false)）——否则两个行为
// 各抬一次，悬停时会合成出一次多余的上移。
//
// 模板见 App.xaml 的 ScCategoryNavItemTemplate。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.Launcher.Uno.Views.Behaviors;

namespace Starcloud.Launcher.Uno.Views.Controls;

/// <summary>左侧分类导航项：选中底 + 悬停底 + 跟手光斑都写在模板里，选中态由 <see cref="IsActive"/> 表达。</summary>
public sealed class CategoryNavItem : ContentControl
{
    /// <summary>模板里那层选中底（ScCategoryNavItemTemplate 里的 ActiveLayer）</summary>
    private const string ActiveLayerPartName = "ActiveLayer";
    /// <summary>模板里那层悬停底（由 RowHoverBehavior 驱动 Opacity）</summary>
    private const string HoverLayerPartName = "HoverLayer";
    /// <summary>模板里那层跟手光斑（由 HoverShadowBehavior 驱动 Opacity）</summary>
    private const string GlowPartName = "Glow";
    /// <summary>模板里那个字形图标（有自定义 <see cref="Icon"/> 时让位）</summary>
    private const string GlyphPartName = "GlyphIcon";

    /// <summary>
    /// 是否为当前选中项（选中底的不透明度 0/1）。切换是瞬时的，不做动画——
    /// 动画只留给悬停反馈，选中态跳变反而更利落。
    /// </summary>
    public static readonly DependencyProperty IsActiveProperty =
        DependencyProperty.Register(
            nameof(IsActive),
            typeof(bool),
            typeof(CategoryNavItem),
            new PropertyMetadata(false, OnIsActiveChanged));

    public bool IsActive
    {
        get => (bool)GetValue(IsActiveProperty);
        set => SetValue(IsActiveProperty, value);
    }

    /// <summary>
    /// 选中底 / 悬停底 / 光斑三层的统一圆角（模板 TemplateBinding 读取）。
    /// Uno 的 Control 没有 CornerRadius 属性（WinUI 有），样式 Setter 与 TemplateBinding
    /// 都会静默失效——桌面圆角正常、Uno 方角的根因即此，故显式注册（new 隐藏基类同名成员）。
    /// </summary>
    public static readonly new DependencyProperty CornerRadiusProperty =
        DependencyProperty.Register(
            nameof(CornerRadius),
            typeof(CornerRadius),
            typeof(CategoryNavItem),
            new PropertyMetadata(new CornerRadius(10)));

    public new CornerRadius CornerRadius
    {
        get => (CornerRadius)GetValue(CornerRadiusProperty);
        set => SetValue(CornerRadiusProperty, value);
    }

    /// <summary>字形图标（HarmonyOS 图标库的字形码）。<see cref="Icon"/> 非空时被它盖掉。</summary>
    public static readonly DependencyProperty GlyphProperty =
        DependencyProperty.Register(
            nameof(Glyph),
            typeof(string),
            typeof(CategoryNavItem),
            new PropertyMetadata(string.Empty));


    public string Glyph
    {
        get => (string)GetValue(GlyphProperty);
        set => SetValue(GlyphProperty, value);
    }

    /// <summary>
    /// 自绘图标，任意元素（设置页的 Java 咖啡杯是描边 Path + Viewbox，字形库里没有）。
    /// 槽位必须是 object 而不是 IconElement：PathIcon 是填充式的，复现不出那套线稿。
    /// </summary>
    public static readonly DependencyProperty IconProperty =
        DependencyProperty.Register(
            nameof(Icon),
            typeof(object),
            typeof(CategoryNavItem),
            new PropertyMetadata(null, OnIconChanged));

    public object? Icon
    {
        get => GetValue(IconProperty);
        set => SetValue(IconProperty, value);
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();

        // 顺序要紧：先把两个载体认下来，再开行为开关。反过来的话第一次接线时还没有载体，
        // 行为会先自建一层光斑，等载体到位再拆掉重建 —— 白白闪一帧。
        if (GetTemplateChild(GlowPartName) is FrameworkElement glow)
            HoverShadowBehavior.SetCarrier(this, glow);

        if (GetTemplateChild(HoverLayerPartName) is FrameworkElement hover)
            RowHoverBehavior.SetOverlay(this, hover);

        // 位移只有一个来源：上浮 / 按下交给 RowHoverBehavior，这里关掉光斑行为的自带位移
        HoverShadowBehavior.SetLiftEnabled(this, false);
        HoverShadowBehavior.SetIsEnabled(this, true);
        RowHoverBehavior.SetIsEnabled(this, true);

        ApplyActive();
        ApplyIcon();
    }

    private static void OnIsActiveChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CategoryNavItem)d).ApplyActive();

    private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((CategoryNavItem)d).ApplyIcon();

    /// <summary>把选中态落到模板里那层选中底上（模板还没套上时什么也不做，套上时 OnApplyTemplate 会再调一次）</summary>
    private void ApplyActive()
    {
        if (GetTemplateChild(ActiveLayerPartName) is Border active)
            active.Opacity = IsActive ? 1 : 0;
    }

    /// <summary>给了自绘图标就让字形图标让位（反之亦然），两个槽不会同时画</summary>
    private void ApplyIcon()
    {
        if (GetTemplateChild(GlyphPartName) is FontIcon glyph)
            glyph.Visibility = Icon is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
