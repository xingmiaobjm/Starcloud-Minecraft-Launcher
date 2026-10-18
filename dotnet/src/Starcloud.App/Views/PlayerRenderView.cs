// 账户页右侧悬浮的 Minecraft 玩家 3D 形象（无卡片、透明背景，LauncherX 式）。
//
// 渲染方案：皮肤贴图（64x64 / 64x32）+ 软件正交投影。把官方模型拆成 6 个 cuboid
//（头/身/双臂/双腿），每个 cuboid 的 6 个面按 Minecraft 盒 UV 从皮肤上取矩形，
// 经「肢体摆动 → 全局 yaw/pitch → 正交投影」后用 Win2D 仿射矩阵逐面绘制
//（正交投影下面片是平行四边形，Matrix3x2 仿射即可无畸变贴图；NearestNeighbor 保像素风），
// 全部面按相机空间深度排序解决遮挡，再按面方向叠一层黑色半透明做明暗（MC 风格光照）。
//
// 动画：四肢绕肩/髋正弦摆动（对角相位）、头部缓慢左右微转；设置 account_walk_animation=false
// 或页面不可见时保持站立静止（单帧，不跑计时器）。
//
// 皮肤来源：微软账户用 UUID、离线账户用玩家名，走 minotar.net/skin（与头像同源，
// mc-heads 403 已弃用）→ 磁盘缓存 skin_cache → 全部失败用内置 Assets/steve.png。

using System.Diagnostics;
using System.Numerics;
using System.Net.Http;
using Microsoft.Graphics.Canvas;
using Microsoft.Graphics.Canvas.UI.Xaml;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;
using Windows.UI;

namespace Starcloud.App.Views;

