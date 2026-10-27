// D3D11 硬件光栅化的皮肤渲染器（软件光栅化器 SkinRenderer 的 GPU 对应实现）。
//
// 为什么要有它：软件光栅化在这台机器上 630×1020 一帧 58ms（约 17fps），
// 走路动画只能靠"降渲染分辨率 + 线性放大"换帧率，代价是像素风糊掉。
// 把逐像素着色搬到 GPU 后，同样的图在 1:1 分辨率下不到 1ms，动画不再掉档。
//
// 与软件路径的关系（两者必须像素级一致，靠 SkinGpuTests 的对照测试兜底）：
//   · 投影只有一份实现（SkinProjection）；CPU 走重心坐标，GPU 交给硬件光栅器。
//   · 采样用 Texture2D.Load（整数纹素取数、不经过采样器/滤波），并像 CPU 那样把
//     纹素坐标钳在本面片的 UV 矩形内——这是"边缘杂色描边"那个 bug 的根因。
//   · 混合：内部按预乘 alpha 做 source-over（与 CPU 的直通 alpha 混合 + 最后预乘等价），
//     渲染目标直接就是预乘 BGRA8，交给上层贴图。
//
// 取帧方式：GPU 渲染到离屏纹理 → CopyResource 到 staging → Map 回读到 byte[]。
// 这一步是刻意的取舍：上层（Win2D 画布）只吃 CPU 侧字节，回读 2.6MB 约 1ms，
// 换来"不跨设备共享纹理、不碰 Win2D 设备、不引入线程时序问题"的干净边界。
// 真正的零拷贝通路（共享纹理 / SwapChainPanel）是下一步的事。

using System.Runtime.InteropServices;
using SharpGen.Runtime;
using Vortice.D3DCompiler;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;
using Vortice.Mathematics;
using Starcloud.Core.Services;

namespace Starcloud.Core.Rendering;

/// <summary>
/// GPU 版皮肤渲染器。<see cref="TryCreate"/> 失败（没有显卡、缺 d3dcompiler、非 Windows）
/// 一律返回 null，调用方回退 <see cref="SkinRenderer"/>。
/// <para>线程：内部只有一个 D3D11 设备与立即上下文，**同一时刻只允许一个线程调用**
/// <see cref="Render"/>（调用方已用 _renderBusy 串行化）。</para>
/// </summary>
public sealed class SkinGpuRenderer : IDisposable
{
    /// <summary>
    /// 顶点着色器只做屏幕像素 → NDC 的换算；像素着色器取纹素、按面片矩形钳制、预乘输出。
    /// 不引采样器：Load 就是最近的整数纹素，和 CPU 的 (int)u 取数完全对齐。
    /// <para><b>注意：这段 HLSL 必须是纯 ASCII</b>——Vortice 把源码按 ANSI 交给 D3DCompile，
    /// 中文注释会被截断成语法错误（实测 X3000: unexpected end of file）。C# 源码照旧用中文注释。</para>
    /// </summary>
    private const string ShaderSource = """
        cbuffer Frame : register(b0)
        {
            float2 Viewport;   // render target size in pixels
            float2 Padding;
        };

        struct VsIn
        {
            float3 Pos  : POSITION;    // screen pixel xy + NDC depth
            float3 Uvz  : TEXCOORD0;   // xy = uv / zc, z = 1 / zc
            float4 Rect : TEXCOORD1;   // inclusive texel rect of this face
        };

        struct VsOut
        {
            float4 Pos  : SV_POSITION;
            float3 Uvz  : TEXCOORD0;
            nointerpolation float4 Rect : TEXCOORD1;
        };

        Texture2D Skin : register(t0);

        VsOut VSMain(VsIn input)
        {
            VsOut output;
            output.Pos = float4(input.Pos.x / Viewport.x * 2.0 - 1.0,
                                1.0 - input.Pos.y / Viewport.y * 2.0,
                                input.Pos.z, 1.0);
            output.Uvz = input.Uvz;
            output.Rect = input.Rect;
            return output;
        }

        float4 PSMain(VsOut input) : SV_Target
        {
            // w = 1 is passed in, so hardware interpolates attributes linearly in screen
            // space; dividing (uv/z) by (1/z) here reproduces exactly what the software
            // rasterizer computes per pixel.
            float2 uv = input.Uvz.xy / input.Uvz.z;
            int2 texel = (int2)floor(uv);
            texel = clamp(texel, (int2)input.Rect.xy, (int2)input.Rect.zw);
            float4 c = Skin.Load(int3(texel, 0));
            clip(c.a - 0.5 / 255.0);   // fully transparent texels write no depth (same as CPU)
            // DXGI BGRA8 names memory byte order: shader .x lands in the R component
            // (memory offset 2), so output RGBA here and memory ends up B,G,R,A
            // which is exactly BitmapFrame's layout.
            return float4(c.r * c.a, c.g * c.a, c.b * c.a, c.a);
        }
        """;

