// 系统「另存为」对话框（Win32 IFileSaveDialog）。
//
// 为什么不用 WinUI 的 FileSavePicker：桌面版它没有「初始目录」能力（SuggestedStartFolder
// 是 UWP 独有），而「另存 Mod」必须默认落在目标版本的 mods 文件夹里。
// IFileSaveDialog 自 Vista 起就有 SetFolder，正是干这个的；COM 声明只覆盖用到的槽位。

using System.Runtime.InteropServices;
using System.Text;
using Starcloud.Core.Services;

namespace Starcloud.App.Services;

internal static class SystemSaveFileDialog
{
    private const uint FosOverwritePrompt = 0x2;
    private const uint FosForceFileSystem = 0x40;
    private const uint SigdnFilesysPath = 0x80058000;
    private static readonly Guid IidShellItem = new("43826D1E-E718-42EE-BC55-A1E261C37BFE");

    /// <summary>
    /// 弹出系统另存为对话框。
    /// </summary>
    /// <param name="owner">宿主窗口句柄（居中/模态用）</param>
    /// <param name="title">对话框标题；空串用系统默认</param>
    /// <param name="fileName">预填文件名</param>
    /// <param name="defaultExtension">默认扩展名（如 ".jar"，不带点也行）</param>
    /// <param name="initialDirectory">初始目录；打不开时退到系统记住的上次位置</param>
    /// <returns>用户确认的完整路径；取消返回 null</returns>
    public static string? Show(IntPtr owner, string title, string fileName,
        string defaultExtension, string initialDirectory)
    {
        IFileSaveDialog dialog;
        try
        {
            dialog = (IFileSaveDialog)new FileSaveDialogRcw();
        }
        catch (COMException ex) when (ex.HResult == unchecked((int)0x80040154))
        {
            // 个别系统（如部分 Insider 构建）没注册 FileSaveDialog 组件：
            // 退回 comdlg32 的旧式对话框 —— 观感旧一点，但 lpstrInitialDir 同样生效
            return ShowLegacy(owner, title, fileName, defaultExtension, initialDirectory);
        }
        dialog.GetOptions(out var options);
        dialog.SetOptions(options | FosOverwritePrompt | FosForceFileSystem);

        if (!string.IsNullOrWhiteSpace(initialDirectory))
        {
            try
            {
                var iid = IidShellItem;
                var hr = SHCreateItemFromParsingName(initialDirectory, IntPtr.Zero,
                    ref iid, out var folder);
                if (hr == 0 && folder is not null)
                {
                    // SetFolder 是「打开时停在哪」；部分新构建的通用对话框只认
                    // SetDefaultFolder（用户上次位置优先的旧行为），两个都设最稳。
                    dialog.SetFolder(folder);
                    dialog.SetDefaultFolder(folder);
                }
                else
                {
                    LauncherLog.Write("download",
                        $"另存为初始目录无法解析（hr=0x{hr:X8}）：{initialDirectory}");
                }
            }
            catch (Exception ex)
            {
                // 目录打不开（还没建好/被删）就让对话框用默认位置，不拦用户
                LauncherLog.Write("download", $"另存为初始目录设置失败：{ex.Message}");
            }
        }
        if (!string.IsNullOrWhiteSpace(fileName)) dialog.SetFileName(fileName);
        if (!string.IsNullOrWhiteSpace(defaultExtension)) dialog.SetDefaultExtension(defaultExtension);
        if (!string.IsNullOrWhiteSpace(title)) dialog.SetTitle(title);

        if (dialog.Show(owner) != 0) return null;   // 取消 / 关闭
        dialog.GetResult(out var result);
        if (result is null) return null;
        result.GetDisplayName(SigdnFilesysPath, out var text);
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out IShellItem? ppv);

    // ── 旧式对话框（comdlg32）：IFileSaveDialog 组件缺失时的兜底 ──

    private const uint OfnOverwritePrompt = 0x2;
    private const uint OfnHideReadOnly = 0x4;
    private const uint OfnPathMustExist = 0x800;

