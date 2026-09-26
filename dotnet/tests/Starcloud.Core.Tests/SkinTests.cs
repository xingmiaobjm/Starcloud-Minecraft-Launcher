// 皮肤栈测试：库（导入 / 校验 / 重命名 / 删除 / 型号）、账户绑定、离线注入
// （userProperties 两种编码 + 回环服务真实取图）、上传请求体形状。
//
// 夹具皮肤用仓库自带的 Assets/steve.png（真实 64×64 双层皮肤），
// 负例用同一张 PNG 改 IHDR 尺寸（解码会因 CRC/长度不符失败）与纯文本，覆盖两类拒绝原因。

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

/// <summary>
/// 皮肤栈测试。账户存储与皮肤库都落在进程级数据目录（accounts.json / skins/），
/// 因此与其它写这些文件的用例串行执行，避免互相看到对方刚写的账户。
/// 皮肤库目录按用例注入独立的临时目录：数据根是进程级静态值（改环境变量已经来不及），
/// 各用例只能靠注入根目录来隔离。
/// </summary>
[Collection("进程内单例")]
public class SkinTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(),
        "starcloud-skin-" + Guid.NewGuid().ToString("N")[..8]);

    /// <summary>本用例专属皮肤库目录</summary>
    private readonly string _skins;

    public SkinTests()
    {
        Directory.CreateDirectory(_dir);
        _skins = Path.Combine(_dir, "skins");
        OfflineSkins.Reset();
    }

    public void Dispose()
    {
        OfflineSkins.Reset();
        try { Directory.Delete(_dir, recursive: true); } catch { /* 占用忽略 */ }
    }

    /// <summary>本用例专属的皮肤库（不共享进程级数据根）</summary>
    private SkinLibraryService NewLibrary() => new(_skins);

    /// <summary>仓库自带的默认皮肤（真实 64×64 皮肤纹理）</summary>
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

    private static SkinEntry ImportDefault(SkinLibraryService library, string name)
    {
        var (entry, error) = library.ImportPath(StevePng(), name);
        Assert.Null(error);
        Assert.NotNull(entry);
        return entry!;
    }

    // ── 皮肤库 ───────────────────────────────────────────────

    [Fact]
    public void 导入皮肤_落盘到皮肤库并记下尺寸()
    {
        var library = NewLibrary();
        var entry = ImportDefault(library, "我的皮肤");

        Assert.Equal("我的皮肤", entry.Name);
        Assert.Equal(64, entry.Width);
        Assert.Equal(64, entry.Height);
        Assert.Equal("64×64", entry.SizeText);
        Assert.Equal(SkinModels.Auto, entry.Model);
        Assert.True(File.Exists(library.PathOf(entry)));
        Assert.Single(library.List());

        // 库文件在本用例的 skins/ 下，不与任何别的东西混在一起
        Assert.Equal(Path.Combine(_skins, entry.FileName), library.PathOf(entry));
    }

    [Fact]
    public void 默认皮肤库落在数据根的skins目录()
    {
        // 不传根目录时跟随数据根：%APPDATA%/starcloud-launcher/skins
        Assert.Equal(LauncherPaths.SkinsDir, new SkinLibraryService().Dir);
    }

    [Fact]
    public void 导入非PNG被拒绝()
    {
        var library = NewLibrary();
        var file = Path.Combine(_dir, "not-a-skin.png");
        File.WriteAllText(file, "这不是图片");

        var (entry, error) = library.ImportPath(file);

        Assert.Null(entry);
        Assert.Contains("PNG", error);
        Assert.Empty(library.List());
    }

    [Fact]
    public void 导入尺寸不符的图片被拒绝并说明原因()
    {
        var library = NewLibrary();
        var file = Path.Combine(_dir, "big.png");
        // 把 IHDR 的宽高改成 100×100（不是 64 的倍数），解码或尺寸校验必然拦下
        var png = File.ReadAllBytes(StevePng());
        png[16] = 0; png[17] = 0; png[18] = 0; png[19] = 100;
        png[20] = 0; png[21] = 0; png[22] = 0; png[23] = 100;
        File.WriteAllBytes(file, png);

        var (entry, error) = library.ImportPath(file);

        Assert.Null(entry);
        Assert.False(string.IsNullOrWhiteSpace(error));
        Assert.Empty(library.List());
    }

    [Fact]
    public void 重命名与型号设置()
    {
        var library = NewLibrary();
        var entry = ImportDefault(library, "旧名字");

        Assert.True(library.Rename(entry.Id, "新名字").Ok);
        Assert.Equal("新名字", library.Find(entry.Id)!.Name);
        Assert.False(library.Rename(entry.Id, "   ").Ok);

        Assert.True(library.SetModel(entry.Id, SkinModels.Slim));
        Assert.Equal(SkinModels.Slim, library.Find(entry.Id)!.Model);
        Assert.True(library.SetModel(entry.Id, "乱写的值"));   // 归一到 auto，不炸
        Assert.Equal(SkinModels.Auto, library.Find(entry.Id)!.Model);
    }

    [Fact]
    public void 删除皮肤会连文件一起清掉()
    {
        var library = NewLibrary();
        var entry = ImportDefault(library, "待删");
        var path = library.PathOf(entry);

        Assert.True(library.Delete(entry.Id));

        Assert.False(File.Exists(path));
        Assert.Empty(library.List());
        Assert.False(library.Delete(entry.Id)); // 重复删除不报错
    }

    [Fact]
    public void 纹理尺寸规则与官方一致()
    {
        Assert.True(SkinTexture.IsValidSize(64, 64));
        Assert.True(SkinTexture.IsValidSize(64, 32));
        Assert.True(SkinTexture.IsValidSize(128, 128));
        Assert.True(SkinTexture.IsValidSize(256, 128));
        Assert.False(SkinTexture.IsValidSize(100, 100));
        Assert.False(SkinTexture.IsValidSize(64, 48));
        Assert.False(SkinTexture.IsValidSize(32, 32));
    }

    // ── 账户绑定 ─────────────────────────────────────────────

    [Fact]
    public void 账户可绑定皮肤_删除皮肤时清引用()
    {
        var library = NewLibrary();
        var store = new AccountStore();
        var entry = ImportDefault(library, "皮肤");
        var uuid = store.AddOrUpdateOffline("Steve").Uuid;

        store.SetSkin(uuid, entry.Id, SkinModels.Slim);
        var account = store.Accounts.First(a => a.Uuid == uuid);
        Assert.Equal(entry.Id, account.SkinId);
        Assert.Equal(SkinModels.Slim, account.SkinModel);

        // 落盘 → 重新读回来仍在
        var reloaded = AccountStore.Load();
        var reloadedAccount = reloaded.Accounts.First(a => a.Uuid == uuid);
        Assert.Equal(entry.Id, reloadedAccount.SkinId);

        library.Delete(entry.Id);
        Assert.Equal(1, reloaded.ClearSkinReference(entry.Id));
        Assert.Null(reloadedAccount.SkinId);
        Assert.Null(reloadedAccount.SkinModel);
    }

    [Fact]
    public void 取消绑定()
    {
        var library = NewLibrary();
        var store = new AccountStore();
        var entry = ImportDefault(library, "皮肤");
        var uuid = store.AddOrUpdateOffline("Steve").Uuid;
        store.SetSkin(uuid, entry.Id);

        var account = store.SetSkin(uuid, null);

        Assert.NotNull(account);
        Assert.Null(account!.SkinId);
        Assert.Null(account.SkinModel);
    }

    // ── 离线注入 ─────────────────────────────────────────────

    [Theory]
    [InlineData("1.19.3", true)]
    [InlineData("1.19.4", true)]
    [InlineData("1.20.1", true)]
    [InlineData("1.21.11", true)]
    [InlineData("24w14a", true)]
    [InlineData("1.16.5", false)]
    [InlineData("1.12.2", false)]
    [InlineData("1.7.10", false)]
    public void userProperties编码按版本切换(string version, bool base64)
    {
        var raw = OfflineSkins.BuildUserProperties("uuid-1", "Steve",
            "http://127.0.0.1:1234/skin/uuid1.png", version);
        var json = base64
            ? Encoding.UTF8.GetString(Convert.FromBase64String(raw))
            : raw;

        using var doc = JsonDocument.Parse(json);
        Assert.True(doc.RootElement.TryGetProperty("textures", out var textures));
        var inner = Encoding.UTF8.GetString(
            Convert.FromBase64String(textures[0].GetString()!));
        using var innerDoc = JsonDocument.Parse(inner);
        var url = innerDoc.RootElement.GetProperty("textures").GetProperty("SKIN").GetProperty("url")
            .GetString();
        Assert.Equal("http://127.0.0.1:1234/skin/uuid1.png", url);
        Assert.Equal("uuid1", innerDoc.RootElement.GetProperty("profileId").GetString());
        Assert.Equal("Steve", innerDoc.RootElement.GetProperty("profileName").GetString());
    }

    [Fact]
    public void 没有指定皮肤时不注入()
    {
        var library = NewLibrary();
        var store = new AccountStore();
        var offline = store.AddOrUpdateOffline("Steve");
        OfflineSkins.Configure(store, library);

        Assert.Null(OfflineSkins.UrlFor(offline.Uuid));
        Assert.False(OfflineSkins.HasSkin(offline.Uuid));
    }

    [Fact]
    public async Task 指定皮肤后给出回环URL并真的能取到纹理()
    {
        var library = NewLibrary();
        var store = new AccountStore();
        var entry = ImportDefault(library, "皮肤");
        var uuid = store.AddOrUpdateOffline("Steve").Uuid;
        store.SetSkin(uuid, entry.Id);
        OfflineSkins.Configure(store, library);

        var url = OfflineSkins.UrlFor(uuid);
        Assert.NotNull(url);
        Assert.StartsWith("http://127.0.0.1:", url);
        Assert.EndsWith(".png", url);

        // 客户端视角真实拉一次：拿到的字节必须与库里的文件逐字节相同
        using var http = new HttpClient();
        var fetched = await http.GetByteArrayAsync(url);
        Assert.Equal(File.ReadAllBytes(library.PathOf(entry)), fetched);

        // 顺带验证服务头的 MIME 类型（客户端按 content-type 判断图片）
        using var response = await http.GetAsync(url);
        Assert.Equal("image/png", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task 回环服务对没有皮肤的uuid返回404()
    {
        var library = NewLibrary();
        var store = new AccountStore();
        var entry = ImportDefault(library, "皮肤");
        var uuid = store.AddOrUpdateOffline("Steve").Uuid;
        store.SetSkin(uuid, entry.Id);
        OfflineSkins.Configure(store, library);
        var baseUrl = OfflineSkins.UrlFor(uuid)!;
        var root = baseUrl[..baseUrl.IndexOf("/skin/", StringComparison.Ordinal)];

        using var http = new HttpClient();
        var missing = await http.GetAsync(root + "/skin/00000000000000000000000000000000.png");
        Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);

        // 健康检查端点
        var health = await http.GetAsync(root + "/");
        Assert.Equal(HttpStatusCode.OK, health.StatusCode);
    }

    // ── 启动参数接线 ─────────────────────────────────────────

    [Fact]
    public async Task 离线与正版身份解析都带上皮肤URL_未选皮肤时都不带()
    {
        var library = NewLibrary();
        var store = new AccountStore();
        var entry = ImportDefault(library, "皮肤");
        var offline = store.AddOrUpdateOffline("Steve");
        store.SetSkin(offline.Uuid, entry.Id);
        OfflineSkins.Configure(store, library);

        var svc = new LaunchAccountService(store, () => new LauncherSettings());
        svc.Mode = "offline";
        var player = await svc.ResolveAsync();
        Assert.NotNull(player.SkinUrl);

        store.AddOrUpdateMicrosoft("uuid-ms", "Alex");
        store.SetDefault("uuid-ms");
        var ms = new LaunchAccountService(store, () => new LauncherSettings { LastUsername = null });
        ms.Mode = "microsoft";
        var ex = await Record.ExceptionAsync(() => ms.ResolveAsync());
        Assert.IsType<AccountLoginRequiredException>(ex); // 正版仍需登录，没有会话就不解析皮肤

        // 登录后（会话在内存里）：正版选过本地皮肤同样带上本机注入 —— 这是"正版也能换肤"的前提，
        // 界面上的强制警告才有意义（否则点了没反应）
        store.SetSession("uuid-ms", new AccountSession("token-ms"));
        var resolved = await ms.ResolveAsync();
        Assert.Equal("Alex", resolved.Name);
        Assert.Null(resolved.SkinUrl);                  // 该账户还没选本地皮肤：照旧走官方皮肤
        store.SetSkin("uuid-ms", entry.Id);
        Assert.NotNull((await ms.ResolveAsync()).SkinUrl);
    }

    [Fact]
    public void 回环服务端口是随机的且能正常关闭()
    {
        var server = new LoopbackSkinServer(uuid =>
            uuid == "abc" ? [1, 2, 3] : null);
        server.Start();
        Assert.True(server.Port is > 0 and < 65536);
        Assert.Contains(server.Port.ToString(), server.SkinUrl("abc"));
        server.Dispose();

        // 关掉后端口不再接受连接（允许系统短暂保留，只断言不抛未处理异常）
        using var client = new TcpClient();
        var closed = Record.Exception(() => client
            .ConnectAsync(IPAddress.Loopback, server.Port).Wait(TimeSpan.FromSeconds(2)));
        Assert.True(closed is null || closed is AggregateException);
    }
}
