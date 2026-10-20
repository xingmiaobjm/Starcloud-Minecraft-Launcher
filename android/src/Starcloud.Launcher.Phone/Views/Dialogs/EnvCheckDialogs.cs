// 启动前环境体检弹窗：列出 Block/Warn 项，可逐项一键修复，或仍然启动 / 取消
// 设计：docs/designs/2026-09-11-launch-reliability-design.md §6.3

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Views.Controls;

namespace Starcloud.Launcher.Phone.Views.Dialogs;

/// <summary>环境体检结果弹窗</summary>
internal static class EnvCheckDialogs
{
    /// <summary>展示体检结果；返回 true = 用户选择「仍然启动」</summary>
    public static async Task<bool> ShowAsync(XamlRoot root, EnvironmentReport report,
        Func<EnvIssue, Task<bool>> fixAsync)
    {
        // 手机版：去掉 MinWidth=420 固定下限（竖屏可用宽约 300-360 DIP 放不下），宽度随对话框自适应，上限 520 不变
        var panel = new StackPanel { Spacing = 12, MaxWidth = 520 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("检测到以下问题，可先修复再继续启动。"),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        });

        foreach (var issue in report.Issues)
            panel.Children.Add(CreateRow(issue, fixAsync));

        var dlg = new ThemedDialog
        {
            Title = Loc.T("运行环境检查"),
            Content = new ScrollViewer { Content = panel, MaxHeight = 420 },
            PrimaryButtonText = Loc.T("仍然启动"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>一行问题：等级色点 + 标题/细节 + 可修复时的「修复」按钮</summary>
    private static Grid CreateRow(EnvIssue issue, Func<EnvIssue, Task<bool>> fixAsync)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var dot = new Ellipse
        {
            Width = 8,
            Height = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Fill = ThemeBrush(issue.Level == EnvLevel.Block
                ? "SystemFillColorCriticalBrush"
                : "SystemFillColorCautionBrush"),
        };
        row.Children.Add(dot);

        var text = new StackPanel { Spacing = 2 };
        text.Children.Add(new TextBlock
        {
            Text = Loc.TCore(issue.Title),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });
        text.Children.Add(new TextBlock
        {
            Text = Loc.TCore(issue.Detail),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (!issue.AutoFixable) return row;

        var fixBtn = new GlowButton
        {
            Content = Loc.TCore(issue.FixHint) is { Length: > 0 } hint ? hint : Loc.T("修复"),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(fixBtn, 2);
        row.Children.Add(fixBtn);

        fixBtn.Click += async (_, _) =>
        {
            fixBtn.IsEnabled = false;
            fixBtn.Content = Loc.T("修复中…");
            var ok = false;
            try
            {
                ok = await fixAsync(issue);
            }
            catch
            {
                // 修复失败的原文由调用方以提示条展示，这里只恢复按钮
            }
            if (ok)
            {
                fixBtn.Content = Loc.T("已修复");
                dot.Fill = ThemeBrush("SystemFillColorSuccessBrush");
                text.Opacity = 0.6;
            }
            else
            {
                fixBtn.Content = Loc.TCore(issue.FixHint) is { Length: > 0 } hint ? hint : Loc.T("修复");
                fixBtn.IsEnabled = true;
            }
        };
        return row;
    }

    private static Brush? ThemeBrush(string key)
    {
        // 直接索引可能解析到非当前主题的字典；先按主题键防御查找（对齐页面侧做法）
        return Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;
    }
}
