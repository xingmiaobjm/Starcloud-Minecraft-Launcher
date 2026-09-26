// 皮肤库索引的"读写不对称"守卫。
//
// 真踩过的坑：索引读不出来时按空库处理（本意是别让一条坏 JSON 拖垮整个换肤功能），
// 但下一次写入就把「空库 + 刚导入的这一张」落盘 —— 用户攒的皮肤连同索引一起没了。
// 这里钉住：读路径可以兜底成空库，写路径一律拒绝动手。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

[Collection("进程内单例")]
public class SkinLibraryIndexGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "starcloud-skin-guard-" + Guid.NewGuid().ToString("N")[..8]);

    private readonly SkinLibraryService _library;

    public SkinLibraryIndexGuardTests()
    {
        Directory.CreateDirectory(_dir);
        _library = new SkinLibraryService(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* 占用忽略 */ }
    }

    [Fact]
    public void 索引读不出来时导入不动手_已有皮肤不会被覆盖()
    {
        Import("先存一张");
        var broken = "这不是 JSON";
        File.WriteAllText(Path.Combine(_dir, "library.json"), broken);

        var (entry, error) = _library.ImportBytes(SkinPng(), "后来的");

        Assert.Null(entry);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Equal(broken, File.ReadAllText(Path.Combine(_dir, "library.json")));   // 一个字节都没改

        // 读路径仍然兜底：界面继续能用（显示成空库），而不是整块功能报错
        Assert.Empty(_library.List());
    }

    [Fact]
    public void 索引读不出来时删除与改名都不动手()
    {
        var id = Import("要保住的").Id;
        File.WriteAllText(Path.Combine(_dir, "library.json"), "{ 坏掉的索引");

        Assert.False(_library.Delete(id));
        Assert.False(_library.Rename(id, "新名字").Ok);
        Assert.False(_library.SetModel(id, SkinModels.Slim));
        Assert.True(File.Exists(Path.Combine(_dir, id + ".png")));   // 文件也还在
    }

    private SkinEntry Import(string name)
    {
        var (entry, error) = _library.ImportBytes(SkinPng(), name);
        Assert.Null(error);
        return entry!;
    }

    /// <summary>找一张现成的合法皮肤 PNG 当素材</summary>
    private static byte[] SkinPng()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName,
                "src", "Starcloud.App", "Assets", "steve.png");
            if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            dir = dir.Parent;
        }
        throw new FileNotFoundException("找不到测试用皮肤 steve.png");
    }
}

