// Maven 坐标解析测试：Fabric/Quilt 的库只给 name + url，必须能拼出和下载到
// libraries 下的真实路径，否则 classpath 缺 loader，启动报“找不到主类”。

using Starcloud.Core.Models;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class MavenArtifactsTests
{
    [Theory]
    [InlineData("net.fabricmc:fabric-loader:0.19.5",
        "net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.3:natives-windows",
        "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar")]
    [InlineData("com.example:pack:1.0@zip",
        "com/example/pack/1.0/pack-1.0.zip")]
    public void 坐标路径与预期一致(string name, string expected)
    {
        Assert.True(MavenArtifacts.TryBuildPath(name, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("foo:bar")]
    [InlineData("a:b:c@")]
    [InlineData(":b:c")]
    [InlineData("a::c")]
    public void 非法坐标返回false(string name) =>
        Assert.False(MavenArtifacts.TryBuildPath(name, out _));

    [Fact]
    public void Mojang库优先使用downloads_artifact()
    {
        var lib = new Library
        {
            Name = "com.example:demo:1.0",
            Url = "https://maven.example.com/",
            Downloads = new LibraryDownloads
            {
                Artifact = new DownloadableFile
                {
                    Url = "https://cdn.example.com/demo-1.0.jar",
                    Path = "com/example/demo/1.0/demo-1.0.jar",
                    Sha1 = "abc",
                    Size = 42,
                },
            },
        };

        var resolved = MavenArtifacts.Resolve(lib);
        Assert.NotNull(resolved);
        Assert.Equal("https://cdn.example.com/demo-1.0.jar", resolved!.Url);
        Assert.Equal(42, resolved.Size);
    }

    [Fact]
    public void Maven回退拼出下载地址并保留校验信息()
    {
        var lib = new Library
        {
            Name = "net.fabricmc:fabric-loader:0.19.5",
            Url = "https://maven.fabricmc.net/",
            Sha1 = "deadbeef",
            Size = 1024,
        };

        var resolved = MavenArtifacts.Resolve(lib);
        Assert.NotNull(resolved);
        Assert.Equal(
            "https://maven.fabricmc.net/net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar",
            resolved!.Url);
        Assert.Equal("net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar", resolved.Path);
        Assert.Equal("deadbeef", resolved.Sha1);
        Assert.Equal(1024, resolved.Size);
    }

    [Fact]
    public void 缺仓库根地址时无法解析()
    {
        var lib = new Library { Name = "net.fabricmc:fabric-loader:0.19.5", Url = "" };
        Assert.Null(MavenArtifacts.Resolve(lib));
    }
}
