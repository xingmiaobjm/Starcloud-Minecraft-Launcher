using Starcloud.Multiplayer.Core.Protocol;
using Starcloud.Multiplayer.MockServer;

// 用法：Starcloud.Multiplayer.MockServer [--port N] [--data DIR] [--seed FILE]
var port = 0;
var dataDir = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "Starcloud-Multiplayer", "mock");
var seedFile = Path.Combine(AppContext.BaseDirectory, "seed.json");

for (var i = 0; i < args.Length - 1; i++)
{
    switch (args[i])
    {
        case "--port" when int.TryParse(args[i + 1], out var parsed):
            port = parsed;
            break;
        case "--data":
            dataDir = args[i + 1];
            break;
        case "--seed":
            seedFile = args[i + 1];
            break;
    }
}

Directory.CreateDirectory(dataDir);
var handle = await MockServerHost.StartAsync(seedFile, Path.Combine(dataDir, "state.json"), port);

Console.WriteLine($"联机 mock 服务端已启动：{handle.BaseAddress}{MpProtocol.BasePath}");
Console.WriteLine($"seed：{seedFile}");
Console.WriteLine("按 Ctrl+C 退出。");

await handle.App.WaitForShutdownAsync();
await handle.DisposeAsync();
