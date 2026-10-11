namespace Starcloud.Multiplayer.Client;

/// <summary>
/// 平台差异的唯一收口。桌面实现见 <see cref="DesktopPlatformHost"/>；
/// 将来安卓端换一个实现即可，内核其余部分不用改。
/// </summary>
public interface IPlatformHost
{
    /// <summary>内核自己的数据目录（会话、设置、内核二进制、日志都放这儿）。</summary>
    string DataDirectory { get; }

    /// <summary>写一行日志（各平台落点不同：桌面写文件 + 控制台，移动端写系统日志）。</summary>
    void Log(string message);
}