public sealed class PlayerRenderView : UserControl
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(8) };
    private static readonly string CacheDir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "starcloud-launcher", "skin_cache");

    private readonly CanvasControl _canvas;
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private CanvasBitmap? _skin;
    private bool _legacySkin;   // 64x32 旧版皮肤：左肢复用右肢贴图
    private bool _slimArms;     // Alex 模型：3px 细手臂
    private bool _visible;
    private string? _skinKey;
    private bool _resourcesReady;   // CanvasControl 的设备就绪（CreateResources 已触发）
    private (string? Type, string? Uuid, string? Username) _pendingAccount;

    /// <summary>是否播放走路动画（设置 account_walk_animation）。</summary>
    public bool WalkEnabled { get; set; } = true;

    public PlayerRenderView()
    {
        _canvas = new CanvasControl { ClearColor = Microsoft.UI.Colors.Transparent };
        _canvas.Draw += OnDraw;
        // 皮肤位图必须等 CanvasDevice 就绪后才能创建（Win2D 要求在 CreateResources/Draw 中做资源工作）
        _canvas.CreateResources += (_, _) =>
        {
            _resourcesReady = true;
            _ = LoadSkinAsync(_pendingAccount.Type, _pendingAccount.Uuid, _pendingAccount.Username);
        };
        Content = _canvas;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(33) };
        _timer.Tick += (_, _) => { if (_visible && WalkEnabled) _canvas.Invalidate(); };
        _timer.Start();

        Unloaded += (_, _) =>
        {
            _visible = false;
            _timer.Stop();
            _canvas.Draw -= OnDraw;
            _canvas.RemoveFromVisualTree();
        };
        Loaded += (_, _) => { _visible = true; _canvas.Invalidate(); };
    }

    /// <summary>按账户切换皮肤（离线=玩家名、微软=UUID）；空账户渲染默认 Steve。</summary>
    public void SetAccount(string? type, string? uuid, string? username)
    {
        _pendingAccount = (type, uuid, username);
        if (!_resourcesReady) return; // 设备未就绪：CreateResources 触发时会补载
        _ = LoadSkinAsync(type, uuid, username);
    }

    /// <summary>设置项变化后立即重绘（关闭走路 → 静止帧）。</summary>
    public void RefreshAnimation() => _canvas.Invalidate();

    // ── 皮肤加载：磁盘缓存 → minotar → 内置 Steve ──

    private async Task LoadSkinAsync(string? type, string? uuid, string? username)
    {
        var id = type == "microsoft" ? uuid : username;
        var key = string.IsNullOrWhiteSpace(id) ? "steve" : $"{(type == "microsoft" ? "m_" : "u_")}{id}";
        _skinKey = key;
        _skin = null;
        _canvas.Invalidate();

        if (key != "steve")
        {
            var file = Path.Combine(CacheDir, $"{Sanitize(key)}.png");
            if (!File.Exists(file))
            {
                var url = $"https://minotar.net/skin/{Uri.EscapeDataString(id!)}";
                try
                {
                    using var resp = await Http.GetAsync(url);
                    resp.EnsureSuccessStatusCode();
                    Directory.CreateDirectory(CacheDir);
                    await using var fs = File.Create(file);
                    await resp.Content.CopyToAsync(fs);
                }
                catch (Exception ex)
                {
                    Starcloud.Core.Services.LauncherLog.Error("player", $"皮肤下载失败 url={url}", ex);
                    File.Delete(file);
                } // 下载失败（含半截文件）→ 落到内置兜底
            }
            if (File.Exists(file))
            {
                try
                {
                    var bmp = await CanvasBitmap.LoadAsync(_canvas, file);
                    if (_skinKey != key) return; // 期间已切到别的账户
                    _legacySkin = bmp.SizeInPixels.Height < 64;
                    _slimArms = !_legacySkin && IsSlim(bmp);
                    _skin = bmp;
                    Starcloud.Core.Services.LauncherLog.Write("player",
                        $"皮肤已加载 {bmp.SizeInPixels.Width}x{bmp.SizeInPixels.Height} legacy={_legacySkin} slim={_slimArms}");
                    _canvas.Invalidate();
                    return;
                }
                catch (Exception ex)
                {
                    Starcloud.Core.Services.LauncherLog.Error("player", "缓存皮肤解码失败", ex);
                }
            }
        }

        try
        {
            // 非打包应用优先物理路径，ms-appx 作为后备（两者都指向输出目录的 Assets/steve.png）
            var path = Path.Combine(AppContext.BaseDirectory, "Assets", "steve.png");
            var fallback = File.Exists(path)
                ? await CanvasBitmap.LoadAsync(_canvas, path)
                : await CanvasBitmap.LoadAsync(_canvas, new Uri("ms-appx:///Assets/steve.png"));
            if (_skinKey != key) return;
            _legacySkin = false;
            _slimArms = false; // 内置 Steve 为 classic 模型
            _skin = fallback;
            Starcloud.Core.Services.LauncherLog.Write("player", "使用内置 Steve 兜底");
            _canvas.Invalidate();
        }
        catch (Exception ex)
        {
            Starcloud.Core.Services.LauncherLog.Error("player", "Steve 兜底加载失败", ex);
        }
    }

    /// <summary>
    /// 判断 64x64 皮肤是否为 Alex（slim）模型：取 (54,20) 像素（classic 臂区占用、slim 侧留空），
    /// 透明即 slim（skinview3d 同款启发式）。HD 皮肤按 64 宽等比换算采样点。
    /// </summary>
    private static bool IsSlim(CanvasBitmap bmp)
    {
        try
        {
            var size = bmp.SizeInPixels;
            var bytes = bmp.GetPixelBytes(); // BGRA
            var stride = (int)size.Width * 4;
            var sx = (int)(54L * size.Width / 64);
            var sy = (int)(20L * size.Height / 64);
            var i = sy * stride + sx * 4;
            return i + 3 < bytes.Length && bytes[i + 3] == 0;
        }
        catch { return false; }
    }

    private static string Sanitize(string key)
    {
        var sb = new System.Text.StringBuilder(key.Length);
        foreach (var c in key) sb.Append(char.IsLetterOrDigit(c) ? c : '_');
        return sb.ToString();
    }

    private void OnDraw(CanvasControl sender, CanvasDrawEventArgs args)
    {
        if (_skin is null) return;
        var w = (float)sender.ActualWidth;
        var h = (float)sender.ActualHeight;
        if (w < 8 || h < 8) return;
        Render(args.DrawingSession, w, h);
    }

    // ── 模型定义（单位 = 皮肤像素；y 向上，原点在脚底中心） ──

    private readonly record struct Part(
        Vector3 Center, Vector3 Pivot, Vector3 Half,
        int U, int V, int Ou, int Ov,
        float SwingDir, bool IsHead);

    // classic（Steve）：4px 宽手臂，肩位 x=±6；slim（Alex）：3px 细手臂，肩位内收至 x=±5.5。
    // UV 公式在 AddBox 内按 half 参数化，两种臂宽共用同一套贴图布局。
    private static Part[] BuildParts(bool slim)
    {
        var armHalf = new Vector3(slim ? 1.5f : 2f, 6, 2);
        var armX = slim ? 5.5f : 6f;
        return
        [
            new(new(0, 28, 0),  new(0, 24, 0), new(4, 4, 4),      0,  0, 32,  0, 0,  true),   // 头
            new(new(0, 18, 0),  new(0, 18, 0), new(4, 6, 2),     16, 16, 16, 32, 0,  false),  // 身体
            new(new(-armX, 18, 0), new(-armX, 22, 0), armHalf,   40, 16, 40, 32, +1, false),  // 右臂
            new(new(armX, 18, 0),  new(armX, 22, 0),  armHalf,   32, 48, 48, 48, -1, false),  // 左臂
            new(new(-2, 6, 0),  new(-2, 12, 0), new(2, 6, 2),     0, 16,  0, 32, -1, false),  // 右腿
            new(new(2, 6, 0),   new(2, 12, 0),  new(2, 6, 2),    16, 48,  0, 48, +1, false),  // 左腿
        ];
    }

    private sealed class Face
    {
        public required Rect Uv;          // 皮肤贴图上的源矩形
        public required Vector3[] World;  // [tl, tr, br, bl]（模型空间，已含摆动/微转）
        public required float Shade;      // MC 风格面亮度
        public required bool Overlay;     // 第二层（帽/外套/袖/裤），多含透明像素，不叠黑色明暗
    }

    // 生成一个 cuboid 的 6 个面；顶点顺序按 three.js BoxGeometry 的 UV 方向约定
    //（px/nx 侧面、pz 正面、nz 背面水平镜像），保证贴图不上下颠倒、背面不反字。
    // overlay=true 时使用第二层 UV，与基础层共面（渲染时以微小深度偏移贴在其上，避免外扩产生的关节细缝）。
    private void AddBox(List<Face> list, in Part part, bool overlay, float angle)
    {
        var half = part.Half;
        var (u, v, ou, ov) = (part.U, part.V, part.Ou, part.Ov);
        // 64x32 旧版皮肤：左肢贴图复用右肢
        if (_legacySkin && part.V == 48)
        {
            if (part.Center.X > 4) (u, v, ou, ov) = (40, 16, 40, 32); // 左臂→右臂
            else (u, v, ou, ov) = (0, 16, 0, 32);                     // 左腿→右腿
        }
        if (overlay) (u, v) = (ou, ov);

        var x = half.X; var y = half.Y; var z = half.Z;      // 几何尺寸（overlay 与基础层共面，不外扩）
        var (ux, uy, uz) = (part.Half.X, part.Half.Y, part.Half.Z); // UV 按原始尺寸，避免采样溢出邻区
        var p = new Vector3[8];
        for (int i = 0; i < 8; i++)
        {
            var q = new Vector3(part.Center.X + ((i & 1) != 0 ? x : -x),
                                part.Center.Y + ((i & 2) != 0 ? y : -y),
                                part.Center.Z + ((i & 4) != 0 ? z : -z));
            p[i] = angle != 0 ? Swing(q, part.Pivot, angle, part.IsHead) : q;
        }
        // 索引：bit0=X bit1=Y bit2=Z（1=正侧）：0=(-,-,-) 1=(+,-,-) 2=(-,+,-) 3=(+,+,-)
        //                                    4=(-,-,+) 5=(+,-,+) 6=(-,+,+) 7=(+,+,+)

        // [tl,tr,br,bl] = 从面外侧看向面时贴图的左上/右上/右下/左下（p[] 约定：bit0=X bit1=Y bit2=Z）
        (Vector3 tl, Vector3 tr, Vector3 br, Vector3 bl, Rect uv, float shade)[] faces =
        {
            // -X（right 列，角色右侧面）：u=0 在背缘，u=max 折边与前缘相连
            (p[2], p[6], p[4], p[0], new Rect(u, v + 2 * uz, 2 * uz, 2 * uy), 0.6f),
            // +X（left 列，角色左侧面）：u=0 在前缘（与 front 区域折边连续）
            (p[6], p[3], p[1], p[5], new Rect(u + 2 * uz + 2 * ux, v + 2 * uz, 2 * uz, 2 * uy), 0.6f),
            // +Y（top）：v=max 边为前缘，与 front 区域上边连续
            (p[2], p[3], p[7], p[6], new Rect(u + 2 * uz, v, 2 * ux, 2 * uz), 1.0f),
            // -Y（bottom）
            (p[0], p[1], p[5], p[4], new Rect(u + 2 * uz + 2 * ux, v, 2 * ux, 2 * uz), 0.5f),
            // +Z（front，正对观察者）
            (p[6], p[7], p[5], p[4], new Rect(u + 2 * uz, v + 2 * uz, 2 * ux, 2 * uy), 0.8f),
            // -Z（back）
            (p[3], p[2], p[0], p[1], new Rect(u + 4 * uz + 2 * ux, v + 2 * uz, 2 * ux, 2 * uy), 0.8f),
        };
        foreach (var (tl, tr, br, bl, uv, shade) in faces)
            list.Add(new Face { Uv = uv, World = [tl, tr, br, bl], Shade = shade, Overlay = overlay });
    }

    private static Vector3 Swing(Vector3 p, Vector3 pivot, float ang, bool aroundY)
    {
        var d = p - pivot;
        float cos = MathF.Cos(ang), sin = MathF.Sin(ang);
        if (aroundY) // 绕 Y（头部 idle 微转）
            return pivot + new Vector3(d.X * cos + d.Z * sin, d.Y, -d.X * sin + d.Z * cos);
        return pivot + new Vector3(d.X, d.Y * cos - d.Z * sin, d.Y * sin + d.Z * cos); // 绕 X（四肢前后摆）
    }

    private void Render(CanvasDrawingSession ds, float w, float h)
    {
        // 相机：接近正面（微偏航 8.6°）+ 极小俯视；偏航过大会让侧面贴图（长发、衣侧）投影成宽板，形似错位
        const float Yaw = -0.15f, Pitch = 0.05f;
        float scale = MathF.Min(h / 36f, w / 22f);
        var origin = new Vector2(w / 2f, h / 2f + 16f * scale); // 模型 y=0（脚底）的屏幕位置
        float cy = MathF.Cos(Yaw), sy = MathF.Sin(Yaw);
        float cp = MathF.Cos(Pitch), sp = MathF.Sin(Pitch);

        var t = WalkEnabled && _visible ? (float)_clock.Elapsed.TotalSeconds : 0f;
        var swing = WalkEnabled && _visible ? MathF.Sin(t * MathF.PI * 2f / 1.4f) : 0f;
        var headYaw = WalkEnabled && _visible ? MathF.Sin(t * 0.6f) * 0.06f : 0f;

        var faces = new List<Face>(72);
        foreach (var part in BuildParts(_slimArms))
        {
            var angle = part.IsHead ? headYaw
                : part.SwingDir != 0 ? swing * part.SwingDir * (part.Center.Y > 12 ? 0.12f : 0.15f)
                : 0f; // 手臂 0.12 / 腿 0.15 rad 摆幅（小步悠然走姿；大摆幅会叉腿露出鞋底，正交投影下形似变形）
            AddBox(faces, part, overlay: false, angle);
            AddBox(faces, part, overlay: true, angle);
        }

        // 全局 yaw → pitch → 正交投影；背面剔除后按面中心相机空间深度升序（远→近）绘制
        var drawn = new List<(Vector2 tl, Vector2 tr, Vector2 br, Vector2 bl, float z, Face f)>(faces.Count);
        for (int i = 0; i < faces.Count; i++)
        {
            var v = faces[i].World;
            var s = new Vector2[4];
            var c3 = new Vector3[4];
            float z = 0;
            for (int k = 0; k < 4; k++)
            {
                var pt = new Vector3(v[k].X * cy + v[k].Z * sy, v[k].Y, -v[k].X * sy + v[k].Z * cy); // yaw
                c3[k] = pt;
                var y2 = pt.Y * cp - pt.Z * sp;
                var z2 = pt.Y * sp + pt.Z * cp;                                                       // pitch
                s[k] = new(origin.X + pt.X * scale, origin.Y - y2 * scale);
                z += z2;
            }
            // 背面剔除：面法线 n = (bl-tl)×(tr-tl)（tl,tr,br,bl 按从外侧看的顺序，n 恒朝外），n.Z ≤ 0 背对相机不画
            var n = Vector3.Cross(c3[3] - c3[0], c3[1] - c3[0]);
            if (n.Z <= 0) continue;
            // 第二层与基础层共面：给一个微小深度偏移，保证排序后总是紧贴画在对应基础层之上
            drawn.Add((s[0], s[1], s[2], s[3], z / 4f - (faces[i].Overlay ? 0.4f : 0f), faces[i]));
        }

        var order = Enumerable.Range(0, drawn.Count).ToArray();
        Array.Sort(order, (a, b) => drawn[a].z.CompareTo(drawn[b].z));

        foreach (var i in order)
        {
            var (tl, tr, br, bl, _, f) = drawn[i];
            float sw = (float)f.Uv.Width, sh = (float)f.Uv.Height;

            // 仿射矩阵：贴图像素空间 → 屏幕平行四边形（tl→(0,0)、tr→(sw,0)、bl→(0,sh)）
            ds.Transform = new Matrix3x2(
                (tr.X - tl.X) / sw, (tr.Y - tl.Y) / sw,
                (bl.X - tl.X) / sh, (bl.Y - tl.Y) / sh,
                tl.X, tl.Y);
            // 1:1 贴图（源矩形 → 同尺寸目标矩形），经 Transform 仿射映射到面片平行四边形
            ds.DrawImage(_skin, new Rect(0, 0, sw, sh), f.Uv, 1.0f, CanvasImageInterpolation.NearestNeighbor);
            // 明暗只叠加在基础层；第二层含大量透明像素，叠色会画出深色幽灵壳
            if (!f.Overlay && f.Shade < 1f)
                ds.FillRectangle(new Rect(0, 0, sw, sh),
                    Color.FromArgb((byte)(255 * (1f - f.Shade)), 0, 0, 0));
            ds.Transform = Matrix3x2.Identity;
        }
    }
}
