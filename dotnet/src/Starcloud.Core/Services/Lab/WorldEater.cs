// SC 实验室 · 世吞（World Eater）投影生成器。
//
// 诚实说明边界：完整世吞是红石机器（TNT 复制、时序、活塞阵列、发射器链），它的"机器结构"
// 与坐标强绑定，参数化生成整机不现实。这里生成的是**爆破阵列模板**——按参数铺好的
// 分层 TNT 网格投影，用于：① 直接在创造模式里引爆做地形清理；② 作为自建世吞的"药室"参照。
// 生成物是标准 .litematic，可在 Litematica 里加载、也可在实验室里继续编辑/转换。
//
// 参数语义：
//   Length × Width  —— 单层网格尺寸（格）
//   Layers          —— TNT 层数（沿 Y 叠放）
//   Spacing         —— 同层相邻 TNT 的间隔（1 = 紧贴，2 = 隔一格……）
//   LayerGap        —— 层与层之间的空层数（0 = 紧贴）
//   Offset          —— 每层相对上一层在 Z 方向的错位（模仿世吞的阶梯式推进，0 = 对齐）

using Starcloud.Core.Services.Projection;

namespace Starcloud.Core.Services.Lab;

public sealed record WorldEaterOptions(
    int Length = 64,
    int Width = 16,
    int Layers = 3,
    int Spacing = 2,
    int LayerGap = 1,
    int Offset = 2,
    string TntBlock = "minecraft:tnt");

public static class WorldEater
{
    public const int MaxDimension = 512;
    public const int MaxLayers = 32;

    /// <summary>
    /// 解析网页端（WebView2 里的 HTML 生成器）发来的参数 JSON：键为
    /// length / width / layers / spacing / layerGap / offset，缺省用默认值。
    /// 非法值不在这里拒绝——交给 <see cref="Validate"/> 统一报错，网页端能把原因显示出来。
    /// </summary>
    public static WorldEaterOptions ParseOptions(System.Text.Json.JsonElement json)
    {
        int Get(string key, int fallback) =>
            json.ValueKind == System.Text.Json.JsonValueKind.Object &&
            json.TryGetProperty(key, out var v) &&
            v.ValueKind == System.Text.Json.JsonValueKind.Number &&
            v.TryGetInt32(out var n)
                ? n
                : fallback;

        return new WorldEaterOptions(
            Length: Get("length", 64),
            Width: Get("width", 16),
            Layers: Get("layers", 3),
            Spacing: Get("spacing", 2),
            LayerGap: Get("layerGap", 1),
            Offset: Get("offset", 2));
    }

    /// <summary>参数体检：越界直接拒绝（避免生成超大投影把内存吃穿）</summary>
    public static string? Validate(WorldEaterOptions options)
    {
        if (options.Length is < 1 or > MaxDimension) return $"长度需在 1~{MaxDimension} 之间";
        if (options.Width is < 1 or > MaxDimension) return $"宽度需在 1~{MaxDimension} 之间";
        if (options.Layers is < 1 or > MaxLayers) return $"层数需在 1~{MaxLayers} 之间";
        if (options.Spacing is < 1 or > 16) return "间隔需在 1~16 之间";
        if (options.LayerGap is < 0 or > 16) return "层间距需在 0~16 之间";
        if (Math.Abs(options.Offset) > MaxDimension) return "错位量过大";
        return null;
    }

    /// <summary>预计 TNT 数量（供 UI 预览，避免用户生成后才发现量级不对）</summary>
    public static int EstimateTntCount(WorldEaterOptions options)
    {
        var perLayerX = (options.Length + options.Spacing - 1) / options.Spacing;
        var perLayerZ = (options.Width + options.Spacing - 1) / options.Spacing;
        return perLayerX * perLayerZ * options.Layers;
    }

