// 骨架摆放与走路动画（设计：docs/designs/2026-09-12-skin-viewer-design.md §3.3）
//
// 坐标系：+X 指向玩家左侧，+Y 向上，+Z 朝向玩家正面，脚底 y = 0，单位 = 皮肤像素。
// 动画是纯函数 Pose.At(seconds) —— 给定时刻得到确定姿态，便于单测与逐帧录制。

namespace Starcloud.Core.Rendering;

/// <summary>一个面片（四个顶点已按纹理 UV 的 左上→右上→右下→左下 顺序排好）</summary>
public readonly record struct Quad(string Part, Vec3 A, Vec3 B, Vec3 C, Vec3 D, RectI Uv, bool MirrorU)
{
    public Vec3 Corner(int i) => i switch { 0 => A, 1 => B, 2 => C, _ => D };
}

/// <summary>某一时刻的姿态：各部件绕 X 轴旋转（弧度）+ 上身位移</summary>
public sealed record Pose(
    double RightLegX, double LeftLegX,
    double RightArmX, double LeftArmX,
    double HeadX, double CapeX, double BodyOffsetY)
{
    public static readonly Pose Still = new(0, 0, 0, 0, 0, 0, 0);
}

public static class WalkAnimator
{
    /// <summary>步频：φ = seconds × 7.0（约 0.9 秒 / 步）</summary>
    public static Pose At(double seconds) => FromPhase(seconds * 7.0);

    /// <summary>按相位取姿态（逐帧录制时直接喂 0..1 的周期相位，避免时间累积误差）</summary>
    public static Pose FromPhase(double phi)
    {
        const double Deg = Math.PI / 180.0;
        var s = Math.Sin(phi);
        return new Pose(
            RightLegX: s * 30 * Deg,
            LeftLegX: -s * 30 * Deg,
            RightArmX: -s * 25 * Deg,
            LeftArmX: s * 25 * Deg,
            HeadX: s * 3 * Deg,
            CapeX: (6 + Math.Sin(phi - 0.6) * 7) * Deg,
            BodyOffsetY: -Math.Abs(s) * 0.6);
    }
}

public static class PlayerModel
{
    /// <summary>各部件的旋转轴心（模型坐标）</summary>
    private static Vec3 PivotOf(string part, bool slim) => part switch
    {
        "head" or "head_overlay" => new Vec3(0, 24, 0),
        "rightArm" or "rightArm_overlay" => new Vec3(slim ? -5.5 : -6, 22, 0),
        "leftArm" or "leftArm_overlay" => new Vec3(slim ? 5.5 : 6, 22, 0),
        "rightLeg" or "rightLeg_overlay" => new Vec3(-2, 12, 0),
        "leftLeg" or "leftLeg_overlay" => new Vec3(2, 12, 0),
        "cape" => new Vec3(0, 24, -2.2),  // 披风在 -Z 侧，pivot 跟着挪
        _ => new Vec3(0, 12, 0), // body 等不旋转
    };

    /// <summary>
    /// 披风的基础倾角（度）：静止时也微微上翘，不完全垂直贴背。
    /// <para>绕 X 轴正向 = 下沿朝背后翘、上沿留在肩后，视觉上像布被风或动作带起；
    /// 走路时的摆动叠加在这个基础上。</para>
    /// </summary>
    private const double CapeBaseTiltDeg = 14;

    private static double RotationOf(string part, Pose pose) => part switch
    {
        "rightLeg" or "rightLeg_overlay" => pose.RightLegX,
        "leftLeg" or "leftLeg_overlay" => pose.LeftLegX,
        "rightArm" or "rightArm_overlay" => pose.RightArmX,
        "leftArm" or "leftArm_overlay" => pose.LeftArmX,
        "head" or "head_overlay" => pose.HeadX,
        "cape" => pose.CapeX + CapeBaseTiltDeg * (Math.PI / 180.0),
        _ => 0,
    };

    /// <summary>腿是"钉在地上"的：上身（含头、臂、披风）才随走路起伏</summary>
    private static bool BobsWithBody(string part) => !part.StartsWith("rightLeg") && !part.StartsWith("leftLeg");

