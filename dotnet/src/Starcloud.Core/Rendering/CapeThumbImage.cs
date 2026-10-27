// 披风缩略图：把披风/鞘翅纹理里"穿戴时可见的正面"裁出来放大。
//
// 两种纹理布局（皮肤站两种都存在，必须按尺寸区分）：
//   · 64×32（高 = 宽/2）——老式披风：正面 x1..11、y1..17；背面 x12..22
//   · 64×64（正方形）——鞘翅：披风正面 (1,1,10,16)、背面 (12,1,10,16)，其余是翅膀
// 取正面那块，最近邻放大到卡片尺寸（整张平铺看不清是什么东西）。
//
// 复用 PngDecoder / PngEncoder（与 SkinHeadImage 同一套零依赖实现）。
// SkinBitmap 是**直通 RGBA**，不是预乘 BGRA——照搬渲染帧的处理会把红蓝对调。

namespace Starcloud.Core.Rendering;

public static class CapeThumbImage
{
    /// <summary>正面区域（64×32 基准坐标）</summary>
    private const int FrontX = 1, FrontY = 1, FrontW = 10, FrontH = 16;

    private const int Scale = 8;

    public static byte[] Encode(byte[] capePng)
    {
        var texture = PngDecoder.Decode(capePng);
        // HD 披风（128×64 / 1280×640…）布局按 64×32 等比放大：裁剪区域跟着宽度缩放
        var unit = texture.Width / 64.0;
        var sx = (int)Math.Round(FrontX * unit);
        var sy = (int)Math.Round(FrontY * unit);
        var sw = Math.Max(1, (int)Math.Round(FrontW * unit));
        var sh = Math.Max(1, (int)Math.Round(FrontH * unit));
        var region = texture.GetRegion(sx, sy, sw, sh);   // RGBA，直通 alpha

        var w = FrontW * Scale;
        var h = FrontH * Scale;
        var rgba = new byte[w * h * 4];
        // 最近邻要按 region 的**实际**宽高做比例映射。HD 披风 region 是 20×32，
        // 目标 80×128：写死 y/8、x/8 只会取到左上四分之一（脸只剩右下角一块黑）。
        for (var y = 0; y < h; y++)
        {
            var srcRow = (y * sh / h) * sw * 4;
            var dstRow = y * w * 4;
            for (var x = 0; x < w; x++)
            {
                var src = srcRow + (x * sw / w) * 4;
                var dst = dstRow + x * 4;
                rgba[dst] = region[src];
                rgba[dst + 1] = region[src + 1];
                rgba[dst + 2] = region[src + 2];
                rgba[dst + 3] = region[src + 3];
            }
        }
        return PngEncoder.Encode(w, h, rgba);
    }
}
