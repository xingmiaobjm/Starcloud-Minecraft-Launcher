// Media Foundation 互操作（录屏编码用）：只声明本项目用到的那一小部分。
//
// 为什么要手写：MF 没有 .NET 投影，而录屏必须走硬件编码器（NVENC / QSV / AMF 注册的
// H.264 MFT），这就要求 MF_SINK_WRITER + MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS 这套原生属性。
//
// 约定：所有方法按 COM 的 vtable 顺序声明（名字不参与绑定，顺序才是），
// 因此每个接口都必须从 IUnknown 起把所有前置方法补齐，不能只写"我要用的那个"。

using System.Runtime.InteropServices;
using System.Text;

namespace Starcloud.App.Services.Recording;

internal static class MfGuids
{
    public static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    public static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    public static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    public static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    public static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    public static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    public static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    public static readonly Guid MF_MT_DEFAULT_STRIDE = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    public static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    public static readonly Guid MFVideoFormat_ARGB32 = new("00000015-0000-0010-8000-00aa00389b71");

    public static readonly Guid MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS = new("a634a91c-822b-41b9-a494-4de4643612b0");
    public static readonly Guid MF_SINK_WRITER_DISABLE_THROTTLING = new("08b845d8-2b74-4afe-9d53-be16d2d5ae4f");
    public static readonly Guid MF_LOW_LATENCY = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    public static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");

    public const int MFVideoInterlace_Progressive = 2;
    public const int MFSTARTUP_FULL = 0;
    public const uint MF_VERSION = 0x00020070;
}

