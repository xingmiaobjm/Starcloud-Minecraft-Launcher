// 启动编排：内置组件部署 → Java 桥初始化 → 版本加载 → LaunchSpec 构建 → JVM 拉起。
// 由 MAUI 壳原样迁移（仅改命名空间）：业务逻辑与宿主/纯逻辑层调用完全不变。

using Starcloud.Android.Core.Components;
using Starcloud.Android.Core.Launch;
using Starcloud.Android.Host.Bridging;
using Starcloud.Android.Host.Runtime;
using Starcloud.Core.Models;
using Starcloud.Core.Services;

namespace Starcloud.Launcher.Phone.Services;

/// <summary>asset:// 内置组件包来源：从 APK assets 提供归档流</summary>
internal sealed class AssetComponentSource(global::Android.Content.Res.AssetManager assets)
    : IComponentArchiveSource
{
    public Stream? OpenArchive(string url)
    {
        const string prefix = "asset://";
        if (!url.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return null;
        try
        {
            return assets.Open(url[prefix.Length..]);
        }
        catch (Java.IO.FileNotFoundException)
        {
            return null;
        }
    }
}

public sealed class GameLauncherService
{
    public static GameLauncherService Instance { get; } = new(global::Android.App.Application.Context);

    /// <summary>启动过程进度日志（回调线程不定，消费方自行切主线程）</summary>
    public event Action<string>? Log;

    private readonly global::Android.Content.Context _context;
    private readonly string _dataRoot;
    private readonly string _dotMc;
    private readonly string _componentsRoot;
    private readonly string _cacheRoot;
    private readonly LauncherSettings _settings;
    private readonly ManifestService _manifests;
    private readonly InstallerService _installer;
    private bool _grabCallbacksRegistered;

    /// <summary>安装 / 启动共用设置：.minecraft 指向安卓外部私有目录</summary>
    public LauncherSettings Settings => _settings;

    /// <summary>崩溃取证：关键路径构造期实际值（UI 日志区直接可读，不依赖 logcat）</summary>
    public string PathDiagnostics =>
        $"files={_context.FilesDir!.AbsolutePath} external={_dataRoot} components={_componentsRoot}";

    /// <summary>游戏根目录（.minecraft），清单 / 安装服务所需</summary>
    public string DotMinecraft => _dotMc;

    /// <summary>版本清单（piston-meta，本地缓存优先）</summary>
    public ManifestService Manifests => _manifests;

    /// <summary>版本安装器</summary>
    public InstallerService Installer => _installer;

    /// <summary>账户库（离线账户；持久化于 DataDir/accounts.json）</summary>
    public AccountStore Accounts { get; }

    /// <summary>用户选定的启动版本（首页 / 版本页设置；空 = 已安装首个）</summary>
    public string? SelectedVersion { get; set; }

    /// <summary>
    /// 本次启动是否走 SDL 后端（MC 26.x / LWJGL ≥3.4）：GamePage 据此跳过 GLFW 桥的
    /// Surface 注册（SDL 窗口由 SdlGameActivity 的 SDLSurface 供给）
    /// </summary>
    public static volatile bool IsSdlMode;

    private GameLauncherService(global::Android.Content.Context context)
    {
        _context = context;
        // 外部私有目录（Android/data/<pkg>/files）：文件管理器可达、卸载即清（设计稿 §7）。
        // Main.Android OnCreate 已把 STARCLOUD_DATA_DIR 指向同一根，账户 / 清单缓存落此处。
        _dataRoot = context.GetExternalFilesDir(null)!.AbsolutePath!;
        _dotMc = Path.Combine(_dataRoot, ".minecraft");
        // 组件根必须落内部存储：targetSdk 30+ 的 linker classloader-namespace 白名单
        // （permitted_paths）不含外部私有目录，/storage/... 下的一切 so 无法 dlopen
        // （Java System.load 与 C# NativeLibrary.Load 同样被拒，真机实证 linker E 日志）。
        // /data/user/0 与 /data/data 是同一目录的两个视图，这里规范成 /data/data 形式
        // 保证 Debug（permitted 含 /data）与 Release（仅 /data/data/<pkg>）都命中白名单
        _componentsRoot = Path.Combine(NormalizedFilesDir(context), "components");
        _cacheRoot = Path.Combine(_dataRoot, "cache");
        _settings = new LauncherSettings { DotMinecraft = _dotMc };
        _manifests = new ManifestService(new DownloadService(() => _settings));
        _installer = new InstallerService(new DownloadService(() => _settings));
        Accounts = AccountStore.Load();
    }

    /// <summary>确保全部内置组件就位；返回解析后的组件清单</summary>
    public async Task<ComponentManifest> EnsureComponentsAsync(CancellationToken ct = default)
    {
        string json;
        await using (var stream = _context.Assets!.Open("components/manifest.json"))
        using (var reader = new StreamReader(stream))
            json = await reader.ReadToEndAsync(ct);
        var manifest = ComponentManifest.Parse(json);

        var deployer = new ComponentDeployer(_componentsRoot, _cacheRoot,
            new AssetComponentSource(_context.Assets));
        var plan = DeploymentPlanner.Plan(manifest, _componentsRoot, _cacheRoot,
            manifest.Components.Select(c => c.Name));
        Emit($"组件：待部署 {plan.Pending.Count} 个，已就位 {plan.AlreadyInstalled.Count} 个" +
            $"：{string.Join(",", plan.Pending.Select(p => p.Name))}");
        await deployer.DeployAllAsync(plan, ct);
        return manifest;
    }

    /// <summary>扫描已安装版本（versions/&lt;id&gt;/&lt;id&gt;.json），按名称排序</summary>
    public IReadOnlyList<string> FindVersions()
    {
        var dir = Path.Combine(_dotMc, "versions");
        if (!Directory.Exists(dir))
            return [];
        return Directory.GetDirectories(dir)
            .Select(d => Path.Combine(d, Path.GetFileName(d) + ".json"))
            .Where(File.Exists)
            .Select(p => Path.GetFileNameWithoutExtension(p)!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>从官方清单安装原版（版本 JSON → jar → 库 → 资源索引）</summary>
    public Task InstallVanillaAsync(
        string versionId, string versionUrl, IProgress<BatchProgress>? progress,
        CancellationToken ct = default) =>
        _installer.InstallVanillaAsync(versionId, versionUrl, _settings, progress, ct);

    /// <summary>删除已安装版本目录</summary>
    public void RemoveVersion(string versionId) =>
        InstalledVersionManager.Remove(_dotMc, versionId);

    /// <summary>内置 JRE（jre21 组件）是否已部署就绪（存在部署完成标记）</summary>
    public bool HasJavaRuntime()
    {
        var root = Path.Combine(_componentsRoot, "jre21");
        if (!Directory.Exists(root))
            return false;
        return Directory.GetDirectories(root)
            .Any(v => File.Exists(Path.Combine(v, DeploymentPlanner.OkMarker)));
    }

    /// <summary>完整启动链路（SelectedVersion 优先，回落已安装首个）</summary>
    public async Task LaunchAsync(CancellationToken ct = default)
    {
        var versions = FindVersions();
        if (versions.Count == 0)
            throw new InvalidOperationException("未发现已安装版本：请先在「版本」页下载安装");
        await LaunchAsync(SelectedVersion is not null && versions.Contains(SelectedVersion)
            ? SelectedVersion
            : versions[0], ct);
    }

    /// <summary>按指定版本完整启动：部署组件 → 初始化 Java 桥 → 构建参数 → 拉起 JVM</summary>
    public async Task LaunchAsync(string versionId, CancellationToken ct = default)
    {
        Emit("[flow] 启动链开始");
        // 崩溃取证：实际路径值直达 UI 日志区（真机 logcat 过滤不可靠）
        Emit($"[flow] filesDir={_context.FilesDir!.AbsolutePath} componentsRoot={_componentsRoot}");
        var manifest = await EnsureComponentsAsync(ct);
        Emit("[flow] 组件部署完成");

        // pojavexec 路径：glfw-stub 组件目录下的 libpojavexec.so（Java 桥静态块的加载目标）
        var glfwEntry = manifest.Require("glfw-stub");
        var pojavExec = Path.Combine(
            DeploymentPlanner.ComponentDir(_componentsRoot, glfwEntry.Name, glfwEntry.Version),
            "libpojavexec.so");
        if (!File.Exists(pojavExec))
            throw new InvalidOperationException($"组件包异常：{pojavExec} 不存在");

        // Java 桥须在 JVM 拉起前就绪（输入投递 / Surface 注册都依赖它）；Initialize 幂等。
        // libpojavexec 的 DT_NEEDED（fcl 系依赖，ELF 动态段取证）不在 classloader namespace
        // 搜索路径内，必须按依赖序经 System.load 先行注册（linker 递归解析 DT_NEEDED 按
        // soname 命中已加载表）：libc++_shared（androidnsbypass 依赖）→ bytehook（fcl 依赖）
        // → androidnsbypass → fcl；jli 是 jre libinstrument 的依赖，预加载无害（去重）
        JavaBridge.Traced -= OnBridgeTrace;
        JavaBridge.Traced += OnBridgeTrace;
        var glfwDir = DeploymentPlanner.ComponentDir(
            _componentsRoot, glfwEntry.Name, glfwEntry.Version);
        var dependencyLibs = new List<string>();
        foreach (var name in new[]
            { "libc++_shared.so", "libbytehook.so", "libandroidnsbypass.so", "libfcl.so" })
        {
            var lib = Path.Combine(glfwDir, name);
            if (File.Exists(lib))
                dependencyLibs.Add(lib);
        }
        if (Directory.Exists(_componentsRoot))
            dependencyLibs.AddRange(Directory.EnumerateFiles(_componentsRoot, "libjli.so",
                SearchOption.AllDirectories));
        JavaBridge.Initialize(pojavExec, _context, dependencyLibs);
        RegisterGrabCallbacksOnce();
        Emit("Java 桥已就绪");

        var version = await _installer.LoadVersionAsync(_dotMc, versionId, ct);
        Emit("[flow] 版本加载完成");

        // MC 26.x（LWJGL ≥3.4 = SDL 窗口/输入后端）：窗口面归 SdlGameActivity 的
        // SDLSurface；pre-26.x GLFW 链路保持原路径不动
        var lwjglEntry = manifest.Require(LaunchSpecBuilder.LwjglComponentNameOf(version));
        IsSdlMode = lwjglEntry.Name == "lwjgl341";

        // 真实账户：取账户库默认（离线）；一个没有时自动创建占位账户保证开箱即玩
        var account = Accounts.Default;
        if (account is null)
        {
            account = Accounts.AddOrUpdateOffline("Player");
            Accounts.SetDefault(account.Uuid);
            Emit("未发现账户：已创建离线账户 Player");
        }
        var isOffline = account.Type == AccountStore.OfflineType;
        var options = new LaunchOptions(
            VersionId: versionId,
            PlayerName: account.Username,
            GameDir: _dotMc,
            SharedDir: _dotMc,
            MinMemoryMb: 512,
            MaxMemoryMb: 2048,
            JavaExecutable: null,
            ExtraJvmArgs: [],
            UseBuiltinJvmArgs: true,
            // 离线走 legacy 占位令牌；在线类型的令牌持久化被禁止（仅内存会话），M4 阶段传占位
            Credentials: new LaunchCredentials(
                account.Uuid, "0", isOffline ? "legacy" : "mojang"));
        // TMPDIR 必须在应用私有内部存储：libpojavexec 会把补丁版 libvulkan.so 写进 TMPDIR 再 dlopen，
        // 外部存储（/storage）的 SELinux 上下文 media_rw_data_file 禁止 execute，dlopen 失败即 SIGSEGV
        var tmpDir = Path.Combine(_context.CacheDir!.AbsolutePath, "tmp");
        Directory.CreateDirectory(tmpDir);
        var rt = new LaunchRuntimeOptions(
            _componentsRoot,
            RenderChainPreference.Auto,
            NativeLibDir: _context.ApplicationInfo!.NativeLibraryDir,
            TmpDir: tmpDir);

        var spec = await new LaunchSpecBuilder(manifest, new GameLauncher(_installer))
            .BuildAsync(version, options, rt, ct);
        Emit($"参数构建完成：主类 {spec.MainClass}，渲染链 {spec.Backend}");

        // SDL 游戏活动在 spec 构建后拉起（loadLibraries 需要 ldPath / glDir）：
        // onCreate 完成 libSDL3 加载 + FCL 链（setLdLibraryPath → DLOPEN → initializeHooks），
        // sReady 就绪后 JVM 线程里 MC 的 SDL_Init 才能通过
        if (IsSdlMode)
        {
            var sdlLib = Path.Combine(
                DeploymentPlanner.ComponentDir(_componentsRoot, lwjglEntry.Name, lwjglEntry.Version),
                "natives", "arm64-v8a", "libSDL3.so");
            if (!File.Exists(sdlLib))
                throw new InvalidOperationException($"组件包异常：{sdlLib} 不存在");
            var glDir = spec.Backend == RenderBackend.Zink
                ? DeploymentPlanner.ComponentDir(_componentsRoot, "mesa-zink", manifest.Require("mesa-zink").Version)
                : null;
            // FCL 同款补系统目录：mesa 的 DT_NEEDED（libcutils 等）在 /system/lib64，
            // 尾部保留 nativeLibraryDir 与默认搜索路径兼容
            spec.Env.TryGetValue("LD_LIBRARY_PATH", out var ldPath);
            var fullLdPath = $"{ldPath}:/system/lib64:/vendor/lib64:/vendor/lib64/hw:/system_ext/lib64:{_context.ApplicationInfo!.NativeLibraryDir}";
            // FCL 原版顺序是 setEnv 全部 setenv 完才 initializeHooks（钩子安装时对环境
            // 变量做快照，如 dlsym 代理读 POJAVEXEC_EGL）；活动 onCreate 内装钩子，
            // 所以 setenv 必须先于活动启动，否则钩子只能看到空值（eglGetDisplay 报错根因）
            JvmHost.ApplyEnvironment(spec);
            await SdlGameLauncher.StartActivityAsync(sdlLib, fullLdPath, glDir, ct);
            Emit("SDL 游戏活动已就绪（libSDL3 已加载，FCL 钩子已安装）");
        }

        JvmHost.Start(spec);
        // 记录上次启动时间：首页版本卡「上次启动」列的数据源（对齐桌面端启动链行为）
        try { new InstanceSettingsStore(_dotMc).TouchPlayed(versionId); }
        catch { /* 记录失败不影响已拉起的游戏 */ }
        Emit("JVM 线程已拉起");
    }

    /// <summary>
    /// 内部 files 目录的规范路径（/data/data/&lt;pkg&gt;/files 形式）：
    /// linker namespace permitted_paths 按路径字符串匹配，/data/user 视图在 Release 下不命中；
    /// /data/user/&lt;uid&gt;/&lt;pkg&gt; 与 /data/data/&lt;pkg&gt; 是同一目录的两个视图，映射时必须去掉 uid 段
    /// </summary>
    private static string NormalizedFilesDir(global::Android.Content.Context context)
    {
        var path = context.FilesDir!.AbsolutePath!;
        const string prefix = "/data/user/";
        if (!path.StartsWith(prefix, StringComparison.Ordinal))
            return path;
        var rest = path[prefix.Length..];               // "<uid>/<pkg>/files"
        var uidEnd = rest.IndexOf('/');                  // uid 段与包名的分界
        return uidEnd > 0 ? "/data/data/" + rest[(uidEnd + 1)..] : path;
    }

    /// <summary>抓取状态回调只翻转 JavaBridge.IsGrabbing（输入枢纽据此分流相对/绝对坐标），注册一次</summary>
    private void RegisterGrabCallbacksOnce()
    {
        if (_grabCallbacksRegistered)
            return;
        _grabCallbacksRegistered = true;
        JavaBridge.SetGrabCallbacks(static () => { }, static () => { });
    }

    /// <summary>Java 桥 Trace 转发 UI 日志区（JavaBridge.Trace 已自行写 logcat，此处不重复）</summary>
    private void OnBridgeTrace(string line) => Log?.Invoke(line);

    private void Emit(string line)
    {
        // 直达 logcat（任意线程安全）：启动链时间线的权威来源，不依赖 UI 日志链
        global::Android.Util.Log.Info("StarcloudFlow", line);
        Log?.Invoke(line);
    }
}
