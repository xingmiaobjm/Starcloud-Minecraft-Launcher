// 存档页：列出 saves 目录世界，支持备份 zip / 删除 / 打开文件夹

using System.Diagnostics;
using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class SavesPage : Page
{
    private readonly AppServices _s = AppServices.Instance;
    private List<SaveRow> _rows = [];
    private bool _busy;

    public SavesPage()
    {
        InitializeComponent();
        Loaded += (_, _) => LoadSaves();
    }

    private void LoadSaves()
    {
        if (_busy) return;
        try
        {
            var saves = _s.Saves.ListSaves(_s.Settings.DotMinecraft);
            _rows = saves.Select(v => new SaveRow(
                v.FolderName, v.LevelName, v.IconPath,
                v.LastPlayed is { } t ? $"上次游玩 {t:yyyy-MM-dd HH:mm}" : "无游玩记录",
                FormatSize(v.SizeBytes))).ToList();
        }
        catch (Exception ex)
        {
            _rows = [];
            ErrorBar.Message = $"读取存档失败：{ex.Message}";
            ErrorBar.IsOpen = true;
        }
        SaveList.ItemsSource = _rows;
        EmptyHint.Visibility = _rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateRowActions();
    }

    private void UpdateRowActions()
    {
        var has = SaveList.SelectedItem is SaveRow;
        BackupBtn.IsEnabled = has && !_busy;
        DeleteBtn.IsEnabled = has && !_busy;
        OpenFolderBtn.IsEnabled = has;
    }

    private void SaveList_SelectionChanged(object s, SelectionChangedEventArgs e) => UpdateRowActions();

    private void Refresh_Click(object s, RoutedEventArgs e) => LoadSaves();

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "空",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };

    private static void OpenInExplorer(string path, bool select)
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = select ? $"/select,\"{path}\"" : $"\"{path}\"",
            UseShellExecute = true,
        });
    }

    private void OpenFolder_Click(object s, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row) return;
        var dir = Path.Combine(_s.Settings.DotMinecraft, "saves", row.FolderName);
        if (Directory.Exists(dir)) OpenInExplorer(dir, select: false);
    }

    private void OpenSavesDir_Click(object s, RoutedEventArgs e)
    {
        var dir = Path.Combine(_s.Settings.DotMinecraft, "saves");
        Directory.CreateDirectory(dir);
        OpenInExplorer(dir, select: false);
    }

    private async void Backup_Click(object s, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row || _busy) return;
        var dlg = new ContentDialog
        {
            Title = "备份世界",
            Content = $"将「{row.Name}」压缩备份到 backups 文件夹，可能需要一些时间，继续吗？",
            PrimaryButtonText = "开始备份",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        _busy = true;
        BackupProgress.Visibility = Visibility.Visible;
        UpdateRowActions();
        try
        {
            var zipPath = await Task.Run(() =>
                _s.Saves.Backup(_s.Settings.DotMinecraft, row.FolderName));
            OkBar.Message = $"已备份到 {zipPath}";
            OkBar.IsOpen = true;
        }
        catch (Exception ex)
        {
            ErrorBar.Message = $"备份失败：{ex.Message}";
            ErrorBar.IsOpen = true;
        }
        finally
        {
            _busy = false;
            BackupProgress.Visibility = Visibility.Collapsed;
            UpdateRowActions();
        }
    }

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        if (SaveList.SelectedItem is not SaveRow row || _busy) return;
        var dlg = new ContentDialog
        {
            Title = "删除世界",
            Content = $"确定要删除「{row.Name}」吗？\n世界文件夹将被移除，建议先备份。此操作不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        try
        {
            _s.Saves.Delete(_s.Settings.DotMinecraft, row.FolderName);
            LoadSaves();
        }
        catch (Exception ex)
        {
            ErrorBar.Message = $"删除失败：{ex.Message}";
            ErrorBar.IsOpen = true;
        }
    }
}
