using Starcloud.Multiplayer.Cli;

var options = CliOptions.Parse(args);

using var cts = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    // 第一下 Ctrl+C 走优雅收尾（关房 / 停内核），再按一次强行退出。
    e.Cancel = true;
    cts.Cancel();
};

try
{
    return await CliApp.RunAsync(options, cts.Token);
}
catch (OperationCanceledException)
{
    Console.WriteLine("已中断。");
    return 130;
}
catch (Exception ex)
{
    // 验收工具：失败一律把类型与堆栈打全，方便定位（正常路径不会走到这里）。
    Console.WriteLine($"失败：{ex}");
    return 1;
}
