// SwapChainPanel 原生互操作：把 D3D11 合成交换链挂到 WinUI 3 的 SwapChainPanel 上。
using System.Runtime.InteropServices;

namespace Starcloud.App.Views;

[ComImport]
[Guid("63aad0b8-7c24-40ff-85a8-640d944cc325")]
[InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
internal interface ISwapChainPanelNative
{
    void SetSwapChain([MarshalAs(UnmanagedType.IUnknown)] object swapChain);
}
