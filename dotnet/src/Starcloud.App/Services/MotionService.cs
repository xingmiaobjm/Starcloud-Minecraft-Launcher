// 界面动效四档总控（原「总开关」升级，设计 §4）：
//   minimal（极少）  只有最基本的页面切换转场；
//   lite（较少）    在极少之上，+跟手光斑、毛玻璃卡片与悬浮/入场微反馈；
//   balanced（均衡） 在较少之上，+全局实时鼠标光照、按钮阴影跟随鼠标、边缘引力形变；
//   full（全部）    拥有全部动效（弹簧形变、窗口环境光斑），建议高性能独立显卡机型。
// 四档均可在设置里手动切换；系统「在 Windows 中显示动画」关闭时一切动效统一抑制（无障碍）。

using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.Core.Services;

namespace Starcloud.App.Services;

/// <summary>界面动效等级（与设置键 ui_motion_level 一一对应）。</summary>
public enum UiMotionLevel
{
    /// <summary>极少：只有最基本的页面切换动画</summary>
    Minimal,
    /// <summary>较少：极少 + 跟手光斑 + 毛玻璃 + 悬浮/入场微反馈</summary>
    Lite,
    /// <summary>均衡：较少 + 全局鼠标光照 + 阴影跟手 + 边缘引力形变（默认）</summary>
    Balanced,
    /// <summary>全部：均衡 + 弹簧形变 + 窗口环境光斑（高性能独显机型推荐）</summary>
    Full,
}

/// <summary>
/// 界面动效等级总控：解析设置中的 <c>ui_motion_level</c>，并探测系统「在 Windows 中显示动画」偏好。
/// 任何探测失败或动效不可用的情形都静默降级为无动效，绝不影响功能。
/// </summary>
public sealed class MotionService
{
    private readonly Func<LauncherSettings> _settings;

    public MotionService(Func<LauncherSettings> settings) => _settings = settings;

    /// <summary>系统是否开启动画（读取失败按开启处理）</summary>
    public static bool SystemAnimationsEnabled
    {
        get
        {
            try
            {
                return new Windows.UI.ViewManagement.UISettings().AnimationsEnabled;
            }
            catch
            {
                return true;
            }
        }
    }

    /// <summary>当前动效等级（设置值非法时 Core 侧钳制已兜底，这里再按默认档收口）</summary>
    public UiMotionLevel Level => _settings().UiMotionLevel switch
    {
        "minimal" => UiMotionLevel.Minimal,
        "lite" => UiMotionLevel.Lite,
        "full" => UiMotionLevel.Full,
        _ => UiMotionLevel.Balanced,
    };

    /// <summary>
    /// 合成动效（悬浮反馈、入场淡入、光斑等「非转场」动效）是否可用：较少及以上。
    /// 各页/各行为的既有判定点都读它，等级降到极少时全部落为「直接到位、零开销」。
    /// </summary>
    public bool Enabled => Level >= UiMotionLevel.Lite && SystemAnimationsEnabled;

    /// <summary>
    /// 页面切换转场是否可用：极少也保留（这就是该档「最基本的切换页面动画」）；
    /// 只有系统关闭动画时统一抑制。
    /// </summary>
    public bool Transitions => SystemAnimationsEnabled;

    /// <summary>全局鼠标光照族（边缘光、阴影跟随、引力形变）：均衡及以上</summary>
    public bool LightFx => Level >= UiMotionLevel.Balanced && SystemAnimationsEnabled;

    /// <summary>全部档专属（弹簧形变、窗口环境光斑）</summary>
    public bool FullFx => Level == UiMotionLevel.Full && SystemAnimationsEnabled;

    /// <summary>按开关与层级选择页面转场；关闭时返回抑制转场</summary>
    public static NavigationTransitionInfo TransitionFor(bool enabled, bool drillIn = false)
    {
        if (!enabled) return new SuppressNavigationTransitionInfo();
        return drillIn ? new DrillInNavigationTransitionInfo() : new EntranceNavigationTransitionInfo();
    }

    /// <summary>返回上一层：框架的滑动转场（从左侧进入），与进入时的 drill-in 互补；关闭动效时抑制。</summary>
    public static NavigationTransitionInfo BackTransition(bool enabled) =>
        enabled
            ? new SlideNavigationTransitionInfo { Effect = SlideNavigationTransitionEffect.FromLeft }
            : new SuppressNavigationTransitionInfo();
}
