// 皮肤纹理 → 立方体几何（设计：docs/designs/2026-09-12-skin-viewer-design.md §3.2）
//
// 六个面的 UV 不手抄常量，而是用 Minecraft 的展开公式从 (u, v, w, h, d) 推出来——
// 少 30 个魔法矩形，改版型时也不会抄错。

namespace Starcloud.Core.Rendering;

/// <summary>一个已展开的立方体：几何包围盒 + 六面 UV（顺序同 CubeFace）</summary>
public sealed record ModelCube(
    string Part,
    Vec3 Min,
    Vec3 Max,
    RectI[] Faces,
    bool IsCape = false,
    bool MirrorU = false);

public sealed class SkinModel
{
    /// <summary>各部件：纹理原点、尺寸、几何包围盒（模型坐标）</summary>
    private sealed record PartDef(
        string Name, int U, int V, int W, int H, int D,
        double X0, double X1, double Y0, double Y1, double Z0, double Z1,
        int OverlayU = -1, int OverlayV = -1);

    public bool Slim { get; }
    public bool Legacy { get; }              // 老版 64×32：无覆盖层、左肢镜像右肢
    public int Scale { get; }                // 皮肤纹理相对 64×64 的整数倍率
    /// <summary>底层皮肤纹理（渲染时按面片 UV 采样）</summary>
    public SkinBitmap Texture { get; }
    public IReadOnlyList<ModelCube> Cubes { get; }

    public SkinModel(SkinBitmap skin, bool slim, SkinBitmap? cape = null)
    {
        if (skin.Width % 64 != 0 || (skin.Height != skin.Width && skin.Height != skin.Width / 2))
            throw new SkinTextureException($"皮肤尺寸不支持：{skin.Width}×{skin.Height}");

        Scale = skin.Width / 64;
        Slim = slim;
        Legacy = skin.Height == skin.Width / 2;
        Texture = skin;

        var armW = slim ? 3 : 4;
        var parts = new List<PartDef>
        {
            // 头 / 帽子层
            new("head", 0, 0, 8, 8, 8, -4, 4, 24, 32, -4, 4, 32, 0),
            // 身 / 夹克层
            new("body", 16, 16, 8, 12, 4, -4, 4, 12, 24, -2, 2, 16, 32),
            // 右臂（slim 细 3 px）
            new("rightArm", 40, 16, armW, 12, 4, -4 - armW, -4, 12, 24, -2, 2, 40, 32),
            // 左臂
            new("leftArm", 32, 48, armW, 12, 4, 4, 4 + armW, 12, 24, -2, 2, 48, 48),
            // 右腿
            new("rightLeg", 0, 16, 4, 12, 4, -4, 0, 0, 12, -2, 2, 0, 32),
            // 左腿
            new("leftLeg", 16, 48, 4, 12, 4, 0, 4, 0, 12, -2, 2, 0, 48),
        };

        var cubes = new List<ModelCube>();
        foreach (var p in parts)
        {
            // 老版皮肤没有左肢纹理：复用右肢并左右镜像
            var legacyLeft = Legacy && p.Name is "leftArm" or "leftLeg";
            var (u, v) = legacyLeft
                ? p.Name == "leftArm" ? (40, 16) : (0, 16)
                : (p.U, p.V);

            cubes.Add(new ModelCube(p.Name,
                new Vec3(p.X0, p.Y0, p.Z0), new Vec3(p.X1, p.Y1, p.Z1),
                FacesFor(u, v, p.W, p.H, p.D, Scale), MirrorU: legacyLeft));

            // 覆盖层：几何每边外扩 0.5（尺寸 +1）；整面全透明则剔除该面，六面全透明则不出现
            if (Legacy || p.OverlayU < 0) continue;
            var overlay = FacesFor(p.OverlayU, p.OverlayV, p.W, p.H, p.D, Scale);
            var kept = new RectI[6];
            var any = false;
            for (var f = 0; f < 6; f++)
            {
                var r = overlay[f];
                if (r.W <= 0 || r.H <= 0 || skin.IsRegionFullyTransparent(r.X, r.Y, r.W, r.H)) continue;
                kept[f] = r;
                any = true;
            }
            if (!any) continue;

            cubes.Add(new ModelCube(p.Name + "_overlay",
                new Vec3(p.X0 - 0.5, p.Y0 - 0.5, p.Z0 - 0.5),
                new Vec3(p.X1 + 0.5, p.Y1 + 0.5, p.Z1 + 0.5),
                kept));
        }

        if (cape is not null && cape.Width % 64 == 0 && cape.Height == cape.Width / 2)
        {
            var cs = cape.Width / 64;
            var faces = new RectI[6];
            faces[(int)CubeFace.Front] = ScaleRect(new RectI(1, 1, 10, 16), cs);
            faces[(int)CubeFace.Back] = ScaleRect(new RectI(12, 1, 10, 16), cs);
            cubes.Add(new ModelCube("cape",
                new Vec3(-5, 8, 2), new Vec3(5, 24, 3), faces, IsCape: true));
        }

        Cubes = cubes;
    }

    /// <summary>按纹理倍率放大一个矩形</summary>
    private static RectI ScaleRect(RectI r, int scale) =>
        new(r.X * scale, r.Y * scale, r.W * scale, r.H * scale);

    /// <summary>Minecraft 的立方体展开公式（见设计文档 §3.2）</summary>
    public static RectI FaceUv(int u, int v, int w, int h, int d, CubeFace face, int scale = 1)
    {
        var r = face switch
        {
            CubeFace.Top => new RectI(u + d, v, w, d),
            CubeFace.Bottom => new RectI(u + d + w, v, w, d),
            CubeFace.Right => new RectI(u, v + d, d, h),
            CubeFace.Front => new RectI(u + d, v + d, w, h),
            CubeFace.Left => new RectI(u + d + w, v + d, d, h),
            _ => new RectI(u + d + w + d, v + d, w, h),
        };
        return ScaleRect(r, scale);
    }

    private static RectI[] FacesFor(int u, int v, int w, int h, int d, int scale)
    {
        var faces = new RectI[6];
        for (var f = 0; f < 6; f++)
            faces[f] = FaceUv(u, v, w, h, d, (CubeFace)f, scale);
        return faces;
    }
}
