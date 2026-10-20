// UI 线程 / 资源 / 图片 / 对话框助手：MAUI 壳的 MainThread、Application.Resources、
// DisplayAlertAsync / DisplayPromptAsync 在 WinUI 方言下的等价实现。

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace Starcloud.Launcher.Uno.Services;

/// <summary>UI 线程调度：任意线程安全；已在 UI 线程则直接执行</summary>
internal static class UiThread
{
    public static DispatcherQueue? Queue { get; private set; }

    public static void Init(DispatcherQueue queue) => Queue = queue;

    public static void Run(Action action)
    {
        var q = Queue;
        if (q is null || q.HasThreadAccess)
        {
            action();
            return;
        }
        q.TryEnqueue(() => action());
    }
}

/// <summary>资源 / 图片 / 可见性小助手</summary>
internal static class Ui
{
    /// <summary>对话框宿主（Shell 根视图加载后注入）</summary>
    public static XamlRoot? Root { get; set; }

    /// <summary>取品牌画刷（键名同 MAUI：Primary / TextSecondary…）</summary>
    public static Brush Brush(string key) =>
        (Brush)Application.Current.Resources[key]!;

    /// <summary>取品牌颜色（键 = 名称 + Color；WinUI 方言下 Color 位于 Windows.UI）</summary>
    public static Windows.UI.Color Color(string key) =>
        (Windows.UI.Color)Application.Current.Resources[key + "Color"]!;

    /// <summary>布尔 → 可见性（MAUI IsVisible 的 WinUI 形态）</summary>
    public static Visibility Vis(bool visible) =>
        visible ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>应用内资产图片（png；resizetizer 打包后经 ms-appx 解析）</summary>
    public static ImageSource Asset(string relative) =>
        new BitmapImage(new Uri($"ms-appx:///{relative}"));

    /// <summary>Nova 品牌标记（SVG 由 resizetizer 出 PNG，按同名 png 引用）</summary>
    public static ImageSource NovaMark => Asset("Assets/Images/nova_mark.png");

    /// <summary>
    /// 绝对路径图片（实例自定义图标 / 皮肤头像缓存）：file:// URI 经 Skia 加载器读取
    /// </summary>
    public static ImageSource FileImage(string absolutePath) =>
        new BitmapImage(new Uri("file://" + absolutePath));

    /// <summary>网络头像（minotar 渲染头部，对齐桌面端）</summary>
    public static ImageSource? HttpImage(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            return null;
        return new BitmapImage(new Uri(url));
    }
}

/// <summary>模态对话框（MAUI DisplayAlert / DisplayPrompt 的 ContentDialog 等价）</summary>
internal static class UiDialog
{
    /// <summary>信息提示（单确认键）；返回 true 表示用户点了确认键</summary>
    public static async Task<bool> AlertAsync(string title, string message, string confirm = "好的")
    {
        var result = await new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirm,
            XamlRoot = Ui.Root,
        }.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>确认对话（确认 + 取消）</summary>
    public static async Task<bool> ConfirmAsync(
        string title, string message, string confirm, string cancel = "取消")
    {
        var result = await new ContentDialog
        {
            Title = title,
            Content = message,
            PrimaryButtonText = confirm,
            CloseButtonText = cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Ui.Root,
        }.ShowAsync();
        return result == ContentDialogResult.Primary;
    }

    /// <summary>单行文本输入（MAUI DisplayPromptAsync 等价）；取消返回 null</summary>
    public static async Task<string?> PromptAsync(
        string title, string message, string confirm, string cancel = "取消",
        string? placeholder = null)
    {
        var input = new TextBox
        {
            PlaceholderText = placeholder ?? string.Empty,
            Margin = new Thickness(0, 8, 0, 0),
        };
        var result = await new ContentDialog
        {
            Title = title,
            Content = new StackPanel
            {
                Spacing = 4,
                Children =
                {
                    new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                    input,
                },
            },
            PrimaryButtonText = confirm,
            CloseButtonText = cancel,
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Ui.Root,
        }.ShowAsync();
        return result == ContentDialogResult.Primary ? input.Text : null;
    }
}
