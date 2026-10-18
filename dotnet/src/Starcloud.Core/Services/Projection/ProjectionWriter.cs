// 投影写入与转换：元数据回写（.litematic）、litematic ↔ 结构 nbt 互转。
//
// 两者调色板都是 blockstate 字符串、坐标都是整数格——转换是"解包 → 重排 → 重打包"，
// 不涉及任何方块 ID 映射表，所以可靠（.schematic 那种数字 ID 旧格式才需要映射，故只读不转）。

namespace Starcloud.Core.Services.Projection;

public static class ProjectionWriter
{
    /// <summary>
    /// 回写 .litematic 的元数据（Name / Author / Description）。
    /// 其余标签原样保留——解析成 NBT 树后只改三个字段，不重建结构。
    /// </summary>
    public static void UpdateLitematicMetadata(string path, string name, string author, string description)
    {
        var root = Nbt.ReadFile(path);
        if (root.Get("Regions") is not NbtCompound)
            throw new InvalidOperationException("这不是 .litematic 投影文件（缺少 Regions）");
        var meta = root.GetCompound("Metadata") ?? new NbtCompound { Name = "Metadata" };
        meta.Set(new NbtString { Name = "Name", Value = name });
        meta.Set(new NbtString { Name = "Author", Value = author });
        meta.Set(new NbtString { Name = "Description", Value = description });
        meta.Set(new NbtLong { Name = "TimeModified", Value = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() });
        root.Set(meta);
        Nbt.WriteFile(path, root);
    }

    /// <summary>
    /// .litematic → 结构方块 .nbt：多区域按包围盒合并，只输出非空气方块
    /// （结构方块保存的惯例就是忽略空气）。
    /// </summary>
    public static string LitematicToStructureNbt(string sourcePath, string destPath)
    {
        var root = Nbt.ReadFile(sourcePath);
        if (root.GetCompound("Regions") is not { } regions || regions.Items.Count == 0)
            throw new InvalidOperationException("投影里没有区域，无法转换");

        // ① 收集所有区域到统一网格（记录包围盒）
        var cells = new Dictionary<(int X, int Y, int Z), string>();
        var dataVersion = root.GetInt("MinecraftDataVersion", 0);
        int minX = int.MaxValue, minY = int.MaxValue, minZ = int.MaxValue;
        int maxX = int.MinValue, maxY = int.MinValue, maxZ = int.MinValue;

        foreach (var tag in regions.Items)
        {
            if (tag is not NbtCompound region) continue;
            var size = region.GetCompound("Size");
            var pos = region.GetCompound("Position");
            var sx = Math.Abs(size?.GetInt("x", 0) ?? 0);
            var sy = Math.Abs(size?.GetInt("y", 0) ?? 0);
            var sz = Math.Abs(size?.GetInt("z", 0) ?? 0);
            if (sx <= 0 || sy <= 0 || sz <= 0) continue;
            var px = pos?.GetInt("x", 0) ?? 0;
            var py = pos?.GetInt("y", 0) ?? 0;
            var pz = pos?.GetInt("z", 0) ?? 0;

            var palette = ProjectionReader.PaletteNames(region.Get("BlockStatePalette"));
            var bits = ProjectionReader.BitsNeeded(palette.Count);
            var states = ProjectionReader.Unpack(
                (region.Get("BlockStates") as NbtLongArray)?.Value ?? [], bits, sx * sy * sz, dataVersion);

            // Litematica 的坐标是"区域内的最小角"：x/z 递增方向与 Size 正负一致，这里按正方向展开
            var i = 0;
            for (var y = 0; y < sy; y++)
            for (var z = 0; z < sz; z++)
            for (var x = 0; x < sx; x++, i++)
            {
                if (i >= states.Length) break;
                var idx = states[i];
                if (idx < 0 || idx >= palette.Count) continue;
                var id = palette[idx];
                if (ProjectionReader.IsAir(id)) continue;
                var wx = px + x; var wy = py + y; var wz = pz + z;
                cells[(wx, wy, wz)] = id;
                minX = Math.Min(minX, wx); maxX = Math.Max(maxX, wx);
                minY = Math.Min(minY, wy); maxY = Math.Max(maxY, wy);
                minZ = Math.Min(minZ, wz); maxZ = Math.Max(maxZ, wz);
            }
        }

        if (cells.Count == 0) throw new InvalidOperationException("投影里没有非空气方块，无法转换");

        // ② 调色板（稳定排序：名称序，保证输出可复现）
        var paletteList = cells.Values.Distinct(StringComparer.Ordinal)
            .OrderBy(s => s, StringComparer.Ordinal).ToList();
        var paletteIndex = paletteList
            .Select((name, index) => (name, index))
            .ToDictionary(t => t.name, t => t.index, StringComparer.Ordinal);

        // ③ 方块列表（相对坐标）
        var blocks = new NbtList { Name = "blocks", ItemType = Nbt.TagCompound };
        foreach (var kv in cells.OrderBy(k => k.Key.Y).ThenBy(k => k.Key.Z).ThenBy(k => k.Key.X))
        {
            var block = new NbtCompound();
            block.Items.Add(new NbtList
            {
                Name = "pos",
                ItemType = Nbt.TagInt,
                Items =
                [
                    new NbtInt { Value = kv.Key.X - minX },
                    new NbtInt { Value = kv.Key.Y - minY },
                    new NbtInt { Value = kv.Key.Z - minZ },
                ],
            });
            block.Items.Add(new NbtInt { Name = "state", Value = paletteIndex[kv.Value] });
            blocks.Items.Add(block);
        }

        var meta = root.GetCompound("Metadata");
        var outRoot = new NbtCompound();
        outRoot.Items.Add(new NbtInt { Name = "DataVersion", Value = dataVersion > 0 ? dataVersion : 3953 });
        outRoot.Items.Add(new NbtList
        {
            Name = "size",
            ItemType = Nbt.TagInt,
            Items =
            [
                new NbtInt { Value = maxX - minX + 1 },
                new NbtInt { Value = maxY - minY + 1 },
                new NbtInt { Value = maxZ - minZ + 1 },
            ],
        });
        outRoot.Items.Add(new NbtList
        {
            Name = "palette",
            ItemType = Nbt.TagCompound,
            Items = paletteList.Select(BlockStateTag).Cast<NbtTag>().ToList(),
        });
        outRoot.Items.Add(blocks);
        outRoot.Items.Add(new NbtList { Name = "entities", ItemType = Nbt.TagCompound });
        var author = meta?.GetString("Author") ?? "";
        if (author.Length > 0) outRoot.Items.Add(new NbtString { Name = "author", Value = author });

        Nbt.WriteFile(destPath, outRoot);
        return destPath;
    }

