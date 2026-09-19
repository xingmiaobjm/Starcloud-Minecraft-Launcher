// Java 管理：系统检测 + Adoptium 下载解压（数据根/java/<major>；Windows zip，unix tar.gz）

using System.Diagnostics;
using System.IO.Compression;

namespace Starcloud.Core.Services;

public sealed record JavaRuntime(string Executable, int MajorVersion, string VersionString, bool Is64Bit,
    string Source);

public sealed class JavaService
{
    private readonly DownloadService _downloader;

    public JavaService(DownloadService downloader) => _downloader = downloader;

    /// <summary>
    /// 列出可用 Java：启动器已下载 + 官方启动器运行时（<c>.minecraft/runtime</c>）
    /// + 系统检测（Windows：PATH / JAVA_HOME / 注册表 / 厂商默认目录 / 各盘常见 Java 文件夹；
    /// unix：PATH / JAVA_HOME / 发行版与厂商惯例目录，不做整盘扫描）。
    /// </summary>
    public async Task<List<JavaRuntime>> ListRuntimesAsync(CancellationToken ct = default,
        string? officialRuntimeDir = null)
    {
        var taskCenter = TaskCenterService.Instance;
        var scanTask = taskCenter.Begin("搜索 Java");
        taskCenter.Update(scanTask, detail: "扫描已下载 / 官方 / 系统 Java");
        try
        {
            return await ListRuntimesCoreAsync(ct, officialRuntimeDir);
        }
        finally
        {
            taskCenter.Complete(scanTask, "扫描完成");
        }
    }

    private async Task<List<JavaRuntime>> ListRuntimesCoreAsync(CancellationToken ct = default,
        string? officialRuntimeDir = null)
    {
        var result = new List<JavaRuntime>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        LauncherPaths.EnsureDataDir();

        // 探测一个候选 exe，版本能解析出来才收进结果
        async Task AddAsync(string exe, string source)
        {
            if (ct.IsCancellationRequested) return;
            if (!seen.Add(NormalizeExe(exe))) return;
            var version = await GetJavaVersionStringAsync(exe, ct);
            if (version is null) return;
            var major = ParseMajorVersion(version);
            if (major <= 0) return;
            result.Add(new JavaRuntime(exe, major, version, true, source));
        }

        // ① 已下载：java/<major>/bin/java（目录名即主版本，省一次探测）
        foreach (var dir in SafeEnumerateDirectories(LauncherPaths.JavaDir))
        {
            var exe = Path.Combine(dir, "bin", PlatformInfo.JavaExeName);
            if (!File.Exists(exe) || !seen.Add(NormalizeExe(exe))) continue;
            var major = Path.GetFileName(dir);
            var version = await GetJavaVersionStringAsync(exe, ct) ?? $"Java {major}";
            result.Add(new JavaRuntime(exe, int.TryParse(major, out var m) ? m : 0, version, true, "launcher"));
        }

        // ② 官方启动器运行时（.minecraft/runtime/java-runtime-* 等）
        foreach (var root in RuntimeSearchRoots(officialRuntimeDir))
            foreach (var exe in EnumerateChildJavaExes(root))
                await AddAsync(exe, "official");

        // ③ 系统 PATH / JAVA_HOME
        var pathVar = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var entry in pathVar.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var exe in JavaExesIn(entry.Trim().Trim('"')))
                await AddAsync(exe, "system");

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
            foreach (var exe in JavaExesIn(javaHome))
                await AddAsync(exe, "system");

        if (OperatingSystem.IsWindows())
        {
            // ④ 注册表：各厂商安装时登记的 Java 主目录
            foreach (var home in await QueryRegistryJavaHomesAsync())
                foreach (var exe in JavaExesIn(home))
                    await AddAsync(exe, "system");

            // ⑤ 各厂商默认安装目录：装完没写 PATH、也没登记注册表的情况
            foreach (var root in ExpandJavaInstallRoots())
                foreach (var exe in EnumerateChildJavaExes(root))
                    await AddAsync(exe, "system");

            // ⑥ 各盘根目录下名字像 Java 的文件夹（如 D:\Java\jdk-17、C:\jdk1.8）：PCL 能扫到的那类
            foreach (var exe in EnumerateDriveJavaExes())
                await AddAsync(exe, "system");
        }
        else
        {
            // ④ unix 系统惯例目录（发行版包管理器 / 厂商 / SDKMAN；固定目录，无整盘扫描）
            foreach (var home in EnumerateUnixJavaHomes())
                foreach (var exe in JavaExesIn(home))
                    await AddAsync(exe, "system");
        }

