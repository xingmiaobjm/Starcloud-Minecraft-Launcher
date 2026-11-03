// 安装器直链构造：build 版本号可能带 MC 前缀（整合包依赖表里很常见），
// 不剥离就会拼出 .../{mc}-{mc}-{build}/... 直接 404（issue #6 的 lwjgl3ify 实例）。

using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class InstallerUrlTests
{
    [Fact]
    public void Forge纯版本号()
    {
        Assert.Equal(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.12.2-14.23.5.2860/forge-1.12.2-14.23.5.2860-installer.jar",
            ForgeInstallerService.ForgeInstallerUrl("1.12.2", "14.23.5.2860"));
    }

    [Fact]
    public void Forge带MC前缀时剥离()
    {
        // 整合包/手填最常见：1.7.10-10.13.4.1614。不剥离会 404
        Assert.Equal(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.7.10-10.13.4.1614/forge-1.7.10-10.13.4.1614-installer.jar",
            ForgeInstallerService.ForgeInstallerUrl("1.7.10", "1.7.10-10.13.4.1614"));
    }

    [Fact]
    public void Forge候选地址_两段在前三段在后()
    {
        // 实测：1.8.9 只认三段（1.8.9-11.15.1.2318-1.8.9），1.8 只认两段
        var two = ForgeInstallerService.ForgeInstallerUrl("1.8.9", "11.15.1.2318");
        var candidates = ForgeInstallerService.ForgeInstallerUrlCandidates(two, "1.8.9");

        Assert.Equal(2, candidates.Count);
        Assert.Equal(two, candidates[0]);
        Assert.Equal(
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.8.9-11.15.1.2318-1.8.9/forge-1.8.9-11.15.1.2318-1.8.9-installer.jar",
            candidates[1]);
    }

    [Fact]
    public void Forge候选地址_已是三段时不重复追加()
    {
        const string legacy =
            "https://maven.minecraftforge.net/net/minecraftforge/forge/1.7.10-10.13.4.1614-1.7.10/forge-1.7.10-10.13.4.1614-1.7.10-installer.jar";
        var candidates = ForgeInstallerService.ForgeInstallerUrlCandidates(legacy, "1.7.10");
        Assert.Single(candidates);
    }

    [Fact]
    public void NeoForge地址不追加Forge候选()
    {
        var url = ForgeInstallerService.NeoForgeInstallerUrl("1.21.1", "21.1.80");
        var candidates = ForgeInstallerService.ForgeInstallerUrlCandidates(url, "1.21.1");
        Assert.Single(candidates);
    }

    [Fact]
    public void NeoForge普通版本()
    {
        Assert.Equal(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.80/neoforge-21.1.80-installer.jar",
            ForgeInstallerService.NeoForgeInstallerUrl("1.21.1", "21.1.80"));
    }

    [Fact]
    public void NeoForge带前缀与1_20_1特例()
    {
        Assert.Equal(
            "https://maven.neoforged.net/releases/net/neoforged/neoforge/21.1.80/neoforge-21.1.80-installer.jar",
            ForgeInstallerService.NeoForgeInstallerUrl("1.21.1", "1.21.1-21.1.80"));
        // 1.20.1 的 NeoForge 产物挂在 legacy forge 构件下
        Assert.Equal(
            "https://maven.neoforged.net/releases/net/neoforged/forge/1.20.1-47.1.106/forge-1.20.1-47.1.106-installer.jar",
            ForgeInstallerService.NeoForgeInstallerUrl("1.20.1", "47.1.106"));
    }
}
