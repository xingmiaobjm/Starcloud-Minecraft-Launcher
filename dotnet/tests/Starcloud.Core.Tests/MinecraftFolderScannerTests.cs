// 硬盘 .minecraft 扫描：只测可离线验证的判定与已知位置，真实磁盘遍历不测。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class MinecraftFolderScannerTests : IDisposable
{
    private readonly List<string> _dirs = [];

    private string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-scan-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        _dirs.Add(dir);
        return dir;
    }

    public void Dispose()
    {
        foreach (var d in _dirs) try { Directory.Delete(d, recursive: true); } catch { }
    }

    [Fact]
    public void 有versions目录才算游戏目录()
    {
        var dir = TempDir();
        Assert.False(MinecraftFolderScanner.LooksLikeMinecraft(dir));      // 空目录不算
        Directory.CreateDirectory(Path.Combine(dir, "versions"));
        Assert.True(MinecraftFolderScanner.LooksLikeMinecraft(dir));
    }

    [Fact]
    public void 只有launcher_profiles或options也算()
    {
        var a = TempDir();
        File.WriteAllText(Path.Combine(a, "launcher_profiles.json"), "{}");
        Assert.True(MinecraftFolderScanner.LooksLikeMinecraft(a));

        var b = TempDir();
        File.WriteAllText(Path.Combine(b, "options.txt"), "fov:0.5");
        Assert.True(MinecraftFolderScanner.LooksLikeMinecraft(b));
    }

    [Fact]
    public void 不存在的目录不算()
    {
        Assert.False(MinecraftFolderScanner.LooksLikeMinecraft(Path.Combine(Path.GetTempPath(), "sc-none-" + Guid.NewGuid().ToString("N"))));
    }

    [Fact]
    public void 版本数按versions下子目录计()
    {
        var dir = TempDir();
        Directory.CreateDirectory(Path.Combine(dir, "versions", "1.20.1"));
        Directory.CreateDirectory(Path.Combine(dir, "versions", "1.21"));
        File.WriteAllText(Path.Combine(dir, "versions", "readme.txt"), "x"); // 文件不算
        Assert.Equal(2, MinecraftFolderScanner.CountVersions(dir));
    }

    [Fact]
    public void 候选的显示名带版本数且ToString直接可用()
    {
        var withVersions = new MinecraftFolderCandidate(@"D:\Games\.minecraft", 12);
        Assert.Contains("12 个版本", withVersions.Display);
        Assert.Equal(withVersions.Display, withVersions.ToString());   // ListView 直接显示这一行

        var noVersions = new MinecraftFolderCandidate(@"E:\.minecraft", 0);
        Assert.Equal(@"E:\.minecraft", noVersions.ToString());
    }

    [Fact]
    public void 已知位置包含默认游戏目录()
    {
        Assert.Contains(MinecraftFolderScanner.KnownLocations(), p =>
            string.Equals(p, PlatformInfo.DefaultDotMinecraft, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task 扫描可随时取消()
    {
        // 不在测试里全盘遍历（CI 上既慢又不可控）：只验证取消语义
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => MinecraftFolderScanner.ScanAsync(null, null, cts.Token));
    }
}
