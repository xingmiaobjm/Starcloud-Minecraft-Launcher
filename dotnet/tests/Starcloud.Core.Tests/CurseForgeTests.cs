// CurseForge 接入测试：响应映射、直链兜底、整合包清单（全部离线）。
//
// fixture 取自 2026-09-21 对 `api.curse.tools/v1`（官方 API 只读代理）的真实响应，
// 只裁掉了与本用例无关的字段；字段结构保持原样，所以能真实反映"官方 API 长什么样"。

using System.IO.Compression;
using System.Text;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class CurseForgeTests
{
    private const string SearchJson = """
    {
      "data": [
        {
          "id": 394468,
          "name": "Sodium",
          "slug": "sodium",
          "summary": "Modern rendering engine and client-side optimization mod for Minecraft",
          "downloadCount": 151401038,
          "dateModified": "2026-09-17T12:38:59.583Z",
          "authors": [ { "name": "jellysquid3" } ],
          "logo": {
            "thumbnailUrl": "https://media.forgecdn.net/avatars/thumbnails/394/468/256/256/635628639437463989.png",
            "url": "https://media.forgecdn.net/avatars/394/468/635628639437463989.png"
          },
          "categories": [ { "name": "Performance", "slug": "performance" } ],
          "latestFilesIndexes": [
            { "gameVersion": "1.21.1", "fileId": 8756581, "filename": "sodium-fabric-0.8.13+mc1.21.1.jar", "modLoader": 4 }
          ],
          "links": { "websiteUrl": "https://www.curseforge.com/minecraft/mc-mods/sodium" },
          "classId": 6
        }
      ],
      "pagination": { "index": 0, "pageSize": 20, "resultCount": 1, "totalCount": 44 }
    }
    """;

    private const string FilesJson = """
    {
      "data": [
        {
          "id": 8756581,
          "modId": 394468,
          "displayName": "Sodium 0.8.13 for Fabric 1.21.1",
          "fileName": "sodium-fabric-0.8.13+mc1.21.1.jar",
          "releaseType": 1,
          "fileLength": 1574609,
          "downloadUrl": "https://edge.forgecdn.net/files/8756/581/sodium-fabric-0.8.13%2bmc1.21.1.jar",
          "gameVersions": [ "1.21.1", "Fabric", "Client" ],
          "modLoaderType": 4,
          "hashes": [
            { "value": "003c114c85ca88ef3362e018deb6aca0c682d6a1", "algo": 1 },
            { "value": "9a1dd1a2d9f0b3f0a1d5c2b0bb0c1a1f", "algo": 2 }
          ],
          "dependencies": [ { "modId": 306612, "relationType": 3 }, { "modId": 308702, "relationType": 2 } ]
        },
        {
          "id": 8591793,
          "modId": 394468,
          "displayName": "Sodium 0.8.13-beta.2 for Fabric 1.21.1",
          "fileName": "sodium-fabric-0.8.13-beta.2+mc1.21.1.jar",
          "releaseType": 2,
          "fileLength": 1574596,
          "gameVersions": [ "1.21.1", "Fabric" ],
          "modLoaderType": 4,
          "hashes": [ { "value": "D01F2112698D08B052D042C68C1206AF790E660A", "algo": 1 } ]
        }
      ],
      "pagination": { "index": 0, "pageSize": 50, "resultCount": 2, "totalCount": 2 }
    }
    """;

    [Fact]
    public void 搜索结果映射到统一资源模型()
    {
        var result = CurseForgeService.MapSearchResponse(SearchJson);
        Assert.Equal(44, result.TotalHits);
        var hit = Assert.Single(result.Hits);
        Assert.Equal("394468", hit.ProjectId);
        Assert.Equal("Sodium", hit.Title);
        Assert.Equal("jellysquid3", hit.Author);
        Assert.Equal(151401038, hit.Downloads);
        Assert.Contains("thumbnails", hit.IconUrl!);
        Assert.Equal(new DateTime(2026, 9, 17, 12, 38, 59, 583, DateTimeKind.Utc),
            hit.DateModified.ToUniversalTime());
        Assert.Equal(ContentSourceIds.CurseForge, hit.Source);
        Assert.Equal(new List<string> { "fabric" }, hit.Loaders);
        Assert.Equal(new List<string> { "Performance" }, hit.DisplayCategories);
        Assert.Contains("curseforge.com", hit.WebUrl);
    }

    [Fact]
    public void 文件列表映射出直链校验与依赖关系()
    {
        var versions = CurseForgeService.MapFilesResponse("394468", FilesJson);
        Assert.Equal(2, versions.Count);

        var release = versions[0];
        Assert.Equal("8756581", release.Id);
        Assert.Equal("394468", release.ProjectId);
        Assert.Equal("release", release.VersionType);
        Assert.Equal(new List<string> { "1.21.1" }, release.GameVersions);  // 加载器名 / Client 不入版本列表
        Assert.Equal(new List<string> { "fabric" }, release.Loaders);
        Assert.Equal("sodium-fabric-0.8.13+mc1.21.1.jar", release.PrimaryFile!.Filename);
        Assert.Equal(1574609, release.PrimaryFile.Size);
        Assert.Equal("003c114c85ca88ef3362e018deb6aca0c682d6a1", release.PrimaryFile.Sha1);
        Assert.StartsWith("https://edge.forgecdn.net/", release.PrimaryFile.Url);
        Assert.Equal(ContentSourceIds.CurseForge, release.Source);
        // relationType: 3 必需 / 2 可选
        Assert.Equal("required", release.Dependencies[0].DependencyType);
        Assert.Equal("306612", release.Dependencies[0].ProjectId);
        Assert.Equal("optional", release.Dependencies[1].DependencyType);

        // beta 版本类型与 SHA1 大小写归一
        Assert.Equal("beta", versions[1].VersionType);
        Assert.Equal("d01f2112698d08b052d042c68c1206af790e660a", versions[1].PrimaryFile!.Sha1);
    }

    [Fact]
    public void CDN直链按官方分片规则拼接()
    {
        Assert.Equal("https://mediafilez.forgecdn.net/files/8756/581/sodium-fabric-0.8.13%2Bmc1.21.1.jar",
            CurseForgeService.CdnUrl("8756581", "sodium-fabric-0.8.13+mc1.21.1.jar"));
        Assert.Equal("", CurseForgeService.CdnUrl("not-a-number", "x.jar"));
    }

    [Theory]
    [InlineData("forge-47.2.0", "forge", "47.2.0")]
    [InlineData("neoforge-21.1.9", "neoforge", "21.1.9")]
    [InlineData("fabric-0.15.11", "fabric-loader", "0.15.11")]
    [InlineData("quilt-0.23.1", "quilt-loader", "0.23.1")]
    [InlineData("unknown-1.0", null, "1.0")]
    [InlineData("", null, null)]
    public void 整合包加载器标识解析(string input, string? name, string? version)
    {
        var (parsedName, parsedVersion) = CurseForgePackInstaller.ParseLoader(input);
        Assert.Equal(name, parsedName);
        Assert.Equal(version, parsedVersion);
    }

    [Fact]
    public async Task 整合包可读取清单并安全铺开overrides()
    {
        var root = NewTempDir();
        try
        {
            var zipPath = Path.Combine(root, "pack.zip");
            WritePackZip(zipPath, withTraversal: true);

            var service = NewInstaller();
            var info = service.ReadPackageInfo(zipPath);
            Assert.Equal("示例整合包", info.Name);
            Assert.Equal("1.20.1", info.GameVersion);
            Assert.Equal("neoforge-21.1.9", info.LoaderId);
            Assert.Equal(0, info.FileCount);
            Assert.True(info.OverrideFileCount >= 2);

            var target = Path.Combine(root, "game");
            var result = await service.InstallAsync(zipPath, LauncherSettings.Load(), target);

            Assert.Equal("1.20.1", result.GameVersion);
            Assert.Equal("21.1.9", result.Dependencies["neoforge"]);
            Assert.Equal("1.20.1", result.Dependencies["minecraft"]);
            Assert.True(File.Exists(Path.Combine(target, "config", "example.toml")));
            // 路径穿越条目必须被丢弃：不能落到实例目录之外
            Assert.False(File.Exists(Path.Combine(root, "evil.txt")));
            Assert.False(File.Exists(Path.Combine(target, "evil.txt")));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    // ── 测试脚手架 ───────────────────────────────────────────────────────

    private static CurseForgePackInstaller NewInstaller()
    {
        var settings = LauncherSettings.Load();
        var download = new DownloadService(() => settings);
        return new CurseForgePackInstaller(download, new CurseForgeService(download, () => settings));
    }

    // ── 真机联调（默认跳过；需要网络） ──────────────────────────────────

    [Fact]
    public async Task 真机联调_搜索_列版本_取直链_下载校验()
    {
        if (Environment.GetEnvironmentVariable("STARCLOUD_IT_CURSEFORGE") != "1") return;

        var settings = LauncherSettings.Load();
        var download = new DownloadService(() => settings);
        var cf = new CurseForgeService(download, () => settings);

        var search = await cf.SearchAsync("sodium", "mod", "fabric", "1.21.1", limit: 5);
        Assert.NotEmpty(search.Hits);
        var hit = search.Hits.First(h => h.Title.Contains("Sodium", StringComparison.OrdinalIgnoreCase));
        Assert.True(search.TotalHits > 0);

        var versions = await cf.ListVersionsAsync(hit.ProjectId, "fabric", "1.21.1");
        Assert.NotEmpty(versions);
        var file = versions[0].PrimaryFile!;
        Assert.StartsWith("http", file.Url);
        Assert.Contains("1.21.1", versions[0].GameVersions);
        Assert.Equal("fabric", versions[0].Loaders[0]);

        // 单独查同一个文件应与列表一致（整合包 manifest 走的就是这条）
        var single = await cf.GetFileAsync(hit.ProjectId, versions[0].Id);
        Assert.Equal(file.Filename, single!.PrimaryFile!.Filename);

        // 真正下载一次：校验落盘字节数与 SHA1 与官方一致
        var dir = NewTempDir();
        try
        {
            var dest = Path.Combine(dir, file.Filename);
            await download.DownloadFileAsync(file.Url, dest, file.Sha1);
            Assert.Equal(file.Size, new FileInfo(dest).Length);
            var sha1 = Convert.ToHexString(
                System.Security.Cryptography.SHA1.HashData(File.ReadAllBytes(dest))).ToLowerInvariant();
            Assert.Equal(file.Sha1, sha1);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string NewTempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "starcloud-cf-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>造一个最小 CurseForge 整合包（清单 + overrides，可带一条路径穿越条目）</summary>
    private static void WritePackZip(string path, bool withTraversal)
    {
        const string manifest = """
        {
          "minecraft": { "version": "1.20.1", "modLoaders": [ { "id": "neoforge-21.1.9", "primary": true } ] },
          "name": "示例整合包",
          "version": "1.0.0",
          "files": [],
          "overrides": "overrides"
        }
        """;
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        AddText(zip, "manifest.json", manifest);
        AddText(zip, "overrides/config/example.toml", "key = 1\n");
        AddText(zip, "overrides/options.txt", "fov:0.5\n");
        if (withTraversal) AddText(zip, "overrides/../../evil.txt", "pwned\n");
    }

    private static void AddText(ZipArchive zip, string name, string content)
    {
        var entry = zip.CreateEntry(name);
        using var stream = entry.Open();
        stream.Write(Encoding.UTF8.GetBytes(content));
    }
}
