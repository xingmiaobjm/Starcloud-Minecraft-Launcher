// 投影文件解析与编辑（「实验室」核心，类似美西螈启动器的 Axolotl 实验室）：
//   · .litematic  —— Litematica 投影（NBT：Metadata + Regions → Palette + BlockStates）
//   · .nbt        —— 结构方块结构（1.13+：size + palette + blocks 列表；旧版：BlockStates/Palette）
//   · .schematic  —— MCEdit 旧格式（数字 ID，只读元信息）
//   · .schem      —— Sponge V2（Palette + varint BlockData）
//
// 能做的事（M1）：读信息（尺寸 / 区域 / 总方块 / 调色板与方块统计）、改元数据（名称/作者/描述）、
// 在 litematic 与结构 nbt 之间互转（两者调色板都是 blockstate 字符串，转换可靠）。
//
// 位打包（BlockStates）：1.16（DataVersion ≥ 2529）起为"连续打包"（一个条目不跨 long）；
// 更早为"跨长分割"。两种解包都实现，按 DataVersion 选择；写出统一用连续打包（现代格式）。

using System.Text;

namespace Starcloud.Core.Services.Projection;

public enum ProjectionFormat
{
    Litematic,
    StructureNbt,
    LegacySchematic,
    SpongeSchem,
    Unknown,
}

/// <summary>一种方块在投影里的出现次数</summary>
public sealed record BlockCount(string BlockId, int Count);

/// <summary>投影文件解析结果（只读信息 + 元数据快照）</summary>
public sealed class ProjectionInfo
{
    public string FilePath { get; init; } = "";
    public ProjectionFormat Format { get; init; }
    public string Name { get; set; } = "";
    public string Author { get; set; } = "";
    public string Description { get; set; } = "";
    /// <summary>包围盒尺寸（x, y, z）</summary>
    public (int X, int Y, int Z) Size { get; init; }
    public int RegionCount { get; init; }
    public int TotalBlocks { get; init; }
    public int DataVersion { get; init; }
    public long FileSizeBytes { get; init; }
    public DateTime ModifiedAt { get; init; }
    /// <summary>方块统计（按出现次数倒序）</summary>
    public IReadOnlyList<BlockCount> TopBlocks { get; init; } = [];
    public int PaletteSize { get; init; }

    public string FormatName => Format switch
    {
        ProjectionFormat.Litematic => "Litematica 投影（.litematic）",
        ProjectionFormat.StructureNbt => "结构方块结构（.nbt）",
        ProjectionFormat.LegacySchematic => "MCEdit 旧格式（.schematic）",
        ProjectionFormat.SpongeSchem => "Sponge V2（.schem）",
        _ => "未知格式",
    };
}

public static class ProjectionReader
{
    public static ProjectionFormat DetectFormat(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext == ".litematic") return ProjectionFormat.Litematic;
        if (ext == ".schematic") return ProjectionFormat.LegacySchematic;
        if (ext == ".schem") return ProjectionFormat.SpongeSchem;
        if (ext != ".nbt") return ProjectionFormat.Unknown;

