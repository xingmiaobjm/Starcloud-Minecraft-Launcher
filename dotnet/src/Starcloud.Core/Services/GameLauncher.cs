// 游戏启动器：参数构建（新旧两代格式）+ 进程启动与退出监控

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

public sealed record LaunchOptions(
    string VersionId,
    string PlayerName,
    string GameDir,      // GameRoot：独立 = versions/<id>/，共享 = .minecraft（游戏工作目录）
    string SharedDir,    // 共享根 .minecraft：libraries/assets/natives/runtime 等永远共享
    int MinMemoryMb,
    int MaxMemoryMb,
    string? JavaExecutable,
    List<string> ExtraJvmArgs,
    bool UseBuiltinJvmArgs,
    LaunchCredentials? Credentials = null,
    string? QuickPlayMultiplayer = null,
    string Language = "zh_CN",
    string? LanguageOverride = null,
    int? WindowWidth = null,
    int? WindowHeight = null,
    bool? Fullscreen = null,
    IReadOnlyDictionary<string, string>? EnvVars = null,
    string? PreLaunchCommand = null,
    string? PostLaunchCommand = null,
    string? AgentJarPath = null,
    string? AgentBase = null,
    string? PlayerSkinUrl = null,
    Plugins.IPluginLaunchHook? PluginHook = null);

/// <summary>已认证账户凭据（微软 / 第三方账户）；合规要求启动必须携带真实凭证</summary>
public sealed record LaunchCredentials(string Uuid, string AccessToken, string UserType, string? Xuid = null);

/// <summary>参数构建结果：JVM 参数 / 游戏参数 / 主类（安卓进程内 JVM 启动直接消费，不拼命令行字符串）</summary>
public sealed record GameArguments(IReadOnlyList<string> JvmArgs, IReadOnlyList<string> GameArgs, string MainClass);

/// <summary>
/// 构建覆盖项：宿主环境与桌面默认推导不同时注入——
/// OsName 覆盖库规则过滤平台，NativesDir 覆盖 -Djava.library.path，Classpath 覆盖 ${classpath}。
/// 桌面端不传（null），行为与原有命令行构建完全一致。
/// </summary>
public sealed record BuildOverrides(string? OsName = null, string? NativesDir = null, string? Classpath = null);

public sealed class GameLauncher
{
    private readonly InstallerService _installer;
    private readonly JavaService? _java;

    /// <summary>
    /// classpath 缓存：键 = 实例目录 + 版本 id + 库集合指纹。
    /// <para>为什么值得缓存：classpath 要对上百个库文件逐个 <c>File.Exists</c>，
    /// 重度整合包能到两百多个 —— 每次启动游戏都 stat 一遍这些文件是纯浪费。
    /// 库文件很少在两次启动之间变动，所以按「集合指纹」缓存住结果即可。</para>
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> ClasspathCache = new();

    public GameLauncher(InstallerService installer, JavaService? java = null)
    {
        _installer = installer;
        _java = java;
    }

    /// <summary>构建完整启动命令行（java.exe + 参数）</summary>
    public async Task<string> BuildCommandLineAsync(MinecraftVersion version, LaunchOptions options,
        CancellationToken ct = default)
    {
        var (jvmArgs, gameArgs) = BuildArgsCore(version, options, null);
        var java = string.IsNullOrWhiteSpace(options.JavaExecutable)
            ? await ResolveJavaAsync(version, options.SharedDir, ct)
            : options.JavaExecutable!;
        var parts = new List<string> { QuoteIfNeeded(java) };
        parts.AddRange(jvmArgs.Select(QuoteIfNeeded));
        parts.Add(version.MainClass);
        parts.AddRange(gameArgs.Select(QuoteIfNeeded));
        return string.Join(' ', parts);
    }

    /// <summary>
    /// 构建参数集（不拼 java 前缀、不解析 Java 路径）：供安卓进程内 JVM 启动复用桌面同一套
    /// 变量替换与新旧代参数逻辑；overrides 覆盖平台差异，缺省时行为与命令行构建完全一致。
    /// </summary>
    public Task<GameArguments> BuildArgumentsAsync(MinecraftVersion version, LaunchOptions options,
        BuildOverrides? overrides = null, CancellationToken ct = default)
    {
        var (jvmArgs, gameArgs) = BuildArgsCore(version, options, overrides);
        return Task.FromResult(new GameArguments(jvmArgs, gameArgs, version.MainClass));
    }

