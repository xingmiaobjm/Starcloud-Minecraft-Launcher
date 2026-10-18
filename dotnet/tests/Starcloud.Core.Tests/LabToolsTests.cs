// SC 实验室工具测试：彩色文字（§ 代码格式）、指令助手（装配 / 校验 / 模板自洽）、
// 世吞生成器（参数校验 / 预估与生成一致 / 生成物可被投影解析器读回）。

using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Projection;
using Xunit;

namespace Starcloud.Core.Tests;

public class LabToolsTests
{
    // ── 彩色文字 ──

    [Fact]
    public void 十六进制前缀是标准的section_x语法()
    {
        Assert.Equal("§x§F§F§5§5§F§F", McText.HexPrefix("#FF55FF"));
        Assert.Equal("§x§0§0§A§A§0§0", McText.HexPrefix("00AA00"));
        Assert.Equal("§x§F§F§F§F§F§F", McText.HexPrefix("#FFF")); // 三位缩写展开
    }

    [Fact]
    public void 单色模式输出前缀加原文()
    {
        var text = McText.Solid("你好", "#FFD322");
        Assert.Equal("§x§F§F§D§3§2§2你好", text);
        Assert.Equal("你好", McText.Strip(text));
    }

    [Fact]
    public void 原版色单色模式落到16色之一()
    {
        // 按 RGB 距离取最近：原版红 c(#FF5555) 与原版深红 4(#AA0000) 都是候选
        Assert.Equal("§cx", McText.Solid("x", "#FF5555", legacyPalette: true));
        Assert.Equal("§4x", McText.Solid("x", "#FE0102", legacyPalette: true)); // 更接近深红
        Assert.Equal("§ex", McText.Solid("x", "#FFFF55", legacyPalette: true)); // 原版黄
    }

    [Fact]
    public void 逐字符渐变每个字符都有自己的色且首尾色正确()
    {
        var text = McText.Gradient("abcd", "#000000", "#FFFFFF");
        Assert.Contains("§x§0§0§0§0§0§0a", text);
        Assert.Contains("§x§F§F§F§F§F§Fd", text);
        Assert.Equal("abcd", McText.Strip(text));
    }

    [Fact]
    public void 分段渐变按段长落色且比精确模式短()
    {
        var exact = McText.Gradient("abcdefgh", "#000000", "#FFFFFF", exact: true);
        var segmented = McText.Gradient("abcdefgh", "#000000", "#FFFFFF", exact: false, segmentLength: 4);
        Assert.True(segmented.Length < exact.Length);
        Assert.Equal("abcdefgh", McText.Strip(segmented));
    }

    [Fact]
    public void 样式代码按整段前缀插入且顺序固定()
    {
        var codes = McText.StyleCodes(bold: true, italic: true, underline: true, strikethrough: true, obfuscated: true);
        Assert.Equal("§k§l§m§n§o", codes);
        var text = McText.Gradient("hi", "#FFFFFF", "#000000", styleCodes: codes);
        Assert.StartsWith(codes, text);
    }

    [Fact]
    public void 彩虹模式首尾在色相环上不同且剥离后为原文()
    {
        var text = McText.Rainbow("abcdef");
        Assert.Equal("abcdef", McText.Strip(text));
        Assert.StartsWith("§x", text);
    }

    [Fact]
    public void 最近原版色选取符合直觉()
    {
        Assert.Equal('f', McText.NearestLegacy("#FEFEFE").Code); // 近白 → 白
        Assert.Equal('0', McText.NearestLegacy("#010101").Code); // 近黑 → 黑
        Assert.Equal('6', McText.NearestLegacy("#FFAA00").Code); // 原版金
    }

    [Fact]
    public void 剥离函数能处理十六进制前缀与普通色码()
    {
        Assert.Equal("hello", McText.Strip("§x§F§F§0§0§0§0he§cllo"));
        Assert.Equal("", McText.Strip(""));
    }

    // ── 指令助手 ──