        // .nbt：结构方块结构有 size + palette；litematic 改名成 .nbt 的情况也有（按结构判别）
        try
        {
            var root = Nbt.ReadFile(path);
            if (root.Get("Regions") is NbtCompound) return ProjectionFormat.Litematic;
            if (root.Get("size") is not null || root.Get("palette") is not null) return ProjectionFormat.StructureNbt;
        }
        catch
        {
            // 解析不了就按未知处理
        }
        return ProjectionFormat.Unknown;
    }

    /// <summary>解析任意支持的投影文件为信息快照</summary>
    public static ProjectionInfo Read(string path)
    {
        var format = DetectFormat(path);
        var info = new FileInfo(path);
        return format switch
        {
            ProjectionFormat.Litematic => ReadLitematic(path, info),
            ProjectionFormat.StructureNbt => ReadStructureNbt(path, info),
            ProjectionFormat.LegacySchematic => ReadLegacySchematic(path, info),
            ProjectionFormat.SpongeSchem => ReadSpongeSchem(path, info),
            _ => throw new InvalidOperationException("不认识的文件格式（支持 .litematic / .nbt / .schematic / .schem）"),
        };
    }

    // ── Litematica ──

    private static ProjectionInfo ReadLitematic(string path, FileInfo file)
    {
        var root = Nbt.ReadFile(path);
        var meta = root.GetCompound("Metadata");
        var regions = root.GetCompound("Regions") ?? new NbtCompound();
        var dataVersion = root.GetInt("MinecraftDataVersion", 0);

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var paletteTotal = 0;
        var totalBlocks = 0;
        var minX = int.MaxValue; var minY = int.MaxValue; var minZ = int.MaxValue;
        var maxX = int.MinValue; var maxY = int.MinValue; var maxZ = int.MinValue;
        var regionCount = 0;

        foreach (var tag in regions.Items)
        {
            if (tag is not NbtCompound region) continue;
            regionCount++;
            var regionSize = region.GetCompound("Size");
            var pos = region.GetCompound("Position");
            var sx = Math.Abs(regionSize?.GetInt("x", 0) ?? 0);
            var sy = Math.Abs(regionSize?.GetInt("y", 0) ?? 0);
            var sz = Math.Abs(regionSize?.GetInt("z", 0) ?? 0);
            var px = pos?.GetInt("x", 0) ?? 0;
            var py = pos?.GetInt("y", 0) ?? 0;
            var pz = pos?.GetInt("z", 0) ?? 0;
            minX = Math.Min(minX, Math.Min(px, px + sx)); maxX = Math.Max(maxX, Math.Max(px, px + sx));
            minY = Math.Min(minY, Math.Min(py, py + sy)); maxY = Math.Max(maxY, Math.Max(py, py + sy));
            minZ = Math.Min(minZ, Math.Min(pz, pz + sz)); maxZ = Math.Max(maxZ, Math.Max(pz, pz + sz));

            var palette = PaletteNames(region.Get("BlockStatePalette"));
            paletteTotal += palette.Count;
            var blockStates = region.Get("BlockStates") as NbtLongArray;
            CountBlocks(palette, blockStates?.Value ?? [], (long)sx * sy * sz, dataVersion, counts, ref totalBlocks);
        }

        var enclosing = meta?.GetCompound("EnclosingSize");
        var size = enclosing is not null
            ? (Math.Abs(enclosing.GetInt("x", 0)), Math.Abs(enclosing.GetInt("y", 0)), Math.Abs(enclosing.GetInt("z", 0)))
            : regionCount > 0
                ? (maxX - minX, maxY - minY, maxZ - minZ)
                : (0, 0, 0);

        return new ProjectionInfo
        {
            FilePath = path,
            Format = ProjectionFormat.Litematic,
            Name = meta?.GetString("Name") ?? Path.GetFileNameWithoutExtension(path),
            Author = meta?.GetString("Author") ?? "",
            Description = meta?.GetString("Description") ?? "",
            Size = size,
            RegionCount = regionCount,
            // 用实际统计值：投影文件自带的 Metadata.TotalBlocks 是生成器写的，可能和内容对不上
            // （实测 redenmc 生成的两个文件都写着 114514，而实际只有几千个方块）
            TotalBlocks = totalBlocks,
            DataVersion = dataVersion,
            FileSizeBytes = file.Length,
            ModifiedAt = file.LastWriteTime,
            TopBlocks = TopBlocks(counts),
            PaletteSize = paletteTotal,
        };
    }

    // ── 结构方块结构（.nbt） ──

    private static ProjectionInfo ReadStructureNbt(string path, FileInfo file)
    {
        var root = Nbt.ReadFile(path);
        var sizeArr = (root.Get("size") as NbtList)?.Items.OfType<NbtInt>().Select(i => i.Value).ToArray() ?? [];
        var size = sizeArr.Length >= 3 ? (sizeArr[0], sizeArr[1], sizeArr[2]) : (0, 0, 0);
        var palette = PaletteNames(root.Get("palette"));

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        if (root.Get("blocks") is NbtList blocks)
        {
            foreach (var item in blocks.Items)
            {
                if (item is not NbtCompound block) continue;
                var state = block.GetInt("state", -1);
                if (state < 0 || state >= palette.Count) continue;
                var id = palette[state];
                counts[id] = counts.TryGetValue(id, out var n) ? n + 1 : 1;
                total++;
            }
        }
        else if (root.Get("BlockStates") is NbtLongArray packed)
        {
            // 旧版结构（1.13 前）：BlockStates + Palette，与 litematic 同一套解包
            CountBlocks(palette, packed.Value, (long)size.Item1 * size.Item2 * size.Item3,
                root.GetInt("DataVersion", 0), counts, ref total);
        }

        return new ProjectionInfo
        {
            FilePath = path,
            Format = ProjectionFormat.StructureNbt,
            Name = Path.GetFileNameWithoutExtension(path),
            Author = root.GetString("author"),
            Description = "",
            Size = size,
            RegionCount = 1,
            TotalBlocks = total,
            DataVersion = root.GetInt("DataVersion", 0),
            FileSizeBytes = file.Length,
            ModifiedAt = file.LastWriteTime,
            TopBlocks = TopBlocks(counts),
            PaletteSize = palette.Count,
        };
    }

    // ── MCEdit 旧格式（数字 ID，只读元信息） ──

    private static ProjectionInfo ReadLegacySchematic(string path, FileInfo file)
    {
        var root = Nbt.ReadFile(path);
        var w = root.GetInt("Width");
        var h = root.GetInt("Height");
        var l = root.GetInt("Length");
        var blocks = root.Get("Blocks") as NbtByteArray;
        return new ProjectionInfo
        {
            FilePath = path,
            Format = ProjectionFormat.LegacySchematic,
            Name = Path.GetFileNameWithoutExtension(path),
            Author = "",
            Description = "MCEdit 旧格式（数字方块 ID）：只读信息，建议在 MCEdit / 结构方块里转换为现代格式",
            Size = (w, h, l),
            RegionCount = 1,
            TotalBlocks = blocks?.Value.Length ?? w * h * l,
            DataVersion = root.GetInt("DataVersion", 0),
            FileSizeBytes = file.Length,
            ModifiedAt = file.LastWriteTime,
            TopBlocks = [],
            PaletteSize = 0,
        };
    }

    // ── Sponge V2（.schem） ──

    private static ProjectionInfo ReadSpongeSchem(string path, FileInfo file)
    {
        var root = Nbt.ReadFile(path);
        var w = root.GetInt("Width");
        var h = root.GetInt("Height");
        var l = root.GetInt("Length");

        // Palette：blockstate 字符串 → 索引；BlockData 是 varint 编码的索引序列
        var indexToName = new Dictionary<int, string>();
        if (root.Get("Palette") is NbtCompound palette)
        {
            foreach (var item in palette.Items)
            {
                if (item is NbtInt idx) indexToName[idx.Value] = item.Name;
            }
        }

        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var total = 0;
        if (root.Get("BlockData") is NbtByteArray data && indexToName.Count > 0)
        {
            var i = 0;
            var expected = w * h * l;
            while (i < data.Value.Length && total < expected)
            {
                var value = 0;
                var shift = 0;
                while (i < data.Value.Length)
                {
                    var b = data.Value[i++];
                    value |= (b & 0x7F) << shift;
                    if ((b & 0x80) == 0) break;
                    shift += 7;
                }
                var id = indexToName.TryGetValue(value, out var n) ? n : $"#{value}";
                counts[id] = counts.TryGetValue(id, out var c) ? c + 1 : 1;
                total++;
            }
        }

        return new ProjectionInfo
        {
            FilePath = path,
            Format = ProjectionFormat.SpongeSchem,
            Name = Path.GetFileNameWithoutExtension(path),
            Author = root.GetString("author"),
            Description = "",
            Size = (w, h, l),
            RegionCount = 1,
            TotalBlocks = total,
            DataVersion = root.GetInt("DataVersion", 0),
            FileSizeBytes = file.Length,
            ModifiedAt = file.LastWriteTime,
            TopBlocks = TopBlocks(counts),
            PaletteSize = indexToName.Count,
        };
    }

    // ── 公共：调色板与方块统计 ──

    /// <summary>调色板（list of Compound{Name, Properties}）→ blockstate 字符串表（转换器共用）</summary>
    internal static List<string> PaletteNames(NbtTag? tag)
    {
        var result = new List<string>();
        if (tag is not NbtList list) return result;
        foreach (var item in list.Items)
        {
            if (item is not NbtCompound entry)
            {
                result.Add("minecraft:air");
                continue;
            }
            var name = entry.GetString("Name", "minecraft:air");
            if (entry.Get("Properties") is NbtCompound props && props.Items.Count > 0)
            {
                var sb = new StringBuilder(name);
                sb.Append('[');
                var first = true;
                foreach (var p in props.Items.OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    if (!first) sb.Append(',');
                    first = false;
                    sb.Append(p.Name).Append('=').Append(p is NbtString s ? s.Value : "");
                }
                sb.Append(']');
                name = sb.ToString();
            }
            result.Add(name);
        }
        return result;
    }

    /// <summary>统计扫描时最多逐格看的数量：超过就按步长抽样（估算值），避免超大投影把界面拖死</summary>
    private const long CountScanLimit = 8_000_000;

    /// <summary>
    /// 抽样步长取质数：机器类投影（世吞这种）方块排布有周期性，用 8/21 这类合数步长会共振，
    /// 系统性漏掉整列，数量能偏小好几倍（实测 26 万格估成 8 万）。质数步长把相位打散。
    /// </summary>
    internal static int PrimeStride(long volume, long limit)
    {
        if (volume <= limit) return 1;
        var n = (int)Math.Max(2, volume / limit);
        while (!IsPrime(n)) n++;
        return n;
    }

    private static bool IsPrime(int n)
    {
        if (n < 2) return false;
        if (n % 2 == 0) return n == 2;
        for (var d = 3; (long)d * d <= n; d += 2)
            if (n % d == 0) return false;
        return true;
    }

    /// <summary>
    /// 按调色板统计方块（跳过空气；空气名判定兼容带命名空间与不带两种写法）。
    /// 用 <see cref="ReadAt"/> 逐位取，不整块解包——超大投影只抽样统计（数量按步长放大）。
    /// </summary>
    private static void CountBlocks(
        List<string> palette, long[] blockStates, long volume, int dataVersion,
        Dictionary<string, int> counts, ref int total)
    {
        if (palette.Count == 0 || volume <= 0) return;
        var bits = Math.Max(2, BitsNeeded(palette.Count));
        var stride = PrimeStride(volume, CountScanLimit);
        for (long i = 0; i < volume; i += stride)
        {
            var idx = ReadAt(blockStates, bits, i, dataVersion);
            if (idx < 0 || idx >= palette.Count) continue;
            var id = palette[idx];
            if (IsAir(id)) continue;
            counts[id] = counts.TryGetValue(id, out var n) ? n + stride : stride;
            total += stride;
        }
    }

    public static bool IsAir(string blockId) =>
        blockId is "minecraft:air" or "air" or "minecraft:cave_air" or "minecraft:void_air";

    public static int BitsNeeded(int paletteSize)
    {
        var bits = 0;
        var v = Math.Max(1, paletteSize - 1);
        while (v > 0) { bits++; v >>= 1; }
        return Math.Max(2, bits);
    }

    /// <summary>
    /// 解包 BlockStates。**1.16+（DataVersion ≥ 2529）是连续打包**：条目按 bit 流依次排列，
    /// 一个条目可以跨两个 long；更早的旧式是「每个 long 内独立打包，剩余位丢弃」。
    /// 两者只在 bits 不整除 64 时不同（5/6/7 bit 很常见），搞反了整片方块都会错位。
    /// </summary>
    public static int[] Unpack(long[] data, int bits, int count, int dataVersion)
    {
        var result = new int[count];
        if (bits <= 0 || data.Length == 0) return result;
        var mask = (1UL << bits) - 1;

        if (dataVersion >= 2529)
        {
            for (var i = 0; i < count; i++)
            {
                var bitIndex = (long)i * bits;
                var longIndex = bitIndex >> 6;
                if (longIndex >= data.Length) break;
                var offset = (int)(bitIndex & 63);
                var raw = (ulong)data[longIndex] >> offset;
                if (offset + bits > 64 && longIndex + 1 < data.Length)
                    raw |= (ulong)data[longIndex + 1] << (64 - offset);
                result[i] = (int)(raw & mask);
            }
            return result;
        }

        // 旧式：每个 long 内独立打包，剩余位丢弃
        var perLong = 64 / bits;
        for (var i = 0; i < count; i++)
        {
            var longIndex = i / perLong;
            if (longIndex >= data.Length) break;
            var offset = (i % perLong) * bits;
            result[i] = (int)((ulong)data[longIndex] >> offset & mask);
        }
        return result;
    }

    /// <summary>
    /// 只解某一个位置的状态索引（不整块解包）。超大投影（上亿格）必须先量再按需取：
    /// 一次性 Unpack 会分配几百 MB 的 int 数组，直接把启动器拖死（实测 998×81×2006 踩过）。
    /// 位布局与 <see cref="Unpack"/> 完全一致。
    /// </summary>
    public static int ReadAt(long[] data, int bits, long index, int dataVersion)
    {
        if (bits <= 0 || data.Length == 0 || index < 0) return 0;
        var mask = (1UL << bits) - 1;

        if (dataVersion >= 2529)
        {
            var bitIndex = index * bits;
            var longIndex = bitIndex >> 6;
            if (longIndex >= data.Length) return 0;
            var offset = (int)(bitIndex & 63);
            var raw = (ulong)data[longIndex] >> offset;
            if (offset + bits > 64 && longIndex + 1 < data.Length)
                raw |= (ulong)data[longIndex + 1] << (64 - offset);
            return (int)(raw & mask);
        }

        var perLong = 64 / bits;
        var li = index / perLong;
        if (li >= data.Length) return 0;
        var off = (int)((index % perLong) * bits);
        return (int)((ulong)data[li] >> off & mask);
    }

    /// <summary>打包（1.16+ 连续打包）：与 Unpack 的现代分支互逆</summary>
    public static long[] Pack(int[] indices, int bits)
    {
        bits = Math.Max(2, bits);
        var data = new long[((long)indices.Length * bits + 63) / 64];
        var mask = (1UL << bits) - 1;
        for (var i = 0; i < indices.Length; i++)
        {
            var bitIndex = (long)i * bits;
            var longIndex = (int)(bitIndex >> 6);
            var offset = (int)(bitIndex & 63);
            var value = (ulong)indices[i] & mask;
            data[longIndex] |= (long)(value << offset);
            if (offset + bits > 64 && longIndex + 1 < data.Length)
                data[longIndex + 1] |= (long)(value >> (64 - offset));
        }
        return data;
    }

    private static IReadOnlyList<BlockCount> TopBlocks(Dictionary<string, int> counts, int take = 24) =>
        counts.OrderByDescending(kv => kv.Value)
              .ThenBy(kv => kv.Key, StringComparer.Ordinal)
              .Take(take)
              .Select(kv => new BlockCount(kv.Key, kv.Value))
              .ToList();
}
