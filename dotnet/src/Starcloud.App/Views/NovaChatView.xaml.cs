// Nova 复用对话组件（NOVA_INTEGRATION.md：页面 / 顶栏面板 / 底部对话条共用）。
// 自身不持有会话：直接订阅 NovaRuntime 全局消息流与状态机，Unloaded 时解绑防泄漏。

using System.Collections.Specialized;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Services;

namespace Starcloud.App.Views;

public sealed partial class NovaChatView : UserControl
{
    private readonly AppServices _s = AppServices.Instance;

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
        UpdateBusy();
        InputBox.Focus(FocusState.Programmatic);
    }

    private void Detach()
    {
        _s.NovaRuntime.StateChanged -= Runtime_StateChanged;
        _s.NovaRuntime.InputRequested -= Runtime_InputRequested;
        _s.NovaRuntime.Messages.CollectionChanged -= Messages_CollectionChanged;
    }

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
        // 等一项布局后再滚动到底（CollectionChanged 时容器尚未生成）
        if (e.Action is not (NotifyCollectionChangedAction.Add or NotifyCollectionChangedAction.Replace)) return;
        var items = e.NewItems;
        if (items is not { Count: > 0 }) return;
        DispatcherQueue.TryEnqueue(() =>
        {
            ChatList.ScrollIntoView(items[e.Action == NotifyCollectionChangedAction.Add ? items.Count - 1 : 0]!);
            // ScrollIntoView 是瞬移；再让滚动条自己动画收尾，读起来更顺（框架 ChangeView 的动画参数）
            if (FindScrollViewer(ChatList) is { } sv)
            {
                sv.ChangeView(null, sv.ScrollableHeight, null, disableAnimation: false);
            }
        });
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

    private void Quick_Click(object sender, RoutedEventArgs e)
    {
        if (_s.NovaRuntime.State == NovaState.Thinking) return;
        var tag = (sender as FrameworkElement)?.Tag?.ToString();
        if (!string.IsNullOrEmpty(tag)) _ = _s.NovaRuntime.SendAsync(tag);
    }

    // ── 确认清单：点头才动手（执行走 NovaRuntime，流水事件由右栏订阅刷新） ──

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
