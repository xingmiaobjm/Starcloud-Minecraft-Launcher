// 投影体素导出（3D 预览用）：只导出"非空气方块 + 完整 blockstate 字符串"。
//
// 方块外观（模型继承、元素旋转、多部件、UV、透明纹理）由第三方库 **deepslate**（MIT）
// 在页面里解析和渲染，这里不再自带几何/纹理解析——它已经把那些做对了，没必要重造。
//
// 体量守门：超大投影（实测 998×81×2006 = 1.6 亿格）**绝不能整块解包**成 int 数组
// （那是几百 MB 的分配，直接把启动器拖死），一律用 ProjectionReader.ReadAt 逐位读。
// 方块数在预算内就**全量直出**（用户要的"完全大小"），只有真的超预算才降采样并如实标记。

using Starcloud.Core.Services.Projection;

namespace Starcloud.Core.Services.Lab;

/// <summary>一个非空气方块（坐标已归一化到 0 起；<paramref name="State"/> 是完整 blockstate 字符串）</summary>
public sealed record VoxelBlock(int X, int Y, int Z, string State);

public sealed record ProjectionVoxels(
    int SizeX,
    int SizeY,
    int SizeZ,
    IReadOnlyList<VoxelBlock> Blocks,
    bool Truncated,
    string? Unsupported)
{
    /// <summary>涉及的方块种类（去重、去属性），用于收集资源</summary>
    public IEnumerable<string> BlockIds =>
        Blocks.Select(b => StripState(b.State)).Distinct(StringComparer.Ordinal);

    private static string StripState(string state)
    {
        var bracket = state.IndexOf('[');
        return bracket < 0 ? state : state[..bracket];
    }
}

public static class VoxelExporter
{
    /// <summary>直出的方块上限：低于此值全量渲染；超过才降采样（20 万实例浏览器扛得住）</summary>
    public const int MaxBlocks = 200_000;

    /// <summary>降采样路径单次扫描最多看多少格（按质数步长抽样，保证耗时可控）</summary>
    private const long ScanBudget = 60_000_000;

    public static ProjectionVoxels Read(string path, int maxBlocks = MaxBlocks, MinecraftAssets? assets = null)
    {
        var format = ProjectionReader.DetectFormat(path);
        return format switch
        {
            ProjectionFormat.Litematic => ReadLitematic(path, maxBlocks),
            ProjectionFormat.StructureNbt => ReadStructureNbt(path, maxBlocks),
            _ => Empty("该格式暂不支持 3D 预览（支持 .litematic 与结构 .nbt）"),
        };
    }

    private static ProjectionVoxels Empty(string? reason = null) => new(0, 0, 0, [], false, reason);

    /// <summary>一个区域：位置、尺寸、调色板、位打包数组</summary>
    private sealed record Region(int X, int Y, int Z, int Sx, int Sy, int Sz,
        List<string> Palette, long[] States, int Bits, bool[] AirMask)
    {
        public long Plane => (long)Sx * Sz;
        public long Volume => Plane * Sy;
    }

