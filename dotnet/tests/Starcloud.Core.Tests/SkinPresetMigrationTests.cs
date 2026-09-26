// 老预置皮肤清理测试：只删「来源 = 预置 且 id 已不在清单里」的条目，
// 用户自己导入的（来源 file / url）和当前清单里的官方默认皮肤都不许动。

using Starcloud.Core.Rendering;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

[Collection("进程内单例")]
public class SkinPresetMigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "starcloud-skin-mig-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly string _skins;

    public SkinPresetMigrationTests()
    {
        Directory.CreateDirectory(_dir);
        _skins = Path.Combine(_dir, "skins");
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 占用忽略 */ }
    }

    private static string StevePng()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName,
                "src", "Starcloud.App", "Assets", "steve.png");
            if (File.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到测试用皮肤 steve.png");
    }

    [Fact]
    public void 只清理不再是预置皮肤的条目()
    {
        var library = new SkinLibraryService(_skins);
        var store = new AccountStore();
        var png = File.ReadAllBytes(StevePng());

        // 上一版启动器画出来的老预置皮肤（id 已不在清单里）
        var legacy = library.ImportBytes(png, "经典", source: SkinPresetMigration.PresetSource,
            sourceUrl: "classic").Entry!;
        // 用户自己导入的皮肤
        var mine = library.ImportBytes(png, "我的皮肤", source: "file").Entry!;
        // 当前清单里的官方默认皮肤
        var official = library.ImportBytes(png, SkinPresets.All[0].Name,
            source: SkinPresetMigration.PresetSource, sourceUrl: SkinPresets.All[0].Id).Entry!;

        var uuid = store.AddOrUpdateOffline("Steve").Uuid;
        store.SetSkin(uuid, legacy.Id);

        var removed = SkinPresetMigration.PurgeLegacyPresetSkins(library, store);

        Assert.Equal(1, removed);
        Assert.Null(library.Find(legacy.Id));
        Assert.NotNull(library.Find(mine.Id));
        Assert.NotNull(library.Find(official.Id));
        Assert.Null(store.Accounts.First(a => a.Uuid == uuid).SkinId);   // 账户引用被清掉，回到默认形象
    }

    [Fact]
    public void 没有老条目时什么也不做()
    {
        var library = new SkinLibraryService(_skins);
        var store = new AccountStore();
        Assert.Equal(0, SkinPresetMigration.PurgeLegacyPresetSkins(library, store));
    }
}
