// 按「联机 SDK 协议描述」驱动的通用提供方实现。
//
// 内置的陶瓦联机与第三方 SDK 清单共用这一份实现，差别只在
// <see cref="MultiplayerSdkProtocol"/> 里的常量（下载地址、启动参数、HTTP 路径、状态映射）。
//
// 一次会话的生命周期（以陶瓦联机为例，其它同形 SDK 一致）：
//
//   1. 下载官方平台包 → 解压到核心目录 → 可执行文件统一重命名 → 写版本文件；
//   2. 以 `--hmcl <端口文件>` 拉起核心：核心把本地 HTTP 端口写进该文件；
//      Windows 上这个进程会再委派一个无窗口子进程去监听，自己随即退出——
//      所以「会话是否在跑」以**端口文件 + /state 是否响应**为准，不能看进程句柄；
//   3. 之后全部通过本地 HTTP 驱动：查状态 / 创建房间 / 加入房间 / 回空闲 / 关进程 / 取日志。

using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace Starcloud.Core.Services.Multiplayer;

public sealed class ProtocolMultiplayerProvider : IMultiplayerProvider
{
    private readonly MultiplayerSdkProtocol _p;
    private readonly DownloadService _download;
    private readonly HttpClient _http = CreateLocalClient();
    /// <summary>会话拉起 / 状态轮询互斥：多条入口（页面刷新 + 轮询器）可能同时进来</summary>
    private readonly SemaphoreSlim _sessionGate = new(1, 1);

    private int _port;
    /// <summary>本次会话是不是本启动器拉起来的（决定停止时能不能关掉核心进程）</summary>
    private bool _ownSession;
    private bool _disposed;

    public ProtocolMultiplayerProvider(MultiplayerSdkProtocol protocol, DownloadService download)
    {
        _p = protocol;
        _download = download;
    }

    public string Id => _p.Id;

    public string DisplayName => _p.DisplayName;

    public string Vendor => _p.Vendor;

    public string? VendorUrl => _p.VendorUrl;

    public string? Description => _p.Description;

    public MultiplayerSdkProtocol Protocol => _p;

    /// <summary>
    /// 联机控制面专用 HttpClient：**必须绕开系统代理**。
    /// 很多玩家开着 Clash / mihomo 之类的系统代理（指向 <c>127.0.0.1:7890</c> 之类），
    /// .NET 的 HttpClient 默认连"访问本机回环地址"也会丢给代理，而代理对 localhost 目标
    /// 通常直接回 404 —— 现象就是「核心明明起来了，却一直查不到联机状态」。
    /// 本地控制面与官方节点列表都直连，这条路径不掺代理。
    /// </summary>
    private static HttpClient CreateLocalClient() =>
        new(new SocketsHttpHandler { UseProxy = false }) { Timeout = TimeSpan.FromSeconds(10) };

    private string CoreDir => LauncherPaths.MultiplayerCoreDir(_p.Id);

    private string RunDir => LauncherPaths.MultiplayerRunDir(_p.Id);

    private string PortFilePath => Path.Combine(RunDir, _p.PortFile);

    /// <summary>Windows 用包内声明的 terracotta.exe；其它平台同名二进制不带扩展名。</summary>
    private string EffectiveExecutableName =>
        OperatingSystem.IsWindows()
            ? _p.ExecutableName
            : Path.GetFileNameWithoutExtension(_p.ExecutableName);

    private string ExecutablePath => Path.Combine(CoreDir, EffectiveExecutableName);

    public bool Installed => InstalledVersion == _p.Version && File.Exists(ExecutablePath);

    public string? InstalledVersion
    {
        get
        {
            try
            {
                var f = Path.Combine(CoreDir, _p.VersionFile);
                return File.Exists(f) ? File.ReadAllText(f).Trim() : null;
            }
            catch
            {
                return null;
            }
        }
    }

    public bool SessionRunning => _port != 0 || TryReadPortFile() is not null;

    // ── 安装 ─────────────────────────────────────────────────────────────

