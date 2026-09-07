// 主窗口：Mica 背景 + NavigationView 导航

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Starcloud.App.Views;

namespace Starcloud.App;

public sealed partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();

        SystemBackdrop = new MicaBackdrop();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        Title = "Starcloud Minecraft Launcher";

        Nav.SelectedItem = Nav.MenuItems[0];
        ContentFrame.Navigate(typeof(HomePage));
    }

    private void Nav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer is not { } item) return;
        var tag = item.Tag?.ToString();
        Type page = tag switch
        {
            "versions" => typeof(VersionsPage),
            "saves" => typeof(SavesPage),
            "download" => typeof(DownloadPage),
            "accounts" => typeof(AccountsPage),
            "ai" => typeof(AiPage),
            "settings" => typeof(SettingsPage),
            "logs" => typeof(LogsPage),
            _ => typeof(HomePage),
        };
        if (ContentFrame.CurrentSourcePageType != page)
            ContentFrame.Navigate(page);
        // 选择完成后自动收起侧栏，保持内容区宽畅
        Nav.IsPaneOpen = false;
    }

    /// <summary>页面内跳转入口：通过设置导航栏选中项驱动 Frame 导航，保证左侧选中状态同步。</summary>
    public void NavigateTo(string tag)
    {
        var target = Nav.MenuItems.Concat(Nav.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(i => i.Tag?.ToString() == tag);
        if (target is not null && !ReferenceEquals(Nav.SelectedItem, target))
            Nav.SelectedItem = target;
    }
}
