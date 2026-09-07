// 存档管理：saves 目录扫描（解析 level.dat NBT）+ 备份 zip + 删除

using System.IO.Compression;
using System.Text;

namespace Starcloud.Core.Services;

/// <summary>单个世界的信息（level.dat 解析结果）</summary>
public sealed record SaveInfo(
    string FolderName,
    string LevelName,
    DateTime? LastPlayed,
    long SizeBytes,
    string? IconPath);

public sealed class SaveService
{
    private string SavesDir(string dotMinecraft) => Path.Combine(dotMinecraft, "saves");

    /// <summary>列出 saves 下的世界（level.dat 存在的目录）</summary>
    public List<SaveInfo> ListSaves(string dotMinecraft)
    {
        var dir = SavesDir(dotMinecraft);
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
                var root = NbtReader.Read(gz);
                if (root?.Get("Data") is NbtCompound data)
                {
                    levelName = (data.Get("LevelName") as NbtString)?.Value;
                    if (data.Get("LastPlayed") is NbtLong lp)
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
                File.Exists(icon) ? icon : null));
        }

        // 最近游玩在前，无时间戳的排后
        result.Sort((a, b) => (b.LastPlayed ?? DateTime.MinValue).CompareTo(a.LastPlayed ?? DateTime.MinValue));
        return result;
    }

    /// <summary>备份世界到 {dotMinecraft}/backups/&lt;名&gt;-&lt;时间戳&gt;.zip，返回 zip 路径</summary>
    public string Backup(string dotMinecraft, string folderName)
    {
        var src = Path.Combine(SavesDir(dotMinecraft), folderName);
        if (!Directory.Exists(src))
            throw new DirectoryNotFoundException($"存档目录不存在：{folderName}");

        var backupDir = Path.Combine(dotMinecraft, "backups");
        Directory.CreateDirectory(backupDir);
        var stamp = DateTime.Now.ToString("yyyy-MM-dd_HHmmss");
        var safeName = string.Concat(folderName.Split(Path.GetInvalidFileNameChars()));
        var zipPath = Path.Combine(backupDir, $"{safeName}-{stamp}.zip");
        ZipFile.CreateFromDirectory(src, zipPath, CompressionLevel.Optimal, includeBaseDirectory: false);
        return zipPath;
    }

    /// <summary>删除世界目录</summary>
    public void Delete(string dotMinecraft, string folderName)
    {
        var dir = Path.Combine(SavesDir(dotMinecraft), folderName);
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

public abstract record NbtTag
{
    public string Name { get; init; } = "";
}

public sealed record NbtCompound : NbtTag
{
    public List<NbtTag> Items { get; init; } = [];
    public NbtTag? Get(string name) => Items.FirstOrDefault(t => t.Name == name);
}

public sealed record NbtString : NbtTag
{
    public string Value { get; init; } = "";
}

public sealed record NbtLong : NbtTag
{
    public long Value { get; init; }
}

public sealed record NbtInt : NbtTag
{
    public int Value { get; init; }
}

public sealed record NbtByte : NbtTag
{
    public byte Value { get; init; }
}

/// <summary>大端序 NBT 流读取（Tag_End 终止 Compound；未使用的标量按长度跳过）</summary>
public static class NbtReader
{
    public static NbtCompound? Read(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var type = br.ReadByte();
        if (type != 10) return null;    // 根必须是 Compound
        return ReadTag(br, 10) as NbtCompound;
    }

    private static NbtTag ReadTag(BinaryReader br, int type) => type switch
    {
        1 => new NbtByte { Value = br.ReadByte() },
        2 => new NbtInt { Value = ReadShort(br) },                       // short 按 int 承载
        3 => new NbtInt { Value = ReadInt(br) },
        4 => new NbtLong { Value = ReadLong(br) },
        5 => new NbtInt { Value = BitConverter.ToInt32(BitConverter.GetBytes(ReadInt(br)), 0) }, // float 存 int 位
        6 => new NbtLong { Value = ReadLong(br) },                       // double 存 long 位
        7 => Skip(br, ReadInt(br)) ?? new NbtByte(),                     // byte array → 跳过
        8 => new NbtString { Value = ReadString(br) },
        9 => ReadList(br),
        10 => ReadCompound(br),
        11 => Skip(br, ReadInt(br) * 4) ?? new NbtByte(),                // int array
        12 => Skip(br, ReadInt(br) * 8) ?? new NbtByte(),                // long array
        _ => throw new InvalidDataException($"未知 NBT 标签类型 {type}"),
    };

    private static NbtTag? Skip(BinaryReader br, long count)
    {
        for (long i = 0; i < count; i++) br.ReadByte();
        return null;
    }

    private static NbtList ReadList(BinaryReader br)
    {
        var itemType = br.ReadByte();
        var count = ReadInt(br);
        var items = new List<NbtTag>();
        for (int i = 0; i < count; i++)
            items.Add(ReadTag(br, itemType));
        return new NbtList { Items = items };
    }

    private static NbtCompound ReadCompound(BinaryReader br)
    {
        var items = new List<NbtTag>();
        while (true)
        {
            var type = br.ReadByte();
            if (type == 0) break;
            var name = ReadString(br);
            var tag = ReadTag(br, type);
            items.Add(tag with { Name = name });
        }
        return new NbtCompound { Items = items };
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

public sealed record NbtList : NbtTag
{
    public List<NbtTag> Items { get; init; } = [];
}
