// 完整 NBT（Named Binary Tag）读写：投影文件（.litematic / 结构 .nbt / .schematic / .schem）
// 需要 LongArray / ByteArray 与写入能力，与 SaveService 里那个"仅够解析 level.dat"的最小读取器
// 分开维护（那个刻意只读、跳过数组，别动它以免影响存档解析）。
//
// 格式要点：大端序；根必须是 Compound；List 记元素类型与个数；字符串为 ushort 长度 + UTF-8；
// 投影文件外层统一 gzip（ReadGzip/WriteGzip 包一层）。

using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace Starcloud.Core.Services.Projection;

public abstract class NbtTag
{
    public string Name { get; set; } = "";
}

public sealed class NbtByte : NbtTag { public byte Value { get; set; } }
public sealed class NbtShort : NbtTag { public short Value { get; set; } }
public sealed class NbtInt : NbtTag { public int Value { get; set; } }
public sealed class NbtLong : NbtTag { public long Value { get; set; } }
public sealed class NbtFloat : NbtTag { public float Value { get; set; } }
public sealed class NbtDouble : NbtTag { public double Value { get; set; } }
public sealed class NbtString : NbtTag { public string Value { get; set; } = ""; }
public sealed class NbtByteArray : NbtTag { public byte[] Value { get; set; } = []; }
public sealed class NbtIntArray : NbtTag { public int[] Value { get; set; } = []; }
public sealed class NbtLongArray : NbtTag { public long[] Value { get; set; } = []; }

public sealed class NbtList : NbtTag
{
    /// <summary>元素类型（TAG id；空列表用 0 = TAG_End，与原版行为一致）</summary>
    public byte ItemType { get; set; }
    public List<NbtTag> Items { get; set; } = [];

    public NbtList Of(byte itemType) { ItemType = itemType; return this; }
}

public sealed class NbtCompound : NbtTag
{
    public List<NbtTag> Items { get; set; } = [];

    public NbtTag? Get(string name) => Items.FirstOrDefault(t => t.Name == name);
    public NbtCompound? GetCompound(string name) => Get(name) as NbtCompound;

    public string GetString(string name, string fallback = "") =>
        Get(name) is NbtString s ? s.Value : fallback;

    public int GetInt(string name, int fallback = 0) =>
        Get(name) is NbtInt i ? i.Value
        : Get(name) is NbtShort sh ? sh.Value
        : Get(name) is NbtByte b ? b.Value
        : fallback;

    public long GetLong(string name, long fallback = 0) =>
        Get(name) is NbtLong l ? l.Value
        : Get(name) is NbtInt i ? i.Value
        : fallback;

    /// <summary>新增或替换同名标签（保持原位置，替换时名称沿用）</summary>
    public void Set(NbtTag tag)
    {
        for (var i = 0; i < Items.Count; i++)
        {
            if (!string.Equals(Items[i].Name, tag.Name, StringComparison.Ordinal)) continue;
            Items[i] = tag;
            return;
        }
        Items.Add(tag);
    }
}

public static class Nbt
{
    public const byte TagEnd = 0, TagByte = 1, TagShort = 2, TagInt = 3, TagLong = 4;
    public const byte TagFloat = 5, TagDouble = 6, TagByteArray = 7, TagString = 8;
    public const byte TagList = 9, TagCompound = 10, TagIntArray = 11, TagLongArray = 12;

    // ── gzip 包装（投影文件外层统一 gzip；也容忍未压缩的裸 NBT） ──

