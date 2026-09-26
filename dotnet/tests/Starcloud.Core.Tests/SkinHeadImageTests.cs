// 账户头像渲染测试。
//
// 背景：账户挂了启动器皮肤后，头像一度是"直接把 64×64 纹理塞进方框"，屏幕上出来的是头、
// 身子、胳膊挤在一起的一团色块。头像改为走光栅化器只画头部之后，这里钉住三件事：
//   1. 出图是方形、头部居中、四周留边（不顶边、不偏移）；
//   2. 画的是头（头部正面颜色出现在画面中心），不是顺手把身子也画进去；
//   3. 纹理不合法时抛异常，交给调用方兜底（界面退回首字母色块）。

using Starcloud.Core.Rendering;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class SkinHeadImageTests
{
    [Fact]
    public void 头像_铺满整张画布()
    {
        var head = PngDecoder.Decode(SkinHeadImage.Encode(SkinBytes()));

        Assert.Equal(SkinHeadImage.Size, head.Width);
        Assert.Equal(SkinHeadImage.Size, head.Height);

        // 满幅取景：脸贴到四边（界面上的圆角与底色由外面那层方框给，图本身不留白）
        var (minX, maxX, minY, maxY) = Bounds(head);
        var w = maxX - minX + 1;
        var h = maxY - minY + 1;
        Assert.InRange(w, SkinHeadImage.Size - 1, SkinHeadImage.Size);
        Assert.InRange(h, SkinHeadImage.Size - 1, SkinHeadImage.Size);
        Assert.True(Math.Abs(minX - (head.Width - 1 - maxX)) <= 1, "左右留边应该对称");
        Assert.True(Math.Abs(minY - (head.Height - 1 - maxY)) <= 1, "上下留边应该对称");
    }

    [Fact]
    public void 头像_画的是头不是身子()
    {
        // 造一张"头绿身红"的皮肤：画面中心必须是头（绿），出现红就说明把身子也画进来了。
        const int size = 64;
        var rgba = new byte[size * size * 4];
        Fill(8, 8, 8, 8, 0, 220, 0);      // 头部正面 (8,8)-(15,15)
        Fill(20, 20, 8, 12, 220, 0, 0);   // 躯干正面
        Fill(44, 20, 4, 12, 0, 0, 220);   // 右腿正面

        var head = PngDecoder.Decode(SkinHeadImage.Encode(PngEncoder.Encode(size, size, rgba)));
        var center = ((head.Height / 2) * head.Width + head.Width / 2) * 4;

        Assert.Equal(255, head.Rgba[center + 3]);
        Assert.True(head.Rgba[center + 1] > 180, "画面中心应该是头部正面的颜色");
        Assert.True(head.Rgba[center] < 80);

        void Fill(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            for (var yy = y; yy < y + h; yy++)
                for (var xx = x; xx < x + w; xx++)
                {
                    var i = (yy * size + xx) * 4;
                    rgba[i] = r; rgba[i + 1] = g; rgba[i + 2] = b; rgba[i + 3] = 255;
                }
        }
    }

    [Fact]
    public void 头像_尺寸不合法抛异常()
    {
        Assert.Throws<SkinTextureException>(
            () => SkinHeadImage.Encode(PngEncoder.Encode(32, 32, new byte[32 * 32 * 4])));
    }

    /// <summary>官方默认皮肤里的第一张（64×64 合法纹理）</summary>
    private static byte[] SkinBytes()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName, "src", "Starcloud.App",
                SkinPresets.AssetDir.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate))
                return File.ReadAllBytes(Path.Combine(candidate, SkinPresets.All[0].File));
            dir = dir.Parent;
        }
        throw new InvalidOperationException("找不到默认皮肤资产目录");
    }

    private static (int MinX, int MaxX, int MinY, int MaxY) Bounds(SkinBitmap bmp)
    {
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (var y = 0; y < bmp.Height; y++)
            for (var x = 0; x < bmp.Width; x++)
            {
                if (bmp.Rgba[(y * bmp.Width + x) * 4 + 3] == 0) continue;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        return (minX, maxX, minY, maxY);
    }
}