    /// <summary>顶点布局：屏幕坐标(3) + uv/z 与 1/z(3) + UV 矩形(4)，共 40 字节</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct GpuVertex
    {
        public float X, Y, Z;
        public float Uz, Vz, Iz;
        public float U0, V0, U1, V1;
    }

    private const uint VertexStride = 40;
    private const int MaxVerticesPerFace = 6;

    private readonly ID3D11Device _device;
    private readonly ID3D11DeviceContext _context;
    private readonly ID3D11VertexShader _vertexShader;
    private readonly ID3D11PixelShader _pixelShader;
    private readonly ID3D11InputLayout _inputLayout;
    private readonly ID3D11BlendState _blendState;
    private readonly ID3D11DepthStencilState _depthState;
    private readonly ID3D11RasterizerState _rasterizerState;
    private readonly ID3D11Buffer _frameBuffer;
    private readonly string _driver;

    private ID3D11Buffer? _vertexBuffer;
    private int _vertexCapacity;
    private GpuVertex[] _vertices = [];

    private ID3D11Texture2D? _target;
    private ID3D11RenderTargetView? _targetView;
    private ID3D11Texture2D? _depth;
    private ID3D11DepthStencilView? _depthView;
    private ID3D11Texture2D? _staging;
    private byte[] _output = [];
    private int _width, _height;

    private ID3D11Texture2D? _skinTexture;
    private ID3D11Texture2D? _capeTexture;
    private ID3D11ShaderResourceView? _capeView;
    private SkinBitmap? _capeSource;
    private ID3D11ShaderResourceView? _skinView;
    private SkinBitmap? _skinSource;

    private bool _disposed;

    /// <summary>
    /// <see cref="Render"/>（后台线程）与 <see cref="Dispose"/>（UI 线程，视图卸载时）互斥。
    /// <para>没有它会真的崩：卸载时 Dispose 把顶点缓冲等 COM 对象放掉，正在跑的
    /// UpdateSubresource 就握着野指针 → AccessViolationException。这不是能 catch 的异常，
    /// 整个进程直接没（实测在进入账户详情页时崩过一次，WER 报 coreclr + 0xc0000005）。
    /// 有了锁之后最坏情况是 Render 拿到 ObjectDisposedException，被调用方 catch 住回退软件光栅化。</para>
    /// </summary>
    private readonly object _gate = new();

    /// <summary>显卡名（记日志用）</summary>
    public string DriverName => _driver;

    private SkinGpuRenderer(bool allowSoftwareDevice)
    {
        var (device, context) = CreateDevice(allowSoftwareDevice);
        _device = device;
        _context = context;

        var vs = Compile("VSMain", "vs_5_0").ToArray();
        var ps = Compile("PSMain", "ps_5_0").ToArray();
        _vertexShader = _device.CreateVertexShader(vs, null);
        _pixelShader = _device.CreatePixelShader(ps, null);
        _inputLayout = _device.CreateInputLayout(
        [
            new InputElementDescription("POSITION", 0, Format.R32G32B32_Float, 0, 0),
            new InputElementDescription("TEXCOORD", 0, Format.R32G32B32_Float, 12, 0),
            new InputElementDescription("TEXCOORD", 1, Format.R32G32B32A32_Float, 24, 0),
        ], vs);

        // 预乘 alpha 的 source-over：src=One / dst=InvSrcAlpha（RGB 与 alpha 同规则）
        _blendState = _device.CreateBlendState(new BlendDescription(
            Blend.One, Blend.InverseSourceAlpha, Blend.One, Blend.InverseSourceAlpha));
        // 严格 Less：深度相等时先画的先占位，与 CPU 的 "iz <= depth 则跳过" 一致
        _depthState = _device.CreateDepthStencilState(
            new DepthStencilDescription(true, DepthWriteMask.All, ComparisonFunction.Less));
        // 不做背面剔除（与 CPU 一致：靠深度解决内外层）、实心填充、不开 MSAA（像素风要硬边）
        _rasterizerState = _device.CreateRasterizerState(RasterizerDescription.CullNone);
        _frameBuffer = _device.CreateBuffer(new BufferDescription(
            16, BindFlags.ConstantBuffer, ResourceUsage.Default, CpuAccessFlags.None,
            ResourceOptionFlags.None, 0));

        _driver = QueryDriverName();
    }

