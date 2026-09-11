// 整合包与版本资产对话框（导出 / 导入 / 备份回档）：由 VersionsPage 入口调用
// 计划：docs/superpowers/plans/2026-09-11-modpack-version-assets-plan.md T7/T8/T9

using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Services;
using Starcloud.Core.Services;
using Windows.Storage.Pickers;

namespace Starcloud.App.Views;

/// <summary>备份对话框里「列表内按钮 → 关闭后处理」的待办动作</summary>
internal sealed class BackupPanelState
{
    public BackupEntry? Restore;
    public BackupEntry? Delete;
    public bool Rebuild;
    public bool OpenDir;
}

public static class ModPackDialogs
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    // ───────────────────────── T7 导出整合包 ─────────────────────────

    /// <summary>导出对话框：勾选内容目录 → 选保存路径 → 打包 .mrpack</summary>
    public static async Task ExportAsync(XamlRoot root, AppServices s, LocalVersionRow row)
    {
        var dotMc = s.Settings.DotMinecraft;
        var profile = new InstanceSettingsStore(dotMc).Get(row.Id);
        var isolated = InstanceLayout.IsIsolated(profile, s.Settings);
        var gameRoot = InstanceLayout.GameRoot(dotMc, row.Id, isolated);

        var (gameVersion, deps) = ResolveDependencies(dotMc, row.Id);
        var candidates = s.ModPackExport.DiscoverDirs(gameRoot);

        var meta = new Dictionary<string, (int Count, long Bytes)>(StringComparer.OrdinalIgnoreCase);
        var dirs = new List<ExportDirRow>();
        var boxes = new List<CheckBox>();
        foreach (var c in candidates)
        {
            meta[c.Name] = (c.FileCount, c.SizeBytes);
            dirs.Add(new ExportDirRow
            {
                Name = c.Name,
                Detail = $"{c.FileCount} 项 · {FormatSize(c.SizeBytes)}",
                IsStandard = c.IsStandard,
                IsChecked = c.IsStandard,
            });
        }

        var rowsPanel = new StackPanel { Spacing = 2 };
        var summary = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        var hint = new TextBlock { FontSize = 12, Opacity = 0.8, Foreground = null, TextWrapping = TextWrapping.Wrap };

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
                ? "未选择任何内容目录。"
                : $"预计导出体积：约 {FormatSize(bytes)}（{files} 个文件）";
        }

        CheckBox MakeBox(ExportDirRow d)
        {
            var box = new CheckBox
            {
                Content = d.IsStandard ? $"{d.Name}    {d.Detail}" : $"{d.Name}    {d.Detail}（自定义）",
                IsChecked = d.IsChecked,
                Tag = d,
            };
            box.Checked += (_, _) => { d.IsChecked = true; RefreshSummary(); };
            box.Unchecked += (_, _) => { d.IsChecked = false; RefreshSummary(); };
            boxes.Add(box);
            return box;
        }

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

        var customBox = new TextBox { PlaceholderText = "目录名，如 kubejs / schematics", Width = 240 };
        var addBtn = new Button { Content = "添加" };
        addBtn.Click += (_, _) =>
        {
            hint.Text = "";
            var name = customBox.Text.Trim().Replace('\\', '/').Trim('/');
            if (name.Length == 0) { hint.Text = "请输入目录名。"; return; }
            if (name.Split('/').Any(seg => seg is "" or "." or "..")) { hint.Text = "目录名不合法。"; return; }
            if (dirs.Any(d => d.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            {
                hint.Text = "该目录已在列表中。";
                return;
            }
            var abs = Path.Combine(gameRoot, name.Replace('/', Path.DirectorySeparatorChar));
            if (!Directory.Exists(abs)) { hint.Text = $"目录不存在：{name}"; return; }
            var m = Measure(abs);
            meta[name] = m;
            var d = new ExportDirRow
            {
                Name = name,
                Detail = $"{m.Count} 项 · {FormatSize(m.Bytes)}",
                IsChecked = true,
            };
            dirs.Add(d);
            rowsPanel.Children.Add(MakeBox(d));
            customBox.Text = "";
            RefreshSummary();
        };

        var loader = DescribeLoader(deps);
        var panel = new StackPanel { Spacing = 10, MinWidth = 460, MaxWidth = 560 };
        panel.Children.Add(new TextBlock
        {
            Text = $"源版本：{row.Id}        MC {gameVersion}" + (loader.Length > 0 ? $" · {loader}" : ""),
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        });
        var head = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        head.Children.Add(new TextBlock
        {
            Text = "打包内容",
            FontSize = 12,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
        });
        head.Children.Add(MakePresetButton("仅 Mod 与配置",
            () => ApplyPreset(n => n.Equals("mods", StringComparison.OrdinalIgnoreCase) ||
                                   n.Equals("config", StringComparison.OrdinalIgnoreCase))));
        head.Children.Add(MakePresetButton("全部内容",
            () => ApplyPreset(n => !n.Equals("saves", StringComparison.OrdinalIgnoreCase))));
        head.Children.Add(MakePresetButton("全部（含存档）", () => ApplyPreset(_ => true)));
        panel.Children.Add(head);
        panel.Children.Add(new ScrollViewer { MaxHeight = 220, Content = rowsPanel });
        var customRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        customRow.Children.Add(customBox);
        customRow.Children.Add(addBtn);
        panel.Children.Add(customRow);
        panel.Children.Add(hint);
        panel.Children.Add(summary);
        RefreshSummary();

        var dlg = new ContentDialog
        {
            Title = "导出整合包",
            Content = panel,
            PrimaryButtonText = "导出…",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var selectedDirs = dirs.Where(d => d.IsChecked).Select(d => d.Name).ToList();
        if (selectedDirs.Count == 0)
        {
            await InfoAsync(root, "导出整合包", "请至少勾选一个内容目录。");
            return;
        }

        var picker = new FileSavePicker
        {
            SuggestedStartLocation = PickerLocationId.Desktop,
            SuggestedFileName = $"{InstanceLayout.SafeFileName(row.Id)}-{DateTime.Now:yyyyMMdd}",
        };
        picker.FileTypeChoices.Add("Modrinth 整合包", new List<string> { ".mrpack" });
        WinRT.Interop.InitializeWithWindow.Initialize(picker,
            WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow));
        var file = await picker.PickSaveFileAsync();
        if (file is null) return;

        var request = new ExportRequest(gameRoot, file.Path, row.Id, gameVersion, deps, selectedDirs);
        ExportResult? result;
        try
        {
            result = await RunWithProgressAsync(root, "导出整合包", "后台导出",
                p => s.ModPackExport.ExportAsync(request, p), indeterminate: true);
        }
        catch (Exception ex)
        {
            await ErrorAsync(root, "导出失败", ex);
            return;
        }
        if (result is null) return;

        var message = $"可下载 {result.DownloadableFiles} 个 · 打包 {result.OverriddenFiles} 个 · " +
                      $"共 {FormatSize(result.TotalBytes)}";
        if (!string.IsNullOrEmpty(result.LoaderWarning)) message += $"\n\n提示：{result.LoaderWarning}";
        message += $"\n\n已保存到：{file.Path}";
        await InfoAsync(root, "导出完成", message);
    }

    /// <summary>推断 MC 版本与加载器依赖。
    /// 注意：不能用 InstallerService.LoadVersionAsync 的返回值，它内部会把 inheritsFrom 清空。</summary>
    private static (string GameVersion, Dictionary<string, string> Deps) ResolveDependencies(
        string dotMc, string versionId)
    {
        var found = new Dictionary<string, string>();
        var game = versionId;
        var jsonPath = Path.Combine(dotMc, "versions", versionId, $"{versionId}.json");
        string? raw = null;
        try { if (File.Exists(jsonPath)) raw = File.ReadAllText(jsonPath); } catch { /* 读不到走目录名兜底 */ }

        if (raw is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                var rootEl = doc.RootElement;
                if (rootEl.TryGetProperty("inheritsFrom", out var inh) &&
                    inh.ValueKind == JsonValueKind.String && inh.GetString() is { Length: > 0 } parent)
                    game = parent;
                else if (rootEl.TryGetProperty("id", out var idEl) &&
                         idEl.ValueKind == JsonValueKind.String && idEl.GetString() is { Length: > 0 } self)
                    game = self;

                if (rootEl.TryGetProperty("libraries", out var libs) && libs.ValueKind == JsonValueKind.Array)
                {
                    foreach (var lib in libs.EnumerateArray())
                    {
                        if (!lib.TryGetProperty("name", out var nameEl)) continue;
                        var name = nameEl.GetString();
                        if (string.IsNullOrEmpty(name)) continue;
                        var parts = name.Split(':');
                        if (parts.Length < 3) continue;
                        var (group, artifact, ver) = (parts[0], parts[1], parts[2]);
                        if (group == "net.fabricmc" && artifact == "fabric-loader") found["fabric-loader"] = ver;
                        else if (group == "org.quiltmc" && artifact == "quilt-loader") found["quilt-loader"] = ver;
                        else if (group == "net.minecraftforge" && artifact == "forge")
                            found["forge"] = TrimMcPrefix(ver, game);
                        else if (group == "net.neoforged" && artifact == "neoforge") found["neoforge"] = ver;
                    }
                }
            }
            catch { /* JSON 损坏 → 退回目录名推断 */ }
        }

        // 目录名兜底：fabric-loader-<loader>-<mc> / <mc>-forge-<build> / neoforge-<build>
        if (found.Count == 0)
        {
            var m = Regex.Match(versionId, @"^(fabric-loader|quilt-loader)-(.+?)-(\d[\w.]*)$",
                RegexOptions.IgnoreCase);
            if (m.Success) found[m.Groups[1].Value.ToLowerInvariant()] = m.Groups[2].Value;
            else if ((m = Regex.Match(versionId, @"-forge-(.+)$", RegexOptions.IgnoreCase)).Success)
                found["forge"] = m.Groups[1].Value;
            else if ((m = Regex.Match(versionId, @"neoforge-(.+)$", RegexOptions.IgnoreCase)).Success)
                found["neoforge"] = m.Groups[1].Value;
        }

        var deps = new Dictionary<string, string> { ["minecraft"] = game };
        foreach (var kv in found) deps[kv.Key] = kv.Value;
        return (game, deps);
    }

    private static string TrimMcPrefix(string version, string mc)
    {
        var prefix = mc + "-";
        return version.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? version[prefix.Length..]
            : version;
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

    // ───────────────────────── T8 导入整合包 ─────────────────────────

    /// <summary>导入对话框：预览包信息 → 选版本名与隔离 → 安装 + 联动装原版/加载器</summary>
    public static async Task ImportAsync(XamlRoot root, AppServices s, string mrpackPath)
    {
        var dotMc = s.Settings.DotMinecraft;

        MrPackPackageInfo info;
        try
        {
            info = s.MrPack.ReadPackageInfo(mrpackPath);
        }
        catch (Exception ex)
        {
            await InfoAsync(root, "无法导入整合包",
                "本启动器支持 Modrinth 格式整合包（.mrpack 或内含 modrinth.index.json 的 zip）。\n\n" + ex.Message);
            return;
        }

        TextBox nameBox = null!;
        CheckBox isoBox = null!;
        TextBlock hint = null!;

        UIElement BuildPanel()
        {
            nameBox = new TextBox
            {
                Text = InstanceLayout.SafeFileName(info.Name),
                PlaceholderText = "新建版本名称",
            };
            isoBox = new CheckBox
            {
                Content = "版本隔离（推荐，整合包内容独立存放）",
                IsChecked = true,
            };
            hint = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
            var note = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
            void SyncNote() => note.Text = isoBox.IsChecked == true
                ? "整合包内容将独立存放在 versions/<新名称>/ 下。"
                : "将写入共享 .minecraft 目录，不会新建版本条目。";
            isoBox.Checked += (_, _) => SyncNote();
            isoBox.Unchecked += (_, _) => SyncNote();
            SyncNote();

            var loader = DescribeLoader(info.Dependencies);
            var panel = new StackPanel { Spacing = 10, MinWidth = 420, MaxWidth = 520 };
            panel.Children.Add(new TextBlock
            {
                Text = "包名：" + info.Name,
                FontWeight = FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"Minecraft {info.GameVersion}" + (loader.Length > 0 ? $" · {loader}" : "") +
                       $" · {info.DownloadFileCount} 个文件 · {FormatSize(info.TotalBytes)}",
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock { Text = "新建版本名称", FontSize = 12, Opacity = 0.8 });
            panel.Children.Add(nameBox);
            panel.Children.Add(isoBox);
            panel.Children.Add(note);
            panel.Children.Add(hint);
            return panel;
        }

        string targetId;
        while (true)
        {
            var dlg = new ContentDialog
            {
                Title = "导入整合包",
                Content = BuildPanel(),
                PrimaryButtonText = "导入",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            var typed = nameBox.Text.Trim();
            if (!InstanceLayout.IsValidVersionId(typed))
            {
                hint.Text = "版本名不能为空，且不能包含 < > : \" / \\ | ? *";
                continue;
            }
            targetId = typed;
            break;
        }

        var isolated = isoBox.IsChecked == true;
        var renamed = false;
        if (isolated && Directory.Exists(Path.Combine(dotMc, "versions", targetId)))
        {
            targetId = NextVersionName(dotMc, targetId);
            renamed = true;
        }
        var installRoot = isolated ? Path.Combine(dotMc, "versions", targetId) : dotMc;

        var progressDlg = new ContentDialog
        {
            Title = "导入整合包",
            Content = CreateProgressPanel(out var statusText, out var bar),
            CloseButtonText = "后台导入",
            XamlRoot = root,
        };
        _ = progressDlg.ShowAsync();
        var dq = DispatcherQueue.GetForCurrentThread();
        var progress = new Progress<BatchProgress>(p => dq.TryEnqueue(() =>
        {
            bar.Value = p.Percentage;
            statusText.Text = p.Stage;
        }));

        string summary;
        try
        {
            if (isolated) Directory.CreateDirectory(installRoot);
            var result = await s.MrPack.InstallAsync(mrpackPath, s.Settings, installRoot, progress: progress);
            summary = await MrPackFlow.EnsureGameAndLoaderAsync(result, s, progress);
            if (isolated)
            {
                // 版本骨架：父版本优先指向联动装好的加载器，否则退回首层原版
                var parent = LoaderVersionId(dotMc, info.GameVersion, info.Dependencies) ?? info.GameVersion;
                var jsonPath = Path.Combine(installRoot, $"{targetId}.json");
                if (!File.Exists(jsonPath))
                {
                    var skeleton = new { id = targetId, inheritsFrom = parent, type = "release" };
                    File.WriteAllText(jsonPath, JsonSerializer.Serialize(skeleton, JsonOpts));
                }
                var store = new InstanceSettingsStore(dotMc);
                store.Set(targetId, new VersionProfile { Isolation = "isolated" });
                store.Save();
            }
        }
        catch (Exception ex)
        {
            try { progressDlg.Hide(); } catch { /* 用户已关闭弹窗时忽略 */ }
            await ErrorAsync(root, "导入失败", ex);
            return;
        }
        try { progressDlg.Hide(); } catch { /* 用户已关闭弹窗时忽略 */ }

        var message = isolated
            ? $"已创建版本「{targetId}」。\n{summary}"
            : $"已写入共享 .minecraft 目录。\n{summary}";
        if (renamed) message += "\n（同名版本已存在，已自动改名）";
        await InfoAsync(root, "导入完成", message);
    }

    /// <summary>名称冲突时递增 " (n)"（baseName 可用时原样返回）</summary>
    private static string NextVersionName(string dotMc, string baseName)
    {
        if (!Directory.Exists(Path.Combine(dotMc, "versions", baseName))) return baseName;
        for (var i = 2; ; i++)
        {
            var candidate = $"{baseName} ({i})";
            if (!Directory.Exists(Path.Combine(dotMc, "versions", candidate))) return candidate;
        }
    }

    /// <summary>联动安装后实际存在的加载器版本目录名（fabric-loader-&lt;ver&gt;-&lt;mc&gt; / &lt;mc&gt;-forge-&lt;build&gt; / neoforge-&lt;build&gt;）</summary>
    private static string? LoaderVersionId(string dotMc, string mc, IReadOnlyDictionary<string, string> deps)
    {
        string? id = null;
        if (deps.TryGetValue("fabric-loader", out var fab) && fab.Length > 0) id = $"fabric-loader-{fab}-{mc}";
        else if (deps.TryGetValue("quilt-loader", out var quilt) && quilt.Length > 0) id = $"quilt-loader-{quilt}-{mc}";
        else if (deps.TryGetValue("forge", out var forge) && forge.Length > 0) id = $"{mc}-forge-{forge}";
        else if (deps.TryGetValue("neoforge", out var neo) && neo.Length > 0) id = $"neoforge-{neo}";
        return id is not null && File.Exists(Path.Combine(dotMc, "versions", id, $"{id}.json")) ? id : null;
    }

    // ───────────────────────── T9 备份与回档 ─────────────────────────

    /// <summary>备份与回档对话框：粒度/附加内容 → 备份；列表内可回档 / 删除 / 重建索引</summary>
    public static async Task BackupAsync(XamlRoot root, AppServices s, LocalVersionRow row)
    {
        var dotMc = s.Settings.DotMinecraft;
        if (App.RunningVersionId == row.Id)
        {
            await InfoAsync(root, "备份与回档", "该版本正在运行，请先退出游戏再备份或回档。");
            return;
        }

        var isolated = row.Isolated;
        while (true)
        {
            var state = new BackupPanelState();
            ContentDialog? dlg = null;
            var panel = BuildBackupPanel(s, dotMc, row.Id, isolated, state, () => dlg?.Hide(), out var gather);
            dlg = new ContentDialog
            {
                Title = $"备份与回档 · {row.Id}",
                Content = panel,
                PrimaryButtonText = "开始备份",
                CloseButtonText = "关闭",
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
            };
            var res = await dlg.ShowAsync();

            if (state.OpenDir)
            {
                OpenDir(InstanceLayout.BackupRoot(dotMc));
                continue;
            }
            if (state.Rebuild)
            {
                s.VersionBackup.RebuildIndex(dotMc);
                continue;
            }
            if (state.Delete is { } del)
            {
                if (await ConfirmAsync(root, "删除备份", $"确定删除备份「{del.Name}」？此操作不可撤销。"))
                    s.VersionBackup.Delete(dotMc, del.Id);
                continue;
            }
            if (state.Restore is { } restore)
            {
                var ok = await ConfirmAsync(root, "确认回档",
                    $"将把「{restore.Name}」的内容覆盖回当前版本。\n回档前会先自动备份当前状态。");
                if (ok)
                {
                    try
                    {
                        await RunWithProgressAsync(root, "正在回档", "后台回档",
                            async p =>
                            {
                                await s.VersionBackup.RestoreAsync(dotMc, restore, isolated, p);
                                return "ok";
                            }, indeterminate: true);
                        await InfoAsync(root, "回档完成", $"已回档到「{restore.Name}」。");
                    }
                    catch (Exception ex)
                    {
                        await ErrorAsync(root, "回档失败", ex);
                    }
                }
                continue;
            }
            if (res == ContentDialogResult.Primary)
            {
                var req = gather();
                if (req is null)
                {
                    await InfoAsync(root, "备份与回档", "请填写备份名称。");
                    continue;
                }
                try
                {
                    var entry = await RunWithProgressAsync(root, "正在备份", "后台备份",
                        p => s.VersionBackup.BackupAsync(dotMc, req, p), indeterminate: true);
                    if (entry is not null)
                        await InfoAsync(root, "备份完成",
                            $"已创建备份「{entry.Name}」（{FormatSize(entry.SizeBytes)}，{entry.FileCount} 个文件）。");
                }
                catch (Exception ex)
                {
                    await ErrorAsync(root, "备份失败", ex);
                }
                continue;
            }
            return;
        }
    }

    /// <summary>构造备份面板；gather 返回 null 表示名称为空</summary>
    private static UIElement BuildBackupPanel(AppServices s, string dotMc, string versionId,
        bool isolated, BackupPanelState state, Action close, out Func<VersionBackupRequest?> gather)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 460, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = "备份粒度", FontSize = 12, Opacity = 0.8 });

        var radios = new RadioButtons
        {
            Items =
            {
                "最小（版本定义与启动器设置）",
                "标准（该版本的内容）",
                "完整（含共享 .minecraft 内容）",
            },
            SelectedIndex = 1,
        };
        panel.Children.Add(radios);

        var cbSaves = new CheckBox { Content = "世界存档", IsChecked = true };
        var cbConfig = new CheckBox { Content = "配置", IsChecked = true };
        var cbPack = new CheckBox { Content = "资源包" };
        var cbShader = new CheckBox { Content = "光影" };
        var attach = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        attach.Children.Add(new TextBlock
        {
            Text = "附加内容",
            FontSize = 12,
            Opacity = 0.8,
            VerticalAlignment = VerticalAlignment.Center,
        });
        attach.Children.Add(cbSaves);
        attach.Children.Add(cbConfig);
        attach.Children.Add(cbPack);
        attach.Children.Add(cbShader);
        panel.Children.Add(attach);

        var nameBox = new TextBox { Text = $"{versionId}-{DateTime.Now:yyyy-MM-dd_HHmmss}" };
        panel.Children.Add(new TextBlock { Text = "备份名称", FontSize = 12, Opacity = 0.8 });
        panel.Children.Add(nameBox);

        var estimate = new TextBlock { FontSize = 12, Opacity = 0.8 };
        panel.Children.Add(estimate);

        var hint = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(hint);

        List<string> Tags() => ResolveDirTags(radios.SelectedIndex,
            cbSaves.IsChecked == true, cbConfig.IsChecked == true,
            cbPack.IsChecked == true, cbShader.IsChecked == true, versionId, isolated);

        void RefreshEstimate()
        {
            // 去重：被上层目录覆盖的范围不重复计入
            var roots = new List<string>();
            foreach (var tag in Tags())
            {
                var abs = Path.GetFullPath(TagToAbs(dotMc, versionId, isolated, tag));
                if (roots.Any(r => abs.StartsWith(r + Path.DirectorySeparatorChar,
                        StringComparison.OrdinalIgnoreCase))) continue;
                roots.RemoveAll(r => r.StartsWith(abs + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase));
                if (!roots.Contains(abs, StringComparer.OrdinalIgnoreCase)) roots.Add(abs);
            }
            long bytes = 0;
            var files = 0;
            foreach (var abs in roots)
            {
                var m = Measure(abs);
                bytes += m.Bytes;
                files += m.Count;
            }
            estimate.Text = $"预计体积：约 {FormatSize(bytes)}（{files} 个文件）";
        }

        void ApplyGranularity(int index)
        {
            cbSaves.IsChecked = index >= 1;
            cbConfig.IsChecked = index >= 1;
            cbPack.IsChecked = index >= 2;
            cbShader.IsChecked = index >= 2;
            RefreshEstimate();
        }
        radios.SelectionChanged += (_, _) => ApplyGranularity(radios.SelectedIndex);
        cbSaves.Click += (_, _) => RefreshEstimate();
        cbConfig.Click += (_, _) => RefreshEstimate();
        cbPack.Click += (_, _) => RefreshEstimate();
        cbShader.Click += (_, _) => RefreshEstimate();

        panel.Children.Add(new TextBlock
        {
            Text = "已有备份",
            FontSize = 12,
            Opacity = 0.8,
            Margin = new Thickness(0, 6, 0, 0),
        });
        var entries = s.VersionBackup.List(dotMc, versionId);
        if (entries.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = "暂无备份。", FontSize = 12, Opacity = 0.7 });
        }
        else
        {
            var listPanel = new StackPanel { Spacing = 4 };
            foreach (var entry in entries) listPanel.Children.Add(BuildBackupRow(entry, state, close));
            panel.Children.Add(new ScrollViewer { MaxHeight = 200, Content = listPanel });
        }

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var openBtn = new Button { Content = "打开备份目录" };
        openBtn.Click += (_, _) => { state.OpenDir = true; close(); };
        tools.Children.Add(openBtn);
        var rebuildBtn = new Button { Content = "重建索引" };
        rebuildBtn.Click += (_, _) => { state.Rebuild = true; close(); };
        tools.Children.Add(rebuildBtn);
        panel.Children.Add(tools);

        RefreshEstimate();

        gather = () =>
        {
            var name = nameBox.Text.Trim();
            if (name.Length == 0) { hint.Text = "备份名称不能为空。"; return null; }
            return new VersionBackupRequest(versionId, isolated, name, Tags(), cbSaves.IsChecked == true);
        };
        return panel;
    }

    private static UIElement BuildBackupRow(BackupEntry entry, BackupPanelState state, Action close)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        row.Children.Add(new TextBlock
        {
            Text = entry.Name,
            Width = 240,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = FormatSize(entry.SizeBytes),
            Width = 80,
            Opacity = 0.8,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        row.Children.Add(new TextBlock
        {
            Text = $"{entry.CreatedAt:MM-dd HH:mm}",
            Width = 90,
            Opacity = 0.8,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var restoreBtn = new Button { Content = "回档", FontSize = 12 };
        restoreBtn.Click += (_, _) => { state.Restore = entry; close(); };
        row.Children.Add(restoreBtn);
        var deleteBtn = new Button { Content = "删除", FontSize = 12 };
        deleteBtn.Click += (_, _) => { state.Delete = entry; close(); };
        row.Children.Add(deleteBtn);
        return row;
    }

    /// <summary>粒度 + 附加内容 → 备份标签列表（"version" = 版本目录本体；"@x" = 相对 .minecraft 根）</summary>
    private static List<string> ResolveDirTags(int granularity, bool saves, bool config,
        bool packs, bool shaders, string versionId, bool isolated)
    {
        var tags = new List<string>();
        if (granularity == 0)
        {
            tags.Add($"versions/{versionId}/{versionId}.json");
            tags.Add($"versions/{versionId}/{versionId}.jar");
            tags.Add("@starcloud/version-settings.json");
        }
        else if (isolated)
        {
            // 标准 = 版本目录整体；完整 = 再附带相对 .minecraft 根的共享内容目录
            tags.Add("version");
            if (granularity >= 2) tags.AddRange(["@mods", "@config", "@resourcepacks", "@shaderpacks"]);
        }
        else
        {
            // 共享实例：内容目录就在 .minecraft 根下
            tags.AddRange(["mods", "config", "resourcepacks", "shaderpacks"]);
        }
        if (config) tags.Add("config");
        if (packs) tags.Add("resourcepacks");
        if (shaders) tags.Add("shaderpacks");
        _ = saves; // 世界存档通过 IncludeSaves 传给服务
        return tags;
    }

    /// <summary>标签 → 磁盘绝对路径（与 VersionBackupService.ResolveSources 规则一致）</summary>
    private static string TagToAbs(string dotMc, string versionId, bool isolated, string tag)
    {
        if (tag.Equals("version", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(dotMc, "versions", versionId);
        if (tag.StartsWith('@'))
            return Path.Combine(dotMc, tag[1..].Replace('/', Path.DirectorySeparatorChar));
        if (tag.StartsWith("versions/", StringComparison.OrdinalIgnoreCase))
            return Path.Combine(dotMc, tag.Replace('/', Path.DirectorySeparatorChar));
        return isolated
            ? Path.Combine(dotMc, "versions", versionId, tag.Replace('/', Path.DirectorySeparatorChar))
            : Path.Combine(dotMc, tag.Replace('/', Path.DirectorySeparatorChar));
    }

    // ───────────────────────── 公共辅助 ─────────────────────────

    private static Button MakePresetButton(string text, Action onClick)
    {
        var btn = new Button { Content = text, FontSize = 12 };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    private static (int Count, long Bytes) Measure(string abs)
    {
        if (File.Exists(abs))
        {
            try { return (1, new FileInfo(abs).Length); } catch { return (1, 0); }
        }
        if (!Directory.Exists(abs)) return (0, 0);
        var count = 0;
        long bytes = 0;
        try
        {
            foreach (var file in Directory.EnumerateFiles(abs, "*", SearchOption.AllDirectories))
            {
                count++;
                try { bytes += new FileInfo(file).Length; } catch { /* 单文件不可读时忽略 */ }
            }
        }
        catch { /* 目录不可枚举时按已统计部分计 */ }
        return (count, bytes);
    }

    private static UIElement CreateProgressPanel(out TextBlock statusText, out ProgressBar bar)
    {
        var panel = new StackPanel { Spacing = 10, MinWidth = 300 };
        statusText = new TextBlock { Text = "准备中…", TextWrapping = TextWrapping.Wrap, FontSize = 12 };
        bar = new ProgressBar { Maximum = 100 };
        panel.Children.Add(statusText);
        panel.Children.Add(bar);
        return panel;
    }

    /// <summary>后台进度对话框执行 work；任务结束后自动关闭弹窗</summary>
    private static async Task<T?> RunWithProgressAsync<T>(XamlRoot root, string title,
        string closeText, Func<IProgress<string>, Task<T>> work, bool indeterminate = false)
        where T : class
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = CreateProgressPanel(out var statusText, out var bar),
            CloseButtonText = closeText,
            XamlRoot = root,
        };
        bar.IsIndeterminate = indeterminate;
        _ = dlg.ShowAsync();
        var dq = DispatcherQueue.GetForCurrentThread();
        var progress = new Progress<string>(m => dq.TryEnqueue(() => statusText.Text = m));
        try
        {
            return await work(progress);
        }
        finally
        {
            try { dlg.Hide(); } catch { /* 用户已关闭弹窗时忽略 */ }
        }
    }

    private static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private static async Task InfoAsync(XamlRoot root, string title, string message)
    {
        var dlg = new ContentDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            CloseButtonText = "确定",
            XamlRoot = root,
        };
        await dlg.ShowAsync();
    }

    private static async Task ErrorAsync(XamlRoot root, string title, Exception error) =>
        await InfoAsync(root, title, error.Message);

    private static void OpenDir(string path)
    {
        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch { /* 打开目录失败不阻断流程 */ }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes <= 0) return "0 B";
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        double value = bytes;
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }
        return $"{value:0.#} {units[unit]}";
    }
}
