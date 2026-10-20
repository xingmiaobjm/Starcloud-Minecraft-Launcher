// 崩溃诊断弹窗：结构化结论 + 证据 + 建议 + 一键修复 / 重新启动 / 问 Nova
// 设计：docs/designs/2026-09-11-launch-reliability-design.md §7.4
//
// 注意：同一时刻只允许一个 ContentDialog，故修复动作（重装进度、内存写入说明）
// 一律在弹窗内以行内状态呈现，不再嵌套二级对话框。
// 安卓适配：桌面 OpenDir 用 Process.Start 拉起资源管理器，安卓无对应语义，
// 改为 UiDialog 展示目录路径（用户用文件管理器自行前往）。

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;

namespace Starcloud.Launcher.Phone.Views.Dialogs;

/// <summary>游戏异常退出诊断弹窗</summary>
internal static class CrashDialog
{
    private enum FixOutcome { Noop, Done, CloseToSettings }

    /// <summary>展示诊断结果；用户选择「重新启动」时执行 restartAsync，「让 Nova 看看」执行 askNovaAsync</summary>
    public static async Task ShowAsync(XamlRoot root, CrashDiagnosis diagnosis, string versionId,
        int exitCode, Func<Task> restartAsync, Func<Task> askNovaAsync)
    {
        // 手机版：去掉 MinWidth=440 固定下限（竖屏可用宽约 300-360 DIP 放不下），宽度随对话框自适应，上限 520 不变
        var content = new StackPanel { Spacing = 10, MaxWidth = 520 };

        content.Children.Add(new TextBlock
        {
            Text = Loc.TCore(diagnosis.Title),
            FontSize = 16,
            FontWeight = FontWeights.Medium,
            TextWrapping = TextWrapping.Wrap,
        });
        content.Children.Add(new TextBlock
        {
            Text = Loc.TCore(diagnosis.Summary),
            FontSize = 13,
            TextWrapping = TextWrapping.Wrap,
        });

        if (diagnosis.Suggestions.Count > 0)
        {
            var tips = new StackPanel { Spacing = 4 };
            foreach (var tip in diagnosis.Suggestions)
            {
                tips.Children.Add(new TextBlock
                {
                    Text = "• " + Loc.TCore(tip),
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                });
            }
            content.Children.Add(tips);
        }

        // 证据折叠区（等宽小字 ≤3 行 + 来源）
        if (!string.IsNullOrWhiteSpace(diagnosis.Evidence))
        {
            var evidence = new TextBox
            {
                Text = diagnosis.Evidence,
                IsReadOnly = true,
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new FontFamily("Consolas"),
                FontSize = 11,
                MaxHeight = 140,
                Visibility = Visibility.Collapsed,
            };
            var toggle = new HyperlinkButton { Content = Loc.T("查看证据"), FontSize = 12 };
            toggle.Click += (_, _) =>
            {
                var show = evidence.Visibility == Visibility.Collapsed;
                evidence.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
                toggle.Content = show ? Loc.T("收起证据") : Loc.T("查看证据");
            };
            content.Children.Add(toggle);
            content.Children.Add(evidence);
            if (!string.IsNullOrWhiteSpace(diagnosis.Source))
            {
                content.Children.Add(new TextBlock
                {
                    Text = Loc.T("证据来源：") + Loc.TCore(diagnosis.Source),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
                });
            }
        }

        // 修复说明（点击即确认，避免嵌套对话框）
        if (diagnosis.Fix != CrashFixKind.None && diagnosis.FixLabel is not null)
            content.Children.Add(new TextBlock
            {
                Text = FixExplain(diagnosis, versionId),
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
            });

        var status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed,
            Foreground = ThemeBrush("TextFillColorSecondaryBrush"),
        };
        var bar = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed };
        content.Children.Add(status);
        content.Children.Add(bar);

        var dlg = new ThemedDialog
        {
            Title = Loc.T("游戏异常退出（退出码 {0}）", exitCode),
            Content = new ScrollViewer { Content = content, MaxHeight = 460 },
            SecondaryButtonText = Loc.T("让 Nova 看看"),
            CloseButtonText = Loc.T("返回主页"),
            XamlRoot = root,
        };

        var wantsFix = diagnosis.Fix != CrashFixKind.None && diagnosis.FixLabel is not null;
        var restartMode = !wantsFix;
        var fixing = false;
        var wantRestart = false;
        var wantSettings = false;
        dlg.PrimaryButtonText = wantsFix ? Loc.TCore(diagnosis.FixLabel) : Loc.T("重新启动");

        dlg.PrimaryButtonClick += async (_, args) =>
        {
            if (restartMode) { wantRestart = true; return; }   // 放行关闭 → 弹窗后重启
            if (fixing) { args.Cancel = true; return; }
            fixing = true;
            var deferral = args.GetDeferral();
            try
            {
                switch (await PerformFixAsync(diagnosis, versionId, status, bar))
                {
                    case FixOutcome.Done:
                        args.Cancel = true;
                        dlg.PrimaryButtonText = Loc.T("重新启动");
                        restartMode = true;
                        break;
                    case FixOutcome.CloseToSettings:
                        wantSettings = true;
                        restartMode = true;
                        args.Cancel = false;   // 放行关闭 → 跳设置页
                        break;
                    default:
                        args.Cancel = true;    // 未修复 → 留在弹窗
                        break;
                }
            }
            catch (Exception ex)
            {
                args.Cancel = true;
                ShowStatus(status, Loc.T("修复失败：") + ex.Message);
            }
            finally
            {
                fixing = false;
                deferral.Complete();
            }
        };

        var result = await dlg.ShowAsync();

        if (wantSettings)
        {
            // 桌面 (App.MainWindow as MainWindow)?.NavigateTo("settings")；Uno 壳走 ShellRootView
            (App.MainWindow?.Content as ShellRootView)?.NavigateTo("settings");
            return;
        }
        if (result == ContentDialogResult.Primary && wantRestart)
        {
            await restartAsync();
            return;
        }
        if (result == ContentDialogResult.Secondary)
            await askNovaAsync();
    }

    /// <summary>执行一键修复动作；返回后续处理方式</summary>
    private static async Task<FixOutcome> PerformFixAsync(CrashDiagnosis diagnosis, string versionId,
        TextBlock status, ProgressBar bar)
    {
        var s = AppServices.Instance;
        var dotMc = s.Settings.DotMinecraft;
        var store = new InstanceSettingsStore(dotMc);
        var gameRoot = InstanceLayout.Resolve(dotMc, versionId, store.Get(versionId), s.Settings).GameRoot;

        switch (diagnosis.Fix)
        {
            case CrashFixKind.SetMemory:
            {
                var mb = diagnosis.SuggestMaxMemoryMb ?? 0;
                if (mb <= 0) return FixOutcome.Noop;
                var profile = store.Get(versionId);
                profile.MemoryMode = "manual";
                profile.MemoryMinMb = 512;
                profile.MemoryMaxMb = mb;
                store.Set(versionId, profile);
                store.Save();
                LauncherLog.Write("crash", $"崩溃修复：{versionId} 内存档位改为 manual 512-{mb} MB");
                ShowStatus(status, Loc.T("已把最大内存设为 {0} MB，点击「重新启动」生效。", mb));
                return FixOutcome.Done;
            }
            case CrashFixKind.ReinstallVersion:
            {
                ShowStatus(status, Loc.T("正在重新安装 {0}…", versionId));
                bar.Visibility = Visibility.Visible;
                try
                {
                    var message = await s.Diagnostics.RepairAsync(versionId, s.Settings);
                    bar.Visibility = Visibility.Collapsed;
                    ShowStatus(status, string.IsNullOrWhiteSpace(message) ? Loc.T("已重新安装，可重新启动。") : message);
                    LauncherLog.Write("crash", $"崩溃修复：重新安装 {versionId} → {message}");
                    return FixOutcome.Done;
                }
                catch (Exception ex)
                {
                    bar.Visibility = Visibility.Collapsed;
                    ShowStatus(status, Loc.T("重新安装失败：") + ex.Message);
                    LauncherLog.Error("crash", $"重新安装 {versionId} 失败", ex);
                    return FixOutcome.Noop;
                }
            }
            case CrashFixKind.OpenCrashReport:
            {
                var dir = Directory.Exists(Path.Combine(gameRoot, "crash-reports"))
                    ? Path.Combine(gameRoot, "crash-reports")
                    : Path.Combine(gameRoot, "logs");
                ShowDir(dir);
                ShowStatus(status, Loc.T("崩溃报告目录：{0}", dir));
                return FixOutcome.Done;
            }
            case CrashFixKind.OpenModsFolder:
            {
                var dir = Path.Combine(gameRoot, "mods");
                ShowDir(dir);
                ShowStatus(status, Loc.T("模组目录：{0}（启动器不会自动删除模组）", dir));
                return FixOutcome.Done;
            }
            case CrashFixKind.ReinstallJavaHint:
                return FixOutcome.CloseToSettings;
            default:
                return FixOutcome.Noop;
        }
    }

    /// <summary>修复动作的一句话说明（点击按钮即视为确认）。
    /// 桌面的「打开目录」在安卓语义下是「展示目录路径」，文案同步改写。</summary>
    private static string FixExplain(CrashDiagnosis diagnosis, string versionId) => diagnosis.Fix switch
    {
        CrashFixKind.SetMemory =>
            Loc.T("点击左侧按钮会把版本 {0} 的内存档位改为「自定义」并写入推荐值（最小 512 MB）。", versionId),
        CrashFixKind.ReinstallVersion =>
            Loc.T("点击左侧按钮会重新下载并覆盖 {0} 的缺失/损坏文件（不会动 mods 与存档）。", versionId),
        CrashFixKind.OpenCrashReport => Loc.T("点击左侧按钮会给出崩溃报告所在目录的路径。"),
        CrashFixKind.OpenModsFolder => Loc.T("点击左侧按钮会给出该实例 mods 目录的路径。"),
        CrashFixKind.ReinstallJavaHint => Loc.T("点击左侧按钮会带你到设置页选择合适的 Java。"),
        _ => "",
    };

    private static void ShowStatus(TextBlock status, string text)
    {
        status.Text = text;
        status.Visibility = Visibility.Visible;
    }

    /// <summary>桌面直接拉起资源管理器打开目录；安卓无对应语义，确保目录存在后弹窗展示路径。</summary>
    private static void ShowDir(string path)
    {
        try { Directory.CreateDirectory(path); }
        catch { /* 目录创建失败不影响弹窗 */ }
        _ = UiDialog.AlertAsync(Loc.T("目录位置"), path);
    }

    private static Brush? ThemeBrush(string key) =>
        Application.Current.Resources.TryGetValue(key, out var v) ? v as Brush : null;
}
