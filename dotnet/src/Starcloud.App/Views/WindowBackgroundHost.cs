// 窗口背景宿主：背景图 / 模糊层 / 毛玻璃遮罩三层（设计：custom-background-theme-design.md §6）
//
// 层序（自下而上）：SystemBackdrop 亚克力 → 本类三层 → 常规 XAML 内容。
// 模糊用 composition 的 GaussianBlurEffect 作用于图片表面（而非 backdrop），
// 因此「背景不透明度」语义干净（不会与背后的清晰图重影）；模糊不可用时静默退化为清晰图。

using System.Numerics;
using Microsoft.Graphics.Canvas.Effects;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.UI;

namespace Starcloud.App.Views;

internal sealed class WindowBackgroundHost
{
    private readonly Grid _root;
    private readonly Image _image;
    private readonly Border _blurHost;
    private readonly Border _mask;

    private Compositor? _compositor;
    private SpriteVisual? _blurVisual;
    private CompositionEffectBrush? _blurBrush;
    private CompositionSurfaceBrush? _surfaceBrush;
    private LoadedImageSurface? _surface;
    private string? _surfacePath;
    private string? _imagePath;
    private bool _blurUnavailable;

    public WindowBackgroundHost(Grid root, Image image, Border blurHost, Border mask)
    {
        _root = root;
        _image = image;
        _blurHost = blurHost;
        _mask = mask;
    }

    /// <summary>当前是否启用了自定义背景（未启用 = 纯亚克力外观）。</summary>
    public bool IsActive { get; private set; }

    /// <summary>按最新设置重建背景视觉。</summary>
    public void Apply(LauncherSettings settings, ElementTheme theme)
    {
        var path = AppearanceService.ResolveBackgroundPath(settings);
        if (string.IsNullOrEmpty(path) || !TrySetImage(path))
        {
            IsActive = false;
            _root.Visibility = Visibility.Collapsed;
            _image.Source = null;
            _imagePath = null;
            _image.Visibility = Visibility.Visible;
            _blurHost.Visibility = Visibility.Collapsed;
            return;
        }

        var opacity = Math.Clamp(settings.BackgroundImageOpacity, 0.2, 1.0);
        var radius = Math.Clamp(settings.BackgroundBlurRadius, 0, 64);

        _image.Opacity = opacity;
        _mask.Background = new SolidColorBrush(theme == ElementTheme.Dark
            ? Color.FromArgb(0xFF, 0x14, 0x14, 0x14)
            : Color.FromArgb(0xFF, 0xFC, 0xFC, 0xFC));
        _mask.Opacity = AppearanceService.ResolveMaskOpacity(settings, theme);

        var blurred = radius > 0.5 && ApplyBlur(path, opacity, radius);
        _blurHost.Visibility = blurred ? Visibility.Visible : Visibility.Collapsed;
        _image.Visibility = blurred ? Visibility.Collapsed : Visibility.Visible;

        _root.Visibility = Visibility.Visible;
        IsActive = true;
    }

    /// <summary>窗口尺寸变化时同步模糊 SpriteVisual 尺寸。</summary>
    public void Resize(double width, double height)
    {
        if (_blurVisual is null) return;
        _blurVisual.Size = new Vector2((float)Math.Max(1, width), (float)Math.Max(1, height));
    }

    private bool TrySetImage(string path)
    {
        if (_imagePath == path && _image.Source is not null) return true;
        try
        {
            _image.Source = new BitmapImage(new Uri(path));
            _imagePath = path;
            return true;
        }
        catch
        {
            return false;
        }
    }

    private bool ApplyBlur(string path, double opacity, double radius)
    {
        if (!EnsureBlurVisual() || !SetBlurSource(path)) return false;
        _blurBrush!.Properties.InsertScalar("Blur.BlurAmount", (float)radius);
        _blurVisual!.Opacity = (float)opacity;
        Resize(_root.ActualWidth, _root.ActualHeight);
        return true;
    }

    private bool EnsureBlurVisual()
    {
        if (_blurVisual is not null) return true;
        if (_blurUnavailable) return false;
        try
        {
            var compositor = ElementCompositionPreview.GetElementVisual(_blurHost).Compositor;
            var effect = new GaussianBlurEffect
            {
                Name = "Blur",
                BlurAmount = 24f,
                BorderMode = EffectBorderMode.Hard,
                Optimization = EffectOptimization.Speed,
                Source = new CompositionEffectSourceParameter("Source"),
            };
            var factory = compositor.CreateEffectFactory(effect, new[] { "Blur.BlurAmount" });
            _blurBrush = factory.CreateBrush();

            var visual = compositor.CreateSpriteVisual();
            visual.Brush = _blurBrush;
            visual.Size = new Vector2(1, 1);
            ElementCompositionPreview.SetElementChildVisual(_blurHost, visual);

            _compositor = compositor;
            _blurVisual = visual;
            return true;
        }
        catch
        {
            // 组合器不可用（元素尚未进入可视树 / 平台不支持）→ 永久退化为清晰背景图
            _blurUnavailable = true;
            return false;
        }
    }

    private bool SetBlurSource(string path)
    {
        if (_surfacePath == path && _surfaceBrush is not null) return true;
        if (_blurBrush is null || _compositor is null) return false;
        try
        {
            var surface = LoadedImageSurface.StartLoadFromUri(new Uri(path));
            var brush = _compositor.CreateSurfaceBrush(surface);
            brush.Stretch = CompositionStretch.UniformToFill;
            _blurBrush.SetSourceParameter("Source", brush);

            _surface?.Dispose();
            _surface = surface;
            _surfaceBrush = brush;
            _surfacePath = path;
            return true;
        }
        catch
        {
            return false;
        }
    }
}