    /// <summary>
    /// 把模型摆成给定姿态，输出可直接光栅化的面片（世界坐标）。
    /// <paramref name="partPrefix"/> 非空时只输出该前缀的部件（HeadOnly 头像用）。
    /// </summary>
    public static IReadOnlyList<Quad> BuildQuads(SkinModel model, Pose pose, string? partPrefix = null)
    {
        var quads = new List<Quad>(model.Cubes.Count * 6);
        foreach (var cube in model.Cubes)
        {
            if (partPrefix is not null && !cube.Part.StartsWith(partPrefix, StringComparison.Ordinal)) continue;
            var pivot = PivotOf(cube.Part, model.Slim);
            var rot = RotationOf(cube.Part, pose);
            var dy = BobsWithBody(cube.Part) ? pose.BodyOffsetY : 0;

            for (var f = 0; f < 6; f++)
            {
                var uv = cube.Faces[f];
                if (uv.W <= 0 || uv.H <= 0) continue; // 被剔除的面（覆盖层整面透明）
                var corners = FaceCorners(cube.Min, cube.Max, (CubeFace)f);
                var moved = new Vec3[4];
                for (var i = 0; i < 4; i++)
                    moved[i] = Move(corners[i], pivot, rot, dy);
                quads.Add(new Quad(cube.Part, moved[0], moved[1], moved[2], moved[3], uv, cube.MirrorU));
            }
        }
        return quads;
    }

    /// <summary>绕 X 轴旋转 + 上身位移</summary>
    private static Vec3 Move(Vec3 p, Vec3 pivot, double rot, double dy)
    {
        var y = p.Y - pivot.Y;
        var z = p.Z - pivot.Z;
        var c = Math.Cos(rot);
        var s = Math.Sin(rot);
        return new Vec3(p.X,
            pivot.Y + y * c - z * s + dy,
            pivot.Z + y * s + z * c);
    }

    /// <summary>
    /// 一个面的四个角，顺序必须与纹理 UV 的 左上→右上→右下→左下 对齐。
    /// 朝向推导：观察者站在面外侧、up = +Y，屏幕右 = forward × up。
    /// </summary>
    public static Vec3[] FaceCorners(Vec3 min, Vec3 max, CubeFace face)
    {
        double x0 = min.X, x1 = max.X, y0 = min.Y, y1 = max.Y, z0 = min.Z, z1 = max.Z;
        return face switch
        {
            // 正面：+Z 朝外，屏幕右 = +X，屏幕上 = +Y
            CubeFace.Front =>
            [
                new Vec3(x0, y1, z1), new Vec3(x1, y1, z1), new Vec3(x1, y0, z1), new Vec3(x0, y0, z1),
            ],
            // 背面：-Z 朝外，屏幕右 = -X
            CubeFace.Back =>
            [
                new Vec3(x1, y1, z0), new Vec3(x0, y1, z0), new Vec3(x0, y0, z0), new Vec3(x1, y0, z0),
            ],
            // 玩家右侧：-X 朝外，屏幕右 = +Z
            CubeFace.Right =>
            [
                new Vec3(x0, y1, z0), new Vec3(x0, y1, z1), new Vec3(x0, y0, z1), new Vec3(x0, y0, z0),
            ],
            // 玩家左侧：+X 朝外，屏幕右 = -Z
            CubeFace.Left =>
            [
                new Vec3(x1, y1, z1), new Vec3(x1, y1, z0), new Vec3(x1, y0, z0), new Vec3(x1, y0, z1),
            ],
            // 顶面：+Y 朝外，屏幕上 = -Z（模型正面出现在屏幕下方）
            CubeFace.Top =>
            [
                new Vec3(x0, y1, z0), new Vec3(x1, y1, z0), new Vec3(x1, y1, z1), new Vec3(x0, y1, z1),
            ],
            // 底面：-Y 朝外，屏幕上 = +Z
            _ =>
            [
                new Vec3(x0, y0, z1), new Vec3(x1, y0, z1), new Vec3(x1, y0, z0), new Vec3(x0, y0, z0),
            ],
        };
    }
}
