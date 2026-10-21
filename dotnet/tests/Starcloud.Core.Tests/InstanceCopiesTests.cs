// 同名版本多份安装：目录名就是实例名，版本 JSON 里的 id 必须跟着改。
// 这两条回归护栏对应「装了 4 份 26.3，但每份在 UI 里都叫 26.3」的旧行为。

using System.Text.Json.Nodes;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

/// <summary>下载链路会往进程内单例 TaskCenterService 写聚合任务，
/// 与 TaskCenterServiceTests 抢同一个单例 —— 必须同集合串行。</summary>
[Collection("进程内单例")]
public class InstanceCopiesTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "sc-instance-copies-" + Guid.NewGuid().ToString("N"));

    public InstanceCopiesTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 清理失败不影响断言 */ }
    }

    private void MakeVersion(string id, string jsonId)
    {
        var dir = Path.Combine(_root, "versions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"),
            $"{{\"id\":\"{jsonId}\",\"type\":\"release\"}}");
    }

    [Fact]
    public void CountInstalledCopies_CountsBaseAndNumericSuffixesOnly()
    {
        MakeVersion("26.3", "26.3");
        MakeVersion("26.3-2", "26.3");
        MakeVersion("26.3-3", "26.3");
        MakeVersion("26.3-rc-3", "26.3-rc-3");   // 快照，不是副本
        MakeVersion("26.30", "26.30");           // 前缀相同但不是一个版本
        Directory.CreateDirectory(Path.Combine(_root, "versions", "26.3-4")); // 没有 json：不算

        var installer = new InstallerService(new DownloadService(() => new LauncherSettings()));
        Assert.Equal(3, installer.CountInstalledCopies(_root, "26.3"));
        Assert.Equal(0, installer.CountInstalledCopies(_root, "1.20.1"));
    }

    [Fact]
    public void CountInstalledCopies_IsCaseInsensitive()
    {
        MakeVersion("Fabric-Test", "Fabric-Test");
        var installer = new InstallerService(new DownloadService(() => new LauncherSettings()));
        Assert.Equal(1, installer.CountInstalledCopies(_root, "fabric-test"));
    }

    [Fact]
    public void PatchJsonId_KeepsBaseVersionForLaterLookup()
    {
        var dir = Path.Combine(_root, "versions", "26.3-2");
        Directory.CreateDirectory(dir);
        var json = Path.Combine(dir, "26.3-2.json");
        File.WriteAllText(json, "{\"id\":\"26.3\",\"type\":\"release\"}");

        InstanceLayout.PatchJsonId(json, "26.3-2");

        var obj = JsonNode.Parse(File.ReadAllText(json))!.AsObject();
        Assert.Equal("26.3-2", obj["id"]!.GetValue<string>());
        // 改名只能改 id，原版号要留在旁边，否则「这是哪个 MC 版本」就丢了
        Assert.Equal("26.3", obj["starcloudBaseVersion"]!.GetValue<string>());
        Assert.Equal("26.3", InstanceVersion.BaseGameVersion(_root, "26.3-2"));
    }
}