    /// <summary>
    /// 结构 .nbt → .litematic：单区域、位置 (0,0,0)，调色板含空气（索引 0），
    /// 与 Litematica 自己保存的形态一致（可直接在游戏里加载）。
    /// </summary>
    public static string StructureNbtToLitematic(string sourcePath, string destPath)
    {
        var root = Nbt.ReadFile(sourcePath);
        var sizeArr = (root.Get("size") as NbtList)?.Items.OfType<NbtInt>().Select(i => i.Value).ToArray()
                      ?? throw new InvalidOperationException("结构文件缺少 size");
        if (sizeArr.Length < 3) throw new InvalidOperationException("结构 size 不完整");
        var (sx, sy, sz) = (sizeArr[0], sizeArr[1], sizeArr[2]);
        if (sx <= 0 || sy <= 0 || sz <= 0) throw new InvalidOperationException("结构尺寸非法");

        var palette = PaletteTags(root.Get("palette"));
        if (palette.Count == 0) throw new InvalidOperationException("结构文件缺少 palette");

        // 索引 → 网格；缺省为空气。palette 里没有空气时在头部补一个，
        // 原索引整体后移一位（indexShift）——结构 nbt 的 state 与 litematic 的调色板索引必须对齐
        var airIndex = palette.FindIndex(p => ProjectionReader.IsAir(p.Name));
        var indexShift = 0;
        if (airIndex < 0)
        {
            palette.Insert(0, new BlockStateTagDef("minecraft:air", []));
            airIndex = 0;
            indexShift = 1;
        }

        var grid = new int[sx * sy * sz];
        Array.Fill(grid, airIndex);
        if (root.Get("blocks") is NbtList blocks)
        {
            foreach (var item in blocks.Items)
            {
                if (item is not NbtCompound block) continue;
                var pos = (block.Get("pos") as NbtList)?.Items.OfType<NbtInt>().Select(i => i.Value).ToArray() ?? [];
                if (pos.Length < 3) continue;
                var state = block.GetInt("state", -1);
                if (state < 0 || state >= palette.Count) continue;
                var (x, y, z) = (pos[0], pos[1], pos[2]);
                if (x < 0 || y < 0 || z < 0 || x >= sx || y >= sy || z >= sz) continue;
                grid[(y * sz + z) * sx + x] = state + indexShift;
            }
        }

        var bits = ProjectionReader.BitsNeeded(palette.Count);
        var packed = ProjectionReader.Pack(grid, bits);

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
        region.Items.Add(new NbtList
        {
            Name = "BlockStatePalette",
            ItemType = Nbt.TagCompound,
            Items = palette.Select(p => (NbtTag)BlockStateTag(p.Name)).ToList(),
        });
        region.Items.Add(new NbtLongArray { Name = "BlockStates", Value = packed });
        region.Items.Add(new NbtList { Name = "Entities", ItemType = Nbt.TagCompound });
        region.Items.Add(new NbtList { Name = "TileEntities", ItemType = Nbt.TagCompound });
        region.Items.Add(new NbtList { Name = "PendingBlockTicks", ItemType = Nbt.TagCompound });
        region.Items.Add(new NbtList { Name = "PendingFluidTicks", ItemType = Nbt.TagCompound });

        var dataVersion = root.GetInt("DataVersion", 3953);
        var nonAir = grid.Count(v => v != airIndex);
        var name = Path.GetFileNameWithoutExtension(sourcePath);
        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

        var metadata = new NbtCompound { Name = "Metadata" };
        metadata.Items.Add(new NbtString { Name = "Name", Value = name });
        metadata.Items.Add(new NbtString { Name = "Author", Value = root.GetString("author") });
        metadata.Items.Add(new NbtString { Name = "Description", Value = "由 Starcloud 实验室从结构文件转换" });
        metadata.Items.Add(new NbtInt { Name = "RegionCount", Value = 1 });
        metadata.Items.Add(new NbtInt { Name = "TotalBlocks", Value = nonAir });
        metadata.Items.Add(new NbtInt { Name = "TotalVolume", Value = sx * sy * sz });
        metadata.Items.Add(new NbtCompound
        {
            Name = "EnclosingSize",
            Items = [new NbtInt { Name = "x", Value = sx }, new NbtInt { Name = "y", Value = sy }, new NbtInt { Name = "z", Value = sz }],
        });
        metadata.Items.Add(new NbtLong { Name = "TimeCreated", Value = now });
        metadata.Items.Add(new NbtLong { Name = "TimeModified", Value = now });

        var outRoot = new NbtCompound();
        outRoot.Items.Add(new NbtInt { Name = "MinecraftDataVersion", Value = dataVersion });
        outRoot.Items.Add(new NbtInt { Name = "Version", Value = 6 });
        outRoot.Items.Add(metadata);
        var regions = new NbtCompound { Name = "Regions" };
        regions.Items.Add(region);
        outRoot.Items.Add(regions);

        Nbt.WriteFile(destPath, outRoot);
        return destPath;
    }

