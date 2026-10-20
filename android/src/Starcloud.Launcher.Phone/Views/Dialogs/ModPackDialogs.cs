// 整合包与版本资产对话框（导出 / 导入 / 备份回档）：由 VersionsPage 入口调用
// 移植自桌面 Starcloud.App.Views.Dialogs.ModPackDialogs，安卓适配：
// - 导出无 FileSavePicker：固定落盘到「数据目录/exports/」，完成提示给出完整路径；
// - 「打开目录」无桌面资源管理器语义：弹窗展示路径（与版本页打开目录同口径）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Text;
using Starcloud.Launcher.Phone.Localization;
using Starcloud.Launcher.Phone.Services;
using Starcloud.Launcher.Phone.Views.Controls;
using Starcloud.Launcher.Phone.Views.Rows;
using Starcloud.Launcher.Phone.Views.Shared;
using Starcloud.Core.Services;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Starcloud.Launcher.Phone.Views.Dialogs;

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

    // ───────────────────────── 导出整合包 ─────────────────────────

    /// <summary>导出对话框：填高级设置 → 按所选格式打包（Modrinth / CurseForge / MultiMC）。
    /// 安卓无保存选择器：固定导出到 数据目录/exports/。</summary>
    public static async Task ExportAsync(XamlRoot root, AppServices s, LocalVersionRow row)
    {
        var dotMc = s.Settings.DotMinecraft;
        var profile = new InstanceSettingsStore(dotMc).Get(row.Id);
        var isolated = InstanceLayout.IsIsolated(profile, s.Settings);
        var gameRoot = InstanceLayout.GameRoot(dotMc, row.Id, isolated);

        var (gameVersion, deps) = ResolveDependencies(dotMc, row.Id);
        var form = await ModPackExportDialog.ShowAsync(root, s, row.Id, gameRoot, gameVersion, deps);
        if (form is null) return;

        var kind = form.Format switch
        {
            ModPackExportFormat.CurseForge => Loc.T("CurseForge 整合包"),
            ModPackExportFormat.MultiMc => Loc.T("MultiMC / Prism 实例包"),
            _ => Loc.T("Modrinth 整合包"),
        };

        // 安卓适配：无 FileSavePicker，导出落盘到应用数据目录 exports/（完成提示给出路径）
        var exportDir = Path.Combine(LauncherPaths.DataDir, "exports");
        Directory.CreateDirectory(exportDir);
        var ext = form.Format == ModPackExportFormat.Modrinth ? ".mrpack" : ".zip";
        var exportPath = Path.Combine(exportDir,
            $"{InstanceLayout.SafeFileName(row.Id)}-{DateTime.Now:yyyyMMdd-HHmmss}{ext}");

        var request = new ExportRequest(gameRoot, exportPath, form.Name, form.GameVersion, form.Dependencies, form.Dirs)
        {
            Format = form.Format,
            VersionId = form.VersionId,
            Author = form.Author,
            Description = form.Description,
            ExcludeRules = form.ExcludeRules,
            Compression = form.Compression,
            IncludeGameSettings = form.IncludeGameSettings,
            UnresolvedAsOverrides = form.UnresolvedAsOverrides,
        };
        ExportResult? result;
        try
        {
            result = await RunWithProgressAsync(root, Loc.T("导出整合包"), Loc.T("后台导出"),
                p => s.ModPackExport.ExportAsync(request, p), indeterminate: true);
        }
        catch (Exception ex)
        {
            await ErrorAsync(root, Loc.T("导出失败"), ex);
            return;
        }
        if (result is null) return;

        var message = Loc.T("{0} · 可下载 {1} 个 · 打包 {2} 个 · ", kind, result.DownloadableFiles, result.OverriddenFiles) +
                      Loc.T("共 {0}", FormatSize(result.TotalBytes));
        if (result.SkippedFiles > 0) message += Loc.T("\n按排除规则跳过 {0} 个文件。", result.SkippedFiles);
        if (!string.IsNullOrEmpty(result.LoaderWarning)) message += Loc.T("\n\n提示：{0}", result.LoaderWarning);
        message += Loc.T("\n\n已保存到：{0}", exportPath);
        await InfoAsync(root, Loc.T("导出完成"), message);
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

    private static string FormatLabel(ModPackFormat format) => format switch
    {
        ModPackFormat.CurseForge => Loc.T("CurseForge 整合包"),
        ModPackFormat.MultiMc => Loc.T("MultiMC / Prism 实例包"),
        _ => Loc.T("Modrinth 整合包"),
    };

    // ───────────────────────── 导入整合包 ─────────────────────────

    /// <summary>识别到的包预览（三种格式统一视图）</summary>
    private sealed record PackPreview(
        ModPackFormat Format, string Name, string GameVersion,
        IReadOnlyDictionary<string, string> Dependencies, int FileCount, long TotalBytes);

    /// <summary>导入对话框：自动识别格式 → 预览 → 选版本名与隔离 → 安装 + 联动装原版/加载器</summary>
    public static async Task ImportAsync(XamlRoot root, AppServices s, string packPath)
    {
        ModPackProbe? probe;
        try { probe = ModPackDetector.Detect(packPath); }
        catch (Exception ex) { await InfoAsync(root, Loc.T("无法导入整合包"), ex.Message); return; }
        if (probe is null)
        {
            await InfoAsync(root, Loc.T("无法导入整合包"),
                Loc.T("未能识别整合包格式。支持 Modrinth（.mrpack / 内含 modrinth.index.json）、" +
                       "CurseForge（内含 manifest.json）与 MultiMC / Prism 实例包（内含 mmc-pack.json 或 instance.cfg）。"));
            return;
        }

        // 外层多套了一层目录的包先重打成根级包，安装器只需认根级入口文件
        var effective = ModPackArchive.FlattenIfNested(packPath, probe);
        try
        {
            PackPreview preview;
            try { preview = ReadPreview(s, effective, probe.Format); }
            catch (Exception ex) { await InfoAsync(root, Loc.T("无法导入整合包"), ex.Message); return; }
            await RunImportAsync(root, s, effective, preview);
        }
        finally
        {
            if (!ReferenceEquals(effective, packPath))
            {
                try { File.Delete(effective); } catch { /* 临时包清理失败可忽略 */ }
            }
        }
    }

    /// <summary>按格式读取预览信息（CurseForge 的加载器 id 需转成统一依赖键）</summary>
    private static PackPreview ReadPreview(AppServices s, string packPath, ModPackFormat format)
    {
        switch (format)
        {
            case ModPackFormat.CurseForge:
            {
                var info = s.CurseForgePacks.ReadPackageInfo(packPath);
                var deps = new Dictionary<string, string>();
                if (!string.IsNullOrWhiteSpace(info.GameVersion)) deps["minecraft"] = info.GameVersion;
                if (CurseForgePackInstaller.ParseLoader(info.LoaderId) is var (name, version) && name is not null)
                    deps[name] = version ?? "";
                return new PackPreview(format, info.Name, info.GameVersion, deps,
                    info.FileCount + info.OverrideFileCount, info.TotalBytes);
            }
            case ModPackFormat.MultiMc:
            {
                var info = s.MultiMc.ReadPackageInfo(packPath);
                return new PackPreview(format, info.Name, info.GameVersion, info.Dependencies,
                    info.FileCount, info.TotalBytes);
            }
            default:
            {
                var info = s.MrPack.ReadPackageInfo(packPath);
                return new PackPreview(format, info.Name, info.GameVersion, info.Dependencies,
                    info.DownloadFileCount + info.OverrideFileCount, info.TotalBytes);
            }
        }
    }

    /// <summary>导入对话框主体：预览 → 选版本名与隔离 → 按格式安装 → 联动装原版/加载器</summary>
    private static async Task RunImportAsync(XamlRoot root, AppServices s, string packPath, PackPreview preview)
    {
        var dotMc = s.Settings.DotMinecraft;

        TextBox nameBox = null!;
        CheckBox isoBox = null!;
        TextBlock hint = null!;

        UIElement BuildPanel()
        {
            nameBox = new TextBox
            {
                Text = InstanceLayout.SafeFileName(preview.Name),
                PlaceholderText = Loc.T("新建版本名称"),
            };
            isoBox = new CheckBox
            {
                Content = Loc.T("版本隔离（推荐，整合包内容单独存放）"),
                IsChecked = true,
            };
            hint = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
            var note = new TextBlock { FontSize = 12, Opacity = 0.8, TextWrapping = TextWrapping.Wrap };
            void SyncNote() => note.Text = isoBox.IsChecked == true
                ? Loc.T("整合包内容将单独存放在 versions/<新名称>/ 下。")
                : Loc.T("将写入共享 .minecraft 目录，不会新建版本条目。");
            isoBox.Checked += (_, _) => SyncNote();
            isoBox.Unchecked += (_, _) => SyncNote();
            SyncNote();

            var loader = DescribeLoader(preview.Dependencies);
            // 手机版：去掉 MinWidth=420 固定下限（竖屏可用宽约 300-360 DIP 放不下），宽度随对话框自适应，上限 520 不变
            var panel = new StackPanel { Spacing = 10, MaxWidth = 520 };
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("格式：{0}", FormatLabel(preview.Format)),
                FontSize = 12,
                Opacity = 0.8,
            });
            panel.Children.Add(new TextBlock
            {
                Text = Loc.T("包名：") + preview.Name,
                FontWeight = FontWeights.Medium,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock
            {
                Text = $"Minecraft {preview.GameVersion}" + (loader.Length > 0 ? $" · {loader}" : "") +
                       Loc.T(" · {0} 个文件 · {1}", preview.FileCount, FormatSize(preview.TotalBytes)),
                FontSize = 12,
                Opacity = 0.8,
                TextWrapping = TextWrapping.Wrap,
            });
            panel.Children.Add(new TextBlock { Text = Loc.T("新建版本名称"), FontSize = 12, Opacity = 0.8 });
            panel.Children.Add(nameBox);
            panel.Children.Add(isoBox);
            panel.Children.Add(note);
            panel.Children.Add(hint);
            return panel;
        }

        string targetId;
        while (true)
        {
            var dlg = new ThemedDialog
            {
                Title = Loc.T("导入整合包"),
                Content = BuildPanel(),
                PrimaryButtonText = Loc.T("导入"),
                CloseButtonText = Loc.T("取消"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
            };
            if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;
            var typed = nameBox.Text.Trim();
            if (!InstanceLayout.IsValidVersionId(typed))
            {
                hint.Text = Loc.T("版本名不能为空，且不能包含 < > : \" / \\ | ? *");
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

        var progressDlg = new ThemedDialog
        {
            Title = Loc.T("导入整合包"),
            Content = CreateProgressPanel(out var statusText, out var bar),
            CloseButtonText = Loc.T("后台导入"),
            XamlRoot = root,
        };
        _ = progressDlg.ShowAsync();
        var progress = new Progress<BatchProgress>(p => UiThread.Run(() =>
        {
            bar.Value = p.Percentage;
            statusText.Text = p.Stage;
        }));

        string summary;
        try
        {
            if (isolated) Directory.CreateDirectory(installRoot);
            var result = preview.Format switch
            {
                ModPackFormat.CurseForge => await s.CurseForgePacks.InstallAsync(packPath, s.Settings, installRoot, progress: progress),
                ModPackFormat.MultiMc => await s.MultiMc.InstallAsync(packPath, s.Settings, installRoot, progress: progress),
                _ => await s.MrPack.InstallAsync(packPath, s.Settings, installRoot, progress: progress),
            };
            summary = await MrPackFlow.EnsureGameAndLoaderAsync(result, s, progress);
            if (isolated)
            {
                // 版本骨架：父版本优先指向联动装好的加载器，否则退回首层原版
                var parent = LoaderVersionId(dotMc, result.GameVersion, result.Dependencies) ?? result.GameVersion;
                var jsonPath = Path.Combine(installRoot, $"{targetId}.json");
                if (!File.Exists(jsonPath) && !string.IsNullOrEmpty(parent))
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
            await ErrorAsync(root, Loc.T("导入失败"), ex);
            return;
        }
        try { progressDlg.Hide(); } catch { /* 用户已关闭弹窗时忽略 */ }

        var message = isolated
            ? Loc.T("已创建版本「{0}」。\n{1}", targetId, summary)
            : Loc.T("已写入共享 .minecraft 目录。\n{0}", summary);
        if (renamed) message += Loc.T("\n（同名版本已存在，已自动改名）");
        await InfoAsync(root, Loc.T("导入完成"), message);
    }

    /// <summary>从链接导入：粘贴 Modrinth / CurseForge 整合包页或压缩包直链 → 下载 → 走统一导入流程</summary>
    public static async Task ImportFromLinkAsync(XamlRoot root, AppServices s)
    {
        var input = new TextBox
        {
            PlaceholderText = Loc.T("https://modrinth.com/modpack/… 或 …/curseforge.com/minecraft/modpacks/…"),
        };
        // 手机版：去掉 MinWidth=440 固定下限（竖屏可用宽约 300-360 DIP 放不下），宽度随对话框自适应，上限 540 不变
        var panel = new StackPanel { Spacing = 10, MaxWidth = 540 };
        panel.Children.Add(new TextBlock
        {
            Text = Loc.T("支持 Modrinth / CurseForge 整合包页面链接，以及 .mrpack / .zip 压缩包直链。"),
            FontSize = 12,
            Opacity = 0.8,
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(input);
        var dlg = new ThemedDialog
        {
            Title = Loc.T("从链接导入"),
            Content = panel,
            PrimaryButtonText = Loc.T("下载并导入"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        if (await dlg.ShowAsync() != ContentDialogResult.Primary) return;

        var link = input.Text.Trim();
        if (link.Length == 0) return;

        string? packPath;
        try
        {
            packPath = await RunWithProgressAsync(root, Loc.T("从链接导入"), Loc.T("后台下载"),
                p => ResolveAndDownloadAsync(s, link, p, CancellationToken.None), indeterminate: true);
        }
        catch (Exception ex)
        {
            await ErrorAsync(root, Loc.T("下载失败"), ex);
            return;
        }
        if (packPath is null) return;

        try { await ImportAsync(root, s, packPath); }
        finally { try { File.Delete(packPath); } catch { /* 临时包清理失败可忽略 */ } }
    }

    /// <summary>解析链接 → 下载到临时包，返回临时包路径</summary>
    private static async Task<string> ResolveAndDownloadAsync(
        AppServices s, string link, IProgress<string> status, CancellationToken ct)
    {
        var (url, fileName) = await ResolvePackUrlAsync(s, link, status, ct);
        var ext = fileName.EndsWith(".mrpack", StringComparison.OrdinalIgnoreCase) ? ".mrpack" : ".zip";
        var dir = Path.Combine(Path.GetTempPath(), "starcloud-pack");
        Directory.CreateDirectory(dir);
        var dest = Path.Combine(dir, $"{Guid.NewGuid():N}{ext}");
        status.Report(Loc.T("正在下载 {0}…", fileName));
        await s.Download.DownloadFileAsync(url, dest, null, 3, ct);
        return dest;
    }

    /// <summary>把粘贴的链接解析成整合包直链：Modrinth / CurseForge 页面 → 最新（或指定）文件；压缩包直链原样返回</summary>
    private static async Task<(string Url, string FileName)> ResolvePackUrlAsync(
        AppServices s, string link, IProgress<string> status, CancellationToken ct)
    {
        if (!Uri.TryCreate(link, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            throw new InvalidOperationException(Loc.T("请输入完整的 http(s) 链接。"));

        var host = uri.Host.ToLowerInvariant();
        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (host.EndsWith("modrinth.com", StringComparison.Ordinal))
        {
            // https://modrinth.com/modpack/<slug>[/version/<version>]
            var index = Array.FindIndex(segments, x => x.Equals("modpack", StringComparison.OrdinalIgnoreCase) ||
                                                       x.Equals("modpacks", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= segments.Length)
                throw new InvalidOperationException(Loc.T("不是有效的 Modrinth 整合包链接。"));

            status.Report(Loc.T("正在解析 Modrinth 整合包…"));
            var versions = await s.Mods.ListVersionsAsync(segments[index + 1], null, null, ct);
            if (versions.Count == 0) throw new InvalidOperationException(Loc.T("该整合包没有可下载的文件。"));

            var pick = versions[0];
            if (index + 3 < segments.Length && segments[index + 2].Equals("version", StringComparison.OrdinalIgnoreCase))
            {
                var want = segments[index + 3];
                pick = versions.FirstOrDefault(v =>
                           string.Equals(v.VersionNumber, want, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(v.Id, want, StringComparison.OrdinalIgnoreCase)) ?? pick;
            }
            var file = pick.PrimaryFile ?? throw new InvalidOperationException(Loc.T("该整合包没有可下载的文件。"));
            return (file.Url, file.Filename);
        }

        if (host.EndsWith("curseforge.com", StringComparison.Ordinal))
        {
            // https://www.curseforge.com/minecraft/modpacks/<slug>[/files/<fileId>]
            var index = Array.FindIndex(segments, x => x.Equals("modpacks", StringComparison.OrdinalIgnoreCase) ||
                                                       x.Equals("modpack", StringComparison.OrdinalIgnoreCase));
            if (index < 0 || index + 1 >= segments.Length)
                throw new InvalidOperationException(Loc.T("不是有效的 CurseForge 整合包链接。"));

            status.Report(Loc.T("正在解析 CurseForge 整合包…"));
            var slug = segments[index + 1];
            var hits = (await s.CurseForge.SearchAsync(slug, "modpack", null, null, 0, 20, "relevance", ct)).Hits;
            var hit = hits.FirstOrDefault(h => string.Equals(h.Slug, slug, StringComparison.OrdinalIgnoreCase))
                      ?? hits.FirstOrDefault()
                      ?? throw new InvalidOperationException(Loc.T("未找到该整合包。"));

            var fileId = index + 3 < segments.Length &&
                         segments[index + 2].Equals("files", StringComparison.OrdinalIgnoreCase)
                ? segments[index + 3]
                : null;
            var version = fileId is null
                ? (await s.CurseForge.ListVersionsAsync(hit.ProjectId, null, null, ct)).FirstOrDefault()
                : await s.CurseForge.GetFileAsync(hit.ProjectId, fileId, ct);
            var cfFile = version?.PrimaryFile ?? throw new InvalidOperationException(Loc.T("该整合包没有可下载的文件。"));
            return (cfFile.Url, cfFile.Filename);
        }

        // 直链：必须是压缩包扩展名
        var ext = Path.GetExtension(uri.AbsolutePath);
        if (!ext.Equals(".zip", StringComparison.OrdinalIgnoreCase) &&
            !ext.Equals(".mrpack", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                Loc.T("无法识别的链接。请粘贴 Modrinth / CurseForge 整合包页面或压缩包直链。"));
        return (uri.ToString(), Path.GetFileName(uri.AbsolutePath));
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

    // ───────────────────────── 备份与回档 ─────────────────────────

    /// <summary>备份与回档对话框：粒度/附加内容 → 备份；列表内可回档 / 删除 / 重建索引</summary>
    public static async Task BackupAsync(XamlRoot root, AppServices s, LocalVersionRow row)
    {
        var dotMc = s.Settings.DotMinecraft;
        if (s.Runtime.Instances.Any(i => i.VersionId == row.Id))
        {
            await InfoAsync(root, Loc.T("备份与回档"), Loc.T("该版本正在运行，请先退出游戏再备份或回档。"));
            return;
        }

        var isolated = row.Isolated;
        while (true)
        {
            var state = new BackupPanelState();
            ContentDialog? dlg = null;
            var panel = BuildBackupPanel(s, dotMc, row.Id, isolated, state, () => dlg?.Hide(), out var gather);
            dlg = new ThemedDialog
            {
                Title = Loc.T("备份与回档 · {0}", row.Id),
                Content = panel,
                PrimaryButtonText = Loc.T("开始备份"),
                CloseButtonText = Loc.T("关闭"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
            };
            var res = await dlg.ShowAsync();

            if (state.OpenDir)
            {
                ShowDir(InstanceLayout.BackupRoot(dotMc));
                continue;
            }
            if (state.Rebuild)
            {
                s.VersionBackup.RebuildIndex(dotMc);
                continue;
            }
            if (state.Delete is { } del)
            {
                if (await ConfirmAsync(root, Loc.T("删除备份"), Loc.T("确定删除备份「{0}」？此操作不可撤销。", del.Name)))
                    s.VersionBackup.Delete(dotMc, del.Id);
                continue;
            }
            if (state.Restore is { } restore)
            {
                var ok = await ConfirmAsync(root, Loc.T("确认回档"),
                    Loc.T("将把「{0}」的内容覆盖回当前版本。\n回档前会先自动备份当前状态。", restore.Name));
                if (ok)
                {
                    try
                    {
                        await RunWithProgressAsync(root, Loc.T("正在回档"), Loc.T("后台回档"),
                            async p =>
                            {
                                await s.VersionBackup.RestoreAsync(dotMc, restore, isolated, p);
                                return "ok";
                            }, indeterminate: true);
                        await InfoAsync(root, Loc.T("回档完成"), Loc.T("已回档到「{0}」。", restore.Name));
                    }
                    catch (Exception ex)
                    {
                        await ErrorAsync(root, Loc.T("回档失败"), ex);
                    }
                }
                continue;
            }
            if (res == ContentDialogResult.Primary)
            {
                var req = gather();
                if (req is null)
                {
                    await InfoAsync(root, Loc.T("备份与回档"), Loc.T("请填写备份名称。"));
                    continue;
                }
                try
                {
                    var entry = await RunWithProgressAsync(root, Loc.T("正在备份"), Loc.T("后台备份"),
                        p => s.VersionBackup.BackupAsync(dotMc, req, p), indeterminate: true);
                    if (entry is not null)
                        await InfoAsync(root, Loc.T("备份完成"),
                            Loc.T("已创建备份「{0}」（{1}，{2} 个文件）。", entry.Name, FormatSize(entry.SizeBytes), entry.FileCount));
                }
                catch (Exception ex)
                {
                    await ErrorAsync(root, Loc.T("备份失败"), ex);
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
        // 手机版：去掉 MinWidth=460 固定下限（竖屏可用宽约 300-360 DIP 放不下），宽度随对话框自适应，上限 560 不变
        var panel = new StackPanel { Spacing = 10, MaxWidth = 560 };
        panel.Children.Add(new TextBlock { Text = Loc.T("备份粒度"), FontSize = 12, Opacity = 0.8 });

        var radios = new RadioButtons
        {
            Items =
            {
                Loc.T("最小（版本定义与启动器设置）"),
                Loc.T("标准（该版本的内容）"),
                Loc.T("完整（含共享 .minecraft 内容）"),
            },
            SelectedIndex = 1,
        };
        panel.Children.Add(radios);

        var cbSaves = new CheckBox { Content = Loc.T("世界存档"), IsChecked = true };
        var cbConfig = new CheckBox { Content = Loc.T("配置"), IsChecked = true };
        var cbPack = new CheckBox { Content = Loc.T("资源包") };
        var cbShader = new CheckBox { Content = Loc.T("光影") };
        // 手机版：原「附加内容」标签与四个复选框横排一行约 370 DIP，竖屏放不下，
        // 改为标签单独一行 + 复选框两行两列（勾选项与刷新逻辑不变）
        panel.Children.Add(new TextBlock { Text = Loc.T("附加内容"), FontSize = 12, Opacity = 0.8 });
        var attach = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        attach.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        attach.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        attach.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        attach.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetColumn(cbSaves, 0);
        Grid.SetColumn(cbConfig, 1);
        Grid.SetRow(cbPack, 1);
        Grid.SetColumn(cbPack, 0);
        Grid.SetRow(cbShader, 1);
        Grid.SetColumn(cbShader, 1);
        attach.Children.Add(cbSaves);
        attach.Children.Add(cbConfig);
        attach.Children.Add(cbPack);
        attach.Children.Add(cbShader);
        panel.Children.Add(attach);

        var nameBox = new TextBox { Text = $"{versionId}-{DateTime.Now:yyyy-MM-dd_HHmmss}" };
        panel.Children.Add(new TextBlock { Text = Loc.T("备份名称"), FontSize = 12, Opacity = 0.8 });
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
            estimate.Text = Loc.T("预计体积：约 {0}（{1} 个文件）", FormatSize(bytes), files);
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
            Text = Loc.T("已有备份"),
            FontSize = 12,
            Opacity = 0.8,
            Margin = new Thickness(0, 6, 0, 0),
        });
        var entries = s.VersionBackup.List(dotMc, versionId);
        if (entries.Count == 0)
        {
            panel.Children.Add(new TextBlock { Text = Loc.T("暂无备份。"), FontSize = 12, Opacity = 0.7 });
        }
        else
        {
            var listPanel = new StackPanel { Spacing = 4 };
            foreach (var entry in entries) listPanel.Children.Add(BuildBackupRow(entry, state, close));
            panel.Children.Add(new ScrollViewer { MaxHeight = 200, Content = listPanel });
        }

        var tools = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var openBtn = new GlowButton { Content = Loc.T("打开备份目录") };
        openBtn.Click += (_, _) => { state.OpenDir = true; close(); };
        tools.Children.Add(openBtn);
        var rebuildBtn = new GlowButton { Content = Loc.T("重建索引") };
        rebuildBtn.Click += (_, _) => { state.Rebuild = true; close(); };
        tools.Children.Add(rebuildBtn);
        panel.Children.Add(tools);

        RefreshEstimate();

        gather = () =>
        {
            var name = nameBox.Text.Trim();
            if (name.Length == 0) { hint.Text = Loc.T("备份名称不能为空。"); return null; }
            return new VersionBackupRequest(versionId, isolated, name, Tags(), cbSaves.IsChecked == true);
        };
        return panel;
    }

    private static UIElement BuildBackupRow(BackupEntry entry, BackupPanelState state, Action close)
    {
        // 手机版：原横排一行（名称固定 240 + 大小 80 + 日期 90 + 回档/删除两钮 ≈ 550 DIP）竖屏放不下，
        // 改为两行：上行名称独占整行（超长省略号），下行 大小/日期 + 回档/删除按钮
        var row = new StackPanel { Spacing = 4 };

        row.Children.Add(new TextBlock
        {
            Text = entry.Name,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        });

        var meta = new Grid { ColumnSpacing = 8 };
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        meta.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var metaInfo = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        metaInfo.Children.Add(new TextBlock
        {
            Text = FormatSize(entry.SizeBytes),
            Opacity = 0.8,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        metaInfo.Children.Add(new TextBlock
        {
            Text = $"{entry.CreatedAt:MM-dd HH:mm}",
            Opacity = 0.8,
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        });
        meta.Children.Add(metaInfo);

        // 手机版：回档/删除双按钮保留横排（两钮在 300 DIP 内放得下），与大小/日期同行右对齐
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var restoreBtn = new GlowButton { Content = Loc.T("回档"), FontSize = 12 };
        restoreBtn.Click += (_, _) => { state.Restore = entry; close(); };
        actions.Children.Add(restoreBtn);
        var deleteBtn = new GlowButton { Content = Loc.T("删除"), FontSize = 12 };
        deleteBtn.Click += (_, _) => { state.Delete = entry; close(); };
        actions.Children.Add(deleteBtn);
        Grid.SetColumn(actions, 1);
        meta.Children.Add(actions);

        row.Children.Add(meta);
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

    public static Button MakePresetButton(string text, Action onClick)
    {
        var btn = new GlowButton { Content = text, FontSize = 12 };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    public static (int Count, long Bytes) Measure(string abs)
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
        statusText = new TextBlock { Text = Loc.T("准备中…"), TextWrapping = TextWrapping.Wrap, FontSize = 12 };
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
        var dlg = new ThemedDialog
        {
            Title = title,
            Content = CreateProgressPanel(out var statusText, out var bar),
            CloseButtonText = closeText,
            XamlRoot = root,
        };
        bar.IsIndeterminate = indeterminate;
        _ = dlg.ShowAsync();
        var progress = new Progress<string>(m => UiThread.Run(() => statusText.Text = m));
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
        var dlg = new ThemedDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = Loc.T("确定"),
            CloseButtonText = Loc.T("取消"),
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = root,
        };
        return await dlg.ShowAsync() == ContentDialogResult.Primary;
    }

    private static async Task InfoAsync(XamlRoot root, string title, string message)
    {
        var dlg = new ThemedDialog
        {
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 460 },
            CloseButtonText = Loc.T("确定"),
            XamlRoot = root,
        };
        await dlg.ShowAsync();
    }

    private static async Task ErrorAsync(XamlRoot root, string title, Exception error) =>
        await InfoAsync(root, title, error.Message);

    /// <summary>安卓无桌面资源管理器：弹窗展示目录路径（保证目录已建好，便于文件管理器查看）</summary>
    private static void ShowDir(string path)
    {
        try { Directory.CreateDirectory(path); } catch { /* 创建失败不影响展示 */ }
        _ = UiDialog.AlertAsync(Loc.T("备份目录"), path);
    }

    public static string FormatSize(long bytes)
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
