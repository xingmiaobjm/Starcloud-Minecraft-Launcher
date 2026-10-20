// LaunchSpec 构造器：复用桌面 Starcloud.Core 的完整参数构建逻辑
// （变量替换 / 新旧两代参数 / 规则过滤），只在平台差异处做覆盖：
// OS 规则按 linux 过滤、类路径与 natives 目录由组件目录自行组装。

using Starcloud.Android.Core.Components;
using Starcloud.Core.Models;
using Starcloud.Core.Services;

namespace Starcloud.Android.Core.Launch;

/// <summary>运行时选项：组件根目录与渲染链偏好；NativeLibDir/TmpDir 由 MAUI 壳按设备情况提供</summary>
public sealed record LaunchRuntimeOptions(
    string ComponentsRoot,
    RenderChainPreference RenderChain = RenderChainPreference.Auto,
    string? NativeLibDir = null,
    string? TmpDir = null);

public sealed class LaunchSpecBuilder
{
    private readonly ComponentManifest _manifest;
    private readonly GameLauncher _launcher;

    public LaunchSpecBuilder(ComponentManifest manifest, GameLauncher launcher)
    {
        _manifest = manifest;
        _launcher = launcher;
    }

    /// <summary>LWJGL 组件选择：版本 json 声明 ≥3.4（SDL 窗口/输入后端）用 FCL 3.4.1 定制包（含 SDL-over-GLFW 桥接），否则 3.3.3；未声明时 26.x 新命名默认 3.4.1</summary>
    public static string LwjglComponentNameOf(MinecraftVersion version)
    {
        var declared = version.Libraries
            .Where(l => l.Name.StartsWith("org.lwjgl:lwjgl:", StringComparison.Ordinal))
            .Select(l => l.Name["org.lwjgl:lwjgl:".Length..])
            .FirstOrDefault();
        if (declared is not null)
        {
            return Version.TryParse(declared, out var v) && v >= new Version(3, 4) ? "lwjgl341" : "lwjgl3";
        }
        return RuntimeSelector.ParseMinor(version.Id) is null ? "lwjgl341" : "lwjgl3";
    }