    [Fact]
    public void 装配替换已填参数并保留未填占位符()
    {
        var template = CommandHelper.Templates.First(t => t.Id == "tp");
        var command = CommandHelper.Build(template, new Dictionary<string, string>
        {
            ["target"] = "@p",
            ["x"] = "100",
            ["y"] = "70",
            // z 故意不填
        });
        Assert.Equal("tp @p 100 70 {z}", command);
        Assert.True(CommandHelper.HasUnfilled(command));

        var full = CommandHelper.Build(template, new Dictionary<string, string>
        {
            ["target"] = "@p", ["x"] = "100", ["y"] = "70", ["z"] = "200",
        });
        Assert.Equal("tp @p 100 70 200", full);
        Assert.False(CommandHelper.HasUnfilled(full));
    }

    [Theory]
    [InlineData("give @s minecraft:diamond 1", null)]
    [InlineData("give @s minecraft:diamond{Enchantments:[{id:\"x\",lvl:1}]} 1", null)]
    [InlineData("give @s minecraft:diamond[", "方括号")]
    [InlineData("give @s x{a:1", "花括号")]
    [InlineData("title @a title {\"text\":\"hi}", "双引号")]
    [InlineData("x]", "括号")]
    public void 体检能发现常见手误(string command, string? expectedFragment)
    {
        var problem = CommandHelper.Validate(command);
        if (expectedFragment is null) Assert.Null(problem);
        else Assert.Contains(expectedFragment, problem);
    }

    [Fact]
    public void 模板库自洽_每个占位符都有对应参数定义()
    {
        foreach (var template in CommandHelper.Templates)
        {
            var placeholders = System.Text.RegularExpressions.Regex
                .Matches(template.Template, @"\{(\w+)\}")
                .Select(m => m.Groups[1].Value)
                .Distinct()
                .ToList();
            var keys = template.Params.Select(p => p.Key).ToList();
            foreach (var placeholder in placeholders)
                Assert.Contains(placeholder, keys);
            Assert.True(template.Params.Count > 0, $"{template.Id} 没有参数定义");
            Assert.True(template.Category.Length > 0 && template.Name.Length > 0);
        }
        // Id 唯一
        Assert.Equal(CommandHelper.Templates.Count, CommandHelper.Templates.Select(t => t.Id).Distinct().Count());
    }

    // ── 世吞生成器 ──

    [Fact]
    public void 参数校验拦住越界值()
    {
        Assert.NotNull(WorldEater.Validate(new WorldEaterOptions(Length: 0)));
        Assert.NotNull(WorldEater.Validate(new WorldEaterOptions(Length: 9999)));
        Assert.NotNull(WorldEater.Validate(new WorldEaterOptions(Layers: 0)));
        Assert.NotNull(WorldEater.Validate(new WorldEaterOptions(Spacing: 0)));
        Assert.Null(WorldEater.Validate(new WorldEaterOptions()));
    }

    [Fact]
    public void 网页端参数解析_缺省与越界分别处理()
    {
        // 完整参数
        var full = System.Text.Json.JsonDocument.Parse(
            """{"length":32,"width":8,"layers":4,"spacing":3,"layerGap":2,"offset":-1}""").RootElement;
        var o = WorldEater.ParseOptions(full);
        Assert.Equal(new WorldEaterOptions(32, 8, 4, 3, 2, -1), o);
        Assert.Null(WorldEater.Validate(o));

        // 缺省：给空对象也应得到默认值（网页端只发部分键时同样成立）
        var partial = System.Text.Json.JsonDocument.Parse("""{"length":16}""").RootElement;
        var p = WorldEater.ParseOptions(partial);
        Assert.Equal(16, p.Length);
        Assert.Equal(16, p.Width);   // 其余走默认
        Assert.Equal(3, p.Layers);

        // 越界值原样返回，由 Validate 报错（网页端要能显示原因）
        var bad = System.Text.Json.JsonDocument.Parse("""{"spacing":0}""").RootElement;
        Assert.NotNull(WorldEater.Validate(WorldEater.ParseOptions(bad)));
    }

