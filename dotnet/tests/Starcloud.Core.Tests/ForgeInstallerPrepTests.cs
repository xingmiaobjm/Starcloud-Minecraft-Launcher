// Forge/NeoForge 安装前置：官方安装器要求游戏目录里有 launcher_profiles.json，
// 缺了会直接报 “There is no minecraft launcher profile...” 并退出码 1。
// PCL/HMCL 都在跑安装器前自动补这个文件，这里钉住同样的行为。

using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class ForgeInstallerPrepTests
{
    [Fact]
    public void 缺少时创建合法的launcher_profiles()
    {
        var root = TempDir();
        try
        {
            var created = ForgeInstallerService.EnsureLauncherProfiles(root);
            var path = Path.Combine(root, "launcher_profiles.json");

            Assert.True(created);
            Assert.True(File.Exists(path));

            var bytes = File.ReadAllBytes(path);
            Assert.False(bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF,
                "launcher_profiles.json 不能带 UTF-8 BOM（Gson 解析会失败）");

            using var doc = JsonDocument.Parse(bytes);
            Assert.Equal(JsonValueKind.Object, doc.RootElement.GetProperty("profiles").ValueKind);
            Assert.Equal(3, doc.RootElement.GetProperty("version").GetInt32());
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void 已存在时不动用户配置()
    {
        var root = TempDir();
        try
        {
            var path = Path.Combine(root, "launcher_profiles.json");
            File.WriteAllText(path, """{"profiles":{"mine":{"lastVersionId":"1.20.1"}}}""");

            var created = ForgeInstallerService.EnsureLauncherProfiles(root);

            Assert.False(created);
            Assert.Contains("\"mine\"", File.ReadAllText(path));
        }
        finally { TryDelete(root); }
    }

    [Fact]
    public void 游戏目录不存在时一并创建()
    {
        var parent = TempDir();
        var root = Path.Combine(parent, "nested", ".minecraft");
        try
        {
            Assert.True(ForgeInstallerService.EnsureLauncherProfiles(root));
            Assert.True(File.Exists(Path.Combine(root, "launcher_profiles.json")));
        }
        finally { TryDelete(parent); }
    }

    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-forge-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void TryDelete(string path)
    {
        try { if (Directory.Exists(path)) Directory.Delete(path, recursive: true); } catch { }
    }
}
