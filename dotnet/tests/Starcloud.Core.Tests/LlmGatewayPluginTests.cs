// LLM 网关插件：注册后能从契约解析，且"可用性判定"与自接模型的互斥关系正确。
//
// 这里不联网：只验证契约接线与纯逻辑（CanUse / HasOwnModel），
// 真正的 HTTP 协议由 NovaGatewayTests 覆盖。

using Starcloud.Core.Kernel;
using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Kernel.Plugins;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public sealed class LlmGatewayPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sc-llm-gateway-" + Guid.NewGuid().ToString("N"));

    private readonly LauncherSettings _settings = new();

    public LlmGatewayPluginTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清不掉不影响断言 */ }
    }

    private KernelHost NewKernel()
    {
        var kernel = new KernelHost(new KernelOptions { DataRoot = _root });
        kernel.Register(BuiltinLlmGatewayPlugin.Descriptor,
            _ => new BuiltinLlmGatewayPlugin(() => _settings));
        return kernel;
    }

    private static StarcloudSession Session(string key) =>
        new("sc-1", "Tester", key, StarcloudIdService.CloudChannel, DateTime.Now);

    [Fact]
    public async Task 网关插件_启动后能按契约解析()
    {
        var kernel = NewKernel();
        await kernel.StartAsync();

        var gateway = kernel.Services.Get<ILlmGateway>();
        Assert.Equal("Starcloud 云端网关", gateway.DisplayName);
    }

    [Fact]
    public async Task 网关可用性_跟随开关与登录态()
    {
        var kernel = NewKernel();
        await kernel.StartAsync();
        var gateway = kernel.Services.Get<ILlmGateway>();

        // 开关默认开、Key 有效 → 可用
        Assert.True(gateway.CanUse(Session("sk-live")));
        // 没登录 / 空 Key → 不可用
        Assert.False(gateway.CanUse(null));
        Assert.False(gateway.CanUse(Session("")));

        // 关掉开关后一律不可用
        _settings.NovaGatewayEnabled = false;
        Assert.False(gateway.CanUse(Session("sk-live")));
    }

    [Fact]
    public async Task 自接模型判定_未配置时为假()
    {
        var kernel = NewKernel();
        await kernel.StartAsync();
        var gateway = kernel.Services.Get<ILlmGateway>();

        // 没有配置 profile → 没有自接模型
        Assert.False(gateway.HasOwnModel());
    }
}
