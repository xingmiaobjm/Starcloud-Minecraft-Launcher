// 跨版本启动的未决记录：记录一次「复制到其他版本并启动」，供退出后询问成果归属 / 中断后补问
// 设计：docs/superpowers/specs/2026-09-10-saves-cross-version-design.md §3.5

using System.Text.Json;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>一次跨版本存档启动的未决记录</summary>
public sealed class PendingWorldRecord
{
    [JsonPropertyName("world_folder")]
    public string WorldFolder { get; set; } = "";

    [JsonPropertyName("world_name")]
    public string WorldName { get; set; } = "";

    [JsonPropertyName("source_version")]
    public string SourceVersion { get; set; } = "";

    [JsonPropertyName("target_version")]
    public string TargetVersion { get; set; } = "";

    [JsonPropertyName("source_game_root")]
    public string SourceGameRoot { get; set; } = "";

    [JsonPropertyName("target_game_root")]
    public string TargetGameRoot { get; set; } = "";

    /// <summary>是否已把源存档复制到目标版本</summary>
    [JsonPropertyName("copied")]
    public bool Copied { get; set; }

    [JsonPropertyName("started_at")]
    public DateTime StartedAt { get; set; }

    /// <summary>游戏进程 Id；0 表示尚未成功启动</summary>
    [JsonPropertyName("process_id")]
    public int ProcessId { get; set; }
}

/// <summary>&lt;dotMc&gt;/starcloud/pending-world.json 存取</summary>
public sealed class PendingWorldStore(string dotMc)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private string FilePath => Path.Combine(dotMc, "starcloud", "pending-world.json");

    /// <summary>读取未决记录；文件不存在或损坏返回 null</summary>
    public PendingWorldRecord? Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return null;
            return JsonSerializer.Deserialize<PendingWorldRecord>(File.ReadAllText(FilePath), Options);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>原子写（temp + File.Move overwrite）</summary>
    public void Save(PendingWorldRecord record)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(record, Options));
        File.Move(temp, FilePath, overwrite: true);
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(FilePath)) File.Delete(FilePath);
        }
        catch
        {
            // 删除失败不影响后续流程（下次 Save 会覆盖）
        }
    }
}
