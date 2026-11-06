// Java 版本上界：只看下限会让老版本（尤其是配 OptiFine 的）用新 Java 直接崩在启动阶段，
// 而崩溃信息（Unsupported class file major version）对用户毫无指向性。
// 这里的边界来自社区长期验证的兼容结论，不是某个启动器的私有逻辑。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class JavaUpperBoundTests
{
    [Fact]
    public void 一点五二及更早最高Java8()
    {
        Assert.Equal(8, JavaRequirement.MaxSupported("1.5.2", hasOptiFine: false));
        Assert.Equal(8, JavaRequirement.MaxSupported("1.4.7", hasOptiFine: false));
    }

    [Theory]
    [InlineData("1.8.9")]
    [InlineData("1.10.2")]
    [InlineData("1.11.2")]
    public void OptiFine老版本必须Java8(string version)
    {
        Assert.Equal(8, JavaRequirement.MaxSupported(version, hasOptiFine: true));
        // 没装 OptiFine 时不该限制（这些版本的加载器自己能跑新 Java 的少见但仍允许）
        Assert.Null(JavaRequirement.MaxSupported(version, hasOptiFine: false));
    }

    [Fact]
    public void OptiFine与1_12最高Java8()
    {
        Assert.Equal(8, JavaRequirement.MaxSupported("1.12.2", hasOptiFine: true));
    }

    [Fact]
    public void 新版本不设上限()
    {
        Assert.Null(JavaRequirement.MaxSupported("1.20.1", hasOptiFine: false));
        Assert.Null(JavaRequirement.MaxSupported("1.20.5", hasOptiFine: false));
        // 24w14a 这类快照不解析成 1.x，不设上限
        Assert.Null(JavaRequirement.MaxSupported("24w14a", hasOptiFine: true));
    }

    [Fact]
    public void LaunchWrapper系实例最高Java8()
    {
        // Forge ≤1.12 / LiteLoader / OptiFine 走 LaunchWrapper：它假定系统类加载器是
        // URLClassLoader，Java 9+ 会直接抛类型错误（1.12.2 + Java 17 是高频误配）
        const string lw = "net.minecraft.launchwrapper.Launch";
        Assert.Equal(8, JavaRequirement.MaxSupported("1.12.2", hasOptiFine: false, mainClass: lw));
        Assert.Equal(8, JavaRequirement.MaxSupported("1.7.10", hasOptiFine: false, mainClass: lw));

        var warn = JavaRequirement.TooNewWarning("1.12.2", false, 17, lw);
        Assert.NotNull(warn);
        Assert.Contains("LaunchWrapper", warn);

        // lwjgl3ify / Cleanroom 会换成自己的 mainClass，不该被这个限制误伤
        Assert.Null(JavaRequirement.MaxSupported("1.12.2", false,
            mainClass: "com.cleanroommc.boot.Main"));
        Assert.Null(JavaRequirement.MaxSupported("1.7.10", false,
            mainClass: "com.gtnewhorizons.retrofuturabootstrap.Main"));
    }

    [Fact]
    public void 超上限时给出可执行的提示()
    {
        var warn = JavaRequirement.TooNewWarning("1.12.2", hasOptiFine: true, actualMajor: 17);
        Assert.NotNull(warn);
        Assert.Contains("Java 8", warn);
        Assert.Contains("OptiFine", warn);

        // 在范围内不该报警
        Assert.Null(JavaRequirement.TooNewWarning("1.12.2", hasOptiFine: true, actualMajor: 8));
        Assert.Null(JavaRequirement.TooNewWarning("1.20.1", hasOptiFine: false, actualMajor: 21));
    }
}
