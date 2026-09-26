// 游戏运行历史（最近 50 局）
// 设计：docs/designs/2026-09-26-game-running-status-panel-design.md §3 / §7
//
// 「运行状态」页的「最近运行」数据源。文件损坏时的取舍：
// 读可以兜底（返回空列表），写必须拒绝——否则用户攒的历史会被一次空库覆盖。

using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Starcloud.Core.Services;

/// <summary>一局结束后的落盘记录</summary>
public sealed record GameRunRecord(
    [property: JsonPropertyName("version_id")] string VersionId,
    [property: JsonPropertyName("started_at")] DateTime StartedAt,
    [property: JsonPropertyName("ended_at")] DateTime EndedAt,
    [property: JsonPropertyName("duration_seconds")] double DurationSeconds,
    [property: JsonPropertyName("exit_code")] int? ExitCode,
    [property: JsonPropertyName("peak_working_set_bytes")] long PeakWorkingSetBytes,
    [property: JsonPropertyName("crashed")] bool Crashed)
{
    /// <summary>时长（由 <see cref="DurationSeconds"/> 还原，避免 TimeSpan 的序列化差异）</summary>
    public TimeSpan Duration => TimeSpan.FromSeconds(DurationSeconds);
}

/// <summary>运行历史存取（&lt;dataDir&gt;/game.history.json，上限 50 条，新记录在前）</summary>
public sealed class GameRunHistoryStore
{
    /// <summary>保留的最大条数</summary>
    public const int MaxRecords = 50;

    private readonly string _file;
    private readonly List<GameRunRecord> _records = [];

    /// <summary>上次加载失败（文件损坏）：此后拒绝写入，避免覆盖用户既有历史</summary>
    private bool _loadFailed;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public GameRunHistoryStore(string dataDir)
    {
        _file = Path.Combine(dataDir, "game.history.json");
    }

    public string FilePath => _file;

    /// <summary>内存镜像（新 → 旧）</summary>
    public IReadOnlyList<GameRunRecord> Records => _records;

    /// <summary>上次加载是否失败（损坏）。为 true 时 <see cref="Append"/> 一律拒绝写。</summary>
    public bool LoadFailed => _loadFailed;

    /// <summary>读取历史；文件不存在视为空；解析失败 → 空列表 + 标记拒绝写。不抛。</summary>
    public bool TryLoad()
    {
        _records.Clear();
        _loadFailed = false;
        try
        {
            if (!File.Exists(_file)) return true;
            var root = JsonNode.Parse(File.ReadAllText(_file));
            if (root?["records"] is not JsonArray arr) return true;
            foreach (var node in arr)
            {
                var rec = node?.Deserialize<GameRunRecord>(Options);
                if (rec is not null) _records.Add(rec);
            }
            return true;
        }
        catch
        {
            _records.Clear();
            _loadFailed = true;
            return false;
        }
    }

    /// <summary>
    /// 追加一条记录（插到最前，超出 <see cref="MaxRecords"/> 丢弃最旧）并原子写盘。
    /// 上次加载失败 → 拒绝写并返回 false（不覆盖可能仍有价值的历史文件）。
    /// </summary>
    public bool Append(GameRunRecord rec)
    {
        if (_loadFailed)
        {
            LauncherLog.Write("runtime", "运行历史文件损坏，已拒绝写入以免覆盖既有记录");
            return false;
        }

        var snapshot = new List<GameRunRecord>(_records);
        _records.Insert(0, rec);
        if (_records.Count > MaxRecords) _records.RemoveRange(MaxRecords, _records.Count - MaxRecords);

        if (Write()) return true;

        // 落盘失败：回滚内存，保持与磁盘一致
        _records.Clear();
        _records.AddRange(snapshot);
        return false;
    }

    private bool Write()
    {
        try
        {
            var dir = Path.GetDirectoryName(_file);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var root = new JsonObject
            {
                ["version"] = 1,
                ["records"] = new JsonArray(_records
                    .Select(r => (JsonNode?)JsonSerializer.SerializeToNode(r, Options))
                    .ToArray()),
            };
            var tmp = _file + ".tmp";
            File.WriteAllText(tmp, root.ToJsonString(Options));
            File.Move(tmp, _file, overwrite: true);
            return true;
        }
        catch { return false; }
    }
}
