// 组件清单解析测试：字段校验 / URL 协议 / 哈希格式 / 同名去重 / 查找语义

using Starcloud.Android.Core.Components;
using Xunit;

namespace Starcloud.Android.Core.Tests;

public class ComponentManifestTests
{
    // 合法 SHA256：64 位十六进制
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private static string ValidManifestJson => """
        // 组件清单（允许注释）
        {
            "manifest_version": 1,
            "components": [
                {
                    "name": "jre21",
                    "version": "21.0.4",
                    "url": "https://example.com/jre21.tar.xz",
                    "sha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
                    "archive": "TarXz",
                    "size_bytes": 48000000,
                    "license": "GPL-2.0-with-classpath-exception",
                    "license_url": "https://example.com/license",
                },
            ],
        }
        """;

    [Fact]
    public void 合法清单解析成功且容忍注释与尾逗号()
    {
        var manifest = ComponentManifest.Parse(ValidManifestJson);

        Assert.Equal(1, manifest.ManifestVersion);
        var entry = Assert.Single(manifest.Components);
        Assert.Equal("jre21", entry.Name);
        Assert.Equal("21.0.4", entry.Version);
        Assert.Equal("https://example.com/jre21.tar.xz", entry.Url);
        Assert.Equal(Sha, entry.Sha256);
        Assert.Equal(ArchiveKind.TarXz, entry.Archive);
        Assert.Equal(48000000L, entry.SizeBytes);
        Assert.Equal("GPL-2.0-with-classpath-exception", entry.License);
    }

    [Fact]
    public void 空组件清单抛出异常()
    {
        var json = """{ "manifest_version": 1, "components": [] }""";

        var ex = Assert.Throws<InvalidOperationException>(() => ComponentManifest.Parse(json));
        Assert.Contains("组件清单为空", ex.Message);
    }

    [Fact]
    public void 缺少名称的条目抛出异常()
    {
        var json = $$"""
            {
                "manifest_version": 1,
                "components": [
                    { "name": "", "version": "1.0", "url": "https://example.com/a.zip",
                      "sha256": "{{Sha}}", "archive": "Zip", "license": "MIT" }
                ]
            }
            """;

        Assert.Throws<InvalidOperationException>(() => ComponentManifest.Parse(json));
    }

    [Fact]
    public void 缺少版本的条目抛出异常()
    {
        var json = $$"""
            {
                "manifest_version": 1,
                "components": [
                    { "name": "gl4es", "version": "", "url": "https://example.com/a.zip",
                      "sha256": "{{Sha}}", "archive": "Zip", "license": "MIT" }
                ]
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => ComponentManifest.Parse(json));
        Assert.Contains("gl4es", ex.Message);
    }

    [Theory]
    [InlineData("ftp://example.com/a.zip")]
    [InlineData("file:///tmp/a.zip")]
    [InlineData("not-a-url")]
    [InlineData("")]
    public void 非下载地址抛出异常(string url)
    {
        var json = $$"""
            {
                "manifest_version": 1,
                "components": [
                    { "name": "lwjgl3", "version": "3.3.3", "url": "{{url}}",
                      "sha256": "{{Sha}}", "archive": "Zip", "license": "BSD" }
                ]
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => ComponentManifest.Parse(json));
        Assert.Contains("下载地址无效", ex.Message);
    }

    [Fact]
    public void 内置资产地址解析成功()
    {
        // asset: 为 APK 内置组件包伪协议，由宿主 IComponentArchiveSource 提供流
        var json = $$"""
            {
                "manifest_version": 1,
                "components": [
                    { "name": "jre21", "version": "21.0.4", "url": "asset://components/jre21.tar.xz",
                      "sha256": "{{Sha}}", "archive": "TarXz", "license": "GPL-2.0-with-classpath-exception" }
                ]
            }
            """;

        var manifest = ComponentManifest.Parse(json);
        Assert.Equal("asset://components/jre21.tar.xz", manifest.Require("jre21").Url);
    }

    [Theory]
    [InlineData("abc")]                                            // 过短
    [InlineData("0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdeg")] // 含 g
    [InlineData("")]                                               // 空
    public void 哈希格式非法抛出异常(string sha)
    {
        var json = $$"""
            {
                "manifest_version": 1,
                "components": [
                    { "name": "openal", "version": "1.23", "url": "https://example.com/a.zip",
                      "sha256": "{{sha}}", "archive": "Zip", "license": "LGPL" }
                ]
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => ComponentManifest.Parse(json));
        Assert.Contains("SHA256", ex.Message);
    }

    [Fact]
    public void 缺少许可证的条目抛出异常()
    {
        var json = $$"""
            {
                "manifest_version": 1,
                "components": [
                    { "name": "mesa-zink", "version": "23.0", "url": "https://example.com/a.tar.xz",
                      "sha256": "{{Sha}}", "archive": "TarXz", "license": " " }
                ]
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => ComponentManifest.Parse(json));
        Assert.Contains("许可证", ex.Message);
    }

    [Fact]
    public void 同名条目忽略大小写判重抛出异常()
    {
        var json = $$"""
            {
                "manifest_version": 1,
                "components": [
                    { "name": "glfw-stub", "version": "1.0", "url": "https://example.com/a.zip",
                      "sha256": "{{Sha}}", "archive": "Zip", "license": "MIT" },
                    { "name": "GLFW-Stub", "version": "1.1", "url": "https://example.com/b.zip",
                      "sha256": "{{Sha}}", "archive": "Zip", "license": "MIT" }
                ]
            }
            """;

        var ex = Assert.Throws<InvalidOperationException>(() => ComponentManifest.Parse(json));
        Assert.Contains("重复条目", ex.Message);
    }

    [Fact]
    public void 查找忽略大小写且未命中返回空()
    {
        var manifest = ComponentManifest.Parse(ValidManifestJson);

        Assert.NotNull(manifest.Find("JRE21"));
        Assert.Null(manifest.Find("jre17"));
    }

    [Fact]
    public void 必需组件缺失时抛出异常并提示更新清单()
    {
        var manifest = ComponentManifest.Parse(ValidManifestJson);

        var ex = Assert.Throws<InvalidOperationException>(() => manifest.Require("jre8"));
        Assert.Contains("组件清单缺少 jre8", ex.Message);
    }

    [Fact]
    public void 格式错误的清单抛出解析失败异常()
    {
        var ex = Assert.Throws<InvalidOperationException>(
            () => ComponentManifest.Parse("{ not json "));
        Assert.Contains("组件清单解析失败", ex.Message);
    }
}
