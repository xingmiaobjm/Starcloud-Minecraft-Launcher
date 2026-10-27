// 亚克力背景手动挂载（不走 Window.SystemBackdrop）。
//
// 为什么不用 SystemBackdrop 子类：框架会在自己的时机回调
// SystemBackdrop.OnDefaultSystemBackdropConfigurationChanged，此时 target/xamlRoot 可能
// 已失效，基类直接抛 ArgumentException——stowed exception（0xc000027b）能绕过托管
// UnhandledException 处理器直接终止进程（弹公告的 Popup 打开时就触发过）。
// 这里的做法是老式手动挂载：DesktopAcrylicController 直接对窗口目标工作，
// 激活态/主题变化自己接线，框架完全没有回调路径可言。

using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Starcloud.Core.Services;
using WinRT;

namespace Starcloud.App.Services;

/// <summary>把 DesktopAcrylicController 手动挂到窗口（替代 TransparentAcrylicBackdrop）。</summary>
internal sealed class AcrylicBackdropHost
{
    private readonly Window _window;
    private readonly FrameworkElement _root;
    private DesktopAcrylicController? _controller;
    private SystemBackdropConfiguration? _configuration;
    private bool _connected;

    private ElementTheme _theme = ElementTheme.Light;
    private bool _followTheme = true;
    private double? _maskOpacity;

    public AcrylicBackdropHost(Window window, FrameworkElement root)
    {
        _window = window;
        _root = root;
        try
        {
            _controller = new DesktopAcrylicController();
            _configuration = new SystemBackdropConfiguration
            {
                IsInputActive = true,
            };
            _controller.AddSystemBackdropTarget(window.As<ICompositionSupportsSystemBackdrop>());
            _controller.SetSystemBackdropConfiguration(_configuration);
            _connected = true;

            ApplyAcrylicTheme(root.ActualTheme);
            root.ActualThemeChanged += OnRootThemeChanged;
            window.Activated += OnWindowActivated;
            window.Closed += (_, _) => Teardown();
        }
        catch (Exception ex)
        {
            Teardown();
            LauncherLog.Error("ui", "亚克力背景初始化失败，退回无背景", ex);
        }
    }

    /// <summary>
    /// 应用「背景与外观 → 毛玻璃强度」。值就是遮罩不透明度（0–1）：
    /// 浅色主题默认 0.25、深色 0.45，与 AppearanceService.DefaultMaskOpacity 同源。
    /// </summary>
    public void SetGlassStrength(bool followTheme, double? maskOpacity)
    {
        _followTheme = followTheme;
        _maskOpacity = maskOpacity;
        ApplyAcrylicTheme(_theme);
    }

    private void OnWindowActivated(object sender, WindowActivatedEventArgs args)
    {
        if (_configuration is null) return;
        try
        {
            _configuration.IsInputActive =
                args.WindowActivationState != WindowActivationState.Deactivated;
        }
        catch
        {
            // 静默：激活态同步失败只是失去焦点时不再压暗
        }
    }

    private void OnRootThemeChanged(FrameworkElement sender, object args)
    {
        // 只改控制器自己的配色，不写配置的 Theme（原因见类注释）
        ApplyAcrylicTheme(sender.ActualTheme);
    }

    /// <summary>深浅主题使用不同的亚克力配方：浅色通透偏白，深色为近黑暗玻璃并保留可见的模糊背景。</summary>
    private void ApplyAcrylicTheme(ElementTheme theme)
    {
        _theme = theme;
        if (!_connected || _controller is null) return;
        var dark = theme == ElementTheme.Dark;

        // 基准值：浅色 #FC 0.25 / 深色 #14 0.45
        var baseMask = dark ? 0.45 : 0.25;
        var mask = _followTheme || _maskOpacity is not { } m ? baseMask : Math.Clamp(m, 0, 1);

        try
        {
            _controller.TintColor = dark
                ? Windows.UI.Color.FromArgb(0xFF, 0x14, 0x14, 0x14)
                : Windows.UI.Color.FromArgb(0xFF, 0xFC, 0xFC, 0xFC);
            _controller.TintOpacity = (float)Math.Clamp(mask, 0.02, 0.95);
            // 亮度层按同一比例缩放：强度拉到 0 时窗口几乎全透，拉到 1 时是浓毛玻璃
            _controller.LuminosityOpacity = (float)Math.Clamp((dark ? 0.30 : 0.18) * (mask / baseMask), 0.02, 0.95);
            _controller.FallbackColor = dark
                ? Windows.UI.Color.FromArgb(0xFF, 0x11, 0x11, 0x11)
                : Windows.UI.Color.FromArgb(0xFF, 0xF3, 0xF3, 0xF3);
        }
        catch (Exception ex)
        {
            LauncherLog.Error("ui", "亚克力背景参数设置失败", ex);
        }
    }

    private void Teardown()
    {
        _connected = false;
        try
        {
            _controller?.RemoveAllSystemBackdropTargets();
            _controller = null;
            _configuration = null;
        }
        catch
        {
            // 静默：窗口关闭路径上的清理失败无意义
        }
    }
}
