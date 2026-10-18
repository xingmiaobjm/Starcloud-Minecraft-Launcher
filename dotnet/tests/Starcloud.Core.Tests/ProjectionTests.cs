// 实验室（投影文件）测试：NBT 读写回环、BlockStates 位打包互逆（1.16+ 连续 / 旧式跨长）、
// litematic 解析与统计、结构 nbt 解析、litematic ↔ 结构 nbt 转换回环、元数据回写。

using Starcloud.Core.Services;
using Starcloud.Core.Services.Projection;
using Xunit;

namespace Starcloud.Core.Tests;

public class ProjectionTests
{
    private static string TempDir()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-projection-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    // ── NBT 读写 ──

    [Fact]
    public void NBT全类型读写回环()
    {
        var root = new NbtCompound { Name = "" };
        root.Items.Add(new NbtByte { Name = "b", Value = 7 });
        root.Items.Add(new NbtShort { Name = "s", Value = -1234 });
        root.Items.Add(new NbtInt { Name = "i", Value = 123456 });
        root.Items.Add(new NbtLong { Name = "l", Value = 1L << 40 });
        root.Items.Add(new NbtFloat { Name = "f", Value = 1.5f });
        root.Items.Add(new NbtDouble { Name = "d", Value = 3.25 });
        root.Items.Add(new NbtString { Name = "str", Value = "中文 blockstate" });
        root.Items.Add(new NbtByteArray { Name = "ba", Value = [1, 2, 3] });
        root.Items.Add(new NbtIntArray { Name = "ia", Value = [10, -20] });
        root.Items.Add(new NbtLongArray { Name = "la", Value = [long.MinValue, 42] });
        var inner = new NbtCompound { Name = "inner" };
        inner.Items.Add(new NbtInt { Name = "x", Value = 9 });
        root.Items.Add(inner);
        root.Items.Add(new NbtList
        {
            Name = "list",
            ItemType = Nbt.TagCompound,
            Items = [inner, inner],
        });

        using var ms = new MemoryStream();
        Nbt.Write(ms, root);
        ms.Position = 0;
        var read = Nbt.Read(ms);

        Assert.Equal((byte)7, ((NbtByte)read.Get("b")!).Value);
        Assert.Equal((short)-1234, ((NbtShort)read.Get("s")!).Value);
        Assert.Equal(123456, read.GetInt("i"));
        Assert.Equal(1L << 40, read.GetLong("l"));
        Assert.Equal(1.5f, ((NbtFloat)read.Get("f")!).Value);
        Assert.Equal(3.25, ((NbtDouble)read.Get("d")!).Value);
        Assert.Equal("中文 blockstate", read.GetString("str"));
        Assert.Equal([1, 2, 3], ((NbtByteArray)read.Get("ba")!).Value);
        Assert.Equal([10, -20], ((NbtIntArray)read.Get("ia")!).Value);
        Assert.Equal([long.MinValue, 42], ((NbtLongArray)read.Get("la")!).Value);
        Assert.Equal(9, read.GetCompound("inner")!.GetInt("x"));
        Assert.Equal(2, ((NbtList)read.Get("list")!).Items.Count);
    }

