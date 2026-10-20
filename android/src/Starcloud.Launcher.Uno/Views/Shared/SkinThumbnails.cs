// 皮肤卡片缩略图：把 64×64 的纹理渲染成一张小的"正面立绘"，供账户详情页的卡片使用。
//
// 为什么不用 BitmapImage 直接显示纹理：64×64 的贴图在卡片里就是一堆看不懂的色块，
// 玩家认的是"这个角色长什么样"。所以走 Core 的软件光栅化器（与账户页 3D 预览同一个渲染器、
// 同一套投影），出一张 88×112 的立绘。
//
// 缓存落在磁盘（数据目录 cache/skin-thumbs/<指纹>.png）而不是内存：
//   · 卡片用的是普通 BitmapImage（同步拿到 ImageSource，x:Bind 一次性绑定就够）；
//   · 指纹含文件写入时间，换皮肤/改皮肤自动重算；重开启动器也不用重渲染。
//
// 移植自桌面 Starcloud.App.Views.Shared.SkinThumbnails.cs：渲染管线（Core 软件光栅化 +
// 反预乘）原样保留；唯一差异是磁盘图片进 BitmapImage 时必须补 file:// 前缀
// （安卓不允许省略 scheme 的绝对路径 URI，同 VersionRowHelpers.ResolveIconSource）。

using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Starcloud.Core.Rendering;
using Starcloud.Core.Services;
using Starcloud.Launcher.Uno.Services;

namespace Starcloud.Launcher.Uno.Views.Shared;

internal static class SkinThumbnails
{
    private const int CardWidth = 88;
    private const int CardHeight = 112;

    private static string CacheDir => Path.Combine(LauncherPaths.CacheDir, "skin-thumbs");

    /// <summary>头像缓存单独一个目录：同一个皮肤文件要出两张不同图（立绘 / 头像），别互相覆盖。</summary>
    private static string HeadCacheDir => Path.Combine(LauncherPaths.CacheDir, "skin-heads");

    /// <summary>
    /// 账户头像：皮肤文件 → 只画头部的方形头像（磁盘缓存）。
    /// 直接把 64×64 纹理丢给 Image 会显示成一团挤在一起的色块，所以头像也走渲染。
    /// </summary>
    public static ImageSource? HeadFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            // 指纹里带上取景参数：改了镜头距离（头像取景变满幅）旧缓存就得作废重渲，
            // 否则界面上还挂着老图，改了半天看不出变化
            var hash = Convert.ToHexString(SHA1.HashData(
                Encoding.UTF8.GetBytes(
                    $"{path}|{stamp}|head|{SkinHeadImage.Size}|{SkinHeadImage.Distance}")))[..16];
            Directory.CreateDirectory(HeadCacheDir);
            var file = Path.Combine(HeadCacheDir, hash + ".png");
            if (!File.Exists(file))
                File.WriteAllBytes(file, SkinHeadImage.Encode(File.ReadAllBytes(path)));
            return SourceFromFile(file);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("skin", $"头像渲染失败（{path}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>渲染皮肤文件；文件不存在 / 解码失败返回 null（卡片显示占位底色）。</summary>
    public static ImageSource? FromFile(string? path, bool slim)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            var stamp = File.GetLastWriteTimeUtc(path).Ticks;
            return FromBytes(File.ReadAllBytes(path), slim, $"{path}|{stamp}");
        }
        catch (Exception ex)
        {
            LauncherLog.Write("skin", $"缩略图渲染失败（{path}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>渲染内存里的皮肤 PNG（预置皮肤用：不必先落盘）。</summary>
    public static ImageSource? FromBytes(byte[] png, bool slim, string cacheKey)
        => SourceFromFile(EnsureThumbFile(png, slim, cacheKey));

    /// <summary>
    /// 渲染并缓存缩略图，返回缓存文件路径（失败返回 null）。
    /// 纯 CPU + 磁盘活：可以放心丢到后台线程——皮肤站的衣柜是一次几十张图，不该占着 UI 线程渲染。
    /// </summary>
    public static string? EnsureThumbFile(byte[] png, bool slim, string cacheKey)
    {
        try
        {
            Directory.CreateDirectory(CacheDir);
            var hash = Convert.ToHexString(
                SHA1.HashData(Encoding.UTF8.GetBytes($"{cacheKey}|{slim}|{CardWidth}x{CardHeight}")))[..16];
            var file = Path.Combine(CacheDir, hash + ".png");
            if (!File.Exists(file))
            {
                var frame = Render(png, slim);
                File.WriteAllBytes(file, frame);
            }
            return file;
        }
        catch (Exception ex)
        {
            LauncherLog.Write("skin", $"缩略图生成失败（{cacheKey}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 本地图片路径 → ImageSource。**只能在 UI 线程调用**：BitmapImage 是 DependencyObject，
    /// 在后台线程 new 会直接崩。
    /// <para>路径必须补 file:// 前缀（安卓不允许省略 scheme 的绝对路径 URI），
    /// 否则绑定 Image.Source 时会抛异常导致整页崩溃。</para>
    /// </summary>
    public static ImageSource? SourceFromFile(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return null;
        try
        {
            return new BitmapImage(new Uri("file://" + path));
        }
        catch (Exception ex)
        {
            LauncherLog.Write("skin", $"缩略图读入失败（{path}）：{ex.Message}");
            return null;
        }
    }

    /// <summary>渲染并编码成 PNG 字节</summary>
    private static byte[] Render(byte[] png, bool slim)
    {
        var texture = PngDecoder.Decode(png);
        var model = new SkinModel(texture, slim);
        // yaw=0 正对镜头，俯视 8° 露一条顶面（头发 / 肩），距离按卡片高度取到"人物占满但不顶边"
        var frame = SkinRenderer.Render(model,
            new RenderOptions(new Camera(0, 8, 66, 30)), CardWidth, CardHeight);

        // 渲染输出是 BGRA8 预乘 alpha；PNG 要直通 alpha，这里反预乘
        var rgba = new byte[frame.Bgra.Length];
        for (var i = 0; i < rgba.Length; i += 4)
        {
            var a = frame.Bgra[i + 3];
            rgba[i] = Unpremultiply(frame.Bgra[i + 2], a);
            rgba[i + 1] = Unpremultiply(frame.Bgra[i + 1], a);
            rgba[i + 2] = Unpremultiply(frame.Bgra[i], a);
            rgba[i + 3] = a;
        }
        return PngEncoder.Encode(frame.Width, frame.Height, rgba);
    }

    private static byte Unpremultiply(byte value, byte alpha) =>
        alpha == 0 ? (byte)0 : (byte)Math.Min(255, value * 255 / alpha);
}
