using Android.Content;
using Starcloud.Multiplayer.Client;
// 类里声明的 Log 方法会遮蔽 Android.Util.Log 类名，用别名指代后者。
using AndroidLog = Android.Util.Log;

namespace Starcloud.Multiplayer.Client.Android;

/// <summary>安卓平台宿主：数据目录在应用私有存储（FilesDir/multiplayer），日志走 logcat（tag：StarcloudMp）。</summary>
public sealed class AndroidPlatformHost : IPlatformHost
{
    private const string LogTag = "StarcloudMp";

    public AndroidPlatformHost(Context context)
    {
        DataDirectory = Path.Combine(context.FilesDir!.AbsolutePath, "multiplayer");
        Directory.CreateDirectory(DataDirectory);
    }

    public string DataDirectory { get; }

    public void Log(string message) => AndroidLog.Debug(LogTag, message);
}
