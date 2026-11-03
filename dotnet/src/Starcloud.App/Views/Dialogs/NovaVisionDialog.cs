// 游戏画面问答对话框：缩略图 + 一句话问题 → Nova 看图回答。
//
// 为什么用 PrimaryButtonClick + Cancel 而不是 ShowAsync 后再问：点「问 Nova」要留在
// 对话框里等结果（几秒），关掉再弹 Toast 会丢掉上下文；ContentDialog 的标准异步做法
// 是取消关闭、自己 await，再更新内容。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.Storage.Streams;

namespace Starcloud.App.Views.Dialogs;

public sealed class NovaVisionDialog : ContentDialog
{
    private readonly AppServices _s = AppServices.Instance;
    private readonly byte[] _jpeg;
    private readonly string _source;
    private readonly TextBox _question;
    private readonly TextBlock _answer;
    private bool _busy;

    public NovaVisionDialog(byte[] jpeg, string source, XamlRoot root)
    {
        _jpeg = jpeg;
        _source = source;

        Title = "Nova 看画面";
        PrimaryButtonText = "问 Nova";
        CloseButtonText = "关闭";
        DefaultButton = ContentDialogButton.Primary;
        XamlRoot = root;
        // 弹层不继承窗口主题（WinUI 已知问题）：与 ThemedDialog 同一套修法
        Loading += (_, _) =>
        {
            if (XamlRoot?.Content is FrameworkElement host) RequestedTheme = host.ActualTheme;
        };
        ThemedDialog.ApplyShadowButtons(this);

        var thumb = new Border
        {
            CornerRadius = new CornerRadius(8),
            Height = 132,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Microsoft.UI.Colors.Black),
        };
        var image = new Image { Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill };
        thumb.Child = image;
        _ = LoadThumbAsync(image);

        _question = new TextBox
        {
            Text = "结合画面，给我一条最实用的建议",
            PlaceholderText = "想问什么？例如：这地方有什么值得注意的？",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            MaxHeight = 72,
        };

        _answer = new TextBlock
        {
            TextWrapping = TextWrapping.Wrap,
            IsTextSelectionEnabled = true,
            FontSize = 13,
            Visibility = Visibility.Collapsed,
        };

        Content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new TextBlock
                {
                    Text = $"画面来源：{_source}",
                    FontSize = 11,
                    Opacity = 0.6,
                },
                thumb,
                _question,
                _answer,
            },
        };

        PrimaryButtonClick += OnAskAsync;
    }

    private async Task LoadThumbAsync(Image image)
    {
        try
        {
            var bmp = new BitmapImage { DecodePixelWidth = 640 };
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(_jpeg);
                await writer.StoreAsync();
                await writer.FlushAsync();
                writer.DetachStream();
            }
            stream.Seek(0);
            await bmp.SetSourceAsync(stream);
            image.Source = bmp;
        }
        catch
        {
            // 缩略图加载失败不影响提问
        }
    }

    private async void OnAskAsync(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_busy)
        {
            args.Cancel = true;
            return;
        }
        var question = _question.Text?.Trim();
        if (string.IsNullOrEmpty(question))
        {
            args.Cancel = true;
            return;
        }
        if (_s.StarcloudId.Current?.ApiKey is not { Length: > 0 })
        {
            args.Cancel = true;
            _answer.Text = "需要登录 Starcloud ID 才能让 Nova 看画面。";
            _answer.Visibility = Visibility.Visible;
            return;
        }

        // 保持打开、自己等结果
        args.Cancel = true;
        _busy = true;
        IsPrimaryButtonEnabled = false;
        _answer.Text = "Nova 正在看画面…";
        _answer.Visibility = Visibility.Visible;
        try
        {
            var (text, notice) = await _s.Nova.ChatWithImageAsync(
                question, _jpeg, $"画面来源：{_source}", _s.Settings);
            _answer.Text = text is null
                ? notice ?? "这次没看成画面，稍后再试。"
                : notice is null ? text : text + "\n\n（" + notice + "）";
        }
        catch (Exception ex)
        {
            _answer.Text = "提问失败：" + ex.Message;
        }
        finally
        {
            _busy = false;
            IsPrimaryButtonEnabled = true;
            PrimaryButtonText = "再问一次";
        }
    }
}
