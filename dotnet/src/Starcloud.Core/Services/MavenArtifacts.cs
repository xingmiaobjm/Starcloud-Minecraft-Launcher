// Maven 坐标解析：Fabric / Quilt 的 launcher-meta profile 只给 `name` + `url` + `sha1/size`，
// 不像 Mojang 版本 JSON 那样带 `downloads.artifact`。之前安装器与启动命令都只认 downloads，
// 导致 fabric-loader / intermediary / ASM 这些 jar 既没下载也没进 classpath，
// Java 报 “找不到 Fabric 核心启动类”。PCL 能启动是因为它按 Maven 坐标解析。
//
// 这里把两种情况归一到同一个 DownloadableFile：Path = libraries 下的相对路径。

using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

public static class MavenArtifacts
{
    /// <summary>取库的实际下载信息：优先 downloads.artifact，其次按 name + url 拼 Maven 路径。</summary>
    public static DownloadableFile? Resolve(Library lib)
    {
        if (lib.Downloads?.Artifact is { Url: not "" } artifact) return artifact;
        if (string.IsNullOrWhiteSpace(lib.Name) || string.IsNullOrWhiteSpace(lib.Url)) return null;
        if (!TryBuildPath(lib.Name, out var path)) return null;

        return new DownloadableFile
        {
            Url = lib.Url.TrimEnd('/') + "/" + path,
            Path = path,
            Sha1 = lib.Sha1 ?? "",
            Size = lib.Size ?? 0,
        };
    }

    /// <summary>
    /// `group:artifact:version[:classifier][@ext]` → `group/artifact/version/artifact-version[-classifier].ext`
    /// </summary>
    public static bool TryBuildPath(string name, out string path)
    {
        path = "";
        var raw = name.Trim();
        if (raw.Length == 0) return false;

        var ext = "jar";
        var at = raw.IndexOf('@');
        if (at >= 0)
        {
            if (at == raw.Length - 1) return false;
            ext = raw[(at + 1)..];
            raw = raw[..at];
        }

        var parts = raw.Split(':');
        if (parts.Length is < 3 or > 4) return false;

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length == 4 ? parts[3] : null;
        if (group.Length == 0 || artifact.Length == 0 || version.Length == 0) return false;
        if (parts.Length == 4 && classifier!.Length == 0) return false;

        var fileName = $"{artifact}-{version}"
                       + (classifier is null ? "" : "-" + classifier)
                       + "." + ext;
        path = $"{group.Replace('.', '/')}/{artifact}/{version}/{fileName}";
        return true;
    }
}
