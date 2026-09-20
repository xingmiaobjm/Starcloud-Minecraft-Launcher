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

/// <summary>
/// 可复用的渲染缓冲：走路动画每秒要出 30 帧，逐帧新建 640k 像素的三个大数组
/// （颜色/深度/输出）会持续冲击 LOH，动画一卡一卡。这里按尺寸复用。
/// <para>单线程使用（UI 线程渲染）；返回值里的 Bgra 就是这个缓冲，
/// 同一时刻只有"最新一帧"有效——需要同时持有两帧就各自建一个 target。</para>
/// </summary>
public sealed class SkinRenderTarget
{
    internal int Width, Height;
    internal float[] Color = [];
    internal float[] Depth = [];
    internal byte[] Output = [];

    internal void Ensure(int width, int height)
    {
        if (Width == width && Height == height) return;
        Width = width;
        Height = height;
        var n = width * height;
        Color = new float[n * 4];
        Depth = new float[n];
        Output = new byte[n * 4];
    }
}

public static class SkinRenderer
{
    /// <summary>渲染一帧。pose 为空时用静止姿态（走路动画由调用方逐帧喂相位）。</summary>
    public static BitmapFrame Render(SkinModel skin, RenderOptions opts, int width, int height,
        Pose? pose = null, SkinRenderTarget? target = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));

        var quads = PlayerModel.BuildQuads(skin, pose ?? Pose.Still, opts.HeadOnly ? "head" : null);

        target ??= new SkinRenderTarget();
        target.Ensure(width, height);
        var color = target.Color;
        var depth = target.Depth;
        var output = target.Output;
        Array.Clear(color);
        Array.Fill(depth, float.MinValue);   // 深度存 1/z：越大越近
        Array.Clear(output);

        var cam = opts.Camera;
        var yaw = cam.YawDeg * Math.PI / 180.0;
        var pitch = cam.PitchDeg * Math.PI / 180.0;
        var focal = height / 2.0 / Math.Tan(cam.FovDeg * Math.PI / 360.0);
        var centerY = opts.HeadOnly ? 28.0 : 16.0;    // 只看头时把镜头对准头部中心

        // 由近到远画：近面先把深度落好，远处的像素在着色之前就被 z 测试挡掉。
        var ordered = quads
            .OrderBy(q => ViewDepth(q, centerY, yaw, pitch))
            .ToList();

        // 按扫描带并行：每条带只写自己的 y 区间，深度/颜色缓冲互不争用，不用加锁。
        // 单线程下这台机器实测 630×1020 要 58ms（约 17fps，走路动画会发跳）；
        // 这台机器的单核算力地板就在 50ns 量级（同密度空循环实测 51ns/次），
        // 所以把并行度用起来比继续抠算法有效。
        var bands = Math.Max(1, Math.Min(Environment.ProcessorCount, (height + 127) / 128));
        if (bands == 1)
        {
            RasterizeAll(ordered, color, depth, width, height, 0, height, centerY, yaw, pitch,
                cam.Distance, focal, skin.Texture);
        }
        else
        {
            Parallel.For(0, bands, band =>
            {
                var y0 = band * height / bands;
                var y1 = (band + 1) * height / bands;
                RasterizeAll(ordered, color, depth, width, height, y0, y1, centerY, yaw, pitch,
                    cam.Distance, focal, skin.Texture);
            });
        }

        PremultiplyBgra(color, output, width * height);
        return new BitmapFrame(output, width, height);
    }

    /// <summary>把全部面片画进 [clipY0, clipY1) 这一段扫描带</summary>
    private static void RasterizeAll(IReadOnlyList<Quad> quads, float[] color, float[] depth,
        int width, int height, int clipY0, int clipY1, double centerY, double yaw, double pitch,
        double distance, double focal, SkinBitmap texture)
    {
        Span<Vertex> v = stackalloc Vertex[4];
        foreach (var quad in quads)
        {
            var visible = true;
            for (var i = 0; i < 4; i++)
            {
                v[i] = Project(quad, i, centerY, yaw, pitch, distance, focal, width, height);
                if (v[i].Z <= 0.05) visible = false; // 有顶点跑到相机后面：整片跳过（模型很小，够用）
            }
            if (!visible) continue;
            Rasterize(color, depth, width, height, clipY0, clipY1, v[0], v[1], v[2], texture);
            Rasterize(color, depth, width, height, clipY0, clipY1, v[0], v[2], v[3], texture);
        }
    }

    /// <summary>顶点：屏幕坐标 + 相机 z + 已预乘 1/z 的纹理坐标（内循环只做乘加）</summary>
    private readonly record struct Vertex(
        float Sx, float Sy, float Z, float Iz, float Uz, float Vz);

    /// <summary>面片质心在相机空间的深度（排序用；越小越近）</summary>
    private static double ViewDepth(Quad quad, double centerY, double yaw, double pitch)
    {
        var cx = (quad.A.X + quad.B.X + quad.C.X + quad.D.X) / 4;
        var cy = (quad.A.Y + quad.B.Y + quad.C.Y + quad.D.Y) / 4 - centerY;
        var cz = (quad.A.Z + quad.B.Z + quad.C.Z + quad.D.Z) / 4;

        var z1 = -cx * Math.Sin(yaw) + cz * Math.Cos(yaw);
        var z2 = cy * Math.Sin(pitch) + z1 * Math.Cos(pitch);
        return -z2;   // 相机在 +distance 侧朝 -Z 看：z2 越大越近
    }

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

        var iz = (float)(1.0 / zc);
        return new Vertex(
            (float)(width / 2.0 + x1 * focal / zc),
            (float)(height / 2.0 - y1 * focal / zc),
            (float)zc, iz, (float)u * iz, (float)vv * iz);
    }

    /// <summary>
    /// 重心坐标光栅化 + 透视校正 UV + 逐像素 Z 测试 + 直通 alpha 混合。
    /// 内循环刻意只用 float 乘加：每像素一次除法（取 1/iz）——
    /// 之前每像素约 10 次除法，630×1020 下一帧要 100ms，动画只有 10fps。
    /// </summary>
    private static void Rasterize(float[] color, float[] depth, int width, int height,
        int clipY0, int clipY1, Vertex a, Vertex b, Vertex c, SkinBitmap texture)
    {
        var minX = (int)MathF.Floor(MathF.Min(a.Sx, MathF.Min(b.Sx, c.Sx)));
        var maxX = (int)MathF.Ceiling(MathF.Max(a.Sx, MathF.Max(b.Sx, c.Sx)));
        var minY = (int)MathF.Floor(MathF.Min(a.Sy, MathF.Min(b.Sy, c.Sy)));
        var maxY = (int)MathF.Ceiling(MathF.Max(a.Sy, MathF.Max(b.Sy, c.Sy)));
        if (maxX < 0 || minX >= width || maxY < 0 || minY >= height) return;
        minX = Math.Max(minX, 0); maxX = Math.Min(maxX, width - 1);
        minY = Math.Max(minY, 0); maxY = Math.Min(maxY, height - 1);
        minY = Math.Max(minY, clipY0); maxY = Math.Min(maxY, clipY1 - 1);   // 本带负责的行
        if (minY > maxY) return;

        var area = (b.Sx - a.Sx) * (c.Sy - a.Sy) - (c.Sx - a.Sx) * (b.Sy - a.Sy);
        if (MathF.Abs(area) < 1e-6f) return;
        Span<byte> texel = stackalloc byte[4];

        // 三条边预计算成 (起点 x/y、斜率)：逐行求交只做一次乘法，不再每行做除法
        Span<float> ex = stackalloc float[3];
        Span<float> ey0 = stackalloc float[3];
        Span<float> ey1 = stackalloc float[3];
        Span<float> eslope = stackalloc float[3];
        for (var e = 0; e < 3; e++)
        {
            var (x0, y0, x1, y1) = e switch
            {
                0 => (a.Sx, a.Sy, b.Sx, b.Sy),
                1 => (b.Sx, b.Sy, c.Sx, c.Sy),
                _ => (c.Sx, c.Sy, a.Sx, a.Sy),
            };
            ex[e] = x0;
            ey0[e] = y0;
            ey1[e] = y1;
            eslope[e] = y1 == y0 ? 0f : (x1 - x0) / (y1 - y0);
        }

        for (var y = minY; y <= maxY; y++)
        {
            var py = y + 0.5f;

            // 先求这一行三角形真正覆盖的 x 区间：包围盒逐像素测试会有十几倍的无用测试
            //（630×1020 下实测：近 4M 次测试只为 24 万个着色像素，一帧 77ms）。
            // 凸三角形下这个区间就是内部：区间内直接着色，不再重复做重心判定。
            var lo = float.MaxValue;
            var hi = float.MinValue;
            for (var e = 0; e < 3; e++)
            {
                if (ey0[e] == ey1[e]) continue;                // 水平边不界定扫描线
                if (py < MathF.Min(ey0[e], ey1[e]) || py > MathF.Max(ey0[e], ey1[e])) continue;
                var xEdge = ex[e] + (py - ey0[e]) * eslope[e];
                if (xEdge < lo) lo = xEdge;
                if (xEdge > hi) hi = xEdge;
            }
            if (lo > hi) continue;

            var fromX = Math.Max(minX, (int)MathF.Floor(lo));
            var toX = Math.Min(maxX, (int)MathF.Ceiling(hi));
            var rowBase = y * width;
            for (var x = fromX; x <= toX; x++)
            {
                var px = x + 0.5f;
                var w0 = ((b.Sx - px) * (c.Sy - py) - (c.Sx - px) * (b.Sy - py)) / area;
                var w1 = ((c.Sx - px) * (a.Sy - py) - (a.Sx - px) * (c.Sy - py)) / area;
                var w2 = 1f - w0 - w1;

                var iz = w0 * a.Iz + w1 * b.Iz + w2 * c.Iz;
                if (iz <= 0) continue;
                var idx = rowBase + x;
                if (iz <= depth[idx]) continue;   // 越大越近
                var inv = 1f / iz;

                var u = (w0 * a.Uz + w1 * b.Uz + w2 * c.Uz) * inv;
                var v = (w0 * a.Vz + w1 * b.Vz + w2 * c.Vz) * inv;
                texture.Sample((int)u, (int)v, texel);
                var ta = texel[3] * (1f / 255f);
                if (ta <= 0) continue;

                var o = idx * 4;
                var dstA = color[o + 3];
                var outA = ta + dstA * (1 - ta);
                if (outA <= 0) continue;
                var invOut = 1f / outA;
                color[o] = (texel[0] * (1f / 255f) * ta + color[o] * dstA * (1 - ta)) * invOut;
                color[o + 1] = (texel[1] * (1f / 255f) * ta + color[o + 1] * dstA * (1 - ta)) * invOut;
                color[o + 2] = (texel[2] * (1f / 255f) * ta + color[o + 2] * dstA * (1 - ta)) * invOut;
                color[o + 3] = outA;
                depth[idx] = iz;
            }
        }
    }

    /// <summary>直通 alpha → 预乘 BGRA8</summary>
    private static void PremultiplyBgra(float[] color, byte[] bgra, int pixels)
    {
        for (var i = 0; i < pixels; i++)
        {
            var o = i * 4;
            var a = Math.Clamp(color[o + 3], 0f, 1f);
            if (a <= 0) continue;    // 未覆盖的像素输出缓冲本来就是 0，跳过省一遍写
            bgra[o] = ToByte(Math.Clamp(color[o + 2], 0, 1) * a);
            bgra[o + 1] = ToByte(Math.Clamp(color[o + 1], 0, 1) * a);
            bgra[o + 2] = ToByte(Math.Clamp(color[o], 0, 1) * a);
            bgra[o + 3] = ToByte(a);
        }
    }

    private static byte ToByte(float v) => (byte)Math.Clamp(MathF.Round(v * 255), 0, 255);
}
