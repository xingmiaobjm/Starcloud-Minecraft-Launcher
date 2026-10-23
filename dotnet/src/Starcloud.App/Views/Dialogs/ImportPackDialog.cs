// 「导入整合包」入口弹窗：三种方式任选——拖入文件、选择文件、从链接导入。
// 之前是两颗按钮（导入整合包 / 从链接导入）加整页拖拽，入口分散；现在统一收进一个窗口。
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Starcloud.App.Views.Dialogs;

internal static class ImportPackDialog
{
    /// <summary>弹出导入方式选择窗；导入完成后返回 true（调用方据此刷新列表）</summary>
    public static async Task ShowAsync(XamlRoot root, AppServices s)
    {
        var dialog = new ThemedDialog
        {
            Title = Loc.T("导入整合包"),
            CloseButtonText = Loc.T("取消"),
            XamlRoot = root,
        };

        // ── 拖放区 ──
        var dropHint = new TextBlock
        {
            Text = Loc.T("把整合包拖到这里"),
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
        };
        var dropSub = new TextBlock
        {
            Text = Loc.T("支持 .mrpack、.zip（Modrinth / CurseForge / MultiMC / packwiz / 通用压缩包）"),
            FontSize = 12,
            Opacity = 0.7,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center,
            Margin = new Thickness(0, 4, 0, 0),
        };
        var dropStack = new StackPanel();
        dropStack.Children.Add(dropHint);
        dropStack.Children.Add(dropSub);

        var dropZone = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1.5),
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
            Background = (Brush)Application.Current.Resources["GlassCardBrush"],
            Padding = new Thickness(24, 26, 24, 26),
            Child = dropStack,
            AllowDrop = true,
        };
        dropZone.DragOver += (_, e) =>
        {
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = Loc.T("导入整合包");
            e.DragUIOverride.IsCaptionVisible = true;
            dropZone.BorderBrush = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        };
        dropZone.DragLeave += (_, _) =>
            dropZone.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
        dropZone.Drop += async (_, e) =>
        {
            dropZone.BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"];
            if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
            IReadOnlyList<IStorageItem> items;
            try { items = await e.DataView.GetStorageItemsAsync(); }
            catch { return; }
            var file = items.OfType<StorageFile>().FirstOrDefault(f =>
                f.Name.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ||
                f.Name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase));
            if (file is null) return;
            dialog.Hide();
            await ModPackDialogs.ImportAsync(root, s, file.Path);
        };

        // ── 两个按钮 ──
        var pick = new Button
        {
            Content = Loc.T("选择文件"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(12, 8, 12, 8),
        };
        pick.Click += async (_, _) =>
        {
            var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.Downloads };
            WinRT.Interop.InitializeWithWindow.Initialize(picker,
                WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
            picker.FileTypeFilter.Add(".mrpack");
            picker.FileTypeFilter.Add(".zip");
            var file = await picker.PickSingleFileAsync();
            if (file is null) return;
            dialog.Hide();
            await ModPackDialogs.ImportAsync(root, s, file.Path);
        };

        var fromLink = new Button
        {
            Content = Loc.T("从链接导入"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(12, 8, 12, 8),
        };
        fromLink.Click += async (_, _) =>
        {
            dialog.Hide();
            await ModPackDialogs.ImportFromLinkAsync(root, s);
        };

        var row = new Grid { ColumnSpacing = 8 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(pick, 0);
        Grid.SetColumn(fromLink, 1);
        row.Children.Add(pick);
        row.Children.Add(fromLink);

        var panel = new StackPanel { Spacing = 12, MinWidth = 420 };
        panel.Children.Add(dropZone);
        panel.Children.Add(row);
        dialog.Content = panel;
        await dialog.ShowAsync();
    }
}
