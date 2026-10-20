// 组件部署器：缓存 → 哈希校验 → 解压 → 写完成标记 的完整链路。
// 获取来源二选一：asset: 伪协议走宿主注入的 IComponentArchiveSource（APK 内置资产），
// http(s) 走桌面 DownloadService（扩展路径，断点续传）；SHA256 校验由本类统一负责。

using System.IO.Compression;
using System.Security.Cryptography;
using SharpCompress.Compressors.Xz;
using SharpCompress.Readers;
using SharpCompress.Readers.Tar;
using Starcloud.Core.Services;

namespace Starcloud.Android.Core.Components;

/// <summary>asset: 协议归档来源：宿主把 APK 内置资产以流形式提供；测试注入内存流</summary>
public interface IComponentArchiveSource
{
    /// <summary>打开 url 指向的归档流；资产缺失返回 null</summary>
    Stream? OpenArchive(string url);
}

public sealed class ComponentDeployer
{
    private readonly string _cacheRoot;
    private readonly IComponentArchiveSource? _assetSource;
    private readonly DownloadService? _downloads;

    public ComponentDeployer(string componentsRoot, string cacheRoot,
        IComponentArchiveSource? assetSource = null, DownloadService? downloads = null)
    {
        ComponentsRoot = componentsRoot;
        _cacheRoot = cacheRoot;
        _assetSource = assetSource;
        _downloads = downloads;
    }

    public string ComponentsRoot { get; }

    /// <summary>按计划顺序部署全部待装组件</summary>
    public async Task DeployAllAsync(DeploymentPlan plan, CancellationToken ct = default)
    {
        foreach (var item in plan.Pending)
            await DeployAsync(item, ct);
    }

    /// <summary>部署单个组件：已就位直接跳过；否则取包、验哈希、解压、写标记</summary>
    public async Task DeployAsync(ComponentDownloadItem item, CancellationToken ct = default)
    {
        if (Directory.Exists(item.DeployDir) &&
            File.Exists(Path.Combine(item.DeployDir, DeploymentPlanner.OkMarker)))
            return;

        var cachePath = await EnsureCacheAsync(item, ct);
        ExtractToDeployDir(item, cachePath);
    }

