// 账户头像：皮肤纹理 → 一张方形「头 + 帽子层」头像。
//
// 账户挂了启动器皮肤之后，头像不能直接拿 64×64 纹理去填方框：用户看到的是头、身子、
// 胳膊挤在一起的一团色块（原先就是这样）。玩家认的是"这个角色长什么样"，所以头像走与
// 皮肤卡片同一条光栅化路径，只画头部（HeadOnly），正对镜头出图。
//
// 尺寸固定 128：首页 72dip 的大头像放大后仍然清晰，列表里的小头像同源复用同一张图。
// 取景是满幅的（脸贴四边，见 Distance）：圆角、底色由界面那层方框给，图本身不留白。
// 输出 PNG 是直通 alpha（光栅化器给的是预乘 BGRA，这里反预乘后再编码）。

namespace Starcloud.Core.Rendering;

public static class SkinHeadImage
{
    /// <summary>头像边长（像素）。</summary>
    public const int Size = 128;

    /// <summary>
    /// 镜头距离：头部正面**刚好铺满整张画布**（128px 时实测 d = 19 恰好触及四边）。
    /// <para>不给四周留白是刻意的：头像在界面上是塞进一个带底色的圆角方框（列表 40px、
    /// 详情页 52px），留了白就会在头脸外露出一圈底色，看着像"头像没铺满"。
    /// 现在脸贴到四边，圆角由外面的框剪裁给出——与远程头像（minotar 的 helm）同一观感。</para>
    /// </summary>
    public const double Distance = 19;

    /// <summary>
    /// 俯角。取 0 是刻意的：带俯角时头会整体下沉（镜头绕头心转，顶上多出来的高度挤在
    /// 画面下半边），在圆角方框里看着"头没坐正"；平视则是正好居中的一张正面头像。
    /// </summary>
    private const double PitchDeg = 0;

    /// <summary>
    /// 皮肤 PNG 字节 → 头像 PNG 字节；尺寸非法 / 解码失败抛异常，由调用方兜底。
    /// 镜头参数有默认值（调用方一般不用管），留出来是为了能直接渲染不同取景做对比。
    /// </summary>
    public static byte[] Encode(byte[] skinPng, int size = Size,
        double distance = Distance, double pitchDeg = PitchDeg)
    {
        if (size <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var texture = PngDecoder.Decode(skinPng);
        // 版型只影响手臂，头部两种版型一样，这里固定 classic。
        var model = new SkinModel(texture, slim: false);
        var frame = SkinRenderer.Render(
            model, new RenderOptions(new Camera(0, pitchDeg, distance), HeadOnly: true), size, size);
        return PngEncoder.Encode(frame.Width, frame.Height, ToStraightRgba(frame.Bgra));
    }

    /// <summary>预乘 BGRA → 直通 RGBA（PNG 的颜色分量不预乘）</summary>
    private static byte[] ToStraightRgba(byte[] bgra)
    {
        var rgba = new byte[bgra.Length];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            var a = bgra[i + 3];
            rgba[i] = Unpremultiply(bgra[i + 2], a);
            rgba[i + 1] = Unpremultiply(bgra[i + 1], a);
            rgba[i + 2] = Unpremultiply(bgra[i], a);
            rgba[i + 3] = a;
        }
        return rgba;
    }

    private static byte Unpremultiply(byte value, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, value * 255 / alpha);
}
