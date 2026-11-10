using System.Diagnostics;
using System.Text.Json;
using Starcloud.Core;
using Starcloud.Core.Services;

namespace Starcloud.Bridge;

/// <summary>跨领域小工具：JSON 取值、版本内容上下文、文件名与路径校验、打开目录。</summary>
public static partial class Program
{
    private static bool IsBool(JsonElement e) =>
        e.ValueKind == JsonValueKind.True || e.ValueKind == JsonValueKind.False;

    private static void OpenFolder(string dir)
    {
        if (OperatingSystem.IsWindows())
        {
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"")
            {
                UseShellExecute = true,
            });
        }
        else if (OperatingSystem.IsMacOS())
        {
            Process.Start(new ProcessStartInfo("open", $"\"{dir}\""));
        }
        else
        {
            Process.Start(new ProcessStartInfo("xdg-open", $"\"{dir}\""));
        }
    }

    private static string? NormalizeMode(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.String) return null;
        var value = element.GetString();
        return value is "manual" or "auto" ? value : null;
    }

    private static int? ReadNullableInt(JsonElement element) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var value)
            ? value
            : null;

    private static bool? ReadNullableBool(JsonElement element) =>
        element.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? element.GetBoolean()
            : null;

    private static async Task<string> TitleOf(
        string projectId, Dictionary<string, string> cache)
    {
        lock (cache)
        {
            if (cache.TryGetValue(projectId, out var cached)) return cached;
        }
        var title = await _modrinth.GetProjectTitleAsync(projectId) ?? projectId;
        lock (cache)
        {
            cache[projectId] = title;
        }
        return title;
    }

    /// <summary>版本内容上下文：实例根、隔离、MC 版本、加载器（mods/资源包/光影共用）。</summary>
    private static (string GameRoot, bool Isolated, string McVersion,
        string Loader, string? LoaderVersion) VersionContentContext(string versionId)
    {
        if (!InstanceLayout.IsValidVersionId(versionId))
            throw new Exception("非法版本 id");
        var dotMc = _settings.DotMinecraft;
        var profile = new InstanceSettingsStore(dotMc).Get(versionId);
        var isolated = InstanceLayout.IsIsolated(profile, _settings);
        var root = InstanceLayout.GameRoot(dotMc, versionId, isolated);
        var loader = ContentLibraryService.ResolveLoader(versionId, out var loaderVersion);
        return (root, isolated, ContentLibraryService.ResolveMc(versionId),
            loader, loaderVersion);
    }

    private static string NormalizeKind(string? kind) => kind switch
    {
        "mod" or "resourcepack" or "shader" or "datapack" or "modpack" => kind,
        _ => throw new Exception($"未知内容类别：{kind}"),
    };

    private static string SafeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." ||
            name.IndexOfAny(['/', '\\', ':', '*', '?', '"', '<', '>', '|']) >= 0)
            throw new Exception("非法文件名");
        return name;
    }
    /// <summary>递归复制目录（跨版本复制世界用）。</summary>
    private static void CopyDirectory(string src, string dst)
    {
        Directory.CreateDirectory(dst);
        foreach (var file in Directory.EnumerateFiles(src))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("session.lock", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(dst, name), overwrite: true);
        }
        foreach (var dir in Directory.EnumerateDirectories(src))
            CopyDirectory(dir, Path.Combine(dst, Path.GetFileName(dir)));
    }

    /// <summary>目标目录下不重名的目录名（同名则加 -2、-3…）。</summary>
    private static string NextAvailableDir(string parent, string baseName)
    {
        var name = baseName;
        for (var i = 2; Directory.Exists(Path.Combine(parent, name)); i++)
            name = $"{baseName}-{i}";
        return name;
    }
}
