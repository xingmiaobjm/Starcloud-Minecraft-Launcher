// 高级导出对话框（3 种格式 + 全部高级设置）：由 ModPackDialogs.ExportAsync 调用。
//
// 这里只负责「填表」，不碰文件系统之外的执行：用户确认后返回一份 ExportDialogResult，
// 保存路径与真正的打包由调用方完成（这样进度弹窗与结果提示能和工作流保持一致）。

using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;
using Starcloud.Launcher.Phone.Views.Controls;
using Starcloud.Core.Services;
using System.IO;
using System.IO.Compression;

namespace Starcloud.Launcher.Phone.Views.Dialogs;

/// <summary>导出对话框的填写结果（不含保存路径，路径在对话框关闭后由保存选择器给出）</summary>
internal sealed record ExportDialogResult(
    ModPackExportFormat Format,
    string Name,
    string VersionId,
    string Author,
    string Description,
    string GameVersion,
    IReadOnlyDictionary<string, string> Dependencies,
    IReadOnlyList<string> Dirs,
    IReadOnlyList<string> ExcludeRules,
    CompressionLevel Compression,
    bool IncludeGameSettings,
    bool UnresolvedAsOverrides);

internal static class ModPackExportDialog
{
    /// <summary>加载器下拉项：索引 0 = 不使用加载器</summary>
    private static readonly string[] LoaderKeys =
        ["", "fabric-loader", "quilt-loader", "forge", "neoforge"];

    private static readonly string[] LoaderLabels =
        ["不使用加载器", "Fabric", "Quilt", "Forge", "NeoForge"];

    private sealed class DirRow
    {
        public string Name = "";
        public bool IsStandard;
        public bool IsChecked;
    }

    /// <summary>弹出高级导出设置；用户取消时返回 null</summary>
    public static async Task<ExportDialogResult?> ShowAsync(
        XamlRoot root, AppServices s, string versionId, string gameRoot,
        string gameVersion, IReadOnlyDictionary<string, string> deps)
    {
        // ── 目录候选 ──
        var candidates = s.ModPackExport.DiscoverDirs(gameRoot);
        var meta = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<DirRow>();
        foreach (var c in candidates)
        {
            meta[c.Name] = (c.FileCount, c.SizeBytes);
            dirs.Add(new DirRow { Name = c.Name, IsStandard = c.IsStandard, IsChecked = c.IsStandard });
        }

        var rowsPanel = new StackPanel { Spacing = 2 };
        var boxes = new List<CheckBox>();
        var hint = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var summary = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };

        void RefreshSummary()
        {
            long bytes = 0;
            var files = 0;
            var selected = 0;
            foreach (var d in dirs)
            {
                if (!d.IsChecked) continue;
                selected++;
                if (meta.TryGetValue(d.Name, out var m)) { bytes += m.Bytes; files += m.Count; }
            }
            summary.Text = selected == 0
                ? Loc.T("未选择任何内容目录。")
                : Loc.T("预计导出体积：约 {0}（{1} 个文件）", ModPackDialogs.FormatSize(bytes), files);
        }

        CheckBox MakeBox(DirRow d)
        {
            var box = new CheckBox
            {
                Content = d.IsStandard ? $"{d.Name}    {DirDetail(d)}" : Loc.T("{0}    {1}（自定义）", d.Name, DirDetail(d)),
                IsChecked = d.IsChecked,
            };
            box.Checked += (_, _) => { d.IsChecked = true; RefreshSummary(); };
            box.Unchecked += (_, _) => { d.IsChecked = false; RefreshSummary(); };
            boxes.Add(box);
            return box;
        }

        string DirDetail(DirRow d) =>
            meta.TryGetValue(d.Name, out var m)
                ? Loc.T("{0} 项 · {1}", m.Count, ModPackDialogs.FormatSize(m.Bytes))
                : "";

        foreach (var d in dirs) rowsPanel.Children.Add(MakeBox(d));

        void ApplyPreset(Func<string, bool> want)
        {
            for (var i = 0; i < dirs.Count; i++)
            {
                var on = want(dirs[i].Name);
                dirs[i].IsChecked = on;
                boxes[i].IsChecked = on;
            }
            RefreshSummary();
        }

        // ── 格式 ──
        var formatBox = new RadioButtons
        {
            SelectedIndex = 0,
            Items = { "Modrinth 整合包（.mrpack）", "CurseForge 整合包（.zip）", "MultiMC / Prism 实例包（.zip）" },
        };
        var formatHint = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        void SyncFormatHint()
        {
            formatHint.Text = (ModPackExportFormat)Math.Max(formatBox.SelectedIndex, 0) switch
            {
                ModPackExportFormat.CurseForge => Loc.T("按文件指纹反查 CurseForge 来源；反查不到的文件按下方策略处理。"),
                ModPackExportFormat.MultiMc => Loc.T("自包含实例包：全部内容原样打进 .minecraft/，不联网反查，导入端解压即用。"),
                _ => Loc.T("按 SHA1 反查 Modrinth 来源；反查不到的文件按下方策略处理。"),
            };
        }
        formatBox.SelectionChanged += (_, _) => SyncFormatHint();
        SyncFormatHint();

