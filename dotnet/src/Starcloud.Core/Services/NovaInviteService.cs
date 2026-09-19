// Nova 拉人即玩（NOVA_DESIGN.md 第 9 节「拉人即玩 · 陶瓦联机」）：
//   发起方：扫描实例 mods → Modrinth 按 SHA1 反查 → 打包自包含邀请令牌 → starcloud://join?t=… 链接
//   受邀方：解析链接 → Nova 确认清单（Java / 游戏版本 / 模组 / 自动进服）→ 点头后一键装好直接进服
//
// 诚实边界（不臆造未上线的依赖）：
//   • 邀请令牌自包含（清单内嵌 base64url），不依赖云端 manifest 解析服务（该云端服务未部署）
//   • 非 Modrinth 来源的模组（本地 jar / CurseForge）无法同步，清单里明确标注「好友需手动获取」
//   • Fabric / 原版端到端自动安装；Forge / NeoForge 自动安装未接入，清单中引导先装原版再手动补加载器
//   • 自动进服用 Minecraft 1.20+ 的 --quickPlayMultiplayer；旧版本明确提示手动添加服务器地址

using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Starcloud.Core.Models;

namespace Starcloud.Core.Services;

/// <summary>邀请令牌里同步的一个模组文件（Modrinth 直链 + SHA1 校验）</summary>
public sealed record InviteModEntry(
    [property: JsonPropertyName("url")] string Url,
    [property: JsonPropertyName("filename")] string Filename,
    [property: JsonPropertyName("sha1")] string Sha1,
    [property: JsonPropertyName("size")] long Size);

/// <summary>自包含邀请令牌（base64url(JSON) 后拼进 starcloud://join?t=…）</summary>
public sealed record NovaInviteToken(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("mc")] string Mc,
    [property: JsonPropertyName("loader")] string Loader,            // vanilla/fabric/forge/neoforge
    [property: JsonPropertyName("loader_version")] string? LoaderVersion,
    [property: JsonPropertyName("version_id")] string VersionId,
    [property: JsonPropertyName("mods_scope")] string ModsScope,     // version/global
    [property: JsonPropertyName("mods")] List<InviteModEntry> Mods,
    [property: JsonPropertyName("server")] string Server,            // 陶瓦联机组网后的 host:port，可为空
    [property: JsonPropertyName("code")] string Code);               // 陶瓦联机连接码 U/XXXX-XXXX-XXXX-XXXX

/// <summary>发起方生成的邀请草稿（供 UI 展示与复制链接）</summary>
public sealed record NovaInviteDraft(
    NovaInviteToken Token,
    string Url,
    string Mc,
    string Loader,
    int SyncedMods,
    IReadOnlyList<string> UnsyncedFiles,
    long TotalBytes)
{
    public string SizeText => TotalBytes <= 0 ? "0 MB" : $"{TotalBytes / 1024.0 / 1024:F1} MB";
}

/// <summary>一条邀请记录（nova.invites.json；kind: sent=我发起 / joined=我加入）</summary>
public sealed record NovaInviteEntry(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("time")] DateTime Time,
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("version_id")] string VersionId,
    [property: JsonPropertyName("mc")] string Mc,
    [property: JsonPropertyName("loader")] string Loader,
    [property: JsonPropertyName("mod_count")] int ModCount,
    [property: JsonPropertyName("server")] string Server,
    [property: JsonPropertyName("code")] string Code)
{
    public string TimeText => Time.ToString("MM-dd HH:mm");
}

public sealed class NovaInviteService
{
    public const string JoinScheme = "starcloud://join?t=";

    private readonly InstallerService _installer;
    private readonly ManifestService _manifest;
    private readonly JavaService _java;
    private readonly GameLauncher _launcher;
    private readonly DownloadService _downloader;
    private readonly Func<string?>? _agentJarPath; // authlib-injector jar 路径由 App 层注入（Core 不感知打包布局）
    private readonly Func<Plugins.IPluginLaunchHook?>? _pluginHook; // 插件启动前钩子（App 层懒注入，避免构造顺序耦合）

    private static readonly JsonSerializerOptions CompactJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonSerializerOptions StoreJson = new() { WriteIndented = true };

    private List<NovaInviteEntry> _history = [];

    public NovaInviteService(
        InstallerService installer, ManifestService manifest,
        JavaService java, GameLauncher launcher, DownloadService downloader,
        Func<string?>? agentJarPath = null,
        Func<Plugins.IPluginLaunchHook?>? pluginHook = null)
    {
        _installer = installer;
        _manifest = manifest;
        _java = java;
        _launcher = launcher;
        _downloader = downloader;
        _agentJarPath = agentJarPath;
        _pluginHook = pluginHook;
        Load();
    }

