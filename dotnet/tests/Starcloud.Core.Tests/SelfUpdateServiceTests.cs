// SelfUpdateService 单元测试：版本比较 / 清单反序列化 / 更新脚本生成 / 安装形态检测 / SHA256 校验

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class SelfUpdateServiceTests
{
    // ── 1. 版本比较 ────────────────────────────────────────────

    [Theory]
    [InlineData("0.2.1", "0.2.1", false)]   // 相等
    [InlineData("0.2.2", "0.2.1", true)]    // 补丁位升级
    [InlineData("0.3.0", "0.2.1", true)]    // 次版本升级
    [InlineData("1.0.0", "0.2.1", true)]    // 主版本升级
    [InlineData("0.2.0", "0.2.1", false)]   // 更旧
    [InlineData("0.2.1", "0.2.2", false)]   // 反向更旧
    [InlineData("0.2.1.0", "0.2.1", false)] // 4 段缺省补 0 后相等
    [InlineData("0.2.1", "0.2.1.0", false)]
    [InlineData("0.2.2+abc123", "0.2.1", true)]  // +commit 后缀剥离
    [InlineData("abc", "0.2.1", false)]     // 非法 → false（不误升级）
    [InlineData("0.2.1.5.1", "0.2.1", false)] // 5 段解析失败
    [InlineData("", "0.2.1", false)]
    public void IsNewerVersion_VariousCandidates_ReturnsExpected(
        string candidate, string current, bool expected)
    {
        Assert.Equal(expected, SelfUpdateService.IsNewerVersion(candidate, current));
    }

    [Fact]
    public void CurrentVersion_ReturnsNonEmpty()
    {
        // 入口程序集为测试宿主，InformationalVersion 缺省也有值（1.0.0 或 csproj 配置值）
        var v = SelfUpdateService.CurrentVersion();
        Assert.False(string.IsNullOrWhiteSpace(v));
        Assert.DoesNotContain("+", v);
    }

    // ── 2. 清单反序列化 ────────────────────────────────────────

    private const string ManifestJson = """
{
  "version": "0.2.2",
  "notes": "修复若干问题\n优化启动速度",
  "notes_en": null,
  "published_at": "2026-09-13T08:00:00Z",
  "setup": {
    "file": "Starcloud Minecraft Launcher_0.2.2_x64-setup-20260913-080000.exe",
    "sha256": "aa11223344556677889900aabbccddeeff11223344556677889900aabbccddee",
    "size": 152000000
  },
  "portable": {
    "file": "Starcloud Minecraft Launcher_0.2.2_x64-portable-20260913-080000.zip",
    "sha256": "bb11223344556677889900aabbccddeeff11223344556677889900aabbccddee",
    "size": 148000000
  }
}
""";

    [Fact]
    public void FetchManifest_ValidJson_ParsesSnakeCaseFields()
    {
        var m = System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(
            ManifestJson, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(m);
        Assert.Equal("0.2.2", m!.Version);
        Assert.Contains("\n", m.Notes);
        Assert.Equal(152000000L, m.Setup!.Size);
        Assert.StartsWith("Starcloud Minecraft Launcher_0.2.2_x64-setup", m.Setup.File);
        Assert.Equal(64, m.Portable!.Sha256.Length);
    }

    [Fact]
    public void FetchManifest_MissingSetup_DeserializesWithNullArtifact()
    {
        var json = """{"version":"0.3.0","notes":"x"}""";
        var m = System.Text.Json.JsonSerializer.Deserialize<UpdateManifest>(
            json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true });

        Assert.NotNull(m);
        Assert.Equal("0.3.0", m!.Version);
        Assert.Null(m.Setup);
        Assert.Null(m.Portable);
    }

    // ── 3. 更新脚本生成 ────────────────────────────────────────

    [Fact]
    public void BuildInstallerScript_ContainsSilentFlagAndRelaunch()
    {
        var s = SelfUpdateService.BuildInstallerScript(4242, @"C:\dl\setup 0.2.2.exe", @"C:\Programs\Starcloud\Starcloud.App.exe");

        Assert.Contains("-Id 4242", s);                       // 等待发起实例
        Assert.Contains("Get-Process -Name 'Starcloud.App'", s); // 等所有实例（去扩展名）
        Assert.Contains("'/S'", s);                           // 静默安装
        Assert.Contains("Start-Process -FilePath 'C:\\dl\\setup 0.2.2.exe'", s);
        Assert.Contains("Start-Process -FilePath 'C:\\Programs\\Starcloud\\Starcloud.App.exe'", s);
        Assert.Contains("Remove-Item -LiteralPath $PSCommandPath", s); // 自删
    }

    [Fact]
    public void BuildPortableScript_ContainsRobocopyMirrorAndRelaunch()
    {
        var s = SelfUpdateService.BuildPortableScript(
            7, @"C:\staging", @"D:\游戏 Starcloud", @"D:\游戏 Starcloud\Starcloud.App.exe");

        Assert.Contains("robocopy 'C:\\staging' 'D:\\游戏 Starcloud' /MIR", s); // 镜像覆盖
        Assert.Contains("-Id 7", s);
        Assert.Contains("Start-Process -FilePath 'D:\\游戏 Starcloud\\Starcloud.App.exe'", s);
        Assert.Contains("$LASTEXITCODE -ge 8", s);            // robocopy 失败判定
        Assert.Contains("Remove-Item -LiteralPath $PSCommandPath", s);
    }

    // ── 4. 安装形态检测 ────────────────────────────────────────

    [Fact]
    public void DetectInstallForm_RegistryMatchesExeDir_ReturnsNsis()
    {
        var form = SelfUpdateService.DetectInstallForm(
            @"C:\Users\u\AppData\Local\Programs\Starcloud",
            @"C:\Users\u\AppData\Local\Programs\Starcloud\", uninstallExeExists: false);
        Assert.Equal(UpdateInstallForm.NsisInstaller, form); // 尾斜杠差异仍匹配
    }

    [Fact]
    public void DetectInstallForm_UninstallExeFallback_ReturnsNsis()
    {
        var form = SelfUpdateService.DetectInstallForm(
            @"C:\Some\Dir", registryInstallLocation: null, uninstallExeExists: true);
        Assert.Equal(UpdateInstallForm.NsisInstaller, form);
    }

    [Fact]
    public void DetectInstallForm_NoRegistryNoUninstall_ReturnsPortable()
    {
        var form = SelfUpdateService.DetectInstallForm(
            @"D:\Portable\Starcloud", registryInstallLocation: null, uninstallExeExists: false);
        Assert.Equal(UpdateInstallForm.Portable, form);
    }

    [Fact]
    public void DetectInstallForm_RegistryPointsElsewhere_ReturnsPortable()
    {
        var form = SelfUpdateService.DetectInstallForm(
            @"D:\Portable\Starcloud",
            @"C:\Users\u\AppData\Local\Programs\Starcloud", uninstallExeExists: false);
        Assert.Equal(UpdateInstallForm.Portable, form);
    }

    [Fact]
    public void DetectInstallForm_CaseInsensitiveMatch_ReturnsNsis()
    {
        var form = SelfUpdateService.DetectInstallForm(
            @"c:\users\u\appdata\local\programs\STARcloud",
            @"C:\Users\U\AppData\Local\Programs\Starcloud", uninstallExeExists: false);
        Assert.Equal(UpdateInstallForm.NsisInstaller, form);
    }

    // ── 5. SHA256 校验 ─────────────────────────────────────────

    [Fact]
    public void VerifyArtifact_CorrectHashAndSize_Passes()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sc-update-test-{Guid.NewGuid():N}.bin");
        var payload = "starcloud"u8.ToArray();
        File.WriteAllBytes(file, payload);
        try
        {
            using var sha = System.Security.Cryptography.SHA256.Create();
            var hash = Convert.ToHexString(sha.ComputeHash(payload)).ToLowerInvariant();
            var artifact = new UpdateArtifact("x.bin", hash, payload.Length);
            SelfUpdateService.VerifyArtifact(file, artifact); // 不抛即通过
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void VerifyArtifact_WrongHash_Throws()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sc-update-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(file, "starcloud"u8.ToArray());
        try
        {
            var artifact = new UpdateArtifact("x.bin", new string('0', 64), 9);
            Assert.Throws<InvalidDataException>(() => SelfUpdateService.VerifyArtifact(file, artifact));
        }
        finally
        {
            File.Delete(file);
        }
    }

    [Fact]
    public void VerifyArtifact_WrongSize_Throws()
    {
        var file = Path.Combine(Path.GetTempPath(), $"sc-update-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(file, "starcloud"u8.ToArray());
        try
        {
            var artifact = new UpdateArtifact("x.bin", "", 100); // 大小不匹配
            Assert.Throws<InvalidDataException>(() => SelfUpdateService.VerifyArtifact(file, artifact));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