    /// <summary>给定版本与启动选项，构建进程内 JVM 可直接使用的 LaunchSpec</summary>
    public async Task<LaunchSpec> BuildAsync(MinecraftVersion version, LaunchOptions options,
        LaunchRuntimeOptions rt, CancellationToken ct = default)
    {
        var jreMajor = RuntimeSelector.SelectJreMajor(version.Id, version.JavaVersion?.MajorVersion);
        var backend = RenderChainSelector.Select(version.Id, rt.RenderChain);

        var jreEntry = _manifest.Find(RuntimeSelector.ComponentNameFor(jreMajor))
            ?? throw new InvalidOperationException(
                $"此版本需要 Java {jreMajor} 运行时，当前内置组件未提供（已提供 Java 8 / 17 / 21）。" +
                "请改用兼容版本，或补充 jre25 组件后再启动");
        var lwjglEntry = _manifest.Require(LwjglComponentNameOf(version));
        var glfwEntry = _manifest.Require("glfw-stub");
        var openalEntry = _manifest.Require("openal");
        var glEntry = _manifest.Require(RenderChainSelector.ComponentNameOf(backend));

        // glfw-stub（Pojav 血统 GLFW 实现在 libpojavexec.so 内）与转译层 so 必须进 natives 搜索路径
        var nativeDirs = NativeDirsOf(rt.ComponentsRoot, jreEntry, lwjglEntry, glfwEntry, openalEntry, glEntry);

        // JNA（oshi SystemReport / MC 26.x 引导期 NativeLibrariesBootstrap 依赖）：
        // 版本自带 jna.jar 的 jnidispatch 是桌面 glibc 构建，Android 上 dlopen 报 libc.so.6 缺失；
        // 有 jna 组件时以 -Djna.boot.library.path 注入 bionic 版 libjnidispatch（JNA 优先于 jar 内解压）
        var jnaNativeDir = ResolveJnaNativeDir(version, rt.ComponentsRoot);
        if (jnaNativeDir is not null)
        {
            nativeDirs.Add(jnaNativeDir);
        }

        // 类路径次序即类覆盖优先级：glfw-stub 若带胶水 jar（FCL 形态为纯 native，无 jar）
        // 必须排在 LWJGL 自带 glfw binding 之前，否则 JVM 加载官方 binding 后找不到 native 实现
        var classpath = new List<string>();
        classpath.AddRange(ComponentJars(glfwEntry, rt.ComponentsRoot));
        classpath.AddRange(ComponentJars(lwjglEntry, rt.ComponentsRoot));
        classpath.AddRange(DesktopLibraryJars(version, options.SharedDir));

        // 实例目录名才是本次启动的实例（与桌面 BuildCommandLineAsync 同一规则）：
        // JSON 里的 id 可能只是原版号，多实例并存时 jar 落在实例目录下
        var instanceId = string.IsNullOrWhiteSpace(options.VersionId) ? version.Id : options.VersionId;
        var ownJar = GameLauncher.ResolveClientJar(options.SharedDir, instanceId, version.Id)
                     ?? throw new InvalidOperationException(
                         $"实例 {instanceId} 缺少客户端 jar：请先在版本页完成下载");
        classpath.Add(ownJar);

        var distinct = classpath.Distinct(StringComparer.Ordinal).ToList();
        var overrides = new BuildOverrides(
            OsName: "linux",                          // 安卓 = Linux 内核，库规则按 linux 过滤
            NativesDir: string.Join(':', nativeDirs), // JVM 支持以 ':' 分隔的 natives 搜索路径
            Classpath: string.Join(':', distinct));
        var args = await _launcher.BuildArgumentsAsync(version, options, overrides, ct);

        // LWJGL 3.3+ 支持按库名覆盖 so 定位：把转译层 / glfw-stub / openal 的实际 so 交给它
        var jvmArgs = new List<string>
        {
            $"-Dorg.lwjgl.opengl.libname={RenderChainSelector.OpenGLLibraryName(backend)}",
            "-Dorg.lwjgl.glfw.libname=libpojavexec.so",
            "-Dorg.lwjgl.openal.libname=libopenal.so",
        };
        jvmArgs.AddRange(args.JvmArgs);
        if (jnaNativeDir is not null)
        {
            jvmArgs.Add($"-Djna.boot.library.path={jnaNativeDir}");
        }
        // LWJGL 用 System.loadLibrary 定位核心库（liblwjgl.so 等），只认 java.library.path /
        // org.lwjgl.librarypath，LD_LIBRARY_PATH 环境变量对它无效；
        // Debug=true 打印具体失败库名与搜索路径（引导期诊断，稳定后可关）
        var lwjglNativesDir = Path.Combine(
            DeploymentPlanner.ComponentDir(rt.ComponentsRoot, lwjglEntry.Name, lwjglEntry.Version),
            "natives", "arm64-v8a");
        jvmArgs.Add($"-Dorg.lwjgl.librarypath={lwjglNativesDir}");
        jvmArgs.Add($"-Djava.library.path={string.Join(':', nativeDirs)}");
        jvmArgs.Add("-Dorg.lwjgl.util.Debug=true");

        var jreDir = DeploymentPlanner.ComponentDir(rt.ComponentsRoot, jreEntry.Name, jreEntry.Version);
        var glDir = DeploymentPlanner.ComponentDir(rt.ComponentsRoot, glEntry.Name, glEntry.Version);
        var env = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["HOME"] = options.GameDir,
            ["JAVA_HOME"] = jreDir,
            // PATH 尾冒号：JvmHost 启动时在尾部追加进程原 PATH
            ["PATH"] = $"{jreDir}/bin:",
            ["LD_LIBRARY_PATH"] = string.Join(':', nativeDirs),
            ["FORCE_VSYNC"] = "false",
            ["ALSOFT_DRIVERS"] = "opensl",
            ["TMPDIR"] = rt.TmpDir ?? Path.Combine(options.GameDir, "tmp"),
            // 窗口 buffer 变换修正（libstarcloudwindow.so 读取，见其源码注释）：
            // 横屏时面板原生方向为竖屏，SDL 把 buffer 几何设成竖屏、MC 预旋转作画，
            // 但缺 buffer 变换标记导致合成器不反向旋转 → 画面整体转 90°。inverse 让
            // 合成器按显示变换补偿；identity/rot90/rot270 可用于对比验证
            ["STARCLOUD_WINDOW_TRANSFORM"] = "inverse",
        };
        if (!string.IsNullOrEmpty(rt.NativeLibDir))
        {
            env["POJAV_NATIVEDIR"] = rt.NativeLibDir;
        }
        switch (backend)
        {
            case RenderBackend.Zink:
                // Zink（Mesa kopper）：Turnip 之上的 OpenGL 转换层
                env["POJAV_RENDERER"] = "opengles3_desktopgl_zink_kopper";
                env["LIBGL_ES"] = "3";
                // EGL 实现统一指向组件目录的 mesa libEGL。MC 26.3 的 GlBackend.loadLibrary
                // 取 LWJGL OpenGL provider（即 org.lwjgl.opengl.libname 指定的库）的路径交给
                // SDL_GL_LoadLibrary 当 EGL 库加载，libname 因此必须是带 egl* 的 libEGL_mesa.so
                // （见 RenderChainSelector.OpenGLLibraryName 的注释）。随后它还会校验该库上
                // dlsym 的 glGetError 与 SDL 侧 eglGetProcAddress 结果一致，不一致会明确报
                // glGetError mismatch（日志可裁决）。FCL 定制的 LWJGL 中 GL.create 并未读取
                // org.lwjgl.opengl.contextAPI（GL.class 无此字符串引用），故 EGL 上下文模式
                // 那条路不可用，只能直接指定 libname。三键同指一份，保证 SDL 主路径与 FCL
                // 钩子代理（POJAVEXEC_EGL）读到的是同一份 EGL 实现
                string eglPath = Path.Combine(glDir, "libEGL_mesa.so");
                env["POJAVEXEC_EGL"] = eglPath;
                env["SDL_EGL_LIBRARY"] = eglPath;
                env["SDL_OPENGL_LIBRARY"] = eglPath;
                // Vulkan 驱动查找路径（liblinkerhook 拦截 android_dlopen_ext 后在此找
                // libvulkan_freedreno.so）。不设 VULKAN_DRIVER_SYSTEM，让 zink 使用
                // 随包的 Turnip：系统 Adreno 驱动缺 zink 所需扩展（日志实测其 Vulkan
                // 设备不满足 renderpearl 需求），必须走 Turnip 私有副本
                env["DRIVER_PATH"] = glDir;
                env["MESA_GLSL_CACHE_DIR"] = Path.Combine(options.GameDir, ".mesa_parser_cache");
                // 与 FCL 一致：这几个 dri 配置项名不带 MESA_ 前缀，mesa 的 xmlconfig
                // 支持用同名环境变量覆盖配置项
                env["MESA_GL_VERSION_OVERRIDE"] = "4.6";
                env["MESA_GLSL_VERSION_OVERRIDE"] = "460";
                env["force_glsl_extensions_warn"] = "true";
                env["allow_higher_compat_version"] = "true";
                env["allow_glsl_extension_directive_midshader"] = "true";
                // 刻意不提供 DRI 驱动定位（LIBGL_DRIVERS_PATH / MESA_LOADER_DRIVER_OVERRIDE）：
                // 一旦 zink 初始化成功，MC 会在 loadCriticalShaders 阶段以桌面版 GLSL 3.30
                // 编译 GUI 管线、被 ES 上下文拒绝（GLSL 3.30 is not supported）而崩溃退出，
                // 反而失去 Vulkan 回退。保持 GL 侧干净失败，让 MC 走 Vulkan(Turnip)
                break;
            case RenderBackend.Gl4es:
                // gl4es：GL 2.1 语义
                env["POJAV_RENDERER"] = "opengles2";
                env["LIBGL_ES"] = "2";
                env["LIBGL_MIPMAP"] = "3";
                env["LIBGL_NORMALIZE"] = "1";
                env["LIBGL_NOINTOVLHACK"] = "1";
                env["LIBGL_NOERROR"] = "1";
                break;
        }

