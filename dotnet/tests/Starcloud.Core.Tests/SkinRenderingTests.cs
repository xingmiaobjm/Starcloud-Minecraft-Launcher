// 皮肤渲染栈单元测试（设计：docs/designs/2026-09-12-skin-viewer-design.md §7）
//
// 覆盖：PNG 解码（含各颜色类型与五种行滤波）、立方体 UV 展开公式、覆盖层剔除、
// 老版 64×32 的镜像回退、走路姿态的确定性与相位对称、渲染输出的基本不变量。

using System.IO.Compression;
using Starcloud.Core.Rendering;
using Xunit;

namespace Starcloud.Core.Tests;

public class SkinRenderingTests
{
    // ── 构造测试用 PNG（不引第三方编码器：自己按规范拼块） ──

    private static byte[] Png(int w, int h, byte colorType, byte bitDepth, byte[][] rows,
        byte[]? palette = null, byte[]? trns = null)
    {
        var channels = colorType switch { 0 => 1, 2 => 3, 3 => 1, 4 => 2, _ => 4 };
        using var raw = new MemoryStream();
        foreach (var row in rows)
        {
            raw.WriteByte(0);             // filter = None
            raw.Write(row, 0, w * channels);
        }
        raw.Position = 0;
        using var comp = new MemoryStream();
        using (var z = new ZLibStream(comp, CompressionLevel.Optimal, leaveOpen: true)) raw.CopyTo(z);

        using var ms = new MemoryStream();
        ms.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var ihdr = new byte[13];
        WriteInt(ihdr, 0, w); WriteInt(ihdr, 4, h);
        ihdr[8] = bitDepth; ihdr[9] = colorType;
        Chunk(ms, "IHDR", ihdr);
        if (palette is not null) Chunk(ms, "PLTE", palette);
        if (trns is not null) Chunk(ms, "tRNS", trns);
        Chunk(ms, "IDAT", comp.ToArray());
        Chunk(ms, "IEND", []);
        return ms.ToArray();
    }

    private static void Chunk(Stream s, string type, byte[] data)
    {
        var len = new byte[4];
        WriteInt(len, 0, data.Length);
        s.Write(len);
        var t = System.Text.Encoding.ASCII.GetBytes(type);
        s.Write(t);
        s.Write(data);
        s.Write(new byte[4]); // CRC 不校验
    }

    private static void WriteInt(byte[] b, int offset, int v)
    {
        b[offset] = (byte)(v >> 24); b[offset + 1] = (byte)(v >> 16);
        b[offset + 2] = (byte)(v >> 8); b[offset + 3] = (byte)v;
    }

    private static byte[] Rgba(byte r, byte g, byte b, byte a) => [r, g, b, a];

    [Fact]
    public void 解码_RGBA_像素与alpha都保留()
    {
        // 2×1 的 RGBA 图：一行 8 字节
        var png = Png(2, 1, 6, 8, [[10, 20, 30, 40, 50, 60, 70, 80]]);
        var bmp = PngDecoder.Decode(png);

        Assert.Equal(2, bmp.Width);
        Assert.Equal(1, bmp.Height);
        Assert.Equal([10, 20, 30, 40, 50, 60, 70, 80], bmp.Rgba);
    }

    [Fact]
    public void 解码_灰度与调色板都能出RGBA()
    {
        var gray = PngDecoder.Decode(Png(1, 1, 0, 8, [[200]]));
        Assert.Equal([200, 200, 200, 255], gray.Rgba);

        var palettePng = Png(2, 1, 3, 8, [[0, 1]], palette: [9, 8, 7, 1, 2, 3], trns: [128]);
        var pal = PngDecoder.Decode(palettePng);
        Assert.Equal([9, 8, 7, 128, 1, 2, 3, 255], pal.Rgba);
    }

    [Fact]
    public void 解码_损坏数据抛SkinTextureException()
    {
        Assert.Throws<SkinTextureException>(() => PngDecoder.Decode([1, 2, 3, 4]));
        Assert.Throws<SkinTextureException>(() => PngDecoder.Decode(
            [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0]));
    }

    [Fact]
    public void 区域全透明判定_用于覆盖层剔面()
    {
        var png = Png(2, 1, 6, 8, [[0, 0, 0, 0, 1, 2, 3, 255]]);
        var bmp = PngDecoder.Decode(png);

        Assert.True(bmp.IsRegionFullyTransparent(0, 0, 1, 1));
        Assert.False(bmp.IsRegionFullyTransparent(0, 0, 2, 1));
        Assert.False(bmp.IsRegionFullyTransparent(1, 0, 1, 1));
    }

