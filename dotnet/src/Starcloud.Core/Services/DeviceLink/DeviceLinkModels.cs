// 设备链路（DeviceLink）：同一 Starcloud ID 下的多台设备互相发现、配对、传数据。
//
// 三条承载（设计：docs/designs/2026-10-14-cross-device-sync-design.md §2）：
//   · WiFi/局域网 —— UDP 广播发现 + TCP 传输（主通道，M1 全量实现）；
//   · 蓝牙 RFCOMM —— 需要应用包身份（unpackaged 无法声明 bluetooth capability），
//     探针返回 NeedsPackaging，数据面协议与 LAN 完全同一套（帧协议只面向 Stream）；
//   · 星闪 NearLink —— 华为私有栈，探测本机驱动服务，不存在即不可用。
//
// 本文件放共享模型与协议常量；发现（PeerBeacon）、传输（PeerLinkServer/Client）、
// 编排（DeviceLinkService / SaveSyncService）各自一个文件。

using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>链路承载通道及其可用状态（UI 按此如实展示）</summary>
public enum LinkChannelStatus
{
    /// <summary>可用（主通道 WiFi/局域网恒为此值）</summary>
    Available,
    /// <summary>本机没有该环境（如无 NearLink 驱动）</summary>
    Unavailable,
    /// <summary>能力被系统限制（蓝牙：unpackaged 应用无法声明 capability）</summary>
    NeedsPackaging,
}

/// <summary>单条通道的状态</summary>
public sealed record LinkChannel(string Name, LinkChannelStatus Status, string Note);

/// <summary>在线对端设备（beacon 汇聚）</summary>
public sealed record PeerInfo
{
    /// <summary>对端用户标识哈希（sha256(user_id) 前 16 hex；与握手的 uid 同源）</summary>
    public string Uid { get; init; } = "";
    /// <summary>对端展示名（用户设置里的设备名 / 机器名）</summary>
    public string Name { get; init; } = "";
    /// <summary>对端 TCP 服务端口</summary>
    public int Port { get; init; }
    /// <summary>最近一次见到的时间（本地时钟；超 15 秒视为离线）</summary>
    public DateTime LastSeen { get; init; }
    /// <summary>最近一次见到的地址（IPv4；TCP 连接目标）</summary>
    public string Address { get; init; } = "";
}

/// <summary>对端存档清单里的一条世界</summary>
public sealed record RemoteSaveInfo(
    string Folder,
    string LevelName,
    DateTime? LastPlayed,
    long SizeBytes,
    string VersionId,
    bool Isolated);

/// <summary>beacon 载荷（JSON；字段名刻意短，广播包越小越好）</summary>
public sealed record BeaconPayload(
    [property: JsonPropertyName("v")] int V,
    [property: JsonPropertyName("uid")] string Uid,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("app")] string App,
    [property: JsonPropertyName("iid")] string Iid = "")
{
    public const int CurrentVersion = 1;
    public const string AppId = "starcloud";
    public const int BeaconPort = 47777;
    public const int LinkPort = 47778;
    /// <summary>广播间隔与离线判定窗口</summary>
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan OfflineAfter = TimeSpan.FromSeconds(15);

    /// <summary>序列化为 UTF-8 广播包；编码失败返回空（监听端忽略空包）</summary>
    public byte[] Encode() =>
        Encoding.UTF8.GetBytes(System.Text.Json.JsonSerializer.Serialize(this));

    /// <summary>解析广播包；版本 / 应用标识不符或 JSON 非法返回 null（静默丢弃）</summary>
    public static BeaconPayload? Decode(ReadOnlySpan<byte> bytes)
    {
        try
        {
            var p = System.Text.Json.JsonSerializer.Deserialize<BeaconPayload>(bytes);
            if (p is null || p.V != CurrentVersion || p.App != AppId) return null;
            return p;
        }
        catch
        {
            return null;
        }
    }
}

/// <summary>协议层公共助手（uid 派生、HMAC 挑战应答）</summary>
public static class LinkAuth
{
    /// <summary>用户标识哈希：sha256(user_id) 前 16 位小写 hex。广播与握手共用；不落盘、不上线原文。</summary>
    public static string UidHash(string userId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(userId))).ToLowerInvariant()[..16];

    /// <summary>挑战应答：HMAC-SHA256(apiKey, message) 的小写 hex。apiKey 只参与运算，永不上线。</summary>
    public static string Sign(string apiKey, string message)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(apiKey));
        return Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(message))).ToLowerInvariant();
    }
}