    /// <summary>参数构建核心（同步）：桌面命令行与安卓进程内启动共用；overrides 为 null 时走桌面默认推导</summary>
    private (List<string> JvmArgs, List<string> GameArgs) BuildArgsCore(
        MinecraftVersion version, LaunchOptions options, BuildOverrides? ov)
    {
        var dotMc = options.SharedDir; // 共享根：libraries/assets/natives/classpath/jar 全走共享
        var versionId = version.Id;
        // 实例目录名才是「这次启动的实例」：jar 与 natives 都由安装器按实例目录名落盘。
        // JSON 里的 id 可能只是原版号（例如第二个 26.3 实例目录叫 26.3-2，JSON id 仍是 26.3），
        // 拿它拼路径会指向不存在的目录，jar 被丢掉后 Java 直接报 ClassNotFoundException。
        var instanceId = string.IsNullOrWhiteSpace(options.VersionId) ? versionId : options.VersionId;
        var versionDir = _installer.VersionDir(dotMc, instanceId);
        // 安卓侧由 LaunchSpecBuilder 以组件目录组装 natives 搜索路径（多个目录 ':' 分隔）
        var nativesDir = ov?.NativesDir ?? Path.Combine(versionDir, "natives");

        string classpath;
        if (string.IsNullOrEmpty(ov?.Classpath))
        {
            // 类路径：windows 内的库（规则过滤；NeoForge 版本 JSON 有重复库条目，需保序去重）
            // 库条目可能是 Mojang 的 downloads.artifact，也可能是 Fabric/Quilt 的 Maven 坐标；
            // 统一解析后再拼 libraries 路径（缺 loader jar 会表现为“找不到主类”）。
            var classpathEntries = version.Libraries
                .Where(l => InstallerService.RulesAllow(l.Rules, ov?.OsName))
                .Select(MavenArtifacts.Resolve)
                .Where(a => a?.Path is { Length: > 0 })
                .Select(a => Path.Combine(dotMc, "libraries", a!.Path!.Replace('/', Path.DirectorySeparatorChar)))
                .ToList();
            var ownJar = ResolveClientJar(dotMc, instanceId, versionId)
                         ?? throw new InvalidOperationException(
                             $"实例 {instanceId} 缺少客户端 jar（{instanceId}.jar）：请先在版本页执行「补全」再启动");
            classpathEntries.Add(ownJar);

            // 指纹：库集合 + 实例 id + 版本 id。库换了 / 实例换了 → 指纹变 → 缓存失效。
            var fingerprint = $"{instanceId}|{versionId}|{classpathEntries.Count}|" +
                              string.Join('|', classpathEntries);
            if (ClasspathCache.TryGetValue(fingerprint, out var cached))
            {
                classpath = cached;
            }
            else
            {
                var missing = classpathEntries.Where(p => !File.Exists(p)).ToList();
                if (missing.Count > 0)
                {
                    LauncherLog.Write("launch",
                        $"classpath 缺少 {missing.Count} 个文件（先补全版本）：" +
                        string.Join("、", missing.Select(Path.GetFileName)));
                }
                classpath = string.Join(PlatformInfo.ClasspathSeparator,
                    classpathEntries.Where(File.Exists).Distinct(PlatformInfo.PathComparer));
                // 缓存只留最近的一批：版本多了以后指纹集合会无限增长
                if (ClasspathCache.Count > 32) ClasspathCache.Clear();
                ClasspathCache[fingerprint] = classpath;
            }
        }
        else
        {
            // 安卓侧已按组件目录组装好类路径（':' 分隔），直接注入变量表
            classpath = ov!.Classpath;
        }

        // 参数替换
        var assets = version.Assets ?? version.AssetIndex?.Id ?? "legacy";
        // 凭证必须由上游解析传入（在线账户走内存会话，离线账户为名字派生的占位身份）；
        // 这里不做任何兜底生成，缺失即是上游漏了解析步骤
        var cred = options.Credentials
            ?? throw new InvalidOperationException("未提供账户凭证：请先登录微软 / 第三方账户，或添加一个离线账户");
        var authUuid = cred.Uuid;
        var authToken = cred.AccessToken;
        var vars = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["auth_player_name"] = options.PlayerName,
            ["version_name"] = versionId,
            ["game_directory"] = options.GameDir,
            ["assets_root"] = Path.Combine(dotMc, "assets"),
            ["game_assets"] = Path.Combine(dotMc, "assets", "virtual", assets),
            ["assets_index_name"] = version.AssetIndex?.Id ?? assets,
            ["auth_uuid"] = authUuid,
            ["auth_access_token"] = authToken,
            ["auth_session"] = $"token:{authToken}:{authUuid}",
            ["auth_xuid"] = cred.Xuid ?? "0",
            ["clientid"] = "starcloud",
            ["user_type"] = cred.UserType,
            // 本机自定义皮肤：客户端只认 profile 的 textures 属性里的 http(s) URL，
            // 于是把本机回环皮肤服务的地址经 --userProperties 交过去。
            // 离线与正版账户都走这条（传不传由上游 LaunchAccountService 决定）；
            // 第三方账户的皮肤在皮肤站上，不注入
            ["user_properties"] = options.PlayerSkinUrl is { Length: > 0 } skinUrl
                ? OfflineSkins.BuildUserProperties(authUuid, options.PlayerName, skinUrl, versionId)
                : "{}",
            ["version_type"] = version.Type ?? "release",
            ["launcher_name"] = "Starcloud",
            ["launcher_version"] = "1.0.0",
            ["natives_directory"] = nativesDir,
            ["library_directory"] = Path.Combine(dotMc, "libraries"),
            ["classpath_separator"] = PlatformInfo.ClasspathSeparator,
            ["classpath"] = classpath,
            ["resolution_width"] = (options.WindowWidth ?? 1280).ToString(),
            ["resolution_height"] = (options.WindowHeight ?? 720).ToString(),
            ["quickPlayPath"] = "",
            ["quickPlaySingleplayer"] = "",
            ["quickPlayMultiplayer"] = options.QuickPlayMultiplayer ?? "",
            ["quickPlayRealms"] = "",
            ["quickPlayBroadBand"] = "",
        };

