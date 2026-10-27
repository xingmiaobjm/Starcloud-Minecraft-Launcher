// 披风渲染自检：把贴图的每个标准块涂成互不相同的纯色，渲出来一眼看出
// 「画面上这块颜色 = 贴图的哪一块」，不再靠肉眼猜朝向。
//
// 这不是产品代码，是回归夹具：披风的贴图块分配、几何位置、相机朝向三者的关系
// 用一张图说清，以后改披风时跑一遍就知道有没有改错。

using System.Text.Json;
using Starcloud.Core.Rendering;
using Xunit;

namespace Starcloud.Core.Tests;

public class CapeRenderTests
{
    /// <summary>64×64 贴图：(1,1)=红(正面) (12,1)=蓝(背面) (23,1)=绿 (34,1)=黄。</summary>
    private static byte[] MakeCapeTexture()
    {
        var rgba = new byte[64 * 64 * 4];
        void Fill(int x0, int y0, int w, int h, byte r, byte g, byte b)
        {
            for (var y = y0; y < y0 + h; y++)
                for (var x = x0; x < x0 + w; x++)
                {
                    var i = (y * 64 + x) * 4;
                    rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
                }
        }
        Fill(1, 1, 10, 16, 255, 0, 0);     // 正面块 = 红
        Fill(12, 1, 10, 16, 0, 0, 255);    // 背面块 = 蓝
        Fill(23, 1, 10, 16, 0, 255, 0);    // 其它块 = 绿
        Fill(34, 1, 10, 16, 255, 255, 0);  // 其它块 = 黄
        return PngEncoder.Encode(64, 64, rgba);
    }

    /// <summary>64×64 皮肤：身体展开区 (16,16)-(40,32) 全涂洋红，和披风的四个颜色都不撞。</summary>
    private static byte[] MakeSkinTexture()
    {
        var rgba = new byte[64 * 64 * 4];
        // 只填"原点矩形"是不够的：每个面的 UV 在展开图里各不相同，
        // 必须把整个 24×16 的身体展开区涂满，否则大部分面采样到透明。
        for (var y = 16; y < 32; y++)
            for (var x = 16; x < 40; x++)
            {
                var i = (y * 64 + x) * 4;
                rgba[i] = 255; rgba[i + 1] = 0; rgba[i + 2] = 255; rgba[i + 3] = 255;
            }
        return PngEncoder.Encode(64, 64, rgba);
    }

    /// <summary>
    /// 渲染帧是预乘 BGRA，PngEncoder 收直通 RGBA。直接喂进去会 B/R 互换、
    /// 颜色统计全反——这个夹具以前就栽在这上面，现在统一转一次。
    /// </summary>
    private static byte[] ToRgba(BitmapFrame frame)
    {
        var rgba = new byte[frame.Bgra.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var a = frame.Bgra[i + 3];
            rgba[i] = Unpremultiply(frame.Bgra[i + 2], a);
            rgba[i + 1] = Unpremultiply(frame.Bgra[i + 1], a);
            rgba[i + 2] = Unpremultiply(frame.Bgra[i], a);
            rgba[i + 3] = a;
        }
        return rgba;
    }

    private static byte Unpremultiply(byte value, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, value * 255 / alpha);

    private static (int red, int blue, int green, int yellow, int magenta) Count(string path)
    {
        var decoded = PngDecoder.Decode(File.ReadAllBytes(path));
        int r = 0, b = 0, g = 0, y = 0, m = 0;
        for (var i = 0; i < decoded.Rgba.Length; i += 4)
        {
            var (rr, gg, bb, aa) = (decoded.Rgba[i], decoded.Rgba[i + 1], decoded.Rgba[i + 2], decoded.Rgba[i + 3]);
            if (aa < 128) continue;
            if (rr > 140 && gg < 90 && bb < 90) r++;
            else if (bb > 140 && rr < 90 && gg < 90) b++;
            else if (gg > 140 && rr < 90 && bb < 90) g++;
            else if (rr > 140 && gg > 140 && bb < 90) y++;
            else if (rr > 140 && bb > 140 && gg < 90) m++;
        }
        return (r, b, g, y, m);
    }

