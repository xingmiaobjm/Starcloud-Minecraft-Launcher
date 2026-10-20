// 存档同步语义层：把「一个世界推到对端 / 从对端拉回来」编排成原子步骤——
// 打包 zip → 冲突预检（saves.check）→ 上传（saves.put，附带版本配置 JSON）。
// 冲突策略在 SyncConflict（纯函数）：新者胜、旧者改名保留、两边都不丢。

using System.Security.Cryptography;
using System.IO.Compression;

namespace Starcloud.Core.Services;

/// <summary>同步结果（UI 直接展示 Message）</summary>
public sealed record SaveSyncResult(bool Ok, string Message, SyncDecision? Decision = null);

public sealed class SaveSyncService(
    Func<LauncherSettings> settings,
    StarcloudIdService identity,
    DeviceLinkService link,
    SaveService saves,
    Action<string>? log = null) : Starcloud.Core.Kernel.Contracts.ISaveSync
{
    public string DisplayName => "跨设备存档同步";
    /// <summary>把本地一个世界推送到对端设备（含所属版本配置）。</summary>
    public async Task<SaveSyncResult> PushAsync(
        PeerInfo peer, SaveInfo save, CancellationToken ct = default)
    {
        if (!identity.IsLoggedIn) return new SaveSyncResult(false, "同步需要先登录 Starcloud ID");
        var session = identity.Current!;
        var s = settings();

        // ① 打包（复用备份 zip 逻辑；临时目录用完即删）
        var tempDir = Path.Combine(Path.GetTempPath(), "starcloud-sync");
        Directory.CreateDirectory(tempDir);
        var zip = saves.Backup(save.GameRoot, tempDir, save.FolderName);
        try
        {
            var sha256 = await Task.Run(() =>
                Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(zip))).ToLowerInvariant(), ct);
            var cfgJson = SerializeInstanceConfig(s, save.VersionId);

            await using var client = await PeerLinkClient.ConnectAsync(
                LinkAuth.UidHash(session.UserId), session.ApiKey, link.DeviceName,
                peer.Address, peer.Port, ct);

            // ② 冲突预检：对端同名世界更新就直接停（对端的数据不被动）
            var check = await client.CommandAsync(new
            {
                cmd = "saves.check",
                folder = save.FolderName,
                versionId = save.VersionId,
                isolated = save.Isolated,
                lastPlayedMs = save.LastPlayed is { } t ? ToUnixMs(t) : 0,
            }, ct: ct);
            var action = check.Value<string>("action") ?? "Accept";
            if (Enum.TryParse<SyncDecision>(action, out var decision) && decision == SyncDecision.Reject)
                return new SaveSyncResult(false,
                    $"「{save.LevelName}」在对端有更新的同名世界，已保留对端版本（未覆盖）", decision);

            // ③ 上传 zip（版本配置 JSON 附在同一命令里，对端落地为 pending-config / 直接可用）
            var put = await client.CommandAsync(new
            {
                cmd = "saves.put",
                folder = save.FolderName,
                name = save.LevelName,
                versionId = save.VersionId,
                isolated = save.Isolated,
                lastPlayedMs = save.LastPlayed is { } t2 ? ToUnixMs(t2) : 0,
                sha256,
                cfgJson,
                has_body = true,
            }, bodyFile: zip, ct: ct);
            if (!put.Ok) return new SaveSyncResult(false, $"对端接收失败：{put.Error}", decision);
            log?.Invoke($"已把世界「{save.LevelName}」同步到 {peer.Name}");
            return new SaveSyncResult(true,
                decision == SyncDecision.AcceptWithBackup
                    ? $"已同步到 {peer.Name}；对端旧世界已自动改名保留"
                    : $"已同步到 {peer.Name}",
                decision);
        }
        catch (Exception ex)
        {
            return new SaveSyncResult(false, $"同步失败：{ex.Message}");
        }
        finally
        {
            try { File.Delete(zip); } catch { /* 静默 */ }
        }
    }

    /// <summary>列出对端的全部世界（拉取流程第一步）。</summary>
    public async Task<List<RemoteSaveInfo>?> ListRemoteAsync(PeerInfo peer, CancellationToken ct = default)
    {
        if (!identity.IsLoggedIn) return null;
        var session = identity.Current!;
        try
        {
            await using var client = await PeerLinkClient.ConnectAsync(
                LinkAuth.UidHash(session.UserId), session.ApiKey, link.DeviceName,
                peer.Address, peer.Port, ct);
            var resp = await client.CommandAsync(new { cmd = "saves.list" }, ct: ct);
            if (!resp.Ok || resp.Payload is not { } p) return null;
            var result = new List<RemoteSaveInfo>();
            foreach (var el in p.EnumerateObject())
            {
                if (el.Name != "saves") continue;
                foreach (var item in el.Value.EnumerateArray())
                    result.Add(new RemoteSaveInfo(
                        item.GetProperty("folder").GetString() ?? "",
                        item.GetProperty("name").GetString() ?? "",
                        item.GetProperty("lastPlayedMs").GetInt64() is var ms && ms > 0
                            ? DateTimeOffset.FromUnixTimeMilliseconds(ms).LocalDateTime : null,
                        item.GetProperty("size").GetInt64(),
                        item.GetProperty("versionId").GetString() ?? "",
                        item.GetProperty("isolated").GetBoolean()));
            }
            return result;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>从对端拉回一个世界（落到本地对应根；冲突裁决同推送，对称）。</summary>
    public async Task<SaveSyncResult> PullAsync(
        PeerInfo peer, RemoteSaveInfo remote, string localVersionId, bool localIsolated, CancellationToken ct = default)
    {
        if (!identity.IsLoggedIn) return new SaveSyncResult(false, "同步需要先登录 Starcloud ID");
        var session = identity.Current!;
        var s = settings();
        var target = localIsolated && localVersionId.Length > 0
            ? InstanceLayout.GameRoot(s.DotMinecraft, localVersionId, true)
            : s.DotMinecraft;
        var destDir = Path.Combine(target, "saves", remote.Folder);

        var decision = SyncConflict.Resolve(LocalLastPlayed(destDir), remote.LastPlayed);
        if (decision == SyncDecision.Reject)
            return new SaveSyncResult(false, $"本机的「{remote.LevelName}」更新，未拉取（已保留本地版本）", decision);

        var tempZip = Path.Combine(Path.GetTempPath(), "starcloud-sync", $"pull-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(Path.GetDirectoryName(tempZip)!);
        try
        {
            await using var client = await PeerLinkClient.ConnectAsync(
                LinkAuth.UidHash(session.UserId), session.ApiKey, link.DeviceName,
                peer.Address, peer.Port, ct);
            var resp = await client.CommandAsync(new
            {
                cmd = "saves.get",
                folder = remote.Folder,
                versionId = remote.VersionId,
                isolated = remote.Isolated,
            }, destFile: tempZip, ct: ct);
            // PeerLinkClient 已按 payload.size 接收；对端 zip 由其现场打包（哈希在 payload.sha256 给出则已校验）
            if (Directory.Exists(destDir) && decision == SyncDecision.AcceptWithBackup)
            {
                var backup = destDir + $".conflict-{DateTime.Now:yyyyMMdd-HHmmss}";
                Directory.Move(destDir, backup);
                log?.Invoke($"同步冲突：本地旧世界已保留为 {Path.GetFileName(backup)}");
            }
            ZipFile.ExtractToDirectory(tempZip, destDir, overwriteFiles: true);
            return new SaveSyncResult(true, $"已从 {peer.Name} 拉取「{remote.LevelName}」", decision);
        }
        catch (Exception ex)
        {
            return new SaveSyncResult(false, $"拉取失败：{ex.Message}");
        }
        finally
        {
            try { if (File.Exists(tempZip)) File.Delete(tempZip); } catch { /* 静默 */ }
        }
    }

    /// <summary>本机世界的时间戳（与对端 saves.check 同一来源：level.dat 的 LastPlayed）。</summary>
    private static DateTime? LocalLastPlayed(string worldDir)
    {
        var levelDat = Path.Combine(worldDir, "level.dat");
        if (!File.Exists(levelDat)) return null;
        try
        {
            using var gz = new GZipStream(File.OpenRead(levelDat), CompressionMode.Decompress);
            if (LevelNbtReader.Read(gz)?.Get("Data") is not LevelNbtCompound data) return null;
            return data.Get("LastPlayed") is LevelNbtLong lp
                ? DateTimeOffset.FromUnixTimeMilliseconds(lp.Value).LocalDateTime
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>版本的实例配置 JSON（随存档附带；读不到就发空串 = 对端跳过）</summary>
    private static string SerializeInstanceConfig(LauncherSettings s, string versionId)
    {
        if (versionId.Length == 0) return "";
        try
        {
            var store = new InstanceSettingsStore(s.DotMinecraft);
            return System.Text.Json.JsonSerializer.Serialize(store.Get(versionId));
        }
        catch
        {
            return "";
        }
    }

    private static long ToUnixMs(DateTime t) => new DateTimeOffset(t).ToUnixTimeMilliseconds();
}
