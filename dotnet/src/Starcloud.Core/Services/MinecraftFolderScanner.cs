// 硬盘 .minecraft 目录扫描：给「添加游戏目录」用。
//
// 分两段，先快后慢：
//   ① 已知位置：官方默认目录 + 常见第三方启动器的实例目录（PCL/HMCL/MultiMC/Prism/CurseForge/Modrinth），
//      这些路径几乎零成本，先出结果让用户马上能选；
//   ② 逐盘广度优先：只走固定盘与移动盘，深度上限 3 层，跳过系统目录与依赖目录，
//      并对每个候选做「像不像游戏目录」的结构校验，避免把随便一个同名空文件夹列出来。
//
// 边界（重要）：整盘遍历是 IO 密集操作，必须有上限——目录数/耗时到顶就停，返回已找到的；
// 全程可取消。判断只看目录结构，不读任何文件内容。

using System.Collections.Concurrent;

namespace Starcloud.Core.Services;

/// <summary>扫描到的候选目录</summary>
public sealed record MinecraftFolderCandidate(string Path, int VersionCount)
{
    /// <summary>展示用：路径 + 版本数标签</summary>
    public string Display => VersionCount > 0
        ? $"{Path}（{VersionCount} 个版本）"
        : Path;

    /// <summary>ListView 默认用 ToString 显示条目，record 的默认格式太长，直接给展示名</summary>
    public override string ToString() => Display;
}

public static class MinecraftFolderScanner
{
    /// <summary>逐盘遍历的最大深度（盘根 = 0）</summary>
    private const int MaxDepth = 3;

    /// <summary>单盘最多看多少个目录：防止超大硬盘把扫描拖成几分钟</summary>
    private const int MaxDirsPerDrive = 120_000;

    /// <summary>整次扫描的目录总上限</summary>
    private const int MaxDirsTotal = 400_000;

