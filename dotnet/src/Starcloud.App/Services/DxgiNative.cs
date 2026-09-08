// DXGI 独占全屏检测（设计：nova-overlay-plan.md 风险表——精确判据，失败降级为「不隐藏」）。
// IDXGIOutput::GetFullscreenState 返回该显示器是否被独占全屏应用占用；
// 无边框全屏（borderless）不会命中——浮窗 TOPMOST 仍可见，不误伤。
// 注意：COM 接口 GUID 必须真实（RCW cast 走 QueryInterface），vtable 槽位序按 dxgi.h。

using System.Runtime.InteropServices;

namespace Starcloud.App.Services;

internal static class DxgiNative
{
    public static bool IsAnyExclusiveFullscreen()
    {
        try
        {
            // 用 IDXGIFactory 基础接口即可（CreateDXGIFactory1 支持创建任意 IDXGIFactory* 变体）
            if (CreateDXGIFactory1(FactoryIid, out var factoryObj) != 0) return false;
            var factory = (Factory)factoryObj;

            for (var ai = 0u; ; ai++)
            {
                if (factory.EnumAdapters(ai, out var adapterObj) != 0) break;
                var adapter = (Adapter)adapterObj;
                for (var oi = 0u; ; oi++)
                {
                    if (adapter.EnumOutputs(oi, out var outputObj) != 0) break;
                    var output = (Output)outputObj;
                    output.GetFullscreenState(out var fullscreen, IntPtr.Zero);
                    if (fullscreen != 0) return true; // 有独占全屏应用
                }
            }
            return false;
        }
        catch
        {
            return false; // 检测失败 = 不隐藏（宁可漏判不误判）
        }
    }

    private static readonly Guid FactoryIid = new("ae02eedb-c735-4690-8d12-5a84202caaa5"); // IDXGIFactory
    private static readonly Guid AdapterIid = new("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"); // IDXGIAdapter

    [ComImport, Guid("ae02eedb-c735-4690-8d12-5a84202caaa5"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Factory
    {
        // IDXGIObject（IUnknown 由 RCW 隐式处理，声明从第 4 槽开始）
        [PreserveSig] int SetPrivateData(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
        [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int GetParent(IntPtr a, IntPtr b);
        // IDXGIFactory
        [PreserveSig] int EnumAdapters(uint index,
            [MarshalAs(UnmanagedType.Interface)] out object adapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr a, IntPtr b);
        [PreserveSig] int GetWindowAssociation(IntPtr a);
        [PreserveSig] int CreateSwapChain(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr a, IntPtr b);
    }

    [ComImport, Guid("2411e7e1-12ac-4ccf-bd14-9798e8534dc0"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Adapter
    {
        // IDXGIObject
        [PreserveSig] int SetPrivateData(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
        [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int GetParent(IntPtr a, IntPtr b);
        // IDXGIAdapter
        [PreserveSig] int EnumOutputs(uint index,
            [MarshalAs(UnmanagedType.Interface)] out object output);
        [PreserveSig] int GetDesc(IntPtr a);
        [PreserveSig] int CheckInterfaceSupport(IntPtr a, IntPtr b);
    }

    [ComImport, Guid("9d8e1289-d7b3-4d7f-9b06-2608e2b0d0bc"), // IDXGIOutput
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface Output
    {
        // IDXGIObject
        [PreserveSig] int SetPrivateData(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int SetPrivateDataInterface(IntPtr a, IntPtr b);
        [PreserveSig] int GetPrivateData(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int GetParent(IntPtr a, IntPtr b);
        // IDXGIOutput
        [PreserveSig] int GetDesc(IntPtr a);
        [PreserveSig] int GetDisplayModeList(IntPtr a, IntPtr b, IntPtr c, IntPtr d);
        [PreserveSig] int FindClosestMatchingMode(IntPtr a, IntPtr b, IntPtr c);
        [PreserveSig] int WaitForVBlank();
        [PreserveSig] int TakeOwnership(IntPtr a, IntPtr b);
        [PreserveSig] int ReleaseOwnership();
        [PreserveSig] int GetFullscreenState(out int fullscreen, IntPtr target);
    }

    [DllImport("dxgi.dll")]
    private static extern int CreateDXGIFactory1(Guid riid,
        [MarshalAs(UnmanagedType.IUnknown)] out object factory);
}
