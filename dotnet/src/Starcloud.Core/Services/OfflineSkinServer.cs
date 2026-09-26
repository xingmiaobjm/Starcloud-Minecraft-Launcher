// 离线皮肤的「游戏内生效」通道：本机回环 HTTP 服务 + --userProperties 注入。
//
// 为什么这么做：Minecraft 客户端拿皮肤的唯一途径是「profile 的 textures 属性里有 http(s) URL，
// 自己去下载」。正版/第三方由会话服务器给出这个属性；离线账户没有会话服务器，于是启动器自己
// 起一个只监听 127.0.0.1 的极简服务托管 PNG，并把 URL 通过 --userProperties 交给客户端：
//
//   --userProperties {"textures":["<base64 of {\"textures\":{\"SKIN\":{\"url\":\"http://127.0.0.1:port/skin/<uuid>.png\"}}}>"]}
//
// 1.19.3 起 userProperties 本身要求整体 Base64（客户端行为变更），更早的版本用原样 JSON，
// 所以这里按版本号选择编码方式（见 NeedsBase64）。
//
// 边界（必须诚实）：注入只影响本机客户端，因此离线皮肤在单人 / 局域网里本机可见；
// 别人看到什么由他们那侧的验证决定（未签名的纹理通常会被他们拒掉 → 显示默认皮肤）。

using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Services;

/// <summary>
/// 极简回环 HTTP 服务：只服务 <c>GET /skin/&lt;uuid&gt;[.png]</c>。
/// 用 TcpListener 而不是 HttpListener：后者在 Windows 上对非管理员进程需要 urlacl 注册，
/// 会因为「访问被拒绝」直接起不来；裸 TcpListener 不需要任何权限。
/// </summary>
public sealed class LoopbackSkinServer : IDisposable
{
    private readonly Func<string, byte[]?> _provider;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    /// <param name="provider">uuid（去掉连字符、小写）→ 皮肤 PNG 字节；没有则返回 null</param>
    public LoopbackSkinServer(Func<string, byte[]?> provider)
    {
        _provider = provider;
        _listener = new TcpListener(IPAddress.Loopback, 0); // 端口交给系统选，避免和别的程序撞
    }

    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    /// <summary>某个账户的纹理 URL（服务器未启动时抛异常，调用方先 EnsureStarted）</summary>
    public string SkinUrl(string uuid) => $"{BaseUrl}/skin/{Normalize(uuid)}.png";

    public void Start()
    {
        if (_loop is not null) return;
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _loop = Task.Run(AcceptLoop);
        LauncherLog.Write("skin", $"离线皮肤服务已启动：{BaseUrl}");
    }

    private async Task AcceptLoop()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token);
            }
            catch
            {
                return; // 已停止
            }
            _ = Task.Run(() => HandleAsync(client));
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.ReceiveTimeout = 5000;
                client.SendTimeout = 5000;
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync();
                // 把剩下的请求头读掉，方便客户端认为请求已完成（我们不需要它们）
                while (await reader.ReadLineAsync() is { Length: > 0 }) { }
                if (string.IsNullOrWhiteSpace(requestLine)) return;

                var parts = requestLine.Split(' ');
                var path = parts.Length >= 2 ? parts[1] : "";
                var query = path.IndexOf('?');
                if (query >= 0) path = path[..query];

                if (path is "/" or "/health")
                {
                    await WriteAsync(stream, "200 OK", "text/plain; charset=utf-8",
                        Encoding.UTF8.GetBytes("starcloud offline skin server"));
                    return;
                }

                if (!path.StartsWith("/skin/", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteAsync(stream, "404 Not Found", "text/plain", []);
                    return;
                }

                var name = path["/skin/".Length..];
                if (name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
                var uuid = Normalize(name);
                var png = string.IsNullOrWhiteSpace(uuid) ? null : _provider(uuid);
                if (png is null)
                {
                    LauncherLog.Write("skin", $"离线皮肤请求未命中：{uuid}");
                    await WriteAsync(stream, "404 Not Found", "text/plain", []);
                    return;
                }
                LauncherLog.Write("skin", $"游戏已取走离线皮肤：{uuid}（{png.Length} 字节）");
                await WriteAsync(stream, "200 OK", "image/png", png);
            }
            catch (Exception ex)
            {
                LauncherLog.Write("skin", $"离线皮肤服务处理请求失败：{ex.Message}");
            }
        }
    }

    private static async Task WriteAsync(NetworkStream stream, string status, string contentType,
        byte[] body)
    {
        var head = $"HTTP/1.1 {status}\r\nContent-Type: {contentType}\r\n" +
                   $"Content-Length: {body.Length}\r\nConnection: close\r\n\r\n";
        var headBytes = Encoding.ASCII.GetBytes(head);
        await stream.WriteAsync(headBytes);
        if (body.Length > 0) await stream.WriteAsync(body);
        await stream.FlushAsync();
    }

    internal static string Normalize(string uuid) =>
        (uuid ?? "").Replace("-", "").Trim().ToLowerInvariant();

    public void Dispose()
    {
        try { _cts.Cancel(); } catch { /* 已释放 */ }
        try { _listener.Stop(); } catch { /* 已停止 */ }
        try { _cts.Dispose(); } catch { /* 已释放 */ }
    }
}

