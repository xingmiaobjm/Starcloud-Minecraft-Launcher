// 相机投影：模型 → 屏幕（软件光栅化与 GPU 光栅化共用这一份数学）。
//
// 两条渲染路径必须给出同一张图，所以投影只能有一处实现：
//   · SkinRenderer（CPU 逐像素）把 ScreenVertex 再转成带 1/z 预乘的顶点；
//   · SkinGpuRenderer（D3D11）直接把它写进顶点缓冲，透视校正交给硬件光栅器。
// 这段数学原先埋在软件光栅化器里；加 GPU 路径时若照抄一份，两条路就会慢慢跑偏。

namespace Starcloud.Core.Rendering;

/// <summary>
/// 投影后的顶点：屏幕像素坐标（左上角原点，像素中心在 +0.5）、相机深度（越小越近）、
/// 纹素空间的 UV（整数坐标落在纹素边界上，与 UV 矩形的含义一致）。
/// </summary>
public readonly record struct ScreenVertex(double Sx, double Sy, double Z, double U, double V);

/// <summary>投影后的面片：四个顶点 + 原面片（纹理矩形与镜像标志）</summary>
public sealed record ScreenQuad(
    Quad Source, ScreenVertex A, ScreenVertex B, ScreenVertex C, ScreenVertex D, double ViewDepth);

public static class SkinProjection
{
    /// <summary>顶点跑到相机后面（zc 过小）时整片跳过：模型很小，不需要裁剪。</summary>
    public const double NearClipZ = 0.05;

    /// <summary>
    /// 把面片投影到屏幕，返回**由远到近**排序的可见面片（近面后画，深度相等时先画的先占位，
    /// 与软件光栅化器的 z 测试一致）。
    /// </summary>
    public static List<ScreenQuad> Project(
        IReadOnlyList<Quad> quads, Camera camera, int width, int height, double centerY)
    {
        var yaw = camera.YawDeg * Math.PI / 180.0;
        var pitch = camera.PitchDeg * Math.PI / 180.0;
        var focal = height / 2.0 / Math.Tan(camera.FovDeg * Math.PI / 360.0);

        var projected = new List<ScreenQuad>(quads.Count);
        foreach (var quad in quads)
        {
            var depth = ViewDepth(quad, centerY, yaw, pitch);
            var a = Corner(quad, 0);
            var b = Corner(quad, 1);
            var c = Corner(quad, 2);
            var d = Corner(quad, 3);
            if (a.Z <= NearClipZ || b.Z <= NearClipZ || c.Z <= NearClipZ || d.Z <= NearClipZ) continue;
            projected.Add(new ScreenQuad(quad, a, b, c, d, depth));
        }

        // OrderBy 是稳定排序：深度相同的面片（例如手臂内侧面与躯干侧面共面）保持模型顺序，
        // 这样 CPU / GPU 两条路的"谁先占位"完全一致。
        return projected.OrderBy(q => q.ViewDepth).ToList();

        ScreenVertex Corner(Quad quad, int corner)
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
            // 4) 放到相机前方：相机在 +Z 侧的 distance 处、朝 -Z 看
            var zc = camera.Distance - z2;

            // 纹理坐标：UV 矩形按 左上 / 右上 / 右下 / 左下 对应顶点 0..3
            var u0 = quad.Uv.X;
            var u1 = quad.Uv.Right;
            var v0 = quad.Uv.Y;
            var v1 = quad.Uv.Bottom;
            double u = corner switch { 0 or 3 => u0, _ => u1 };
            double v = corner switch { 0 or 1 => v0, _ => v1 };
            if (quad.MirrorU) u = quad.Uv.X + quad.Uv.Right - u;

            return new ScreenVertex(
                width / 2.0 + x1 * focal / zc,
                height / 2.0 - y1 * focal / zc,
                zc, u, v);
        }
    }

    /// <summary>面片质心在相机空间的深度（排序用；越小越近）</summary>
    public static double ViewDepth(Quad quad, double centerY, double yaw, double pitch)
    {
        var cx = (quad.A.X + quad.B.X + quad.C.X + quad.D.X) / 4;
        var cy = (quad.A.Y + quad.B.Y + quad.C.Y + quad.D.Y) / 4 - centerY;
        var cz = (quad.A.Z + quad.B.Z + quad.C.Z + quad.D.Z) / 4;

        var z1 = -cx * Math.Sin(yaw) + cz * Math.Cos(yaw);
        var z2 = cy * Math.Sin(pitch) + z1 * Math.Cos(pitch);
        return -z2;   // 相机在 +distance 侧朝 -Z 看：z2 越大越近
    }
}
