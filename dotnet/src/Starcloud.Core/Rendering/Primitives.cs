// 渲染用轻量几何原语（设计：docs/designs/2026-09-12-skin-viewer-design.md §3.2）
//
// 刻意不引第三方数学库、也不引 System.Numerics 之外的依赖：Core 保持零新增依赖，
// 且这些结构都是"值语义 + 小字段"，用 struct 避免装箱。

namespace Starcloud.Core.Rendering;

/// <summary>整数矩形（纹理 UV 用，左闭右开：x..x+w、y..y+h）</summary>
public readonly record struct RectI(int X, int Y, int W, int H)
{
    public int Right => X + W;
    public int Bottom => Y + H;
}

/// <summary>三维向量（模型坐标，单位 = 皮肤像素）</summary>
public readonly record struct Vec3(double X, double Y, double Z)
{
    public static Vec3 operator +(Vec3 a, Vec3 b) => new(a.X + b.X, a.Y + b.Y, a.Z + b.Z);
    public static Vec3 operator -(Vec3 a, Vec3 b) => new(a.X - b.X, a.Y - b.Y, a.Z - b.Z);
    public static Vec3 operator *(Vec3 a, double k) => new(a.X * k, a.Y * k, a.Z * k);
}

/// <summary>立方体的六个面（顺序固定，纹理展开公式依赖它）</summary>
public enum CubeFace
{
    Top = 0,
    Bottom = 1,
    Right = 2,
    Front = 3,
    Left = 4,
    Back = 5,
}
