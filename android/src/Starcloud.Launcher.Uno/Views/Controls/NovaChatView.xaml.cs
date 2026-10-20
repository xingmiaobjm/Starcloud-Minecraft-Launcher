// Nova 复用对话组件（NOVA_INTEGRATION.md：页面 / 顶栏面板 / 底部对话条共用）。
// 自身不持有会话：直接订阅 NovaRuntime 全局消息流与状态机，Unloaded 时解绑防泄漏。

using System.Collections.Specialized;
using System.Linq;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Starcloud.Launcher.Uno.Services;

namespace Starcloud.Launcher.Uno.Views.Controls;

public sealed partial class NovaChatView : UserControl
{
    private readonly AppServices _s = AppServices.Instance;
    private ScrollViewer? _scroll;   // List 模板里的滚动宿主（取一次就缓存，避免每帧走视觉树）
    private bool _scrollQueued;      // 增量可能每秒几十条，合并成一次贴底

    public NovaChatView()
    {
        InitializeComponent();
        ChatList.ItemsSource = _s.NovaRuntime.Messages;
        Loaded += OnLoaded;
        Unloaded += (_, _) => Detach();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _s.NovaRuntime.StateChanged += Runtime_StateChanged;
        _s.NovaRuntime.InputRequested += Runtime_InputRequested;
        _s.NovaRuntime.Messages.CollectionChanged += Messages_CollectionChanged;
        _s.NovaRuntime.MessageUpdated += Message_Updated;
        UpdateBusy();
        UpdateEmptyState();
        InputBox.Focus(FocusState.Programmatic);
    }

    private void Detach()
    {
        _s.NovaRuntime.StateChanged -= Runtime_StateChanged;
        _s.NovaRuntime.InputRequested -= Runtime_InputRequested;
        _s.NovaRuntime.Messages.CollectionChanged -= Messages_CollectionChanged;
        _s.NovaRuntime.MessageUpdated -= Message_Updated;
        _scroll = null; // 重新挂载后视觉树会重建，缓存作废
    }

    /// <summary>空会话才铺"邀请你开口"的空状态：有对话时那块地方留给内容。</summary>
    private void UpdateEmptyState() =>
        ChatEmpty.Visibility = _s.NovaRuntime.Messages.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    private void Runtime_StateChanged(NovaState _) => UpdateBusy();

    private void UpdateBusy()
    {
        var busy = _s.NovaRuntime.State == NovaState.Thinking;
        SendBtn.IsEnabled = !busy;
        SendProgress.IsActive = busy;
    }

    private void Runtime_InputRequested(string text)
    {
        InputBox.Text = text;
        InputBox.Focus(FocusState.Programmatic);
        InputBox.SelectionStart = InputBox.Text.Length;
    }

    private void Messages_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateEmptyState();
        // 等一项布局后再滚动到底（CollectionChanged 时容器尚未生成）
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace)) return;
        var items = e.NewItems;
        if (items is not { Count: > 0 }) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            ChatList.ScrollIntoView(items[e.Action == NotifyCollectionChangedAction.Add ? items.Count - 1 : 0]!);
            // ScrollIntoView 是瞬移；再让滚动条自己动画收尾，读起来更顺（框架 ChangeView 的动画参数）
            _scroll ??= FindScrollViewer(ChatList);
            _scroll?.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: false);
        });
    }

    /// <summary>
    /// 流式增量到达 → 视口跟着贴底。
    /// 以前只在「集合变化」时滚动，回答越写越长就会滚出屏幕外，用户以为没在回。
    /// 增量频率高，这里合并成一次低优先级贴底（跟手且不抢渲染）。
    /// </summary>
    private void Message_Updated(NovaChatMessage _) => ScrollToEndSoon();

    private void ScrollToEndSoon()
    {
        if (_scrollQueued) return;
        _scrollQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _scrollQueued = false;
            _scroll ??= FindScrollViewer(ChatList);
            // 跟随增长中的元素：动画滚动会一直落后，这里直接贴底
            _scroll?.ChangeView(null, _scroll.ScrollableHeight, null, disableAnimation: true);
        });
    }

    /// <summary>
    /// 「等待中」的三个点：每个点起一条永续 Storyboard，用 BeginTime 错开相位形成打字节奏。
    /// 用 Opacity 做独立动画（不参与布局），虚拟化回收时随元素生命周期停表。
    /// </summary>
    private void TypingDot_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement dot) return;
        var phase = dot.Tag is string s && int.TryParse(s, out var i) ? i : 0;

        var sb = new Storyboard
        {
            RepeatBehavior = RepeatBehavior.Forever,
            BeginTime = TimeSpan.FromMilliseconds(phase * 150),
        };
        var anim = new DoubleAnimationUsingKeyFrames();
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero), Value = 0.25,
        });
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(240)), Value = 1,
        });
        anim.KeyFrames.Add(new LinearDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(480)), Value = 0.25,
        });
        Storyboard.SetTarget(anim, dot);
        Storyboard.SetTargetProperty(anim, "Opacity");
        sb.Children.Add(anim);
        sb.Begin();
        dot.Unloaded += (_, _) => sb.Stop();
    }

    /// <summary>List 内部滚动宿主（List 不自带滚动定位 API，需要取模板里的 ScrollViewer）</summary>
    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv) return sv;
            if (FindScrollViewer(child) is { } nested) return nested;
        }
        return null;
    }

    // ── 输入 / 快捷 ──

    private async void Send_Click(object sender, RoutedEventArgs e) => await SendAsync();

    private void InputBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            e.Handled = true;
            _ = SendAsync();
        }
    }

    private async Task SendAsync()
    {
        var input = InputBox.Text.Trim();
        if (input.Length == 0 || _s.NovaRuntime.State == NovaState.Thinking) return;
        InputBox.Text = "";
        await _s.NovaRuntime.SendAsync(input, FullLogSwitch.IsOn);
        InputBox.Focus(FocusState.Programmatic);
    }

    /// <summary>打开「发送完整日志」时才提示更贵（规格 §5）。</summary>
    private void FullLog_Toggled(object sender, RoutedEventArgs e) =>
        FullLogHint.Visibility = FullLogSwitch.IsOn ? Visibility.Visible : Visibility.Collapsed;

    // ── 确认清单：确认后才执行（执行走 NovaRuntime，日志事件由右栏订阅刷新） ──

    private async void PlanConfirm_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NovaChatMessage { PlanState: "pending" } msg)
            await _s.NovaRuntime.ConfirmPlanAsync(msg);
    }

    private void PlanCancel_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is NovaChatMessage { PlanState: "pending" } msg)
            _s.NovaRuntime.CancelPlan(msg);
    }
}