    private static ProjectionVoxels ReadLitematic(string path, int maxBlocks)
    {
        var root = Nbt.ReadFile(path);
        var regions = root.GetCompound("Regions") ?? new NbtCompound();
        var dataVersion = root.GetInt("MinecraftDataVersion", 0);

        // 只读区域元数据，不解包位数组
        var list = new List<Region>();
        long totalVolume = 0;
        foreach (var tag in regions.Items)
        {
            if (tag is not NbtCompound region) continue;
            var size = region.GetCompound("Size");
            var pos = region.GetCompound("Position");
            var sx = Math.Abs(size?.GetInt("x", 0) ?? 0);
            var sy = Math.Abs(size?.GetInt("y", 0) ?? 0);
            var sz = Math.Abs(size?.GetInt("z", 0) ?? 0);
            if (sx <= 0 || sy <= 0 || sz <= 0) continue;
            var palette = ProjectionReader.PaletteNames(region.Get("BlockStatePalette"));
            if (palette.Count == 0) continue;
            var states = (region.Get("BlockStates") as NbtLongArray)?.Value ?? [];
            if (states.Length == 0) continue;
            // 空气判定只做一次：全量扫描 1.6 亿格时，每个位置都做字符串比较会慢到一分钟
            var airMask = new bool[palette.Count];
            for (var i = 0; i < palette.Count; i++) airMask[i] = ProjectionReader.IsAir(palette[i]);
            list.Add(new Region(pos?.GetInt("x", 0) ?? 0, pos?.GetInt("y", 0) ?? 0, pos?.GetInt("z", 0) ?? 0,
                sx, sy, sz, palette, states, Math.Max(2, ProjectionReader.BitsNeeded(palette.Count)), airMask));
            totalVolume += (long)sx * sy * sz;
        }
        if (list.Count == 0) return Empty();

        // ① 全量直出：边扫边收，超预算就放弃（只多扫一遍，不做无谓的大分配）
        var all = CollectAll(list, dataVersion, maxBlocks);
        if (all is { Count: > 0 })
        {
            var (minX, minY, minZ, maxX, maxY, maxZ) = Bounds(all);
            return Normalize(all, minX, minY, minZ, maxX - minX + 1, maxY - minY + 1, maxZ - minZ + 1, false);
        }

        // ② 超大投影（方块数超预算）：占用格子降采样，保住整体轮廓
        var stride = ProjectionReader.PrimeStride(totalVolume, ScanBudget);
        var measured = Measure(list, dataVersion, stride);
        if (measured.NonAir <= 0) return Empty();
        var cell = Math.Max(1, (int)Math.Ceiling(Math.Cbrt((double)totalVolume / maxBlocks)));
        cell = Math.Max(cell, stride);
        var cells = CollectCells(list, dataVersion, stride, cell);
        if (cells.Count == 0) return Empty();

        var pad = Math.Max(stride, cell / 2);
        return Normalize(cells, measured.MinX - pad, measured.MinY - pad, measured.MinZ - pad,
            measured.MaxX - measured.MinX + 1 + pad * 2,
            measured.MaxY - measured.MinY + 1 + pad * 2,
            measured.MaxZ - measured.MinZ + 1 + pad * 2, true);
    }

    /// <summary>全量收集（逐位读，不整块解包）；超过预算返回 null</summary>
    private static List<(int X, int Y, int Z, string Id)>? CollectAll(
        List<Region> regions, int dataVersion, int limit)
    {
        var result = new List<(int, int, int, string)>(Math.Min(limit, 65536));
        foreach (var r in regions)
        {
            for (long i = 0; i < r.Volume; i++)
            {
                if (!TryBlockAt(r, i, dataVersion, out var x, out var y, out var z, out var id)) continue;
                if (result.Count >= limit) return null;
                result.Add((x, y, z, id));
            }
        }
        return result;
    }

    private static (int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ) Bounds(
        List<(int X, int Y, int Z, string Id)> blocks)
    {
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;
        foreach (var (x, y, z, _) in blocks)
        {
            if (x < minX) minX = x;
            if (x > maxX) maxX = x;
            if (y < minY) minY = y;
            if (y > maxY) maxY = y;
            if (z < minZ) minZ = z;
            if (z > maxZ) maxZ = z;
        }
        return (minX, minY, minZ, maxX, maxY, maxZ);
    }

    private static (long NonAir, int MinX, int MinY, int MinZ, int MaxX, int MaxY, int MaxZ) Measure(
        List<Region> regions, int dataVersion, int stride)
    {
        long nonAir = 0;
        var minX = int.MaxValue; var minY = int.MaxValue; var minZ = int.MaxValue;
        var maxX = int.MinValue; var maxY = int.MinValue; var maxZ = int.MinValue;

        foreach (var r in regions)
        {
            for (long i = 0; i < r.Volume; i += stride)
            {
                if (!TryBlockAt(r, i, dataVersion, out var x, out var y, out var z, out _)) continue;
                nonAir += stride;
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
                if (z < minZ) minZ = z;
                if (z > maxZ) maxZ = z;
            }
        }
        return (nonAir, minX, minY, minZ, maxX, maxY, maxZ);
    }