        return new LaunchSpec(args.MainClass, jvmArgs, distinct, args.GameArgs,
            nativeDirs, env, jreMajor, options.GameDir, backend);
    }

    /// <summary>某版本启动需要的全部组件名（供下载页在启动前预检 / 预下载）</summary>
    public static string[] RequiredComponentNames(string mcVersionId, int? javaMajorVersion,
        RenderChainPreference pref)
    {
        var jre = RuntimeSelector.ComponentNameFor(RuntimeSelector.SelectJreMajor(mcVersionId, javaMajorVersion));
        var gl = RenderChainSelector.ComponentNameOf(RenderChainSelector.Select(mcVersionId, pref));
        // LWJGL 组件按版本命名规则近似判断（无版本 json 时 26.x 新命名默认 3.4.1）
        var lwjgl = RuntimeSelector.ParseMinor(mcVersionId) is null ? "lwjgl341" : "lwjgl3";
        return [jre, lwjgl, "glfw-stub", "openal", gl];
    }

    /// <summary>组件目录内全部 jar（递归，按路径排序保证确定性）；未部署直接抛错</summary>
    private static List<string> ComponentJars(ComponentEntry entry, string componentsRoot)
    {
        var dir = DeploymentPlanner.ComponentDir(componentsRoot, entry.Name, entry.Version);
        if (!Directory.Exists(dir))
            throw new InvalidOperationException($"组件 {entry.Name} 尚未部署：请先在设置页完成组件下载");
        return Directory.EnumerateFiles(dir, "*.jar", SearchOption.AllDirectories)
            .OrderBy(p => p, StringComparer.Ordinal).ToList();
    }

    /// <summary>版本 JSON 声明的桌面库：按 linux 规则过滤、剔除 LWJGL 坐标（org.lwjgl / org.lwjglx 由组件提供）</summary>
    private static List<string> DesktopLibraryJars(MinecraftVersion version, string dotMc) =>
        version.Libraries
            .Where(l => InstallerService.RulesAllow(l.Rules, "linux"))
            .Where(l => !l.Name.StartsWith("org.lwjgl", StringComparison.Ordinal))
            .Where(l => l.Downloads?.Artifact is { Url: not "" })
            .Select(l => Path.Combine(dotMc, "libraries", l.Downloads!.Artifact!.Path ?? ""))
            .Where(File.Exists)
            .ToList();

    /// <summary>
    /// 解析 MC 版本声明的 JNA 对应的 bionic libjnidispatch 目录：
    /// 版本精确匹配 → 回退不高于声明版的最高版 → 再回退最高版（natives ABI 跨小版本兼容）。
    /// 版本未声明 jna 或组件缺失返回 null——旧版本只在崩溃报告里用 JNA（失败可忽略），不阻断。
    /// </summary>
    private string? ResolveJnaNativeDir(MinecraftVersion version, string componentsRoot)
    {
        var declared = version.Libraries
            .Where(l => l.Name.StartsWith("net.java.dev.jna:jna:", StringComparison.Ordinal))
            .Select(l => l.Name["net.java.dev.jna:jna:".Length..])
            .FirstOrDefault();
        if (declared is null || _manifest.Find("jna") is not { } jnaEntry)
        {
            return null;
        }
        var root = DeploymentPlanner.ComponentDir(componentsRoot, jnaEntry.Name, jnaEntry.Version);
        if (!Directory.Exists(root))
        {
            return null;
        }
        var candidates = Directory.GetDirectories(root)
            .Select(d => (Dir: d, Ver: Version.TryParse(Path.GetFileName(d), out var v) ? v : null))
            .Where(t => t.Ver is not null)
            .OrderBy(t => t.Ver)
            .ToList();
        if (candidates.Count == 0)
        {
            return null;
        }
        if (Version.TryParse(declared, out var want))
        {
            var exact = candidates.FirstOrDefault(t => t.Ver!.Equals(want));
            if (exact.Dir is not null)
            {
                return exact.Dir;
            }
            var lower = candidates.LastOrDefault(t => t.Ver! <= want);
            if (lower.Dir is not null)
            {
                return lower.Dir;
            }
        }
        return candidates[^1].Dir;
    }

    /// <summary>递归枚举必需组件中含 .so 的目录集合（排序去重；JRE 的 lib/server 自然覆盖）</summary>
    private static List<string> NativeDirsOf(string componentsRoot, params ComponentEntry[] entries)
    {
        var dirs = new SortedSet<string>(StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            var dir = DeploymentPlanner.ComponentDir(componentsRoot, entry.Name, entry.Version);
            if (!Directory.Exists(dir)) continue; // jar 缺失由 ComponentJars 负责报错；so 目录缺失容忍
            foreach (var so in Directory.EnumerateFiles(dir, "*.so", SearchOption.AllDirectories))
                dirs.Add(Path.GetDirectoryName(so)!);
        }
        return dirs.ToList();
    }
}