    [Fact]
    public void NBT文件读写走gzip且能自动识别()
    {
        var dir = TempDir();
        try
        {
            var path = Path.Combine(dir, "test.nbt");
            var root = new NbtCompound();
            root.Items.Add(new NbtString { Name = "hello", Value = "world" });
            Nbt.WriteFile(path, root);

            // 文件头是 gzip magic
            var head = File.ReadAllBytes(path).Take(2).ToArray();
            Assert.Equal([0x1F, 0x8B], head);
            Assert.Equal("world", Nbt.ReadFile(path).GetString("hello"));
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── BlockStates 位打包 ──

    [Theory]
    [InlineData(4)]    // 2 bits
    [InlineData(5)]    // 3 bits
    [InlineData(17)]   // 5 bits
    [InlineData(300)]  // 9 bits
    public void 位打包与解包互逆(int paletteSize)
    {
        var bits = ProjectionReader.BitsNeeded(paletteSize);
        var random = new Random(paletteSize);
        var indices = Enumerable.Range(0, 4096).Select(_ => random.Next(paletteSize)).ToArray();

        var packed = ProjectionReader.Pack(indices, bits);
        var unpacked = ProjectionReader.Unpack(packed, bits, indices.Length, dataVersion: 3953);
        Assert.Equal(indices, unpacked);
    }

    [Fact]
    public void 逐位读取与整体解包结果一致()
    {
        // 逐位读取（ReadAt）是超大投影唯一可行的取法，必须和 Unpack 完全等价
        foreach (var (paletteSize, dataVersion) in new[] { (17, 3953), (300, 3953), (5, 2000), (40, 2000) })
        {
            var bits = ProjectionReader.BitsNeeded(paletteSize);
            var random = new Random(paletteSize * 7 + dataVersion);
            var indices = Enumerable.Range(0, 4096).Select(_ => random.Next(paletteSize)).ToArray();
            // Pack 写的是 1.16+ 连续布局；旧版用例要按旧式（每 long 独立）自己拼
            var packed = dataVersion >= 2529 ? ProjectionReader.Pack(indices, bits) : PackLegacy(indices, bits);
            var unpacked = ProjectionReader.Unpack(packed, bits, indices.Length, dataVersion);
            for (var i = 0; i < indices.Length; i++)
                Assert.Equal(unpacked[i], ProjectionReader.ReadAt(packed, bits, i, dataVersion));
        }
    }

    [Fact]
    public void 连续打包按位流计算长度()
    {
        // 5 bit 是两种布局差异最大的情形：连续打包 128 项 = 640 bit = 10 个 long，
        // 旧式每 long 只装 12 项，需要 11 个。长度写错就说明布局又反了。
        var indices = Enumerable.Repeat(1, 128).ToArray();
        Assert.Equal(10, ProjectionReader.Pack(indices, 5).Length);
    }

    /// <summary>旧式布局（每个 long 内独立打包，剩余位丢弃）——只用于测试</summary>
    private static long[] PackLegacy(int[] indices, int bits)
    {
        var perLong = 64 / bits;
        var data = new long[(indices.Length + perLong - 1) / perLong];
        var mask = (1UL << bits) - 1;
        for (var i = 0; i < indices.Length; i++)
        {
            var longIndex = i / perLong;
            var offset = (i % perLong) * bits;
            data[longIndex] |= (long)(((ulong)indices[i] & mask) << offset);
        }
        return data;
    }

    [Fact]
    public void 旧式跨长分割解包与手算一致()
    {
        // 3 bits、每 long 21 个条目：第 21 个条目跨 long（offset 63）
        var bits = 3;
        var indices = new[] { 1, 2, 3, 4, 5, 6, 7, 1, 2, 3, 4, 5, 6, 7, 1, 2, 3, 4, 5, 6, 7, 1 };
        // 按旧式打包：直接手写 bit 流（低位在前，跨长拼接）
        var perLong = 64 / bits;
        var data = new long[(indices.Length + perLong - 1) / perLong];
        for (var i = 0; i < indices.Length; i++)
        {
            var longIndex = i / perLong;
            var offset = (i % perLong) * bits;
            var value = (ulong)indices[i] & 0x7UL;
            if (offset + bits <= 64)
            {
                data[longIndex] |= (long)(value << offset);
            }
            else
            {
                var lowBits = 64 - offset;
                data[longIndex] |= (long)(value << offset);
                if (longIndex + 1 < data.Length)
                    data[longIndex + 1] |= (long)(value >> lowBits);
            }
        }

        var unpacked = ProjectionReader.Unpack(data, bits, indices.Length, dataVersion: 2000);
        Assert.Equal(indices, unpacked);
    }

    // ── litematic 解析与统计 ──

    /// <summary>造一个 4×2×4 的 litematic：一半石头、一半空气（索引 0 = air）</summary>
    private static string WriteSampleLitematic(string dir, string name = "小屋")
    {
        var palette = new NbtList
        {
            Name = "BlockStatePalette",
            ItemType = Nbt.TagCompound,
            Items =
            [
                (NbtTag)new NbtCompound { Items = [new NbtString { Name = "Name", Value = "minecraft:air" }] },
                new NbtCompound { Items = [new NbtString { Name = "Name", Value = "minecraft:stone" }] },
            ],
        };

        var (sx, sy, sz) = (4, 2, 4);
        var indices = new int[sx * sy * sz];
        for (var i = 0; i < indices.Length; i++) indices[i] = i % 2 == 0 ? 1 : 0;
        var bits = ProjectionReader.BitsNeeded(2);
        var packed = ProjectionReader.Pack(indices, bits);

        var region = new NbtCompound { Name = "Main" };
        region.Items.Add(new NbtCompound
        {
            Name = "Position",
            Items = [new NbtInt { Name = "x", Value = 0 }, new NbtInt { Name = "y", Value = 0 }, new NbtInt { Name = "z", Value = 0 }],
        });
        region.Items.Add(new NbtCompound
        {
            Name = "Size",
            Items = [new NbtInt { Name = "x", Value = sx }, new NbtInt { Name = "y", Value = sy }, new NbtInt { Name = "z", Value = sz }],
        });
        region.Items.Add(palette);
        region.Items.Add(new NbtLongArray { Name = "BlockStates", Value = packed });

        var metadata = new NbtCompound { Name = "Metadata" };
        metadata.Items.Add(new NbtString { Name = "Name", Value = name });
        metadata.Items.Add(new NbtString { Name = "Author", Value = "作者甲" });
        metadata.Items.Add(new NbtString { Name = "Description", Value = "测试投影" });
        metadata.Items.Add(new NbtInt { Name = "RegionCount", Value = 1 });
        metadata.Items.Add(new NbtInt { Name = "TotalBlocks", Value = 16 });
        metadata.Items.Add(new NbtCompound
        {
            Name = "EnclosingSize",
            Items = [new NbtInt { Name = "x", Value = sx }, new NbtInt { Name = "y", Value = sy }, new NbtInt { Name = "z", Value = sz }],
        });

        var regions = new NbtCompound { Name = "Regions" };
        regions.Items.Add(region);

        var root = new NbtCompound();
        root.Items.Add(new NbtInt { Name = "MinecraftDataVersion", Value = 3953 });
        root.Items.Add(new NbtInt { Name = "Version", Value = 6 });
        root.Items.Add(metadata);
        root.Items.Add(regions);

        var path = Path.Combine(dir, name + ".litematic");
        Nbt.WriteFile(path, root);
        return path;
    }

    [Fact]
    public void 解析litematic得到元数据尺寸与方块统计()
    {
        var dir = TempDir();
        try
        {
            var path = WriteSampleLitematic(dir);
            var info = ProjectionReader.Read(path);

            Assert.Equal(ProjectionFormat.Litematic, info.Format);
            Assert.Equal("小屋", info.Name);
            Assert.Equal("作者甲", info.Author);
            Assert.Equal((4, 2, 4), info.Size);
            Assert.Equal(1, info.RegionCount);
            // 32 格中一半是石头（空气不计）
            Assert.Equal(16, info.TotalBlocks);
            Assert.Single(info.TopBlocks);
            Assert.Equal("minecraft:stone", info.TopBlocks[0].BlockId);
            Assert.Equal(16, info.TopBlocks[0].Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 改元数据后重新解析能读回()
    {
        var dir = TempDir();
        try
        {
            var path = WriteSampleLitematic(dir);
            ProjectionWriter.UpdateLitematicMetadata(path, "新名字", "作者乙", "新描述");

            var info = ProjectionReader.Read(path);
            Assert.Equal("新名字", info.Name);
            Assert.Equal("作者乙", info.Author);
            Assert.Equal("新描述", info.Description);
            // 内容未被破坏：方块统计仍在
            Assert.Equal(16, info.TotalBlocks);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── 结构 nbt 解析与转换回环 ──

    [Fact]
    public void 结构nbt转litematic再转回来方块数一致()
    {
        var dir = TempDir();
        try
        {
            // 造 3×1×2 结构：4 个石头 + 2 个橡木（无空气条目 → 转换时补空气）
            var blocks = new NbtList { Name = "blocks", ItemType = Nbt.TagCompound };
            (int X, int Y, int Z, int State)[] cells =
            [
                (0, 0, 0, 0), (1, 0, 0, 0), (2, 0, 0, 1), (0, 0, 1, 0), (1, 0, 1, 0), (2, 0, 1, 1),
            ];
            foreach (var c in cells)
            {
                var b = new NbtCompound();
                b.Items.Add(new NbtList
                {
                    Name = "pos",
                    ItemType = Nbt.TagInt,
                    Items = [new NbtInt { Value = c.X }, new NbtInt { Value = c.Y }, new NbtInt { Value = c.Z }],
                });
                b.Items.Add(new NbtInt { Name = "state", Value = c.State });
                blocks.Items.Add(b);
            }

            var root = new NbtCompound();
            root.Items.Add(new NbtInt { Name = "DataVersion", Value = 3953 });
            root.Items.Add(new NbtList
            {
                Name = "size",
                ItemType = Nbt.TagInt,
                Items = [new NbtInt { Value = 3 }, new NbtInt { Value = 1 }, new NbtInt { Value = 2 }],
            });
            root.Items.Add(new NbtList
            {
                Name = "palette",
                ItemType = Nbt.TagCompound,
                Items =
                [
                    (NbtTag)new NbtCompound { Items = [new NbtString { Name = "Name", Value = "minecraft:stone" }] },
                    new NbtCompound { Items = [new NbtString { Name = "Name", Value = "minecraft:oak_planks" }] },
                ],
            });
            root.Items.Add(blocks);
            var nbtPath = Path.Combine(dir, "sample.nbt");
            Nbt.WriteFile(nbtPath, root);

            // 结构 → litematic
            var litematicPath = Path.Combine(dir, "converted.litematic");
            ProjectionWriter.StructureNbtToLitematic(nbtPath, litematicPath);
            var asLitematic = ProjectionReader.Read(litematicPath);
            Assert.Equal(ProjectionFormat.Litematic, asLitematic.Format);
            Assert.Equal((3, 1, 2), asLitematic.Size);
            Assert.Equal(6, asLitematic.TotalBlocks);

            // litematic → 结构 nbt（回环）
            var backPath = Path.Combine(dir, "back.nbt");
            ProjectionWriter.LitematicToStructureNbt(litematicPath, backPath);
            var back = ProjectionReader.Read(backPath);
            Assert.Equal(ProjectionFormat.StructureNbt, back.Format);
            Assert.Equal((3, 1, 2), back.Size);
            Assert.Equal(6, back.TotalBlocks);
            Assert.Equal(4, back.TopBlocks.First(b => b.BlockId == "minecraft:stone").Count);
            Assert.Equal(2, back.TopBlocks.First(b => b.BlockId == "minecraft:oak_planks").Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 方块状态带属性时调色板保留属性()
    {
        var dir = TempDir();
        try
        {
            var palette = new NbtList
            {
                Name = "palette",
                ItemType = Nbt.TagCompound,
                Items =
                [
                    (NbtTag)new NbtCompound
                    {
                        Items =
                        [
                            new NbtString { Name = "Name", Value = "minecraft:oak_log" },
                            new NbtCompound
                            {
                                Name = "Properties",
                                Items = [new NbtString { Name = "axis", Value = "y" }],
                            },
                        ],
                    },
                ],
            };
            var blocks = new NbtList { Name = "blocks", ItemType = Nbt.TagCompound };
            var b = new NbtCompound();
            b.Items.Add(new NbtList
            {
                Name = "pos",
                ItemType = Nbt.TagInt,
                Items = [new NbtInt { Value = 0 }, new NbtInt { Value = 0 }, new NbtInt { Value = 0 }],
            });
            b.Items.Add(new NbtInt { Name = "state", Value = 0 });
            blocks.Items.Add(b);

            var root = new NbtCompound();
            root.Items.Add(new NbtList
            {
                Name = "size",
                ItemType = Nbt.TagInt,
                Items = [new NbtInt { Value = 1 }, new NbtInt { Value = 1 }, new NbtInt { Value = 1 }],
            });
            root.Items.Add(palette);
            root.Items.Add(blocks);
            var path = Path.Combine(dir, "prop.nbt");
            Nbt.WriteFile(path, root);

            var info = ProjectionReader.Read(path);
            Assert.Equal("minecraft:oak_log[axis=y]", info.TopBlocks[0].BlockId);
        }
        finally { Directory.Delete(dir, true); }
    }


    [Fact]
    public void 服务扫描目录并按扩展名过滤()
    {
        var dir = TempDir();
        try
        {
            var dotMc = Path.Combine(dir, ".minecraft");
            var schematics = Path.Combine(dotMc, "schematics");
            Directory.CreateDirectory(schematics);
            WriteSampleLitematic(schematics);
            File.WriteAllText(Path.Combine(schematics, "readme.txt"), "not a projection");

            var settings = new LauncherSettings { DotMinecraft = dotMc };
            var service = new ProjectionService(() => settings);
            var list = service.List();

            Assert.Single(list);
            Assert.Equal("小屋", list[0].Name);
            Assert.True(ProjectionService.CanConvert(ProjectionFormat.Litematic));
            Assert.False(ProjectionService.CanConvert(ProjectionFormat.LegacySchematic));
        }
        finally { Directory.Delete(dir, true); }
    }
}
