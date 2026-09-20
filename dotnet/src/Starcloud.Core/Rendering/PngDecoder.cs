// 极简 PNG 解码器（设计：docs/designs/2026-09-12-skin-viewer-design.md §3.1）
//
// 为什么要自己写：Core 没有任何 PackageReference（零新增依赖是项目约定），
// 而 .NET BCL 不带 PNG 解码。PNG 的 IDAT 就是 zlib 流，BCL 从 .NET 6 起自带
// System.IO.Compression.ZLibStream，所以"解压 + 逐行反滤波"就能拿到像素。
//
// 支持范围只覆盖 Mojang 皮肤/披风纹理的实际需要，多一条都不做：
//   bit depth 8；颜色类型 0/2/3/4/6；tRNS（调色板索引 / 灰度与真彩单色键）；
//   interlace 必须为 0；多 IDAT 拼接；不校验 CRC（只校验签名与块长度）。

using System.IO.Compression;

namespace Starcloud.Core.Rendering;

/// <summary>纹理不可用（格式不支持 / 数据损坏）</summary>
public sealed class SkinTextureException : Exception
{
    public SkinTextureException(string message) : base(message) { }
}

/// <summary>解码后的纹理位图（RGBA8，直通 alpha）</summary>
public sealed class SkinBitmap
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }

    public SkinBitmap(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Rgba = rgba;
    }

    /// <summary>取一块像素（越界按边钳制，便于 HD 缩放与覆盖层外扩时直接取用）</summary>
    public byte[] GetRegion(int x, int y, int w, int h)
    {
        var buf = new byte[w * h * 4];
        for (var row = 0; row < h; row++)
        {
            var sy = Math.Clamp(y + row, 0, Height - 1);
            for (var col = 0; col < w; col++)
            {
                var sx = Math.Clamp(x + col, 0, Width - 1);
                var src = (sy * Width + sx) * 4;
                var dst = (row * w + col) * 4;
                buf[dst] = Rgba[src];
                buf[dst + 1] = Rgba[src + 1];
                buf[dst + 2] = Rgba[src + 2];
                buf[dst + 3] = Rgba[src + 3];
            }
        }
        return buf;
    }

    /// <summary>采样一个像素（最近邻，越界钳制）</summary>
    public void Sample(int x, int y, Span<byte> rgba)
    {
        var sx = Math.Clamp(x, 0, Width - 1);
        var sy = Math.Clamp(y, 0, Height - 1);
        var src = (sy * Width + sx) * 4;
        rgba[0] = Rgba[src];
        rgba[1] = Rgba[src + 1];
        rgba[2] = Rgba[src + 2];
        rgba[3] = Rgba[src + 3];
    }

    /// <summary>该区域是否全透明（覆盖层整面剔除用，省算力也避免黑色伪影）</summary>
    public bool IsRegionFullyTransparent(int x, int y, int w, int h)
    {
        for (var row = 0; row < h; row++)
        {
            var sy = Math.Clamp(y + row, 0, Height - 1);
            for (var col = 0; col < w; col++)
            {
                var sx = Math.Clamp(x + col, 0, Width - 1);
                if (Rgba[(sy * Width + sx) * 4 + 3] != 0) return false;
            }
        }
        return true;
    }
}