    /// <summary>邀请历史（我发起 + 我加入）</summary>
    public IReadOnlyList<NovaInviteEntry> History =>
        _history.OrderByDescending(e => e.Time).ToList();

    // ── 发起方：扫描实例并打包邀请链接 ──
    public async Task<NovaInviteDraft> CreateInviteAsync(
        string versionId, string connectionCode, string serverAddress,
        LauncherSettings settings, CancellationToken ct = default)
    {
        var dotMc = settings.DotMinecraft;
        if (!_installer.IsInstalled(dotMc, versionId))
            throw new InvalidOperationException($"实例 {versionId} 未安装，无法发起邀请");

        var merged = await _installer.LoadVersionAsync(dotMc, versionId, ct);
        var mc = ResolveMc(versionId, merged);
        var loader = ResolveLoader(versionId, out var loaderVersion);

        // mods 目录跟随现有启动行为（gameDir = .minecraft 根）
        var modsDir = Path.Combine(dotMc, "mods");
        var synced = new List<InviteModEntry>();
        var unsynced = new List<string>();
        long total = 0;
        if (Directory.Exists(modsDir))
        {
            foreach (var jar in Directory.EnumerateFiles(modsDir, "*.jar", SearchOption.TopDirectoryOnly)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                ct.ThrowIfCancellationRequested();
                var info = new FileInfo(jar);
                if (info.Length == 0) continue;
                var sha1 = Convert.ToHexString(
                    System.Security.Cryptography.SHA1.HashData(await File.ReadAllBytesAsync(jar, ct)))
                    .ToLowerInvariant();
                var entry = await LookupModrinthBySha1Async(sha1, ct);
                if (entry is not null)
                {
                    synced.Add(entry);
                    total += entry.Size;
                }
                else
                {
                    unsynced.Add(info.Name);
                }
            }
        }

        var token = new NovaInviteToken(1, versionId, mc, loader, loaderVersion, versionId,
            "global", synced, serverAddress?.Trim() ?? "", connectionCode?.Trim() ?? "");
        AppendHistory(new NovaInviteEntry(Guid.NewGuid().ToString("N")[..12], DateTime.Now, "sent",
            versionId, versionId, mc, loader, synced.Count, token.Server, token.Code));

        return new NovaInviteDraft(token, JoinScheme + EncodeToken(token), mc, loader,
            synced.Count, unsynced, total);
    }

    // ── 受邀方：解析 starcloud://join?t=… ──
    public static bool TryParseJoinUrl(string url, out NovaInviteToken? token)
    {
        token = null;
        if (string.IsNullOrWhiteSpace(url)) return false;
        var s = url.Trim();
        if (!s.StartsWith("starcloud://join", StringComparison.OrdinalIgnoreCase)) return false;

        var q = s[(s.IndexOf('?') + 1)..];
        foreach (var kv in q.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var i = kv.IndexOf('=');
            if (i <= 0 || kv[..i] != "t") continue;
            var b64 = kv[(i + 1)..].Replace('-', '+').Replace('_', '/');
            switch (b64.Length % 4)
            {
                case 2: b64 += "=="; break;
                case 3: b64 += "="; break;
                case 1: return false;
            }
            try
            {
                token = JsonSerializer.Deserialize<NovaInviteToken>(
                    Convert.FromBase64String(b64), CompactJson);
                return token is { V: 1 };
            }
            catch
            {
                return false;
            }
        }
        return false;
    }

    /// <summary>把令牌变成「拉人即玩」确认清单（点头才动手，动手写流水）</summary>
    public async Task<NovaPlan> BuildJoinPlanAsync(
        NovaInviteToken token, LauncherSettings settings, CancellationToken ct = default)
    {
        var dotMc = settings.DotMinecraft;
        var needsFabric = token.Loader == "fabric" && !string.IsNullOrEmpty(token.LoaderVersion);
        var manualLoader = token.Loader is "forge" or "neoforge";
        var targetId = needsFabric
            ? $"fabric-loader-{token.LoaderVersion}-{token.Mc}"
            : token.VersionId;
        var major = JavaMajorFor(token.Mc);

        var runtimes = await _java.ListRuntimesAsync(ct);
        var hasJava = runtimes.Any(r => r.MajorVersion >= major);

        var items = new List<NovaActionItem>();
        if (!hasJava)
            items.Add(new NovaActionItem($"安装 Java {major}",
                "启动器自动下载 Adoptium JDK，只需一次，其他版本也能共用"));
        if (manualLoader)
        {
            items.Add(new NovaActionItem($"安装原版 {token.Mc}（{token.Loader} 需手动装）",
                $"{token.Loader} 自动安装尚未接入：我先装好原版与模组，再到 {token.Loader} 官网安装加载器。"));
        }
        else if (!_installer.IsInstalled(dotMc, targetId))
        {
            items.Add(new NovaActionItem($"安装游戏版本 {targetId}",
                needsFabric
                    ? $"原版 {token.Mc} + Fabric {token.LoaderVersion}，客户端与依赖自动下载"
                    : "客户端与依赖自动下载"));
        }
        if (token.Mods.Count > 0)
        {
            var mb = token.Mods.Sum(m => Math.Max(m.Size, 0)) / 1024.0 / 1024;
            items.Add(new NovaActionItem($"下载 {token.Mods.Count} 个模组（共 {mb:F1} MB）",
                "来自 Modrinth 直链，SHA1 逐一校验后落位"));
        }
        var quickPlay = SupportsQuickPlay(token.Mc) && !string.IsNullOrWhiteSpace(token.Server);
        items.Add(new NovaActionItem(
            quickPlay ? $"启动后自动进入 {token.Server}"
                : string.IsNullOrWhiteSpace(token.Server) ? "装好后自行启动"
                : $"启动后手动添加服务器 {token.Server}",
            quickPlay
                ? $"陶瓦联机连接码 {token.Code}，游戏启动后直接进联机世界"
                : "该版本不支持自动进服参数，需手动添加服务器地址"));

        return new NovaPlan(Guid.NewGuid().ToString("N")[..12],
            $"拉人即玩：一键装好「{token.Name}」",
            items,
            tk => ExecuteJoinAsync(token, targetId, major, settings, tk));
    }