    /// <summary>不算游戏目录的系统/依赖目录（小写比较）</summary>
    private static readonly HashSet<string> SkipNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "windows", "program files", "program files (x86)", "programdata", "recovery",
        "$recycle.bin", "system volume information", "perflogs", "windowsapps",
        "node_modules", ".git", ".gradle", ".cache", "temp", "tmp",
        "onedrivetemp", "包缓存", "package cache",
    };

    /// <summary>
    /// 已知位置（官方与常见第三方启动器）。返回的路径不一定存在，调用方自己判断。
    /// 这些是「大概率有」的位置，先扫它们能让用户几乎立刻看到结果。
    /// </summary>
    public static IReadOnlyList<string> KnownLocations()
    {
        var list = new List<string>();
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        // 官方 / 大多数启动器的默认位置
        list.Add(PlatformInfo.DefaultDotMinecraft);
        list.Add(Path.Combine(appData, ".minecraft"));

        // 第三方启动器的实例目录：每个实例本身就是一份游戏目录（不叫 .minecraft）
        void AddInstanceRoot(string root)
        {
            try
            {
                if (!Directory.Exists(root)) return;
                foreach (var dir in Directory.EnumerateDirectories(root)) list.Add(dir);
            }
            catch
            {
                // 目录读不到就算了
            }
        }

        AddInstanceRoot(Path.Combine(appData, "MultiMC", "instances"));
        AddInstanceRoot(Path.Combine(appData, "PrismLauncher", "instances"));
        AddInstanceRoot(Path.Combine(appData, "com.modrinth.theseus", "profiles"));
        AddInstanceRoot(Path.Combine(userProfile, "curseforge", "minecraft", "Instances"));
        AddInstanceRoot(Path.Combine(userProfile, "Documents", "curseforge", "minecraft", "Instances"));
        AddInstanceRoot(Path.Combine(userProfile, "Desktop", "curseforge", "minecraft", "Instances"));

        // 老启动器的数据目录里可能放着多个版本目录（各自带 versions/）
        list.Add(Path.Combine(appData, "PCL"));
        list.Add(Path.Combine(localAppData, "PCL"));

        return list;
    }

    /// <summary>
    /// 这个目录像不像一份 Minecraft 游戏目录：有 versions/、assets/、libraries/、
    /// launcher_profiles.json、options.txt、mods/ 中的任意一项即可（空文件夹不算）。
    /// </summary>
    public static bool LooksLikeMinecraft(string dir)
    {
        try
        {
            if (!Directory.Exists(dir)) return false;
            foreach (var marker in new[]
                     {
                         "versions", "assets", "libraries", "mods",
                         "launcher_profiles.json", "options.txt", "launcher_profiles_microsoft_store.json",
                     })
            {
                var path = Path.Combine(dir, marker);
                if (Directory.Exists(path) || File.Exists(path)) return true;
            }
            return false;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>versions 子目录数量（版本数标签用；读不到按 0）</summary>
    public static int CountVersions(string dir)
    {
        try
        {
            var versions = Path.Combine(dir, "versions");
            return Directory.Exists(versions) ? Directory.EnumerateDirectories(versions).Count() : 0;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>
    /// 执行扫描：先报已知位置，再逐盘 BFS。<paramref name="found"/> 在后台线程回调（调用方自行切线程）。
    /// 同一个目录只报一次。
    /// </summary>
    public static Task ScanAsync(
        Action<MinecraftFolderCandidate>? found,
        Action<string>? status,
        CancellationToken ct = default) =>
        Task.Run(() =>
        {
            var seen = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);

            void Report(string dir)
            {
                ct.ThrowIfCancellationRequested();
                string full;
                try { full = Path.GetFullPath(dir); } catch { return; }
                if (!seen.TryAdd(full, true)) return;
                if (!LooksLikeMinecraft(full)) return;
                found?.Invoke(new MinecraftFolderCandidate(full, CountVersions(full)));
            }

            // ① 已知位置
            status?.Invoke("正在检查已知位置…");
            foreach (var dir in KnownLocations())
            {
                ct.ThrowIfCancellationRequested();
                try { if (Directory.Exists(dir)) Report(dir); } catch { }
            }

            // ② 逐盘 BFS
            var total = 0;
            foreach (var drive in SafeDrives())
            {
                ct.ThrowIfCancellationRequested();
                status?.Invoke($"正在扫描 {drive.Name.TrimEnd('\\')} …");
                var budget = MaxDirsPerDrive;
                var queue = new Queue<(string Dir, int Depth)>();
                queue.Enqueue((drive.RootDirectory.FullName, 0));
                while (queue.Count > 0 && budget-- > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    if (++total > MaxDirsTotal)
                    {
                        status?.Invoke("扫描目录数已达上限，结果以目前找到的为准");
                        return;
                    }
                    var (dir, depth) = queue.Dequeue();
                    IEnumerable<string> children;
                    try
                    {
                        children = Directory.EnumerateDirectories(dir, "*", new EnumerationOptions
                        {
                            IgnoreInaccessible = true,
                            AttributesToSkip = FileAttributes.ReparsePoint | FileAttributes.System,
                        });
                    }
                    catch
                    {
                        continue; // 无权限 / 盘拔出
                    }
                    foreach (var child in children)
                    {
                        var name = Path.GetFileName(child);
                        if (SkipNames.Contains(name)) continue;
                        if (name.EndsWith('.')) continue; // .git / .cache 之类的隐藏依赖目录

                        // 候选：目录名就叫 .minecraft（或任意层级上像游戏目录的实例目录）
                        if (name.Equals(".minecraft", StringComparison.OrdinalIgnoreCase))
                        {
                            Report(child);
                            continue; // 游戏目录内部不再往下走
                        }
                        if (depth + 1 < MaxDepth) queue.Enqueue((child, depth + 1));
                    }
                }
            }
            status?.Invoke("扫描完成");
        }, ct);

    /// <summary>可扫的盘：固定盘与移动盘；网络盘/光驱跳过（太慢或没意义）</summary>
    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); } catch { yield break; }
        foreach (var d in drives)
        {
            DriveInfo probe = d;
            bool ok;
            try { ok = probe.IsReady && probe.DriveType is DriveType.Fixed or DriveType.Removable; }
            catch { ok = false; }
            if (ok) yield return probe;
        }
    }
}
