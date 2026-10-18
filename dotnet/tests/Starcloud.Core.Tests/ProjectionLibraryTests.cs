// 投影库：扫描全局 + 各实例 schematics，跨版本复制/移动/删除（重名不覆盖）。

using Xunit;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Core.Tests;

public sealed class ProjectionLibraryTests
{
    [Fact]
    public void 投影库_扫描复制移动删除()
    {
        var root = Path.Combine(Path.GetTempPath(), "sc-proj-" + Guid.NewGuid().ToString("N"));
        var dotMc = Path.Combine(root, ".minecraft");
        var globalDir = Path.Combine(dotMc, "schematics");
        var versionDir = Path.Combine(dotMc, "versions", "v1");
        Directory.CreateDirectory(globalDir);
        Directory.CreateDirectory(Path.Combine(versionDir, "schematics"));
        File.WriteAllText(Path.Combine(versionDir, "v1.json"), "{}");

        try
        {
            var src = Path.Combine(globalDir, "a.litematic");
            File.WriteAllText(src, "x");

            var settings = new LauncherSettings { DotMinecraft = dotMc, VersionIsolated = true };
            var library = new ProjectionLibrary(() => settings);

            var scan = library.Scan();
            Assert.Contains(scan, f => f.FileName == "a.litematic" && f.VersionId is null);
            Assert.Contains("v1", library.ListVersions());
            Assert.Equal(Path.Combine(versionDir, "schematics"), library.TargetDirectory("v1"));
            Assert.Equal(globalDir, library.TargetDirectory(null));

            var copied = library.CopyTo(src, "v1");
            Assert.True(File.Exists(copied));
            Assert.Contains(library.Scan(), f => f.VersionId == "v1" && f.FileName == "a.litematic");

            // 重名不覆盖：第二次复制落到 -2
            var second = library.CopyTo(src, "v1");
            Assert.NotEqual(copied, second);
            Assert.Equal("a-2.litematic", Path.GetFileName(second));

            // 移动：源文件消失
            var moved = library.MoveTo(src, "v1");
            Assert.False(File.Exists(src));
            Assert.True(File.Exists(moved));

            library.Delete(moved);
            Assert.False(File.Exists(moved));
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* 临时目录清理失败不影响结论 */ }
        }
    }
}
