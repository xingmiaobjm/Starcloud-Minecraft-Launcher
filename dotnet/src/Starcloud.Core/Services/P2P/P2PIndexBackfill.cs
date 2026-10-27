// 存量索引回填：把已经装好的游戏文件登记进互联网 P2P 索引。
//
// 为什么需要：索引只靠「下载完成时记账」，老用户装好的几十 GB 库文件永远不会被共享，
// 互助也就没有供给。这里按已安装版本的 version json 反推文件清单——
// client jar、libraries、assets（读本地 assets/indexes/<id>.json）——
// 只做「存在且大小一致」的登记，**不计算哈希**：真正供文件前还会整文件复算 SHA1，
// 对不上就拒发，所以按 manifest 的 sha1 + size 记账是安全的。
//
// 结果：任何一次开机都会把本地已有的文件重新纳入索引，不需要重新下载。

using System.Text.Json;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services.P2P;

internal static class P2PIndexBackfill
{
    /// <summary>扫描一个 .minecraft，返回新增的索引条目数（失败一律当作 0）。</summary>
    public static int Run(string dotMinecraft, P2PFileLedger ledger)
    {
        var before = ledger.Count;
        try
        {
            var versionsDir = Path.Combine(dotMinecraft, "versions");
            if (!Directory.Exists(versionsDir)) return 0;

            foreach (var versionDir in Directory.EnumerateDirectories(versionsDir))
            {
                var id = Path.GetFileName(versionDir);
                var jsonPath = Path.Combine(versionDir, id + ".json");
                if (!File.Exists(jsonPath)) continue;

                MinecraftVersion? version;
                try
                {
                    version = JsonSerializer.Deserialize<MinecraftVersion>(File.ReadAllText(jsonPath));
                }
                catch
                {
                    continue; // 坏 json：跳过这个版本
                }
                if (version is null) continue;

                // 客户端 jar
                if (version.Downloads?.Client is { } client)
                    TryRecord(ledger, Path.Combine(versionDir, id + ".jar"), client.Sha1, client.Size);

                // 库（Mojang 的 downloads.artifact 与 Fabric 的 Maven 坐标都归一）
                foreach (var lib in version.Libraries)
                {
                    if (MavenArtifacts.Resolve(lib) is not { } artifact) continue;
                    if (string.IsNullOrEmpty(artifact.Path)) continue;
                    TryRecord(ledger,
                        Path.Combine(dotMinecraft, "libraries",
                            artifact.Path.Replace('/', Path.DirectorySeparatorChar)),
                        artifact.Sha1, artifact.Size);
                }

                // 资源对象：只在本地有资源索引时才能反推
                if (version.AssetIndex is { Id.Length: > 0 } index)
                {
                    var indexPath = Path.Combine(dotMinecraft, "assets", "indexes", index.Id + ".json");
                    if (!File.Exists(indexPath)) continue;
                    AssetIndexFile? assetIndex;
                    try
                    {
                        assetIndex = JsonSerializer.Deserialize<AssetIndexFile>(File.ReadAllText(indexPath));
                    }
                    catch
                    {
                        continue;
                    }
                    if (assetIndex is null) continue;
                    foreach (var obj in assetIndex.Objects.Values)
                    {
                        if (obj.Hash.Length < 2) continue;
                        TryRecord(ledger,
                            Path.Combine(dotMinecraft, "assets", "objects", obj.Hash[..2], obj.Hash),
                            obj.Hash, obj.Size);
                    }
                }
            }
        }
        catch
        {
            // 回填只是让索引更满：任何异常都不影响启动与下载
        }
        return Math.Max(0, ledger.Count - before);
    }

    private static void TryRecord(P2PFileLedger ledger, string path, string? sha1, long size)
    {
        if (size < P2PFileLedger.MinIndexedSize) return;   // 小文件不值得走 P2P
        if (string.IsNullOrEmpty(sha1) || sha1.Length != 40) return;
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length != size) return;
            ledger.Record(path, sha1, size);
        }
        catch
        {
            // 单个文件读不到就跳过
        }
    }
}