    /// <summary>创建 D3D11 设备 + 立即上下文；连软件设备（WARP）都不行就抛。</summary>
    private static (ID3D11Device Device, ID3D11DeviceContext Context) CreateDevice(bool allowSoftwareDevice)
    {
        var levels = new[]
        {
            FeatureLevel.Level_11_1, FeatureLevel.Level_11_0,
            FeatureLevel.Level_10_1, FeatureLevel.Level_10_0,
        };
        var drivers = allowSoftwareDevice
            ? new[] { DriverType.Hardware, DriverType.Warp }
            : [DriverType.Hardware];

        var result = default(Result);
        foreach (var driver in drivers)
        {
            // BgraSupport：Win2D/D2D 互操作与预乘 BGRA 输出都要它
            result = D3D11.D3D11CreateDevice(null, driver, DeviceCreationFlags.BgraSupport,
                levels, out var device, out _, out var context);
            if (result.Success && device is not null && context is not null) return (device, context);
            // 没装 D3D 11.1 运行时的机器对 11_1 直接返回 E_INVALIDARG：去掉它重试
            result = D3D11.D3D11CreateDevice(null, driver, DeviceCreationFlags.BgraSupport,
                levels[1..], out device, out _, out context);
            if (result.Success && device is not null && context is not null) return (device, context);
        }
        throw new InvalidOperationException($"D3D11CreateDevice 失败：{result.Description}");
    }

