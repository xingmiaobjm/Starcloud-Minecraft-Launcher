// 第三方联机 SDK 清单：把一份 JSON 变成 <see cref="MultiplayerSdkProtocol"/>。
//
// 放一份清单到 `<数据目录>/multiplayer/providers/*.json`，重启启动器后就会出现在
// 联机页的提供方下拉里；清单只描述「装什么、怎么起、怎么问状态」，不含任何可执行代码，
// 因此第三方 SDK 的接入不需要启动器发版，也不需要把 DLL 塞进进程。

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services.Multiplayer;

/// <summary>清单 JSON 的字段（大小写不敏感，允许省略有默认值的字段）</summary>
internal sealed class MultiplayerSdkManifest
{
    [JsonPropertyName("id")] public string? Id { get; set; }
    [JsonPropertyName("displayName")] public string? DisplayName { get; set; }
    [JsonPropertyName("vendor")] public string? Vendor { get; set; }
    [JsonPropertyName("vendorUrl")] public string? VendorUrl { get; set; }
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("version")] public string? Version { get; set; }
    [JsonPropertyName("downloadUrls")] public List<string>? DownloadUrls { get; set; }
    [JsonPropertyName("archiveKind")] public string? ArchiveKind { get; set; }
    [JsonPropertyName("executableName")] public string? ExecutableName { get; set; }
    [JsonPropertyName("launchArguments")] public List<string>? LaunchArguments { get; set; }
    [JsonPropertyName("portFile")] public string? PortFile { get; set; }
    [JsonPropertyName("portJsonKey")] public string? PortJsonKey { get; set; }
    [JsonPropertyName("startTimeoutSeconds")] public int? StartTimeoutSeconds { get; set; }
    [JsonPropertyName("apiBase")] public string? ApiBase { get; set; }
    [JsonPropertyName("statePath")] public string? StatePath { get; set; }
    [JsonPropertyName("idlePath")] public string? IdlePath { get; set; }
    [JsonPropertyName("hostPath")] public string? HostPath { get; set; }
    [JsonPropertyName("joinPath")] public string? JoinPath { get; set; }
    [JsonPropertyName("logPath")] public string? LogPath { get; set; }
    [JsonPropertyName("shutdownPath")] public string? ShutdownPath { get; set; }
    [JsonPropertyName("playerQueryKey")] public string? PlayerQueryKey { get; set; }
    [JsonPropertyName("roomQueryKey")] public string? RoomQueryKey { get; set; }
    [JsonPropertyName("nodesQueryKey")] public string? NodesQueryKey { get; set; }
    [JsonPropertyName("nodeListUrl")] public string? NodeListUrl { get; set; }
    [JsonPropertyName("stateField")] public string? StateField { get; set; }
    [JsonPropertyName("roomField")] public string? RoomField { get; set; }
    [JsonPropertyName("addressField")] public string? AddressField { get; set; }
    [JsonPropertyName("difficultyField")] public string? DifficultyField { get; set; }
    [JsonPropertyName("playersField")] public string? PlayersField { get; set; }
    [JsonPropertyName("errorTypeField")] public string? ErrorTypeField { get; set; }
    [JsonPropertyName("phaseMap")] public Dictionary<string, string>? PhaseMap { get; set; }
    [JsonPropertyName("errorMap")] public Dictionary<string, string>? ErrorMap { get; set; }
    [JsonPropertyName("versionFile")] public string? VersionFile { get; set; }
    [JsonPropertyName("legacyCoreDir")] public string? LegacyCoreDir { get; set; }
}