        // ── 包元数据 ──
        var nameBox = new TextBox { Text = versionId, PlaceholderText = Loc.T("整合包名称") };
        var versionIdBox = new TextBox { Text = "1.0.0", PlaceholderText = Loc.T("包版本号，如 1.0.0") };
        var authorBox = new TextBox { PlaceholderText = Loc.T("作者（可留空）") };
        var descBox = new TextBox
        {
            PlaceholderText = Loc.T("简介（可留空）"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 64,
        };

        // ── 加载器与依赖（可手改）──
        var mcBox = new TextBox { Text = gameVersion, PlaceholderText = Loc.T("Minecraft 版本，如 1.20.1") };
        var loaderBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var label in LoaderLabels) loaderBox.Items.Add(label);
        var loaderVersionBox = new TextBox { PlaceholderText = Loc.T("加载器版本，如 0.15.11") };
        var loaderIndex = Array.FindIndex(LoaderKeys, k =>
            k.Length > 0 && deps.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v));
        if (loaderIndex < 0) loaderIndex = 0;
        loaderBox.SelectedIndex = loaderIndex;
        if (loaderIndex > 0) loaderVersionBox.Text = deps[LoaderKeys[loaderIndex]];
        loaderVersionBox.IsEnabled = loaderIndex > 0;
        loaderBox.SelectionChanged += (_, _) => loaderVersionBox.IsEnabled = loaderBox.SelectedIndex > 0;

        // ── 打包策略 ──
        var settingsBox = new CheckBox
        {
            Content = Loc.T("包含实例设置（options.txt / servers.dat 等）"),
            IsChecked = true,
        };
        var overridesBox = new CheckBox
        {
            Content = Loc.T("无法识别来源的文件打包进包内（关闭则直接丢弃）"),
            IsChecked = true,
        };
        var compressionBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        compressionBox.Items.Add(Loc.T("最优压缩（体积最小）"));
        compressionBox.Items.Add(Loc.T("快速压缩（速度优先）"));
        compressionBox.Items.Add(Loc.T("不压缩（最快）"));
        compressionBox.SelectedIndex = 0;
        var excludeBox = new TextBox
        {
            PlaceholderText = Loc.T("每行一条，如 *.log / config/secret* / saves"),
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 64,
        };

        // 手机版：输入框原固定 Width=240 与「添加」按钮横排合计约 308 DIP，最窄竖屏放不下；
        // 改为 Star/Auto 网格，输入框随行自适应（上限 240），按钮固定在右侧
        var customBox = new TextBox { PlaceholderText = Loc.T("目录名，如 kubejs / schematics"), MaxWidth = 240, HorizontalAlignment = HorizontalAlignment.Stretch };
        var addBtn = new GlowButton { Content = Loc.T("添加") };
        addBtn.Click += (_, _) =>
        {
            hint.Text = "";
            var name = customBox.Text.Trim().Replace('\\', '/').Trim('/');
            if (name.Length == 0) { hint.Text = Loc.T("请输入目录名。"); return; }
            if (name.Split('/').Any(seg => seg is "" or "." or "..")) { hint.Text = Loc.T("目录名不合法。"); return; }
            if (dirs.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                hint.Text = Loc.T("该目录已在列表中。");
                return;
            }
            var abs = Path.Combine(gameRoot, name.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(abs)) { hint.Text = Loc.T("目录不存在：{0}", name); return; }
            meta[name] = ModPackDialogs.Measure(abs);
            var d = new DirRow { Name = name, IsChecked = true };
            dirs.Add(d);
            rowsPanel.Children.Add(MakeBox(d));
            customBox.Text = "";
            RefreshSummary();
        };

        // ── 组装 ──
        // 手机版：去掉 MinWidth=500 固定下限（竖屏可用宽约 300-360 DIP 放不下），宽度随对话框自适应，上限 600 不变
        var panel = new StackPanel { Spacing = 12, MaxWidth = 600 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("源版本：{0}        MC {1}", versionId, gameVersion) +
                   (DescribeLoader(deps) is { Length: > 0 } l ? $" · {l}" : ""),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });

        panel.Children.Add(Section(Loc.T("导出格式"), formatBox, formatHint));

        var metaPanel = new StackPanel { Spacing = 6 };
        metaPanel.Children.Add(Labeled(Loc.T("名称"), nameBox));
        metaPanel.Children.Add(Labeled(Loc.T("包版本号"), versionIdBox));
        metaPanel.Children.Add(Labeled(Loc.T("作者"), authorBox));
        metaPanel.Children.Add(Labeled(Loc.T("简介"), descBox));
        panel.Children.Add(Section(Loc.T("包元数据"), metaPanel));

        var loaderPanel = new StackPanel { Spacing = 6 };
        loaderPanel.Children.Add(Labeled(Loc.T("Minecraft 版本"), mcBox));
        loaderPanel.Children.Add(Labeled(Loc.T("加载器"), loaderBox));
        loaderPanel.Children.Add(Labeled(Loc.T("加载器版本"), loaderVersionBox));
        panel.Children.Add(Section(Loc.T("加载器与依赖（可手改）"), loaderPanel));

        var contentPanel = new StackPanel { Spacing = 8 };
        var presets = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        presets.Children.Add(ModPackDialogs.MakePresetButton(Loc.T("仅 Mod 与配置"),
            () => ApplyPreset(n => n.Equals("mods", StringComparison.OrdinalIgnoreCase) ||
                                   n.Equals("config", StringComparison.OrdinalIgnoreCase))));
        presets.Children.Add(ModPackDialogs.MakePresetButton(Loc.T("全部内容"),
            () => ApplyPreset(n => !n.Equals("saves", StringComparison.OrdinalIgnoreCase))));
        presets.Children.Add(ModPackDialogs.MakePresetButton(Loc.T("全部（含存档）"), () => ApplyPreset(_ => true)));
        contentPanel.Children.Add(presets);
        contentPanel.Children.Add(new ScrollViewer { MaxHeight = 200, Content = rowsPanel });
        // 手机版：输入框+按钮横排改为 Star/Auto 网格（输入框自适应、按钮右侧固定），不再撑破竖屏宽度
        var customRow = new Grid { ColumnSpacing = 8 };
        customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        customRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        customRow.Children.Add(customBox);
        Grid.SetColumn(addBtn, 1);
        customRow.Children.Add(addBtn);
        contentPanel.Children.Add(customRow);
        contentPanel.Children.Add(hint);
        contentPanel.Children.Add(summary);
        panel.Children.Add(Section(Loc.T("打包内容"), contentPanel));
        RefreshSummary();

        var strategyPanel = new StackPanel { Spacing = 6 };
        strategyPanel.Children.Add(settingsBox);
        strategyPanel.Children.Add(overridesBox);
        strategyPanel.Children.Add(Labeled(Loc.T("压缩级别"), compressionBox));
        strategyPanel.Children.Add(Labeled(Loc.T("排除规则"), excludeBox));
        panel.Children.Add(Section(Loc.T("打包策略与排除规则"), strategyPanel));

        var dlgPanel = new ScrollViewer { MaxHeight = 520, Content = panel };
        while (true)
        {
            var dlg = new ThemedDialog
            {
                Title = Loc.T("导出整合包"),
                Content = dlgPanel,
                PrimaryButtonText = Loc.T("下一步"),
                CloseButtonText = Loc.T("取消"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return null;

            var selectedDirs = dirs.Where(d => d.IsChecked).Select(d => d.Name).ToList();
            if (selectedDirs.Count == 0)
            {
                hint.Text = Loc.T("请至少勾选一个内容目录。");
                continue;
            }

            var dependencies = new Dictionary<string, string>();
            var mc = mcBox.Text.Trim();
            if (mc.Length > 0) dependencies["minecraft"] = mc;
            var loaderKey = LoaderKeys[Math.Max(loaderBox.SelectedIndex, 0)];
            var loaderVersion = loaderVersionBox.Text.Trim();
            if (loaderKey.Length > 0 && loaderVersion.Length > 0) dependencies[loaderKey] = loaderVersion;

            return new ExportDialogResult(
                (ModPackExportFormat)Math.Max(formatBox.SelectedIndex, 0),
                nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : versionId,
                versionIdBox.Text.Trim().Length > 0 ? versionIdBox.Text.Trim() : "1.0.0",
                authorBox.Text.Trim(),
                descBox.Text.Trim(),
                mc,
                dependencies,
                selectedDirs,
                excludeBox.Text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
                compressionBox.SelectedIndex switch
                {
                    1 => CompressionLevel.Fastest,
                    2 => CompressionLevel.NoCompression,
                    _ => CompressionLevel.Optimal,
                },
                settingsBox.IsChecked == true,
                overridesBox.IsChecked == true);
        }
    }

    /// <summary>带标题的小节</summary>
    private static UIElement Section(string title, params UIElement[] children)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock
        {
            Text = title,
            FontWeight = FontWeights.SemiBold,
            FontSize = 13,
        });
        foreach (var child in children) panel.Children.Add(child);
        return panel;
    }

    private static UIElement Labeled(string label, UIElement control)
    {
        var panel = new StackPanel { Spacing = 4 };
        panel.Children.Add(new TextBlock { Text = label, FontSize = 12, Opacity = 0.8 });
        panel.Children.Add(control);
        return panel;
    }

    private static string DescribeLoader(IReadOnlyDictionary<string, string> deps)
    {
        var parts = new List<string>();
        if (deps.TryGetValue("fabric-loader", out var f)) parts.Add($"Fabric {f}");
        if (deps.TryGetValue("quilt-loader", out var q)) parts.Add($"Quilt {q}");
        if (deps.TryGetValue("forge", out var fo)) parts.Add($"Forge {fo}");
        if (deps.TryGetValue("neoforge", out var n)) parts.Add($"NeoForge {n}");
        return string.Join(" · ", parts);
    }
}