    /// <summary>
    /// 生成 .litematic 爆破阵列：单区域、位置 (0,0,0)，调色板 = [air, TNT]，
    /// BlockStates 按 1.16+ 连续打包（与实验室的读写口径一致）。
    /// </summary>
    public static string GenerateLitematic(WorldEaterOptions options, string destPath)
    {
        if (Validate(options) is { } error) throw new InvalidOperationException(error);

        // 计算包围盒：Z 方向要容纳每层错位累积
        var totalOffset = options.Offset * (options.Layers - 1);
        var sizeX = options.Length;
        var sizeZ = options.Width + Math.Abs(totalOffset);
        var sizeY = options.Layers + options.LayerGap * (options.Layers - 1);

        const int tntIndex = 1; // 0 = air（网格默认值）
        var grid = new int[sizeX * sizeY * sizeZ];
        var count = 0;

        for (var layer = 0; layer < options.Layers; layer++)
        {
            var y = layer * (1 + options.LayerGap);
            if (y >= sizeY) break;
            var zShift = options.Offset * layer;
            if (zShift < 0) zShift += Math.Abs(totalOffset); // 负错位时整体后移，保证坐标非负

            for (var x = 0; x < options.Length; x += options.Spacing)
            for (var z = 0; z < options.Width; z += options.Spacing)
            {
                var zz = z + zShift;
                if (zz < 0 || zz >= sizeZ) continue;
                grid[(y * sizeZ + zz) * sizeX + x] = tntIndex;
                count++;
            }
        }

        if (count == 0) throw new InvalidOperationException("参数下没有生成任何 TNT，请检查间隔与尺寸");

        var bits = ProjectionReader.BitsNeeded(2);
        var packed = ProjectionReader.Pack(grid, bits);

        var region = new NbtCompound { Name = "BlastArray" };
        region.Items.Add(new NbtCompound
        {
            Name = "Position",
            Items = [new NbtInt { Name = "x", Value = 0 }, new NbtInt { Name = "y", Value = 0 }, new NbtInt { Name = "z", Value = 0 }],
        });
        region.Items.Add(new NbtCompound
        {
            Name = "Size",
            Items = [new NbtInt { Name = "x", Value = sizeX }, new NbtInt { Name = "y", Value = sizeY }, new NbtInt { Name = "z", Value = sizeZ }],
        });
        region.Items.Add(new NbtList
        {
            Name = "BlockStatePalette",
            ItemType = Nbt.TagCompound,
            Items =
            [
                (NbtTag)new NbtCompound { Items = [new NbtString { Name = "Name", Value = "minecraft:air" }] },
                new NbtCompound { Items = [new NbtString { Name = "Name", Value = options.TntBlock }] },
            ],
        });
        region.Items.Add(new NbtLongArray { Name = "BlockStates", Value = packed });
        region.Items.Add(new NbtList { Name = "Entities", ItemType = Nbt.TagCompound });
        region.Items.Add(new NbtList { Name = "TileEntities", ItemType = Nbt.TagCompound });
        region.Items.Add(new NbtList { Name = "PendingBlockTicks", ItemType = Nbt.TagCompound });
        region.Items.Add(new NbtList { Name = "PendingFluidTicks", ItemType = Nbt.TagCompound });

        var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var metadata = new NbtCompound { Name = "Metadata" };
        metadata.Items.Add(new NbtString
        {
            Name = "Name",
            Value = $"世吞阵列 {options.Length}×{options.Width}×{options.Layers}层",
        });
        metadata.Items.Add(new NbtString { Name = "Author", Value = "Starcloud 实验室" });
        metadata.Items.Add(new NbtString
        {
            Name = "Description",
            Value = $"爆破阵列模板：间隔 {options.Spacing}、层距 {options.LayerGap}、错位 {options.Offset}；"
                  + "可作为地形清理或自建世吞的药室参照（完整红石机器请在此基础上搭建）",
        });
        metadata.Items.Add(new NbtInt { Name = "RegionCount", Value = 1 });
        metadata.Items.Add(new NbtInt { Name = "TotalBlocks", Value = count });
        metadata.Items.Add(new NbtInt { Name = "TotalVolume", Value = sizeX * sizeY * sizeZ });
        metadata.Items.Add(new NbtCompound
        {
            Name = "EnclosingSize",
            Items = [new NbtInt { Name = "x", Value = sizeX }, new NbtInt { Name = "y", Value = sizeY }, new NbtInt { Name = "z", Value = sizeZ }],
        });
        metadata.Items.Add(new NbtLong { Name = "TimeCreated", Value = now });
        metadata.Items.Add(new NbtLong { Name = "TimeModified", Value = now });

        var regions = new NbtCompound { Name = "Regions" };
        regions.Items.Add(region);

        var root = new NbtCompound();
        root.Items.Add(new NbtInt { Name = "MinecraftDataVersion", Value = 3953 });
        root.Items.Add(new NbtInt { Name = "Version", Value = 6 });
        root.Items.Add(metadata);
        root.Items.Add(regions);

        Nbt.WriteFile(destPath, root);
        return destPath;
    }
}
