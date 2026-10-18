// 存档管理：saves 目录扫描（解析 level.dat NBT）+ 备份 zip + 删除

using System.IO.Compression;
using System.Text;

namespace Starcloud.Core.Services;

/// <summary>单个世界的信息（level.dat 解析结果 + 所属版本归属）</summary>
public sealed record SaveInfo(
    string FolderName,
    string LevelName,
    DateTime? LastPlayed,
    long SizeBytes,
    string? IconPath,
    string VersionId,   // 所属版本 Id；"" 表示共享根
    bool Isolated,      // 所属版本是否隔离
    string GameRoot);   // 存档所在实例根（打开文件夹 / 备份 / 删除均以此为基准）

public sealed class SaveService
{
    /// <summary>列出实例根 saves 下的世界（level.dat 存在的目录）</summary>
    public List<SaveInfo> ListSaves(string gameRoot, string versionId, bool isolated)
    {
        var dir = Path.Combine(gameRoot, "saves");
        var result = new List<SaveInfo>();
        if (!Directory.Exists(dir)) return result;

        foreach (var folder in Directory.EnumerateDirectories(dir))
        {
            var levelDat = Path.Combine(folder, "level.dat");
            if (!File.Exists(levelDat)) continue;

            string? levelName = null;
            DateTime? lastPlayed = null;
            try
            {
                using var gz = new GZipStream(File.OpenRead(levelDat), CompressionMode.Decompress);
                var root = LevelNbtReader.Read(gz);
                if (root?.Get("Data") is LevelNbtCompound data)
                {
                    levelName = (data.Get("LevelName") as LevelNbtString)?.Value;
                    if (data.Get("LastPlayed") is LevelNbtLong lp)
                        lastPlayed = DateTimeOffset.FromUnixTimeMilliseconds(lp.Value).LocalDateTime;
                }
            }
            catch
            {
                // level.dat 损坏时仍列出目录，名称回退为文件夹名
            }

            var icon = Path.Combine(folder, "icon.png");
            result.Add(new SaveInfo(
                Path.GetFileName(folder),
                string.IsNullOrWhiteSpace(levelName) ? Path.GetFileName(folder) : levelName,
                lastPlayed,
                GetDirSize(folder),
                File.Exists(icon) ? icon : null,
                versionId,
                isolated,
                gameRoot));
        }

        // 最近游玩在前，无时间戳的排后
        result.Sort((a, b) => (b.LastPlayed ?? DateTime.MinValue).CompareTo(a.LastPlayed ?? DateTime.MinValue));
        return result;
    }