    [Fact]
    public void 六面UV_按Minecraft展开公式()
    {
        // 头：原点 (0,0)、尺寸 8,8,8
        Assert.Equal(new RectI(8, 0, 8, 8), SkinModel.FaceUv(0, 0, 8, 8, 8, CubeFace.Top));
        Assert.Equal(new RectI(16, 0, 8, 8), SkinModel.FaceUv(0, 0, 8, 8, 8, CubeFace.Bottom));
        Assert.Equal(new RectI(0, 8, 8, 8), SkinModel.FaceUv(0, 0, 8, 8, 8, CubeFace.Right));
        Assert.Equal(new RectI(8, 8, 8, 8), SkinModel.FaceUv(0, 0, 8, 8, 8, CubeFace.Front));
        Assert.Equal(new RectI(16, 8, 8, 8), SkinModel.FaceUv(0, 0, 8, 8, 8, CubeFace.Left));
        Assert.Equal(new RectI(24, 8, 8, 8), SkinModel.FaceUv(0, 0, 8, 8, 8, CubeFace.Back));
    }

    [Fact]
    public void 六面UV_HD纹理按整数倍放大()
    {
        // 128×128 皮肤（2 倍）：头正面从 (8,8,8,8) 变成 (16,16,16,16)
        Assert.Equal(new RectI(16, 16, 16, 16), SkinModel.FaceUv(0, 0, 8, 8, 8, CubeFace.Front, 2));
    }

    // ── 皮肤模型 ──

    private static SkinBitmap BlankSkin(int size = 64, bool withOverlay = false)
    {
        var rgba = new byte[size * size * 4];
        if (withOverlay)
        {
            // 头覆盖层的正面 (40,8,8,8)：填成不透明，确保覆盖层立方体会建出来
            //（注意 (32,0,8,8) 是展开图里未使用的角落，填它不会有任何效果）
            for (var y = 8; y < 16; y++)
                for (var x = 40; x < 48; x++)
                    rgba[(y * size + x) * 4 + 3] = 255;
        }
        return new SkinBitmap(size, size, rgba);
    }

    [Fact]
    public void 模型_基础层六个部件齐备且各六面()
    {
        var model = new SkinModel(BlankSkin(), slim: false);
        var parts = model.Cubes.Select(c => c.Part).ToList();

        foreach (var name in new[] { "head", "body", "rightArm", "leftArm", "rightLeg", "leftLeg" })
        {
            var cube = model.Cubes.Single(c => c.Part == name);
            Assert.Equal(6, cube.Faces.Count(f => f.W > 0));
            Assert.Contains(name, parts);
        }
        Assert.False(model.Slim);
        Assert.False(model.Legacy);
    }

    [Fact]
    public void 模型_全透明的覆盖层不建立方体()
    {
        var model = new SkinModel(BlankSkin(), slim: false);
        Assert.DoesNotContain(model.Cubes, c => c.Part == "head_overlay");
    }

    [Fact]
    public void 模型_有内容的覆盖层会被建出来并外扩半像素()
    {
        var model = new SkinModel(BlankSkin(withOverlay: true), slim: false);
        var overlay = model.Cubes.Single(c => c.Part == "head_overlay");
        var head = model.Cubes.Single(c => c.Part == "head");

        Assert.Equal(head.Min.X - 0.5, overlay.Min.X);
        Assert.Equal(head.Max.Y + 0.5, overlay.Max.Y);
        Assert.True(overlay.Faces.Count(f => f.W > 0) is > 0 and <= 6);
    }

    [Fact]
    public void 模型_slim细臂把手臂收窄一像素()
    {
        var wide = new SkinModel(BlankSkin(), slim: false).Cubes.Single(c => c.Part == "rightArm");
        var thin = new SkinModel(BlankSkin(), slim: true).Cubes.Single(c => c.Part == "rightArm");

        Assert.Equal(4, wide.Max.X - wide.Min.X);
        Assert.Equal(3, thin.Max.X - thin.Min.X);
    }

    [Fact]
    public void 模型_老版64x32无覆盖层且左肢镜像右肢()
    {
        var legacy = new SkinBitmap(64, 32, new byte[64 * 32 * 4]);
        var model = new SkinModel(legacy, slim: false);

        Assert.True(model.Legacy);
        Assert.DoesNotContain(model.Cubes, c => c.Part.EndsWith("_overlay"));
        Assert.True(model.Cubes.Single(c => c.Part == "leftArm").MirrorU);
        Assert.False(model.Cubes.Single(c => c.Part == "rightArm").MirrorU);
    }

    [Fact]
    public void 模型_尺寸不合法抛异常()
    {
        Assert.Throws<SkinTextureException>(() =>
            new SkinModel(new SkinBitmap(50, 50, new byte[50 * 50 * 4]), false));
    }

    // ── 走路动画 ──

    [Fact]
    public void 走路_左右腿反相且周期为2π()
    {
        var a = WalkAnimator.FromPhase(0.7);
        Assert.Equal(a.RightLegX, -a.LeftLegX, 10);
        var b = WalkAnimator.FromPhase(0.7 + 2 * Math.PI);
        Assert.Equal(a.RightLegX, b.RightLegX, 10);
        Assert.Equal(a.CapeX, b.CapeX, 10);
    }

    [Fact]
    public void 走路_相位为0时是站姿()
    {
        var p = WalkAnimator.FromPhase(0);
        Assert.Equal(0, p.RightLegX, 10);
        Assert.Equal(0, p.LeftArmX, 10);
        Assert.Equal(0, p.BodyOffsetY, 10);
    }

