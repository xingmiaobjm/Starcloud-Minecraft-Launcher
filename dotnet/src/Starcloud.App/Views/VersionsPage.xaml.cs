// 版本页：本地已安装版本列表（类型推断 / 删除 / 打开目录），PCL2 版本列表风格

using System.IO;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Services;
using Starcloud.Core.Services;

namespace Starcloud.App.Views;

public sealed partial class VersionsPage : Page
{
    private readonly AppServices _s = AppServices.Instance;

    public VersionsPage()
    {
        InitializeComponent();
        Loaded += (_, _) => RefreshList();
    }

    private void RefreshList()
    {
        DirText.Text = _s.Settings.DotMinecraft;
        var installed = _s.Manifest.ListInstalled(_s.Settings.DotMinecraft);
        VersionListView.ItemsSource = installed
            .Select(i => new LocalVersionRow(i.Id, DetectKind(_s.Settings.DotMinecraft, i.Id), FormatSize(i.SizeBytes)))
            .ToList();
        EmptyHint.Visibility = installed.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>从版本 JSON 推断类型（NeoForge / Fabric / Forge / 原版类型）</summary>
    private static string DetectKind(string dotMc, string id)
    {
        try
        {
            var json = File.ReadAllText(Path.Combine(dotMc, "versions", id, id + ".json"));
            if (json.Contains("neoforge", StringComparison.OrdinalIgnoreCase)) return "NeoForge";
            if (json.Contains("fabricloader", StringComparison.OrdinalIgnoreCase) ||
                json.Contains("fabricmc", StringComparison.OrdinalIgnoreCase)) return "Fabric";
            if (json.Contains("forge", StringComparison.OrdinalIgnoreCase)) return "Forge";
            if (json.Contains("\"old_beta\"", StringComparison.Ordinal)) return "Beta 版";
            if (json.Contains("\"old_alpha\"", StringComparison.Ordinal)) return "Alpha 版";
            if (json.Contains("\"snapshot\"", StringComparison.Ordinal)) return "快照版";
            return "正式版";
        }
        catch
        {
            return "未知";
        }
    }

    private void Refresh_Click(object s, RoutedEventArgs e) => RefreshList();

    private void OpenDir_Click(object s, RoutedEventArgs e)
    {
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = _s.Settings.DotMinecraft,
            UseShellExecute = true,
        });
    }

    private async void Delete_Click(object s, RoutedEventArgs e)
    {
        if (s is not Button btn || btn.DataContext is not LocalVersionRow row) return;
        var dlg = new ContentDialog
        {
            Title = "删除版本",
            Content = $"确定要删除版本 {row.Id} 吗？\n将删除其版本文件夹，此操作不可恢复。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            InstalledVersionManager.Remove(_s.Settings.DotMinecraft, row.Id);
        }
        catch (Exception ex)
        {
            var err = new ContentDialog
            {
                Title = "删除失败",
                Content = ex.Message,
                CloseButtonText = "确定",
                XamlRoot = Content.XamlRoot,
            };
            await err.ShowAsync();
        }
        RefreshList();
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        <= 0 => "未完成",
        < 1024 * 1024 => $"{bytes / 1024.0:F0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / 1024.0 / 1024:F1} MB",
        _ => $"{bytes / 1024.0 / 1024 / 1024:F2} GB",
    };
}
