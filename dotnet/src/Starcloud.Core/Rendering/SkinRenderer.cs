// 软件光栅化渲染器（设计：docs/designs/2026-09-12-skin-viewer-design.md §3.4）
//
// 为什么自己写而不是把模型丢给 WebView/GPU：项目明确拒绝 WebView 假原生（DESIGN.md §1），
// 而模型只有 80 个面左右，软件光栅化在 256×384 上是毫秒级——换来一条零依赖、
// 可单测、能跑在 Core 里的渲染路径。
//
// 关键选择：
//   · 透视投影 + 透视校正 UV（近处的面纹理不会糊或漂）
//   · 逐像素 Z-buffer，**不做背面剔除**：面数极少，Z-buffer 处理披风内衬与覆盖层内面更稳
//   · 最近邻采样：Minecraft 本来就是像素风，插值反而毁质感
//   · 内部按直通 alpha 混合，输出前统一预乘（BGRA8 预乘可直接送 SoftwareBitmap）

namespace Starcloud.Core.Rendering;

/// <summary>相机：绕模型中心 (y≈16) 的球坐标（角度制）</summary>
public sealed record Camera(double YawDeg, double PitchDeg, double Distance, double FovDeg = 30);

/// <summary>渲染选项（HeadOnly = 只画头 + 帽子层，用于 40px 头像）</summary>
public sealed record RenderOptions(Camera Camera, bool HeadOnly = false);

/// <summary>渲染结果：BGRA8 预乘 alpha</summary>
public sealed record BitmapFrame(byte[] Bgra, int Width, int Height);

public static class SkinRenderer
{
    /// <summary>渲染一帧。pose 为空时用静止姿态（走路动画由调用方逐帧喂相位）。</summary>
    public static BitmapFrame Render(SkinModel skin, RenderOptions opts, int width, int height,
        Pose? pose = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        var quads = PlayerModel.BuildQuads(skin, pose ?? Pose.Still, opts.HeadOnly ? "head" : null);

        var color = new double[width * height * 4];   // 直通 alpha 的颜色缓冲
        var depth = new float[width * height];        // 相机空间 z，越小越近
        Array.Fill(depth, float.MaxValue);

        var cam = opts.Camera;
        var yaw = cam.YawDeg * Math.PI / 180.0;
        var pitch = cam.PitchDeg * Math.PI / 180.0;
        var focal = height / 2.0 / Math.Tan(cam.FovDeg * Math.PI / 360.0);
        var centerY = opts.HeadOnly ? 28.0 : 16.0;    // 只看头时把镜头对准头部中心

        foreach (var quad in quads)
        {
            var v = new Vertex[4];
            var visible = true;
            for (var i = 0; i < 4; i++)
            {
                v[i] = Project(quad, i, centerY, yaw, pitch, cam.Distance, focal, width, height);
                if (v[i].Z <= 0.05) visible = false; // 有顶点跑到相机后面：整片跳过（模型很小，够用）
            }
            if (!visible) continue;
            Rasterize(color, depth, width, height, v[0], v[1], v[2], skin.Texture);
            Rasterize(color, depth, width, height, v[0], v[2], v[3], skin.Texture);
        }

        return new BitmapFrame(PremultiplyBgra(color, width, height), width, height);
    }

    private readonly record struct Vertex(
        double Sx, double Sy, double Z, double U, double V);

    /// <summary>取面片第 i 个顶点：绕模型中心旋转 → 透视投影，并带上对应的纹理坐标</summary>
    private static Vertex Project(Quad quad, int corner, double centerY, double yaw, double pitch,
        double distance, double focal, int width, int height)
    {
        var p = quad.Corner(corner);

        // 1) 平移到模型竖直中心
        var x = p.X;
        var y = p.Y - centerY;
        var z = p.Z;
        // 2) 绕 Y 轴（yaw）：+yaw 让模型向右转
        var cy = Math.Cos(yaw);
        var sy = Math.Sin(yaw);
        var x1 = x * cy + z * sy;
        var z1 = -x * sy + z * cy;
        // 3) 绕 X 轴（pitch）
        var cp = Math.Cos(pitch);
        var sp = Math.Sin(pitch);
        var y1 = y * cp - z1 * sp;
        var z2 = y * sp + z1 * cp;
        // 4) 放到相机前方：相机在 +Z 侧的 distance 处、朝 -Z 看，
        //    这样模型正面（+Z）朝相机（用 z2 + distance 会把相机放到 -Z 侧，看到的是背面）
        var zc = distance - z2;

        // 纹理坐标：UV 矩形按 左上 / 右上 / 右下 / 左下 对应顶点 0..3
        var u0 = quad.Uv.X;
        var u1 = quad.Uv.Right;
        var v0 = quad.Uv.Y;
        var v1 = quad.Uv.Bottom;
        double u = corner switch { 0 or 3 => u0, _ => u1 };
        double vv = corner switch { 0 or 1 => v0, _ => v1 };
        if (quad.MirrorU) u = quad.Uv.X + quad.Uv.Right - u;

        return new Vertex(
            width / 2.0 + x1 * focal / zc,
            height / 2.0 - y1 * focal / zc,
            zc, u, vv);
    }

