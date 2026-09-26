// 启动器自更新：检查清单 → 下载校验 → 退出后由脚本静默升级 → 自动重启
// 设计：.trae/documents/auto-update-feature.md
//
// 两种安装形态（DetectInstallForm）：
//   NsisInstaller：注册表 InstallLocation == 当前 exe 目录，或目录有 uninstall.exe
//                  → 下载 setup.exe，退出后 `setup /S` 静默原地升级（沿用注册表目录）
//   Portable     ：其余情况 → 下载 zip 解压到 staging，退出后 robocopy /MIR 覆盖
//
// 注意：
//   - 下载/清单请求不接 DownloadSourceRegistry（那是 MC 资源镜像改写，自更新源必须原样直连）
//   - 版本号从入口程序集（Starcloud.App）读，Core 程序集自身版本与启动器版本无关
//   - 更新脚本用 PowerShell（UTF-8 BOM 编码写入），避免 cmd 按 ANSI 误读中文路径

using System.Diagnostics;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

public enum UpdateInstallForm
{
    NsisInstaller,
    Portable,
}

/// <summary>当前平台的自更新能力（跨平台迁移设计：macOS/Linux 打包形态在 Phase 5 落地）</summary>
public enum UpdateCapability
{
    /// <summary>退出后脚本静默升级 + 自动重启（Windows NSIS / 便携）</summary>
    SilentInstall,

    /// <summary>仅检查与下载新版本，由用户手动安装（macOS dmg / Linux deb·AppImage）</summary>
    ManualDownload,
}

/// <summary>更新产物（安装器或便携包），file 为相对清单 URL 的文件名</summary>
public sealed record UpdateArtifact(
    [property: JsonPropertyName("file")] string File,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("size")] long Size);

/// <summary>更新清单（服务端 latest.json）</summary>
public sealed record UpdateManifest(
    [property: JsonPropertyName("version")] string Version,
    [property: JsonPropertyName("notes")] string Notes,
    [property: JsonPropertyName("notes_en")] string? NotesEn,
    [property: JsonPropertyName("published_at")] string? PublishedAt,
    [property: JsonPropertyName("setup")] UpdateArtifact? Setup,
    [property: JsonPropertyName("portable")] UpdateArtifact? Portable);

public sealed class SelfUpdateService
{
    private const string MainExe = "Starcloud.App.exe";

    /// <summary>当前平台自更新能力：Windows 支持静默升级；macOS/Linux 引导用户手动下载安装</summary>
    public static UpdateCapability Capability =>
        OperatingSystem.IsWindows() ? UpdateCapability.SilentInstall : UpdateCapability.ManualDownload;

    /// <summary>内置清单地址（发布前确认服务器已部署该文件）</summary>
    public const string DefaultManifestUrl =
        "https://starcloudfur.cn/downloads/latest.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly Func<LauncherSettings> _settings;
    private readonly HttpClient _http;