    /// <summary>
    /// 读文件。**必须先把 gzip 整体解压到内存再解析**：GZipStream 每次 Read 都要走一遍
    /// 解压器，而 NBT 解析是"逐标量读 2/4/8 字节"，一个 80 万方块的投影光 BlockStates
    /// 就有 65 万个 long——逐值读会变成 65 万次 GZipStream.Read，实测 .NET 8 下 60 秒起步。
    /// 解压到内存后同样的解析只要几十毫秒。
    /// </summary>
    public static NbtCompound ReadFile(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 2 && bytes[0] == 0x1F && bytes[1] == 0x8B)
        {
            using var gz = new GZipStream(new MemoryStream(bytes, writable: false), CompressionMode.Decompress);
            using var plain = new MemoryStream(Math.Max(bytes.Length * 4, 64 * 1024));
            gz.CopyTo(plain);
            plain.Position = 0;
            return Read(plain);
        }
        return Read(new MemoryStream(bytes, writable: false));
    }

    public static void WriteFile(string path, NbtCompound root)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Optimal);
        Write(gz, root);
    }

    // ── 读 ──

    public static NbtCompound Read(Stream stream)
    {
        using var br = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        var type = br.ReadByte();
        if (type != TagCompound) throw new InvalidDataException($"NBT 根标签必须是 Compound（实为 {type}）");
        ReadString(br); // 根名（通常为空）
        return (NbtCompound)ReadPayload(br, TagCompound);
    }

    private static NbtTag ReadPayload(BinaryReader br, byte type) => type switch
    {
        TagByte => new NbtByte { Value = br.ReadByte() },
        TagShort => new NbtShort { Value = BinaryPrimitives.ReadInt16BigEndian(br.ReadBytes(2)) },
        TagInt => new NbtInt { Value = BinaryPrimitives.ReadInt32BigEndian(br.ReadBytes(4)) },
        TagLong => new NbtLong { Value = BinaryPrimitives.ReadInt64BigEndian(br.ReadBytes(8)) },
        TagFloat => new NbtFloat { Value = BinaryPrimitives.ReadSingleBigEndian(br.ReadBytes(4)) },
        TagDouble => new NbtDouble { Value = BinaryPrimitives.ReadDoubleBigEndian(br.ReadBytes(8)) },
        TagByteArray => new NbtByteArray { Value = br.ReadBytes(BinaryPrimitives.ReadInt32BigEndian(br.ReadBytes(4))) },
        TagString => new NbtString { Value = ReadString(br) },
        TagList => ReadList(br),
        TagCompound => ReadCompound(br),
        TagIntArray => ReadIntArray(br),
        TagLongArray => ReadLongArray(br),
        _ => throw new InvalidDataException($"未知 NBT 标签类型 {type}"),
    };

    private static NbtList ReadList(BinaryReader br)
    {
        var itemType = br.ReadByte();
        var count = BinaryPrimitives.ReadInt32BigEndian(br.ReadBytes(4));
        var list = new NbtList { ItemType = itemType };
        for (var i = 0; i < count; i++)
            list.Items.Add(ReadPayload(br, itemType));
        return list;
    }

    private static NbtCompound ReadCompound(BinaryReader br)
    {
        var compound = new NbtCompound();
        while (true)
        {
            var type = br.ReadByte();
            if (type == TagEnd) break;
            var name = ReadString(br);
            var tag = ReadPayload(br, type);
            tag.Name = name;
            compound.Items.Add(tag);
        }
        return compound;
    }

    private static NbtIntArray ReadIntArray(BinaryReader br)
    {
        var count = BinaryPrimitives.ReadInt32BigEndian(br.ReadBytes(4));
        var value = new int[Math.Max(0, count)];
        if (value.Length > 0)
        {
            // 整块读，避免每个元素一次 ReadBytes（大数组下分配与调用开销占主导）
            var raw = br.ReadBytes(value.Length * 4);
            var actual = raw.Length / 4;
            if (actual < value.Length) Array.Resize(ref value, actual);
            for (var i = 0; i < value.Length; i++)
                value[i] = BinaryPrimitives.ReadInt32BigEndian(raw.AsSpan(i * 4, 4));
        }
        return new NbtIntArray { Value = value };
    }

    private static NbtLongArray ReadLongArray(BinaryReader br)
    {
        var count = BinaryPrimitives.ReadInt32BigEndian(br.ReadBytes(4));
        var value = new long[Math.Max(0, count)];
        if (value.Length > 0)
        {
            // 同上：BlockStates 动辄几十万个 long，必须整块读
            var raw = br.ReadBytes(value.Length * 8);
            var actual = raw.Length / 8;
            if (actual < value.Length) Array.Resize(ref value, actual);
            for (var i = 0; i < value.Length; i++)
                value[i] = BinaryPrimitives.ReadInt64BigEndian(raw.AsSpan(i * 8, 8));
        }
        return new NbtLongArray { Value = value };
    }

    private static string ReadString(BinaryReader br)
    {
        var len = BinaryPrimitives.ReadUInt16BigEndian(br.ReadBytes(2));
        return Encoding.UTF8.GetString(br.ReadBytes(len));
    }

    // ── 写 ──

    public static void Write(Stream stream, NbtCompound root)
    {
        using var bw = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        bw.Write(TagCompound);
        WriteString(bw, root.Name);
        WriteCompound(bw, root);
    }

    private static void WriteTag(BinaryWriter bw, NbtTag tag)
    {
        bw.Write(TypeOf(tag));
        WriteString(bw, tag.Name);
        WritePayload(bw, tag);
    }

    private static void WritePayload(BinaryWriter bw, NbtTag tag)
    {
        switch (tag)
        {
            case NbtByte b: bw.Write(b.Value); break;
            case NbtShort s: bw.Write(BinaryPrimitives.ReverseEndianness(s.Value)); break;
            case NbtInt i: bw.Write(BinaryPrimitives.ReverseEndianness(i.Value)); break;
            case NbtLong l: bw.Write(BinaryPrimitives.ReverseEndianness(l.Value)); break;
            case NbtFloat f: bw.Write(BinaryPrimitives.ReverseEndianness(BitConverter.SingleToInt32Bits(f.Value))); break;
            case NbtDouble d: bw.Write(BinaryPrimitives.ReverseEndianness(BitConverter.DoubleToInt64Bits(d.Value))); break;
            case NbtByteArray ba:
                bw.Write(BinaryPrimitives.ReverseEndianness(ba.Value.Length));
                bw.Write(ba.Value);
                break;
            case NbtString str: WriteString(bw, str.Value); break;
            case NbtList list:
                bw.Write(list.Items.Count > 0 ? TypeOf(list.Items[0]) : TagEnd);
                bw.Write(BinaryPrimitives.ReverseEndianness(list.Items.Count));
                foreach (var item in list.Items) WritePayload(bw, item);
                break;
            case NbtCompound compound: WriteCompound(bw, compound); break;
            case NbtIntArray ia:
                bw.Write(BinaryPrimitives.ReverseEndianness(ia.Value.Length));
                foreach (var v in ia.Value) bw.Write(BinaryPrimitives.ReverseEndianness(v));
                break;
            case NbtLongArray la:
                bw.Write(BinaryPrimitives.ReverseEndianness(la.Value.Length));
                foreach (var v in la.Value) bw.Write(BinaryPrimitives.ReverseEndianness(v));
                break;
        }
    }

    private static void WriteCompound(BinaryWriter bw, NbtCompound compound)
    {
        foreach (var item in compound.Items) WriteTag(bw, item);
        bw.Write(TagEnd);
    }

    private static void WriteString(BinaryWriter bw, string value)
    {
        var bytes = Encoding.UTF8.GetBytes(value);
        bw.Write(BinaryPrimitives.ReverseEndianness((ushort)bytes.Length));
        bw.Write(bytes);
    }

    public static byte TypeOf(NbtTag tag) => tag switch
    {
        NbtByte => TagByte,
        NbtShort => TagShort,
        NbtInt => TagInt,
        NbtLong => TagLong,
        NbtFloat => TagFloat,
        NbtDouble => TagDouble,
        NbtByteArray => TagByteArray,
        NbtString => TagString,
        NbtList => TagList,
        NbtCompound => TagCompound,
        NbtIntArray => TagIntArray,
        NbtLongArray => TagLongArray,
        _ => throw new InvalidDataException($"无法序列化的 NBT 类型 {tag.GetType().Name}"),
    };
}