    public async Task InstallCoreAsync(IProgress<MultiplayerProgress>? progress, CancellationToken ct)
    {
        if (Installed) return;
        // 老版本（Tauri）已经下过同一个核心就直接搬过来，省掉一次 8 MB 下载
        if (TryAdoptLegacyCore(progress))
        {
            progress?.Report(new MultiplayerProgress("安装完成", 0, null));
            return;
        }

        var workDir = LauncherPaths.MultiplayerDownloadDir;
        Directory.CreateDirectory(workDir);
        var fileName = $"{_p.Id}-{_p.Version}-{Platforms.PlatformTag}.pkg";
        var pkg = Path.Combine(workDir, fileName);

        var urls = _p.DownloadUrls.Select(u => _p.Expand(u)).ToList();
        if (urls.Count == 0) throw new MultiplayerSdkManifestException($"{DisplayName} 没配置下载地址");

        progress?.Report(new MultiplayerProgress("下载联机核心", 0, null));
        Exception? last = null;
        // 每个源的失败原因都留着：只报最后一个的话，排查时分不清是网络不通还是
        // 源返回了登录页/错误页（两种表现都只是"失败"两个字）
        var failures = new List<string>();
        foreach (var url in urls)
        {
            try
            {
                // 下载期间按文件大小轮询上报进度与瞬时速度（DownloadService 只负责把文件拿到手）
                using var reporter = new SizeReporter(pkg, "下载联机核心", progress);
                await _download.DownloadFileAsync(url, pkg, ct: ct);

                // 内容校验：Gitee/GitHub 在限流或要求登录时会返回 200 的 HTML 页面，
                // 直接当核心包解压会抛一句看不懂的 gzip 错误。这里先看包魔数。
                EnsurePackageLooksValid(pkg);
                last = null;
                break;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                last = ex;
                failures.Add($"{ShortHost(url)}：{ex.Message}");
                LauncherLog.Write("multiplayer", $"{_p.Id} 下载失败（换下一个源）：{url} → {ex.Message}");
                TryDelete(pkg);
            }
        }
        if (last is not null)
            throw new InvalidOperationException(
                $"下载联机核心失败（{failures.Count} 个源都不可用）：{string.Join("；", failures)}", last);

        progress?.Report(new MultiplayerProgress("解压联机核心", 0, null));
        // 解压前清空旧核心：避免上一次安装残留的 DLL 干扰新核心。
        // 核心进程还在跑 / 被杀软锁定时删除会抛「访问被拒绝」——先把进程结束掉再删，
        // 仍失败就给可执行的指引（而不是丢一句 UnauthorizedAccessException）
        if (Directory.Exists(CoreDir))
        {
            KillCoreProcesses();
            try
            {
                Directory.Delete(CoreDir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                LauncherLog.Write("multiplayer", $"{_p.Id} 清理旧核心失败：{ex.Message}");
                throw new InvalidOperationException(
                    $"无法写入联机核心目录：{CoreDir}（{ex.Message}）。"
                    + "核心进程可能仍在运行，或被安全软件占用——请关掉正在进行的联机/结束 terracotta 进程后重试，"
                    + "或把该目录加入安全软件白名单。", ex);
            }
        }
        Directory.CreateDirectory(CoreDir);

        await Task.Run(() =>
        {
            Extract(pkg, CoreDir);
            NormalizeExecutable();
        }, ct);

        Directory.CreateDirectory(RunDir);
        await File.WriteAllTextAsync(Path.Combine(CoreDir, _p.VersionFile), _p.Version, ct);
        TryDelete(pkg);
        LauncherLog.Write("multiplayer", $"{_p.Id} 核心 {_p.Version} 安装完成 → {CoreDir}");
        progress?.Report(new MultiplayerProgress("安装完成", 0, null));
    }

    /// <summary>
    /// 复用旧版安装的核心目录（版本号一致 + 可执行文件在位才认）。
    /// 复制而不是移动：旧版启动器若还在用，不能把它脚下的文件抽走。
    /// </summary>
    private bool TryAdoptLegacyCore(IProgress<MultiplayerProgress>? progress)
    {
        if (string.IsNullOrWhiteSpace(_p.LegacyCoreDir)) return false;
        try
        {
            var legacy = Path.GetFullPath(_p.Expand(_p.LegacyCoreDir));
            if (!Directory.Exists(legacy)) return false;
            var versionFile = Path.Combine(legacy, _p.VersionFile);
            if (!File.Exists(Path.Combine(legacy, _p.ExecutableName)) || !File.Exists(versionFile)) return false;
            if (File.ReadAllText(versionFile).Trim() != _p.Version) return false;

            progress?.Report(new MultiplayerProgress("复用旧版联机核心", 0, null));
            Directory.CreateDirectory(CoreDir);
            foreach (var file in Directory.EnumerateFiles(legacy, "*", SearchOption.AllDirectories))
            {
                var dest = Path.Combine(CoreDir, Path.GetRelativePath(legacy, file));
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                File.Copy(file, dest, overwrite: true);
            }
            Directory.CreateDirectory(RunDir);
            LauncherLog.Write("multiplayer", $"{_p.Id} 复用旧版核心：{legacy} → {CoreDir}");
            return true;
        }
        catch (Exception ex)
        {
            // 迁移失败不影响正事：下面照常下载安装
            LauncherLog.Write("multiplayer", $"{_p.Id} 复用旧版核心失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 校验下载到的确实是核心包（按压缩格式看魔数）。
    /// 镜像站限流/要求登录时常见「HTTP 200 + HTML 登录页」，不拦截的话
    /// 用户看到的会是 "The archive entry was compressed using an unsupported
    /// compression method" 这种无从下手的错误。
    /// </summary>
    private void EnsurePackageLooksValid(string pkg)
    {
        var head = new byte[4];
        int read;
        using (var fs = File.OpenRead(pkg)) read = fs.Read(head, 0, head.Length);
        if (read < 2) throw new InvalidOperationException("下载到的文件是空的");

        bool ok = _p.ArchiveKind.ToLowerInvariant() switch
        {
            // gzip: 1F 8B
            "tar.gz" or "tgz" => head[0] == 0x1F && head[1] == 0x8B,
            // zip / jar: PK
            "zip" or "jar" => head[0] == 0x50 && head[1] == 0x4B,
            _ => true,   // 未知格式不做判断，交给解压阶段
        };
        if (!ok)
        {
            var looksHtml = read >= 1 && (head[0] == (byte)'<' || head[0] == (byte)'{');
            throw new InvalidOperationException(looksHtml
                ? "下载源返回了网页而不是核心包（可能需要登录或被限流）"
                : "下载到的文件不是有效的核心包（可能下载中断）");
        }
    }

    /// <summary>日志里的源标识：只留主机名，避免把整条长 URL 写进提示</summary>
    private static string ShortHost(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : url;

    /// <summary>按压缩包类型解压；逐条目校验路径，拒绝绝对路径与 <c>..</c> 穿越</summary>
    private void Extract(string pkg, string destDir)
    {
        switch (_p.ArchiveKind.ToLowerInvariant())
        {
            case "tar.gz" or "tgz":
                using (var file = File.OpenRead(pkg))
                using (var gz = new GZipStream(file, CompressionMode.Decompress))
                using (var reader = new TarReader(gz))
                {
                    while (reader.GetNextEntry() is { } entry)
                    {
                        var target = SafeJoin(destDir, entry.Name);
                        if (target is null) continue;
                        if (entry.EntryType is TarEntryType.Directory)
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, overwrite: true);
                    }
                }
                break;

            case "zip":
                using (var zip = ZipFile.OpenRead(pkg))
                {
                    foreach (var entry in zip.Entries)
                    {
                        var target = SafeJoin(destDir, entry.FullName);
                        if (target is null) continue;
                        if (entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\'))
                        {
                            Directory.CreateDirectory(target);
                            continue;
                        }
                        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                        entry.ExtractToFile(target, overwrite: true);
                    }
                }
                break;

            case "raw":
                File.Copy(pkg, Path.Combine(destDir, _p.ExecutableName), overwrite: true);
                break;

            default:
                throw new MultiplayerSdkManifestException(
                    $"{DisplayName} 的压缩包类型不支持：{_p.ArchiveKind}（可选 tar.gz / zip / raw）");
        }
    }

    /// <summary>条目路径 → 目标绝对路径；非法（绝对路径 / 穿越 / 空）返回 null 跳过</summary>
    private static string? SafeJoin(string destDir, string entryName)
    {
        var name = entryName.Replace('\\', '/').TrimStart('/');
        while (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
        if (name.Length == 0 || name.Contains(':') || name.Split('/').Any(s => s == ".."))
        {
            LauncherLog.Write("multiplayer", $"跳过不安全的压缩包条目：{entryName}");
            return null;
        }
        var full = Path.GetFullPath(Path.Combine(destDir, name));
        return full.StartsWith(Path.GetFullPath(destDir), StringComparison.OrdinalIgnoreCase) ? full : null;
    }

    /// <summary>
    /// 官方包内可执行文件带版本与平台后缀（如 <c>terracotta-0.4.2-windows-x86_64.exe</c>），
    /// 统一重命名成固定名——只改文件名、不动二进制内容，符合官方许可例外条款。
    /// </summary>
    private void NormalizeExecutable()
    {
        if (File.Exists(ExecutablePath))
        {
            EnsureExecutableBit(ExecutablePath);
            return;
        }
        var stem = Path.GetFileNameWithoutExtension(_p.ExecutableName);
        var ext = Path.GetExtension(_p.ExecutableName);
        var files = Directory.EnumerateFiles(CoreDir, "*", SearchOption.AllDirectories);
        var candidate = OperatingSystem.IsWindows()
            ? files.FirstOrDefault(f =>
                f.EndsWith(ext, StringComparison.OrdinalIgnoreCase) &&
                Path.GetFileName(f).StartsWith(stem, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(Path.GetFileName(f), _p.ExecutableName, StringComparison.OrdinalIgnoreCase))
            : files.FirstOrDefault(f =>
            {
                var name = Path.GetFileName(f);
                // Linux/macOS 包内是 terracotta-<ver>-<platform>-<arch>（无扩展名）
                return name.StartsWith(stem, StringComparison.OrdinalIgnoreCase) &&
                       !name.EndsWith(".so", StringComparison.OrdinalIgnoreCase) &&
                       !name.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                       !name.EndsWith(".dylib", StringComparison.OrdinalIgnoreCase) &&
                       !name.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
            });
        if (candidate is null)
            throw new InvalidOperationException($"联机核心包缺少可执行文件 {EffectiveExecutableName}");
        File.Move(candidate, ExecutablePath);
        EnsureExecutableBit(ExecutablePath);
    }

    /// <summary>Unix 下从 tar.gz 解出来的文件默认没有执行位，启动前补上。</summary>
    private static void EnsureExecutableBit(string path)
    {
        if (OperatingSystem.IsWindows()) return;
        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
            // 文件系统不支持（如挂载的 NTFS）时忽略，交给运行时自行判断
        }
    }

    // ── 会话 ─────────────────────────────────────────────────────────────

    public async Task<bool> EnsureSessionAsync(CancellationToken ct)
    {
        await _sessionGate.WaitAsync(ct);
        try
        {
            // 已经在跑（含上一次启动器留下的核心）：直接沿用
            if (_port != 0 && await PingAsync(_port, ct)) return true;
            if (TryReadPortFile() is { } port && await PingAsync(port, ct))
            {
                _port = port;
                return true;
            }
            if (!Installed) throw new MultiplayerCoreNotReadyException($"{DisplayName} 核心未安装");

            Directory.CreateDirectory(RunDir);
            TryDelete(PortFilePath);

            var args = _p.LaunchArguments
                .Select(a => a.Replace("{port_file}", PortFilePath))
                .ToArray();
            var psi = new ProcessStartInfo(ExecutablePath)
            {
                WorkingDirectory = CoreDir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            foreach (var a in args) psi.ArgumentList.Add(a);

            Process? child;
            try
            {
                child = Process.Start(psi);
            }
            catch (Exception ex)
            {
                // 「拒绝访问」几乎都是安全软件（Defender / 360 / 火绒）拦了 P2P 类程序，
                // 或 exe 被杀软隔离：光报 ex.Message 用户完全不知道该干什么。
                var denied = ex is UnauthorizedAccessException
                    || (ex is System.ComponentModel.Win32Exception w32 && w32.NativeErrorCode == 5)
                    || ex.Message.Contains("denied", StringComparison.OrdinalIgnoreCase)
                    || ex.Message.Contains("拒绝", StringComparison.Ordinal);
                LauncherLog.Write("multiplayer", $"{_p.Id} 启动核心失败：{ExecutablePath} → {ex}");
                throw new InvalidOperationException(denied
                    ? $"联机核心被阻止启动（访问被拒绝）：{ExecutablePath}。"
                      + "通常是安全软件（Windows  Defender / 360 / 火绒）拦截了 P2P 程序——"
                      + "请把该文件或整个目录加入白名单后重试；若文件已被隔离，请先「重装联机核心」。"
                    : $"启动联机核心失败：{ex.Message}（路径 {ExecutablePath}）", ex);
            }

            var deadline = DateTimeOffset.Now.AddSeconds(Math.Max(5, _p.StartTimeoutSeconds));
            while (DateTimeOffset.Now < deadline)
            {
                ct.ThrowIfCancellationRequested();
                if (TryReadPortFile() is { } p2 && await PingAsync(p2, ct))
                {
                    _port = p2;
                    _ownSession = true;
                    LauncherLog.Write("multiplayer", $"{_p.Id} 核心已就绪，本地端口 {p2}");
                    return true;
                }
                // 委派进程在没有端口文件的情况下就退了 → 不等满超时
                if (child is { HasExited: true } && child.ExitCode != 0)
                    throw new InvalidOperationException($"联机核心启动失败（退出码 {child.ExitCode}）");
                await Task.Delay(250, ct);
            }
            throw new TimeoutException($"等待联机核心启动超时（{_p.StartTimeoutSeconds} 秒）");
        }
        finally
        {
            _sessionGate.Release();
        }
    }

    /// <summary>探活：本地已有活着的会话就接管它的端口；不启动新进程</summary>
    public async Task<bool> ProbeSessionAsync(CancellationToken ct)
    {
        if (_port != 0 && await PingAsync(_port, ct)) return true;
        if (TryReadPortFile() is { } port && await PingAsync(port, ct))
        {
            // 上一次启动器留下的核心还活着：接管它的端口，之后就能直接驱动它
            _port = port;
            return true;
        }
        return false;
    }

    /// <summary>读端口文件（不存在 / 还没写完 → null）</summary>
    private int? TryReadPortFile()
    {
        try
        {
            if (!File.Exists(PortFilePath)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(PortFilePath));
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            return doc.RootElement.TryGetProperty(_p.PortJsonKey, out var v) && v.TryGetInt32(out var port)
                ? port
                : null;
        }
        catch
        {
            return null; // 写入过程中读到半截 JSON：下一轮再试
        }
    }

    private async Task<bool> PingAsync(int port, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync(Api(port, _p.StatePath), ct);
            return resp.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    public async Task StopSessionAsync(CancellationToken ct)
    {
        var port = _port != 0 ? _port : TryReadPortFile() ?? 0;
        if (port != 0)
        {
            await TryGetAsync(Api(port, _p.IdlePath), ct);
            // 只有本启动器拉起的核心才允许关进程：用户自己开的陶瓦联机不该被我们结束
            if (_ownSession && !string.IsNullOrEmpty(_p.ShutdownPath))
                await TryGetAsync(Api(port, _p.ShutdownPath), ct);
        }
        _port = 0;
        _ownSession = false;
        TryDelete(PortFilePath);
    }

    /// <summary>急停：优雅关闭 → 仍存活就强杀核心进程 → 清端口记录。杀不掉抛异常。</summary>
    public async Task EmergencyStopAsync(CancellationToken ct)
    {
        var port = _port != 0 ? _port : TryReadPortFile() ?? 0;
        // 1) 优雅关闭先试一次：核心自己收尾虚拟网卡最干净；挂死/无响应由下一步兜底
        if (port != 0)
        {
            try { await TryGetAsync(Api(port, _p.IdlePath), ct); } catch { /* 挂死就走强杀 */ }
            if (!string.IsNullOrEmpty(_p.ShutdownPath))
            {
                try { await TryGetAsync(Api(port, _p.ShutdownPath), ct); } catch { /* 同上 */ }
            }
        }

        // 2) 进程兜底：按核心可执行文件名找进程强杀（核心名唯一，无同名误伤风险；
        //    核心自提权后 Kill 可能拒绝访问 —— 那就报错让用户知道要手动处理）
        var killed = KillCoreProcesses();

        // 3) 状态清零 + 确认真的死了
        _port = 0;
        _ownSession = false;
        TryDelete(PortFilePath);
        if (port != 0 && await PingAsync(port, CancellationToken.None))
        {
            throw new InvalidOperationException(killed
                ? "联机核心仍在响应（可能以管理员权限运行），请手动结束它"
                : "联机核心进程无法结束（可能以管理员权限运行），请手动结束它");
        }
        LauncherLog.Write("multiplayer", $"{_p.Id} 急停完成（强杀 {killed} 个进程）");
    }

    /// <summary>
    /// 结束所有核心进程（按可执行文件名匹配），返回是否杀掉过。
    /// 重装核心前清目录、急停、异常态复位都靠它——核心进程活着时目录删不掉。
    /// </summary>
    private bool KillCoreProcesses()
    {
        var killed = false;
        var name = Path.GetFileNameWithoutExtension(ExecutablePath);
        foreach (var proc in Process.GetProcessesByName(name))
        {
            try
            {
                proc.Kill(entireProcessTree: true);
                killed = true;
            }
            catch (Exception ex)
            {
                // 以管理员权限运行时 Kill 会被拒绝：记日志，交给调用方给用户提示
                LauncherLog.Write("multiplayer", $"{_p.Id} 进程 {proc.Id} 结束失败（{ex.Message}）");
            }
            finally
            {
                try { proc.Dispose(); } catch { }
            }
        }
        if (killed) LauncherLog.Write("multiplayer", $"{_p.Id} 已结束残留核心进程");
        return killed;
    }

    public async Task IdleAsync(CancellationToken ct)
    {
        var port = await RequirePortAsync(ct);
        await TryGetAsync(Api(port, _p.IdlePath), ct);
    }

    public async Task HostAsync(string? player, IReadOnlyList<string> nodes, CancellationToken ct)
    {
        await ResetIfRoleBusyAsync(switchingToHost: true, ct);
        await SendAsync(_p.HostPath, player, room: null, nodes, ct);
    }

    public async Task JoinAsync(string room, string? player, IReadOnlyList<string> nodes, CancellationToken ct)
    {
        await ResetIfRoleBusyAsync(switchingToHost: false, ct);
        await SendAsync(_p.JoinPath, player, room, nodes, ct);
    }

    /// <summary>
    /// 切角色前复位：核心只在 waiting 接受 host/guesting 命令，已处于另一端角色或
    /// 异常态时直接发会被拒（实测 400 Bad Request，响应体是框架的 HTML 错误页）。
    /// 这里先回空闲再发；复位失败不阻断（让 SendAsync 去报真实错误）。
    /// </summary>
    private async Task ResetIfRoleBusyAsync(bool switchingToHost, CancellationToken ct)
    {
        try
        {
            var state = await FetchStateAsync(ct);
            var mustReset = state.Phase switch
            {
                MultiplayerPhase.Idle => false,
                MultiplayerPhase.HostScanning or MultiplayerPhase.HostStarting or MultiplayerPhase.HostReady
                    => !switchingToHost,      // 房主角色要切去当玩家：先复位
                MultiplayerPhase.GuestConnecting or MultiplayerPhase.GuestEntering or MultiplayerPhase.GuestReady
                    => switchingToHost,       // 玩家角色要切去开房：先复位
                _ => true,                    // Failed 等异常态：一律复位
            };
            if (!mustReset) return;
            LauncherLog.Write("multiplayer",
                $"{_p.Id} 核心当前 {state.Phase}，切角色前先回空闲");
            await IdleAsync(ct);
            await Task.Delay(300, ct);   // 给核心一点时间落状态
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            LauncherLog.Write("multiplayer", $"{_p.Id} 切角色前复位失败（继续尝试）：{ex.Message}");
        }
    }

    /// <summary>把「玩家名 / 房间号 / 公共节点」拼成查询参数后调用控制面</summary>
    private async Task SendAsync(string path, string? player, string? room, IReadOnlyList<string> nodes,
        CancellationToken ct)
    {
        var (status, body) = await SendOnceAsync(path, player, room, nodes, ct);
        if ((int)status == 400)
        {
            // 400 基本都是「核心当前状态不接受该角色切换」（profile 被上一次 host/guest 占用）：
            // 回空闲再来一次，成了就继续，别让用户看到一个框架的 HTML 错误页
            LauncherLog.Write("multiplayer",
                $"{_p.Id} 请求 {path} 返回 400，复位核心后重试一次");
            await IdleAsync(ct);
            await Task.Delay(400, ct);
            (status, body) = await SendOnceAsync(path, player, room, nodes, ct);
            if ((int)status is >= 200 and < 300)
            {
                LauncherLog.Write("multiplayer", $"{_p.Id} 复位后重试成功");
                return;
            }
        }
        if ((int)status is >= 200 and < 300) return;

        LauncherLog.Write("multiplayer",
            $"{_p.Id} 核心请求失败 {(int)status}：{path} → {(body.Length > 0 ? body : "(无响应体)")}");
        throw new InvalidOperationException(
            $"联机核心返回 HTTP {(int)status}" + (body.Length > 0 ? $"：{body}" : ""));
    }

    /// <summary>发一次控制面请求，返回状态码与响应体（不抛 HTTP 错误）</summary>
    private async Task<(System.Net.HttpStatusCode Status, string Body)> SendOnceAsync(
        string path, string? player, string? room, IReadOnlyList<string> nodes, CancellationToken ct)
    {
        var port = await RequirePortAsync(ct);
        var sb = new StringBuilder(Api(port, path));
        var sep = path.Contains('?') ? '&' : '?';
        if (!string.IsNullOrWhiteSpace(player) && !string.IsNullOrEmpty(_p.PlayerQueryKey))
        {
            sb.Append(sep).Append(_p.PlayerQueryKey).Append('=').Append(Uri.EscapeDataString(player));
            sep = '&';
        }
        if (!string.IsNullOrWhiteSpace(room) && !string.IsNullOrEmpty(_p.RoomQueryKey))
        {
            sb.Append(sep).Append(_p.RoomQueryKey).Append('=').Append(Uri.EscapeDataString(room));
            sep = '&';
        }
        if (!string.IsNullOrEmpty(_p.NodesQueryKey))
        {
            foreach (var node in nodes)
            {
                sb.Append(sep).Append(_p.NodesQueryKey).Append('=').Append(Uri.EscapeDataString(node));
                // 必须更新分隔符：多个节点时只用第一个带 '?'，否则拼出
                // "...?public_nodes=a?public_nodes=b"，核心直接回 400（实测踩过）
                sep = '&';
            }
        }

        using var resp = await _http.GetAsync(sb.ToString(), ct);
        // 400 这类错误核心会在响应体里说明哪不对，带上才不会只能靠猜
        var body = "";
        try { body = (await resp.Content.ReadAsStringAsync(ct)).Trim(); } catch { }
        if (body.Length > 300) body = body[..300];
        return (resp.StatusCode, body);
    }

    public async Task<MultiplayerState> FetchStateAsync(CancellationToken ct)
    {
        var port = await RequirePortAsync(ct);
        string body;
        try
        {
            using var resp = await _http.GetAsync(Api(port, _p.StatePath), ct);
            if (!resp.IsSuccessStatusCode)
                throw new MultiplayerCoreNotReadyException($"联机核心返回 HTTP {(int)resp.StatusCode}");
            body = await resp.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException ex)
        {
            throw new MultiplayerCoreNotReadyException($"无法连接联机核心：{ex.Message}");
        }

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        var raw = Field(root, _p.StateField) ?? "waiting";
        var phase = _p.PhaseMap.TryGetValue(raw, out var mapped) ? mapped : MultiplayerPhase.Idle;
        var players = ReadPlayers(root);
        if (phase == MultiplayerPhase.Failed)
        {
            var kind = Field(root, _p.ErrorTypeField) ?? "unknown";
            var message = _p.ErrorMap.TryGetValue(kind, out var m) ? m : "联机发生未知错误";
            return new MultiplayerState(phase, ErrorKind: kind, Error: message);
        }
        return new MultiplayerState(
            phase,
            Room: Field(root, _p.RoomField),
            Address: Field(root, _p.AddressField),
            Difficulty: Field(root, _p.DifficultyField)?.ToLowerInvariant(),
            Players: players);
    }

    /// <summary>房间成员（核心返回 <c>profiles:[{name..}]</c>；字段缺失时留空即可）</summary>
    private IReadOnlyList<string> ReadPlayers(JsonElement root)
    {
        if (string.IsNullOrEmpty(_p.PlayersField) ||
            !root.TryGetProperty(_p.PlayersField, out var arr) ||
            arr.ValueKind != JsonValueKind.Array) return [];
        var list = new List<string>();
        foreach (var item in arr.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.Object)
            {
                var name = Field(item, "name");
                if (!string.IsNullOrWhiteSpace(name)) list.Add(name);
            }
            else if (item.ValueKind == JsonValueKind.String)
            {
                list.Add(item.GetString()!);
            }
        }
        return list;
    }

    private static string? Field(JsonElement root, string name)
    {
        if (string.IsNullOrEmpty(name) || !root.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.ToString(),
            JsonValueKind.True or JsonValueKind.False => v.GetBoolean().ToString(),
            _ => null,
        };
    }

    public async Task<string> FetchLogsAsync(CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_p.LogPath)) return "";
        var port = await RequirePortAsync(ct);
        try
        {
            using var resp = await _http.GetAsync(Api(port, _p.LogPath), ct);
            return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : "";
        }
        catch
        {
            return "";
        }
    }

    public async Task<IReadOnlyList<string>> FetchNodesAsync(string region, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_p.NodeListUrl)) return [];
        try
        {
            using var resp = await _http.GetAsync(_p.Expand(_p.NodeListUrl), ct);
            if (!resp.IsSuccessStatusCode) return [];
            using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync(ct));
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return [];
            var includeCn = !string.Equals(region, "global", StringComparison.OrdinalIgnoreCase);
            var onlyCn = string.Equals(region, "cn", StringComparison.OrdinalIgnoreCase);
            var nodes = new List<string>();
            foreach (var item in doc.RootElement.EnumerateArray())
            {
                // 兼容 [{"url":..,"region":"CN"}] 与 ["tcp://.."] 两种形态
                var url = item.ValueKind == JsonValueKind.String
                    ? item.GetString()
                    : Field(item, "url");
                var nodeRegion = item.ValueKind == JsonValueKind.Object ? Field(item, "region") : null;
                if (string.IsNullOrWhiteSpace(url)) continue;
                var isCn = nodeRegion?.Equals("CN", StringComparison.OrdinalIgnoreCase) == true;
                if (onlyCn && !isCn && nodeRegion is not null) continue;
                if (!includeCn && isCn) continue;
                nodes.Add(url);
            }
            return nodes;
        }
        catch (Exception ex)
        {
            // 节点列表拿不到不阻断联机：核心自带默认节点
            LauncherLog.Write("multiplayer", $"{_p.Id} 获取公共节点失败：{ex.Message}");
            return [];
        }
    }

    private async Task<int> RequirePortAsync(CancellationToken ct)
    {
        if (_port != 0) return _port;
        if (!await EnsureSessionAsync(ct))
            throw new MultiplayerCoreNotReadyException("联机会话未就绪");
        if (_port == 0) throw new MultiplayerCoreNotReadyException("联机会话未就绪");
        return _port;
    }

    private string Api(int port, string path) => _p.Expand(_p.ApiBase, port) + path;

    /// <summary>「回到空闲」这类不需要结果、失败也不该炸的调用</summary>
    private async Task TryGetAsync(string url, CancellationToken ct)
    {
        try
        {
            using var _ = await _http.GetAsync(url, ct);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("multiplayer", $"{_p.Id} 调用 {url} 失败：{ex.Message}");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* 占用/权限问题不阻断 */ }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _http.Dispose();
        _sessionGate.Dispose();
    }

    /// <summary>
    /// 下载期间按落盘文件大小上报进度与瞬时速度。
    /// <para>DownloadService 只保证把文件拿到手（多源重试 / 分片），没有进度回调；
    /// 小包（几 MB）用「看文件长多大」这一招足够，且不影响下载链路本身。</para>
    /// </summary>
    private sealed class SizeReporter : IDisposable
    {
        private readonly CancellationTokenSource _cts = new();
        private readonly Task _loop;

        public SizeReporter(string path, string stage, IProgress<MultiplayerProgress>? progress)
        {
            if (progress is null)
            {
                _loop = Task.CompletedTask;
                return;
            }
            _loop = Task.Run(async () =>
            {
                long last = 0;
                var lastAt = DateTimeOffset.Now;
                while (!_cts.IsCancellationRequested)
                {
                    try
                    {
                        var len = File.Exists(path) ? new FileInfo(path).Length : 0;
                        var now = DateTimeOffset.Now;
                        var dt = (now - lastAt).TotalSeconds;
                        var speed = dt > 0.2 ? (len - last) / dt : 0;
                        if (len != last || dt > 0.2)
                        {
                            progress.Report(new MultiplayerProgress(stage, len, null, Math.Max(0, speed)));
                            last = len;
                            lastAt = now;
                        }
                    }
                    catch
                    {
                        // 文件还没建出来 / 已被读走：下一轮再看
                    }
                    try { await Task.Delay(300, _cts.Token); } catch { return; }
                }
            });
        }

        public void Dispose()
        {
            try { _cts.Cancel(); } catch { /* 已释放 */ }
            _cts.Dispose();
        }
    }
}
