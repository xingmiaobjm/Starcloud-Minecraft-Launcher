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
            var missing = classpathEntries.Where(p => !File.Exists(p)).ToList();
            if (missing.Count > 0)
            {
                LauncherLog.Write("launch",
                    $"classpath 缺少 {missing.Count} 个文件（先补全版本）：" +
                    string.Join("、", missing.Select(Path.GetFileName)));
            }
            classpath = string.Join(PlatformInfo.ClasspathSeparator,
                classpathEntries.Where(File.Exists).Distinct(PlatformInfo.PathComparer));
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
        if (!jvmArgs.Any(a => a.Equals("-cp", StringComparison.OrdinalIgnoreCase) ||
                              a.Equals("-classpath", StringComparison.OrdinalIgnoreCase)))
        {
            jvmArgs.Add("-cp");
            jvmArgs.Add(classpath);
        }

        // 旧代参数字符串
        if (!string.IsNullOrEmpty(version.MinecraftArguments))
        {
            gameArgs.AddRange(version.MinecraftArguments
                .Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(Substitute));
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
        var required = version.JavaVersion?.MajorVersion ?? 8;
        var runtimes = await _java.ListRuntimesAsync(ct, Path.Combine(dotMc, "runtime"));
        var fit = runtimes
            .Where(r => r.MajorVersion >= required)
            .OrderByDescending(r => r.MajorVersion == required)
            .ThenBy(r => r.MajorVersion)
            .FirstOrDefault();
        if (fit is not null) return fit.Executable;
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