    public SelfUpdateService(Func<LauncherSettings> settings)
    {
        _settings = settings;
        _http = new HttpClient(new SocketsHttpHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All,
        })
        {
            Timeout = TimeSpan.FromSeconds(100),
        };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Starcloud-Launcher");
    }

    /// <summary>设置覆盖优先（便于测试指向本地服务器），否则内置常量</summary>
    public string ManifestUrl
    {
        get
        {
            var s = _settings().UpdateManifestUrl?.Trim();
            return string.IsNullOrWhiteSpace(s) ? DefaultManifestUrl : s!;
        }
    }

    /// <summary>启动器自身版本（入口程序集 InformationalVersion，去 +commit 后缀）</summary>
    public static string CurrentVersion()
    {
        var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        var raw = asm.GetCustomAttributes(typeof(AssemblyInformationalVersionAttribute), false)
            .OfType<AssemblyInformationalVersionAttribute>()
            .FirstOrDefault()?.InformationalVersion ?? "";
        return NormalizeVersion(raw);
    }

    /// <summary>candidate 是否比 current 新；任一方解析失败（或更旧）都返回 false，安全侧不误升级</summary>
    public static bool IsNewerVersion(string candidate, string current)
    {
        var a = TryParseVersion(candidate);
        var b = TryParseVersion(current);
        // System.Version 把缺省 Build/Revision 视为 -1（小于 0），补齐为 0 使 "0.2.1" == "0.2.1.0"
        return a is not null && b is not null && NormalizeVersion(a) > NormalizeVersion(b);
    }

    private static Version NormalizeVersion(Version v) => new(
        v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));

    private static Version? TryParseVersion(string v)
    {
        if (string.IsNullOrWhiteSpace(v)) return null;
        var i = v.IndexOf('+');
        if (i >= 0) v = v[..i];
        return Version.TryParse(v.Trim(), out var ver) ? ver : null;
    }

    private static string NormalizeVersion(string v) =>
        TryParseVersion(v)?.ToString() ?? v.Trim();

    // ── 安装形态 ──

    /// <summary>注册表 InstallLocation 指向当前目录，或目录有 uninstall.exe → NSIS 安装版；否则便携版</summary>
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public static UpdateInstallForm DetectInstallForm()
    {
        string? regLoc = null;
        try
        {
            regLoc = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Starcloud\Starcloud Minecraft Launcher",
                null, null) as string;
        }
        catch { /* 注册表不可读按便携处理 */ }

        var exeDir = TrimDir(AppContext.BaseDirectory);
        var uninstallExists = File.Exists(Path.Combine(exeDir, "uninstall.exe"));
        return DetectInstallForm(exeDir, regLoc, uninstallExists);
    }

    internal static UpdateInstallForm DetectInstallForm(
        string exeDir, string? registryInstallLocation, bool uninstallExeExists)
    {
        if (!string.IsNullOrWhiteSpace(registryInstallLocation) &&
            SameDir(registryInstallLocation, exeDir))
            return UpdateInstallForm.NsisInstaller;
        if (uninstallExeExists) return UpdateInstallForm.NsisInstaller;
        return UpdateInstallForm.Portable;
    }

    private static bool SameDir(string a, string b)
    {
        try
        {
            return string.Equals(TrimDir(Path.GetFullPath(a)), TrimDir(Path.GetFullPath(b)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    private static string TrimDir(string d) =>
        d.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    // ── 检查与下载 ──

    /// <summary>拉取并解析更新清单；网络/解析异常向上抛（启动路径静默，手动路径提示）</summary>
    public async Task<UpdateManifest> FetchManifestAsync(CancellationToken ct = default)
    {
        // 去掉可能的 UTF-8 BOM：清单是打包脚本生成的（Windows PowerShell 的
        // Set-Content -Encoding UTF8 会带 BOM），而 System.Text.Json 的字符串重载
        // 遇到开头 U+FEFF 会直接判为非法 JSON。生成端已经改成无 BOM 写出，
        // 这里再兜一层，免得手工传的清单把自动更新整条路堵死。
        var json = (await _http.GetStringAsync(ManifestUrl, ct)).TrimStart('\uFEFF');
        var m = JsonSerializer.Deserialize<UpdateManifest>(json, JsonOptions)
                ?? throw new InvalidDataException("清单为空");
        return m;
    }

    /// <summary>
    /// 下载产物到 UpdateDownloadDir 并做大小 + SHA256 校验，返回成品路径。
    /// 已有校验通过的成品直接复用（覆盖「下载完没重启就退出」的场景）；不做断点续传。
    /// </summary>
    public async Task<string> DownloadArtifactAsync(
        UpdateArtifact artifact, IProgress<double>? progress, CancellationToken ct = default)
    {
        if (artifact is null || string.IsNullOrWhiteSpace(artifact.File))
            throw new InvalidDataException("清单缺少下载文件信息");
        Directory.CreateDirectory(LauncherPaths.UpdateDownloadDir);
        var target = Path.Combine(LauncherPaths.UpdateDownloadDir, artifact.File);

        if (File.Exists(target) && MatchesArtifact(target, artifact))
        {
            LauncherLog.Write("update", $"已缓存校验通过的更新包 {artifact.File}，跳过下载");
            return target;
        }

        var part = target + ".part";
        try
        {
            using var resp = await _http.GetAsync(
                ResolveArtifactUrl(artifact.File),
                HttpCompletionOption.ResponseHeadersRead, ct);
            resp.EnsureSuccessStatusCode();

            await using (var fs = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None))
            {
                var stream = await resp.Content.ReadAsStreamAsync(ct);
                var total = resp.Content.Headers.ContentLength;
                var buffer = new byte[81920];
                long read = 0;
                int n;
                while ((n = await stream.ReadAsync(buffer, ct)) > 0)
                {
                    await fs.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;
                    if (total is > 0) progress?.Report(read * 100.0 / total.Value);
                }
            }

            VerifyArtifact(part, artifact);
            File.Move(part, target, overwrite: true);
            LauncherLog.Write("update", $"更新包下载完成 {artifact.File}");
            return target;
        }
        catch
        {
            try { if (File.Exists(part)) File.Delete(part); } catch { }
            throw;
        }
    }

    /// <summary>产物 URL = 清单地址所在目录 + 文件名（构建脚本只填文件名，上传整体拷贝即可）</summary>
    private Uri ResolveArtifactUrl(string fileName)
    {
        if (!Uri.TryCreate(ManifestUrl, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"清单地址无效：{ManifestUrl}");
        return new Uri(baseUri, fileName);
    }

    private static bool MatchesArtifact(string file, UpdateArtifact artifact)
    {
        try
        {
            if (new FileInfo(file).Length != artifact.Size) return false;
            VerifyArtifact(file, artifact);
            return true;
        }
        catch { return false; }
    }

    /// <summary>流式校验大小 + SHA256，不匹配抛 InvalidDataException</summary>
    public static void VerifyArtifact(string file, UpdateArtifact artifact)
    {
        var fi = new FileInfo(file);
        if (artifact.Size > 0 && fi.Length != artifact.Size)
            throw new InvalidDataException($"文件大小不匹配（期望 {artifact.Size}，实际 {fi.Length}）");

        var expected = artifact.Sha256?.Trim() ?? "";
        if (expected.Length == 0) return; // 清单未提供哈希时只校验大小
        var actual = ComputeSha256(file);
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"SHA256 校验失败（期望 {expected[..Math.Min(12, expected.Length)]}…）");
    }

    private static string ComputeSha256(string file)
    {
        using var fs = File.OpenRead(file);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        int n;
        while ((n = fs.Read(buffer, 0, buffer.Length)) > 0)
            hash.AppendData(buffer, 0, n);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>便携版：解压 zip 到 staging 并确认主程序存在，返回 staging 路径</summary>
    public Task<string> ExtractPortableAsync(string zipPath, CancellationToken ct = default)
    {
        var staging = LauncherPaths.UpdateStagingDir;
        try
        {
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
        }
        catch (Exception ex)
        {
            throw new IOException("无法清理更新暂存目录（可能被占用）", ex);
        }
        Directory.CreateDirectory(staging);
        System.IO.Compression.ZipFile.ExtractToDirectory(zipPath, staging, overwriteFiles: true);
        if (!File.Exists(Path.Combine(staging, MainExe)))
            throw new InvalidDataException("更新包内容异常：未找到 Starcloud.App.exe");
        return Task.FromResult(staging);
    }

    // ── 更新脚本（PowerShell 5.1，UTF-8 BOM 写入以支持中文路径） ──

    /// <summary>写更新脚本并立即启动（不等待），脚本会等本实例与所有启动器实例退出后执行升级并重启</summary>
    public void LaunchUpdaterScript(int pid, UpdateInstallForm form, string artifactPath, string version)
    {
        var appDir = TrimDir(AppContext.BaseDirectory);
        var appExe = Path.Combine(appDir, MainExe);
        var script = form switch
        {
            UpdateInstallForm.NsisInstaller => BuildInstallerScript(pid, artifactPath, appExe),
            _ => BuildPortableScript(pid, LauncherPaths.UpdateStagingDir, appDir, appExe),
        };

        Directory.CreateDirectory(LauncherPaths.UpdateDir);
        var scriptPath = Path.Combine(LauncherPaths.UpdateDir,
            $"update-{DateTime.Now:yyyyMMddHHmmss}.ps1");
        File.WriteAllText(scriptPath, script, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        LauncherLog.Write("update", $"启动更新脚本（{form}，当前 {version}）：{scriptPath}");
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            CreateNoWindow = true,   // 项目约束：子进程禁止弹控制台窗口
            UseShellExecute = false,
        };
        Process.Start(psi);
    }

    /// <summary>安装器形态：等退出 → setup /S 静默原地升级 → 重启 → 自删</summary>
    internal static string BuildInstallerScript(int pid, string setupExe, string appExe)
    {
        const string tmpl = """
$ErrorActionPreference = 'SilentlyContinue'
# 等发起更新的实例退出（最多 60s）
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and (Get-Process -Id @PID@ -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
# 等所有启动器实例退出（覆盖多开），超时后强推
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and (Get-Process -Name '@MAINNAME@' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 2
# NSIS /S 静默安装自动沿用注册表里的原安装目录，原地升级
Start-Process -FilePath '@SETUP@' -ArgumentList '/S' -Wait
Start-Sleep -Seconds 1
Start-Process -FilePath '@APPEXE@'
Remove-Item -LiteralPath $PSCommandPath -Force
""";
        return tmpl.Replace("@PID@", pid.ToString())
            .Replace("@MAINNAME@", Path.GetFileNameWithoutExtension(MainExe))
            .Replace("@SETUP@", setupExe)
            .Replace("@APPEXE@", appExe);
    }

    /// <summary>便携形态：等退出 → robocopy /MIR 覆盖（与 NSIS 的 RMDir /r 语义对齐，杜绝新旧 dll 混装）→ 重启 → 自删</summary>
    internal static string BuildPortableScript(int pid, string stagingDir, string appDir, string appExe)
    {
        const string tmpl = """
$ErrorActionPreference = 'SilentlyContinue'
# 等发起更新的实例退出（最多 60s）
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and (Get-Process -Id @PID@ -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
# 等所有启动器实例退出（覆盖多开），超时后强推
$deadline = (Get-Date).AddSeconds(60)
while ((Get-Date) -lt $deadline -and (Get-Process -Name '@MAINNAME@' -ErrorAction SilentlyContinue)) { Start-Sleep -Milliseconds 500 }
Start-Sleep -Seconds 2
& robocopy '@STAGING@' '@APPDIR@' /MIR /R:2 /W:1 /NFL /NDL /NJH /NJS /NP | Out-Null
if ($LASTEXITCODE -ge 8) { exit 1 }
Start-Process -FilePath '@APPEXE@'
Remove-Item -LiteralPath $PSCommandPath -Force
""";
        return tmpl.Replace("@PID@", pid.ToString())
            .Replace("@MAINNAME@", Path.GetFileNameWithoutExtension(MainExe))
            .Replace("@STAGING@", stagingDir)
            .Replace("@APPDIR@", appDir)
            .Replace("@APPEXE@", appExe);
    }

    // ── 辅助 ──

    /// <summary>exe 目录可写探测（便携版放在 Program Files 时无法就地更新）</summary>
    public static bool IsAppDirWritable(string exeDir)
    {
        try
        {
            var probe = Path.Combine(TrimDir(exeDir), $".update-probe-{Guid.NewGuid():N}.tmp");
            File.WriteAllText(probe, "x");
            File.Delete(probe);
            return true;
        }
        catch { return false; }
    }

    /// <summary>清空自更新工作目录（下载缓存 / staging / 旧脚本），启动时调用；静默吞异常</summary>
    public static void CleanupLeftovers()
    {
        try
        {
            if (Directory.Exists(LauncherPaths.UpdateDir))
                Directory.Delete(LauncherPaths.UpdateDir, recursive: true);
        }
        catch { /* 被占用等场景下忽略，下次再清 */ }
        try { Directory.CreateDirectory(LauncherPaths.UpdateDir); } catch { }
    }
}