        string Substitute(string s) =>
            System.Text.RegularExpressions.Regex.Replace(
                s, @"\$\{([a-zA-Z0-9_]+)\}",
                m => vars.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);

        var jvmArgs = new List<string>
        {
            "-Xms" + options.MinMemoryMb + "M",
            "-Xmx" + options.MaxMemoryMb + "M",
            $"-Djava.library.path={nativesDir}",
            "-Dorg.lwjgl.opengl.Display.allowSoftwareOpenGL=true",
        };
        if (options.UseBuiltinJvmArgs)
        {
            // 内置优化参数（对应旧 use_builtin_jvm_args）
            jvmArgs.AddRange(
            [
                "-XX:+UseG1GC",
                "-XX:+UnlockExperimentalVMOptions",
                "-XX:G1NewSizePercent=20",
                "-XX:G1ReservePercent=20",
                "-XX:MaxGCPauseMillis=50",
                "-XX:G1HeapRegionSize=32M",
                "-Dsun.rmi.dgc.server.gcInterval=2147483646",
                "-Dfile.encoding=UTF-8",
            ]);
        }
        jvmArgs.AddRange(options.ExtraJvmArgs.Where(a => !string.IsNullOrWhiteSpace(a)));

        // 用户自定义的 JVM 参数与内置参数可能撞名（最常见的是 -Xmx）：
        // JVM 取最后一个生效，所以「用户在设置里写了 -Xmx4G」实际上会覆盖设置里的内存值 ——
        // 但界面上显示的还是设置里的数字，两边对不上。这里显式去重：
        // 同名参数只保留最后一次出现的（用户写的在前、内置的在后，那就把内置那条删掉）。
        jvmArgs = DedupeJvmArgs(jvmArgs, options.ExtraJvmArgs.Where(a => !string.IsNullOrWhiteSpace(a)));

        // authlib-injector：第三方（Yggdrasil）外置登录注入，必须出现在 -cp 之前
        if (!string.IsNullOrWhiteSpace(options.AgentJarPath) && !string.IsNullOrWhiteSpace(options.AgentBase))
            jvmArgs.Add($"-javaagent:{options.AgentJarPath}={options.AgentBase}");