    private async Task<string> ExecuteJoinAsync(
        NovaInviteToken token, string targetId, int javaMajor,
        LauncherSettings settings, CancellationToken ct)
    {
        var dotMc = settings.DotMinecraft;
        var sb = new StringBuilder("已按清单装好：");

        // 1. Java
        var runtimes = await _java.ListRuntimesAsync(ct);
        var javaExe = runtimes.Where(r => r.MajorVersion >= javaMajor)
            .OrderBy(r => r.MajorVersion).FirstOrDefault()?.Executable;
        if (javaExe is null)
        {
            javaExe = await _java.DownloadRuntimeAsync(javaMajor, null, ct);
            sb.Append($"\n• Java {javaMajor} 已装好");
        }

        // 2. 游戏版本（Fabric = 先原版再 loader profile）
        var needsFabric = token.Loader == "fabric" && !string.IsNullOrEmpty(token.LoaderVersion);
        if (!_installer.IsInstalled(dotMc, targetId))
        {
            var manifest = await _manifest.GetManifestAsync(false, ct);
            var mv = manifest.Versions.FirstOrDefault(v => v.Id == token.Mc)
                     ?? throw new InvalidOperationException($"官方清单里没有版本 {token.Mc}");
            await _installer.InstallVanillaAsync(token.Mc, mv.Url, settings, null, ct);
            if (needsFabric)
            {
                await _installer.InstallFabricAsync(token.Mc, token.LoaderVersion!, settings, null, ct);
                if (!_installer.IsInstalled(dotMc, targetId))
                    throw new InvalidOperationException($"Fabric 装完未找到 {targetId}");
            }
            sb.Append($"\n• {targetId} 已装好");
        }

        // 3. 模组（SHA1 校验下载）
        if (token.Mods.Count > 0)
        {
            var modsDir = token.ModsScope == "version"
                ? Path.Combine(_installer.VersionDir(dotMc, targetId), "mods")
                : Path.Combine(dotMc, "mods");
            Directory.CreateDirectory(modsDir);
            var batch = token.Mods
                .Select(m => (m.Url, Path.Combine(modsDir, m.Filename), (string?)m.Sha1, m.Size))
                .ToList();
            await _downloader.DownloadBatchAsync(batch, "好友的模组",
                settings.DownloadConcurrency, settings.DownloadMaxRetries, null, ct);
            sb.Append($"\n• {token.Mods.Count} 个模组已就位（SHA1 全部通过）");
        }

        AppendHistory(new NovaInviteEntry(Guid.NewGuid().ToString("N")[..12], DateTime.Now, "joined",
            token.Name, targetId, token.Mc, token.Loader, token.Mods.Count, token.Server, token.Code));

        // 4. 启动 + 自动进服（1.20+ --quickPlayMultiplayer）；合规：凭证只走内存会话
        var store = AccountStore.Load();
        var account = store.Default;
        var player = account?.Username ?? settings.LastUsername ?? "Player";
        var session = account is null ? null : store.GetSession(account.Uuid);
        if (account is null || string.IsNullOrEmpty(session?.AccessToken))
            throw new InvalidOperationException(
                "还没有可用的游戏凭证：请先到账户页登录（微软或第三方），本次应用会话内登录一次即可");

        var external = account.Type == "external";
        var cred = external
            ? new LaunchCredentials(account.Uuid, session!.AccessToken, "mojang")
            : new LaunchCredentials(account.Uuid, session!.AccessToken, "msa", session.Xuid);
        var quickPlay = SupportsQuickPlay(token.Mc) ? token.Server : null;
        var agentJar = external ? _agentJarPath?.Invoke() : null;
        if (external && string.IsNullOrWhiteSpace(agentJar))
            throw new InvalidOperationException("authlib-injector 组件缺失，无法启动第三方账户，请重新安装启动器");
        var options = new LaunchOptions(targetId, player, dotMc, dotMc,
            settings.DefaultMinMemoryMb, settings.DefaultMaxMemoryMb,
            javaExe, settings.ExtraJvmArgs, settings.UseBuiltinJvmArgs, cred, quickPlay,
            AgentJarPath: agentJar, AgentBase: external ? account.ServerBase : null,
            PluginHook: _pluginHook?.Invoke());
        await _launcher.LaunchVersionAsync(dotMc, targetId, options, ct);
        sb.Append($"\n• 游戏已启动（{player}）" +
                  (string.IsNullOrEmpty(quickPlay) ? "" : $"，正在进入 {token.Server}"));
        return sb.ToString();
    }

