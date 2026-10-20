// 游戏前台服务：游戏进程运行期间把应用提升为前台服务，
// 防止 JVM 线程（非后台线程）存活时进程被系统按后台限制冻结/回收。
// dataSync 类型对应 manifest 的 foregroundServiceType 声明（API 34+ 校验）。

using Android.App;
using Android.Content;
using Android.Content.PM;
using Android.OS;

namespace Starcloud.Android.Host;

[Service(Name = "com.starcloud.launcher.GameForegroundService",
    ForegroundServiceType = ForegroundService.TypeDataSync,
    Enabled = true, Exported = false)]
public class GameForegroundService : Service
{
    private const string ChannelId = "starcloud_game";
    private const string ChannelName = "游戏运行";
    private const int NotificationId = 1;

    // 前台化握手：StartForegroundService 后系统只给 5 秒窗口执行 StartForeground，
    // 否则 ForegroundServiceDidNotStartInTimeException 直接杀进程。启动链必须在
    // 主线程空闲时先请求前台并握手确认，再进入重活（组件解压 + JVM 拉起会打满 CPU）
    private static TaskCompletionSource<bool>? _foregroundReady;

    /// <summary>提升为前台服务（游戏启动时调用）</summary>
    public static void Start(Context context)
    {
        if (OperatingSystem.IsAndroidVersionAtLeast(26))
        {
            var channel = new NotificationChannel(
                ChannelId, ChannelName, NotificationImportance.Low);
            var manager = context.GetSystemService(NotificationService) as NotificationManager;
            manager?.CreateNotificationChannel(channel);
        }

        _foregroundReady = new TaskCompletionSource<bool>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var intent = new Intent(context, typeof(GameForegroundService));
        context.StartForegroundService(intent);
    }

    /// <summary>
    /// 等待服务完成前台化（OnStartCommand 已执行 StartForeground）。
    /// 超时不判失败：服务稍后仍会自行前台化，调用方继续链路即可
    /// </summary>
    public static Task WaitForForegroundAsync(TimeSpan timeout) =>
        _foregroundReady?.Task.WaitAsync(timeout) ?? Task.CompletedTask;

    /// <summary>退出前台服务（游戏退出时调用）</summary>
    public static void Stop(Context context)
    {
        _foregroundReady = null;
        context.StopService(new Intent(context, typeof(GameForegroundService)));
    }

    public override IBinder? OnBind(Intent? intent) => null;

    public override StartCommandResult OnStartCommand(Intent? intent, StartCommandFlags flags, int startId)
    {
        var notification = new Notification.Builder(this, ChannelId)
            .SetContentTitle("Starcloud 启动器")
            .SetContentText("游戏正在运行")
            .SetSmallIcon(global::Android.Resource.Drawable.SymDefAppIcon)
            .SetOngoing(true)
            .Build();

        // API 29+ 必须三参重载声明服务类型，与 manifest 的 foregroundServiceType 一致
        if (OperatingSystem.IsAndroidVersionAtLeast(29))
        {
            StartForeground(NotificationId, notification, ForegroundService.TypeDataSync);
        }
        else
        {
            StartForeground(NotificationId, notification);
        }
        _foregroundReady?.TrySetResult(true);
        return StartCommandResult.Sticky;
    }
}