public static class PngDecoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    public static SkinBitmap Decode(byte[] png)
    {
        if (png.Length < Signature.Length + 12 || !png.AsSpan(0, Signature.Length).SequenceEqual(Signature))
            throw new SkinTextureException("不是 PNG 文件");

        int width = 0, height = 0, bitDepth = 0, colorType = 0;
        byte[]? palette = null, trns = null;
        using var idat = new MemoryStream();

        var pos = Signature.Length;
        while (pos + 8 <= png.Length)
        {
            var length = ReadBigEndian(png, pos);
            var type = System.Text.Encoding.ASCII.GetString(png, pos + 4, 4);
            var dataStart = pos + 8;
            if (length < 0 || dataStart + length > png.Length)
                throw new SkinTextureException("PNG 块长度越界");

            switch (type)
            {
                case "IHDR":
                    if (length < 13) throw new SkinTextureException("IHDR 长度不足");
                    width = ReadBigEndian(png, dataStart);
                    height = ReadBigEndian(png, dataStart + 4);
                    bitDepth = png[dataStart + 8];
                    colorType = png[dataStart + 9];
                    if (bitDepth != 8) throw new SkinTextureException($"不支持的位深 {bitDepth}");
                    if (png[dataStart + 12] != 0) throw new SkinTextureException("不支持隔行扫描 PNG");
                    if (colorType is not (0 or 2 or 3 or 4 or 6))
                        throw new SkinTextureException($"不支持的颜色类型 {colorType}");
                    break;
                case "PLTE":
                    palette = png.AsSpan(dataStart, length).ToArray();
                    break;
                case "tRNS":
                    trns = png.AsSpan(dataStart, length).ToArray();
                    break;
                case "IDAT":
                    idat.Write(png, dataStart, length);
                    break;
                case "IEND":
                    pos = png.Length; // 结束
                    continue;
            }

            pos = dataStart + length + 4; // 跳过 CRC
        }

        if (width <= 0 || height <= 0) throw new SkinTextureException("缺少 IHDR");
        if (idat.Length == 0) throw new SkinTextureException("缺少 IDAT");

        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        var raw = Inflate(idat.ToArray(), width, height, channels, out var stride);
        return ToRgba(raw, width, height, colorType, channels, stride, palette, trns);
    }

    /// <summary>解压后逐行反滤波（规则与 PNG 规范一致）</summary>
    private static byte[] Inflate(byte[] zlib, int width, int height, int channels, out int stride)
    {
        stride = width * channels;
        var raw = new byte[(stride + 1) * height];
        try
        {
            using var input = new MemoryStream(zlib);
            using var inflate = new ZLibStream(input, CompressionMode.Decompress);
            var read = 0;
            while (read < raw.Length)
            {
                var n = inflate.Read(raw, read, raw.Length - read);
                if (n <= 0) break;
                read += n;
            }
            if (read < raw.Length) throw new SkinTextureException("IDAT 解压数据不足");
        }
        catch (SkinTextureException) { throw; }
        catch (Exception ex)
        {
            throw new SkinTextureException($"IDAT 解压失败：{ex.Message}");
        }

        var bpp = channels;
        var outBytes = new byte[stride * height];
        for (var y = 0; y < height; y++)
        {
            var filter = raw[y * (stride + 1)];
            var src = y * (stride + 1) + 1;
            var dst = y * stride;
            for (var x = 0; x < stride; x++)
            {
                int a = x >= bpp ? outBytes[dst + x - bpp] : 0;          // 左
                int b = y > 0 ? outBytes[dst - stride + x] : 0;           // 上
                int c = x >= bpp && y > 0 ? outBytes[dst - stride + x - bpp] : 0; // 左上
                int value = raw[src + x];
                outBytes[dst + x] = (byte)(filter switch
                {
                    0 => value,
                    1 => value + a,
                    2 => value + b,
                    3 => value + (a + b) / 2,
                    4 => value + Paeth(a, b, c),
                    _ => throw new SkinTextureException($"未知行滤波 {filter}"),
                });
            }
        }
        return outBytes;
    }

    private static int Paeth(int a, int b, int c)
    {
        var p = a + b - c;
        var pa = Math.Abs(p - a);
        var pb = Math.Abs(p - b);
        var pc = Math.Abs(p - c);
        if (pa <= pb && pa <= pc) return a;
        return pb <= pc ? b : c;
    }

    private static SkinBitmap ToRgba(byte[] raw, int width, int height, int colorType, int channels,
        int stride, byte[]? palette, byte[]? trns)
    {
        var rgba = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var src = y * stride + x * channels;
                var dst = (y * width + x) * 4;
                switch (colorType)
                {
                    case 0: // 灰度 + 可选单色透明键
                    {
                        var g = raw[src];
                        var alpha = trns is { Length: >= 2 } && g == trns[1] ? (byte)0 : (byte)255;
                        rgba[dst] = g; rgba[dst + 1] = g; rgba[dst + 2] = g; rgba[dst + 3] = alpha;
                        break;
                    }
                    case 2: // 真彩 + 可选单色透明键
                    {
                        var r = raw[src]; var g = raw[src + 1]; var b = raw[src + 2];
                        var alpha = (byte)255;
                        if (trns is { Length: >= 6 } &&
                            r == trns[1] && g == trns[3] && b == trns[5]) alpha = 0;
                        rgba[dst] = r; rgba[dst + 1] = g; rgba[dst + 2] = b; rgba[dst + 3] = alpha;
                        break;
                    }
                    case 3: // 调色板
                    {
                        if (palette is null) throw new SkinTextureException("调色板 PNG 缺少 PLTE");
                        var index = raw[src];
                        var pi = index * 3;
                        if (pi + 2 >= palette.Length) throw new SkinTextureException("调色板索引越界");
                        rgba[dst] = palette[pi];
                        rgba[dst + 1] = palette[pi + 1];
                        rgba[dst + 2] = palette[pi + 2];
                        rgba[dst + 3] = index < (trns?.Length ?? 0) ? trns![index] : (byte)255;
                        break;
                    }
                    case 4: // 灰度 + alpha
                        rgba[dst] = raw[src]; rgba[dst + 1] = raw[src];
                        rgba[dst + 2] = raw[src]; rgba[dst + 3] = raw[src + 1];
                        break;
                    default: // 6: RGBA
                        rgba[dst] = raw[src]; rgba[dst + 1] = raw[src + 1];
                        rgba[dst + 2] = raw[src + 2]; rgba[dst + 3] = raw[src + 3];
                        break;
                }
            }
        }
        return new SkinBitmap(width, height, rgba);
    }

    private static int ReadBigEndian(byte[] data, int offset) =>
        (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];
}
