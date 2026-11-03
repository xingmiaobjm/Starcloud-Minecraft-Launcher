// 游戏画面抓取：找 Minecraft 窗口 → GDI 抓一帧 → 缩到指定宽度 → JPEG。
//
// 为什么用 GDI 而不是 Windows.Graphics.Capture：这里是「问一下 AI」的单帧快照，
// 不需要合成器管线、不需要 UI 线程；GDI BitBlt 对窗口化的 OpenGL 游戏（MC 默认窗口化）
// 直接可用，延迟也只有毫秒级。独占全屏时浮窗本来就隐藏，不在这里处理。
//
// JPEG 编码走 Win2D（项目已引用 Microsoft.Graphics.Win2D）：CanvasBitmap.SaveAsync
// 直接支持 JPEG 质量参数，省掉一个 System.Drawing.Common 依赖。

using System.Runtime.InteropServices;
using System.Text;
using Starcloud.Core.Services;
using Windows.Foundation;
using Windows.Foundation.Collections;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Starcloud.App.Services;

public static class GameCaptureService
{
    /// <summary>找到的游戏窗口（标题 / 屏幕矩形，物理像素）。</summary>
    public sealed record GameWindowInfo(IntPtr Hwnd, string Title, int X, int Y, int Width, int Height);

    /// <summary>
    /// 找 Minecraft 窗口：标题含 "Minecraft"（Java 版窗口标题形如「Minecraft 1.21.4 - 世界名」，
    /// 基岩版就是 "Minecraft"），或者类名 GLFW30（MC 1.13+ 的窗口类）。
    /// </summary>
    public static GameWindowInfo? FindGameWindow()
    {
        GameWindowInfo? found = null;
        var myPid = (uint)Environment.ProcessId;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h)) return true;
            GetWindowThreadProcessId(h, out var pid);
            if (pid == myPid) return true;   // 不抓自己（启动器 / 浮窗）

            var title = new StringBuilder(256);
            GetWindowTextW(h, title, 256);
            var cls = new StringBuilder(64);
            GetClassNameW(h, cls, 64);
            var text = title.ToString();
            // 先认窗口类：MC 1.13+ 是 GLFW（类名 GLFW30），1.12 及更早是 LWJGL。
            // 标题必须**以** Minecraft 开头 —— 浏览器标签页标题里经常含 "Minecraft"
            // （实测把 Edge 的「…Minecraft 启动器… bilibili」当成游戏截了）。
            var isGame = cls.ToString() is "GLFW30" or "LWJGL"
                || text.StartsWith("Minecraft", StringComparison.OrdinalIgnoreCase);
            if (!isGame) return true;

            var r = new RECT();
            if (!GetWindowRect(h, out r)) return true;
            var w = r.Right - r.Left;
            var hh = r.Bottom - r.Top;
            if (w <= 0 || hh <= 0) return true;

            found = new GameWindowInfo(h, text, r.Left, r.Top, w, hh);
            return false;   // 找到就停
        }, IntPtr.Zero);
        return found;
    }

    /// <summary>默认长边 = 1080P（1920），单图体积上限 1.2 MB。</summary>
    public const int DefaultLongSide = 1920;
    public const long DefaultMaxBytes = 1_200_000;

    /// <summary>
    /// 抓一帧画面。优先游戏窗口；没有游戏窗口时抓主显示器（方便在桌面上试效果）。
    /// <para>压缩策略：长边缩到 1080P（再大对模型没增益，反而贵），JPEG 质量从 0.72 起，
    /// 超过体积上限就逐档降到 0.42 —— 「越小越好，只要模型能看清」。</para>
    /// 返回 (JPEG 字节, 来源描述)；失败返回 null。
    /// </summary>
    public static async Task<(byte[] Jpeg, string Source)?> CaptureAsync(
        int maxLongSide = DefaultLongSide, long maxBytes = DefaultMaxBytes)
    {
        GameWindowInfo? game = null;
        try { game = FindGameWindow(); }
        catch (Exception ex) { LauncherLog.Write("novacap", $"找游戏窗口失败：{ex.Message}"); }

        int x, y, w, h;
        string source;
        if (game is { } g)
        {
            (x, y, w, h) = (g.X, g.Y, g.Width, g.Height);
            source = g.Title;
        }
        else
        {
            x = GetSystemMetrics(SM_XVIRTUALSCREEN);
            y = GetSystemMetrics(SM_YVIRTUALSCREEN);
            w = GetSystemMetrics(SM_CXVIRTUALSCREEN);
            h = GetSystemMetrics(SM_CYVIRTUALSCREEN);
            source = "桌面";
        }
        if (w <= 0 || h <= 0)
        {
            LauncherLog.Write("novacap", $"画面尺寸无效 {w}x{h}");
            return null;
        }
        LauncherLog.Write("novacap", game is null
            ? $"未找到 Minecraft 窗口，抓桌面 {w}x{h}"
            : $"窗口「{game.Title}」{w}x{h}");

        // 缩放目标：长边不超过 maxLongSide（1080P），保持比例、不放大
        var scale = Math.Min(1.0, maxLongSide / (double)Math.Max(w, h));
        var outW = Math.Max(1, (int)(w * scale));
        var outH = Math.Max(1, (int)(h * scale));

        byte[]? pixels = null;
        LauncherLog.Write("novacap", $"开始 GDI 抓屏 {outW}x{outH}…");
        try
        {
            await Task.Run(() => pixels = GrabBgra(x, y, w, h, outW, outH));
        }
        catch (Exception ex)
        {
            LauncherLog.Write("novacap", $"GDI 抓屏异常：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
        LauncherLog.Write("novacap", $"GDI 完成：{pixels?.Length ?? -1} 字节");
        if (pixels is null)
        {
            LauncherLog.Write("novacap", "抓屏失败：GDI StretchBlt / GetDIBits 返回空");
            return null;
        }

        try
        {
            // 编码走 WIC（BitmapEncoder，纯 CPU）：Win2D 的 CanvasDevice/SaveAsync 在
            // 没有渲染循环的浮窗线程上会一直挂着（实测 GDI 拿到像素后就没有下文）。
            var quality = 0.72f;
            byte[] bytes;
            while (true)
            {
                using var stream = new InMemoryRandomAccessStream();
                var props = new BitmapPropertySet
                {
                    { "ImageQuality", new BitmapTypedValue(quality, PropertyType.Single) },
                };
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, props);
                encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    (uint)outW, (uint)outH, 96, 96, pixels);
                await encoder.FlushAsync();
                bytes = new byte[stream.Size];
                stream.Seek(0);
                using var reader = new DataReader(stream);
                await reader.LoadAsync((uint)stream.Size);
                reader.ReadBytes(bytes);
                if (bytes.LongLength <= maxBytes || quality <= 0.42f) break;
                quality -= 0.1f;
            }
            LauncherLog.Write("novacap",
                $"截图 {outW}x{outH}，{bytes.Length / 1024.0:F0} KB，q={quality:F2}，源={source}");
            return (bytes, source);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("novacap", $"编码截图失败：{ex.Message}");
            return null;
        }
    }

    /// <summary>GDI 抓屏（StretchBlt 直接缩放到目标尺寸），输出 top-down BGRA。</summary>
    private static byte[]? GrabBgra(int srcX, int srcY, int srcW, int srcH, int outW, int outH)
    {
        IntPtr screenDc = IntPtr.Zero, memDc = IntPtr.Zero, bmp = IntPtr.Zero, old = IntPtr.Zero;
        try
        {
            screenDc = GetDC(IntPtr.Zero);
            memDc = CreateCompatibleDC(screenDc);
            bmp = CreateCompatibleBitmap(screenDc, outW, outH);
            old = SelectObject(memDc, bmp);
            SetStretchBltMode(memDc, HALFTONE);
            SetBrushOrgEx(memDc, 0, 0, IntPtr.Zero);
            if (!StretchBlt(memDc, 0, 0, outW, outH, screenDc, srcX, srcY, srcW, srcH, SRCCOPY))
                return null;

            var info = new BITMAPINFO
            {
                bmiHeader = new BITMAPINFOHEADER
                {
                    biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                    biWidth = outW,
                    biHeight = -outH,       // 负数 = top-down 行序
                    biPlanes = 1,
                    biBitCount = 32,
                    biCompression = 0,   // BI_RGB
                },
            };
            var buffer = new byte[outW * outH * 4];
            var lines = GetDIBits(memDc, bmp, 0, (uint)outH, buffer, ref info, DIB_RGB_COLORS);
            return lines == 0 ? null : buffer;
        }
        catch (Exception ex)
        {
            LauncherLog.Write("novacap", $"GrabBgra 异常：{ex.GetType().Name}: {ex.Message}");
            return null;
        }
        finally
        {
            if (old != IntPtr.Zero) SelectObject(memDc, old);
            if (bmp != IntPtr.Zero) DeleteObject(bmp);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            if (screenDc != IntPtr.Zero) ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    // ── Win32 ──

    private const int SM_XVIRTUALSCREEN = 76;
    private const int SM_YVIRTUALSCREEN = 77;
    private const int SM_CXVIRTUALSCREEN = 78;
    private const int SM_CYVIRTUALSCREEN = 79;
    private const uint SRCCOPY = 0x00CC0020;
    private const int HALFTONE = 4;
    private const uint DIB_RGB_COLORS = 0;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public int biCompression;
        public int biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public int biClrUsed;
        public int biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public int bmiColors;
    }

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder text, int max);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern int SetStretchBltMode(IntPtr hdc, int mode);

    [DllImport("gdi32.dll")]
    private static extern bool SetBrushOrgEx(IntPtr hdc, int x, int y, IntPtr prev);

    [DllImport("gdi32.dll")]
    private static extern bool StretchBlt(IntPtr destDc, int destX, int destY, int destW, int destH,
        IntPtr srcDc, int srcX, int srcY, int srcW, int srcH, uint rop);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr bmp, uint start, uint lines,
        byte[] bits, ref BITMAPINFO info, uint usage);
}