    [DllImport("comdlg32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSaveFileNameW(ref OpenFileNameW lpofn);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct OpenFileNameW
    {
        public int LStructSize;
        public IntPtr HwndOwner;
        public IntPtr HInstance;
        public IntPtr LpstrFilter;
        public IntPtr LpstrCustomFilter;
        public int NMaxCustFilter;
        public int NFilterIndex;
        public IntPtr LpstrFile;
        public int NMaxFile;
        public IntPtr LpstrFileTitle;
        public int NMaxFileTitle;
        public IntPtr LpstrInitialDir;
        public IntPtr LpstrTitle;
        public uint Flags;
        public ushort NFileOffset;
        public ushort NFileExtension;
        public IntPtr LpstrDefExt;
        public IntPtr LCustData;
        public IntPtr LpfnHook;
        public IntPtr LpTemplateName;
        public IntPtr PvReserved;
        public uint DwReserved;
        public uint FlagsEx;
    }

    /// <summary>comdlg32 旧式另存为：观感老，但 lpstrInitialDir 可靠生效。</summary>
    private static string? ShowLegacy(IntPtr owner, string title, string fileName,
        string defaultExtension, string initialDirectory)
    {
        // 双 NUL 结尾的成对过滤器（名称\0模式\0…\0\0），必须走 HGlobal 手工排版
        var filter = Encoding.Unicode.GetBytes(
            string.IsNullOrEmpty(defaultExtension)
                ? "所有文件\0*.*\0\0"
                : $"Mod 文件\0*{FormatExtension(defaultExtension)}\0所有文件\0*.*\0\0");
        var filterPtr = Marshal.AllocHGlobal(filter.Length);
        var filePtr = Marshal.AllocHGlobal(1040);   // 520 字符，足够 MAX_PATH+扩展
        var titlePtr = IntPtr.Zero;
        var dirPtr = IntPtr.Zero;
        var extPtr = IntPtr.Zero;
        try
        {
            Marshal.Copy(filter, 0, filterPtr, filter.Length);
            // 老式对话框：lpstrInitialDir 在部分系统上会被「上次位置」盖掉，
            // 把文件名直接写成完整路径（目录 + 文件名）就能稳稳定位到目标目录。
            var initialName = string.IsNullOrWhiteSpace(initialDirectory)
                ? fileName
                : Path.Combine(initialDirectory, fileName);
            var name = Encoding.Unicode.GetBytes(initialName + "\0");
            Marshal.Copy(name, 0, filePtr, Math.Min(name.Length, 1038));

            if (!string.IsNullOrWhiteSpace(title))
            {
                var t = Encoding.Unicode.GetBytes(title + "\0");
                titlePtr = Marshal.AllocHGlobal(t.Length);
                Marshal.Copy(t, 0, titlePtr, t.Length);
            }
            if (!string.IsNullOrWhiteSpace(initialDirectory))
            {
                var d = Encoding.Unicode.GetBytes(initialDirectory + "\0");
                dirPtr = Marshal.AllocHGlobal(d.Length);
                Marshal.Copy(d, 0, dirPtr, d.Length);
            }
            if (!string.IsNullOrWhiteSpace(defaultExtension))
            {
                var e = Encoding.Unicode.GetBytes(FormatExtension(defaultExtension) + "\0");
                extPtr = Marshal.AllocHGlobal(e.Length);
                Marshal.Copy(e, 0, extPtr, e.Length);
            }

            var ofn = new OpenFileNameW
            {
                LStructSize = Marshal.SizeOf<OpenFileNameW>(),
                HwndOwner = owner,
                LpstrFilter = filterPtr,
                LpstrFile = filePtr,
                NMaxFile = 520,
                LpstrInitialDir = dirPtr,
                LpstrTitle = titlePtr,
                Flags = OfnOverwritePrompt | OfnHideReadOnly | OfnPathMustExist,
                LpstrDefExt = extPtr,
            };
            return GetSaveFileNameW(ref ofn) ? Marshal.PtrToStringUni(filePtr) : null;
        }
        finally
        {
            Marshal.FreeHGlobal(filterPtr);
            Marshal.FreeHGlobal(filePtr);
            if (titlePtr != IntPtr.Zero) Marshal.FreeHGlobal(titlePtr);
            if (dirPtr != IntPtr.Zero) Marshal.FreeHGlobal(dirPtr);
            if (extPtr != IntPtr.Zero) Marshal.FreeHGlobal(extPtr);
        }
    }

    private static string FormatExtension(string ext)
    {
        var t = ext.Trim().TrimStart('.');
        return string.IsNullOrEmpty(t) ? "*.*" : "." + t;
    }

    [ComImport, Guid("C0B4E2F3-0538-486C-AE49-8EF8E8ECCCB1")]
    private class FileSaveDialogRcw   // 不能 sealed：要对它做接口转换（运行时是 COM QI）
    {
    }

    [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IShellItem ppsi);
        void GetDisplayName(uint sigdnName, [MarshalAs(UnmanagedType.LPWStr)] out string ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IShellItem psi, uint hint, out int piOrder);
    }

    /// <summary>槽位必须与 shobjidl_core.idl 完全一致：IModalWindow.Show + IFileDialog 全量 + IFileSaveDialog。</summary>
    [ComImport, Guid("84BCCD23-5FDE-4CDB-AEA4-AF64B83D78AB"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IFileSaveDialog
    {
        // IModalWindow
        [PreserveSig]
        int Show(IntPtr hwndOwner);

        // IFileDialog
        void SetFileTypes(uint cFileTypes, IntPtr rgFilterSpec);
        void SetFileTypeIndex(uint iFileType);
        void GetFileTypeIndex(out uint piFileType);
        void Advise(IntPtr pfde, out uint pdwCookie);
        void Unadvise(uint dwCookie);
        void SetOptions(uint fos);
        void GetOptions(out uint pfos);
        void SetDefaultFolder(IShellItem psi);
        void SetFolder(IShellItem psi);
        void GetFolder(out IShellItem ppsi);
        void GetCurrentSelection(out IShellItem ppsi);
        void SetFileName([MarshalAs(UnmanagedType.LPWStr)] string pszName);
        void GetTitle([MarshalAs(UnmanagedType.LPWStr)] out string pszName);
        void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string pszTitle);
        void SetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] string pszText);
        void GetOkButtonLabel([MarshalAs(UnmanagedType.LPWStr)] out string pszText);
        void SetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] string pszLabel);
        void GetFileNameLabel([MarshalAs(UnmanagedType.LPWStr)] out string pszLabel);
        void SetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] string pszDefaultExtension);
        void GetDefaultExtension([MarshalAs(UnmanagedType.LPWStr)] out string pszDefaultExtension);
        void AddPlace(IShellItem psi, int fdap);
        void SetClientGuid(ref Guid guid);
        void ClearClientData();
        void SetFilter(IntPtr pFilter);

        // IFileSaveDialog
        void SetCollectedProperties(IntPtr pStore, int fApplyDefault);
        void SetProperties(IntPtr pStore);
        void ApplyProperties(IShellItem psi, IntPtr pStore, IntPtr hwnd, IntPtr pSink);
        void GetProperties(out IntPtr ppStore);
        void ApplyPropertiesToSelectedItems(IntPtr pStore, IntPtr hwnd);
        void GetResult(out IShellItem ppsi);
    }
}
