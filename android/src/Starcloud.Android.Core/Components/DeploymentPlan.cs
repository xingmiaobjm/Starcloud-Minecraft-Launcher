// 部署计划：把「清单 + 已装状态」折算成待下载清单，供 M2 组件下载器消费。
// 目录约定：components/<名称>/<版本>/，部署完成后写入 .starcloud-ok 标记
// （下载、校验、解压三步全部成功才算就位）。

namespace Starcloud.Android.Core.Components;

/// <summary>单个组件的下载项：缓存路径与部署目录都已折算成绝对路径</summary>
public sealed record ComponentDownloadItem(
    string Name,
    string Version,
    string Url,
    string Sha256,
    ArchiveKind Archive,
    string CachePath,
    string DeployDir);

public sealed record DeploymentPlan(
    IReadOnlyList<ComponentDownloadItem> Pending,
    IReadOnlyList<ComponentDownloadItem> AlreadyInstalled)
{
    /// <summary>全部必需组件都已就位时为 true（无需任何下载）</summary>
    public bool IsComplete => Pending.Count == 0;
}

public static class DeploymentPlanner
{
    /// <summary>部署完成标记文件：解压成功后写入，其存在即代表该组件版本可用</summary>
    public const string OkMarker = ".starcloud-ok";

    public static string ComponentDir(string componentsRoot, string name, string version) =>
        Path.Combine(componentsRoot, name, version);

    /// <summary>下载缓存路径：cacheRoot/components/&lt;名称&gt;-&lt;版本&gt;&lt;扩展名&gt;</summary>
    public static string CachePath(string cacheRoot, ComponentEntry entry)
    {
        var ext = entry.Archive switch
        {
            ArchiveKind.TarXz => ".tar.xz",
            ArchiveKind.Zip => ".zip",
            _ => ".bin",
        };
        return Path.Combine(cacheRoot, "components", $"{entry.Name}-{entry.Version}{ext}");
    }

    /// <summary>组件版本目录存在且含完成标记，即视为已部署</summary>
    public static bool IsDeployed(string componentsRoot, ComponentEntry entry)
    {
        var dir = ComponentDir(componentsRoot, entry.Name, entry.Version);
        return Directory.Exists(dir) && File.Exists(Path.Combine(dir, OkMarker));
    }

    /// <summary>为必需组件生成部署计划；清单缺组件直接抛异常（Require 语义）</summary>
    public static DeploymentPlan Plan(ComponentManifest manifest, string componentsRoot,
        string cacheRoot, IEnumerable<string> requiredNames)
    {
        var pending = new List<ComponentDownloadItem>();
        var installed = new List<ComponentDownloadItem>();
        foreach (var name in requiredNames)
        {
            var entry = manifest.Require(name);
            var item = new ComponentDownloadItem(
                entry.Name, entry.Version, entry.Url, entry.Sha256, entry.Archive,
                CachePath(cacheRoot, entry),
                ComponentDir(componentsRoot, entry.Name, entry.Version));
            if (IsDeployed(componentsRoot, entry)) installed.Add(item);
            else pending.Add(item);
        }
        return new DeploymentPlan(pending, installed);
    }
}