    [Fact]
    public void 走路_身体只向下沉不向上浮()
    {
        for (var phase = 0.0; phase < 6.3; phase += 0.37)
            Assert.True(WalkAnimator.FromPhase(phase).BodyOffsetY <= 0);
    }

    // ── 渲染 ──

    [Fact]
    public void 渲染_不透明皮肤会画出像素且背景透明()
    {
        // 把整张皮肤填成不透明红，模型就应该是实心的
        var size = 64;
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            rgba[i * 4] = 255; rgba[i * 4 + 3] = 255;
        }
        var model = new SkinModel(new SkinBitmap(size, size, rgba), slim: false);

        var frame = SkinRenderer.Render(model, new RenderOptions(new Camera(0, 0, 72)), 128, 192);
        Assert.Equal(128, frame.Width);
        Assert.Equal(192, frame.Height);
        Assert.Equal(128 * 192 * 4, frame.Bgra.Length);

        var opaque = 0;
        for (var i = 3; i < frame.Bgra.Length; i += 4) if (frame.Bgra[i] > 200) opaque++;
        Assert.True(opaque > 1000, $"模型应该画出可见像素，实际 {opaque}");

        // 四角一定是透明的（模型没铺满整画布）
        foreach (var (x, y) in new[] { (0, 0), (127, 0), (0, 191), (127, 191) })
            Assert.Equal(0, frame.Bgra[(y * 128 + x) * 4 + 3]);
    }

    [Fact]
    public void 渲染_预乘alpha不超过alpha通道()
    {
        var size = 64;
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            rgba[i * 4] = 200; rgba[i * 4 + 1] = 100; rgba[i * 4 + 2] = 50; rgba[i * 4 + 3] = 255;
        }
        var model = new SkinModel(new SkinBitmap(size, size, rgba), slim: false);
        var frame = SkinRenderer.Render(model, new RenderOptions(new Camera(20, 8, 72)), 128, 192);

        for (var i = 0; i < frame.Bgra.Length; i += 4)
            for (var c = 0; c < 3; c++)
                Assert.True(frame.Bgra[i + c] <= frame.Bgra[i + 3]);
    }

    [Fact]
    public void 渲染_HeadOnly只画头()
    {
        var size = 64;
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++) rgba[i * 4 + 3] = 255;
        var model = new SkinModel(new SkinBitmap(size, size, rgba), slim: false);

        var full = SkinRenderer.Render(model, new RenderOptions(new Camera(0, 0, 72)), 96, 96);
        var head = SkinRenderer.Render(model, new RenderOptions(new Camera(0, 0, 72), HeadOnly: true), 96, 96);

        static int Opaque(BitmapFrame f)
        {
            var n = 0;
            for (var i = 3; i < f.Bgra.Length; i += 4) if (f.Bgra[i] > 200) n++;
            return n;
        }
        Assert.True(Opaque(head) > 0);
        Assert.True(Opaque(head) < Opaque(full), "只画头应该比整身像素少");
    }

    [Fact]
    public void 渲染_轮廓边缘只采面片自己的纹素()
    {
        // 回归：轮廓边缘会被"保守填充"（三角形外半像素也着色），插值出的 UV 会越过面片
        // 矩形。修之前那些像素直接采到图集里相邻部件的纹素，模型边缘因此出现一条杂色
        // 描边（读到 alpha=0 时还断成虚线）。
        //
        // 造一块"陷阱皮肤"：全图填洋红，再把所有面片的 UV 矩形涂绿 ——
        // 输出里只要出现洋红，就说明采样跑出了面片矩形。
        const int size = 64;
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            rgba[i * 4] = 255; rgba[i * 4 + 1] = 0; rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = 255;
        }

        // 每个面片（含覆盖层）的矩形都涂成绿色。纹理与 SkinBitmap 共用同一个数组，
        // 所以先建模型枚举面片、再改颜色即可。
        var model = new SkinModel(new SkinBitmap(size, size, rgba), slim: false);
        foreach (var cube in model.Cubes)
            foreach (var face in cube.Faces)
                for (var y = face.Y; y < face.Bottom; y++)
                    for (var x = face.X; x < face.Right; x++)
                    {
                        var o = (y * size + x) * 4;
                        rgba[o] = 0; rgba[o + 1] = 255; rgba[o + 2] = 0;
                    }

        foreach (var (yaw, pitch) in new[] { (24.0, 6.0), (0.0, 0.0), (-40.0, 25.0), (100.0, -30.0) })
        {
            var frame = SkinRenderer.Render(
                model, new RenderOptions(new Camera(yaw, pitch, 96.0 / 1.4)), 300, 600);

            for (var y = 0; y < frame.Height; y++)
                for (var x = 0; x < frame.Width; x++)
                {
                    var o = (y * frame.Width + x) * 4;
                    var a = frame.Bgra[o + 3];
                    if (a == 0) continue;
                    var b = frame.Bgra[o]; var g = frame.Bgra[o + 1]; var r = frame.Bgra[o + 2];
                    Assert.False(r > a / 2 && b > a / 2 && g < a / 2,
                        $"yaw={yaw} pitch={pitch} 的 ({x},{y}) 采到了面片 UV 之外的纹素");
                }
        }
    }
}
