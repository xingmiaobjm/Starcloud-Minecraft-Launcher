// 客户端 jar 定位：实例目录名才是启动时该用的身份。
//
// 真实故障（错误码 1 + java.lang.ClassNotFoundException: net.minecraft.client.main.Main）：
// 官网 26.3 装了两次 → 第二个实例目录叫 26.3-2，但版本 JSON 里的 id 仍是 26.3。
// 启动时按 JSON 的 id 去拼 versions/26.3/26.3.jar（不存在）→ jar 被剔除出 -cp → 0 秒踢出。

using Starcloud.Core.Models;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class GameLauncherClasspathTests
{
    [Fact]
    public async Task 实例目录名与JSON_id不同时用实例自己的jar()
    {
        using var dotMc = new TempDotMc();
        // 第二个 26.3 实例：目录 26.3-2，JSON id 还是 26.3（官网原始 JSON 没改过）
        dotMc.File("versions/26.3-2/26.3-2.json", """{"id":"26.3"}""");
        var instanceJar = dotMc.File("versions/26.3-2/26.3-2.jar", "CLIENT-JAR");

        var cmd = await CommandFor(dotMc, instanceId: "26.3-2", jsonId: "26.3");

        Assert.Contains(instanceJar, cmd);
        Assert.DoesNotContain(Path.Combine("versions", "26.3", "26.3.jar"), cmd);
    }

    [Fact]
    public async Task 加载器实例自己没有jar时回退到原版jar()
    {
        using var dotMc = new TempDotMc();
        dotMc.File("versions/fabric-loader-1.21.1/fabric-loader-1.21.1.json",
            """{"id":"1.21.1","inheritsFrom":"1.21.1"}""");
        var vanillaJar = dotMc.File("versions/1.21.1/1.21.1.jar", "VANILLA-CLIENT-JAR");

        var cmd = await CommandFor(dotMc, instanceId: "fabric-loader-1.21.1", jsonId: "1.21.1");

        Assert.Contains(vanillaJar, cmd);
    }

    [Fact]
    public async Task 完全没有客户端jar时给出可读错误而不是丢给java报错()
    {
        using var dotMc = new TempDotMc();
        dotMc.File("versions/空的/空的.json", """{"id":"1.21.1"}""");

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            CommandFor(dotMc, instanceId: "空的", jsonId: "1.21.1"));

        Assert.Contains("空的", ex.Message);
        Assert.Contains("jar", ex.Message);
    }

    [Fact]
    public void 定位jar时忽略sources与javadoc()
    {
        using var dotMc = new TempDotMc();
        dotMc.File("versions/1.21.1/1.21.1.json", """{"id":"1.21.1"}""");
        // 目录名被手工改过：主 jar 名字和目录名对不上，旁边还躺着体积更大的 sources
        dotMc.File("versions/1.21.1/1.21.1-sources.jar", new string('s', 4096));
        var client = dotMc.File("versions/1.21.1/手工改名的客户端.jar", new string('c', 512));

        var jar = GameLauncher.ResolveClientJar(dotMc.Path, "1.21.1", "1.21.1");

        Assert.Equal(client, jar);
    }

    // ── 测试基础设施 ──

    private static async Task<string> CommandFor(TempDotMc dotMc, string instanceId, string jsonId)
    {
        var version = new MinecraftVersion
        {
            Id = jsonId,
            MainClass = "net.minecraft.client.main.Main",
            MinecraftArguments = "-cp ${classpath} --username ${auth_player_name}",
        };
        var launcher = new GameLauncher(new InstallerService(null!));
        var options = new LaunchOptions(instanceId, "Steve", dotMc.Path, dotMc.Path, 512, 1024,
            "/opt/java/bin/java", [], false,
            Credentials: new LaunchCredentials("uuid-1", "token-1", "msa"));
        return await launcher.BuildCommandLineAsync(version, options);
    }

    private sealed class TempDotMc : IDisposable
    {
        public TempDotMc()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                "sc-cp-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public string File(string relative, string content)
        {
            var path = System.IO.Path.Combine(Path,
                relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            System.IO.File.WriteAllText(path, content);
            return path;
        }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }
}
