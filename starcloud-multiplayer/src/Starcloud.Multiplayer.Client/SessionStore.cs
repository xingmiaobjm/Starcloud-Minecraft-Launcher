using System.Text.Json;
using Starcloud.Multiplayer.Core.Models;
using Starcloud.Multiplayer.Core.Protocol;

namespace Starcloud.Multiplayer.Client;

/// <summary>会话持久化：只有本内核自己读写，不碰启动器的 starcloud-id.json。</summary>
public sealed class SessionStore
{
    private readonly IPlatformHost _platform;

    public SessionStore(IPlatformHost platform)
    {
        _platform = platform;
        FilePath = Path.Combine(platform.DataDirectory, "session.json");
    }

    public string FilePath { get; }

    public StarcloudIdentity? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return MpJson.Deserialize<StarcloudIdentity>(File.ReadAllText(FilePath));
        }
        catch (Exception ex) when (ex is IOException or JsonException)
        {
            _platform.Log($"会话文件解析失败，按未登录处理：{ex.Message}");
            return null;
        }
    }

    public void Save(StarcloudIdentity identity)
    {
        try
        {
            Directory.CreateDirectory(_platform.DataDirectory);
            File.WriteAllText(FilePath, MpJson.Serialize(identity));
        }
        catch (Exception ex)
        {
            _platform.Log($"会话写入失败：{ex.Message}");
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch (Exception ex)
        {
            _platform.Log($"会话删除失败：{ex.Message}");
        }
    }
}
