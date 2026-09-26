// 极简 PNG 编码器（与 PngDecoder 对称：零依赖，只用 BCL 的 ZLibStream）
//
// 用途：预置皮肤是**运行时画出来的**（见 SkinPresets），要交给三处使用——
//   1. 皮肤库落盘（必须是一个真 PNG 文件）；
//   2. 本机回环皮肤服务（客户端按 content-type 取图，必须是能解码的 PNG）；
//   3. 同步到正版 / 第三方账号（接口收的就是 PNG 字节）。
// 所以需要一个"写"的方向。
//
// 只做 RGBA8、filter 0（None）、单 IDAT——皮肤是 64×64 的像素画，不值得为通用性堆代码。

using System.IO.Compression;

namespace Starcloud.Core.Rendering;

public static class PngEncoder
{
    private static readonly byte[] Signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    /// <summary>把 RGBA8（直通 alpha）位图编码为 PNG 字节</summary>
    public static byte[] Encode(SkinBitmap bitmap) => Encode(bitmap.Width, bitmap.Height, bitmap.Rgba);

    /// <summary>把 RGBA8（直通 alpha）像素编码为 PNG 字节</summary>
    public static byte[] Encode(int width, int height, byte[] rgba)
    {
        if (width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "尺寸必须为正数");
        if (rgba.Length < width * height * 4)
            throw new ArgumentException("像素数据长度不足", nameof(rgba));

        // 原始扫描行：每行前面一个 filter 字节（0 = None）
        var stride = width * 4;
        var raw = new byte[height * (stride + 1)];
        for (var y = 0; y < height; y++)
        {
            raw[y * (stride + 1)] = 0;
            Array.Copy(rgba, y * stride, raw, y * (stride + 1) + 1, stride);
        }

        var ihdr = new byte[13];
        WriteBigEndian(ihdr, 0, width);
        WriteBigEndian(ihdr, 4, height);
        ihdr[8] = 8;   // bit depth
        ihdr[9] = 6;   // color type: truecolor + alpha
        ihdr[10] = 0;  // compression: deflate
        ihdr[11] = 0;  // filter method: adaptive
        ihdr[12] = 0;  // interlace: none

        var idat = new MemoryStream();
        using (var zlib = new ZLibStream(idat, CompressionLevel.Optimal, leaveOpen: true))
            zlib.Write(raw, 0, raw.Length);

        using var outStream = new MemoryStream();
        outStream.Write(Signature, 0, Signature.Length);
        WriteChunk(outStream, "IHDR", ihdr);
        WriteChunk(outStream, "IDAT", idat.ToArray());
        WriteChunk(outStream, "IEND", []);
        return outStream.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        Span<byte> head = stackalloc byte[8];
        WriteBigEndian(head, 0, data.Length);
        for (var i = 0; i < 4; i++) head[4 + i] = (byte)type[i];
        stream.Write(head);
        stream.Write(data, 0, data.Length);

        var crc = Crc32.Compute(head[4..], data);
        Span<byte> tail = stackalloc byte[4];
        WriteBigEndian(tail, 0, unchecked((int)crc));
        stream.Write(tail);
    }

    private static void WriteBigEndian(Span<byte> buffer, int offset, int value)
    {
        buffer[offset] = (byte)(value >> 24);
        buffer[offset + 1] = (byte)(value >> 16);
        buffer[offset + 2] = (byte)(value >> 8);
        buffer[offset + 3] = (byte)value;
    }

    /// <summary>PNG 块校验用的 CRC-32（IEEE，反射多项式 0xEDB88320）</summary>
    private static class Crc32
    {
        private static readonly uint[] Table = BuildTable();

        private static uint[] BuildTable()
        {
            var table = new uint[256];
            for (uint n = 0; n < 256; n++)
            {
                var c = n;
                for (var k = 0; k < 8; k++)
                    c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
                table[n] = c;
            }
            return table;
        }

        public static uint Compute(ReadOnlySpan<byte> first, ReadOnlySpan<byte> second)
        {
            var crc = 0xFFFFFFFFu;
            foreach (var b in first) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            foreach (var b in second) crc = Table[(crc ^ b) & 0xFF] ^ (crc >> 8);
            return crc ^ 0xFFFFFFFFu;
        }
    }
}
