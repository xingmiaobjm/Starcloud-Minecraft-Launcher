// 正版登录 30 天免登录：只保存加密的 refresh_token，过期惰性清理，刷新轮换不重置 30 天起点。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class MicrosoftTokenStoreTests
{
    private static readonly DateTime T0 = new(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void 保存后可以取回且文件里没有明文()
    {
        using var temp = new TempStore();
        MicrosoftTokenStore.SaveNew("uuid-1", "refresh-token-中文-123");

        Assert.True(MicrosoftTokenStore.TryGet("uuid-1", out var token));
        Assert.Equal("refresh-token-中文-123", token);

        var raw = File.ReadAllText(temp.Path);
        Assert.DoesNotContain("refresh-token-中文-123", raw);
    }

    [Fact]
    public void 超过30天视为过期并自动清理()
    {
        using var temp = new TempStore();
        MicrosoftTokenStore.SaveNew("uuid-1", "token-a");

        MicrosoftTokenStore.UtcNow = () => T0.AddDays(29);
        Assert.True(MicrosoftTokenStore.TryGet("uuid-1", out _));

        MicrosoftTokenStore.UtcNow = () => T0.AddDays(31);
        Assert.False(MicrosoftTokenStore.TryGet("uuid-1", out _));
        Assert.False(MicrosoftTokenStore.HasValid("uuid-1"));
    }

    [Fact]
    public void 刷新轮换token不延长30天期限()
    {
        using var temp = new TempStore();
        MicrosoftTokenStore.SaveNew("uuid-1", "token-old");

        MicrosoftTokenStore.UtcNow = () => T0.AddDays(20);
        MicrosoftTokenStore.Update("uuid-1", "token-new");

        Assert.True(MicrosoftTokenStore.TryGet("uuid-1", out var token));
        Assert.Equal("token-new", token);

        // 起点仍是 T0：第 31 天照样过期
        MicrosoftTokenStore.UtcNow = () => T0.AddDays(31);
        Assert.False(MicrosoftTokenStore.TryGet("uuid-1", out _));
    }

    [Fact]
    public void 删除账户会清掉凭证()
    {
        using var temp = new TempStore();
        MicrosoftTokenStore.SaveNew("uuid-1", "token-a");
        MicrosoftTokenStore.Remove("uuid-1");
        Assert.False(MicrosoftTokenStore.TryGet("uuid-1", out _));
    }

    private sealed class TempStore : IDisposable
    {
        private readonly string? _oldPath;
        private readonly Func<DateTime> _oldClock = MicrosoftTokenStore.UtcNow;

        public TempStore()
        {
            var root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "sc-ms-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(root);
            Path = System.IO.Path.Combine(root, "microsoft.tokens.json");
            _oldPath = MicrosoftTokenStore.FilePathOverride;
            MicrosoftTokenStore.FilePathOverride = Path;
            MicrosoftTokenStore.UtcNow = () => T0;
        }

        public string Path { get; }

        public void Dispose()
        {
            MicrosoftTokenStore.FilePathOverride = _oldPath;
            MicrosoftTokenStore.UtcNow = _oldClock;
            try { Directory.Delete(System.IO.Path.GetDirectoryName(Path)!, true); } catch { }
        }
    }
}
