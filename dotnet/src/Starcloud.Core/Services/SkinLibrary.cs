// 皮肤库（多皮肤管理）：把用户导入的皮肤 PNG 收进启动器数据目录，一个账户可以随时换一个。
//
// 与账户的关系：账户只记 skin_id（+ 型号覆盖），皮肤文件由本库统一管；删掉库里的皮肤时
// 账户上的引用会被清空（见 AccountStore.ClearSkinReference），不会留下指向空洞的引用。
//
// 纹理校验用渲染层同一个 PngDecoder：能解码 + 尺寸符合 Minecraft 皮肤规则（64×32 / 64×64 / HD
// 倍数）才算合法，避免把一张风景照塞进库、到启动游戏时才发现画不出来。

using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core.Rendering;

namespace Starcloud.Core.Services;

/// <summary>皮肤型号：auto = 按纹理自动判断（纤细手臂透明即 slim）</summary>
public static class SkinModels
{
    public const string Auto = "auto";
    public const string Classic = "classic";
    public const string Slim = "slim";

    public static string Normalize(string? model) => model switch
    {
        Classic => Classic,
        Slim => Slim,
        _ => Auto,
    };

}

/// <summary>皮肤库条目（索引文件里的一行）</summary>
public sealed class SkinEntry
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>皮肤库目录内的文件名（<c>&lt;id&gt;.png</c>）</summary>
    [JsonPropertyName("file")] public string FileName { get; set; } = "";

    [JsonPropertyName("model")] public string Model { get; set; } = SkinModels.Auto;

    /// <summary>来源："file"（本地导入）/ "url"（链接导入）</summary>
    [JsonPropertyName("source")] public string Source { get; set; } = "file";

    [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
    [JsonPropertyName("width")] public int Width { get; set; }
    [JsonPropertyName("height")] public int Height { get; set; }
    [JsonPropertyName("added_at")] public DateTime AddedAt { get; set; } = DateTime.Now;

    public string SizeText => $"{Width}×{Height}";
}

/// <summary>皮肤纹理校验与小工具（库 / 上传 / 预览共用同一口径）</summary>
public static class SkinTexture
{
    /// <summary>Minecraft 皮肤合法尺寸：64×32（老版）、64×64（双层）、HD 等比放大</summary>
    public static bool IsValidSize(int width, int height) =>
        width % 64 == 0 && height % 32 == 0 &&
        (height == width || height * 2 == width);

    /// <summary>解码并校验；失败时给出可直接展示给用户的原因</summary>
    public static bool TryDecode(byte[] png, out SkinBitmap bitmap, out string error)
    {
        bitmap = null!;
        error = "";
        try
        {
            bitmap = PngDecoder.Decode(png);
        }
        catch (SkinTextureException ex)
        {
            error = ex.Message;
            return false;
        }
        if (!IsValidSize(bitmap.Width, bitmap.Height))
        {
            error = $"尺寸 {bitmap.Width}×{bitmap.Height} 不是 Minecraft 皮肤（需 64×32 / 64×64 或它们的等比放大）";
            return false;
        }
        return true;
    }

    /// <summary>
    /// 纤细手臂判定：双层皮肤在 (54,20) 处的透明像素表示手臂只有 3 像素宽（slim）。
    /// 与 <c>SkinFigureView</c> 的判定一致，口径唯一。
    /// </summary>
    public static bool DetectSlim(SkinBitmap bitmap)
    {
        if (bitmap.Width != bitmap.Height) return false; // 64×32 老皮肤只有经典版型
        var x = 54 * bitmap.Width / 64;
        var y = 20 * bitmap.Height / 64;
        if (x >= bitmap.Width || y >= bitmap.Height) return false;
        return bitmap.Rgba[(y * bitmap.Width + x) * 4 + 3] == 0;
    }

    /// <summary>解析最终生效型号：auto 时按纹理判断</summary>
    public static string ResolveModel(string? model, SkinBitmap? bitmap) =>
        SkinModels.Normalize(model) switch
        {
            SkinModels.Classic => SkinModels.Classic,
            SkinModels.Slim => SkinModels.Slim,
            _ => bitmap is not null && DetectSlim(bitmap) ? SkinModels.Slim : SkinModels.Classic,
        };
}

