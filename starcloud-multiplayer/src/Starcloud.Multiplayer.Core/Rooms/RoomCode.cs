using System.Security.Cryptography;

namespace Starcloud.Multiplayer.Core.Rooms;

/// <summary>
/// 房间码与进网凭据的生成规则。**跨端冻结**：鸿蒙侧必须用同一套字母表与字长，
/// 否则同一房间的成员算不出同一个网络名/密钥，根本进不了同一个 EasyTier 网络。
/// </summary>
public static class RoomCode
{
    /// <summary>字母表：去掉易混的 0/O/1/I/L（读出来不歧义，人也念得清）。</summary>
    public const string Alphabet = "23456789ABCDEFGHJKMNPQRSTUVWXYZ";

    public const int RoomIdLength = 8;

    /// <summary>32 位随机密钥 ≈ 158 bit，够用且便于人工核对。</summary>
    public const int NetworkSecretLength = 32;

    /// <summary>网络名前缀。房间码变了网络名就变，密钥也不复用。</summary>
    public const string NetworkNamePrefix = "sc-mp-";

    /// <summary>生成一个新的房间号（由服务端负责去重）。</summary>
    public static string NewRoomId() => RandomChars(RoomIdLength);

    /// <summary>生成进网密钥。房主本地生成后上报服务端，服务端只在有权限时下发。</summary>
    public static string NewNetworkSecret() => RandomChars(NetworkSecretLength);

    /// <summary>房间号 → EasyTier 网络名。</summary>
    public static string NetworkNameFor(string roomId)
    {
        if (!IsValidRoomId(roomId))
            throw new ArgumentException($"房间号不合法：{roomId}", nameof(roomId));
        return NetworkNamePrefix + roomId;
    }

    public static bool IsValidRoomId(string? roomId) =>
        roomId is { Length: RoomIdLength } && roomId.All(c => Alphabet.Contains(c, StringComparison.Ordinal));

    public static bool IsValidNetworkSecret(string? secret) =>
        secret is { Length: NetworkSecretLength } && secret.All(c => Alphabet.Contains(c, StringComparison.Ordinal));

    private static string RandomChars(int length)
    {
        var buffer = new char[length];
        // 用 RandomNumberGenerator.GetItems 直接在字母表上取，避免取模偏置。
        RandomNumberGenerator.GetItems<char>(Alphabet, buffer);
        return new string(buffer);
    }
}
