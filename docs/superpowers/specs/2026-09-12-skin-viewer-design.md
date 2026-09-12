# 玩家形象（皮肤 / 披风）三维预览设计

日期：2026-09-12
状态：待用户确认
所属：PCL2 平替（皮肤与披风：账户页形象渲染）

## 1. 背景与问题

用户诉求：

> 我需要可以读取头像，皮肤。账户页右侧那块空白区域渲染玩家皮肤的形象，类似于 LauncherX 的；
> 我需要可以任意角度查看，并且形象是在走路的。

现状探查结论：

1. **头像只有首字母占位**。[PageRows.cs#L103-L108](../../../dotnet/src/Starcloud.App/Views/PageRows.cs#L103-L108)
   的 `AccountRow.Initial` 取玩家名首字母，模板把它画在
   [AccountsPage.xaml#L153-L159](../../../dotnet/src/Starcloud.App/Views/AccountsPage.xaml#L153-L159)
   的 40px 圆形 `Border` 里。没有任何图像来源。
   （主界面右上角那个 40px 圆是 **Starcloud ID 会话头像**，与游戏账户无关，本设计不动它。）
2. **`StoredAccount` 没有任何皮肤字段**。[AccountService.cs#L10-L29](../../../dotnet/src/Starcloud.Core/Services/AccountService.cs#L10-L29)
   只有 `uuid / username / type / access_token / refresh_token / is_default`。
3. **皮肤数据其实已经到手但被丢掉**。`/minecraft/profile` 返回体含 `skins[]`
   （每项有 `state` / `url` / `variant`）与 `capes[]`，而
   [GetProfileAsync](../../../dotnet/src/Starcloud.Core/Services/AccountService.cs#L239-L260)
   只让 [AuthenticateWithMicrosoftTokenAsync](../../../dotnet/src/Starcloud.Core/Services/AccountService.cs#L374-L380)
   取了 `name` 与 `id`，其余直接丢弃。纹理 URL 指向 `textures.minecraft.net`，
   **公开可下载，不需要令牌**。
4. **Core 零依赖，且没有 PNG 解码**。[Starcloud.Core.csproj](../../../dotnet/src/Starcloud.Core/Starcloud.Core.csproj)
   无任何 `PackageReference`；.NET 也没有内置 PNG 解码器（`System.Drawing.Common`
   非跨平台且要引包）。但 `.NET 6+` 的 BCL 自带 `System.IO.Compression.ZLibStream`，
   PNG 的 `IDAT` 就是 zlib 流 → **PNG 解码可以自实现**。
5. **账户页右侧本来就空着**。[AccountsPage.xaml#L102-L191](../../../dotnet/src/Starcloud.App/Views/AccountsPage.xaml#L102-L191)
   的「保存的游戏账户」卡片用 `ItemsWrapGrid ItemWidth="288"` 靠左流式排列，
   全宽容器右侧是自然留白（用户截图红框区）。把这块交给形象区不需要动整体版式。
6. **App 侧没有任何位图写回 / 3D 渲染先例**。且 .NET 8 + CsWinRT 下
   `IBuffer.AsStream()` 扩展已不可用，位图写回必须走
   `DataWriter.WriteBytes` → `DetachBuffer()` → `SoftwareBitmap.CopyFromBuffer` →
   `SoftwareBitmapSource.SetBitmapAsync` 这条纯 WinRT 路径（见 §3.9）。

## 2. 用户决策记录

| 决策点 | 结论 |
|---|---|
| 功能范围 | **只读展示**：真实头像 + 全身形象 + 披风。不做上传 / 更换皮肤 |
| 渲染方式 | **Core 软件 3D 渲染器**（自解 PNG + 正交/透视投影 + Z-buffer 光栅化） |
| 第三方依赖 | **零新增依赖**；不使用 `mc-heads.net` 之类的第三方渲染服务 |
| 视角 | **可任意角度查看**：拖拽改 yaw / pitch，滚轮缩放 |
| 动画 | **持续走路**：双腿反相摆动 + 双臂反相 + 身体起伏 + 披风滞后摆动 |
| 头像 | 账户卡 40px 圆头像换成真实皮肤头部（含帽子层），首字母保留作兜底 |
| 披风 | 读取并展示（`capes[]` 中 `state=ACTIVE` 的那条） |
| 预览对象 | 默认账户；点击账户卡可切换预览对象（不改变默认账户标记） |
| 纹理缓存 | `%APPDATA%\starcloud-launcher\cache\skins\<sha1(url)>.png`，离线可用 |
| 无皮肤时 | 程序化生成的默认形象（近似 Steve / Alex 风格），不引入 Mojang 版权素材 |

### 关键取舍（需记录）

1. **为什么不用第三方渲染服务**：`mc-heads.net` / `crafatar` 只能返回固定角度的静态图，
   满足不了「任意角度查看 + 走路动画」；且强依赖外网与第三方稳定性，
   与现有自包含、零依赖架构冲突。
2. **为什么不用 XAML 面片拼贴**：裁剪容器 + `SkewTransform` 只能拼出静态平面人像，
   无法真正旋转。
3. **为什么不在 App 层做 GPU 渲染**：Windows App SDK 的 `Visual.TransformMatrix`
   确实支持 4×4（含透视），但组合树逐面片搭建调试成本高、无法单测。
   Core 软件渲染器行为确定、可单测、可离线、跨平台无依赖，符合本仓库的
   「逻辑下沉 Core + 单测覆盖」约定。

## 3. 组件设计

### 3.1 新增 `SkinBitmap.cs` + `PngDecoder.cs`（`Starcloud.Core/Rendering/`）

```csharp
/// <summary>解码后的纹理位图（RGBA8，直通 alpha）</summary>
public sealed class SkinBitmap
{
    public int Width { get; }
    public int Height { get; }
    public byte[] Rgba { get; }          // 长度 = Width * Height * 4
    public byte[] GetRegion(int x, int y, int w, int h);   // 越界按边钳制
    public bool IsRegionFullyTransparent(int x, int y, int w, int h);
}

public static class PngDecoder
{
    /// <summary>解码 PNG；不支持 / 损坏时抛 SkinTextureException</summary>
    public static SkinBitmap Decode(byte[] png);
}

public sealed class SkinTextureException : Exception { }
```

支持范围（够覆盖 Mojang 全部纹理，多一条都不做）：

- 采样：bit depth **8**（16 位不支持）
- 颜色类型：0（灰度）/ 2（RGB）/ 3（调色板）/ 4（灰度+alpha）/ 6（RGBA）
- `tRNS`：调色板透明索引、灰度/真彩单色透明键
- `interlace` 必须为 0（隔行扫描不支持）
- 多 `IDAT` 块拼接后 `ZLibStream` 解压，再按 5 种 filter（None / Sub / Up / Average / Paeth）
  逐行还原
- 不校验 CRC（只校验块长度与签名，避免为损坏文件浪费代码）

### 3.2 新增 `SkinModel.cs`（`Starcloud.Core/Rendering/`）

把纹理 UV 布局与方块几何绑在一起。**统一用 Minecraft 的展开公式**推导六个面，
而不是手抄 30 个常量矩形：

```csharp
/// <summary>方块纹理展开：给定纹理原点 (u,v) 与尺寸 (w,h,d)，推出六个面的子矩形</summary>
public static RectI FaceUv(int u, int v, int w, int h, int d, CubeFace face);
// Top    = (u + d,       v,     w, d)
// Bottom = (u + d + w,   v,     w, d)
// Right  = (u,           v + d, d, h)
// Front  = (u + d,       v + d, w, h)
// Left   = (u + d + w,   v + d, d, h)
// Back   = (u + d + w+d, v + d, w, h)
```

`RectI` / `Vec3` 是 `Rendering` 下的两个轻量 struct（`int` 与 `double` 字段），
`CubeFace` 为 6 值枚举（顺序固定 Top / Bottom / Right / Front / Left / Back）。

模型对象（供 §3.4 渲染与 §7 单测共同使用）：

```csharp
/// <summary>一个已展开的立方体：几何包围盒 + 六面 UV（顺序同 CubeFace）</summary>
public sealed record ModelCube(
    string Part,        // "head" / "hat" / "body" / "rightArm" / ... / "cape"
    Vec3 Min, Vec3 Max,
    RectI[] Faces,      // 长度 6
    bool IsCape = false);

public sealed class SkinModel
{
    /// <param name="skin">64×64 或老版 64×32（或等比 HD）已解码皮肤纹理</param>
    /// <param name="slim">true = Alex 细臂（3 px）</param>
    /// <param name="cape">可空；非 64×32（或等比 HD）时视为无披风</param>
    public SkinModel(SkinBitmap skin, bool slim, SkinBitmap? cape = null);

    public bool Slim { get; }

    /// <summary>全部几何（含覆盖层与披风）；覆盖层整面全透明时该面被剔除，
    /// 六面全透明时整个立方体不出现</summary>
    public IReadOnlyList<ModelCube> Cubes { get; }
}
```

各部件纹理原点（1.8+ 双层皮肤，64×64）：

| 部件 | 尺寸 (w,h,d) | 基础层原点 | 覆盖层原点 |
|---|---|---|---|
| 头 | 8,8,8 | (0,0) | (32,0) |
| 身 | 8,12,4 | (16,16) | (16,32) |
| 右臂 | 4,12,4（slim 3,12,4） | (40,16) | (40,32) |
| 左臂 | 4,12,4（slim 3,12,4） | (32,48) | (48,48) |
| 右腿 | 4,12,4 | (0,16) | (0,32) |
| 左腿 | 4,12,4 | (16,48) | (0,48) |

其他约定：

- **HD 纹理**：缩放系数 = `Width / 64`，要求为整数且 `Height == Width / 2`。
  非整数倍 / 长宽比不符 → 抛 `SkinTextureException` 走默认形象。
- **老版 64×32 皮肤**（`Height == 32`）：无覆盖层，左臂 / 左腿 UV 镜像右臂 / 右腿。
- **覆盖层**：几何按每边外扩 0.5 单位（即尺寸 +1）；**整面全透明则跳过该面**
  （既省算力，也避免帽子层未使用面的黑色伪影）。
- **披风**：64×32（或等比 HD）纹理；外壳面 `(1,1,10,16)`，内衬面 `(12,1,10,16)`；
  方块尺寸 10×16×1。

### 3.3 新增 `PlayerModel.cs` + `WalkAnimator.cs`（`Starcloud.Core/Rendering/`）

模型坐标系（单位 = 皮肤像素）：**+X 指向玩家左侧，+Y 向上，+Z 朝向玩家正面**，
脚底 `y = 0`。

| 部件 | 包围盒 | 旋转轴心 |
|---|---|---|
| 头 | x[-4,4] y[24,32] z[-4,4] | (0, 24, 0) |
| 身 | x[-4,4] y[12,24] z[-2,2] | — |
| 右臂 | x[-8,-4] y[12,24] z[-2,2]（slim x[-7,-4]） | (-6, 22, 0)（slim -5.5） |
| 左臂 | x[4,8] y[12,24] z[-2,2]（slim x[4,7]） | (6, 22, 0)（slim 5.5） |
| 右腿 | x[-4,0] y[0,12] z[-2,2] | (-2, 12, 0) |
| 左腿 | x[0,4] y[0,12] z[-2,2] | (2, 12, 0) |
| 披风 | x[-5,5] y[8,24] z[2,3] | (0, 24, 2) |

`WalkAnimator`：纯函数 `Pose At(double seconds)`，返回每个部件的旋转（绕 X 轴，单位弧度）
与身体位移：

```
φ        = seconds * 7.0                 // 步频 ≈ 0.9 s / 步
右腿      rotX =  sin(φ) * 30°
左腿      rotX = -sin(φ) * 30°
右臂      rotX = -sin(φ) * 25°
左臂      rotX =  sin(φ) * 25°
身体      偏移 y = -|sin(φ)| * 0.6；头随身体
头        rotX = sin(φ) * 3°
披风      rotX = 6° + sin(φ - 0.6) * 7°
```

### 3.4 新增 `SkinRenderer.cs`（`Starcloud.Core/Rendering/`）

```csharp
public sealed record Camera(double YawDeg, double PitchDeg, double Distance, double FovDeg = 30);

public sealed record RenderOptions(Camera Camera, bool HeadOnly = false);

/// <summary>渲染结果：BGRA8 预乘 alpha，可直接送 SoftwareBitmap</summary>
public sealed record BitmapFrame(byte[] Bgra, int Width, int Height);

public static class SkinRenderer
{
    public static BitmapFrame Render(SkinModel skin, RenderOptions opts, int width, int height);
}
```

- **投影**：透视。相机在球坐标 (yaw, pitch) 上绕模型中心（`y ≈ 16`）放置，
  默认 `yaw = 0, pitch = 6°, distance = 48, fov = 30°`；
  `focal = (height / 2) / tan(fov / 2)`。视图层不直接改 `distance`，
  而是维护缩放因子 `zoom` 并用 `distance = 96 / zoom` 换算（见 §3.9）。
- **图元**：每个面 = 2 个三角形（`v0v1v2` + `v0v2v3`），每个顶点带世界坐标、
  纹理坐标、相机空间深度。
- **光栅化**：包围盒 + 重心坐标；透视校正插值 `u/z`、`v/z`、`1/z`；
  **最近邻采样**（Minecraft 像素风）。
- **深度**：逐像素 Z-buffer（`float[]`，相机空间 `-z`，越小越近）。
  **不做背面剔除** —— 面数极少（约 78 面 / 156 三角形），Z-buffer 能正确处理
  披风内衬与覆盖层内面，比剔除更稳。
- **`HeadOnly`**：只画头 + 帽子层，相机固定正视，用于账户卡 40px 头像。
- **输出**：透明背景 + BGRA8 预乘 alpha（渲染过程内部按直通 alpha 混合，
  最后一次遍历预乘）。

### 3.5 新增 `SkinTextureCache.cs` + `DefaultSkinFactory.cs`（`Starcloud.Core/Services/`）

```csharp
public sealed class SkinTextureCache
{
    /// <summary>取纹理：缓存命中直接读盘；未命中下载并落盘；失败返回 null（不抛）</summary>
    public async Task<SkinBitmap?> GetAsync(string? url, CancellationToken ct = default);

    /// <summary>账户 → 皮肤（无 skin_url 时用默认形象）</summary>
    public async Task<SkinModel> ResolveSkinAsync(StoredAccount account, CancellationToken ct = default);

    /// <summary>账户 → 披风（无 cape_url / 纹理非法时返回 null）</summary>
    public async Task<SkinBitmap?> ResolveCapeAsync(StoredAccount account, CancellationToken ct = default);

    /// <summary>URL → 缓存文件名（SHA1 前 16 位 hex + ".png"），供单测与落盘共用</summary>
    public static string CacheFileName(string url);
}
```

- 缓存文件名 = `SHA1(url)` 前 16 位 hex + `.png`（`url` 本身是内容哈希，天然可长期缓存）。
- 下载超时 30 s，单次不重试；任何失败只写 `LauncherLog.Write("skin", ...)` 并返回 null。
- `DefaultSkinFactory.Create(bool slim)`：**纯代码绘制** 64×64 默认皮肤
  （肤色头部 + 眼睛与嘴、青蓝上衣、深蓝裤、手臂肤色），不使用 Mojang 版权素材。
- 用哪一张：`GameLauncher.OfflineUuid(account.Username)` 的字节奇偶决定 Steve / Alex（slim），
  保证同一玩家名永远同一形象。

### 3.6 `StoredAccount` / 认证链扩展

```csharp
// AccountService.cs StoredAccount 新增三个字段（旧 accounts.json 反序列化为 null，天然兼容）
[JsonPropertyName("skin_url")]     public string? SkinUrl { get; set; }
[JsonPropertyName("skin_variant")] public string? SkinVariant { get; set; }   // CLASSIC | SLIM
[JsonPropertyName("cape_url")]     public string? CapeUrl { get; set; }
```

- `MicrosoftAuthService.LoginResult` 追加
  `string? SkinUrl = null, string? SkinVariant = null, string? CapeUrl = null`（保持现有调用兼容）。
- `AuthenticateWithMicrosoftTokenAsync` 在已有的 `profileDoc` 上多解析两项：
  `skins[]` 取 `state == "ACTIVE"`（无则取第一项）的 `url` / `variant`；
  `capes[]` 同样取 `state == "ACTIVE"` 的 `url`，无则 null。
- `AccountStore.AddOrUpdateMicrosoft` 增加三个可选参数并在更新分支写入。
- [LaunchAccountService.ResolveAsync](../../../dotnet/src/Starcloud.Core/Services/LaunchAccountService.cs#L42-L48)
  续期成功后调用 `AddOrUpdateMicrosoft` 时把新皮肤一并回写 —— 否则游戏内换肤后
  启动器形象不会同步。

### 3.7 路径

`LauncherPaths` 新增：

```csharp
/// <summary>皮肤 / 披风纹理缓存目录</summary>
public static string SkinsCacheDir => Path.Combine(CacheDir, "skins");
```

并在 `EnsureDataDir()` 中创建。

### 3.8 AppServices 接线

`AppServices` 新增 `public SkinTextureCache Skins { get; }`，构造函数中
`Skins = new SkinTextureCache();`（放在 `MicrosoftAuth` 之后）。

### 3.9 UI：新增 `SkinFigureView` + 账户页布局

**新增 `Starcloud.App/Views/Controls/SkinFigureView.xaml(.cs)`**（`UserControl`）：

- 结构：`Grid` = 一个 `<Image x:Name="Surface" Stretch="None" HorizontalAlignment="Center"
  VerticalAlignment="Bottom" />` + 右下角一行淡出的提示文字「拖拽旋转 · 滚轮缩放」。
- `void SetAccount(StoredAccount? account, string displayName)`：
  取皮肤（缓存 / 下载 / 默认）→ 建 `SkinModel` → 起表。
- 交互：`PointerPressed/Moved/Released` 改 `yaw` / `pitch`；
  `PointerWheelChanged` 改 `zoom`（默认 2.0，钳制 1.5–4.0）；
  `pitch` 钳制 ±80°；`distance = 96 / zoom`（默认 48，即钳制区间 24–64）。
- 驱动：`DispatcherQueueTimer` 33 ms；`Loaded` / `Unloaded` 与 `Visibility` 变化时启停。
- 每帧写回（关键：.NET 8 下 `IBuffer.AsStream()` 不可用）：

```csharp
var frame = SkinRenderer.Render(_skin!, new RenderOptions(camera), w, h);
var writer = new DataWriter();
writer.WriteBytes(frame.Bgra);
var sb = SoftwareBitmap.CreateCopyFromBuffer(writer.DetachBuffer(),
    BitmapPixelFormat.Bgra8, w, h, BitmapAlphaMode.Premultiplied);
await _src.SetBitmapAsync(sb);        // 上一次未完成则跳过本帧，不排队
Surface.Source = _src;
```

- 渲染分辨率 = 控件实际尺寸 × `XamlRoot.RasterizationScale`，上限 640×960；
  超过上限时按上限渲染并由 XAML 放大。

**`AccountsPage.xaml` 第 3 行改造**（不动其它行）：

- 「保存的游戏账户」那个 `Border` 内部、原有标题行下方的 `*` 行拆成两列：
  左（`*`）= 现有 `ListView` / `EmptyStateView`；右（`Auto`，约 300–340 px）=
  账户名小标题 + `SkinFigureView`（两者仅在账户列表非空时可见）。
- 卡片头像：`AccountRow` 增加 `ImageSource? Head`，模板里 `Head != null` 时显示
  `Image`（圆形裁切），否则回落到现有首字母 `Border`。
  头像由 `SkinRenderer.Render(..., HeadOnly: true)` 生成 40×40，
  渲染耗时在微秒级，可在 `RefreshList()` 里同步做（缓存未命中时先用首字母，
  纹理下载完成后重跑一次 `RefreshList()`）。
- 预览对象 = 默认账户；卡片点击 → `PreviewAccount(uuid)`（仅切预览，不动 `IsDefault`）。
- 主界面右上角头像不动。

### 3.10 本地化

- 新增字符串写入 [en-US.json](../../../dotnet/src/Starcloud.App/Strings/en-US.json)：
  「拖拽旋转 · 滚轮缩放」「皮肤加载中…」「该账户暂无皮肤，显示默认形象」
  「查看 {0} 的形象」。
- Core 侧不产生面向用户的文案（`SkinTextureCache` 只写日志、不抛用户可见异常），
  因此无需 `Loc.TCore`。

## 4. 数据流

```
登录 / 续期 → AuthenticateWithMicrosoftTokenAsync
              → LoginResult(skins/capes)
              → AccountStore.AddOrUpdateMicrosoft 落盘 accounts.json
账户页 Loaded / 卡片点击
              → SkinFigureView.SetAccount(account)
              → SkinTextureCache.ResolveSkinAsync（缓存 → 下载 → 默认）
              → SkinModel（UV 布局）
              → 每帧 SkinRenderer.Render(相机 + 走路相位)
              → SoftwareBitmapSource → Image
```

## 5. 错误处理与降级

| 场景 | 行为 |
|---|---|
| 纹理下载失败 / 超时 | 用默认形象；仅 `[skin]` 日志，不弹窗 |
| PNG 解码失败 / 尺寸非法（隔行、位深 16、非整数倍 HD） | 同上 |
| 披风纹理非法，但皮肤正常 | 只画人像，不画披风 |
| 账户无 `skin_url`（离线账户） | 默认形象（按玩家名哈希选 Steve / Alex） |
| 控件尺寸为 0 / 未布局 | 跳过该帧 |
| 上一帧 `SetBitmapAsync` 未完成 | 跳过本帧，不排队堆积 |
| 拖拽 / 滚轮越界 | `pitch` 钳制 ±80°，`zoom` 钳制 1.5–4.0 |
| `accounts.json` 为旧格式 | 三个新字段为 null，自动走默认形象 |

## 6. 不做的事（YAGNI）

1. 皮肤上传 / 更换、披风切换（本次只读展示；上传接口涉及 Bearer 上传、违规与限流处理，
   留作独立迭代）。
2. 离线自定义皮肤在游戏内生效（需 CustomSkinLoader 之类的 mod 配合，版本相关）。
3. GPU / Composition 4×4 真 3D 渲染。
4. 走路以外的动画（跳跃、潜行、坐下）、表情、随机待机动作。
5. 皮肤历史记录、纹理本地图库、导入本地 PNG 预览。
6. 主界面右上角头像（属于 Starcloud ID 会话，不在本次范围）。

## 7. 单元测试

`dotnet/tests/Starcloud.Core.Tests/` 新增测试（全部离线、不触网）：

1. `PngDecoder`：手写最小 PNG 字节 → 解出 1×1 RGBA 正确。
2. `PngDecoder`：灰度 / RGB / 调色板（含 `tRNS`）/ 灰度+alpha 四种颜色类型各一例。
3. `PngDecoder`：分别使用 Sub / Up / Average / Paeth 滤镜的行 → 像素还原正确。
4. `PngDecoder`：隔行标志为 1 → 抛 `SkinTextureException`；截断 `IDAT` → 抛。
5. `SkinModel`：六个部件 × 六个面的 UV 与标准布局表逐项一致（含表驱动断言）。
6. `SkinModel`：slim 手臂宽度 = 3、classic = 4。
7. `SkinModel`：64×32 老皮肤的左臂 / 左腿 UV 等于右肢镜像，且无覆盖层。
8. `SkinModel`：整面全透明的覆盖层不产生几何。
9. `WalkAnimator`：`φ=0` 时双腿角度为 0；`φ=π/2` 时左右腿等大反向；
   `φ` 推进 `2π` 后姿态与初态一致。
10. `WalkAnimator`：腿摆幅落在 25°–35°、臂摆幅落在 20°–30°。
11. `SkinRenderer`：整块同色的假皮肤 + 默认相机 → 画面中心像素等于该色
    （投影与最近邻采样正确）。
12. `SkinRenderer`：Z-buffer —— 近面遮挡远面（构造前后两面不同色）。
13. `DefaultSkinFactory`：输出 64×64、关键区域不透明、`slim` 与 `classic` 手臂宽度差异正确。
14. 缓存键：同一 URL 两次 → 同一文件名；不同 URL → 不同文件名。

现有 88 个测试必须保持通过。

## 8. 实机验收

1. 微软账户登录后，账户卡出现真实头部头像（带帽子层），不再是首字母。
2. 账户页右侧出现全身形象，默认正面略带俯角；躯干、双层皮肤、披风贴合正确，
   无穿模与黑面。
3. 拖拽可任意角度查看，滚轮缩放；上下拖到极限仍限制在 ±80°，不出现翻转错乱。
4. 形象持续走路：双腿反相摆动、双臂与腿反相、身体轻微起伏、披风滞后摆动，循环无跳变。
5. 有披风的账户能看到披风（如 Migrator 披风）；无披风则不显示。
6. 断网后重启启动器 → 仍显示缓存中的皮肤与披风，不退回默认形象。
7. 点击另一个账户卡 → 预览切换为该账户；「默认」徽标不变。
8. 离线账户显示程序化默认形象且不报错；删除全部账户后形象区随空状态一起隐藏。

## 9. 风险与未决

1. **CPU 占用**：每帧软件光栅化。缓解手段为 30 fps 上限、页面不可见即停表、
   渲染分辨率上限、上一帧未完成则跳过。若高 DPI 大窗口下仍卡顿，
   降级到 15 fps 并降低分辨率上限。
2. **PNG 兼容性**：仅支持位深 8、非隔行、整数倍 HD。Mojang 纹理均满足；
   异常一律回落默认形象，不会崩。
3. **默认皮肤为程序化自绘的近似 Steve 风格**，不使用 Mojang 版权素材，
   因此观感与官方默认皮肤不完全一致。
4. **披风布局**：不支持 46×22 老披风（判定非法 → 不显示披风）。
5. **走路相位与速度是视觉自定**，不反映游戏内真实移动速度。
6. **`SoftwareBitmapSource` 每帧新建对象**可能带来 GC 压力。若实测掉帧，
   退路是复用同一 `SoftwareBitmap` 并通过 COM 互操作 `IMemoryBufferByteAccess`
   直接写显存（作为后续优化项，不在本次范围）。
7. **`SoftwareBitmap` / `SoftwareBitmapSource` 在非打包（unpackaged）WinUI 3 下的可用性**
   需在实机第 1 条验收时确认；若不可用，退路是在 Core 内补一个 PNG 编码器
   （复用解码器的 filter 与 `ZLibStream`）改用 `BitmapImage.SetSourceAsync`。