    /// <summary>确保缓存中存在哈希合法的组件包，返回缓存路径</summary>
    private async Task<string> EnsureCacheAsync(ComponentDownloadItem item, CancellationToken ct)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.CachePath)!);

        if (File.Exists(item.CachePath))
        {
            if (await VerifySha256Async(item.CachePath, item.Sha256, ct))
                return item.CachePath;
            // 缓存损坏（半截下载/落盘错误）：删除后重新获取
            File.Delete(item.CachePath);
        }

        if (item.Url.StartsWith("asset:", StringComparison.OrdinalIgnoreCase))
        {
            if (_assetSource is null)
                throw new InvalidOperationException($"组件 {item.Name} 使用内置资产来源，但宿主未提供 asset 解析器");
            await using var src = _assetSource.OpenArchive(item.Url)
                ?? throw new InvalidOperationException($"内置组件包缺失：{item.Url}（组件 {item.Name}）");
            await using (var dst = File.Create(item.CachePath))
                await src.CopyToAsync(dst, ct);
        }
        else
        {
            if (_downloads is null)
                throw new InvalidOperationException($"组件 {item.Name} 需要网络下载，但下载服务不可用");
            // DownloadService 仅支持 SHA1 校验，SHA256 交给部署器统一自验
            await _downloads.DownloadFileAsync(item.Url, item.CachePath, ct: ct);
        }

        if (!await VerifySha256Async(item.CachePath, item.Sha256, ct))
        {
            File.Delete(item.CachePath);
            throw new InvalidOperationException($"组件 {item.Name} 哈希校验失败：包内容与清单 SHA256 不符，已删除缓存");
        }
        return item.CachePath;
    }

    private static async Task<bool> VerifySha256Async(string file, string expectedSha256, CancellationToken ct)
    {
        await using var fs = File.OpenRead(file);
        var actual = Convert.ToHexString(await SHA256.HashDataAsync(fs, ct));
        return actual.Equals(expectedSha256, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>清空目标目录后解压，成功写入完成标记（内容为版本号）</summary>
    private static void ExtractToDeployDir(ComponentDownloadItem item, string cachePath)
    {
        var dir = item.DeployDir;
        if (Directory.Exists(dir))
            Directory.Delete(dir, recursive: true);
        Directory.CreateDirectory(dir);

        switch (item.Archive)
        {
            case ArchiveKind.Zip:
                ZipFile.ExtractToDirectory(cachePath, dir, overwriteFiles: true);
                break;
            case ArchiveKind.TarXz:
                ExtractTarXz(cachePath, dir);
                break;
            case ArchiveKind.None:
                File.Copy(cachePath, Path.Combine(dir, item.Name + ".bin"), overwrite: true);
                break;
            default:
                throw new InvalidOperationException($"组件 {item.Name} 的归档类型不受支持：{item.Archive}");
        }

        File.WriteAllText(Path.Combine(dir, DeploymentPlanner.OkMarker), item.Version);
    }

    /// <summary>解压 tar.xz：拒绝路径穿越；符号链接尽力创建、失败则降级为复制目标内容</summary>
    private static void ExtractTarXz(string cachePath, string destDir)
    {
        // SharpCompress 1.0.0 的 TarReader 无法直接读非 seekable 的 XZ 流（读 512 字节头时误判 EOF），
        // 故先把 XZ 解压为临时 tar 文件，再以 seekable 流遍历条目
        var tarPath = Path.Combine(Path.GetTempPath(), "starcloud-extract-" + Guid.NewGuid().ToString("N") + ".tar");
        try
        {
            using (var xzIn = File.OpenRead(cachePath))
            using (var xz = new XZStream(xzIn))
            using (var tarOut = File.Create(tarPath))
                xz.CopyTo(tarOut);

            using var tarFs = File.OpenRead(tarPath);
            using var reader = TarReader.Open(tarFs, new ReaderOptions());
            while (reader.MoveToNextEntry())
            {
                var entry = reader.Entry;
                var key = entry.Key ?? "";
                if (key.Length == 0 || key.EndsWith('/'))
                {
                    var dirTarget = SafeJoin(destDir, key);
                    if (dirTarget is not null)
                        Directory.CreateDirectory(dirTarget);
                    continue;
                }

                // 链接条目：LinkTarget 非空即符号/硬链接
                if (!string.IsNullOrEmpty(entry.LinkTarget))
                {
                    ExtractLink(destDir, key, entry.LinkTarget, destDir);
                    continue;
                }
                if (entry.IsDirectory)
                {
                    var dirTarget = SafeJoin(destDir, key);
                    if (dirTarget is not null)
                        Directory.CreateDirectory(dirTarget);
                    continue;
                }

                // 文件条目必须有有效路径；归根段说明包结构异常，直接报错
                var target = SafeJoin(destDir, key)
                    ?? throw new InvalidOperationException($"组件包内出现空路径：{key}");
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                using (var output = File.Create(target))
                    reader.WriteEntryTo(output);
            }
        }
        finally
        {
            try { File.Delete(tarPath); } catch (IOException) { /* 临时文件清理失败不影响结果 */ }
        }
    }

    /// <summary>符号/硬链接落地：能建符号链接就建，否则复制已解压的目标内容（JRE 内 libjsig 等链接靠此兜底）</summary>
    private static void ExtractLink(string destDir, string key, string? linkTarget, string rootForResolve)
    {
        if (string.IsNullOrEmpty(linkTarget))
            return;
        var target = SafeJoin(destDir, key);
        if (target is null)
            return;
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        var source = linkTarget.StartsWith('/')
            ? Path.GetFullPath(Path.Combine(rootForResolve, linkTarget.TrimStart('/')))
            : Path.GetFullPath(Path.Combine(Path.GetDirectoryName(target)!, linkTarget));

        try
        {
            if (Directory.Exists(source) && !File.Exists(source))
                Directory.CreateSymbolicLink(target, linkTarget);
            else
                File.CreateSymbolicLink(target, linkTarget);
            return;
        }
        catch (IOException)
        {
            // Windows 无开发者模式等场景：降级为复制
        }
        catch (UnauthorizedAccessException)
        {
            // 同上
        }

        if (File.Exists(source))
            File.Copy(source, target, overwrite: true);
    }

    /// <summary>归档内路径 → 安全落地路径：拒绝 .. 穿越与盘符，忽略 . 段；
    /// 归根段（"/"、"../." 过滤后无有效段）返回 null，由调用方决定跳过或报错</summary>
    private static string? SafeJoin(string destDir, string key)
    {
        var normalized = key.Replace('\\', '/');
        var parts = new List<string>();
        foreach (var seg in normalized.Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            if (seg == ".") continue;
            if (seg == ".." || seg.Contains(':') || seg == "~")
                throw new InvalidOperationException($"组件包内出现非法路径：{key}");
            parts.Add(seg);
        }
        if (parts.Count == 0)
            return null;
        return Path.GetFullPath(Path.Combine(destDir, Path.Combine([.. parts])));
    }
}
