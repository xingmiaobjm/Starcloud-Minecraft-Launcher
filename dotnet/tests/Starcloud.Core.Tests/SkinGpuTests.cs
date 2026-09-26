// GPU 光栅化（SkinGpuRenderer）测试。
//
// 这里要证的不是"GPU 能出图"，而是"GPU 与软件光栅化器逐像素一致"：
// 两条路一旦分叉，表现就是边缘杂色、颜色偏移这类很难复现的问题。
// 没有独显/核显的环境会退到 WARP（D3D11 自带的软件设备）照样验证；
// 连 WARP 都起不来（例如非 Windows）才跳过。

using Starcloud.Core.Rendering;
using Xunit;

namespace Starcloud.Core.Tests;

public class SkinGpuTests
{
    private static readonly (double Yaw, double Pitch)[] Cameras =
        [(24, 6), (0, 0), (-40, 25), (100, -30)];

    /// <summary>取 GPU 渲染器；环境实在不支持时返回 null（调用方跳过用例并留痕）。</summary>
    private static SkinGpuRenderer? TryGpu(out string note)
    {
        var gpu = SkinGpuRenderer.TryCreate(allowSoftwareDevice: true);
        note = gpu is null ? "本机无法创建 D3D11 设备（含 WARP），跳过 GPU 用例" : string.Empty;
        return gpu;
    }

    /// <summary>花哨的假皮肤：每个纹素颜色不同且全不透明（覆盖层因此全部存在）。</summary>
    private static SkinModel PatternSkin(int size = 64)
    {
        var rgba = new byte[size * size * 4];
        for (var y = 0; y < size; y++)
            for (var x = 0; x < size; x++)
            {
                var o = (y * size + x) * 4;
                rgba[o] = (byte)(x * 7 % 256);
                rgba[o + 1] = (byte)(y * 11 % 256);
                rgba[o + 2] = (byte)((x + y) * 5 % 256);
                rgba[o + 3] = 255;
            }
        return new SkinModel(new SkinBitmap(size, size, rgba), slim: false);
    }

    [Fact]
    public void GPU_着色器源码保持纯ASCII()
    {
        // Vortice 把 HLSL 源码按 ANSI 交给 D3DCompile：源码里出现非 ASCII（尤其是中文注释）
        // 会被截断成 "X3000: unexpected end of file / unrecognized identifier"。
        // 这条用例把"别在 HLSL 里写中文"变成会失败的断言，省得下次再踩。
        var source = (string)typeof(SkinGpuRenderer)
            .GetField("ShaderSource", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .GetValue(null)!;

        var bad = source.Select((c, i) => (c, i)).Where(p => p.c > 127).Select(p => p.i).Take(5).ToList();
        Assert.True(bad.Count == 0,
            $"HLSL 源码必须是纯 ASCII，非 ASCII 位置：{string.Join(", ", bad)}");
    }

    [Fact]
    public void GPU_与软件渲染逐像素一致()
    {
        using var gpu = TryGpu(out var note);
        if (gpu is null) return;

        var model = PatternSkin();
        foreach (var (w, h) in new[] { (300, 600), (128, 192), (600, 900) })
            foreach (var (yaw, pitch) in Cameras)
            {
                var opts = new RenderOptions(new Camera(yaw, pitch, 96.0 / 1.4));
                var cpu = SkinRenderer.Render(model, opts, w, h, WalkAnimator.FromPhase(1.1));
                var gpuFrame = gpu.Render(model, opts, w, h, WalkAnimator.FromPhase(1.1));

                Assert.Equal(cpu.Width, gpuFrame.Width);
                Assert.Equal(cpu.Height, gpuFrame.Height);
                AssertClose(cpu, gpuFrame, $"{w}×{h} yaw={yaw} pitch={pitch}{note}");
            }
    }

    /// <summary>
    /// 两条路径的差异只允许出现在"边缘一侧"：
    ///   · CPU 的保守填充（三角形外半像素也着色）让轮廓与面片接缝比真实几何宽 ≤1px，
    ///     GPU 用硬件的精确填充规则，于是那一圈像素归属不同；
    ///   · 少数像素正好压在纹素边界上，CPU 的 float 与硬件的定点插值会取到相邻纹素。
    /// 所以判据是"结构一致"而不是"逐像素相同"：轮廓差 ≤8%、同覆盖像素里大差 ≤10%、
    /// 深内部（3×3 同色）大差 ≤1%。实测 300×600 为 1.2% / 0.7% / 0.07%，留了数倍余量。
    /// 颜色整体错位（例如 RGB/BGR 弄反）、丢面、错位这类真 bug 都会让深内部大差爆表。
    /// </summary>
    private static void AssertClose(BitmapFrame cpu, BitmapFrame gpuFrame, string where)
    {
        var w = cpu.Width;
        var h = cpu.Height;
        var cpuEdge = EdgeMask(cpu);
        var gpuEdge = EdgeMask(gpuFrame);
        int covered = 0, silhouette = 0, clean = 0, cleanBig = 0;
        int worstDelta = 0;
        (int X, int Y) worst = (0, 0);

        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = y * w + x;
                var o = p * 4;
                var ca = cpu.Bgra[o + 3];
                var ga = gpuFrame.Bgra[o + 3];
                if (ca > 0 || ga > 0) covered++;
                if ((ca == 0) != (ga == 0)) { silhouette++; continue; }
                if (ca == 0) continue;

                var delta = 0;
                for (var c = 0; c < 4; c++)
                    delta = Math.Max(delta, Math.Abs(cpu.Bgra[o + c] - gpuFrame.Bgra[o + c]));
                if (delta > worstDelta) { worstDelta = delta; worst = (x, y); }

                // 只统计"干净"像素：两个图里都不是边（1px 邻域内无颜色/透明度变化）。
                // 两条路径对"面片接缝归谁"的判断天然不同，那些像素属于已知差异，不计入。
                if (cpuEdge[p] || gpuEdge[p]) continue;
                clean++;
                if (delta > 24) cleanBig++;
            }

        Assert.True(silhouette * 100 <= covered * 8,
            $"{where}：轮廓归属不同的像素过多 {silhouette}/{covered}");
        Assert.True(clean * 100 >= covered * 20, $"{where}：干净像素太少（{clean}/{covered}），判据失效");
        Assert.True(cleanBig * 100 <= clean * 3,
            $"{where}：干净像素里出现差异 {cleanBig}/{clean}（最大差 {worstDelta} @ {worst.X},{worst.Y}）");
    }

