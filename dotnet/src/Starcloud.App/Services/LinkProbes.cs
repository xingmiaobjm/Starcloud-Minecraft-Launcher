// 通道探针（App 层实现，注入给 Core 的 DeviceLinkService）：
// 蓝牙与星闪的可用性探测需要 Windows 专属 API（Radio / 注册表），进不了纯 C# 的 Core。

using Microsoft.Win32;
using Starcloud.Core.Services;
using Windows.Devices.Radios;

namespace Starcloud.App.Services;

public static class LinkProbes
{
    /// <summary>
    /// 蓝牙通道：探测本机蓝牙无线电。协议与数据面已就绪（帧协议承载无关），
    /// 但 RFCOMM 监听需要应用包身份声明 bluetooth capability——unpackaged 发布形态
    /// 拿不到，所以探测到适配器也只能如实标注「待应用打包后启用」。
    /// </summary>
    public static async Task<LinkChannel> BluetoothAsync()
    {
        try
        {
            var access = await Radio.RequestAccessAsync();
            if (access != RadioAccessStatus.Allowed)
                return new LinkChannel("蓝牙", LinkChannelStatus.NeedsPackaging, "系统未授权无线电访问（未打包应用受限）");
            var radios = await Radio.GetRadiosAsync();
            var bt = radios.FirstOrDefault(r => r.Kind == RadioKind.Bluetooth);
            return bt is null
                ? new LinkChannel("蓝牙", LinkChannelStatus.Unavailable, "本机没有蓝牙适配器")
                : new LinkChannel("蓝牙", LinkChannelStatus.NeedsPackaging,
                    "检测到蓝牙适配器；传输需应用包身份，当前以局域网通道为准");
        }
        catch
        {
            return new LinkChannel("蓝牙", LinkChannelStatus.Unavailable, "探测失败");
        }
    }

    /// <summary>
    /// 星闪（NearLink）通道：华为私有栈，当前以「本机是否装了 NearLink 驱动服务」为判据。
    /// 驱动在位时视为可用（链路帧协议与 LAN 同一套，接入即用）；否则如实提示无环境。
    /// </summary>
    public static Task<LinkChannel> NearLinkAsync()
    {
        try
        {
            var has = false;
            using var services = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Services");
            if (services is not null)
            {
                foreach (var name in services.GetSubKeyNames())
                {
                    if (name.Contains("NearLink", StringComparison.OrdinalIgnoreCase))
                    {
                        has = true;
                        break;
                    }
                }
            }
            return Task.FromResult(has
                ? new LinkChannel("星闪（NearLink）", LinkChannelStatus.Available, "检测到 NearLink 驱动环境")
                : new LinkChannel("星闪（NearLink）", LinkChannelStatus.Unavailable, "本机未检测到星闪（NearLink）环境"));
        }
        catch
        {
            return Task.FromResult(new LinkChannel("星闪（NearLink）", LinkChannelStatus.Unavailable, "探测失败"));
        }
    }
}
