using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core;
using Starcloud.Core.Models;
using Starcloud.Core.Plugins;
using Starcloud.Core.Services;
using Starcloud.Core.Services.Lab;
using Starcloud.Core.Services.Multiplayer;
using Starcloud.Core.Services.Projection;

namespace Starcloud.Bridge;


public static partial class Program
{

    private static async Task<object?> HandleLabLoadAsync(TextWriter stdout, JsonElement p)
    {
            var path = p.GetProperty("path").GetString()!;
            if (!File.Exists(path))
                throw new Exception($"文件不存在：{path}");
            var voxels = VoxelExporter.Read(path, VoxelExporter.MaxBlocks);
            if (voxels.Unsupported is { } unsupported)
                return new { ok = false, path, unsupported };
            var stats = voxels.Blocks
                .GroupBy(b => LabStripState(b.State))
                .Select(g => new { id = g.Key, count = g.Count() })
                .OrderByDescending(x => x.count)
                .ToList();
            const int previewLimit = 8000;
            var stride = Math.Max(1, voxels.Blocks.Count / previewLimit);
            var preview = new List<object>();
            for (var i = 0; i < voxels.Blocks.Count && preview.Count < previewLimit; i += stride)
            {
                var block = voxels.Blocks[i];
                preview.Add(new
                {
                    block.X,
                    block.Y,
                    block.Z,
                    id = LabStripState(block.State),
                });
            }
            return new
            {
                ok = true,
                path,
                sizeX = voxels.SizeX,
                sizeY = voxels.SizeY,
                sizeZ = voxels.SizeZ,
                truncated = voxels.Truncated,
                totalBlocks = voxels.Blocks.Count,
                distinctBlocks = stats.Count,
                stats = stats.Take(60),
                preview,
                previewTruncated = preview.Count < voxels.Blocks.Count,
            };
    }

    private static async Task<object?> HandleLabAssetsAsync(TextWriter stdout, JsonElement p)
    {
            // 投影预览用 Win 端同一套渲染器（three.js + deepslate）。
            // 它需要的不只是方块坐标，还有 blockstates / models / textures，
            // 这些由 Core 的 ProjectionAssetCollector 从客户端 jar 里抽（Win 端也是这条路）。
            var assetsPath = LabSafePath(p.GetProperty("path").GetString()!);
            var budget = LabInt(p, "budget", VoxelExporter.MaxBlocks);
            var assets = new MinecraftAssets(() => _settings);
            var voxels = VoxelExporter.Read(assetsPath, budget, assets);
            var pack = ProjectionAssetCollector.Collect(voxels.BlockIds, assets.Read);
            return new
            {
                sizeX = voxels.SizeX,
                sizeY = voxels.SizeY,
                sizeZ = voxels.SizeZ,
                truncated = voxels.Truncated,
                assetSource = assets.SourceLabel ?? "",
                blocks = voxels.Blocks.Select(b => new object[] { b.X, b.Y, b.Z, b.State }),
                states = pack.BlockStates,
                models = pack.Models,
                textures = pack.Textures.ToDictionary(
                    kv => kv.Key,
                    kv => "data:image/png;base64," + Convert.ToBase64String(kv.Value)),
            };
    }

    private static async Task<object?> HandleLabWorldEaterAsync(TextWriter stdout, JsonElement p)
    {
            var options = new WorldEaterOptions(
                Length: LabInt(p, "length", 64),
                Width: LabInt(p, "width", 16),
                Layers: LabInt(p, "layers", 3),
                Spacing: LabInt(p, "spacing", 2),
                LayerGap: LabInt(p, "layerGap", 1),
                Offset: LabInt(p, "offset", 2));
            var error = WorldEater.Validate(options);
            if (error is not null) throw new Exception(error);
            var dir = Path.Combine(LauncherPaths.DataDir, "lab");
            Directory.CreateDirectory(dir);
            var dest = Path.Combine(dir,
                $"world-eater-{DateTime.Now:yyyyMMdd-HHmmss}.litematic");
            var path = WorldEater.GenerateLitematic(options, dest);
            return new { ok = true, path };
    }

    private static async Task<object?> HandleProjectionsScanAsync(TextWriter stdout, JsonElement p)
    {
            var files = _projections.Scan();
            return new
            {
                files = files.Select(f => new
                {
                    filePath = f.FilePath,
                    fileName = f.FileName,
                    sizeBytes = f.SizeBytes,
                    modifiedAt = f.ModifiedAt.ToString("O"),
                    versionId = f.VersionId,
                }),
                versions = _projections.ListVersions(),
            };
    }