    /// <summary>blockstate 字符串 → 结构/litematic 的调色板条目（"minecraft:oak_log[axis=y]" 拆出属性）</summary>
    private static NbtCompound BlockStateTag(string blockState)
    {
        var compound = new NbtCompound();
        var bracket = blockState.IndexOf('[');
        if (bracket < 0)
        {
            compound.Items.Add(new NbtString { Name = "Name", Value = blockState });
            return compound;
        }
        compound.Items.Add(new NbtString { Name = "Name", Value = blockState[..bracket] });
        var props = new NbtCompound { Name = "Properties" };
        var inner = blockState[(bracket + 1)..].TrimEnd(']');
        foreach (var pair in inner.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            if (eq <= 0) continue;
            props.Items.Add(new NbtString { Name = pair[..eq], Value = pair[(eq + 1)..] });
        }
        compound.Items.Add(props);
        return compound;
    }

    private sealed record BlockStateTagDef(string Name, List<(string Key, string Value)> Properties);

    /// <summary>调色板条目 → blockstate 字符串（与 ProjectionReader.PaletteNames 同构）</summary>
    private static List<BlockStateTagDef> PaletteTags(NbtTag? tag)
    {
        var result = new List<BlockStateTagDef>();
        if (tag is not NbtList list) return result;
        foreach (var item in list.Items)
        {
            if (item is not NbtCompound entry)
            {
                result.Add(new BlockStateTagDef("minecraft:air", []));
                continue;
            }
            var props = new List<(string, string)>();
            if (entry.Get("Properties") is NbtCompound p)
                foreach (var t in p.Items)
                    props.Add((t.Name, t is NbtString s ? s.Value : ""));
            result.Add(new BlockStateTagDef(entry.GetString("Name", "minecraft:air"), props));
        }
        return result;
    }
}