        var gameArgs = new List<string>();

        // 新代参数（arguments.jvm / arguments.game）
        if (version.Arguments is { } args)
        {
            foreach (var item in args.Jvm.Where(i => RulesAllowValue(i, ov?.OsName)))
                AppendValue(jvmArgs, item, Substitute);
            foreach (var item in args.Game.Where(i => RulesAllowValue(i, ov?.OsName)))
                AppendValue(gameArgs, item, Substitute);
        }

        // 1.13 之前没有 arguments.jvm，classpath 必须由启动器自己补；
        // 个别手写/旧整合包 JSON 也可能漏掉 -cp。缺了它 JVM 直接报「找不到主类」。
        // 注意要认全三种写法（-cp / -classpath / --class-path）：JVM 三者等价，
        // 只认前两种时，用户参数里写了 --class-path 会被再补一条 -cp，两条 classpath 互相打架。
        //
        // Forge 1.13 之前用的是 legacyClassPath 字段（安装器写死的整条 classpath），
        // 不认它就会退化成启动器自己拼的 classpath —— 少了 Forge 的补丁 jar，
        // 表现为「Forge 装了却报找不到主类」。
        if (!HasClasspathArg(jvmArgs))
        {
            if (!string.IsNullOrWhiteSpace(version.LegacyClassPath))
            {
                jvmArgs.Add("-cp");
                jvmArgs.Add(Substitute(version.LegacyClassPath));
            }
            else
            {
                jvmArgs.Add("-cp");
                jvmArgs.Add(classpath);
            }
        }

