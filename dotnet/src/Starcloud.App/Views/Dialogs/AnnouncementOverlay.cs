// 云端公告展示层：两个置顶 Popup（全窗遮罩 + 居中卡片）。
//
// 为什么不用 ContentDialog：无边框 + Acrylic 窗口在 125% 缩放下 ContentDialog 定位偏右下。
// 为什么不用挂在 RootGrid 的覆盖层：外壳插件会在启动后重建导航布局，挂在 Grid 上的
// 覆盖层会被整体替换掉（实测「日志显示已挂载、界面看不到」）。Popup 是窗口级独立浮层，
// 不参与布局、天然置顶，关闭只需 IsOpen = false。
//
// 安全边界同前：只渲染 Markdown 文本（MarkdownDocument → RichTextBlock），
// 不解析 HTML、不加载远程资源、不执行脚本；链接仅 http/https 且交系统浏览器。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.Foundation;

namespace Starcloud.App.Views.Dialogs;

internal static class AnnouncementOverlay
{
    private static bool _open;   // 同一时刻只允许一张公告（跨调用防重入）

    /// <summary>展示公告；用户点「知道了」/ 点遮罩 / 按 Esc 后关闭并回调已读（幂等）</summary>
    public static void Show(XamlRoot root, Announcement announcement, Action? onClosed = null)
    {
        if (_open) return;   // 已有公告在显示，避免叠加
        var width = Math.Clamp(root.Size.Width * 0.72, 420, 860);
        var maxHeight = Math.Clamp(root.Size.Height * 0.72, 260, 620);

        // ── 遮罩层（全窗口半透明黑） ──
        var mask = new Border
        {
            Width = root.Size.Width,
            Height = root.Size.Height,
            Background = new SolidColorBrush(Windows.UI.Color.FromArgb(140, 0, 0, 0)),
        };
        var maskPopup = new Popup { Child = mask, XamlRoot = root, IsLightDismissEnabled = false };
        var cardPopup = new Popup { XamlRoot = root, IsLightDismissEnabled = false };

        // ── 卡片 ──
        var title = new TextBlock
        {
            Text = announcement.Title,
            FontSize = 20,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            TextWrapping = TextWrapping.Wrap,
        };

        var meta = new TextBlock { FontSize = 12, Opacity = 0.6, TextWrapping = TextWrapping.Wrap };
        if (!string.IsNullOrWhiteSpace(announcement.PublishedAt))
            meta.Text = Loc.T("发布于 {0}", announcement.PublishedAt);

        var body = new RichTextBlock
        {
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
        };
        Controls.NovaMarkdown.Render(body, announcement.Body);

        // 链接只放行 http/https，点击交给系统浏览器（默认导航被禁用）
        foreach (var hyperlink in HyperlinksOf(body.Blocks))
        {
            var url = hyperlink.NavigateUri?.ToString() ?? "";
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            {
                hyperlink.NavigateUri = null;
                continue;
            }
            hyperlink.NavigateUri = null;
            var target = uri;
            hyperlink.Click += async (_, _) =>
            {
                try { await Windows.System.Launcher.LaunchUriAsync(target); } catch { }
            };
        }

        var scroller = new ScrollViewer
        {
            Content = body,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
        };

        var closeBtn = new Button
        {
            Content = Loc.T("知道了"),
            HorizontalAlignment = HorizontalAlignment.Right,
            MinWidth = 120,
            Margin = new Thickness(0, 14, 0, 0),
        };

        var cardGrid = new Grid();
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        cardGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var header = new StackPanel { Spacing = 2 };
        header.Children.Add(title);
        header.Children.Add(meta);
        Grid.SetRow(header, 0);
        cardGrid.Children.Add(header);

        Grid.SetRow(scroller, 2);
        cardGrid.Children.Add(scroller);

        Grid.SetRow(closeBtn, 3);
        cardGrid.Children.Add(closeBtn);

        var card = new Border
        {
            Width = width,
            MaxHeight = maxHeight,
            Background = (Brush)Application.Current.Resources["SolidBackgroundFillColorBaseBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(28, 22, 28, 18),
            Child = cardGrid,
        };

        // ── 关闭（幂等）+ 已读回调 ──
        var closed = false;
        void Close()
        {
            if (closed) return;
            closed = true;
            _open = false;
            maskPopup.IsOpen = false;
            cardPopup.IsOpen = false;
            try { AppServices.Instance.Announcements.MarkSeen(announcement); } catch { }
            onClosed?.Invoke();
        }
        closeBtn.Click += (_, _) => Close();
        mask.Tapped += (_, _) => Close();

        // 先量卡片高度再居中摆放；遮罩先开，卡片紧随其后置顶
        card.Measure(new Size(width, double.PositiveInfinity));
        var cardHeight = Math.Min(card.DesiredSize.Height, maxHeight);
        cardPopup.Child = card;
        cardPopup.HorizontalOffset = Math.Max(16, (root.Size.Width - width) / 2);
        cardPopup.VerticalOffset = Math.Max(24, (root.Size.Height - cardHeight) / 2);
        maskPopup.Opened += (_, _) => cardPopup.IsOpen = true;
        maskPopup.IsOpen = true;

        // Popup 不接收键盘：Esc 关闭挂在根元素上（向导/主窗口均可）
        if (root.Content is FrameworkElement rootEl)
            rootEl.KeyDown += (_, e) => { if (e.Key == Windows.System.VirtualKey.Escape) Close(); };
    }

    /// <summary>收集正文里的超链接内联（NovaMarkdown 的列表项也是 Paragraph，只需看 Paragraph）</summary>
    private static IEnumerable<Hyperlink> HyperlinksOf(BlockCollection blocks)
    {
        foreach (var block in blocks)
        {
            if (block is Paragraph paragraph)
                foreach (var inline in paragraph.Inlines)
                    if (inline is Hyperlink link)
                        yield return link;
        }
    }
}
