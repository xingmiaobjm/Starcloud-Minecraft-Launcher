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

    private static async Task<object?> HandleVersionDetailAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var ctx = VersionContentContext(versionId);
            return new
            {
                versionId,
                gameRoot = ctx.GameRoot,
                isolated = ctx.Isolated,
                mcVersion = ctx.McVersion,
                loader = ctx.Loader,
                loaderVersion = ctx.LoaderVersion,
                lastPlayedAt = new InstanceSettingsStore(_settings.DotMinecraft)
                    .Get(versionId).LastPlayedAt?.ToString("O"),
                mods = ContentFiles(ctx.GameRoot, "mod"),
                resourcepacks = ContentFiles(ctx.GameRoot, "resourcepack"),
                shaderpacks = ContentFiles(ctx.GameRoot, "shader"),
            };
    }

    private static async Task<object?> HandleVersionToggleContentAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var kind = NormalizeKind(p.GetProperty("kind").GetString());
            var fileName = SafeFileName(p.GetProperty("fileName").GetString()!);
            var enabled = p.GetProperty("enabled").GetBoolean();
            var ctx = VersionContentContext(versionId);
            var dir = Path.Combine(ctx.GameRoot, InstanceLayout.ContentDirFor(kind));
            var current = Path.Combine(dir, fileName);
            if (!File.Exists(current)) throw new Exception($"文件不存在：{fileName}");
            var disabled = current.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
            if (enabled != disabled) return new { files = ContentFiles(ctx.GameRoot, kind) };
            var target = enabled ? current[..^".disabled".Length] : current + ".disabled";
            if (File.Exists(target)) throw new Exception("目标文件已存在，未做改名");
            File.Move(current, target);
            return new { files = ContentFiles(ctx.GameRoot, kind) };
    }

    private static async Task<object?> HandleVersionDeleteContentAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var kind = NormalizeKind(p.GetProperty("kind").GetString());
            var fileName = SafeFileName(p.GetProperty("fileName").GetString()!);
            var ctx = VersionContentContext(versionId);
            var path = Path.Combine(
                ctx.GameRoot, InstanceLayout.ContentDirFor(kind), fileName);
            if (!File.Exists(path)) throw new Exception($"文件不存在：{fileName}");
            File.Delete(path);
            return new { files = ContentFiles(ctx.GameRoot, kind) };
    }

    private static async Task<object?> HandleVersionOpenContentFolderAsync(TextWriter stdout, JsonElement p)
    {
            var versionId = p.GetProperty("versionId").GetString()!;
            var kind = NormalizeKind(p.GetProperty("kind").GetString());
            var ctx = VersionContentContext(versionId);
            var dir = Path.Combine(ctx.GameRoot, InstanceLayout.ContentDirFor(kind));
            Directory.CreateDirectory(dir);
            OpenFolder(dir);
            return new { ok = true, path = dir };
    }

    private static object[] ContentFiles(string gameRoot, string kind)
    {
        var dir = Path.Combine(gameRoot, InstanceLayout.ContentDirFor(kind));
        if (!Directory.Exists(dir)) return [];
        return Directory.EnumerateFiles(dir)
            .Where(f =>
            {
                var n = Path.GetFileName(f);
                return n.EndsWith(".jar", StringComparison.OrdinalIgnoreCase) ||
                       n.EndsWith(".jar.disabled", StringComparison.OrdinalIgnoreCase) ||
                       n.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ||
                       n.EndsWith(".zip.disabled", StringComparison.OrdinalIgnoreCase);
            })
            .Select(f =>
            {
                var name = Path.GetFileName(f);
                var disabled = name.EndsWith(".disabled", StringComparison.OrdinalIgnoreCase);
                return new
                {
                    fileName = name,
                    displayName = disabled ? name[..^".disabled".Length] : name,
                    enabled = !disabled,
                    sizeBytes = new FileInfo(f).Length,
                    path = f,
                };
            })
            .OrderBy(x => x.displayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }
    /// <summary>
    /// 取 BS6 令牌：本地有未过期令牌直接用；有刷新令牌就续期；都没有抛可读错误
    /// （与 Win AccountDetailPage.EnsureBs6TokensAsync 同策略）。
    /// </summary>
}