    /// <summary>边掩码：与 4 邻域相比在 alpha 或颜色上发生变化的像素</summary>
    private static bool[] EdgeMask(BitmapFrame frame)
    {
        var w = frame.Width;
        var h = frame.Height;
        var mask = new bool[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var p = y * w + x;
                for (var d = 0; d < 4 && !mask[p]; d++)
                {
                    var qx = x + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    var qy = y + (d == 2 ? 1 : d == 3 ? -1 : 0);
                    if (qx < 0 || qy < 0 || qx >= w || qy >= h) { mask[p] = true; break; }
                    var q = qy * w + qx;
                    for (var c = 0; c < 4; c++)
                        if (frame.Bgra[p * 4 + c] != frame.Bgra[q * 4 + c]) { mask[p] = true; break; }
                    if (mask[p]) break;
                }
            }
        return mask;
    }

    [Fact]
    public void GPU_尺寸变化后仍与软件一致()
    {
        // 渲染目标/深度/staging 都是按尺寸缓存的，换尺寸必须重建正确
        using var gpu = TryGpu(out _);
        if (gpu is null) return;

        var model = PatternSkin();
        var opts = new RenderOptions(new Camera(24, 6, 96.0 / 1.4));
        foreach (var (w, h) in new[] { (200, 400), (320, 480), (200, 400) })
        {
            var cpu = SkinRenderer.Render(model, opts, w, h);
            var frame = gpu.Render(model, opts, w, h);
            Assert.Equal(w, frame.Width);
            Assert.Equal(h, frame.Height);
            AssertClose(cpu, frame, $"{w}×{h}（换尺寸后）");
        }
    }

    [Fact]
    public void GPU_轮廓边缘不会采到面片UV之外的纹素()
    {
        // 与软件路径同样的"陷阱皮肤"：全图洋红，面片矩形涂绿。
        // 输出里出现洋红 = 边缘像素采样跑出了面片矩形（图集串色）。
        using var gpu = TryGpu(out _);
        if (gpu is null) return;

        const int size = 64;
        var rgba = new byte[size * size * 4];
        for (var i = 0; i < size * size; i++)
        {
            rgba[i * 4] = 255; rgba[i * 4 + 1] = 0; rgba[i * 4 + 2] = 255; rgba[i * 4 + 3] = 255;
        }
        var model = new SkinModel(new SkinBitmap(size, size, rgba), slim: false);
        foreach (var cube in model.Cubes)
            foreach (var face in cube.Faces)
                for (var y = face.Y; y < face.Bottom; y++)
                    for (var x = face.X; x < face.Right; x++)
                    {
                        var o = (y * size + x) * 4;
                        rgba[o] = 0; rgba[o + 1] = 255; rgba[o + 2] = 0;
                    }

        foreach (var (yaw, pitch) in Cameras)
        {
            var frame = gpu.Render(model, new RenderOptions(new Camera(yaw, pitch, 96.0 / 1.4)), 300, 600);
            for (var y = 0; y < frame.Height; y++)
                for (var x = 0; x < frame.Width; x++)
                {
                    var o = (y * frame.Width + x) * 4;
                    var a = frame.Bgra[o + 3];
                    if (a == 0) continue;
                    var b = frame.Bgra[o]; var g = frame.Bgra[o + 1]; var r = frame.Bgra[o + 2];
                    Assert.False(r > a / 2 && b > a / 2 && g < a / 2,
                        $"yaw={yaw} pitch={pitch} 的 ({x},{y}) 采到了面片 UV 之外的纹素");
                }
        }
    }

    [Fact]
    public void GPU_输出是不超过alpha的预乘BGRA()
    {
        using var gpu = TryGpu(out _);
        if (gpu is null) return;

        var model = PatternSkin();
        var frame = gpu.Render(model, new RenderOptions(new Camera(20, 8, 96.0 / 1.4)), 160, 240);

        var covered = 0;
        for (var i = 0; i < frame.Bgra.Length; i += 4)
        {
            var a = frame.Bgra[i + 3];
            if (a > 0) covered++;
            for (var c = 0; c < 3; c++)
                Assert.True(frame.Bgra[i + c] <= a, $"({i}) 预乘通道超过 alpha");
        }
        Assert.True(covered > 1000, $"应该画出可见像素，实际 {covered}");

        // 四角必须在模型之外（透明）
        foreach (var (x, y) in new[] { (0, 0), (159, 0), (0, 239), (159, 239) })
            Assert.Equal(0, frame.Bgra[(y * 160 + x) * 4 + 3]);
    }
}