/// <summary>
/// 离线皮肤注入的门面：账户 → 皮肤库 → 纹理 URL / userProperties。
/// App 启动时 <see cref="Configure"/> 注入账户存储与皮肤库；未配置时 <see cref="UrlFor"/> 返回 null
/// （等于「不注入」，启动流程照常）。
/// </summary>
public static class OfflineSkins
{
    private static readonly object Gate = new();
    private static AccountStore? _accounts;
    private static SkinLibraryService? _library;
    private static LoopbackSkinServer? _server;

    public static void Configure(AccountStore accounts, SkinLibraryService library)
    {
        lock (Gate)
        {
            _accounts = accounts;
            _library = library;
        }
    }

    /// <summary>测试用：关服务、清配置</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            _server?.Dispose();
            _server = null;
            _accounts = null;
            _library = null;
        }
    }

    /// <summary>当前是否需要注入（离线账户 + 该账户已指定库内皮肤）</summary>
    public static bool HasSkin(string uuid)
    {
        lock (Gate)
        {
            var account = _accounts?.Accounts.FirstOrDefault(a => a.Uuid == uuid);
            if (account is null || string.IsNullOrWhiteSpace(account.SkinId)) return false;
            return _library?.Find(account.SkinId!) is not null;
        }
    }

    /// <summary>该账户的纹理 URL；没有自定义皮肤时返回 null（调用方就按老路径走 "{}"）</summary>
    public static string? UrlFor(string uuid)
    {
        if (!HasSkin(uuid)) return null;
        lock (Gate)
        {
            if (_server is null)
            {
                var server = new LoopbackSkinServer(PngFor);
                server.Start();
                _server = server;
            }
            return _server.SkinUrl(uuid);
        }
    }

    /// <summary>按 uuid 取皮肤字节（回环服务用；这里只读库文件，不落任何缓存）</summary>
    public static byte[]? PngFor(string uuid)
    {
        string? skinId;
        SkinLibraryService? library;
        lock (Gate)
        {
            var account = _accounts?.Accounts.FirstOrDefault(a =>
                LoopbackSkinServer.Normalize(a.Uuid) == LoopbackSkinServer.Normalize(uuid));
            skinId = account?.SkinId;
            library = _library;
        }
        var entry = string.IsNullOrWhiteSpace(skinId) ? null : library?.Find(skinId!);
        if (entry is null || library is null) return null;
        try
        {
            var path = library.PathOf(entry);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 构造 --userProperties 的值：textures 属性指向本机皮肤服务；1.19.3 起整体 Base64。
    /// </summary>
    public static string BuildUserProperties(string uuid, string playerName, string skinUrl,
        string versionId)
    {
        var texture = new Dictionary<string, object?>
        {
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["profileId"] = LoopbackSkinServer.Normalize(uuid),
            ["profileName"] = playerName,
            ["textures"] = new Dictionary<string, object?>
            {
                ["SKIN"] = new Dictionary<string, object?> { ["url"] = skinUrl },
            },
        };
        var textureB64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(texture)));
        var properties = JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["textures"] = new[] { textureB64 },
        });
        return NeedsBase64(versionId)
            ? Convert.ToBase64String(Encoding.UTF8.GetBytes(properties))
            : properties;
    }

    /// <summary>
    /// 1.19.3 起 --userProperties 需要整体 Base64 编码（客户端解析方式变更），更早版本用原样 JSON。
    /// 快照 / 未来版本号（含字母）一律按新行为处理。
    /// </summary>
    public static bool NeedsBase64(string versionId)
    {
        var id = (versionId ?? "").Trim();
        if (id.Length == 0) return true;
        var core = id.Split('-')[0].Trim(); // 去掉 -pre1 / -rc1 之类的后缀
        if (core.Length == 0 || !char.IsDigit(core[0])) return true; // 快照（24w14a / 1.20-rc1 变体）
        var segments = core.Split('.');
        var numbers = new int[segments.Length];
        for (var i = 0; i < segments.Length; i++)
        {
            // 形如 24w14a 的版本号第一段就不是纯数字：按新版本（Base64）处理
            if (!int.TryParse(segments[i], out numbers[i])) return true;
        }
        var wanted = new[] { 1, 19, 3 };
        for (var i = 0; i < wanted.Length; i++)
        {
            var value = i < numbers.Length ? numbers[i] : 0;
            if (value != wanted[i]) return value > wanted[i];
        }
        return true; // 1.19.3 起
    }
}