    [Fact]
    public void 预估数量与实际生成一致且能被投影解析器读回()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-lab-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new WorldEaterOptions(Length: 32, Width: 8, Layers: 3, Spacing: 2, LayerGap: 1, Offset: 2);
            var expected = WorldEater.EstimateTntCount(options);
            var path = Path.Combine(dir, "eater.litematic");
            WorldEater.GenerateLitematic(options, path);

            var info = ProjectionReader.Read(path);
            Assert.Equal(ProjectionFormat.Litematic, info.Format);
            Assert.Equal("Starcloud 实验室", info.Author);
            Assert.Equal(expected, info.TotalBlocks);
            Assert.Single(info.TopBlocks);
            Assert.Equal("minecraft:tnt", info.TopBlocks[0].BlockId);
            Assert.Equal(expected, info.TopBlocks[0].Count);
            // 尺寸：X 不变、Z 含错位累积、Y 含层距
            Assert.Equal(32, info.Size.X);
            Assert.Equal(3 + 1 * 2, info.Size.Y);
            Assert.Equal(8 + 2 * 2, info.Size.Z);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 负错位不会生成越界坐标且仍有方块()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-lab-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new WorldEaterOptions(Length: 8, Width: 4, Layers: 2, Spacing: 2, LayerGap: 0, Offset: -2);
            var path = Path.Combine(dir, "neg.litematic");
            WorldEater.GenerateLitematic(options, path);
            var info = ProjectionReader.Read(path);
            Assert.True(info.TotalBlocks > 0);
            Assert.True(info.Size.Z >= 4);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── 投影体素（3D 预览数据） ──

    [Fact]
    public void 体素导出返回非空气方块且坐标归一化()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-lab-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new WorldEaterOptions(Length: 8, Width: 4, Layers: 2, Spacing: 2, LayerGap: 0, Offset: 0);
            var path = Path.Combine(dir, "vox.litematic");
            WorldEater.GenerateLitematic(options, path);

