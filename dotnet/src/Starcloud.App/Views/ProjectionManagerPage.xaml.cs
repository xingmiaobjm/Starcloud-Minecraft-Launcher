// 投影管理页：把散落在全局目录和各版本实例里的投影收在一张表里，
// 直接在界面上复制/移动到需要的版本（投影隔离时不会自动共享）。

using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Localization;
using Starcloud.App.Services;
using Starcloud.App.Views.Dialogs;
using Starcloud.Core.Kernel.Contracts;

namespace Starcloud.App.Views;

/// <summary>投影列表行（页面级模型，供 x:DataType）</summary>
public sealed class ProjectionRow
{
    public ProjectionRow(ProjectionLibraryFile file)
    {
        FilePath = file.FilePath;
        Name = file.FileName;
        LocationText = file.VersionId is null ? Loc.T("全局") : file.VersionId;
        var size = file.SizeBytes >= 1024 * 1024
            ? $"{file.SizeBytes / 1048576.0:F1} MB"
            : $"{Math.Max(1, file.SizeBytes / 1024)} KB";
        Subtitle = $"{size} · {file.ModifiedAt:MM-dd HH:mm} · {(file.VersionId is null ? Loc.T("全局目录") : Loc.T("版本 {0}", file.VersionId))}";
    }

    public string FilePath { get; }
    public string Name { get; }
    public string Subtitle { get; }
    public string LocationText { get; }
}

public sealed partial class ProjectionManagerPage : Page
{
    private readonly AppServices _s = AppServices.Instance;

    public ProjectionManagerPage()
    {
        InitializeComponent();
        Loaded += (_, _) => Refresh();
    }

    private ProjectionRow? Selected => ProjectionList.SelectedItem as ProjectionRow;

    private void Refresh()
    {
        try
        {
            var files = _s.ProjectionLibrary.Scan();
            ProjectionList.ItemsSource = files.Select(f => new ProjectionRow(f)).ToList();
            MotionFx.StaggerListItems(ProjectionList);
            EmptyText.Visibility = files.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            var selected = TargetCombo.SelectedIndex;
            TargetCombo.Items.Clear();
            TargetCombo.Items.Add(Loc.T("全局（共享目录）"));
            foreach (var version in _s.ProjectionLibrary.ListVersions()) TargetCombo.Items.Add(version);
            TargetCombo.SelectedIndex = selected >= 0 && selected < TargetCombo.Items.Count ? selected : 0;

            UpdateButtons();
            SetStatus("");
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("扫描失败：{0}", ex.Message));
        }
    }

    /// <summary>null = 全局目录，否则版本 id</summary>
    private string? SelectedTarget() =>
        TargetCombo.SelectedIndex <= 0 ? null : TargetCombo.SelectedItem as string;

    private void UpdateButtons()
    {
        var has = Selected is not null;
        CopyBtn.IsEnabled = has;
        MoveBtn.IsEnabled = has;
        RevealBtn.IsEnabled = has;
        DeleteBtn.IsEnabled = has;
        // 四个按钮全灰时给出"先选一行"的引导，选中后收起
        SelectHint.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetStatus(string text) => StatusText.Text = text;

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void ProjectionList_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateButtons();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        try
        {
            var dest = _s.ProjectionLibrary.CopyTo(row.FilePath, SelectedTarget());
            SetStatus(Loc.T("已复制到 {0}", dest));
            Refresh();
            SetStatus(Loc.T("已复制到 {0}", dest));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("复制失败：{0}", ex.Message));
        }
    }

    private async void Move_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var dialog = new ThemedDialog
        {
            Title = Loc.T("移动投影"),
            Content = Loc.T("确定把「{0}」移动到该版本吗？原位置的文件会被删除。", row.Name),
            PrimaryButtonText = Loc.T("移动"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            var dest = _s.ProjectionLibrary.MoveTo(row.FilePath, SelectedTarget());
            Refresh();
            SetStatus(Loc.T("已移动到 {0}", dest));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("移动失败：{0}", ex.Message));
        }
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        try
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{row.FilePath}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("打开失败：{0}", ex.Message));
        }
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (Selected is not { } row) return;
        var dialog = new ThemedDialog
        {
            Title = Loc.T("删除投影"),
            Content = Loc.T("确定删除「{0}」吗？此操作不可撤销。", row.Name),
            PrimaryButtonText = Loc.T("删除"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try
        {
            _s.ProjectionLibrary.Delete(row.FilePath);
            Refresh();
            SetStatus(Loc.T("已删除「{0}」", row.Name));
        }
        catch (Exception ex)
        {
            SetStatus(Loc.T("删除失败：{0}", ex.Message));
        }
    }
}