    private static async Task<object?> HandleProjectionsCopyAsync(TextWriter stdout, JsonElement p)
    {
            var src = LabSafePath(p.GetProperty("filePath").GetString()!);
            var versionId = LabNullableString(p, "versionId");
            var dest = _projections.CopyTo(src, versionId);
            return new { ok = true, path = dest };
    }

    private static async Task<object?> HandleProjectionsMoveAsync(TextWriter stdout, JsonElement p)
    {
            var src = LabSafePath(p.GetProperty("filePath").GetString()!);
            var versionId = LabNullableString(p, "versionId");
            var dest = _projections.MoveTo(src, versionId);
            return new { ok = true, path = dest };
    }

    private static async Task<object?> HandleProjectionsDeleteAsync(TextWriter stdout, JsonElement p)
    {
            var src = LabSafePath(p.GetProperty("filePath").GetString()!);
            _projections.Delete(src);
            return new { ok = true };
    }

    private static async Task<object?> HandleLabPaletteAsync(TextWriter stdout, JsonElement p)
    {
            return new
            {
                colors = McText.LegacyColors.Select(c => new { code = c.Code.ToString(), name = c.Name, hex = c.Hex }),
            };
    }

    private static async Task<object?> HandleLabColorTextAsync(TextWriter stdout, JsonElement p)
    {
            var text = p.TryGetProperty("text", out var tEl) ? tEl.GetString() ?? "" : "";
            var mode = p.TryGetProperty("mode", out var mEl) ? mEl.GetString() ?? "solid" : "solid";
            var hex = p.TryGetProperty("hexColor", out var hEl) ? hEl.GetString() ?? "#FFD322" : "#FFD322";
            var fromHex = p.TryGetProperty("fromHex", out var fEl) ? fEl.GetString() ?? "#FFD322" : "#FFD322";
            var toHex = p.TryGetProperty("toHex", out var toEl) ? toEl.GetString() ?? "#00AAFF" : "#00AAFF";
            var legacy = p.TryGetProperty("legacyPalette", out var lEl) && lEl.GetBoolean();
            var exact = !p.TryGetProperty("exact", out var eEl) || eEl.GetBoolean();
            var segment = p.TryGetProperty("segmentLength", out var sEl) && sEl.ValueKind == JsonValueKind.Number
                ? Math.Max(1, sEl.GetInt32())
                : 1;
            var bold = p.TryGetProperty("bold", out var bEl) && bEl.GetBoolean();
            var italic = p.TryGetProperty("italic", out var iEl) && iEl.GetBoolean();
            var underline = p.TryGetProperty("underline", out var uEl) && uEl.GetBoolean();
            var strike = p.TryGetProperty("strikethrough", out var stEl) && stEl.GetBoolean();
            var magic = p.TryGetProperty("obfuscated", out var obEl) && obEl.GetBoolean();
            var style = McText.StyleCodes(bold, italic, underline, strike, magic);
            var result = mode switch
            {
                "gradient" => McText.Gradient(text, fromHex, toHex, exact, segment, style),
                "rainbow" => McText.Rainbow(text, exact, segment, style),
                _ => McText.Solid(text, hex, legacy, style),
            };
            return new { ok = true, text = result };
    }

    /// <summary>投影文件路径校验：必须真实存在，且落在游戏目录（全局或某实例）之内。</summary>
    private static string LabSafePath(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath))
            throw new Exception("投影路径不能为空");
        var full = Path.GetFullPath(filePath);
        if (!File.Exists(full)) throw new Exception($"投影文件不存在：{filePath}");
        var dotMc = Path.GetFullPath(_settings.DotMinecraft);
        if (!full.StartsWith(dotMc + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new Exception("只能管理游戏目录内的投影文件");
        return full;
    }

    private static string? LabNullableString(JsonElement p, string key) =>
        p.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static string LabStripState(string state)
    {
        var bracket = state.IndexOf('[');
        return bracket < 0 ? state : state[..bracket];
    }

    private static int LabInt(JsonElement p, string key, int fallback) =>
        p.TryGetProperty(key, out var value) && value.TryGetInt32(out var number)
            ? number
            : fallback;
}