/// <summary>
/// 皮肤库：<c>%APPDATA%/starcloud-launcher/skins/</c>（一皮肤一 PNG + library.json 索引）。
/// 无状态服务（每次读索引），可安全地跨线程使用。
/// </summary>
public sealed class SkinLibraryService
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly object _gate = new();

    /// <param name="root">
    /// 皮肤库目录；默认 <see cref="LauncherPaths.SkinsDir"/>。
    /// 允许指定是为了测试隔离（数据根是进程级静态值，测试之间不能各自改环境变量）。
    /// </param>
    public SkinLibraryService(string? root = null) => Dir = root ?? LauncherPaths.SkinsDir;

    /// <summary>库目录（一皮肤一 PNG + library.json）</summary>
    public string Dir { get; }

    private string IndexFile => Path.Combine(Dir, "library.json");

    /// <summary>库内全部皮肤（按加入时间倒序）</summary>
    public IReadOnlyList<SkinEntry> List()
    {
        lock (_gate)
        {
            return Load().OrderByDescending(e => e.AddedAt).ToList();
        }
    }

    public SkinEntry? Find(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        lock (_gate)
        {
            return Load().FirstOrDefault(e => e.Id == id);
        }
    }

    /// <summary>条目的绝对文件路径（不保证文件存在）</summary>
    public string PathOf(SkinEntry entry) => Path.Combine(Dir, entry.FileName);

    /// <summary>某条目的纹理字节；文件缺失返回 null</summary>
    public byte[]? ReadBytes(string id)
    {
        var entry = Find(id);
        if (entry is null) return null;
        try
        {
            var path = PathOf(entry);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>当前生效的型号（用于上传 / 预览 / 注入）</summary>
    public string ResolveModel(string id, string? overrideModel = null)
    {
        var entry = Find(id);
        var bytes = ReadBytes(id);
        SkinBitmap? bitmap = null;
        if (bytes is not null && SkinTexture.TryDecode(bytes, out var decoded, out _)) bitmap = decoded;
        var wanted = string.IsNullOrWhiteSpace(overrideModel) || overrideModel == SkinModels.Auto
            ? entry?.Model
            : overrideModel;
        return SkinTexture.ResolveModel(wanted, bitmap);
    }

    /// <summary>导入本地 PNG；返回条目或可读错误</summary>
    public (SkinEntry? Entry, string? Error) ImportPath(string path, string? name = null)
    {
        try
        {
            if (!File.Exists(path)) return (null, "找不到文件：" + path);
            return ImportBytes(File.ReadAllBytes(path), name ?? Path.GetFileNameWithoutExtension(path),
                source: "file", sourceUrl: null);
        }
        catch (Exception ex)
        {
            return (null, ex.Message);
        }
    }

    /// <summary>从链接导入（走启动器下载引擎，支持镜像与重试）</summary>
    public async Task<(SkinEntry? Entry, string? Error)> ImportUrlAsync(
        string url, string? name = null, DownloadService? download = null,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(url)) return (null, "链接为空");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            uri.Scheme is not ("http" or "https"))
            return (null, "只支持 http / https 链接");
        try
        {
            byte[] bytes;
            var http = download is null ? new HttpClient() : null;
            if (http is not null)
            {
                http.Timeout = TimeSpan.FromSeconds(30);
                bytes = await http.GetByteArrayAsync(uri, ct);
                http.Dispose();
            }
            else
            {
                var temp = Path.Combine(Path.GetTempPath(), "starcloud-skin-" +
                    Guid.NewGuid().ToString("N")[..8] + ".png");
                try
                {
                    await download!.DownloadFileAsync(url, temp, null, 2, ct);
                    bytes = await File.ReadAllBytesAsync(temp, ct);
                }
                finally
                {
                    try { File.Delete(temp); } catch { /* 临时文件清理失败忽略 */ }
                }
            }
            var fallback = Path.GetFileNameWithoutExtension(uri.AbsolutePath);
            return ImportBytes(bytes, name ?? (string.IsNullOrWhiteSpace(fallback) ? "链接皮肤" : fallback),
                source: "url", sourceUrl: url);
        }
        catch (Exception ex)
        {
            return (null, "下载失败：" + ex.Message);
        }
    }

    /// <summary>把已解码的皮肤字节收进库（导入路径与链接导入的公共落盘部分）</summary>
    public (SkinEntry? Entry, string? Error) ImportBytes(byte[] png, string? name,
        string source = "file", string? sourceUrl = null)
    {
        if (png.Length == 0) return (null, "文件是空的");
        if (png.Length > 4 * 1024 * 1024) return (null, "文件超过 4MB，不像是皮肤纹理");
        if (!SkinTexture.TryDecode(png, out var bitmap, out var error)) return (null, error);

        var entry = new SkinEntry
        {
            Id = Guid.NewGuid().ToString("N")[..12],
            Name = string.IsNullOrWhiteSpace(name) ? "未命名皮肤" : name!.Trim(),
            FileName = "",
            Model = SkinModels.Auto,
            Source = source,
            SourceUrl = sourceUrl,
            Width = bitmap.Width,
            Height = bitmap.Height,
            AddedAt = DateTime.Now,
        };
        entry.FileName = entry.Id + ".png";

        lock (_gate)
        {
            var list = TryLoad(out var readError);
            if (list is null)
                return (null, $"皮肤库索引读不出来（{readError}），这次没有写入，避免覆盖已有的皮肤");
            Directory.CreateDirectory(Dir);
            File.WriteAllBytes(Path.Combine(Dir, entry.FileName), png);
            list.Add(entry);
            Save(list);
        }
        return (entry, null);
    }

    public (bool Ok, string? Error) Rename(string id, string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return (false, "名字不能为空");
        lock (_gate)
        {
            var list = TryLoad(out var readError);
            if (list is null) return (false, $"皮肤库索引读不出来（{readError}），这次没有写入");
            var entry = list.FirstOrDefault(e => e.Id == id);
            if (entry is null) return (false, "皮肤不存在（可能已被删除）");
            entry.Name = name.Trim();
            Save(list);
        }
        return (true, null);
    }

    public bool SetModel(string id, string model)
    {
        lock (_gate)
        {
            var list = TryLoad(out _);
            if (list is null) return false;
            var entry = list.FirstOrDefault(e => e.Id == id);
            if (entry is null) return false;
            entry.Model = SkinModels.Normalize(model);
            Save(list);
            return true;
        }
    }

    /// <summary>删除皮肤（连同文件）；账户上的引用由调用方清理</summary>
    public bool Delete(string id)
    {
        SkinEntry? removed;
        lock (_gate)
        {
            var list = TryLoad(out _);
            if (list is null) return false;
            removed = list.FirstOrDefault(e => e.Id == id);
            if (removed is null) return false;
            list.Remove(removed);
            Save(list);
            try
            {
                var path = Path.Combine(Dir, removed.FileName);
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 文件被占用：索引已删，下次启动清理残留文件不再影响功能
            }
            // 留一条"谁被删了"的痕迹：皮肤库一夜之间被清空时，日志是唯一的线索
            LauncherLog.Write("skin",
                $"删除皮肤「{removed.Name}」（{removed.Id}，来源 {removed.Source}"
                + (removed.SourceUrl is { Length: > 0 } u ? $"/{u}" : "") + $"），库内还剩 {list.Count} 张");
        }
        return true;
    }

    // ── 索引读写 ──

    /// <summary>
    /// 读索引。读不出来（文件存在但坏了 / 被占用 / 写到一半）返回 <c>null</c> 并给出原因。
    /// <para>为什么读失败不能一律当空库：**读空库 + 紧接着写入 = 把用户攒的皮肤全清掉**。
    /// 一次瞬时的读取失败就足以让下一次"导入一张皮肤"变成"只剩这一张"。所以读路径
    /// （<see cref="List"/> / <see cref="Find"/>）兜底成空库照常显示，写路径必须拒绝动手。</para>
    /// </summary>
    private List<SkinEntry>? TryLoad(out string? error)
    {
        error = null;
        try
        {
            if (!File.Exists(IndexFile)) return [];
            var text = File.ReadAllText(IndexFile);
            if (string.IsNullOrWhiteSpace(text)) return [];   // 刚创建还没写的空文件 = 空库
            return JsonSerializer.Deserialize<List<SkinEntry>>(text, JsonOpts) ?? [];
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>读路径用的兜底版本：读不出来就当空库，界面照常可用</summary>
    private List<SkinEntry> Load() => TryLoad(out _) ?? [];

    private void Save(List<SkinEntry> list)
    {
        Directory.CreateDirectory(Dir);
        var temp = IndexFile + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(list, JsonOpts));
        File.Move(temp, IndexFile, overwrite: true);
    }
}