        return result;
    }

    // ── 候选 java.exe 的收集 ──

    /// <summary>
    /// 要扫描的「运行时目录」：调用方给的游戏目录下的 <c>runtime</c>，以及官方启动器默认的
    /// <c>.minecraft/runtime</c>（没传参时也要扫，否则只装了官方启动器的机器一个都搜不到）。
    /// </summary>
    private static IEnumerable<string> RuntimeSearchRoots(string? gameRuntimeDir)
    {
        if (!string.IsNullOrWhiteSpace(gameRuntimeDir)) yield return gameRuntimeDir;
        var dotMc = PlatformInfo.DefaultDotMinecraft;
        if (!string.IsNullOrWhiteSpace(dotMc))
            yield return Path.Combine(dotMc, "runtime");
    }

    /// <summary>
    /// unix 系统 Java 主目录（均为惯例固定位置，不递归扫描）：
    /// macOS /Library/Java/JavaVirtualMachines/&lt;jdk&gt;/Contents/Home（用户目录同名）；
    /// Linux /usr/lib/jvm/*（发行版包管理器）、/opt/*（厂商解压版）、SDKMAN ~/.sdkman/candidates/java。
    /// </summary>
    private static IEnumerable<string> EnumerateUnixJavaHomes()
    {
        if (OperatingSystem.IsMacOS())
        {
            foreach (var jvm in SafeEnumerateDirectories("/Library/Java/JavaVirtualMachines"))
                yield return Path.Combine(jvm, "Contents", "Home");
            var userJvm = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                "Library", "Java", "JavaVirtualMachines");
            foreach (var jvm in SafeEnumerateDirectories(userJvm))
                yield return Path.Combine(jvm, "Contents", "Home");
            // Homebrew openjdk（opt 下为指向 keg 的软链接目录）
            foreach (var dir in SafeEnumerateDirectories("/opt/homebrew/opt"))
                yield return dir;
        }
        else
        {
            foreach (var dir in SafeEnumerateDirectories("/usr/lib/jvm"))
                yield return dir;
            foreach (var dir in SafeEnumerateDirectories("/opt"))
                yield return dir;
            var sdkman = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                ".sdkman", "candidates", "java");
            foreach (var dir in SafeEnumerateDirectories(sdkman))
                yield return dir;
        }
    }

    /// <summary>各 Java 厂商在注册表中登记安装位置的根键（HKLM 与 HKCU 都会查）</summary>
    private static readonly string[] RegistryJavaRoots =
    [
        @"SOFTWARE\JavaSoft",
        @"SOFTWARE\Eclipse Adoptium",
        @"SOFTWARE\Eclipse Foundation",
        @"SOFTWARE\Microsoft\JDK",
        @"SOFTWARE\Azul Systems\Zulu",
        @"SOFTWARE\Amazon Corretto",
        @"SOFTWARE\BellSoft",
        @"SOFTWARE\SapMachine",
        @"SOFTWARE\IBM\Semeru",
        @"SOFTWARE\GraalVM",
        @"SOFTWARE\AdoptOpenJDK",
    ];

    private static readonly string[] RegistryHives = ["HKLM", "HKCU"];

    /// <summary>各厂商默认安装根目录：其一级子目录形如 &lt;root&gt;\jdk-17\bin\java.exe</summary>
    private static readonly string[] JavaInstallRoots =
    [
        @"%ProgramFiles%\Java",
        @"%ProgramFiles(x86)%\Java",
        @"%ProgramFiles%\Eclipse Adoptium",
        @"%ProgramFiles%\Eclipse Foundation",
        @"%ProgramFiles%\Microsoft",
        @"%ProgramFiles%\Zulu",
        @"%ProgramFiles%\Azul Systems\Zulu",
        @"%ProgramFiles%\Amazon Corretto",
        @"%ProgramFiles%\BellSoft",
        @"%ProgramFiles%\SapMachine",
        @"%ProgramFiles%\IBM\Semeru",
        @"%ProgramFiles%\AdoptOpenJDK",
        @"%ProgramFiles%\GraalVM",
        @"%ProgramFiles%\Common Files\Oracle\Java",
        @"%ProgramFiles%\Android",
        @"%LOCALAPPDATA%\Programs\Eclipse Adoptium",
        @"%LOCALAPPDATA%\Programs\Java",
        @"%LOCALAPPDATA%\Programs\Android",
        @"%USERPROFILE%\.jdks",
    ];

    /// <summary>本身就是 Java 主目录、但层级不规则的常见位置</summary>
    private static readonly string[] DirectJavaHomes =
    [
        @"%ProgramFiles%\Android\Android Studio\jbr",
        @"%LOCALAPPDATA%\Programs\Android Studio\jbr",
        @"%ProgramFiles%\Java\jdk-21",
    ];

    /// <summary><paramref name="dir"/> 里的 java：兼容 <c>dir/bin/java</c> 与直接躺在目录里的（如 Windows javapath）</summary>
    private static IEnumerable<string> JavaExesIn(string? dir)
    {
        if (string.IsNullOrWhiteSpace(dir)) yield break;
        var bin = Path.Combine(dir, "bin", PlatformInfo.JavaExeName);
        if (File.Exists(bin)) yield return bin;
        var direct = Path.Combine(dir, PlatformInfo.JavaExeName);
        if (File.Exists(direct)) yield return direct;
    }

    /// <summary><paramref name="root"/> 下每个一级子目录里的 java.exe</summary>
    private static IEnumerable<string> EnumerateChildJavaExes(string? root)
    {
        foreach (var dir in SafeEnumerateDirectories(root))
            foreach (var exe in JavaExesIn(dir))
                yield return exe;
    }

    private static string[] SafeEnumerateDirectories(string? root)
    {
        if (string.IsNullOrWhiteSpace(root) || root.Contains('%') || !Directory.Exists(root)) return [];
        try { return Directory.GetDirectories(root); }
        catch { return []; }
    }

    /// <summary>目录名里出现这些词就认为它可能是 Java 安装目录（对齐 PCL 的按名匹配）</summary>
    private static readonly string[] JavaFolderKeywords =
    [
        "java", "jdk", "jre", "jvm", "jbr", "temurin", "zulu", "corretto", "adoptium",
        "adoptopenjdk", "graalvm", "sapmachine", "semeru", "bellsoft", "liberica", "dragonwell",
    ];

    /// <summary>
    /// 扫各固定磁盘根目录下「名字像 Java」的一级目录及其一级子目录。
    /// <para>
    /// 覆盖 PCL 能搜到、但既不登记注册表也不写 PATH 的绿色/解压版（如 <c>D:\Java\jdk-17</c>）。
    /// 只看两层，避免整盘递归拖慢「搜索 Java」。
    /// </para>
    /// </summary>
    private static IEnumerable<string> EnumerateDriveJavaExes()
    {
        DriveInfo[] drives;
        try { drives = DriveInfo.GetDrives(); }
        catch { yield break; }

        foreach (var drive in drives)
        {
            string root;
            try
            {
                if (drive.DriveType != DriveType.Fixed || !drive.IsReady) continue;
                root = drive.RootDirectory.FullName;
            }
            catch { continue; }

            foreach (var dir in SafeEnumerateDirectories(root))
            {
                var name = Path.GetFileName(dir);
                if (!JavaFolderKeywords.Any(k => name.Contains(k, StringComparison.OrdinalIgnoreCase)))
                    continue;
                foreach (var exe in JavaExesIn(dir)) yield return exe;
                foreach (var exe in EnumerateChildJavaExes(dir)) yield return exe;
            }
        }
    }

    private static IEnumerable<string> ExpandJavaInstallRoots()
    {
        foreach (var raw in JavaInstallRoots.Concat(DirectJavaHomes))
        {
            var path = Environment.ExpandEnvironmentVariables(raw);
            if (path.Contains('%')) continue;   // 该环境变量在本机不存在（如 32 位系统上的 ProgramFiles(x86)）
            yield return path;
        }
    }

    /// <summary>去重键：解析软链接（Oracle 的 javapath 是符号链接，否则同一运行时会列两次）</summary>
    private static string NormalizeExe(string exe)
    {
        try
        {
            var full = Path.GetFullPath(exe);
            return File.ResolveLinkTarget(full, returnFinalTarget: true)?.FullName ?? full;
        }
        catch
        {
            return exe;
        }
    }

    // ── 注册表查询 ──

    /// <summary>
    /// 用 <c>reg.exe</c> 读出各厂商登记的 Java 主目录。
    /// <para>
    /// 不用 <c>Microsoft.Win32.Registry</c>：Core 的目标框架是 <c>net8.0</c>（非 Windows 专用），
    /// 引用注册表 API 需要额外 NuGet 包；<c>reg.exe</c> 在所有 Windows 上都有，输出也易于解析。
    /// 各厂商的值名不统一（JavaHome / InstallationPath / Path…），故不按名称取值，
    /// 而是把所有 REG_SZ 值中「确实含 java.exe」的路径挑出来。
    /// </para>
    /// </summary>
    private static async Task<IReadOnlyList<string>> QueryRegistryJavaHomesAsync()
    {
        var queries = new List<Task<string?>>();
        foreach (var root in RegistryJavaRoots)
            foreach (var hive in RegistryHives)
                queries.Add(RunRegQueryAsync($@"{hive}\{root}"));

        var homes = new List<string>();
        foreach (var output in await Task.WhenAll(queries))
        {
            if (output is null) continue;
            foreach (var value in ParseRegStringValues(output))
                if (LooksLikeJavaHome(value)) homes.Add(value);
        }
        return homes;
    }

    private static async Task<string?> RunRegQueryAsync(string key)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "reg.exe",
                Arguments = $"query \"{key}\" /s",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi)!;
            var stdout = await p.StandardOutput.ReadToEndAsync();
            await p.WaitForExitAsync();
            return p.ExitCode == 0 ? stdout : null;   // 键不存在时 reg.exe 返回 1
        }
        catch
        {
            return null;
        }
    }

    /// <summary>取 <c>reg query /s</c> 输出里的 REG_SZ / REG_EXPAND_SZ 值（路径可能含空格，故按类型标记切分）</summary>
    private static IEnumerable<string> ParseRegStringValues(string regOutput)
    {
        foreach (var line in regOutput.Split('\n'))
        {
            var tokens = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var typeIndex = Array.FindIndex(tokens,
                t => t.Equals("REG_SZ", StringComparison.OrdinalIgnoreCase)
                     || t.Equals("REG_EXPAND_SZ", StringComparison.OrdinalIgnoreCase));
            if (typeIndex < 1 || typeIndex >= tokens.Length - 1) continue;
            yield return string.Join(' ', tokens[(typeIndex + 1)..]);
        }
    }

    /// <summary>该注册表值是否真的指向一个 Java（值可能是主目录，也可能直接是 java.exe）</summary>
    private static bool LooksLikeJavaHome(string value)
    {
        var path = Environment.ExpandEnvironmentVariables(value.Trim().Trim('"'));
        if (path.Length == 0 || path.Contains('%')) return false;
        if (path.EndsWith("java.exe", StringComparison.OrdinalIgnoreCase)) return File.Exists(path);
        return JavaExesIn(path).Any();
    }

    /// <summary>下载并解压 Adoptium JDK 到 java/&lt;major&gt;/（Windows zip，unix tar.gz）</summary>
    public async Task<string> DownloadRuntimeAsync(int majorVersion,
        IProgress<BatchProgress>? progress, CancellationToken ct = default)
    {
        var taskCenter = TaskCenterService.Instance;
        var dlTask = taskCenter.Begin($"下载 Java {majorVersion}");
        try
        {
            LauncherPaths.EnsureDataDir();
            var url = await _downloader.GetAdoptiumDownloadUrlAsync(majorVersion, ct);
            taskCenter.Update(dlTask, detail: "已取得下载地址，开始下载…");
            var archivePath = Path.Combine(LauncherPaths.JavaDir,
                $"temurin-{majorVersion}{PlatformInfo.JavaArchiveExtension}");
            await _downloader.DownloadFileAsync(url, archivePath, null, 3, ct);
            taskCenter.Update(dlTask, detail: "下载完成，解压安装中…");
            var dest = Path.Combine(LauncherPaths.JavaDir, majorVersion.ToString());
            if (Directory.Exists(dest)) Directory.Delete(dest, recursive: true);
            Directory.CreateDirectory(dest);
            ExtractRuntimeArchive(archivePath, dest);
            File.Delete(archivePath);
            PromoteJavaHome(dest);
            MakeRuntimeExecutable(dest);
            var exe = Path.Combine(dest, "bin", PlatformInfo.JavaExeName);
            taskCenter.Complete(dlTask, $"已安装到 {dest}");
            return exe;
        }
        catch (OperationCanceledException)
        {
            taskCenter.Fail(dlTask, "已取消");
            throw;
        }
        catch (Exception ex)
        {
            taskCenter.Fail(dlTask, ex.Message);
            throw;
        }
    }

    /// <summary>
    /// 归档根可能带一层目录（jdk-21.0.x+y），macOS 归档还会多两层（Contents/Home）：
    /// 定位含 bin/java 的目录并把内容上提到 dest 顶层（java/&lt;major&gt;/bin/java 即运行时主目录）。
    /// </summary>
    internal static void PromoteJavaHome(string dest)
    {
        var exe = Path.Combine(dest, "bin", PlatformInfo.JavaExeName);
        if (File.Exists(exe)) return;
        var home = FindJavaHomeDir(dest)
            ?? throw new InvalidOperationException(
                $"Java 解压后未找到 bin/{PlatformInfo.JavaExeName}");
        if (string.Equals(home, dest)) return;
        // 经 staging 平铺再整体替换，避免搬移过程与旧目录壳互相干扰
        var staging = dest + ".promote";
        Directory.CreateDirectory(staging);
        foreach (var inner in Directory.GetFileSystemEntries(home))
        {
            var target = Path.Combine(staging, Path.GetFileName(inner));
            if (Directory.Exists(inner)) Directory.Move(inner, target);
            else File.Move(inner, target);
        }
        Directory.Delete(dest, recursive: true);
        Directory.Move(staging, dest);
    }

    /// <summary>在解压目录内定位 JAVA_HOME（含 bin/java 的目录）；最多下探三层（jdk-x/Contents/Home 布局）</summary>
    private static string? FindJavaHomeDir(string root, int depth = 0)
    {
        if (File.Exists(Path.Combine(root, "bin", PlatformInfo.JavaExeName))) return root;
        if (depth >= 3) return null;
        foreach (var dir in SafeEnumerateDirectories(root))
        {
            var found = FindJavaHomeDir(dir, depth + 1);
            if (found is not null) return found;
        }
        return null;
    }

    /// <summary>按平台解压 Adoptium 归档（Windows zip；unix tar.gz，TarFile 不保留执行位，解压后补 chmod）</summary>
    internal static void ExtractRuntimeArchive(string archivePath, string dest)
    {
        if (OperatingSystem.IsWindows())
        {
            ZipFile.ExtractToDirectory(archivePath, dest, overwriteFiles: true);
            return;
        }
        using var fs = File.OpenRead(archivePath);
        using var gz = new GZipStream(fs, CompressionMode.Decompress);
        System.Formats.Tar.TarFile.ExtractToDirectory(gz, dest, overwriteFiles: true);
    }

    /// <summary>unix：bin/ 全部与 lib/jspawnserver 补 755 执行位（tar 解压不带权限，java 无法启动就白装）</summary>
    private static void MakeRuntimeExecutable(string runtimeDir)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()) return;
        try
        {
            const UnixFileMode mode = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                                      | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                                      | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
            foreach (var f in Directory.GetFiles(Path.Combine(runtimeDir, "bin")))
                File.SetUnixFileMode(f, mode);
            var jspawn = Path.Combine(runtimeDir, "lib", "jspawnserver");
            if (File.Exists(jspawn)) File.SetUnixFileMode(jspawn, mode);
        }
        catch { /* chmod 失败不致命：多数 JDK 仍可用 */ }
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