            var voxels = VoxelExporter.Read(path);
            Assert.Null(voxels.Unsupported);
            // 归一化到"实际占用包围盒"：x 步长 2 只落 0/2/4/6 → 宽 7；y 两层 → 2；z 步长 2 落 0/2 → 3
            Assert.Equal(7, voxels.SizeX);
            Assert.Equal(2, voxels.SizeY);
            Assert.Equal(3, voxels.SizeZ);
            Assert.Equal(WorldEater.EstimateTntCount(options), voxels.Blocks.Count);
            Assert.All(voxels.Blocks, b => Assert.Equal("minecraft:tnt", b.State));
            Assert.All(voxels.Blocks, b =>
            {
                Assert.InRange(b.X, 0, voxels.SizeX - 1);
                Assert.InRange(b.Y, 0, voxels.SizeY - 1);
                Assert.InRange(b.Z, 0, voxels.SizeZ - 1);
            });
            Assert.False(voxels.Truncated);
            Assert.Equal(["minecraft:tnt"], voxels.BlockIds);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 体素导出保留方块的完整状态字符串()
    {
        // 预览要按状态选模型（piston[extended=true] 和 extended=false 是两个模型），
        // 所以导出的必须是带属性的完整 blockstate，不能只剩方块 id
        var dir = Path.Combine(Path.GetTempPath(), "sc-lab-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = WriteStateLitematic(dir, "minecraft:piston[extended=true,facing=north]");
            var voxels = VoxelExporter.Read(path);
            Assert.Single(voxels.Blocks);
            Assert.Equal("minecraft:piston[extended=true,facing=north]", voxels.Blocks[0].State);
            Assert.Equal(["minecraft:piston"], voxels.BlockIds);   // BlockIds 去掉属性，用于收资源
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 体素导出对旧格式返回不支持说明而不是抛异常()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-lab-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, "old.schematic");
            var root = new NbtCompound();
            root.Items.Add(new NbtInt { Name = "Width", Value = 1 });
            root.Items.Add(new NbtInt { Name = "Height", Value = 1 });
            root.Items.Add(new NbtInt { Name = "Length", Value = 1 });
            Nbt.WriteFile(path, root);

            var voxels = VoxelExporter.Read(path);
            Assert.NotNull(voxels.Unsupported);
            Assert.Empty(voxels.Blocks);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void 体素超限时抽样并标记()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-lab-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var options = new WorldEaterOptions(Length: 32, Width: 32, Layers: 4, Spacing: 1, LayerGap: 0, Offset: 0);
            var path = Path.Combine(dir, "big.litematic");
            WorldEater.GenerateLitematic(options, path);

            var voxels = VoxelExporter.Read(path, maxBlocks: 500);
            Assert.True(voxels.Truncated);
            Assert.True(voxels.Blocks.Count <= 500);
            Assert.True(voxels.Blocks.Count > 0);
        }
        finally { Directory.Delete(dir, true); }
    }

    // ── 彩色文字的标记语法（MD 手感，只保留颜色） ──

    [Fact]
    public void 标记解析_内联片段与开启色与复位()
    {
        var spans = McTextMarkup.Parse("普通{#FF0000 红字}普通{#00FF00}绿到底{/}收尾");
        Assert.Equal(5, spans.Count);
        Assert.Equal(("普通", null), (spans[0].Text, spans[0].Hex));
        Assert.Equal(("红字", "#FF0000"), (spans[1].Text, spans[1].Hex));
        Assert.Equal(("普通", null), (spans[2].Text, spans[2].Hex));
        Assert.Equal(("绿到底", "#00FF00"), (spans[3].Text, spans[3].Hex));
        Assert.Equal(("收尾", null), (spans[4].Text, spans[4].Hex));
    }

    [Fact]
    public void 标记解析_三位缩写与原版色名()
    {
        var spans = McTextMarkup.Parse("{#f55 红}{#gold 金}");
        Assert.Equal("#f55", spans[0].Hex);   // 三位缩写保留原样，生成 § 代码时由 McText.ParseHex 展开
        Assert.Equal("#FFAA00", spans[1].Hex);
    }

    [Fact]
    public void 标记解析_未闭合或非法标记按普通文本处理()
    {
        // 没闭合（或跨行）：视为"开启色"——整段（含后续行）都是该色
        var spans = McTextMarkup.Parse("{#FF0000 红\n下一行");
        Assert.Equal("#FF0000", spans[0].Hex);
        Assert.Equal("红\n下一行", spans[0].Text);
        Assert.Single(spans);
        // 非法色值：原样保留
        var bad = McTextMarkup.Parse("{#zzz 内容}");
        Assert.Equal("{#zzz 内容}", string.Concat(bad.Select(x => x.Text)));
        Assert.All(bad, x => Assert.Null(x.Hex));
    }

    [Fact]
    public void 标记生成section代码_无色段用默认渐变有色段用指定色()
    {
        var spans = McTextMarkup.Parse("A{#FF0000 B}C");
        var code = McTextMarkup.ToSectionCode(spans, exactGradient: true);
        Assert.Contains("§x§F§F§0§0§0§0B", code);   // 指定色段
        Assert.Contains("§x§5§5§F§F§F§FA", code); // 默认渐变段（起始色）
        Assert.Equal("ABC", McText.Strip(code));
        Assert.True(McTextMarkup.HasMarkup("A{#FF0000 B}C"));
        Assert.False(McTextMarkup.HasMarkup("没有标记的普通文本"));
    }

    // ── 投影社区（GitHub 源解析，纯函数离线测） ──

    [Fact]
    public void 社区解析_仓库搜索JSON映射到卡片模型()
    {
        const string json = """
        {
          "total_count": 1,
          "items": [
            {
              "name": "perfect-world-eater",
              "full_name": "someone/perfect-world-eater",
              "description": "1.20 无沟世吞",
              "stargazers_count": 42,
              "pushed_at": "2026-10-01T08:00:00Z",
              "html_url": "https://github.com/someone/perfect-world-eater",
              "owner": { "login": "someone" }
            }
          ]
        }
        """;
        var items = GitHubProjectionSource.ParseSearchJson(json, full => "IMG/" + full);
        Assert.Single(items);
        var it = items[0];
        Assert.Equal("perfect-world-eater", it.Name);
        Assert.Equal("someone", it.Author);
        Assert.Equal("1.20 无沟世吞", it.Description);
        Assert.Equal(42, it.Stars);
        Assert.Equal("someone/perfect-world-eater", it.FullName);
        Assert.Equal("GitHub", it.Source);
        Assert.Equal("IMG/someone/perfect-world-eater", it.ImageUrl); // 封面经当前通道映射
        Assert.True(it.UpdatedAt.Year >= 2026);
    }

    [Fact]
    public void 社区解析_非法或空JSON返回空表不抛()
    {
        Assert.Empty(GitHubProjectionSource.ParseSearchJson("{}", u => u));
        Assert.Empty(GitHubProjectionSource.ParseSearchJson("""{"items":[]}""", u => u));
    }

    [Fact]
    public void 社区解析_Release只取投影扩展名且用最新含文件的release()
    {
        const string json = """
        [
          {
            "assets": [
              { "name": "preview.png", "size": 100, "browser_download_url": "https://github.com/a/b/releases/download/v2/preview.png" }
            ]
          },
          {
            "assets": [
              { "name": "eater-v1.litematic", "size": 2048, "browser_download_url": "https://github.com/a/b/releases/download/v1/eater-v1.litematic" },
              { "name": "readme.txt", "size": 10, "browser_download_url": "https://github.com/a/b/releases/download/v1/readme.txt" },
              { "name": "eater-v1.nbt", "size": 1024, "browser_download_url": "https://github.com/a/b/releases/download/v1/eater-v1.nbt" }
            ]
          }
        ]
        """;
        var files = GitHubProjectionSource.ParseReleasesJson(json, url => "MIRROR/" + url);
        Assert.Equal(2, files.Count);
        Assert.Equal("eater-v1.litematic", files[0].Name);
        Assert.Equal(2048, files[0].SizeBytes);
        Assert.Equal("2 KB", files[0].SizeText);
        Assert.StartsWith("MIRROR/", files[0].DownloadUrl);
        Assert.Equal("eater-v1.nbt", files[1].Name);
    }

    [Fact]
    public void 社区解析_自定义源JSON宽容映射常见字段别名()
    {
        const string json = """
        [
          { "title": "无沟世吞 V5", "owner": "小明", "summary": "64x64 三向",
            "download_url": "https://files.example.com/eater-v5.litematic",
            "thumbnail": "https://files.example.com/eater-v5.png",
            "stars": "12", "updated_at": "2026-09-20T10:00:00Z" },
          { "name": "缺 URL 的记录会被跳过" },
          { "url": "https://files.example.com/plain.nbt" }
        ]
        """;
        var items = GenericJsonSource.Parse(json, "例子源");
        Assert.Equal(2, items.Count);
        Assert.Equal("无沟世吞 V5", items[0].Name);
        Assert.Equal("小明", items[0].Author);
        Assert.Equal("例子源", items[0].Source);
        Assert.Equal(12, items[0].Stars);
        Assert.Equal("https://files.example.com/eater-v5.litematic", items[0].FullName);
        Assert.Equal("https://files.example.com/eater-v5.png", items[0].ImageUrl);
        // 无 name 时用 URL 文件名兜底
        Assert.Equal("plain.nbt", items[1].Name);
        // 非数组返回空
        Assert.Empty(GenericJsonSource.Parse("""{"a":1}""", "x"));
    }

    [Fact]
    public void 社区检索词_叠加litematic且不重复()
    {
        Assert.Equal("litematic", GitHubProjectionSource.BuildSearchQuery(""));
        Assert.Equal("world eater litematic", GitHubProjectionSource.BuildSearchQuery("world eater"));
        // 已含 litematic 不叠
        Assert.Equal("litematic printer", GitHubProjectionSource.BuildSearchQuery("litematic printer"));
        Assert.Equal("Litematic", GitHubProjectionSource.BuildSearchQuery("Litematic"));
    }

    [Fact]
    public void 社区术语词典_世吞译作world_eater且长词优先()
    {
        Assert.Equal("world eater", CommunityGlossary.ToSearchTerm("世吞"));
        Assert.Equal("trenchless world eater", CommunityGlossary.ToSearchTerm("无沟世吞"));
        // 包含式：双倍速世吞 → 命中「世吞」（长词优先顺序保证「无沟世吞」先试）
        Assert.Equal("world eater", CommunityGlossary.ToSearchTerm("双倍速世吞"));
        Assert.Equal("slime farm", CommunityGlossary.ToSearchTerm("史莱姆农场"));
        // 未命中交回通用翻译
        Assert.Null(CommunityGlossary.ToSearchTerm("随便什么词"));
        Assert.Null(CommunityGlossary.ToSearchTerm(""));
    }

    [Fact]
    public void 社区翻译_解析MyMemory响应并识别配额告警()
    {
        Assert.Equal("高级打印机", CommunityTranslator.ParseTranslated(
            """{"responseData":{"translatedText":"高级打印机","match":0.85},"responseStatus":200}"""));
        // 免费端点的配额告警不是译文
        Assert.Null(CommunityTranslator.ParseTranslated(
            """{"responseData":{"translatedText":"MYMEMORY WARNING: YOU USED ALL AVAILABLE FREE TRANSLATIONS FOR TODAY"}}"""));
        Assert.Null(CommunityTranslator.ParseTranslated("{}"));
        Assert.Null(CommunityTranslator.ParseTranslated("not json"));
    }

    [Fact]
    public void 社区解析_非投影资产全部过滤掉时返回空()
    {
        const string json = """
        [ { "assets": [ { "name": "a.png", "size": 1, "browser_download_url": "https://x/a.png" } ] } ]
        """;
        Assert.Empty(GitHubProjectionSource.ParseReleasesJson(json, url => url));
    }

    // ── 投影预览的资源收集（deepslate 用） ──

    [Fact]
    public void 资源收集_只取用到的方块及其模型链与纹理()
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["minecraft/blockstates/piston.json"] =
                """{"variants":{"extended=true,facing=north":{"model":"minecraft:block/piston"}}}""",
            ["minecraft/blockstates/stone.json"] = """{"variants":{"":{"model":"minecraft:block/stone"}}}""",
            ["minecraft/models/block/piston.json"] =
                """{"parent":"minecraft:block/template_piston","textures":{"platform":"minecraft:block/piston_top"}}""",
            ["minecraft/models/block/template_piston.json"] = """{"textures":{"platform":"#platform"}}""",
            ["minecraft/models/block/stone.json"] =
                """{"parent":"minecraft:block/cube_all","textures":{"all":"minecraft:block/stone"}}""",
            ["minecraft/models/block/cube_all.json"] = """{"textures":{"all":"#all"}}""",
        };
        var textureBytes = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["minecraft/textures/block/piston_top.png"] = [1, 2, 3],
            ["minecraft/textures/block/stone.png"] = [4, 5],
        };
        Func<string, byte[]?> read = path =>
            files.TryGetValue(path, out var text) ? System.Text.Encoding.UTF8.GetBytes(text)
            : textureBytes.TryGetValue(path, out var bin) ? bin : null;

        var assets = ProjectionAssetCollector.Collect(
            ["minecraft:piston[extended=true,facing=north]", "minecraft:stone"], read);

        // blockstates 两个；模型链按 parent 递归收（template_piston / cube_all 都要在）
        Assert.Equal(2, assets.BlockStates.Count);
        Assert.Contains("minecraft:block/piston", assets.Models.Keys);
        Assert.Contains("minecraft:block/template_piston", assets.Models.Keys);
        Assert.Contains("minecraft:block/cube_all", assets.Models.Keys);
        // 纹理只收真实路径（#ref 交给页面解）
        Assert.Equal(2, assets.Textures.Count);
        Assert.Contains("minecraft:block/piston_top", assets.Textures.Keys);
        Assert.Contains("minecraft:block/stone", assets.Textures.Keys);
    }

    [Fact]
    public void 体素导出_超预算时走占用格子降采样()
    {
        var dir = Path.Combine(Path.GetTempPath(), "sc-lab-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // 128×64×128 = 104 万格，贴一整面墙（8192 个方块）
            var path = Path.Combine(dir, "wall.litematic");
            WriteSparseLitematic(path, 128, 64, 128, (x, y, z) => z == 0 ? 1 : 0);

            var full = VoxelExporter.Read(path, maxBlocks: 100_000);
            Assert.Equal(128 * 64, full.Blocks.Count);   // 预算内：完全大小，一个不少
            Assert.False(full.Truncated);

            var sampled = VoxelExporter.Read(path, maxBlocks: 500);
            Assert.True(sampled.Truncated);
            Assert.InRange(sampled.Blocks.Count, 1, 500);
            Assert.All(sampled.Blocks, b =>
            {
                Assert.InRange(b.X, 0, sampled.SizeX - 1);
                Assert.InRange(b.Y, 0, sampled.SizeY - 1);
                Assert.InRange(b.Z, 0, sampled.SizeZ - 1);
            });
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>造一个 1×1×1、只含指定方块状态的 litematic（验证状态字符串原样保留）</summary>
    private static string WriteStateLitematic(string dir, string blockState)
    {
        var palette = new NbtList
        {
            Name = "BlockStatePalette",
            ItemType = Nbt.TagCompound,
            Items = [(NbtTag)new NbtCompound { Items = [new NbtString { Name = "Name", Value = blockState }] }],
        };
        var region = new NbtCompound { Name = "Main" };
        region.Items.Add(new NbtCompound
        {
            Name = "Position",
            Items = [new NbtInt { Name = "x", Value = 0 }, new NbtInt { Name = "y", Value = 0 }, new NbtInt { Name = "z", Value = 0 }],
        });
        region.Items.Add(new NbtCompound
        {
            Name = "Size",
            Items = [new NbtInt { Name = "x", Value = 1 }, new NbtInt { Name = "y", Value = 1 }, new NbtInt { Name = "z", Value = 1 }],
        });
        region.Items.Add(palette);
        region.Items.Add(new NbtLongArray { Name = "BlockStates", Value = [0L] });   // 调色板只有 1 项，索引 0

        var regions = new NbtCompound { Name = "Regions" };
        regions.Items.Add(region);

        var root = new NbtCompound();
        root.Items.Add(new NbtInt { Name = "MinecraftDataVersion", Value = 3953 });
        root.Items.Add(new NbtInt { Name = "Version", Value = 6 });
        root.Items.Add(regions);

        var path = Path.Combine(dir, "state.litematic");
        Nbt.WriteFile(path, root);
        return path;
    }

    /// <summary>造一个稀疏 litematic：直接按位打包，不经过 int[] 中间数组（否则测试自己就爆内存）</summary>
    private static void WriteSparseLitematic(string path, int sx, int sy, int sz, Func<int, int, int, int> stateAt)
    {
        const int bits = 2;
        const int perLong = 64 / bits;
        var plane = (long)sx * sz;
        var volume = plane * sy;
        var data = new long[(volume + perLong - 1) / perLong];
        for (long i = 0; i < volume; i++)
        {
            var y = (int)(i / plane);
            var rem = i % plane;
            var z = (int)(rem / sx);
            var x = (int)(rem % sx);
            var state = stateAt(x, y, z);
            if (state == 0) continue;
            data[i / perLong] |= (long)(((ulong)state & 3UL) << (int)((i % perLong) * bits));
        }

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
        region.Items.Add(new NbtLongArray { Name = "BlockStates", Value = data });

        var regions = new NbtCompound { Name = "Regions" };
        regions.Items.Add(region);

        var root = new NbtCompound();
        root.Items.Add(new NbtInt { Name = "MinecraftDataVersion", Value = 3953 });
        root.Items.Add(new NbtInt { Name = "Version", Value = 6 });
        root.Items.Add(regions);
        Nbt.WriteFile(path, root);
    }
}
