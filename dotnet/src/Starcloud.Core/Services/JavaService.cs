// Java 管理：系统检测 + Adoptium 下载解压（%APPDATA%/starcloud-launcher/java/<major>）

using System.Diagnostics;
using System.IO.Compression;

namespace Starcloud.Core.Services;

public sealed record JavaRuntime(string Executable, int MajorVersion, string VersionString, bool Is64Bit,
    string Source);

public sealed class JavaService
{
    private readonly DownloadService _downloader;

    public JavaService(DownloadService downloader) => _downloader = downloader;

    /// <summary>列出可用 Java：启动器已下载 + 官方启动器运行时 + 系统 PATH/JAVA_HOME 检测</summary>
    public async Task<List<JavaRuntime>> ListRuntimesAsync(CancellationToken ct = default,
        string? officialRuntimeDir = null)
    {
        var result = new List<JavaRuntime>();
        LauncherPaths.EnsureDataDir();

        // 已下载：java/<major>/bin/java.exe
        if (Directory.Exists(LauncherPaths.JavaDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(LauncherPaths.JavaDir))
            {
                var exe = Path.Combine(dir, "bin", "java.exe");
                if (!File.Exists(exe)) continue;
                var major = Path.GetFileName(dir);
                var version = await GetJavaVersionStringAsync(exe, ct) ?? $"Java {major}";
                result.Add(new JavaRuntime(exe, int.TryParse(major, out var m) ? m : 0, version, true, "launcher"));
            }
        }

        // 官方启动器运行时（.minecraft\runtime\java-runtime-* 等）
        if (officialRuntimeDir is not null && Directory.Exists(officialRuntimeDir))
        {
            foreach (var dir in Directory.EnumerateDirectories(officialRuntimeDir))
            {
                var exe = Path.Combine(dir, "bin", "java.exe");
                if (!File.Exists(exe)) continue;
                var version = await GetJavaVersionStringAsync(exe, ct);
                if (version is null) continue;
                var major = ParseMajorVersion(version);
                if (major <= 0) continue;
                result.Add(new JavaRuntime(exe, major, version, true, "official"));
            }
        }

        // 系统检测
        var candidates = new List<string>();
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        candidates.AddRange(pathVar.Split(';', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => Path.Combine(p.Trim(), "java.exe")));
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrEmpty(javaHome))
            candidates.Add(Path.Combine(javaHome, "bin", "java.exe"));

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var exe in candidates.Where(File.Exists))
        {
            if (!seen.Add(exe)) continue;
            var version = await GetJavaVersionStringAsync(exe, ct);
            if (version is null) continue;
            var major = ParseMajorVersion(version);
            if (major <= 0) continue;
            result.Add(new JavaRuntime(exe, major, version, true, "system"));
        }
        return result;
    }

    /// <summary>下载并解压 Adoptium JDK 到 java/&lt;major&gt;/</summary>
    public async Task<string> DownloadRuntimeAsync(int majorVersion,
        IProgress<BatchProgress>? progress, CancellationToken ct = default)
    {
        LauncherPaths.EnsureDataDir();
        var url = await _downloader.GetAdoptiumDownloadUrlAsync(majorVersion, ct);
        var zipPath = Path.Combine(LauncherPaths.JavaDir, $"temurin-{majorVersion}.zip");
        await _downloader.DownloadFileAsync(url, zipPath, null, 3, ct);
        var dest = Path.Combine(LauncherPaths.JavaDir, majorVersion.ToString());
        if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
        Directory.CreateDirectory(dest);
        ZipFile.ExtractToDirectory(zipPath, dest, overwriteFiles: true);
        File.Delete(zipPath);

        // Adoptium zip 根目录形如 jdk-21.0.x+y：若 bin/java.exe 在子目录则上提一层
        var exe = Path.Combine(dest, "bin", "java.exe");
        if (!File.Exists(exe))
        {
            var sub = Directory.GetDirectories(dest)
                .FirstOrDefault(d => File.Exists(Path.Combine(d, "bin", "java.exe")))
                ?? throw new InvalidOperationException("Java 解压后未找到 bin/java.exe");
            foreach (var inner in Directory.GetFileSystemEntries(sub))
            {
                var target = Path.Combine(dest, Path.GetFileName(inner));
                if (Directory.Exists(inner)) Directory.Move(inner, target);
                else File.Move(inner, target);
            }
            Directory.Delete(sub, recursive: true);
        }
        return exe;
    }

    public static void RemoveRuntime(int majorVersion)
    {
        var dir = Path.Combine(LauncherPaths.JavaDir, majorVersion.ToString());
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    /// <summary>校验任意 java.exe（版本设置「浏览」用）：版本字符串可解析即返回运行时，否则 null</summary>
    public static async Task<JavaRuntime?> ProbeAsync(string exe, CancellationToken ct = default)
    {
        var version = await GetJavaVersionStringAsync(exe, ct);
        if (version is null) return null;
        var major = ParseMajorVersion(version);
        return major <= 0 ? null : new JavaRuntime(exe, major, version, true, "custom");
    }

    private static async Task<string?> GetJavaVersionStringAsync(string exe, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var err = await p.StandardError.ReadToEndAsync(ct);
            await p.WaitForExitAsync(ct);
            return err.Split('\n').FirstOrDefault()?.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static int ParseMajorVersion(string versionLine)
    {
        // 形如：openjdk version "21.0.4" / java version "1.8.0_402"
        var m = System.Text.RegularExpressions.Regex.Match(
            versionLine, "version \"(?<v>[^\"]+)\"");
        if (!m.Success) return 0;
        var v = m.Groups["v"].Value;
        if (v.StartsWith("1."))
            return int.TryParse(v.AsSpan(2, 1), out var legacy) ? legacy : 0;
        return int.TryParse(v.Split('.')[0], out var major) ? major : 0;
    }
}