    // ── 令牌编解码 ──
    public static string EncodeToken(NovaInviteToken token) =>
        Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(token, CompactJson))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // ── 推断：加载器 / MC 版本 / Java 主版本 / 自动进服支持 ──
    private static string ResolveLoader(string versionId, out string? loaderVersion)
    {
        // Fabric 官方 profile id 固定格式：fabric-loader-<loader>-<mc>
        if (versionId.StartsWith("fabric-loader-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = versionId["fabric-loader-".Length..].Split('-');
            if (parts.Length >= 2)
            {
                loaderVersion = parts[0];
                return "fabric";
            }
        }
        loaderVersion = null;
        if (versionId.StartsWith("forge-", StringComparison.OrdinalIgnoreCase)) return "forge";
        if (versionId.StartsWith("neoforge", StringComparison.OrdinalIgnoreCase)) return "neoforge";
        return "vanilla";
    }

    private static string ResolveMc(string versionId, MinecraftVersion merged)
    {
        // forge-<mc>-<build> / neoforge-<mc>-<build>
        if (versionId.StartsWith("forge-", StringComparison.OrdinalIgnoreCase) ||
            versionId.StartsWith("neoforge-", StringComparison.OrdinalIgnoreCase))
        {
            var parts = versionId.Split('-');
            if (parts.Length >= 2) return parts[1];
        }
        // 原版 / Fabric：LoadVersionAsync 已合并继承链，id 即 MC 版本
        return merged.Id;
    }

    /// <summary>MC 版本 → Java 主版本（快照/未知按现代版本处理为 21）</summary>
    public static int JavaMajorFor(string mc)
    {
        if (!Version.TryParse(mc, out var v)) return 21;
        if (v >= new Version(1, 20, 5)) return 21;
        if (v >= new Version(1, 18)) return 17;
        if (v >= new Version(1, 17)) return 17;
        if (v >= new Version(1, 0)) return 8;
        return 21;
    }

    private static bool SupportsQuickPlay(string mc) =>
        Version.TryParse(mc, out var v) && v >= new Version(1, 20);

    private async Task<InviteModEntry?> LookupModrinthBySha1Async(string sha1, CancellationToken ct)
    {
        try
        {
            var json = await _downloader.GetStringAsync(
                $"{ModrinthService.ApiBase}/version_file/{sha1}", ct);
            var pf = JsonSerializer.Deserialize<ModrinthVersion>(json)?.PrimaryFile;
            return pf is { Url: not "", Filename: not "" }
                ? new InviteModEntry(pf.Url, pf.Filename, pf.Sha1 ?? sha1, pf.Size)
                : null;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return null; // 非 Modrinth 来源（本地 jar / CurseForge 等）
        }
    }

    // ── 历史（nova.invites.json）──
    private void AppendHistory(NovaInviteEntry entry)
    {
        _history.Add(entry);
        if (_history.Count > 50) _history.RemoveRange(0, _history.Count - 50);
        try
        {
            LauncherPaths.EnsureDataDir();
            File.WriteAllText(LauncherPaths.NovaInvitesFile,
                JsonSerializer.Serialize(_history, StoreJson));
        }
        catch { /* 邀请历史落盘失败不阻断 */ }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(LauncherPaths.NovaInvitesFile)) return;
            _history = JsonSerializer.Deserialize<List<NovaInviteEntry>>(
                File.ReadAllText(LauncherPaths.NovaInvitesFile), StoreJson) ?? [];
        }
        catch
        {
            _history = [];
        }
    }
}

/// <summary>令牌的扩展提示（可选：无法自动同步的文件列表，随令牌回传展示）</summary>
public static class NovaInviteTokenExtensions
{
    public static IReadOnlyList<string>? UnsyncedHint(this NovaInviteToken _) => null;
}