    /// <summary>创建 GPU 渲染器；失败返回 null（并写一条日志说明原因），调用方回退软件光栅化。</summary>
    public static SkinGpuRenderer? TryCreate(bool allowSoftwareDevice = false)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return new SkinGpuRenderer(allowSoftwareDevice);
        }
        catch (Exception ex)
        {
            LauncherLog.Write("player3d", $"GPU 渲染器不可用，回退软件光栅化：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 渲染一帧，输出预乘 BGRA8（与 <see cref="SkinRenderer"/> 同格式）。
    /// <para>返回的字节数组在内部复用：同一时刻只有"最新一帧"有效。</para>
    /// </summary>
    public BitmapFrame Render(SkinModel skin, RenderOptions opts, int width, int height, Pose? pose = null)
    {
        if (width <= 0 || height <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            return RenderCore(skin, opts, width, height, pose);
        }
    }

    private BitmapFrame RenderCore(SkinModel skin, RenderOptions opts, int width, int height, Pose? pose)
    {
        EnsureTargets(width, height);
        EnsureSkinTexture(skin.Texture);
        _context.UpdateSubresource<byte>(skin.Texture.Rgba, _skinTexture!, 0, (uint)(skin.Texture.Width * 4), 0, null);
        // 披风纹理必须自己上传：GPU 的 UpdateSubresource 只更新皮肤纹理那一张，
        // 漏掉这步时披风 SRV 里是空数据 → 采样全透明 → 看不见披风
        if (skin.CapeTexture is { } capeTex)
        {
            EnsureCapeTexture(capeTex);
            _context.UpdateSubresource<byte>(capeTex.Rgba, _capeTexture!, 0,
                (uint)(capeTex.Width * 4), 0, null);
        }

        // 近远平面只用于深度比较，包住整个模型即可（模型半径 ≤ 26 皮肤像素）
        var near = Math.Max(0.01, opts.Camera.Distance - 96.0);
        var far = opts.Camera.Distance + 96.0;
        var depthScale = 1.0 / (far - near);

        var quads = PlayerModel.BuildQuads(skin, pose ?? Pose.Still, opts.HeadOnly ? "head" : null);
        var faces = SkinProjection.Project(quads, opts.Camera, width, height, opts.HeadOnly ? 28.0 : 16.0);

        // 披风面片用披风纹理采样：分两组顶点，两次绘制（各自绑定 SRV）
        var hasCape = skin.CapeTexture is not null;
        if (hasCape) EnsureCapeTexture(skin.CapeTexture!);

        var bodyFaces = new List<ScreenQuad>();
        var capeFaceList = new List<ScreenQuad>();
        foreach (var f in faces)
            (f.Source.Part == "cape" && hasCape ? capeFaceList : bodyFaces).Add(f);

        var needed = (faces.Count + (hasCape ? 1 : 0)) * MaxVerticesPerFace;
        if (_vertices.Length < needed) _vertices = new GpuVertex[needed];
        var count = 0;
        var capeStart = -1;

        // 顶点写入助手（两段共用）：UV 先乘 1/z，片元里再除回来（透视校正）
        int Add(ScreenVertex p, float u0, float v0, float u1, float v1)
        {
            var iz = (float)(1.0 / p.Z);
            _vertices[count] = new GpuVertex
            {
                X = (float)p.Sx,
                Y = (float)p.Sy,
                Z = (float)((p.Z - near) * depthScale),
                Uz = (float)p.U * iz,
                Vz = (float)p.V * iz,
                Iz = iz,
                U0 = u0,
                V0 = v0,
                U1 = u1,
                V1 = v1,
            };
            return count + 1;
        }

        foreach (var face in bodyFaces)
        {
            // 采样边界：右/下边界取 Right-1 / Bottom-1（矩形是左闭右开）
            var rect = face.Source.Uv;
            var u0 = rect.X;
            var v0 = rect.Y;
            var u1 = rect.Right - 1;
            var v1 = rect.Bottom - 1;

            count = Add(face.A, u0, v0, u1, v1);
            count = Add(face.B, u0, v0, u1, v1);
            count = Add(face.C, u0, v0, u1, v1);
            count = Add(face.A, u0, v0, u1, v1);
            count = Add(face.C, u0, v0, u1, v1);
            count = Add(face.D, u0, v0, u1, v1);
        }

        if (hasCape)
        {
            capeStart = count;
            foreach (var face in capeFaceList)
            {
                var rect = face.Source.Uv;
                var u0 = rect.X;
                var v0 = rect.Y;
                var u1 = rect.Right - 1;
                var v1 = rect.Bottom - 1;

                count = Add(face.A, u0, v0, u1, v1);
                count = Add(face.B, u0, v0, u1, v1);
                count = Add(face.C, u0, v0, u1, v1);
                count = Add(face.A, u0, v0, u1, v1);
                count = Add(face.C, u0, v0, u1, v1);
                count = Add(face.D, u0, v0, u1, v1);
            }
        }

        if (count == 0)
        {
            Array.Clear(_output);
            return new BitmapFrame(_output, width, height);
        }

        EnsureVertexBuffer(count);
        _context.UpdateSubresource<GpuVertex>(_vertices.AsSpan(0, count), _vertexBuffer!, 0, 0, 0, null);

        Span<FrameConstants> frame = [new FrameConstants { Width = width, Height = height }];
        _context.UpdateSubresource<FrameConstants>(frame, _frameBuffer, 0, 0, 0, null);

        _context.OMSetRenderTargets(_targetView!, _depthView);
        _context.ClearRenderTargetView(_targetView!, new Color4(0f, 0f, 0f, 0f));
        _context.ClearDepthStencilView(_depthView!, DepthStencilClearFlags.Depth, 1f, 0);
        _context.RSSetViewports([new Viewport(0, 0, width, height, 0f, 1f)]);
        _context.RSSetState(_rasterizerState);
        _context.OMSetDepthStencilState(_depthState, 0);
        _context.OMSetBlendState(_blendState);
        _context.IASetInputLayout(_inputLayout);
        _context.IASetVertexBuffers(0, [_vertexBuffer!], [VertexStride], [0u]);
        _context.VSSetShader(_vertexShader);
        _context.VSSetConstantBuffers(0, [_frameBuffer]);
        _context.PSSetShader(_pixelShader);
        if (capeStart < 0)
        {
            _context.PSSetShaderResources(0, [_skinView!]);
            _context.Draw((uint)count, 0);
        }
        else
        {
            // 先画身体（皮肤纹理），再切披风 SRV 画披风段；深度测试排好前后
            _context.PSSetShaderResources(0, [_skinView!]);
            _context.Draw((uint)capeStart, 0);
            _context.PSSetShaderResources(0, [_capeView!]);
            _context.Draw((uint)(count - capeStart), (uint)capeStart);
        }

        _context.CopyResource(_staging!, _target!);
        ReadBack(width, height);
        return new BitmapFrame(_output, width, height);
    }

    /// <summary>staging 纹理 → 输出字节（行距按驱动给的 pitch 走，不能假设等于 width*4）</summary>
    private void ReadBack(int width, int height)
    {
        var mapped = _context.Map(_staging!, 0, MapMode.Read, Vortice.Direct3D11.MapFlags.None);
        try
        {
            var rowBytes = width * 4;
            var pitch = (int)mapped.RowPitch;
            if (pitch == rowBytes)
            {
                Marshal.Copy(mapped.DataPointer, _output, 0, _output.Length);
            }
            else
            {
                for (var y = 0; y < height; y++)
                    Marshal.Copy(mapped.DataPointer + y * pitch, _output, y * rowBytes, rowBytes);
            }
        }
        finally
        {
            _context.Unmap(_staging!, 0);
        }
    }

    private void EnsureTargets(int width, int height)
    {
        if (_target is not null && _width == width && _height == height) return;
        ReleaseTargets();
        _width = width;
        _height = height;

        _target = _device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.RenderTarget, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        _targetView = _device.CreateRenderTargetView(_target);

        _depth = _device.CreateTexture2D(new Texture2DDescription(
            Format.D32_Float, (uint)width, (uint)height, 1, 1,
            BindFlags.DepthStencil, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        _depthView = _device.CreateDepthStencilView(_depth);

        _staging = _device.CreateTexture2D(new Texture2DDescription(
            Format.B8G8R8A8_UNorm, (uint)width, (uint)height, 1, 1,
            BindFlags.None, ResourceUsage.Staging, CpuAccessFlags.Read, 1, 0, ResourceOptionFlags.None));

        _output = new byte[width * height * 4];
    }

    private void EnsureCapeTexture(SkinBitmap cape)
    {
        if (_capeTexture is not null && ReferenceEquals(_capeSource, cape)) return;
        ReleaseCapeTexture();

        _capeTexture = _device.CreateTexture2D(new Texture2DDescription(
            Format.R8G8B8A8_UNorm, (uint)cape.Width, (uint)cape.Height, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        _capeView = _device.CreateShaderResourceView(_capeTexture);
        _capeSource = cape;
    }

    private void ReleaseCapeTexture()
    {
        _capeView?.Dispose(); _capeView = null;
        _capeTexture?.Dispose(); _capeTexture = null;
    }

    private void EnsureSkinTexture(SkinBitmap skin)
    {
        if (_skinTexture is not null && ReferenceEquals(_skinSource, skin)) return;
        ReleaseSkinTexture();

        _skinTexture = _device.CreateTexture2D(new Texture2DDescription(
            Format.R8G8B8A8_UNorm, (uint)skin.Width, (uint)skin.Height, 1, 1,
            BindFlags.ShaderResource, ResourceUsage.Default, CpuAccessFlags.None, 1, 0, ResourceOptionFlags.None));
        _skinView = _device.CreateShaderResourceView(_skinTexture);
        _skinSource = skin;
    }

    private void EnsureVertexBuffer(int vertexCount)
    {
        if (_vertexBuffer is not null && _vertexCapacity >= vertexCount) return;
        _vertexBuffer?.Dispose();
        // 容量向上取整到面数档位，避免姿势/皮肤微调时反复重建
        _vertexCapacity = (vertexCount + 255) / 256 * 256;
        _vertexBuffer = _device.CreateBuffer(new BufferDescription(
            (uint)(_vertexCapacity * (int)VertexStride), BindFlags.VertexBuffer,
            ResourceUsage.Default, CpuAccessFlags.None, ResourceOptionFlags.None, 0));
    }

    private static ReadOnlyMemory<byte> Compile(string entryPoint, string profile)
        => Compiler.Compile(ShaderSource, entryPoint, "SkinGpu.hlsl", profile,
            ShaderFlags.OptimizationLevel3, EffectFlags.None);

    private string QueryDriverName()
    {
        try
        {
            using var dxgiDevice = _device.QueryInterface<IDXGIDevice>();
            using var adapter = dxgiDevice.GetAdapter();
            return adapter.Description.Description ?? "未知显卡";
        }
        catch
        {
            return "未知显卡";
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FrameConstants
    {
        public float Width;
        public float Height;
        public float Padding0;
        public float Padding1;
    }

    private void ReleaseTargets()
    {
        _targetView?.Dispose(); _targetView = null;
        _target?.Dispose(); _target = null;
        _depthView?.Dispose(); _depthView = null;
        _depth?.Dispose(); _depth = null;
        _staging?.Dispose(); _staging = null;
        _output = [];
    }

    private void ReleaseSkinTexture()
    {
        _skinView?.Dispose(); _skinView = null;
        _skinTexture?.Dispose(); _skinTexture = null;
        _skinSource = null;
    }

    public void Dispose()
    {
        // 与 Render 互斥：等正在跑的那一帧收尾（GPU 一帧约 1ms）再放资源，
        // 否则后台线程会在 COM 对象已释放后继续调 UpdateSubresource → 进程级崩溃。
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            ReleaseTargets();
            ReleaseSkinTexture();
            ReleaseCapeTexture();
            _vertexBuffer?.Dispose();
            _frameBuffer.Dispose();
            _rasterizerState.Dispose();
            _depthState.Dispose();
            _blendState.Dispose();
            _inputLayout.Dispose();
            _pixelShader.Dispose();
            _vertexShader.Dispose();
            _context.Dispose();
            _device.Dispose();
        }
    }
}
