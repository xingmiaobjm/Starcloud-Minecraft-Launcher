// LaunchSpec 构造测试：端到端验证「组件清单 + 版本 JSON + 桌面 Core 参数引擎」
// 组装出的进程内 JVM 启动参数（类路径次序 / natives 搜索路径 / 规则按 linux 过滤 / LWJGL 库名注入）

using System.Text.Json;
using Starcloud.Android.Core.Components;
using Starcloud.Android.Core.Launch;
using Starcloud.Core.Models;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Android.Core.Tests;

public class LaunchSpecBuilderTests : IDisposable
{
    private const string Sha = "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private readonly string _root;
    private readonly string _sharedDir;
    private readonly string _gameDir;
    private readonly string _componentsRoot;

    public LaunchSpecBuilderTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "starcloud-tests", Guid.NewGuid().ToString("N"));
        _sharedDir = Path.Combine(_root, "shared");
        _gameDir = Path.Combine(_root, "game");
        _componentsRoot = Path.Combine(_root, "components");
        Directory.CreateDirectory(_sharedDir);
        Directory.CreateDirectory(_gameDir);
        Directory.CreateDirectory(_componentsRoot);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* 测试清理失败可忽略 */ }
    }

    // 八组件内置清单（与 M2 实际清单同名同结构）
    private const string ManifestJson = $$"""
        {
            "manifest_version": 1,
            "components": [
                { "name": "jre8",     "version": "8u402",  "url": "https://example.com/jre8.tar.xz",
                  "sha256": "{{Sha}}", "archive": "TarXz", "license": "GPL-2.0-with-classpath-exception" },
                { "name": "jre17",    "version": "17.0.10", "url": "https://example.com/jre17.tar.xz",
                  "sha256": "{{Sha}}", "archive": "TarXz", "license": "GPL-2.0-with-classpath-exception" },
                { "name": "jre21",    "version": "21.0.4", "url": "https://example.com/jre21.tar.xz",
                  "sha256": "{{Sha}}", "archive": "TarXz", "license": "GPL-2.0-with-classpath-exception" },
                { "name": "lwjgl3",   "version": "3.3.3",  "url": "https://example.com/lwjgl3.zip",
                  "sha256": "{{Sha}}", "archive": "Zip", "license": "BSD-3-Clause" },
                { "name": "glfw-stub", "version": "fcl-1.3.3.6", "url": "https://example.com/glfw-stub.zip",
                  "sha256": "{{Sha}}", "archive": "Zip", "license": "GPL-3.0" },
                { "name": "openal",   "version": "1.23.1", "url": "https://example.com/openal.zip",
                  "sha256": "{{Sha}}", "archive": "Zip", "license": "LGPL-2.1" },
                { "name": "mesa-zink", "version": "23.0",  "url": "https://example.com/mesa-zink.tar.xz",
                  "sha256": "{{Sha}}", "archive": "TarXz", "license": "MIT" },
                { "name": "gl4es",    "version": "1.2",    "url": "https://example.com/gl4es.tar.xz",
                  "sha256": "{{Sha}}", "archive": "TarXz", "license": "MIT" }
            ]
        }
        """;

    /// <summary>部署一个组件：目录 + 可选 jar / so 文件 + 完成标记</summary>
    private string DeployComponent(string name, string version,
        IEnumerable<string> jarNames, IEnumerable<string> soNames)
    {
        var dir = DeploymentPlanner.ComponentDir(_componentsRoot, name, version);
        Directory.CreateDirectory(dir);
        foreach (var jar in jarNames)
            File.WriteAllText(Path.Combine(dir, jar), "jar");
        foreach (var so in soNames)
            File.WriteAllText(Path.Combine(dir, so), "so");
        File.WriteAllText(Path.Combine(dir, DeploymentPlanner.OkMarker), "ok");
        return dir;
    }

    /// <summary>部署 1.20.4 启动所需的全部组件（Zink 链）</summary>
    private void DeployZinkComponents()
    {
        DeployComponent("jre21", "21.0.4", [], ["placeholder.txt"]);
        Directory.CreateDirectory(Path.Combine(_componentsRoot, "jre21", "21.0.4", "lib", "server"));
        File.WriteAllText(
            Path.Combine(_componentsRoot, "jre21", "21.0.4", "lib", "server", "libjvm.so"), "so");
        // FCL 真实形态：glfw-stub 为纯 native 组件（Pojav 血统 GLFW 实现在 libpojavexec.so 内），无 jar
        DeployComponent("glfw-stub", "fcl-1.3.3.6", [],
            ["libpojavexec.so", "libpojavexec_awt.so", "libc++_shared.so"]);
        DeployComponent("lwjgl3", "3.3.3", ["lwjgl.jar", "lwjgl-glfw.jar"],
            ["liblwjgl.so", "liblwjgl_opengl.so"]);
        DeployComponent("openal", "1.23.1", ["lwjgl-openal.jar"], ["libopenal.so"]);
        DeployComponent("mesa-zink", "23.0", [], ["libOSMesa_81.so"]);
    }

    /// <summary>部署 1.20.4 Gl4es 链所需组件（GL 层换 gl4es，其余同 Zink 链）</summary>
    private void DeployGl4esComponents()
    {
        DeployComponent("jre21", "21.0.4", [], ["placeholder.txt"]);
        Directory.CreateDirectory(Path.Combine(_componentsRoot, "jre21", "21.0.4", "lib", "server"));
        File.WriteAllText(
            Path.Combine(_componentsRoot, "jre21", "21.0.4", "lib", "server", "libjvm.so"), "so");
        DeployComponent("glfw-stub", "fcl-1.3.3.6", [],
            ["libpojavexec.so", "libpojavexec_awt.so", "libc++_shared.so"]);
        DeployComponent("lwjgl3", "3.3.3", ["lwjgl.jar", "lwjgl-glfw.jar"],
            ["liblwjgl.so", "liblwjgl_opengl.so"]);
        DeployComponent("openal", "1.23.1", ["lwjgl-openal.jar"], ["libopenal.so"]);
        DeployComponent("gl4es", "1.2", [], ["libgl4es_114.so"]);
    }

    /// <summary>布置实例的客户端 jar 与桌面库（含 LWJGL 坐标与 windows 专属库，用于验证剔除）</summary>
    private void DeployVersionFiles(string versionId = "1.20.4")
    {
        Directory.CreateDirectory(Path.Combine(_sharedDir, "versions", versionId));
        File.WriteAllText(Path.Combine(_sharedDir, "versions", versionId, versionId + ".jar"), "client");
        WriteLibrary("com/example/lib/1.0/lib-1.0.jar");
        WriteLibrary("org/lwjgl/lwjgl/3.3.3/lwjgl.jar");
        WriteLibrary("com/winonly/api/1.0/api-1.0.jar");
    }

    private void WriteLibrary(string relativePath)
    {
        var path = Path.Combine(_sharedDir, "libraries", relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "jar");
    }

    private static MinecraftVersion CreateVersion(string id = "1.20.4") => new()
    {
        Id = id,
        MainClass = "net.minecraft.client.main.Main",
        Type = "release",
        JavaVersion = new JavaVersionRef { Component = "java-runtime-delta", MajorVersion = 21 },
        Arguments = new VersionArguments
        {
            Game =
            [
                new ArgumentItem { Value = JsonSerializer.SerializeToElement("--username") },
                new ArgumentItem { Value = JsonSerializer.SerializeToElement("${auth_player_name}") },
            ],
            Jvm =
            [
                new ArgumentItem { Value = JsonSerializer.SerializeToElement("-Djava.io.tmpdir=${version_name}") },
            ],
        },
        Libraries =
        [
            new()
            {
                Name = "com.example:lib:1.0",
                Downloads = new LibraryDownloads
                {
                    Artifact = new DownloadableFile
                    {
                        Url = "https://libraries.example.com/lib.jar",
                        Path = "com/example/lib/1.0/lib-1.0.jar",
                    },
                },
            },
            // LWJGL 坐标：由组件提供，桌面库必须剔除（同时覆盖 org.lwjglx 前缀规则）
            new()
            {
                Name = "org.lwjgl:lwjgl:3.3.3",
                Downloads = new LibraryDownloads
                {
                    Artifact = new DownloadableFile
                    {
                        Url = "https://libraries.example.com/lwjgl.jar",
                        Path = "org/lwjgl/lwjgl/3.3.3/lwjgl.jar",
                    },
                },
            },
            // windows 专属库：linux 规则过滤后必须剔除
            new()
            {
                Name = "com.winonly:api:1.0",
                Downloads = new LibraryDownloads
                {
                    Artifact = new DownloadableFile
                    {
                        Url = "https://libraries.example.com/api.jar",
                        Path = "com/winonly/api/1.0/api-1.0.jar",
                    },
                },
                Rules = [new OsRule { Action = "allow", Os = new OsSpec { Name = "windows" } }],
            },
        ],
    };

    private LaunchOptions CreateOptions(string versionId = "1.20.4") => new(
        VersionId: versionId,
        PlayerName: "Steve",
        GameDir: _gameDir,
        SharedDir: _sharedDir,
        MinMemoryMb: 1024,
        MaxMemoryMb: 2048,
        JavaExecutable: null,
        ExtraJvmArgs: [],
        UseBuiltinJvmArgs: false,
        Credentials: new LaunchCredentials("uuid-1", "token-1", "msa"));

    private LaunchSpecBuilder CreateBuilder() =>
        new(ComponentManifest.Parse(ManifestJson),
            new GameLauncher(new InstallerService(new DownloadService(() => new LauncherSettings()))));

    [Fact]
    public async Task 构建1_20_4的Zink链启动参数()
    {
        DeployVersionFiles();
        DeployZinkComponents();
        var builder = CreateBuilder();

        var spec = await builder.BuildAsync(CreateVersion(), CreateOptions(),
            new LaunchRuntimeOptions(_componentsRoot));

        // 主类与运行时档位
        Assert.Equal("net.minecraft.client.main.Main", spec.MainClass);
        Assert.Equal(21, spec.JreMajor);
        Assert.Equal(RenderBackend.Zink, spec.Backend);
        Assert.Equal(_gameDir, spec.WorkingDir);

        // LWJGL 库名注入在最前：转译层 / glfw-stub / openal
        Assert.Equal("-Dorg.lwjgl.opengl.libname=libOSMesa_81.so", spec.JvmArgs[0]);
        Assert.Equal("-Dorg.lwjgl.glfw.libname=libpojavexec.so", spec.JvmArgs[1]);
        Assert.Equal("-Dorg.lwjgl.openal.libname=libopenal.so", spec.JvmArgs[2]);

        // 桌面 Core 引擎的常规 JVM 参数仍在
        Assert.Contains("-Xms1024M", spec.JvmArgs);
        Assert.Contains("-Xmx2048M", spec.JvmArgs);
        Assert.Contains("-Djava.io.tmpdir=1.20.4", spec.JvmArgs); // 变量替换生效

        // natives 搜索路径：JVM 参数与 spec.NativeDirs 一致（':' 拼接），覆盖 JRE 与全部图形组件目录
        // （不按 ':' 拆分断言：Windows 临时目录的盘符冒号会干扰）
        var nativesPath = spec.JvmArgs.Single(a => a.StartsWith("-Djava.library.path="))
            ["-Djava.library.path=".Length..];
        Assert.Equal(string.Join(':', spec.NativeDirs), nativesPath);
        Assert.Equal(5, spec.NativeDirs.Count); // jre21 server / glfw-stub / lwjgl3 / mesa-zink / openal
        Assert.Contains(
            Path.Combine(DeploymentPlanner.ComponentDir(_componentsRoot, "jre21", "21.0.4"), "lib", "server"),
            spec.NativeDirs);
        Assert.Contains(DeploymentPlanner.ComponentDir(_componentsRoot, "mesa-zink", "23.0"),
            spec.NativeDirs);

        // 类路径构成：组件 jar（lwjgl 两 jar）+ linux 允许的桌面库 + 实例 jar 收尾
        // （glfw-stub 为纯 native 组件，不贡献 jar）
        var classpath = spec.Classpath.ToList();
        Assert.Equal(4, classpath.Count); // lwjgl 两 jar + 桌面库 + 客户端 jar
        // linux 允许的桌面库在内
        Assert.Contains(classpath,
            p => p.EndsWith("lib-1.0.jar", StringComparison.Ordinal));
        // 组件 jar 必须先于桌面库（次序即类覆盖优先级）
        Assert.True(
            classpath.FindIndex(p => p.EndsWith("lwjgl-glfw.jar", StringComparison.Ordinal))
            < classpath.FindIndex(p => p.EndsWith("lib-1.0.jar", StringComparison.Ordinal)),
            "组件 jar 必须排在桌面库之前");
        // LWJGL 坐标与 windows 专属库被剔除
        Assert.DoesNotContain(classpath, p => p.Contains($"org{Path.DirectorySeparatorChar}lwjgl"));
        Assert.DoesNotContain(classpath, p => p.Contains("winonly"));
        // 实例 jar 收尾
        Assert.Equal(
            Path.Combine(_sharedDir, "versions", "1.20.4", "1.20.4.jar"), classpath[^1]);

        // 游戏参数：变量替换后的身份参数
        var gameArgs = spec.ProgramArgs.ToList();
        var nameIndex = gameArgs.IndexOf("Steve");
        Assert.True(nameIndex > 0, "玩家名必须经 ${auth_player_name} 替换出现");
        Assert.Equal("--username", gameArgs[nameIndex - 1]);

        // 进程环境：HOME 指向游戏目录、JAVA_HOME 指向 JRE 组件目录
        Assert.Equal(_gameDir, spec.Env["HOME"]);
        Assert.Equal(
            DeploymentPlanner.ComponentDir(_componentsRoot, "jre21", "21.0.4"), spec.Env["JAVA_HOME"]);
        // PATH 尾冒号：JvmHost 启动时在尾部追加进程原 PATH
        Assert.Equal(
            $"{DeploymentPlanner.ComponentDir(_componentsRoot, "jre21", "21.0.4")}/bin:",
            spec.Env["PATH"]);
        // natives 搜索路径同步进 LD_LIBRARY_PATH；音频固定走 OpenSL ES
        Assert.Equal(string.Join(':', spec.NativeDirs), spec.Env["LD_LIBRARY_PATH"]);
        Assert.Equal("false", spec.Env["FORCE_VSYNC"]);
        Assert.Equal("opensl", spec.Env["ALSOFT_DRIVERS"]);
        // Zink 链：桌面 GL 4.6 语义 + Mesa so 的 EGL 定位
        Assert.Equal("opengles3_desktopgl_zink_kopper", spec.Env["POJAV_RENDERER"]);
        Assert.Equal("3", spec.Env["LIBGL_ES"]);
        Assert.Equal(Path.Combine(
            DeploymentPlanner.ComponentDir(_componentsRoot, "mesa-zink", "23.0"), "libEGL_mesa.so"),
            spec.Env["POJAVEXEC_EGL"]);
        Assert.Equal("4.6", spec.Env["MESA_GL_VERSION_OVERRIDE"]);
        // TmpDir 未提供时回落到游戏目录下 tmp
        Assert.Equal(Path.Combine(_gameDir, "tmp"), spec.Env["TMPDIR"]);
    }

    [Fact]
    public async Task 构建1_12_2的Gl4es链启动参数()
    {
        // gl4es 仅对 ≤1.16 生效（1.17+ 要求 GL 3.2，强制也会被选择器纠正回 Zink）
        DeployVersionFiles("1.12.2");
        DeployGl4esComponents();
        var builder = CreateBuilder();

        var spec = await builder.BuildAsync(CreateVersion("1.12.2"), CreateOptions("1.12.2"),
            new LaunchRuntimeOptions(_componentsRoot, RenderChainPreference.Gl4es,
                NativeLibDir: "/data/app/nativelib", TmpDir: "/data/tmp"));

        // GL 层换成 gl4es，其余组件链不变
        Assert.Equal(RenderBackend.Gl4es, spec.Backend);
        Assert.Equal("-Dorg.lwjgl.opengl.libname=libgl4es_114.so", spec.JvmArgs[0]);
        Assert.Contains(
            DeploymentPlanner.ComponentDir(_componentsRoot, "gl4es", "1.2"), spec.NativeDirs);

        // gl4es：GL 2.1 语义的渲染标识与兼容开关
        Assert.Equal("opengles2", spec.Env["POJAV_RENDERER"]);
        Assert.Equal("2", spec.Env["LIBGL_ES"]);
        Assert.Equal("3", spec.Env["LIBGL_MIPMAP"]);
        Assert.Equal("1", spec.Env["LIBGL_NORMALIZE"]);
        Assert.Equal("1", spec.Env["LIBGL_NOINTOVLHACK"]);
        Assert.Equal("1", spec.Env["LIBGL_NOERROR"]);
        // 壳提供的 native 库目录与临时目录透传
        Assert.Equal("/data/app/nativelib", spec.Env["POJAV_NATIVEDIR"]);
        Assert.Equal("/data/tmp", spec.Env["TMPDIR"]);
        // Zink 专属的 Mesa 变量不应出现
        Assert.DoesNotContain("MESA_GL_VERSION_OVERRIDE", (IEnumerable<string>)spec.Env.Keys);
    }

    [Fact]
    public async Task 缺少客户端jar时给出下载指引()
    {
        DeployZinkComponents(); // 不布置 versions 目录
        var builder = CreateBuilder();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.BuildAsync(CreateVersion(), CreateOptions(),
                new LaunchRuntimeOptions(_componentsRoot)));
        Assert.Contains("缺少客户端 jar", ex.Message);
        Assert.Contains("版本页", ex.Message);
    }

    [Fact]
    public async Task 组件未部署时给出设置页指引()
    {
        DeployVersionFiles();
        // 只部署除 glfw-stub 外的组件
        DeployComponent("lwjgl3", "3.3.3", ["lwjgl.jar"], ["liblwjgl.so"]);
        DeployComponent("openal", "1.23.1", [], ["libopenal.so"]);
        DeployComponent("mesa-zink", "23.0", [], ["libOSMesa_81.so"]);
        var builder = CreateBuilder();

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            builder.BuildAsync(CreateVersion(), CreateOptions(),
                new LaunchRuntimeOptions(_componentsRoot)));
        Assert.Contains("glfw-stub 尚未部署", ex.Message);
    }

    [Fact]
    public void 启动前预检给出全部必需组件名()
    {
        Assert.Equal(
            ["jre21", "lwjgl3", "glfw-stub", "openal", "mesa-zink"],
            LaunchSpecBuilder.RequiredComponentNames("1.20.4", null, RenderChainPreference.Auto));
        Assert.Equal(
            ["jre8", "lwjgl3", "glfw-stub", "openal", "gl4es"],
            LaunchSpecBuilder.RequiredComponentNames("1.8.9", 8, RenderChainPreference.Gl4es));
    }
}