    /// <summary>缩略图回归：HD（128×64）披风只能取正面块，且颜色分量不能被对调。</summary>
    [Fact]
    public void 披风缩略图_HD_只取正面()
    {
        foreach (var k in new[] { 1, 2 })
        {
            var w = 64 * k;
            var h = 32 * k;
            var rgba = new byte[w * h * 4];
            void Fill(int x0, int y0, int cw, int ch, byte r, byte g, byte b)
            {
                for (var y = y0; y < y0 + ch; y++)
                    for (var x = x0; x < x0 + cw; x++)
                    {
                        var i = (y * w + x) * 4;
                        rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
                    }
            }
            Fill(1 * k, 1 * k, 10 * k, 16 * k, 255, 0, 0);    // 正面块 = 红
            Fill(12 * k, 1 * k, 10 * k, 16 * k, 0, 0, 255);   // 背面块 = 蓝

            var thumb = PngDecoder.Decode(CapeThumbImage.Encode(PngEncoder.Encode(w, h, rgba)));
            Assert.Equal(80, thumb.Width);
            Assert.Equal(128, thumb.Height);
            var c = (64 * 80 + 40) * 4;   // 正中心
            Assert.True(thumb.Rgba[c] > 200 && thumb.Rgba[c + 1] < 60 && thumb.Rgba[c + 2] < 60,
                $"k={k} 中心应为红，实际 R={thumb.Rgba[c]} G={thumb.Rgba[c + 1]} B={thumb.Rgba[c + 2]}");
        }
    }

    [Fact]
    public void 披风渲染_各视角的颜色分布()
    {
        var skin = PngDecoder.Decode(MakeSkinTexture());
        var cape = PngDecoder.Decode(MakeCapeTexture());
        var model = new SkinModel(skin, slim: false, cape);

        var dir = Path.Combine(Path.GetTempPath(), "sc-cape-render");
        Directory.CreateDirectory(dir);

        var results = new Dictionary<string, object>();
        var counts = new Dictionary<int, (int Red, int Blue)>();
        foreach (var yaw in new[] { 0, 90, 180, 270 })
        {
            var frame = SkinRenderer.Render(model,
                new RenderOptions(new Camera(yaw, 0, 96)), 256, 320, Pose.Still);
            var file = Path.Combine(dir, $"yaw{yaw}.png");
            File.WriteAllBytes(file, PngEncoder.Encode(256, 320, ToRgba(frame)));
            var (r, b, g, y, m) = Count(file);
            counts[yaw] = (r, b);
            results[$"yaw{yaw}"] = new { red = r, blue = b, green = g, yellow = y, magenta_body = m };
        }

        var capeCube = model.Cubes.First(c => c.Part == "cape");
        results["frontFaceUv"] = capeCube.Faces[(int)CubeFace.Front].ToString();
        results["backFaceUv"] = capeCube.Faces[(int)CubeFace.Back].ToString();
        results["z"] = $"{capeCube.Min.Z}..{capeCube.Max.Z}";
        File.WriteAllText(Path.Combine(dir, "info.json"),
            JsonSerializer.Serialize(results, new JsonSerializerOptions { WriteIndented = true }));

        Assert.True(File.Exists(Path.Combine(dir, "yaw0.png")));
        // 相机 yaw=180 在 -Z 侧（玩家背后），看到的是披风 Back 面，必须是贴图正面（红）
        Assert.True(counts[180].Red > counts[180].Blue,
            $"背后应看到贴图正面（红），实际 red={counts[180].Red} blue={counts[180].Blue}");
        // yaw=0 从 +Z 侧看：披风内衬朝相机，且被身体挡住，应看到背面块（蓝）
        Assert.True(counts[0].Blue > counts[0].Red,
            $"正面应看到内衬（蓝），实际 red={counts[0].Red} blue={counts[0].Blue}");
        // 侧面：有厚度才看得到披风的窄边（红/蓝都行，只要有披风像素）
        Assert.True(counts[90].Red + counts[90].Blue > 0, "侧面应能看到披风厚度");
        Assert.True(counts[270].Red + counts[270].Blue > 0, "侧面应能看到披风厚度");
    }
}
