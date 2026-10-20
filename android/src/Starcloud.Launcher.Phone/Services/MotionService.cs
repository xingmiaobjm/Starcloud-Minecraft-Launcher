// 界面动效总开关（D 组 ②：界面打磨与动效，设计 §4）

using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.Core.Services;

namespace Starcloud.Launcher.Phone.Services;

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
                // 安卓端无 WinUI 的 UISettings：改读全局「动画时长缩放」，0 = 系统动画已关
                // （含「关闭动画」无障碍档）；读取失败按开启处理，绝不影响功能
                return global::Android.Provider.Settings.Global.GetFloat(
                    global::Android.App.Application.Context.ContentResolver,
                    global::Android.Provider.Settings.Global.AnimatorDurationScale, 1f) > 0f;
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
        // 安卓端框架级转场一律抑制：这台设备上转场起始帧（页面物化 + 整页动画 + 列表错峰
        // 三者叠加）是切页掉帧与「点击延迟感」的主因；页面内容仍由 StaggerIn 等错峰动效
        // 承担入场表现，动效语言保留，但点按即刻呈现
        return new SuppressNavigationTransitionInfo();
    }

    /// <summary>返回上一层：框架的滑动转场（Uno 未实现 Effect 属性，保留默认滑动方向），与进入时的 drill-in 互补；关闭动效时抑制。</summary>
    public static NavigationTransitionInfo BackTransition(bool enabled) =>
        enabled
            // Uno 的 SlideNavigationTransitionInfo 未实现 Effect（从左/从右），只能用默认滑动效果
            ? new SlideNavigationTransitionInfo()
            : new SuppressNavigationTransitionInfo();
}
