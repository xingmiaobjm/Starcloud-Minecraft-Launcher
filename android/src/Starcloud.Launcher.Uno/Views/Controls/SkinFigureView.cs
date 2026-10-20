// 账户详情页右侧的玩家 3D 形象。
//
// 桌面蓝本里它与 Skin3DView 是两个并存实现：Skin3DView 是旧版（WebView2 抓帧 + Win2D），
// SkinFigureView 是新版（Starcloud.Core 光栅化 + Win2D 原生渲染）。安卓侧没有 Win2D
//（csproj 不引用 Microsoft.Graphics.Canvas），原生渲染管线无法复刻，两版合并为同一个
// 引擎实现（可见 WebView2 门面 + skinview3d，见 Skin3DView 顶部说明）。
//
// 保留本类型作为语义别名：账户详情页 XAML 仍写 ctrl:SkinFigureView（与桌面结构 1:1），
// 公共 API 与桌面同形——WalkEnabled、SetAccount（5 参含本地皮肤）、SetLocalSkin 均由基类实现。

namespace Starcloud.Launcher.Uno.Views.Controls;

/// <summary>账户详情页的 3D 玩家形象（安卓侧与 Skin3DView 同一实现，保留桌面类型名与 API）。</summary>
public sealed class SkinFigureView : Skin3DView
{
}
