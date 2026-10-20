// 投影引擎插件：注册进内核后能从契约解析，且引擎能力真的能用（扫描 / 解析 / 转换判定）。
//
// 为什么单独测：引擎与管理是两条契约（IProjectionEngine / IProjectionLibrary），
// 都在内置插件里注册；如果只测了注册不测能力，"换实现"时会以为换掉了其实还在跑旧代码。

using Starcloud.Core.Kernel;
using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Kernel.Plugins;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Projection;
using Xunit;

namespace Starcloud.Core.Tests;

public sealed class ProjectionEnginePluginTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sc-proj-engine-" + Guid.NewGuid().ToString("N"));

    public ProjectionEnginePluginTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private KernelHost NewKernel()
    {
        var kernel = new KernelHost(new KernelOptions { DataRoot = _root });
        var settings = new LauncherSettings { DotMinecraft = Path.Combine(_root, ".minecraft") };
        kernel.Register(BuiltinProjectionEnginePlugin.Descriptor,
            _ => new BuiltinProjectionEnginePlugin(() => settings));
        kernel.Register(BuiltinProjectionPlugin.Descriptor,
            _ => new BuiltinProjectionPlugin(() => settings));
        return kernel;
    }

    [Fact]
    public async Task 引擎插件_启动后能按契约解析()
    {
        var kernel = NewKernel();
        await kernel.StartAsync();

        var engine = kernel.Services.Get<IProjectionEngine>();
        Assert.Equal("内置投影引擎", engine.DisplayName);

        // 两条契约互不影响：管理插件仍在
        Assert.NotNull(kernel.Services.Get<IProjectionLibrary>());
    }

    [Fact]
    public async Task 引擎插件_扫描默认投影目录且按扩展名过滤()
    {
        var dir = Path.Combine(_root, ".minecraft", "schematics");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "a.litematic"), [0]);
        File.WriteAllBytes(Path.Combine(dir, "b.schem"), [0]);
        File.WriteAllBytes(Path.Combine(dir, "ignore.txt"), [0]);

        var kernel = NewKernel();
        await kernel.StartAsync();

        var files = kernel.Services.Get<IProjectionEngine>().List();
        Assert.Equal(2, files.Count);
        Assert.Contains(files, f => f.Name == "a");
        Assert.Contains(files, f => f.Name == "b");
    }

    [Fact]
    public async Task 引擎插件_格式互转能力判定()
    {
        var kernel = NewKernel();
        await kernel.StartAsync();
        var engine = kernel.Services.Get<IProjectionEngine>();

        Assert.True(engine.CanConvert(ProjectionFormat.Litematic));
        Assert.True(engine.CanConvert(ProjectionFormat.StructureNbt));
        Assert.False(engine.CanConvert(ProjectionFormat.LegacySchematic)); // 数字 ID 旧格式只读
    }
}
