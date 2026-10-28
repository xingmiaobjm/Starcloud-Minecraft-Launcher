// 启动参数生成回归：库去重、GC 分档、模块化参数、BootstrapLauncher ignoreList。
//
// 这些都是对照 PCL / HMCL / PrismLauncher 的公开实现补上的：
//   · 库按 artifactId 去重（HMCL uniqueLibraries / PrismLauncher applyLibrary）
//   · GC 按 Java 版本分档（PCL McLaunchArgumentsJVM 的 SetupType）
//   · 模块化 --add-opens / --add-exports（PrismLauncher / PCL）
//   · BootstrapLauncher ignoreList 修复（HMCL rewriteUnsafeBootstrapLauncherIgnoreList）

using Starcloud.Core.Models;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class GameLaunchArgsRegressionTests
{
    private static Library Lib(string name, params OsRule[] rules) => new()
    {
        Name = name,
        Rules = rules.Length > 0 ? rules.ToList() : null,
    };

    // ── 库去重 ──

    [Fact]
    public void 同一artifact的不同版本只保留最高版本()
    {
        var libs = new List<Library>
        {
            Lib("org.ow2.asm:asm:9.0"),
            Lib("org.ow2.asm:asm:9.6"),
            Lib("org.ow2.asm:asm:5.0"),
        };

        var result = GameLauncher.DedupeLibrariesByArtifact(libs);

        Assert.Single(result);
        Assert.Equal("org.ow2.asm:asm:9.6", result[0].Name);
    }

    [Fact]
    public void 规则不同的同artifact两条都保留()
    {
        // 平台特化变体：规则不同就不能只留一条（另一条是给别的平台用的）
        var libs = new List<Library>
        {
            Lib("net.java.dev.jna:jna:5.0", new OsRule { Os = new OsSpec { Name = "windows" }, Action = "allow" }),
            Lib("net.java.dev.jna:jna:5.14", new OsRule { Os = new OsSpec { Name = "osx" }, Action = "allow" }),
        };

        var result = GameLauncher.DedupeLibrariesByArtifact(libs);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void 非Maven坐标的库原样保留()
    {
        var libs = new List<Library>
        {
            Lib("com.example:plain:1.0"),
            Lib("native-library"),
        };

        var result = GameLauncher.DedupeLibrariesByArtifact(libs);

        Assert.Equal(2, result.Count);
    }

    // ── GC 分档 ──

    [Theory]
    [InlineData(8, "-XX:+UseG1GC")]
    [InlineData(14, "-XX:+UseG1GC")]
    [InlineData(17, "-XX:+UseZGC")]
    [InlineData(21, "-XX:+UseZGC")]
    [InlineData(24, "-XX:+UseZGC")]
    public void GC参数按Java版本分档(int javaMajor, string expected)
    {
        var args = GameLauncher.BuiltinGcArgs(javaMajor);

        Assert.Contains(expected, args);
        // G1GC 与 ZGC 不能同时出现
        Assert.False(args.Contains("-XX:+UseG1GC") && args.Contains("-XX:+UseZGC"));
    }

    [Fact]
    public void Java15以下用G1GC()
    {
        Assert.Contains("-XX:+UseG1GC", GameLauncher.BuiltinGcArgs(14));
        Assert.Contains("-XX:+UseG1GC", GameLauncher.BuiltinGcArgs(8));
    }

    [Fact]
    public void Java21以上用分代ZGC()
    {
        Assert.Contains("-XX:+ZGenerational", GameLauncher.BuiltinGcArgs(21));
        Assert.DoesNotContain("-XX:+ZGenerational", GameLauncher.BuiltinGcArgs(17));
    }

    // ── BootstrapLauncher ignoreList ──

    [Fact]
    public void ignoreList补上主jar文件名()
    {
        var args = new List<string> { "-DignoreList=.,fabric-loader", "-Xmx2G" };

        GameLauncher.FixBootstrapIgnoreList(args, "26.3-4.jar");

        Assert.StartsWith("-DignoreList=", args[0]);
        Assert.Contains("26.3-4.jar", args[0]);
        Assert.Contains("fabric-loader", args[0]);
        // 其它参数不动
        Assert.Equal("-Xmx2G", args[1]);
    }

    [Fact]
    public void 没有ignoreList参数时不新增()
    {
        var args = new List<string> { "-Xmx2G" };

        GameLauncher.FixBootstrapIgnoreList(args, "26.3-4.jar");

        Assert.Single(args);
    }

    [Fact]
    public void ignoreList已有主jar名时不重复添加()
    {
        var args = new List<string> { "-DignoreList=26.3-4.jar,asm" };

        GameLauncher.FixBootstrapIgnoreList(args, "26.3-4.jar");

        // 取 -DignoreList= 后面的部分再按逗号拆
        var value = args[0]["-DignoreList=".Length..];
        Assert.Equal(1, value.Split(',').Count(t => t == "26.3-4.jar"));
        Assert.Equal(2, value.Split(',').Length);
    }
}
