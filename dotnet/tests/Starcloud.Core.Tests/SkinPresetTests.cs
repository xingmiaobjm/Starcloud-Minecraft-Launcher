// PNG 编码器与默认皮肤资产测试。
//
// 编码器是"能写"的方向：卡片缩略图要落盘成 PNG 再交给 BitmapImage，
// 所以编码结果必须能被自家解码器读回来。这里用「编码 → 解码 → 逐像素比对」钉死。
//
// 默认皮肤是官方资产（App/Assets/Skins/default/），这里盯三件事：
//   1. 清单里的角色 / 文件 / 版型与官方一致（版型错了游戏里手臂粗细就不对）；
//   2. 资产文件真的在仓库里、能解码、是 64×64 的双层皮肤；
//   3. 是真实画出来的皮肤（颜色数够多），不是占位图。

using Starcloud.Core.Rendering;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class PngEncoderTests
{
    [Fact]
    public void 编码后再解码_像素逐一相同()
    {
        const int w = 64, h = 64;
        var rgba = new byte[w * h * 4];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            rgba[i] = (byte)(i % 251);
            rgba[i + 1] = (byte)(i * 7 % 253);
            rgba[i + 2] = (byte)(i * 13 % 255);
            rgba[i + 3] = (byte)(i % 5 == 0 ? 128 : 255);   // 混入半透明，防止被当成纯 RGB 处理
        }

        var png = PngEncoder.Encode(w, h, rgba);
        var back = PngDecoder.Decode(png);

        Assert.Equal(w, back.Width);
        Assert.Equal(h, back.Height);
        Assert.Equal(rgba, back.Rgba);
    }

    [Fact]
    public void 编码结果的签名与IHDR符合PNG规范()
    {
        var png = PngEncoder.Encode(2, 3, new byte[2 * 3 * 4]);

        Assert.Equal([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A], png[..8]);
        Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
        Assert.Equal(2, (png[16] << 24) | (png[17] << 16) | (png[18] << 8) | png[19]);
        Assert.Equal(3, (png[20] << 24) | (png[21] << 16) | (png[22] << 8) | png[23]);
        Assert.Equal(8, png[24]);   // bit depth
        Assert.Equal(6, png[25]);   // color type = RGBA
        Assert.Equal(0, png[28]);   // interlace = none
    }

    [Fact]
    public void 尺寸或像素数据不合法时抛异常()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => PngEncoder.Encode(0, 4, []));
        Assert.Throws<ArgumentException>(() => PngEncoder.Encode(4, 4, new byte[16]));
    }
}

public class SkinPresetTests
{
    /// <summary>9 个官方默认角色 + 各自的版型（依据见 SkinPresets 注释与资产 README）</summary>
    private static readonly (string Id, string Name, bool Slim)[] Expected =
    [
        ("steve", "Steve", false),
        ("alex", "Alex", true),
        ("ari", "Ari", false),
        ("efe", "Efe", true),
        ("kai", "Kai", false),
        ("makena", "Makena", true),
        ("noor", "Noor", true),
        ("sunny", "Sunny", false),
        ("zuri", "Zuri", false),
    ];

    [Fact]
    public void 清单与官方默认皮肤一致()
    {
        Assert.Equal(Expected.Length, SkinPresets.All.Count);
        for (var i = 0; i < Expected.Length; i++)
        {
            var (id, name, slim) = Expected[i];
            var preset = SkinPresets.All[i];
            Assert.Equal(id, preset.Id);
            Assert.Equal(name, preset.Name);
            Assert.Equal(slim, preset.Slim);
            Assert.Equal(id + ".png", preset.File);
        }
    }

    [Fact]
    public void 资产文件都在仓库里且是合法的双层皮肤()
    {
        var dir = AssetDir();
        Assert.NotNull(dir);
        foreach (var preset in SkinPresets.All)
        {
            var path = Path.Combine(dir!, preset.File);
            Assert.True(File.Exists(path), $"缺少默认皮肤资产：{preset.File}");
            var bitmap = PngDecoder.Decode(File.ReadAllBytes(path));
            Assert.Equal(64, bitmap.Width);
            Assert.Equal(64, bitmap.Height);
            Assert.True(SkinTexture.IsValidSize(bitmap.Width, bitmap.Height));
            // 用清单里的版型能建出模型（slim 时手臂读 3px，纹理必须撑得住）
            _ = new SkinModel(bitmap, preset.Slim);
        }
    }

    [Fact]
    public void 每张默认皮肤都是有内容的画()
    {
        var dir = AssetDir()!;
        foreach (var preset in SkinPresets.All)
        {
            var bitmap = PngDecoder.Decode(File.ReadAllBytes(Path.Combine(dir, preset.File)));
            var colors = new HashSet<int>();
            for (var i = 0; i < bitmap.Rgba.Length; i += 4)
            {
                if (bitmap.Rgba[i + 3] == 0) continue;
                colors.Add((bitmap.Rgba[i] << 16) | (bitmap.Rgba[i + 1] << 8) | bitmap.Rgba[i + 2]);
            }
            Assert.True(colors.Count >= 12, $"{preset.Id} 只有 {colors.Count} 种颜色，像是占位图");
        }
    }

    /// <summary>从测试输出目录往上找仓库里的默认皮肤目录（与 SkinTests 找 steve.png 同一套办法）</summary>
    private static string? AssetDir()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            var candidate = Path.Combine(dir.FullName,
                "src", "Starcloud.App", SkinPresets.AssetDir.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        return null;
    }
}