        // 旧代参数字符串
        if (!string.IsNullOrEmpty(version.MinecraftArguments))
        {
            gameArgs.AddRange(version.MinecraftArguments
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Substitute));
        }

        // 1.16 起的官方日志参数：logging.client.argument 指定 log4j 配置文件。
        // 不接的话游戏日志走默认配置，日志不在官方位置、格式也不对（排查崩溃时很别扭）。
        if (version.Logging is { ValueKind: JsonValueKind.Object } log
            && log.TryGetProperty("client", out var client)
            && client.TryGetProperty("argument", out var argEl)
            && argEl.ValueKind == JsonValueKind.String
            && argEl.GetString() is { Length: > 0 } logArg)
        {
            var resolved = Substitute(logArg);
            if (!jvmArgs.Any(a => a.Contains("log4j2.configurationFile", StringComparison.OrdinalIgnoreCase)))
                jvmArgs.Add($"-Dlog4j2.configurationFile={resolved}");
        }

        // 现代版本没有 minecraftArguments 时补全必需 game 参数
        if (version.MinecraftArguments is null && version.Arguments is not null)
        {
            var has = (string prefix) => gameArgs.Any(a => a.Equals(prefix, StringComparison.OrdinalIgnoreCase));
            if (!has("--username"))
            {
                gameArgs.Add("--username"); gameArgs.Add(options.PlayerName);
                gameArgs.Add("--version"); gameArgs.Add(versionId);
                gameArgs.Add("--gameDir"); gameArgs.Add(options.GameDir);
                gameArgs.Add("--assetsDir"); gameArgs.Add(Path.Combine(dotMc, "assets"));
                gameArgs.Add("--assetIndex"); gameArgs.Add(version.AssetIndex?.Id ?? assets);
                gameArgs.Add("--uuid"); gameArgs.Add(authUuid);
                gameArgs.Add("--accessToken"); gameArgs.Add(authToken);
                gameArgs.Add("--userType"); gameArgs.Add(cred.UserType);
                gameArgs.Add("--versionType"); gameArgs.Add(version.Type ?? "release");
            }
        }

        // 窗口尺寸/全屏（≥1.13 支持 --width/--height/--fullscreen；旧版注入无效参数可能崩溃）
        if (ModernWindowArgs(versionId))
        {
            if (options.WindowWidth is int w && options.WindowHeight is int h)
            {
                gameArgs.Add("--width"); gameArgs.Add(w.ToString());
                gameArgs.Add("--height"); gameArgs.Add(h.ToString());
            }
            if (options.Fullscreen == true)
                gameArgs.Add("--fullscreen");
        }

        // 插件 beforeGameLaunch 钩子（规范 plugin-spec.md）：仅透传插件增量参数，
        // 身份相关参数已在 GameArgsManager 入口拦截；本体核心参数不在增量内，插件不可触碰
        if (options.PluginHook is { } pluginHook)
        {
            var (pluginJvm, pluginGame) = pluginHook.CollectExtraArgs(versionId, options.PlayerName);
            jvmArgs.AddRange(pluginJvm);
            gameArgs.AddRange(pluginGame);
        }

        return (jvmArgs, gameArgs);
    }

    /// <summary>
    /// 定位实例的客户端 jar。查找顺序（同级目录只认一个，避免把 sources/javadoc 当客户端）：
    /// 1) <c>versions/&lt;实例&gt;/&lt;实例&gt;.jar</c> —— 安装器落盘位置（改名/多实例并存的常态）
    /// 2) 实例目录里唯一的客户端 jar —— 手工改过目录名的存量实例
    /// 3) <c>versions/&lt;JSON id&gt;/&lt;JSON id&gt;.jar</c> —— 加载器实例继承原版时兜底
    /// </summary>
    public static string? ResolveClientJar(string dotMinecraft, string instanceId, string? jsonId)
    {
        var exact = Path.Combine(dotMinecraft, "versions", instanceId, instanceId + ".jar");
        if (File.Exists(exact)) return exact;

        var dir = Path.Combine(dotMinecraft, "versions", instanceId);
        if (Directory.Exists(dir))
        {
            var jars = Directory.EnumerateFiles(dir, "*.jar", SearchOption.TopDirectoryOnly)
                .Where(j => !IsSideArtifact(j))
                .OrderByDescending(f => new FileInfo(f).Length)
                .ToList();
            if (jars.Count > 0) return jars[0];
        }

        if (!string.IsNullOrWhiteSpace(jsonId) &&
            !string.Equals(jsonId, instanceId, StringComparison.OrdinalIgnoreCase))
        {
            var byJsonId = Path.Combine(dotMinecraft, "versions", jsonId, jsonId + ".jar");
            if (File.Exists(byJsonId)) return byJsonId;
        }
        return null;
    }

    /// <summary>sources / javadoc / dev 之类不是客户端主 jar</summary>
    private static bool IsSideArtifact(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.EndsWith("-sources", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-javadoc", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith("-dev", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>设置未指定 Java 时：按版本要求的 Java 主版本选运行时（精确匹配优先，其次最小超集），缺则自动下载</summary>
    private async Task<string> ResolveJavaAsync(MinecraftVersion version, string dotMc, CancellationToken ct)
    {
        if (_java is null)
            throw new InvalidOperationException(
                $"未找到可用的 Java。请在设置页指定 {PlatformInfo.JavaExeName} 路径");
        var required = JavaRequirement.Resolve(version.Id, version.JavaVersion);
        var runtimes = await _java.ListRuntimesAsync(ct, Path.Combine(dotMc, "runtime"));
        var fit = runtimes
            .Where(r => r.MajorVersion >= required)
            .OrderByDescending(r => r.MajorVersion == required)
            .ThenBy(r => r.MajorVersion)
            .FirstOrDefault();
        if (fit is not null)
        {
            LauncherLog.Write("java",
                $"自动选择 Java：版本 {version.Id} 需要 >={required}，" +
                $"候选 [{string.Join(", ", runtimes.Select(r => r.MajorVersion).Distinct().OrderBy(m => m))}]，" +
                $"选中 {fit.MajorVersion}（{fit.VersionString}）");
            return fit.Executable;
        }
        LauncherLog.Write("java",
            $"自动选择 Java：版本 {version.Id} 需要 >={required}，" +
            $"候选 [{string.Join(", ", runtimes.Select(r => r.MajorVersion).Distinct().OrderBy(m => m))}]，" +
            "没有满足条件的运行时，开始下载");
        // 系统与启动器都没有合适版本：自动下载（与 Forge 安装器同款兜底）
        return await _java.DownloadRuntimeAsync(required, null, ct);
    }

    private static bool RulesAllowValue(ArgumentItem item, string? osName)
    {
        if (item.Rules is null || item.Rules.Count == 0) return true;
        return InstallerService.RulesAllow(item.Rules, osName);
    }

    private static void AppendValue(List<string> list, ArgumentItem item,
        Func<string, string> substitute)
    {
        switch (item.Value.ValueKind)
        {
            case JsonValueKind.String:
                var s = item.Value.GetString();
                if (!string.IsNullOrEmpty(s)) list.Add(substitute(s));
                break;
            case JsonValueKind.Array:
                foreach (var v in item.Value.EnumerateArray())
                {
                    if (v.ValueKind == JsonValueKind.String)
                    {
                        var sv = v.GetString();
                        if (!string.IsNullOrEmpty(sv)) list.Add(substitute(sv));
                    }
                }
                break;
        }
    }

    private static string QuoteIfNeeded(string arg) =>
        arg.Contains(' ') ? '"' + arg + '"' : arg;

    /// <summary>
    /// JVM 参数去重：同名参数只保留最后一次出现的。
    /// <para>为什么要去重：JVM 对 -Xmx / -Xms 这类参数取「最后一个」生效，
    /// 所以用户在设置里写了 <c>-Xmx4G</c> 时实际生效的是 4G，而界面显示的还是设置里的数字 ——
    /// 两边对不上，用户会以为内存设置没生效。同名参数留一条就够了。</para>
    /// <para>比较用「参数名」而不是整串：<c>-Xmx4G</c> 与 <c>-Xmx2G</c> 名字相同、
    /// 值不同，属于同一个参数；<c>-Dfoo=a</c> 与 <c>-Dfoo=b</c> 名字相同但语义不同，
    /// 同样只保留后一条（JVM 本身也是后者生效）。</para>
    /// </summary>
    internal static List<string> DedupeJvmArgs(List<string> args, IEnumerable<string> userArgs)
    {
        // 只有「用户参数与内置参数撞名」才需要处理：纯内置之间不重复（除了版本 JSON 自带的）
        var userNames = userArgs.Select(JvmArgName)
            .Where(n => n.Length > 0)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (userNames.Count == 0) return args;

        var result = new List<string>(args.Count);
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // 从后往前扫：先标记所有名字，最后保留每个名字的第一次出现（即原序列里的最后一次）
        for (var i = args.Count - 1; i >= 0; i--)
        {
            var name = JvmArgName(args[i]);
            if (name.Length > 0 && !seen.Add(name)) continue;   // 前面还有同名 → 丢掉这条
            result.Add(args[i]);
        }
        result.Reverse();
        return result;
    }

    /// <summary>
    /// 取 JVM 参数的名字部分：<c>-Xmx4G</c> → <c>-Xmx</c>；<c>-Dfoo=bar</c> → <c>-Dfoo</c>；
    /// <c>-XX:+UseG1GC</c> → <c>-XX:+UseG1GC</c>（这类整体是一个开关，不该被拆）。
    /// <para>难点在于值可以紧跟名字（<c>-Xmx4G</c>），没有分隔符。
    /// 办法是按 JVM 的实际参数形态逐条匹配：内存类（-Xms/-Xmx/-Xss…）取前缀，
    /// 系统属性类（-D…）取 '=' 之前，-XX 类整体保留。</para>
    /// </summary>
    private static string JvmArgName(string arg)
    {
        if (string.IsNullOrWhiteSpace(arg)) return "";
        var trimmed = arg.TrimStart();
        if (!trimmed.StartsWith('-')) return "";

        // -XX:... 是整体开关（-XX:+UseG1GC / -XX:MaxGCPauseMillis=50），不拆
        if (trimmed.StartsWith("-XX", StringComparison.OrdinalIgnoreCase)) return trimmed;

        // -Dfoo=bar：取 '=' 之前的部分
        if (trimmed.StartsWith("-D", StringComparison.OrdinalIgnoreCase))
        {
            var eq = trimmed.IndexOf('=');
            return eq < 0 ? trimmed : trimmed[..eq];
        }

        // -Xmx4G / -Xms1024M / -Xss1M：取「-X + 已知前缀」的部分
        if (trimmed.StartsWith("-X", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var prefix in XKnownPrefixes)
            {
                if (trimmed.Length > prefix.Length &&
                    trimmed.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    return prefix;
            }
            // 未知 -X 参数：退化成「-X + 前两个字母」
            return trimmed.Length > 3 ? trimmed[..3] : trimmed;
        }

        return trimmed;
    }

    /// <summary>JVM 的 -X 系列参数前缀（值紧跟其后，没有分隔符）。</summary>
    private static readonly string[] XKnownPrefixes =
    [
        "-Xms", "-Xmx", "-Xss", "-Xmn", "-Xmx", "-Xgc", "-Xmn", "-Xprof", "-Xss",
    ];

    /// <summary>命令行里是否已有 classpath 参数（JVM 认 -cp / -classpath / --class-path 三种写法）。</summary>
    internal static bool HasClasspathArg(List<string> args) => args.Any(a =>
        a.Equals("-cp", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("-classpath", StringComparison.OrdinalIgnoreCase) ||
        a.Equals("--class-path", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// 引号感知的命令行切分（与 <see cref="QuoteIfNeeded"/> 对称）。
    /// 只处理"整段参数被双引号包住"的情形——启动参数里没有嵌套引号。
    /// </summary>
    private static List<string> SplitCommandLine(string commandLine)
    {
        var result = new List<string>();
        var current = new System.Text.StringBuilder();
        var inQuotes = false;
        foreach (var c in commandLine)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }
            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }
            current.Append(c);
        }
        if (current.Length > 0) result.Add(current.ToString());
        return result;
    }

    /// <summary>版本是否支持现代窗口参数（--width/--height/--fullscreen）：≥1.13 或无法解析（快照/特殊命名视为现代）</summary>
    public static bool ModernWindowArgs(string versionId)
    {
        var m = System.Text.RegularExpressions.Regex.Match(versionId, @"^1\.(\d+)");
        return !m.Success || int.TryParse(m.Groups[1].Value, out var minor) && minor >= 13;
    }

    /// <summary>启动游戏进程；返回会话（stdout/stderr 已重定向并落盘，退出由 GameSession.Exited 收口）</summary>
    public GameSession Launch(string commandLine, string workingDirectory, string versionId,
        IReadOnlyDictionary<string, string>? envVars = null)
    {
        // 不能按空格切命令行：Java 路径几乎总在 "Program Files" 这类含空格目录下。
        // 先做引号感知切分，再交给 ArgumentList 逐项转义。
        var parts = SplitCommandLine(commandLine);
        if (parts.Count == 0) throw new InvalidOperationException("启动命令行为空");
        var psi = new ProcessStartInfo
        {
            FileName = parts[0],
            UseShellExecute = false,
            WorkingDirectory = workingDirectory,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        for (var i = 1; i < parts.Count; i++) psi.ArgumentList.Add(parts[i]);
        if (envVars is not null)
        {
            foreach (var (k, v) in envVars) psi.Environment[k] = v;
        }
        var p = Process.Start(psi) ?? throw new InvalidOperationException("游戏进程启动失败");
        // 退出统一由 GameSession 的 WaitForExitAsync 收口，此处不再 EnableRaisingEvents
        // 文件名带 pid：同一秒多开时不会互相覆盖（多实例同时运行）
        var logFile = Path.Combine(LauncherPaths.GameLogsDir, $"game-{DateTime.Now:yyyyMMdd-HHmmss}-{p.Id}.log");
        var session = new GameSession(p, versionId, logFile, commandLine);
        LauncherLog.Write("game", $"启动 {versionId}（pid {session.Pid}），日志 {logFile}");
        return session;
    }

    /// <summary>便捷：加载版本并启动（供 UI 一次调用）</summary>
    public async Task<GameSession> LaunchVersionAsync(string dotMinecraft, string versionId,
        LaunchOptions options, CancellationToken ct = default)
    {
        var version = await _installer.LoadVersionAsync(dotMinecraft, versionId, ct);
        var cmd = await BuildCommandLineAsync(version, options, ct);
        Directory.CreateDirectory(options.GameDir);
        var lang = options.LanguageOverride ?? options.Language;
        EnsureFirstRunLanguage(options.GameDir, lang);
        if (options.LanguageOverride is not null)
            PatchLanguage(options.GameDir, options.LanguageOverride);
        return Launch(cmd, options.GameDir, versionId, options.EnvVars);
    }

    /// <summary>首次开启（无 options.txt）时按启动器语言写入游戏语言；失败静默不阻塞启动</summary>
    private static void EnsureFirstRunLanguage(string gameDir, string language)
    {
        var path = Path.Combine(gameDir, "options.txt");
        if (File.Exists(path)) return;
        try { File.WriteAllText(path, $"lang:{language.Replace('-', '_')}\n"); }
        catch { /* 非阻塞：写不进去就交给游戏默认语言 */ }
    }

    /// <summary>语言覆盖：改写 GameRoot 下 options.txt 的 lang: 行（不存在则创建；失败静默）</summary>
    private static void PatchLanguage(string gameDir, string language)
    {
        var path = Path.Combine(gameDir, "options.txt");
        try
        {
            var line = $"lang:{language.Replace('-', '_')}";
            if (!File.Exists(path))
            {
                File.WriteAllText(path, line + "\n");
                return;
            }
            var lines = File.ReadAllLines(path).ToList();
            var idx = lines.FindIndex(l => l.StartsWith("lang:", StringComparison.OrdinalIgnoreCase));
            if (idx >= 0) lines[idx] = line; else lines.Add(line);
            File.WriteAllLines(path, lines);
        }
        catch { /* 非阻塞：语言覆盖失败交给游戏默认语言 */ }
    }

    /// <summary>执行启动前命令（$GAME_DIR 替换为 GameRoot）；未配置返回 (0, "")；整段捕获输出</summary>
    public Task<(int ExitCode, string Output)> RunPreLaunchAsync(LaunchOptions options)
    {
        var command = options.PreLaunchCommand?.Trim();
        if (string.IsNullOrEmpty(command)) return Task.FromResult((0, ""));
        var psi = new ProcessStartInfo
        {
            FileName = PlatformInfo.ShellFileName,
            Arguments = PlatformInfo.ShellExecFlag + " " + ExpandGameDir(command, options.GameDir),
            UseShellExecute = false,
            WorkingDirectory = options.GameDir,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (options.EnvVars is not null)
        {
            foreach (var (k, v) in options.EnvVars) psi.Environment[k] = v;
        }
        return RunCaptureAsync(psi);
    }

    private static async Task<(int ExitCode, string Output)> RunCaptureAsync(ProcessStartInfo psi)
    {
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("命令进程启动失败");
        var outTask = p.StandardOutput.ReadToEndAsync();
        var errTask = p.StandardError.ReadToEndAsync();
        await Task.WhenAll(outTask, errTask);
        await p.WaitForExitAsync(CancellationToken.None);
        return (p.ExitCode, (outTask.Result + errTask.Result).Trim());
    }

    /// <summary>执行启动后命令（游戏退出后由调用方触发）；任何异常只追加日志不抛出</summary>
    public void RunPostLaunch(LaunchOptions options)
    {
        var command = options.PostLaunchCommand?.Trim();
        if (string.IsNullOrEmpty(command)) return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = PlatformInfo.ShellFileName,
                Arguments = PlatformInfo.ShellExecFlag + " " + ExpandGameDir(command, options.GameDir),
                UseShellExecute = false,
                WorkingDirectory = options.GameDir,
                CreateNoWindow = true,
            };
            if (options.EnvVars is not null)
            {
                foreach (var (k, v) in options.EnvVars) psi.Environment[k] = v;
            }
            using var p = Process.Start(psi);
            p?.WaitForExit();
        }
        catch (Exception ex)
        {
            AppendPostLaunchLog($"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {options.VersionId}: {ex}\n");
        }
    }

    /// <summary>$GAME_DIR 占位替换（大小写不敏感）</summary>
    private static string ExpandGameDir(string command, string gameDir) =>
        command.Replace("$GAME_DIR", gameDir, StringComparison.OrdinalIgnoreCase);

    private static void AppendPostLaunchLog(string text)
    {
        try
        {
            Directory.CreateDirectory(LauncherPaths.LogsDir);
            File.AppendAllText(Path.Combine(LauncherPaths.LogsDir, "game-postlaunch.log"), text);
        }
        catch { /* 日志写入失败忽略 */ }
    }
}
