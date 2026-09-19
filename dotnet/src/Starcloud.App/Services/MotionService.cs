// 界面动效总开关（D 组 ②：界面打磨与动效，设计 §4）

using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.Core.Services;

namespace Starcloud.App.Services;

/// <summary>
/// 界面动效总开关：解析设置中的 <c>ui_animations</c>，并探测系统「在 Windows 中显示动画」偏好。
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

    /// <summary>当前是否启用界面动效：system 跟随系统 / on 始终开启 / off 关闭</summary>
    public bool Enabled => _settings().UiAnimations switch
    {
        "on" => true,
        "off" => false,
        _ => SystemAnimationsEnabled,
    };

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
