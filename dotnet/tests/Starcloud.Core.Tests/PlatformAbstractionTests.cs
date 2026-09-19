// 平台抽象层单元测试：规则判定 / natives 候选 / classpath 分隔符 / Shell 命令 / 内存快照 / 归档解压
// 全部用例在 Windows/Linux/macOS 均可运行：平台差异用显式参数锁定，不依赖宿主 OS

using Starcloud.Core.Models;
using Starcloud.Core.Services;
using System.IO.Compression;
using Xunit;

namespace Starcloud.Core.Tests;

public class PlatformAbstractionTests
{
    // ── 1. 库规则判定（显式 osName 锁定三平台语义） ───────────────

    private static OsRule Rule(string osName, string action = "allow") => new()
    {
        Action = action,
        Os = new OsSpec { Name = osName },
    };

    [Theory]
    [InlineData("windows")]
    [InlineData("osx")]
    [InlineData("linux")]
    public void RulesAllow_MatchingOs_ReturnsTrue(string osName)
    {
        var rules = new List<OsRule> { Rule(osName) };
        Assert.True(InstallerService.RulesAllow(rules, osName));
    }

    [Theory]
    [InlineData("windows", "osx")]
    [InlineData("osx", "windows")]
    [InlineData("linux", "windows")]
    public void RulesAllow_DifferentOs_ReturnsFalse(string ruleOs, string queryOs)
    {
        var rules = new List<OsRule> { Rule(ruleOs) };
        Assert.False(InstallerService.RulesAllow(rules, queryOs));
    }

    [Fact]
    public void RulesAllow_DenyRule_OnQueriedOs_Blocks()
    {
        var rules = new List<OsRule>
        {
            new() { Action = "allow" },
            new() { Action = "deny", Os = new OsSpec { Name = "linux" } },
        };
        Assert.False(InstallerService.RulesAllow(rules, "linux"));
        Assert.True(InstallerService.RulesAllow(rules, "windows"));
    }

    [Fact]
    public void RulesAllow_DefaultOsName_EqualsExplicitCurrentPlatform()
    {
        // 同一组规则：默认参数必须与显式传当前平台 os.name 一致
        var rules = new List<OsRule> { Rule(PlatformInfo.MojangOsName) };
        Assert.Equal(
            InstallerService.RulesAllow(rules, PlatformInfo.MojangOsName),
            InstallerService.RulesAllow(rules));
    }

    // ── 2. natives classifier 候选 ──────────────────────────────

    private static DownloadableFile NativeFile(string path) =>
        new() { Url = "https://x/" + path, Path = path };

    [Fact]
    public void NativesOf_MacosCandidates_PreferArm64ThenFallBack()
    {
        var dl = new LibraryDownloads
        {
            Classifiers = new Dictionary<string, DownloadableFile>
            {
                ["natives-macos"] = NativeFile("b.jar"),
                ["natives-macos-arm64"] = NativeFile("a.jar"),
            },
        };
        Assert.Equal("a.jar", InstallerService.NativesOf(dl,
            ["natives-macos-arm64", "natives-macos", "natives-osx"])!.Path);
        Assert.Equal("b.jar", InstallerService.NativesOf(dl,
            ["natives-macos", "natives-osx"])!.Path);
    }

    [Fact]
    public void NativesOf_LegacyOsxKey_MatchesOldManifests()
    {
        // LWJGL2 时代（≤1.12）清单用 natives-osx
        var dl = new LibraryDownloads
        {
            Classifiers = new Dictionary<string, DownloadableFile> { ["natives-osx"] = NativeFile("old.jar") },
        };
        Assert.Equal("old.jar", InstallerService.NativesOf(dl,
            ["natives-macos", "natives-osx"])!.Path);
    }

    [Fact]
    public void NativesOf_NoMatchingKey_ReturnsNull()
    {
        // 老清单只有 windows natives：unix 平台不应错拿 windows 包
        var dl = new LibraryDownloads
        {
            Classifiers = new Dictionary<string, DownloadableFile> { ["natives-windows"] = NativeFile("w.jar") },
        };
        Assert.Null(InstallerService.NativesOf(dl, ["natives-linux"]));
    }

    [Fact]
    public void NativesOf_DefaultCandidates_AreCurrentPlatform()
    {
        var dl = new LibraryDownloads
        {
            Classifiers = new Dictionary<string, DownloadableFile>
            {
                [PlatformInfo.NativesClassifierCandidates[0]] = NativeFile("cur.jar"),
            },
        };
        Assert.Equal("cur.jar", InstallerService.NativesOf(dl)!.Path);
    }

    // ── 3. classpath 与启动命令行 ──────────────────────────────

