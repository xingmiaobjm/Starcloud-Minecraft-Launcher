namespace Starcloud.Multiplayer.Tests;

/// <summary>测试用平台宿主：数据目录落在临时目录，日志丢掉（别把测试输出刷满）。</summary>
internal sealed class TestPlatformHost : Starcloud.Multiplayer.Client.IPlatformHost
{
    public TestPlatformHost(string dataDirectory)
    {
        DataDirectory = dataDirectory;
        Directory.CreateDirectory(dataDirectory);
    }

    public string DataDirectory { get; }

    public void Log(string message)
    {
        // 测试里不需要日志。
    }
}