[ComImport, Guid("2CD2D921-C447-44A7-A13C-4ADABFC247E3"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFAttributes
{
    int GetItem(ref Guid guidKey, IntPtr pValue);
    int GetItemType(ref Guid guidKey, out int pType);
    int CompareItem(ref Guid guidKey, IntPtr value, out bool pbResult);
    int Compare(IMFAttributes pTheirs, int matchType, out bool pbResult);
    int GetUINT32(ref Guid guidKey, out uint punValue);
    int GetUINT64(ref Guid guidKey, out ulong punValue);
    int GetDouble(ref Guid guidKey, out double pfValue);
    int GetGUID(ref Guid guidKey, out Guid pguidValue);
    int GetStringLength(ref Guid guidKey, out uint pcchLength);
    int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue,
        uint cchBufSize, out uint pcchLength);
    int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
    int GetBlob(ref Guid guidKey, out IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
    int SetItem(ref Guid guidKey, IntPtr value);
    int DeleteItem(ref Guid guidKey);
    int DeleteAllItems();
    int SetUINT32(ref Guid guidKey, uint unValue);
    int SetUINT64(ref Guid guidKey, ulong unValue);
    int SetDouble(ref Guid guidKey, double fValue);
    int SetGUID(ref Guid guidKey, ref Guid guidValue);
    int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
    int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    int LockStore();
    int UnlockStore();
    int GetCount(out uint pcItems);
    int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    int CopyAllItems(IMFAttributes pDest);
}

// ⚠ 下面两个接口把 IMFAttributes 的 30 个方法**抄了一遍**，而不是写成 `: IMFAttributes`。
//
// 原因（踩过一次才知道）：COM 的派生接口 vtable 里基接口方法排在前面，但 C# 的 ComImport
// 接口继承在这台机器上没有按这个规则排——结果 IMFSample.AddBuffer 落到了
// IMFAttributes.GetString 的槽位上，报 MF_E_ATTRIBUTENOTFOUND (0xC00D36E6)，
// 报错点看起来像"加缓冲区失败"，实际是槽位错位。
// 显式展开后，无论运行时是否自动展开基接口方法，槽位都一致，不会再错位。

[ComImport, Guid("44AE0FA8-EA31-4109-8D2E-4CAE4997C555"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaType
{
    int GetItem(ref Guid guidKey, IntPtr pValue);
    int GetItemType(ref Guid guidKey, out int pType);
    int CompareItem(ref Guid guidKey, IntPtr value, out bool pbResult);
    int Compare(IMFAttributes pTheirs, int matchType, out bool pbResult);
    int GetUINT32(ref Guid guidKey, out uint punValue);
    int GetUINT64(ref Guid guidKey, out ulong punValue);
    int GetDouble(ref Guid guidKey, out double pfValue);
    int GetGUID(ref Guid guidKey, out Guid pguidValue);
    int GetStringLength(ref Guid guidKey, out uint pcchLength);
    int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue,
        uint cchBufSize, out uint pcchLength);
    int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
    int GetBlob(ref Guid guidKey, out IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
    int SetItem(ref Guid guidKey, IntPtr value);
    int DeleteItem(ref Guid guidKey);
    int DeleteAllItems();
    int SetUINT32(ref Guid guidKey, uint unValue);
    int SetUINT64(ref Guid guidKey, ulong unValue);
    int SetDouble(ref Guid guidKey, double fValue);
    int SetGUID(ref Guid guidKey, ref Guid guidValue);
    int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
    int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    int LockStore();
    int UnlockStore();
    int GetCount(out uint pcItems);
    int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    int CopyAllItems(IMFAttributes pDest);
    int GetMajorType(out Guid pguidMajorType);
    int IsCompressedFormat(out bool pfCompressed);
    int IsEqual(IMFMediaType pIMediaType, out uint pdwFlags);
    int GetRepresentation(Guid guidRepresentation, out IntPtr ppvRepresentation);
    int FreeRepresentation(Guid guidRepresentation, IntPtr pvRepresentation);
}

[ComImport, Guid("045FA593-8799-42B8-BC8D-8968C6453507"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFMediaBuffer
{
    int Lock(out IntPtr ppbBuffer, out uint pcbMaxLength, out uint pcbCurrentLength);
    int Unlock();
    int GetCurrentLength(out uint pcbCurrentLength);
    int SetCurrentLength(uint cbCurrentLength);
    int GetMaxLength(out uint pcbMaxLength);
}

// 同上：基接口方法显式展开，绝不依赖接口继承的 vtable 排列
[ComImport, Guid("C40A00F2-B93A-4D80-AE8C-5A1C634F58E4"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSample
{
    int GetItem(ref Guid guidKey, IntPtr pValue);
    int GetItemType(ref Guid guidKey, out int pType);
    int CompareItem(ref Guid guidKey, IntPtr value, out bool pbResult);
    int Compare(IMFAttributes pTheirs, int matchType, out bool pbResult);
    int GetUINT32(ref Guid guidKey, out uint punValue);
    int GetUINT64(ref Guid guidKey, out ulong punValue);
    int GetDouble(ref Guid guidKey, out double pfValue);
    int GetGUID(ref Guid guidKey, out Guid pguidValue);
    int GetStringLength(ref Guid guidKey, out uint pcchLength);
    int GetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] StringBuilder pwszValue,
        uint cchBufSize, out uint pcchLength);
    int GetAllocatedString(ref Guid guidKey, out IntPtr ppwszValue, out uint pcchLength);
    int GetBlobSize(ref Guid guidKey, out uint pcbBlobSize);
    int GetBlob(ref Guid guidKey, out IntPtr pBuf, uint cbBufSize, out uint pcbBlobSize);
    int GetAllocatedBlob(ref Guid guidKey, out IntPtr ppBuf, out uint pcbSize);
    int GetUnknown(ref Guid guidKey, ref Guid riid, out IntPtr ppv);
    int SetItem(ref Guid guidKey, IntPtr value);
    int DeleteItem(ref Guid guidKey);
    int DeleteAllItems();
    int SetUINT32(ref Guid guidKey, uint unValue);
    int SetUINT64(ref Guid guidKey, ulong unValue);
    int SetDouble(ref Guid guidKey, double fValue);
    int SetGUID(ref Guid guidKey, ref Guid guidValue);
    int SetString(ref Guid guidKey, [MarshalAs(UnmanagedType.LPWStr)] string wszValue);
    int SetBlob(ref Guid guidKey, byte[] pBuf, uint cbBufSize);
    int SetUnknown(ref Guid guidKey, [MarshalAs(UnmanagedType.IUnknown)] object pUnknown);
    int LockStore();
    int UnlockStore();
    int GetCount(out uint pcItems);
    int GetItemByIndex(uint unIndex, out Guid pguidKey, IntPtr pValue);
    int CopyAllItems(IMFAttributes pDest);
    int GetSampleFlags(out uint pdwSampleFlags);
    int SetSampleFlags(uint dwSampleFlags);
    int GetSampleTime(out long phnsSampleTime);
    int SetSampleTime(long hnsSampleTime);
    int GetSampleDuration(out long phnsSampleDuration);
    int SetSampleDuration(long hnsSampleDuration);
    int GetBufferCount(out uint pdwBufferCount);
    int GetBufferByIndex(uint dwIndex, out IMFMediaBuffer ppBuffer);
    int ConvertToContiguousBuffer(out IMFMediaBuffer ppBuffer);
    int AddBuffer(IMFMediaBuffer pBuffer);
    int RemoveBufferByIndex(uint dwIndex);
    int RemoveAllBuffers();
    int GetTotalLength(out uint pcbTotalLength);
    int CopyToBuffer(IMFMediaBuffer pBuffer);
}

[ComImport, Guid("3137F1CD-FE5E-4805-A5D8-FB477448CB3D"),
 InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface IMFSinkWriter
{
    int AddStream(IMFMediaType pTargetMediaType, out uint pdwStreamIndex);
    int SetInputMediaType(uint dwStreamIndex, IMFMediaType pInputMediaType,
        IMFAttributes? pEncodingParameters);
    int BeginWriting();
    int WriteSample(uint dwStreamIndex, IMFSample pSample);
    int SendStreamTick(uint dwStreamIndex, long llTimestamp);
    int PlaceMarker(uint dwStreamIndex, IntPtr pvContext);
    int NotifyEndOfSegment(uint dwStreamIndex);
    int Flush(uint dwStreamIndex);
    // COM 里叫 Finalize；ComImport 按 vtable 顺序绑定，名字不参与，改名只为避开 object.Finalize
    int FinishWriting();
    int GetServiceForStream(uint dwStreamIndex, ref Guid guidService, ref Guid riid, out IntPtr ppvObject);
    int GetStatistics(uint dwStreamIndex, IntPtr pStats);
}

/// <summary>MFT 枚举项（只用于把"当前这台机器有哪些硬件编码器"如实报给用户看）</summary>
internal sealed record MftInfo(string Name, bool Hardware);

internal static class MediaFoundation
{
    private const string MfPlat = "mfplat.dll";
    private const string MfReadWrite = "mfreadwrite.dll";

    [DllImport(MfPlat, ExactSpelling = true)]
    public static extern int MFStartup(uint version, int dwFlags);

    [DllImport(MfPlat, ExactSpelling = true)]
    public static extern int MFShutdown();

    [DllImport(MfPlat, ExactSpelling = true)]
    public static extern int MFCreateAttributes(out IMFAttributes ppMFAttributes, uint cInitialSize);

    [DllImport(MfPlat, ExactSpelling = true)]
    public static extern int MFCreateMediaType(out IMFMediaType ppMFType);

    [DllImport(MfPlat, ExactSpelling = true)]
    public static extern int MFCreateMemoryBuffer(uint cbMaxLength, out IMFMediaBuffer ppBuffer);

    [DllImport(MfPlat, ExactSpelling = true)]
    public static extern int MFCreateSample(out IMFSample ppIMFSample);

    // Sink Writer 住在 mfreadwrite.dll，不在 mfplat.dll（按 DLL 找错了会 EntryPointNotFound）
    [DllImport(MfReadWrite, ExactSpelling = true, CharSet = CharSet.Unicode)]
    public static extern int MFCreateSinkWriterFromURL(string? pwszOutputURL, IntPtr pByteStream,
        IMFAttributes? pAttributes, out IMFSinkWriter ppSinkWriter);

    [DllImport(MfPlat, ExactSpelling = true)]
    public static extern int MFTEnumEx(Guid guidCategory, uint flags, IntPtr pInputType,
        IntPtr pOutputType, out IntPtr pppMFTActivate, out uint pnumMFTActivate);

    /// <summary>HRESULT 失败即抛（消息里带 16 进制码，便于对照 MF 文档定位）</summary>
    public static void Check(int hr, string what)
    {
        if (hr >= 0) return;
        throw new InvalidOperationException($"{what} 失败：0x{hr:X8}");
    }

    // 入参收 object：MFMediaType/MFSample 现在不再继承 IMFAttributes（见上面的说明），
    // 但底层 COM 对象本来就同时实现基接口，这里显式 QI 一次即可，槽位仍然正确。
    private static IMFAttributes AsAttributes(object target) =>
        target as IMFAttributes ?? (IMFAttributes)target;

    public static void SetGuid(object target, Guid key, Guid value)
    {
        var attrs = AsAttributes(target);
        var k = key;
        Check(attrs.SetGUID(ref k, ref value), "SetGUID");
    }

    public static void SetUInt32(object target, Guid key, uint value)
    {
        var attrs = AsAttributes(target);
        var k = key;
        Check(attrs.SetUINT32(ref k, value), "SetUINT32");
    }

    /// <summary>按 MF 惯例写"两个 32 位打包进一个 UINT64"的属性（尺寸 / 帧率 / 像素宽高比）</summary>
    public static void SetPackedPair(object target, Guid key, uint high, uint low)
    {
        var attrs = AsAttributes(target);
        var k = key;
        Check(attrs.SetUINT64(ref k, ((ulong)high << 32) | low), "SetUINT64");
    }

    /// <summary>枚举指定类别里带硬件标记的 MFT，取展示名（拿不到名字就退回 CLSID）</summary>
    public static IReadOnlyList<MftInfo> EnumerateHardwareTransforms(Guid category)
    {
        var result = new List<MftInfo>();
        // MFT_ENUM_FLAG_HARDWARE = 0x4，MFT_ENUM_FLAG_SORTANDFILTER = 0x40
        var hr = MFTEnumEx(category, 0x4 | 0x40, IntPtr.Zero, IntPtr.Zero, out var ptr, out var count);
        if (hr < 0 || ptr == IntPtr.Zero || count == 0) return result;

        try
        {
            for (uint i = 0; i < count; i++)
            {
                var item = Marshal.ReadIntPtr(ptr, (int)i * IntPtr.Size);
                if (item == IntPtr.Zero) continue;
                try
                {
                    var activate = (IMFAttributes)Marshal.GetObjectForIUnknown(item);
                    var key = MfGuids.MFT_FRIENDLY_NAME_Attribute;
                    if (activate.GetAllocatedString(ref key, out var pName, out _) < 0 || pName == IntPtr.Zero)
                        continue;
                    try
                    {
                        result.Add(new MftInfo(Marshal.PtrToStringUni(pName) ?? "?", true));
                    }
                    finally
                    {
                        Marshal.FreeCoTaskMem(pName);
                    }
                }
                catch
                {
                    // 单个枚举项异常不影响其余
                }
                finally
                {
                    Marshal.Release(item);
                }
            }
        }
        finally
        {
            Marshal.FreeCoTaskMem(ptr);
        }

        return result;
    }
}
