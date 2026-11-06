// 加载器检测回归：整合包版本 id 是包名，不能靠前缀；合并式 JSON 没有 inheritsFrom。
// 这里用临时目录摆两个版本 JSON，验证 neoforge（合并式）与 fabric（inheritsFrom）都能认出来。

using System.Text.Json;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class LoaderDetectorTests
{
    private static string MakeDotMc()
    {
        var root = Path.Combine(Path.GetTempPath(), "sc-loader-detector-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        return root;
    }

    private static void WriteVersion(string dotMc, string id, object json)
    {
        var dir = Path.Combine(dotMc, "versions", id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, id + ".json"), JsonSerializer.Serialize(json));
    }

    [Fact]
    public void 合并式整合包_识别为neoforge并取到MC版本()
    {
        var dotMc = MakeDotMc();
        try
        {
            WriteVersion(dotMc, "机械动力航空学整合包", new
            {
                id = "机械动力航空学整合包",
                clientVersion = "1.21.1",
                mainClass = "cpw.mods.bootstraplauncher.BootstrapLauncher",
                libraries = new[]
                {
                    new { name = "net.neoforged:bus:8.0.5" },
                    new { name = "net.minecraftforge:srgutils:0.4.15" },   // 工具库，不能当 Forge 证据
                },
            });

            var (loader, mc) = LoaderDetector.Detect(dotMc, "机械动力航空学整合包");
            Assert.Equal("neoforge", loader);
            Assert.Equal("1.21.1", mc);
        }
        finally { Directory.Delete(dotMc, true); }
    }

    [Fact]
    public void 前缀版本_按id识别()
    {
        var dotMc = MakeDotMc();
        try
        {
            var (loader, mc) = LoaderDetector.Detect(dotMc, "fabric-loader-0.18.2-1.21.1");
            Assert.Equal("fabric", loader);
            Assert.Equal("1.21.1", mc);
        }
        finally { Directory.Delete(dotMc, true); }
    }

    [Fact]
    public void 原版版本_返回vanilla()
    {
        var dotMc = MakeDotMc();
        try
        {
            WriteVersion(dotMc, "1.21.1", new
            {
                id = "1.21.1",
                mainClass = "net.minecraft.client.main.Main",
                libraries = new[] { new { name = "com.mojang:brigadier:1.0.18" } },
            });

            var (loader, mc) = LoaderDetector.Detect(dotMc, "1.21.1");
            Assert.Equal("vanilla", loader);
            Assert.Equal("1.21.1", mc);
        }
        finally { Directory.Delete(dotMc, true); }
    }

    /// <summary>
    /// 副本实例（版本名 26.3-2）：JSON 里 id 仍是原版号、没有 inheritsFrom ——
    /// 这条链一断，下载弹窗的 MC 筛选就是 null，自动补前置会被整段跳过且不报错。
    /// </summary>
    [Fact]
    public void 副本实例_从JSON的id取MC版本()
    {
        var dotMc = MakeDotMc();
        try
        {
            WriteVersion(dotMc, "26.3-2", new
            {
                id = "26.3",
                mainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
                libraries = new[] { new { name = "net.fabricmc:fabric-loader:0.16.9" } },
            });

            var (loader, mc) = LoaderDetector.Detect(dotMc, "26.3-2");
            Assert.Equal("fabric", loader);
            Assert.Equal("26.3", mc);
        }
        finally { Directory.Delete(dotMc, true); }
    }
}
