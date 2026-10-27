// 版本列表可见性：把「只是别人依赖」的版本从列表里藏起来。
//
// 场景：导入整合包时会联动安装原版与加载器（fabric-loader / forge），它们各自是独立的
// versions/<id> 目录，于是列表里一次多出两三个条目——看起来像装了三份游戏。
// 实际上只有「整合包实例」是用户要的东西，原版与加载器只是它 inheritsFrom 链上的依赖。
//
// 判为依赖的三种情况：
//   ① 被其他版本 inheritsFrom 引用的（包 → 加载器 → 原版，层层命中）；
//   ② 显式打了依赖标记的（.starcloud-dependency，整合包联动安装时写下）；
//   ③ 纯加载器形态且目录里没有任何自有内容（无 mods / 存档 / 资源包 / 光影 / 配置）——
//      这种版本只是装来给整合包用的，用户不会直接「玩」它。
//
// 依赖版本的文件不会被删除，只是不出现在列表里（仍能被整合包正常启动）。
// 兜底：过滤后一条都不剩时不过滤，避免列表空白。

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Starcloud.Core.Services;

public static class VersionVisibility
{
    /// <summary>依赖标记文件名：整合包联动安装时写在版本目录里（空文件）</summary>
    public const string DependencyMarker = ".starcloud-dependency";

    /// <summary>只认「纯加载器」形态的版本名（fabric / quilt / forge / neoforge 生成的版本）</summary>
    private static readonly Regex LoaderIdPattern = new(
        @"^(fabric-loader-|quilt-loader-|neoforge-|[0-9]+\.[0-9]+(\.[0-9]+)?-forge-)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>把版本标记为依赖（整合包联动安装的原版 / 加载器）</summary>
    public static void MarkDependency(string dotMinecraft, string versionId)
    {
        try
        {
            var dir = Path.Combine(dotMinecraft, "versions", versionId);
            if (!Directory.Exists(dir)) return;
            File.WriteAllText(Path.Combine(dir, DependencyMarker), "");
        }
        catch
        {
            // 标记失败只是列表里多一条，不影响安装
        }
    }

    /// <summary>返回「不应在列表里单列」的版本 id 集合。</summary>
    public static HashSet<string> ResolveHidden(string dotMinecraft)
    {
        var hidden = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var versionsDir = Path.Combine(dotMinecraft, "versions");
            if (!Directory.Exists(versionsDir)) return hidden;

            foreach (var dir in Directory.EnumerateDirectories(versionsDir))
            {
                var id = Path.GetFileName(dir);
                var json = Path.Combine(dir, id + ".json");
                if (!File.Exists(json)) continue;

                if (File.Exists(Path.Combine(dir, DependencyMarker)))
                {
                    hidden.Add(id);
                    continue;
                }

                var parent = ReadInheritsFrom(json);
                if (!string.IsNullOrEmpty(parent) && !parent.Equals(id, StringComparison.OrdinalIgnoreCase))
                    hidden.Add(parent);

                if (LoaderIdPattern.IsMatch(id) && !HasOwnContent(dir))
                    hidden.Add(id);
            }
        }
        catch
        {
            // 读不出来就当没有依赖：退回「全部显示」
        }
        return hidden;
    }

    /// <summary>
    /// 过滤安装列表：隐藏依赖版本；若全被隐藏（只剩依赖）则原样返回，避免列表空白。
    /// </summary>
    public static List<InstalledVersionInfo> Filter(string dotMinecraft,
        IReadOnlyList<InstalledVersionInfo> installed)
    {
        if (installed.Count == 0) return installed.ToList();
        var hidden = ResolveHidden(dotMinecraft);
        if (hidden.Count == 0) return installed.ToList();
        var visible = installed.Where(v => !hidden.Contains(v.Id)).ToList();
        return visible.Count > 0 ? visible : installed.ToList();
    }

    /// <summary>
    /// 版本目录里有没有「用户真的在用」的内容：mod / 存档 / 资源包 / 光影。
    /// <para>刻意不算 config / options.txt / logs：这些是启动器或游戏自动生成的，
    /// 空加载器版本跑过一次就全都有，用它们判断会把「纯依赖」误判成用户版本。</para>
    /// </summary>
    private static bool HasOwnContent(string versionDir)
    {
        foreach (var marker in new[] { "mods", "saves", "resourcepacks", "shaderpacks" })
        {
            try
            {
                var path = Path.Combine(versionDir, marker);
                if (Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any())
                    return true;
            }
            catch
            {
                // 读不到按没有处理
            }
        }
        return false;
    }

    private static string? ReadInheritsFrom(string versionJson)
    {
        try
        {
            using var stream = File.OpenRead(versionJson);
            using var doc = JsonDocument.Parse(stream);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            if (!doc.RootElement.TryGetProperty("inheritsFrom", out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString()?.Trim() : null;
        }
        catch
        {
            return null;
        }
    }
}