public static class MultiplayerSdkManifests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>载入清单目录下的全部第三方 SDK；单个文件出错只跳过它，不影响其它提供方</summary>
    public static List<MultiplayerSdkProtocol> LoadAll(string dir)
    {
        var result = new List<MultiplayerSdkProtocol>();
        if (!Directory.Exists(dir)) return result;
        foreach (var file in Directory.EnumerateFiles(dir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            try
            {
                result.Add(Parse(File.ReadAllText(file)));
            }
            catch (Exception ex)
            {
                LauncherLog.Write("multiplayer", $"第三方联机 SDK 清单载入失败：{Path.GetFileName(file)} → {ex.Message}");
            }
        }
        return result;
    }

    public static MultiplayerSdkProtocol Parse(string json)
    {
        var m = JsonSerializer.Deserialize<MultiplayerSdkManifest>(json, Options)
            ?? throw new MultiplayerSdkManifestException("清单内容为空");
        return Build(m);
    }

    private static MultiplayerSdkProtocol Build(MultiplayerSdkManifest m)
    {
        var id = Require(m.Id, "id");
        // id 会变成目录名：只允许字母数字与 - _（避免 ".." 之类的路径穿越写法）
        if (id.Any(c => !(char.IsLetterOrDigit(c) || c is '-' or '_')))
            throw new MultiplayerSdkManifestException($"id 只能包含字母、数字、- 与 _：{id}");
        if (id.Equals("terracotta", StringComparison.OrdinalIgnoreCase))
            throw new MultiplayerSdkManifestException("id 与内置的陶瓦联机冲突");

        var displayName = Require(m.DisplayName, "displayName");
        var version = Require(m.Version, "version");
        var exe = Require(m.ExecutableName, "executableName");
        var urls = m.DownloadUrls?.Where(u => !string.IsNullOrWhiteSpace(u)).ToList() ?? [];
        if (urls.Count == 0) throw new MultiplayerSdkManifestException("downloadUrls 不能为空");
        var args = m.LaunchArguments?.Where(a => !string.IsNullOrWhiteSpace(a)).ToList() ?? [];
        if (args.Count == 0) throw new MultiplayerSdkManifestException("launchArguments 不能为空");

        var phaseMap = MultiplayerSdkProtocol.DefaultPhaseMap();
        if (m.PhaseMap is { Count: > 0 })
        {
            phaseMap = new Dictionary<string, MultiplayerPhase>(StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in m.PhaseMap) phaseMap[k] = ParsePhase(v);
        }

        return new MultiplayerSdkProtocol
        {
            Id = id,
            DisplayName = displayName,
            Vendor = string.IsNullOrWhiteSpace(m.Vendor) ? displayName : m.Vendor!,
            VendorUrl = m.VendorUrl,
            Description = m.Description,
            Version = version,
            DownloadUrls = urls,
            ArchiveKind = string.IsNullOrWhiteSpace(m.ArchiveKind) ? "tar.gz" : m.ArchiveKind!,
            ExecutableName = exe,
            VersionFile = string.IsNullOrWhiteSpace(m.VersionFile) ? "version.txt" : m.VersionFile!,
            LegacyCoreDir = string.IsNullOrWhiteSpace(m.LegacyCoreDir) ? null : m.LegacyCoreDir,
            LaunchArguments = args,
            PortFile = string.IsNullOrWhiteSpace(m.PortFile) ? "http.json" : m.PortFile!,
            PortJsonKey = string.IsNullOrWhiteSpace(m.PortJsonKey) ? "port" : m.PortJsonKey!,
            StartTimeoutSeconds = m.StartTimeoutSeconds is > 0 ? m.StartTimeoutSeconds.Value : 30,
            ApiBase = string.IsNullOrWhiteSpace(m.ApiBase) ? "http://127.0.0.1:{port}" : m.ApiBase!,
            StatePath = string.IsNullOrWhiteSpace(m.StatePath) ? "/state" : m.StatePath!,
            IdlePath = m.IdlePath ?? "/state/ide",
            HostPath = string.IsNullOrWhiteSpace(m.HostPath) ? "/state/scanning" : m.HostPath!,
            JoinPath = string.IsNullOrWhiteSpace(m.JoinPath) ? "/state/guesting" : m.JoinPath!,
            LogPath = m.LogPath ?? "/log?fetch=true",
            ShutdownPath = m.ShutdownPath ?? "",
            PlayerQueryKey = string.IsNullOrWhiteSpace(m.PlayerQueryKey) ? "player" : m.PlayerQueryKey!,
            RoomQueryKey = string.IsNullOrWhiteSpace(m.RoomQueryKey) ? "room" : m.RoomQueryKey!,
            NodesQueryKey = m.NodesQueryKey ?? "public_nodes",
            NodeListUrl = m.NodeListUrl ?? "",
            StateField = string.IsNullOrWhiteSpace(m.StateField) ? "state" : m.StateField!,
            RoomField = m.RoomField ?? "room",
            AddressField = m.AddressField ?? "url",
            DifficultyField = m.DifficultyField ?? "difficulty",
            PlayersField = m.PlayersField ?? "profiles",
            ErrorTypeField = m.ErrorTypeField ?? "type",
            PhaseMap = phaseMap,
            ErrorMap = m.ErrorMap ?? [],
        };
    }

    private static string Require(string? value, string field) =>
        string.IsNullOrWhiteSpace(value)
            ? throw new MultiplayerSdkManifestException($"清单缺少必填字段 {field}")
            : value.Trim();

    private static MultiplayerPhase ParsePhase(string value) => value.Trim().ToLowerInvariant() switch
    {
        "idle" or "waiting" => MultiplayerPhase.Idle,
        "host-scanning" => MultiplayerPhase.HostScanning,
        "host-starting" => MultiplayerPhase.HostStarting,
        "host-ready" or "host-ok" => MultiplayerPhase.HostReady,
        "guest-connecting" => MultiplayerPhase.GuestConnecting,
        "guest-entering" or "guest-starting" => MultiplayerPhase.GuestEntering,
        "guest-ready" or "guest-ok" => MultiplayerPhase.GuestReady,
        "failed" or "exception" or "error" => MultiplayerPhase.Failed,
        _ => throw new MultiplayerSdkManifestException($"phaseMap 里有无法识别的阶段：{value}"),
    };
}
