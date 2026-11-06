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

    // Fabric/Quilt 的 profile 库只有 Maven 坐标（name + url），没有 downloads.artifact。
    // 之前 classpath 只认 downloads，fabric-loader / intermediary / ASM 全被丢掉，
    // Java 启动即报“找不到主类 net.fabricmc.loader.impl.launch.knot.KnotClient”。
    [Fact]
    public async Task Fabric的Maven坐标库会进入classpath()
    {
        using var dotMc = new TempDotMc();
        dotMc.File("versions/fabric-loader-0.19.5-26.3/fabric-loader-0.19.5-26.3.json",
            """{"id":"26.3"}""");
        dotMc.File("versions/fabric-loader-0.19.5-26.3/fabric-loader-0.19.5-26.3.jar", "CLIENT");
        var loader = dotMc.File(
            "libraries/net/fabricmc/fabric-loader/0.19.5/fabric-loader-0.19.5.jar", "LOADER");

        var version = new MinecraftVersion
        {
            Id = "26.3",
            MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
            Arguments = new VersionArguments
            {
                Jvm = [Arg("-cp"), Arg("${classpath}")],
                Game = [Arg("--username"), Arg("${auth_player_name}")],
            },
            Libraries =
            [
                new Library
                {
                    Name = "net.fabricmc:fabric-loader:0.19.5",
                    Url = "https://maven.fabricmc.net/",
                    Sha1 = "deadbeef",
                    Size = 1,
                },
            ],
        };
        var options = new LaunchOptions("fabric-loader-0.19.5-26.3", "Steve",
            dotMc.Path, dotMc.Path, 512, 1024, "/opt/java/bin/java", [], false,
            Credentials: new LaunchCredentials("uuid-1", "token-1", "msa"));

        var cmd = await new GameLauncher(new InstallerService(null!))
            .BuildCommandLineAsync(version, options);

        Assert.Contains(loader, cmd);
        Assert.Contains("net.fabricmc.loader.impl.launch.knot.KnotClient", cmd);
    }

    private static ArgumentItem Arg(string value) =>
        new() { Value = System.Text.Json.JsonSerializer.SerializeToElement(value) };

    // 1.13 之前只有 minecraftArguments、没有 arguments.jvm：启动器必须自己补 -cp，
    // 否则 JVM 报“找不到或无法加载主类 com.mojang.rubydung.RubyDung”。
    [Fact]
    public async Task 远古版本没有arguments时自动补classpath()
    {
        using var dotMc = new TempDotMc();
        dotMc.File("versions/rd-132211/rd-132211.json", """{"id":"rd-132211"}""");
        var jar = dotMc.File("versions/rd-132211/rd-132211.jar", "RD");

        var version = new MinecraftVersion
        {
            Id = "rd-132211",
            MainClass = "com.mojang.rubydung.RubyDung",
            MinecraftArguments = "${auth_player_name} ${auth_session}",
        };
        var options = new LaunchOptions("rd-132211", "Steve", dotMc.Path, dotMc.Path,
            512, 1024, "/opt/java/bin/java", [], false,
            Credentials: new LaunchCredentials("uuid-1", "token-1", "external"));

        var cmd = await new GameLauncher(new InstallerService(null!))
            .BuildCommandLineAsync(version, options);

        Assert.Contains("-cp", cmd);
        Assert.Contains(jar, cmd);
        Assert.Contains("com.mojang.rubydung.RubyDung", cmd);
    }

    // Forge 1.13 之前：安装器把整条 classpath 写在 legacyClassPath 字段里，
    // 不是 arguments.jvm。不认它就会退化成启动器自己拼的 classpath ——
    // 少了 Forge 的补丁 jar，表现为「Forge 装了却报找不到主类」。
    [Fact]
    public async Task Forge老版本用legacyClassPath()
    {
        using var dotMc = new TempDotMc();
        dotMc.File("versions/forge-1.12.2/forge-1.12.2.json", """{"id":"forge-1.12.2"}""");
        var jar = dotMc.File("versions/forge-1.12.2/forge-1.12.2.jar", "F");

        var version = new MinecraftVersion
        {
            Id = "forge-1.12.2",
            MainClass = "net.minecraft.launchwrapper.Launch",
            LegacyClassPath = "C:\\forge\\patch.jar;C:\\forge\\lib\\guava.jar",
            MinecraftArguments = "--username ${auth_player_name}",
        };
        var options = new LaunchOptions("forge-1.12.2", "Steve", dotMc.Path, dotMc.Path,
            512, 1024, "/opt/java/bin/java", [], false,
            Credentials: new LaunchCredentials("uuid-1", "token-1", "external"));

        var cmd = await new GameLauncher(new InstallerService(null!))
            .BuildCommandLineAsync(version, options);

        // 必须是 legacyClassPath 里的那条，而不是启动器自己拼的
        Assert.Contains("-cp", cmd);
        Assert.Contains("patch.jar", cmd);
        Assert.DoesNotContain(jar, cmd);
        Assert.Contains("net.minecraft.launchwrapper.Launch", cmd);
    }

    // 1.16 起的官方日志参数：logging.client.argument 指向 log4j 配置文件。
    [Fact]
    public async Task 现代版本带上官方日志配置参数()
    {
        using var dotMc = new TempDotMc();
        dotMc.File("versions/1.21.1/1.21.1.json", """{"id":"1.21.1"}""");
        dotMc.File("versions/1.21.1/1.21.1.jar", "MC");

        var version = new MinecraftVersion
        {
            Id = "1.21.1",
            MainClass = "net.minecraft.client.main.Main",
            Arguments = new VersionArguments
            {
                Jvm = [Arg("-Dfoo=bar")],
                Game = [Arg("--username"), Arg("${auth_player_name}")],
            },
            Logging = System.Text.Json.JsonDocument.Parse(
                """{"client":{"argument":"-Dlog4j2.configurationFile=${assets_root}/log_configs/client-1.21.xml"}}"""
            ).RootElement,
        };
        var options = new LaunchOptions("1.21.1", "Steve", dotMc.Path, dotMc.Path,
            512, 1024, "/opt/java/bin/java", [], false,
            Credentials: new LaunchCredentials("uuid-1", "token-1", "msa"));

        var cmd = await new GameLauncher(new InstallerService(null!))
            .BuildCommandLineAsync(version, options);

        Assert.Contains("-Dlog4j2.configurationFile=", cmd);
        // ${assets_root} 要被替换成真实的 assets 目录
        Assert.DoesNotContain("${assets_root}", cmd);
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
