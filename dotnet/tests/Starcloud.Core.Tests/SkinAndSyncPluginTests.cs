// 皮肤库 / 跨设备存档同步：两个引擎插件的接线与真实能力。
//
// 皮肤库用真实 SkinLibraryService（临时目录）+ 仓库里的 alex.png 走一次导入/查找/删除，
// 确认契约不是空壳；存档同步需要链路与登录态，这里用替身验证插件接线。

using Starcloud.Core.Kernel;
using Starcloud.Core.Kernel.Contracts;
using Starcloud.Core.Kernel.Plugins;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public sealed class SkinAndSyncPluginTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sc-skin-sync-" + Guid.NewGuid().ToString("N"));

    public SkinAndSyncPluginTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清不掉不影响断言 */ }
    }

    private KernelHost NewKernel() => new(new KernelOptions { DataRoot = _root });

    /// <summary>仓库里的皮肤样例（相对测试输出目录向上找）</summary>
    private static string? FindSampleSkin()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var p = Path.Combine(dir.FullName, "dotnet", "src", "Starcloud.App", "Assets", "Skins", "default", "alex.png");
            if (File.Exists(p)) return p;
            dir = dir.Parent;
        }
        return null;
    }

    // ── 皮肤库 ──

    [Fact]
    public async Task 皮肤插件_注册后能解析且库可读写()
    {
        var skins = new SkinLibraryService(Path.Combine(_root, "skins"));
        var kernel = NewKernel();
        kernel.Register(BuiltinSkinPlugin.Descriptor, _ => new BuiltinSkinPlugin(skins));
        await kernel.StartAsync();

        var lib = kernel.Services.Get<ISkinLibrary>();
        Assert.Equal("本地皮肤库", lib.DisplayName);
        Assert.Empty(lib.List());

        var sample = FindSampleSkin();
        Assert.NotNull(sample); // 样例被挪走就该红，而不是悄悄跳过

        var (entry, error) = lib.ImportBytes(File.ReadAllBytes(sample!), "测试皮肤");
        Assert.Null(error);
        Assert.NotNull(entry);

        var found = lib.Find(entry!.Id);
        Assert.NotNull(found);
        Assert.Equal("测试皮肤", found!.Name);
        Assert.NotEmpty(lib.ReadBytes(entry.Id)!);

        Assert.True(lib.Delete(entry.Id));
        Assert.Null(lib.Find(entry.Id));
        Assert.Empty(lib.List());
    }

    [Fact]
    public async Task 皮肤插件_非法字节被拒且不进库()
    {
        var skins = new SkinLibraryService(Path.Combine(_root, "skins2"));
        var kernel = NewKernel();
        kernel.Register(BuiltinSkinPlugin.Descriptor, _ => new BuiltinSkinPlugin(skins));
        await kernel.StartAsync();

        var lib = kernel.Services.Get<ISkinLibrary>();
        var (entry, error) = lib.ImportBytes([1, 2, 3], "坏文件");

        Assert.Null(entry);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(lib.List());
    }

    // ── 跨设备存档同步 ──

    private sealed class FakeSaveSync : ISaveSync
    {
        public string DisplayName => "替身同步";
        public int PushCalls { get; private set; }

        public Task<SaveSyncResult> PushAsync(PeerInfo peer, SaveInfo save, CancellationToken ct = default)
        {
            PushCalls++;
            return Task.FromResult(new SaveSyncResult(true, "ok"));
        }

        public Task<List<RemoteSaveInfo>?> ListRemoteAsync(PeerInfo peer, CancellationToken ct = default) =>
            Task.FromResult<List<RemoteSaveInfo>?>([]);

        public Task<SaveSyncResult> PullAsync(PeerInfo peer, RemoteSaveInfo remote,
            string localVersionId, bool localIsolated, CancellationToken ct = default) =>
            Task.FromResult(new SaveSyncResult(true, "ok"));
    }

    [Fact]
    public async Task 存档同步插件_注册后能解析且能调到实现()
    {
        var fake = new FakeSaveSync();
        var kernel = NewKernel();
        kernel.Register(BuiltinSaveSyncPlugin.Descriptor, _ => new BuiltinSaveSyncPlugin(fake));
        await kernel.StartAsync();

        var sync = kernel.Services.Get<ISaveSync>();
        Assert.Equal("替身同步", sync.DisplayName);

        var peer = new PeerInfo
        {
            Uid = "p1",
            Name = "另一台",
            Address = "127.0.0.1",
            Port = 1,
            LastSeen = DateTime.Now,
        };
        var save = new SaveInfo("世界", "世界", DateTime.Now, 0, null, "1.20.1", false, _root);
        var result = await sync.PushAsync(peer, save);
        Assert.True(result.Ok);
        Assert.Equal(1, fake.PushCalls);
    }
}
