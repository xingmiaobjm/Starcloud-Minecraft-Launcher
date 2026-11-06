// 启动参数整理与版本合并顺序：这两处错了不会报错，只会让游戏启动异常/加载错类，
// 所以用测试钉住语义（参考 PCL 实战结论后独立实现，见 LaunchArgumentSanitizer 注释）。

using Starcloud.Core.Models;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class LaunchArgumentSanitizerTests
{
    [Fact]
    public void 游戏参数重复时后值覆盖前值()
    {
        // 典型场景：版本 JSON 自带 --width 854，用户设置 1920 → 必须是 1920 生效
        var args = new[] { "--width", "854", "--width", "1920" };
        var result = LaunchArgumentSanitizer.Normalize(args, isJvm: false);
        Assert.Equal(["--width", "1920"], result);
    }

    [Fact]
    public void JVM参数同名不同值不去重()
    {
        // 键值对形式（两个 token）：JVM 允许同名不同值共存，后者不该覆盖前者
        var args = new[] { "-Xss", "1M", "-Xss", "2M" };
        var result = LaunchArgumentSanitizer.Normalize(args, isJvm: true);
        Assert.Equal(["-Xss", "1M", "-Xss", "2M"], result);
    }

    [Fact]
    public void JVM单参数_D属性不合并()
    {
        // -Dkey=value 是单个 token：同名不同值都要保留
        var args = new[] { "-Dfml.ignoreInvalidMinecraftCertificates=true",
                           "-Dfml.ignoreInvalidMinecraftCertificates=false" };
        var result = LaunchArgumentSanitizer.Normalize(args, isJvm: true);
        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void JVM参数键值完全相同才去重()
    {
        var args = new[] { "-XX:+UseG1GC", "-XX:+UseG1GC" };
        var result = LaunchArgumentSanitizer.Normalize(args, isJvm: true);
        Assert.Equal(["-XX:+UseG1GC"], result);
    }

    [Fact]
    public void tweakClass允许重复()
    {
        // OptiFine + Forge 组合依赖多个 tweakClass 同时生效
        var args = new[] { "--tweakClass", "net.minecraftforge.fml.common.launcher.FMLTweaker",
                           "--tweakClass", "optifine.OptiFineForgeTweaker" };
        var result = LaunchArgumentSanitizer.Normalize(args, isJvm: false);
        Assert.Equal(2, result.Count(a => a == "--tweakClass"));
    }

    [Fact]
    public void 负数取值不会被误当成参数()
    {
        // --xPos -50 里的 -50 是值
        var args = new[] { "--xPos", "-50", "--yPos", "-60" };
        var result = LaunchArgumentSanitizer.Normalize(args, isJvm: false);
        Assert.Equal(args, result);
    }

    [Fact]
    public void 普通参数顺序保持不变()
    {
        var args = new[] { "--username", "Steve", "--gameDir", "C:\\mc", "--fullscreen" };
        var result = LaunchArgumentSanitizer.Normalize(args, isJvm: false);
        Assert.Equal(args, result);
    }
}

public class MergeVersionsOrderTests
{
    private static MinecraftVersion Version(string id, params string[] libs) => new()
    {
        Id = id,
        Libraries = libs.Select(n => new Library { Name = n }).ToList(),
    };

    [Fact]
    public void 子版本库排在父版本之前()
    {
        // Java 8 按 -cp 顺序取第一个同名类：加载器必须能覆盖原版类
        var parent = Version("1.20.1", "net.minecraft:client:1.20.1");
        var child = Version("fabric-loader", "net.fabricmc:fabric-loader:0.15");
        var merged = InstallerService.MergeVersions(parent, child);
        Assert.Equal("net.fabricmc:fabric-loader:0.15", merged.Libraries[0].Name);
        Assert.Equal("net.minecraft:client:1.20.1", merged.Libraries[1].Name);
    }

    [Fact]
    public void 多层继承时最子层排最前()
    {
        var vanilla = Version("1.20.1", "net.minecraft:client:1.20.1");
        var forge = Version("forge", "net.minecraftforge:forge:1.0");
        var lwjgl3ify = Version("lwjgl3ify", "me.steinborn:lwjgl3ify:2.0");

        var m1 = InstallerService.MergeVersions(vanilla, forge);
        var m2 = InstallerService.MergeVersions(m1, lwjgl3ify);

        Assert.Equal("me.steinborn:lwjgl3ify:2.0", m2.Libraries[0].Name);
        Assert.Equal("net.minecraftforge:forge:1.0", m2.Libraries[1].Name);
        Assert.Equal("net.minecraft:client:1.20.1", m2.Libraries[2].Name);
    }

    [Fact]
    public void 子版本没有主类时沿用父版本()
    {
        var parent = Version("1.20.1");
        parent.MainClass = "net.minecraft.client.main.Main";
        var child = Version("fabric-loader");
        child.MainClass = "";
        var merged = InstallerService.MergeVersions(parent, child);
        Assert.Equal("net.minecraft.client.main.Main", merged.MainClass);
    }
}
