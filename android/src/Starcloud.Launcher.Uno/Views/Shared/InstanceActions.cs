// 版本实例操作对话框（重命名 / 复制 / 查看 JSON）：VersionsPage 与 VersionDetailPage 共用
// 移植自桌面 Starcloud.App.Views.Shared.InstanceActions（API 签名保持一致，供并行移植页共用）。
// 安卓适配：「打开所在文件夹」无桌面资源管理器语义 → 弹窗展示目录路径（与版本页打开目录同口径）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.Launcher.Uno.Localization;
using Starcloud.Launcher.Uno.Services;
using Starcloud.Launcher.Uno.Views.Controls;
using Starcloud.Launcher.Uno.Views.Dialogs;
using Starcloud.Core.Services;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Starcloud.Launcher.Uno.Views.Shared;

/// <summary>实例操作结果（null = 用户取消）</summary>
public sealed record InstanceActionResult(string Id, Exception? Error);

public static class InstanceActions
{
    /// <summary>重命名：ContentDialog 输入新 ID → RenameInstance + store.RenameKey。</summary>
    public static async Task<InstanceActionResult?> RenameAsync(XamlRoot xamlRoot,
        string dotMc, string oldId)
    {
        var box = new TextBox { PlaceholderText = Loc.T("新的版本名"), Text = oldId };
        var hint = new TextBlock { Foreground = null, FontSize = 12, Opacity = 0.7 };
        var dlg = new ThemedDialog
        {
            Title = Loc.T("重命名版本"),
            Content = new StackPanel { Spacing = 8, Children = { box, hint } },
            PrimaryButtonText = Loc.T("重命名"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        box.TextChanged += (_, _) =>
        {
            var name = box.Text.Trim();
            var invalid = name is "." or ".." ||
                name.IndexOfAny(['<', '>', ':', '"', '/', '\\', '|', '?', '*']) >= 0;
            hint.Text = name.Length == 0 ? Loc.T("版本名不能为空")
                : invalid ? Loc.T("不能包含 < > : \" / \\ | ? *")
                : "";
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        var newId = box.Text.Trim();
        try
        {
            InstanceLayout.RenameInstance(dotMc, oldId, newId);
            var store = new InstanceSettingsStore(dotMc);
            store.RenameKey(oldId, newId);
            return new InstanceActionResult(newId, null);
        }
        catch (Exception ex)
        {
            return new InstanceActionResult(oldId, ex);
        }
    }

    /// <summary>复制版本：新 ID + 可选复制隔离内容 → CopyInstance + 档案深拷贝（时间/图标置空）。</summary>
    public static async Task<InstanceActionResult?> CopyAsync(XamlRoot xamlRoot,
        string dotMc, string sourceId, bool sourceIsolated)
    {
        var box = new TextBox { PlaceholderText = Loc.T("例如 {0}-copy", sourceId) };
        var chk = new CheckBox
        {
            Content = Loc.T("同时复制 mods / 存档等隔离内容"),
            IsChecked = false,
            Visibility = sourceIsolated ? Visibility.Visible : Visibility.Collapsed,
        };
        var dlg = new ThemedDialog
        {
            Title = Loc.T("复制版本"),
            Content = new StackPanel { Spacing = 12, Children = { box, chk } },
            PrimaryButtonText = Loc.T("复制"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = xamlRoot,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;
        var newId = box.Text.Trim();
        if (newId.Length == 0)
            return new InstanceActionResult(sourceId, new ArgumentException(Loc.T("版本名不能为空")));
        try
        {
            InstanceLayout.CopyInstance(dotMc, sourceId, newId, chk.IsChecked == true);
            // 档案深拷贝（Get 返回字典内对象引用，JSON 往返避免污染原档案）：类别/备注等沿用，时间与图标从零开始
            var store = new InstanceSettingsStore(dotMc);
            var copy = JsonSerializer.Deserialize<VersionProfile>(
                JsonSerializer.Serialize(store.Get(sourceId))) ?? new VersionProfile();
            copy.LastPlayedAt = null;
            copy.IconPath = null;
            store.Set(newId, copy);
            store.Save();
            return new InstanceActionResult(newId, null);
        }
        catch (Exception ex)
        {
            return new InstanceActionResult(sourceId, ex);
        }
    }

    /// <summary>错误提示（重命名/复制失败统一弹窗）。</summary>
    public static async Task ShowErrorAsync(XamlRoot xamlRoot, string title, Exception error)
    {
        await new ThemedDialog
        {
            Title = title,
            Content = error.Message,
            CloseButtonText = Loc.T("确定"),
            XamlRoot = xamlRoot,
        }.ShowAsync();
    }

    /// <summary>查看版本 JSON：只读展示（格式化优先，损坏显示原文）+ 查看所在目录。</summary>
    public static async Task ShowJsonAsync(XamlRoot xamlRoot, string dotMc, string versionId)
    {
        var jsonPath = Path.Combine(dotMc, "versions", versionId, versionId + ".json");
        string text;
        try
        {
            text = File.ReadAllText(jsonPath);
            if (JsonNode.Parse(text) is { } node)
                text = node.ToJsonString(new JsonSerializerOptions
                {
                    WriteIndented = true,
                    Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
                });
        }
        catch (Exception ex)
        {
            text = Loc.T("无法读取 {0}.json：{1}", versionId, ex.Message);
        }

        var view = new TextBox
        {
            Text = text,
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            Height = 420,
        };
        ScrollViewer.SetVerticalScrollBarVisibility(view, ScrollBarVisibility.Auto);
        ScrollViewer.SetHorizontalScrollBarVisibility(view, ScrollBarVisibility.Auto);
        // 安卓无桌面资源管理器：按钮改为展示版本目录路径（与版本页「打开文件夹」同口径）
        var openBtn = new GlowButton
        {
            Content = Loc.T("查看所在目录"),
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        openBtn.Click += (_, _) =>
        {
            var dir = Path.GetDirectoryName(jsonPath);
            if (dir is null || !Directory.Exists(dir)) return;
            _ = UiDialog.AlertAsync(Loc.T("版本目录"), dir);
        };
        var dlg = new ThemedDialog
        {
            Title = $"{versionId}.json",
            Content = new StackPanel { Spacing = 8, Children = { view, openBtn } },
            CloseButtonText = Loc.T("关闭"),
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = xamlRoot,
        };
        await dlg.ShowAsync();
    }
}