    /// <summary>重心坐标光栅化 + 透视校正 UV + 逐像素 Z 测试 + 直通 alpha 混合</summary>
    private static void Rasterize(double[] color, float[] depth, int width, int height,
        Vertex a, Vertex b, Vertex c, SkinBitmap texture)
    {
        var minX = (int)Math.Floor(Math.Min(a.Sx, Math.Min(b.Sx, c.Sx)));
        var maxX = (int)Math.Ceiling(Math.Max(a.Sx, Math.Max(b.Sx, c.Sx)));
        var minY = (int)Math.Floor(Math.Min(a.Sy, Math.Min(b.Sy, c.Sy)));
        var maxY = (int)Math.Ceiling(Math.Max(a.Sy, Math.Max(b.Sy, c.Sy)));
        if (maxX < 0 || minX >= width || maxY < 0 || minY >= height) return;
        minX = Math.Max(minX, 0); maxX = Math.Min(maxX, width - 1);
        minY = Math.Max(minY, 0); maxY = Math.Min(maxY, height - 1);

        var area = (b.Sx - a.Sx) * (c.Sy - a.Sy) - (c.Sx - a.Sx) * (b.Sy - a.Sy);
        if (Math.Abs(area) < 1e-9) return;
        var invArea = 1.0 / area;
        Span<byte> texel = stackalloc byte[4];

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5;
            for (var x = minX; x <= maxX; x++)
            {
                var px = x + 0.5;
                var w0 = ((b.Sx - px) * (c.Sy - py) - (c.Sx - px) * (b.Sy - py)) * invArea;
                var w1 = ((c.Sx - px) * (a.Sy - py) - (a.Sx - px) * (c.Sy - py)) * invArea;
                var w2 = 1 - w0 - w1;
                if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6) continue;

                var invZ = w0 / a.Z + w1 / b.Z + w2 / c.Z;
                if (invZ <= 0) continue;
                var z = 1.0 / invZ;
                var idx = y * width + x;
                if (z >= depth[idx]) continue;

                var u = (w0 * a.U / a.Z + w1 * b.U / b.Z + w2 * c.U / c.Z) / invZ;
                var v = (w0 * a.V / a.Z + w1 * b.V / b.Z + w2 * c.V / c.Z) / invZ;
                texture.Sample((int)Math.Floor(u), (int)Math.Floor(v), texel);
                var ta = texel[3] / 255.0;
                if (ta <= 0) continue;

                var o = idx * 4;
                var dstA = color[o + 3];
                var outA = ta + dstA * (1 - ta);
                if (outA <= 0) continue;
                color[o] = (texel[0] / 255.0 * ta + color[o] * dstA * (1 - ta)) / outA;
                color[o + 1] = (texel[1] / 255.0 * ta + color[o + 1] * dstA * (1 - ta)) / outA;
                color[o + 2] = (texel[2] / 255.0 * ta + color[o + 2] * dstA * (1 - ta)) / outA;
                color[o + 3] = outA;
                depth[idx] = (float)z;
            }
        }
    }

    /// <summary>直通 alpha → 预乘 BGRA8</summary>
    private static byte[] PremultiplyBgra(double[] color, int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < width * height; i++)
        {
            var o = i * 4;
            var a = Math.Clamp(color[o + 3], 0, 1);
            bgra[o] = ToByte(Math.Clamp(color[o + 2], 0, 1) * a);
            bgra[o + 1] = ToByte(Math.Clamp(color[o + 1], 0, 1) * a);
            bgra[o + 2] = ToByte(Math.Clamp(color[o], 0, 1) * a);
            bgra[o + 3] = ToByte(a);
        }
        return bgra;
    }

    private static byte ToByte(double v) => (byte)Math.Clamp(Math.Round(v * 255), 0, 255);
}