    /// <summary>备份世界到 backupsDir/&lt;名&gt;-&lt;时间戳&gt;.zip，返回 zip 路径</summary>
    public string Backup(string gameRoot, string backupsDir, string folderName)
    {
        var src = Path.Combine(gameRoot, "saves", folderName);
        if (!Directory.Exists(src))
            throw new DirectoryNotFoundException($"存档目录不存在：{folderName}");

        Directory.CreateDirectory(backupsDir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var safeName = string.Concat(folderName.Split(Path.GetInvalidFileNameChars()));
        var zipPath = Path.Combine(backupsDir, $"{safeName}-{stamp}.zip");
        ZipFile.CreateFromDirectory(src, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return zipPath;
    }

    /// <summary>
    /// 导出世界为分享包（zip，根目录即世界内容）：与备份同一打包逻辑，落点是用户在
    /// 保存对话框里选的路径——发给任何人，对方在存档页「导入分享包」即可，
    /// 不要求对方登录任何账号（跨账号分享走文件，不经云端）。
    /// </summary>
    public string ExportForShare(string gameRoot, string folderName, string destFile)
    {
        var src = Path.Combine(gameRoot, "saves", folderName);
        if (!Directory.Exists(src))
            throw new DirectoryNotFoundException($"存档目录不存在：{folderName}");
        var dir = Path.GetDirectoryName(destFile);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        if (File.Exists(destFile)) File.Delete(destFile);
        ZipFile.CreateFromDirectory(src, destFile, CompressionLevel.Optimal, includeBaseDirectory: false);
        return destFile;
    }

    /// <summary>分享包是不是有效的世界 zip（根目录直接含 level.dat——与导出格式一致）</summary>
    public static bool IsValidSharePackage(string zipFile)
    {
        try
        {
            using var zip = ZipFile.OpenRead(zipFile);
            return zip.Entries.Any(e =>
                string.Equals(e.FullName, "level.dat", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 导入分享包到实例根的 saves。返回实际落地的世界目录名：
    /// 目录名取 zip 文件名（去扩展），与现有世界重名时自动追加 -2 / -3…，绝不覆盖。
    /// </summary>
    public string ImportShared(string zipFile, string gameRoot)
    {
        if (!IsValidSharePackage(zipFile))
            throw new InvalidOperationException("这不是有效的世界分享包（缺少 level.dat）");
        var savesDir = Path.Combine(gameRoot, "saves");
        Directory.CreateDirectory(savesDir);

        var baseName = string.Concat(Path.GetFileNameWithoutExtension(zipFile)
            .Split(Path.GetInvalidFileNameChars())).Trim();
        if (baseName.Length == 0) baseName = "Imported World";
        var folder = baseName;
        for (var i = 2; Directory.Exists(Path.Combine(savesDir, folder)); i++)
            folder = $"{baseName}-{i}";

        ZipFile.ExtractToDirectory(zipFile, Path.Combine(savesDir, folder), overwriteFiles: true);
        return folder;
    }

    /// <summary>删除世界目录</summary>
    public void Delete(string gameRoot, string folderName)
    {
        var dir = Path.Combine(gameRoot, "saves", folderName);
        if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
    }

    private static long GetDirSize(string dir)
    {
        long size = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
            {
                try { size += new FileInfo(f).Length; } catch { /* 忽略不可读文件 */ }
            }
        }
        catch { /* 目录遍历失败按 0 */ }
        return size;
    }
}

// ════════════ 最小 NBT 读取器（仅满足 level.dat 解析） ════════════

public abstract record LevelNbtTag
{
    public string Name { get; init; } = "";
}

public sealed record LevelNbtCompound : LevelNbtTag
{
    public List<LevelNbtTag> Items { get; init; } = [];
    public LevelNbtTag? Get(string name) => Items.FirstOrDefault(t => t.Name == name);
}

public sealed record LevelNbtString : LevelNbtTag
{
    public string Value { get; init; } = "";
}

public sealed record LevelNbtLong : LevelNbtTag
{
    public long Value { get; init; }
}

public sealed record LevelNbtInt : LevelNbtTag
{
    public int Value { get; init; }
}

public sealed record LevelNbtByte : LevelNbtTag
{
    public byte Value { get; init; }
}

/// <summary>大端序 NBT 流读取（Tag_End 终止 Compound；未使用的标量按长度跳过）</summary>
public static class LevelNbtReader
{
    public static LevelNbtCompound? Read(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var type = br.ReadByte();
        if (type != 10) return null;    // 根必须是 Compound
        return ReadTag(br, 10) as LevelNbtCompound;
    }

    private static LevelNbtTag ReadTag(BinaryReader br, int type) => type switch
    {
        1 => new LevelNbtByte { Value = br.ReadByte() },
        2 => new LevelNbtInt { Value = ReadShort(br) },                       // short 按 int 承载
        3 => new LevelNbtInt { Value = ReadInt(br) },
        4 => new LevelNbtLong { Value = ReadLong(br) },
        5 => new LevelNbtInt { Value = BitConverter.ToInt32(BitConverter.GetBytes(ReadInt(br)), 0) }, // float 存 int 位
        6 => new LevelNbtLong { Value = ReadLong(br) },                       // double 存 long 位
        7 => Skip(br, ReadInt(br)) ?? new LevelNbtByte(),                     // byte array → 跳过
        8 => new LevelNbtString { Value = ReadString(br) },
        9 => ReadList(br),
        10 => ReadCompound(br),
        11 => Skip(br, ReadInt(br) * 4) ?? new LevelNbtByte(),                // int array
        12 => Skip(br, ReadInt(br) * 8) ?? new LevelNbtByte(),                // long array
        _ => throw new InvalidDataException($"未知 NBT 标签类型 {type}"),
    };

    private static LevelNbtTag? Skip(BinaryReader br, long count)
    {
        for (long i = 0; i < count; i++) br.ReadByte();
        return null;
    }

    private static LevelNbtList ReadList(BinaryReader br)
    {
        var itemType = br.ReadByte();
        var count = ReadInt(br);
        var items = new List<LevelNbtTag>();
        for (int i = 0; i < count; i++)
            items.Add(ReadTag(br, itemType));
        return new LevelNbtList { Items = items };
    }

    private static LevelNbtCompound ReadCompound(BinaryReader br)
    {
        var items = new List<LevelNbtTag>();
        while (true)
        {
            var type = br.ReadByte();
            if (type == 0) break;
            var name = ReadString(br);
            var tag = ReadTag(br, type);
            items.Add(tag with { Name = name });
        }
        return new LevelNbtCompound { Items = items };
    }

    private static string ReadString(BinaryReader br)
    {
        var len = ReadUShort(br);
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }

    private static short ReadShort(BinaryReader br)
    {
        Span<byte> buf = stackalloc byte[2];
        br.Read(buf);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt16BigEndian(buf);
    }

    private static ushort ReadUShort(BinaryReader br)
    {
        Span<byte> buf = stackalloc byte[2];
        br.Read(buf);
        return System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(buf);
    }

    private static int ReadInt(BinaryReader br)
    {
        Span<byte> buf = stackalloc byte[4];
        br.Read(buf);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(buf);
    }

    private static long ReadLong(BinaryReader br)
    {
        Span<byte> buf = stackalloc byte[8];
        br.Read(buf);
        return System.Buffers.Binary.BinaryPrimitives.ReadInt64BigEndian(buf);
    }
}

public sealed record LevelNbtList : LevelNbtTag
{
    public List<LevelNbtTag> Items { get; init; } = [];
}