    [Fact]
    public async Task BuildCommandLine_Classpath_UsesPlatformSeparator()
    {
        var tmp = CreateTempDir();
        try
        {
            var dotMc = Path.Combine(tmp, "mc");
            var libRel = Path.Combine("com", "example", "a", "1.0", "a-1.0.jar");
            var libFile = Path.Combine(dotMc, "libraries", libRel);
            Directory.CreateDirectory(Path.GetDirectoryName(libFile)!);
            await File.WriteAllTextAsync(libFile, "");
            var jar = Path.Combine(dotMc, "versions", "1.20.1", "1.20.1.jar");
            Directory.CreateDirectory(Path.GetDirectoryName(jar)!);
            await File.WriteAllTextAsync(jar, "");

            var version = new MinecraftVersion
            {
                Id = "1.20.1",
                MainClass = "net.minecraft.client.main.Main",
                MinecraftArguments = "-cp ${classpath} --username ${auth_player_name}",
                Libraries =
                [
                    new Library
                    {
                        Name = "com.example:a:1.0",
                        Downloads = new LibraryDownloads
                        {
                            Artifact = new DownloadableFile
                            {
                                Url = "https://x/a-1.0.jar", Path = libRel, Sha1 = "", Size = 1,
                            },
                        },
                    },
                ],
            };
            var launcher = new GameLauncher(new InstallerService(null!));
            var options = new LaunchOptions("1.20.1", "Steve", dotMc, dotMc, 512, 1024,
                "/opt/java/bin/java", [], false,
                Credentials: new LaunchCredentials("uuid-1", "token-1", "msa"));
            var cmd = await launcher.BuildCommandLineAsync(version, options);

            Assert.StartsWith("/opt/java/bin/java", cmd);
            // classpath 分隔符必须是当前平台语义（Windows ";" / unix ":"）
            Assert.Contains("-cp ", cmd);
            Assert.Contains(PlatformInfo.ClasspathSeparator, cmd);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── 4. pre/post 启动命令 Shell（cmd.exe /c 与 /bin/sh -c 各自验证） ──

    [Fact]
    public async Task RunPreLaunch_EchoCommand_ExecutesOnPlatformShell()
    {
        var tmp = CreateTempDir();
        Directory.CreateDirectory(tmp);
        try
        {
            var launcher = new GameLauncher(new InstallerService(null!));
            var options = new LaunchOptions("1.20.1", "Steve", tmp, tmp, 512, 1024,
                "/opt/java/bin/java", [], false,
                Credentials: new LaunchCredentials("u", "t", "msa"),
                PreLaunchCommand: "echo starcloud-prelaunch-ok");
            var (code, output) = await launcher.RunPreLaunchAsync(options);
            Assert.Equal(0, code);
            Assert.Contains("starcloud-prelaunch-ok", output);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── 5. 内存快照（三平台各自的获取路径） ─────────────────────

    [Fact]
    public void SystemMemory_Snapshot_TotalIsPositive()
    {
        var (totalMb, _) = SystemMemory.Snapshot();
        Assert.True(totalMb > 0);
    }

    // ── 6. 自更新能力与路径默认值 ──────────────────────────────

    [Fact]
    public void SelfUpdate_Capability_MatchesPlatform()
    {
        var expected = OperatingSystem.IsWindows()
            ? UpdateCapability.SilentInstall
            : UpdateCapability.ManualDownload;
        Assert.Equal(expected, SelfUpdateService.Capability);
    }

    [Fact]
    public void LauncherSettings_DefaultDotMinecraft_MatchesPlatformConvention()
    {
        Assert.Equal(PlatformInfo.DefaultDotMinecraft, new LauncherSettings().DotMinecraft);
    }

    // ── 7. Java 归档解压（各平台验证自己的真实归档格式） ─────────

    [Fact]
    public void JavaService_ExtractRuntimeArchive_RestoresJdkLayout()
    {
        var tmp = CreateTempDir();
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows CI 验证 zip 分支；tar.gz 分支由 Linux/macOS CI 验证
                var source = Path.Combine(tmp, "jdk-21.0.5");
                Directory.CreateDirectory(Path.Combine(source, "bin"));
                File.WriteAllText(Path.Combine(source, "bin", "java.exe"), "stub");
                File.WriteAllText(Path.Combine(source, "release"), "JAVA_VERSION=21");
                var zipPath = Path.Combine(tmp, "jdk.zip");
                // 手工带 jdk 根目录前缀建 zip（CreateFromDirectory 不含根目录，与 Adoptium 归档布局不符）
                using (var zip = ZipFile.Open(zipPath, ZipArchiveMode.Create))
                {
                    zip.CreateEntryFromFile(Path.Combine(source, "bin", "java.exe"),
                        "jdk-21.0.5/bin/java.exe");
                    zip.CreateEntryFromFile(Path.Combine(source, "release"), "jdk-21.0.5/release");
                }

                var dest = Path.Combine(tmp, "out");
                JavaService.ExtractRuntimeArchive(zipPath, dest);
                Assert.True(File.Exists(Path.Combine(dest, "jdk-21.0.5", "bin", "java.exe")));
            }
            else
            {
                var source = Path.Combine(tmp, "jdk-21.0.5");
                Directory.CreateDirectory(Path.Combine(source, "bin"));
                File.WriteAllText(Path.Combine(source, "bin", "java"), "stub");
                File.WriteAllText(Path.Combine(source, "release"), "JAVA_VERSION=21");
                var tarPath = Path.Combine(tmp, "jdk.tar.gz");
                using (var outFs = File.Create(tarPath))
                using (var gz = new System.IO.Compression.GZipStream(
                    outFs, System.IO.Compression.CompressionMode.Compress))
                {
                    System.Formats.Tar.TarFile.CreateFromDirectory(source, gz, includeBaseDirectory: true);
                }

                var dest = Path.Combine(tmp, "out");
                JavaService.ExtractRuntimeArchive(tarPath, dest);
                Assert.True(File.Exists(Path.Combine(dest, "jdk-21.0.5", "bin", "java")));
            }
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── 7b. JAVA_HOME 上提（Linux 一层 jdk-x/ 与 macOS 三层 jdk-x/Contents/Home/ 布局） ──

    private static string JavaStubName => PlatformInfo.JavaExeName;

    private static void MakeJdkTree(string root, params string[] nesting)
    {
        var home = root;
        foreach (var part in nesting) home = Path.Combine(home, part);
        Directory.CreateDirectory(Path.Combine(home, "bin"));
        File.WriteAllText(Path.Combine(home, "bin", JavaStubName), "stub");
        File.WriteAllText(Path.Combine(home, "release"), "JAVA_VERSION=21");
    }

    [Fact]
    public void JavaService_PromoteJavaHome_FlatArchiveRoot_MovesJdkUp()
    {
        var tmp = CreateTempDir();
        try
        {
            MakeJdkTree(tmp, "jdk-21.0.5");
            JavaService.PromoteJavaHome(tmp);
            Assert.True(File.Exists(Path.Combine(tmp, "bin", JavaStubName)));
            Assert.True(File.Exists(Path.Combine(tmp, "release")));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void JavaService_PromoteJavaHome_MacNestedLayout_MovesHomeContentsUp()
    {
        // macOS Adoptium 归档布局：jdk-21.0.5+11/Contents/Home/bin/java
        var tmp = CreateTempDir();
        try
        {
            MakeJdkTree(tmp, "jdk-21.0.5+11", "Contents", "Home");
            JavaService.PromoteJavaHome(tmp);
            Assert.True(File.Exists(Path.Combine(tmp, "bin", JavaStubName)));
            Assert.True(File.Exists(Path.Combine(tmp, "release")));
            Assert.False(Directory.Exists(Path.Combine(tmp, "jdk-21.0.5+11")));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void JavaService_PromoteJavaHome_AlreadyFlat_NoOp()
    {
        var tmp = CreateTempDir();
        try
        {
            MakeJdkTree(tmp);
            var before = File.GetLastWriteTimeUtc(Path.Combine(tmp, "bin", JavaStubName));
            JavaService.PromoteJavaHome(tmp);
            Assert.True(File.Exists(Path.Combine(tmp, "bin", JavaStubName)));
            Assert.Equal(before, File.GetLastWriteTimeUtc(Path.Combine(tmp, "bin", JavaStubName)));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    [Fact]
    public void JavaService_PromoteJavaHome_NoJavaFound_Throws()
    {
        var tmp = CreateTempDir();
        try
        {
            Directory.CreateDirectory(Path.Combine(tmp, "empty"));
            Assert.Throws<InvalidOperationException>(() => JavaService.PromoteJavaHome(tmp));
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── 8. PlatformInfo 自洽性 ─────────────────────────────────

    [Fact]
    public void PlatformInfo_OsName_IsOneOfThreeMojangNames()
    {
        Assert.Contains(PlatformInfo.MojangOsName, new[] { "windows", "osx", "linux" });
    }

    [Fact]
    public void PlatformInfo_AdoptiumParams_AreValid()
    {
        Assert.Contains(PlatformInfo.AdoptiumOs, new[] { "windows", "mac", "linux" });
        Assert.Contains(PlatformInfo.AdoptiumArch, new[] { "x64", "aarch64" });
    }

    [Fact]
    public void PlatformInfo_ClasspathSeparator_MatchesRuntime()
    {
        Assert.Equal(Path.PathSeparator.ToString(), PlatformInfo.ClasspathSeparator);
    }

    private static string CreateTempDir() =>
        Path.Combine(Path.GetTempPath(), "sc-platform-tests-" + Guid.NewGuid().ToString("N"));
}
