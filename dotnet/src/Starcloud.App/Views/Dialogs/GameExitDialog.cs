// 游戏结束后的房间处理询问：与「更新提示」同级——全局弹窗，任何页面都能看到，
// 不依赖联机页是否打开（玩家常常游戏结束后直接停在主页）。

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Starcloud.App.Localization;
using Starcloud.App.Services;

namespace Starcloud.App.Views.Dialogs;

public static class GameExitDialog
{
    /// <summary>
    /// 弹出「游戏已结束」询问。默认按钮是 120 秒后退出（玩家常常只是重开游戏）；
    /// 直接关掉弹窗（右上角 ×）同样按 120 秒处理，不打断玩家。
    /// </summary>
    public static async Task<RoomRuntime.GameExitChoice> AskAsync(XamlRoot? root)
    {
        if (root is null) return RoomRuntime.GameExitChoice.After120;
        try
        {
            var dialog = new ThemedDialog
            {
                Title = Loc.T("游戏已结束"),
                Content = Loc.T("联机房间还开着。要现在关闭房间，还是再留 120 秒？（这期间重开游戏会自动保留房间）"),
                PrimaryButtonText = Loc.T("120 秒后退出"),
                CloseButtonText = Loc.T("立即退出"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = root,
            };
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.None
                ? RoomRuntime.GameExitChoice.Now      // 点了「立即退出」
                : RoomRuntime.GameExitChoice.After120;
        }
        catch
        {
            return RoomRuntime.GameExitChoice.After120;
        }
    }
}