    /// <summary>每 k³ 格压成一个占用格（格子中心出一个方块，取格内首个方块的外观）</summary>
    private static List<(int X, int Y, int Z, string Id)> CollectCells(
        List<Region> regions, int dataVersion, int stride, int cell)
    {
        var cells = new Dictionary<(int, int, int), string>();
        foreach (var r in regions)
        {
            for (long i = 0; i < r.Volume; i += stride)
            {
                if (!TryBlockAt(r, i, dataVersion, out var x, out var y, out var z, out var id)) continue;
                var key = (FloorDiv(x, cell), FloorDiv(y, cell), FloorDiv(z, cell));
                if (!cells.ContainsKey(key)) cells[key] = id;
            }
        }

        var half = cell / 2;
        var result = new List<(int, int, int, string)>(cells.Count);
        foreach (var (key, id) in cells)
            result.Add((key.Item1 * cell + half, key.Item2 * cell + half, key.Item3 * cell + half, id));
        return result;
    }

    private static bool TryBlockAt(Region r, long index, int dataVersion,
        out int x, out int y, out int z, out string id)
    {
        x = y = z = 0;
        id = "";
        var state = ProjectionReader.ReadAt(r.States, r.Bits, index, dataVersion);
        if (state < 0 || state >= r.AirMask.Length) return false;
        if (r.AirMask[state]) return false;
        var name = r.Palette[state];
        y = (int)(index / r.Plane);
        var rem = index % r.Plane;
        z = (int)(rem / r.Sx);
        x = (int)(rem % r.Sx);
        x += r.X; y += r.Y; z += r.Z;
        id = name;
        return true;
    }

    private static int FloorDiv(int value, int divisor) =>
        (int)Math.Floor((double)value / divisor);

    private static ProjectionVoxels ReadStructureNbt(string path, int maxBlocks)
    {
        var root = Nbt.ReadFile(path);
        var sizeArr = (root.Get("size") as NbtList)?.Items.OfType<NbtInt>().Select(i => i.Value).ToArray() ?? [];
        if (sizeArr.Length < 3) return Empty("结构文件缺少 size");
        var (sx, sy, sz) = (sizeArr[0], sizeArr[1], sizeArr[2]);

        var palette = ProjectionReader.PaletteNames(root.Get("palette"));
        var blocks = new List<(int X, int Y, int Z, string Id)>();
        var truncated = false;
        if (root.Get("blocks") is NbtList list)
        {
            foreach (var item in list.Items)
            {
                if (item is not NbtCompound block) continue;
                var pos = (block.Get("pos") as NbtList)?.Items.OfType<NbtInt>().Select(i => i.Value).ToArray() ?? [];
                if (pos.Length < 3) continue;
                var state = block.GetInt("state", -1);
                if (state < 0 || state >= palette.Count) continue;
                var id = palette[state];
                if (ProjectionReader.IsAir(id)) continue;
                if (blocks.Count >= maxBlocks) { truncated = true; break; }
                blocks.Add((pos[0], pos[1], pos[2], id));
            }
        }

        if (blocks.Count == 0) return new ProjectionVoxels(sx, sy, sz, [], false, null);
        return Normalize(blocks, 0, 0, 0, sx, sy, sz, truncated);
    }

    private static ProjectionVoxels Normalize(
        List<(int X, int Y, int Z, string Id)> blocks,
        int minX, int minY, int minZ, int sizeX, int sizeY, int sizeZ, bool truncated)
    {
        var result = new List<VoxelBlock>(blocks.Count);
        foreach (var (x, y, z, id) in blocks)
            result.Add(new VoxelBlock(x - minX, y - minY, z - minZ, id));
        return new ProjectionVoxels(sizeX, sizeY, sizeZ, result, truncated, null);
    }
}
