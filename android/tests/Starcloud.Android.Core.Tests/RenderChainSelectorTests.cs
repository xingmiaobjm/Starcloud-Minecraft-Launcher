// 渲染链选择测试：自动分档 / 强制偏好的可行性与回退 / 组件与库名映射

using Starcloud.Android.Core.Launch;
using Xunit;

namespace Starcloud.Android.Core.Tests;

public class RenderChainSelectorTests
{
    [Fact]
    public void 自动模式下老版本走gl4es新版本走zink()
    {
        Assert.Equal(RenderBackend.Gl4es, RenderChainSelector.Select("1.12.2", RenderChainPreference.Auto));
        Assert.Equal(RenderBackend.Gl4es, RenderChainSelector.Select("1.8.9", RenderChainPreference.Auto));
        Assert.Equal(RenderBackend.Zink, RenderChainSelector.Select("1.13", RenderChainPreference.Auto));
        Assert.Equal(RenderBackend.Zink, RenderChainSelector.Select("1.20.4", RenderChainPreference.Auto));
        // 快照无 minor：按 Zink（GL 3.x 语义，覆盖最新版本要求）
        Assert.Equal(RenderBackend.Zink, RenderChainSelector.Select("24w14a", RenderChainPreference.Auto));
    }

    [Fact]
    public void 强制zink无条件生效()
    {
        Assert.Equal(RenderBackend.Zink, RenderChainSelector.Select("1.8.9", RenderChainPreference.Zink));
        Assert.Equal(RenderBackend.Zink, RenderChainSelector.Select("1.12.2", RenderChainPreference.Zink));
        Assert.Equal(RenderBackend.Zink, RenderChainSelector.Select("1.20.4", RenderChainPreference.Zink));
    }

    [Fact]
    public void 强制gl4es仅对不高于116的版本生效()
    {
        Assert.Equal(RenderBackend.Gl4es, RenderChainSelector.Select("1.12.2", RenderChainPreference.Gl4es));
        Assert.Equal(RenderBackend.Gl4es, RenderChainSelector.Select("1.16.5", RenderChainPreference.Gl4es));
        // 1.17+ 要求 GL 3.2，gl4es 兜不住：必败配置回退 Zink
        Assert.Equal(RenderBackend.Zink, RenderChainSelector.Select("1.17.1", RenderChainPreference.Gl4es));
        Assert.Equal(RenderBackend.Zink, RenderChainSelector.Select("1.21", RenderChainPreference.Gl4es));
    }

    [Fact]
    public void 后端映射组件名与转译层库名()
    {
        Assert.Equal("mesa-zink", RenderChainSelector.ComponentNameOf(RenderBackend.Zink));
        Assert.Equal("gl4es", RenderChainSelector.ComponentNameOf(RenderBackend.Gl4es));
        Assert.Equal("libOSMesa_81.so", RenderChainSelector.OpenGLLibraryName(RenderBackend.Zink));
        Assert.Equal("libgl4es_114.so", RenderChainSelector.OpenGLLibraryName(RenderBackend.Gl4es));
    }
}
