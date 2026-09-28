// 游戏运行态：历史落盘 / 进程采样 / 跨重启恢复 / 启动上下文 / 敏感信息不落盘
// 设计：docs/designs/2026-09-26-game-running-status-panel-design.md
//
// 三条铁律（否则测试会污染开发机或变得不确定）：
// 1. 不依赖机器上真的跑着 java —— 判活探针一律注入；
// 2. 不写真实的 LauncherPaths.DataDir —— 历史文件一律走临时目录；
// 3. 不碰 GameRuntimeMonitor.Instance 单例 —— 每个用例 new 一个自用实例。

using System.Diagnostics;
using Starcloud.Core.Services;
using Xunit;

namespace Starcloud.Core.Tests;

public class GameRuntimeTests
{
    // ── 1. 运行历史上限裁剪 ──

    [Fact]
    public void 运行历史超过上限时丢弃最旧记录()
    {
        using var data = new TempDataDir();
        var store = new GameRunHistoryStore(data.Path);

        for (var i = 0; i < 60; i++) Assert.True(store.Append(Record($"v{i}", i)));

        Assert.Equal(GameRunHistoryStore.MaxRecords, store.Records.Count);
        Assert.Equal("v59", store.Records[0].VersionId);   // 新 → 旧
        Assert.Equal("v10", store.Records[^1].VersionId);
        Assert.DoesNotContain(store.Records, r => r.VersionId is "v0" or "v9");
    }

    // ── 2. 落盘原子性：写失败不得破坏既有文件 ──

    [Fact]
    public void 落盘失败时既有历史文件逐字不变()
    {
        using var data = new TempDataDir();
        var store = new GameRunHistoryStore(data.Path);
        Assert.True(store.Append(Record("v1", 0)));
        var before = File.ReadAllText(store.FilePath);

        // 用同名目录占住 .tmp：Write() 的 File.WriteAllText 必然失败
        Directory.CreateDirectory(store.FilePath + ".tmp");
        try
        {
            Assert.False(store.Append(Record("v2", 1)));
        }
        finally
        {
            Directory.Delete(store.FilePath + ".tmp");
        }

        Assert.Equal(before, File.ReadAllText(store.FilePath));
        Assert.Single(store.Records);
        Assert.Equal("v1", store.Records[0].VersionId);   // 内存镜像也回滚
    }

    // ── 3. 损坏文件：读兜底 ──

    [Fact]
    public void 历史文件损坏时读取返回空列表且不抛()
    {
        using var data = new TempDataDir();
        File.WriteAllText(Path.Combine(data.Path, "game.history.json"), "{ 这不是 json");

        var store = new GameRunHistoryStore(data.Path);

        Assert.False(store.TryLoad());
        Assert.Empty(store.Records);
        Assert.True(store.LoadFailed);
    }

    // ── 4. 损坏文件：写必须拒绝（否则用户攒的历史被一次空库覆盖） ──

    [Fact]
    public void 历史读取失败后拒绝写入且文件不变()
    {
        using var data = new TempDataDir();
        var file = Path.Combine(data.Path, "game.history.json");
        File.WriteAllText(file, "{ 这不是 json");
        var before = File.ReadAllText(file);

        var store = new GameRunHistoryStore(data.Path);
        Assert.False(store.TryLoad());

        Assert.False(store.Append(Record("v1", 0)));
        Assert.Equal(before, File.ReadAllText(file));
        Assert.Empty(store.Records);
    }

    // ── 5. 采样器：本进程可采样 ──

    [Fact]
    public void 采样器能读到本进程的CPU与内存()
    {
        using var sampler = new ProcessSampler(Environment.ProcessId);

        Assert.True(sampler.TrySample(collectGpu: false, out var s));
        Assert.True(s.CpuPercent >= 0, $"CPU% 不应为负：{s.CpuPercent}");
        Assert.True(s.WorkingSetBytes > 0, "工作集应大于 0");
        Assert.True(s.ThreadCount > 0, "线程数应大于 0");
    }

    // ── 6. 采样器：GPU 计数器不可用给 null，不是 0 ──

    [Fact]
    public void 采样器关闭GPU采集时给null而不是零()
    {
        using var sampler = new ProcessSampler(Environment.ProcessId);

        Assert.True(sampler.TrySample(collectGpu: false, out var s));
        Assert.Null(s.GpuPercent);
    }

    // ── 7. 采样器：进程已退出返回 false，不抛 ──

    [Fact]
    public void 采样器对已退出进程返回false而不抛()
    {
        var psi = OperatingSystem.IsWindows()
            ? new ProcessStartInfo("cmd", "/c exit")
            : new ProcessStartInfo("/bin/sh", "-c exit");
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;

        int pid;
        using (var child = Process.Start(psi))
        {
            Assert.NotNull(child);
            child!.WaitForExit(5000);
            pid = child.Id;
        }

        using var sampler = new ProcessSampler(pid);
        Assert.False(sampler.TrySample(collectGpu: false, out _));
    }

    // ── 8. 恢复路径：锁文件 + 判活探针 → 恢复出运行快照 ──

    [Fact]
    public void 冷启动能从锁文件恢复出正在运行的游戏()
    {
        using var dotMc = new TempDotMc();
        using var data = new TempDataDir();
        WriteLockFile(dotMc, Environment.ProcessId, "1.21.1-fabric", DateTime.Now.AddMinutes(-5));
        GameLaunchContextStore.Write(dotMc.Path, Environment.ProcessId,
            Context("1.21.1-fabric", maxMem: 8192));

        var mon = NewMonitor(dotMc.Path, data.Path, pid => pid == Environment.ProcessId);
        mon.IntervalMs = 250;
        try
        {
            mon.Start();
            var info = WaitForRunning(mon);

            Assert.NotNull(info);
            Assert.True(info!.RecoveredFromLock);
            Assert.Equal("1.21.1-fabric", info.VersionId);
            Assert.Equal(Environment.ProcessId, info.Pid);
            Assert.NotNull(mon.LaunchContext);            // 上下文也一并恢复
            Assert.Equal(8192, mon.LaunchContext!.MaxMemoryMb);
        }
        finally
        {
            mon.Stop();
            mon.Dispose();
        }
    }

    // ── 9. 恢复路径：探针判死 → 不恢复，也不动别人的锁文件 ──

    [Fact]
    public void 锁文件指向死进程时不恢复也不改写锁文件()
    {
        using var dotMc = new TempDotMc();
        using var data = new TempDataDir();
        WriteLockFile(dotMc, 999_999, "1.21.1", DateTime.Now.AddMinutes(-1));
        var lockFile = LauncherPaths.GameSessionFile(dotMc.Path, 999_999);
        var before = File.ReadAllText(lockFile);

        var mon = NewMonitor(dotMc.Path, data.Path, _ => false);
        mon.IntervalMs = 250;
        try
        {
            mon.Start();
            WaitForNotRunning(mon, timeoutMs: 1200);

            Assert.Null(mon.Current);
            Assert.Equal(before, File.ReadAllText(lockFile));
            Assert.False(File.Exists(Path.Combine(data.Path, "game.history.json")));  // 没收口就没历史
        }
        finally
        {
            mon.Stop();
            mon.Dispose();
        }
    }

    // ── 10. 接管 → 退出收口落历史（含崩溃判定） ──

    [Fact]
    public void 游戏退出时落一条运行历史()
    {
        using var data = new TempDataDir();
        var store = new GameRunHistoryStore(data.Path);
        var started = DateTime.Now.AddMinutes(-3);

        var mon = NewMonitor("", data.Path, _ => false, history: store);
        try
        {
            mon.AttachForTest(Environment.ProcessId, "1.21.1", started);
            mon.RaiseSessionExitedForTest(0);

            Assert.Null(mon.Current);
            var rec = Assert.Single(store.Records);
            Assert.Equal("1.21.1", rec.VersionId);
            Assert.Equal(0, rec.ExitCode);
            Assert.False(rec.Crashed);
            Assert.InRange(rec.Duration.TotalSeconds, 179d, 186d);

            // 非零退出码 = 崩溃（且不是用户主动结束）
            mon.AttachForTest(Environment.ProcessId, "1.21.2", started);
            mon.RaiseSessionExitedForTest(1);

            Assert.Equal(2, store.Records.Count);
            Assert.True(store.Records[0].Crashed);
            Assert.Equal(1, store.Records[0].ExitCode);
        }
        finally
        {
            mon.Dispose();
        }
    }

    // ── 11. 采样环形缓冲上限 ──

    [Fact]
    public void 采样缓冲只保留最近600个点()
    {
        using var data = new TempDataDir();
        var mon = NewMonitor("", data.Path, _ => false);
        try
        {
            mon.AttachForTest(Environment.ProcessId, "1.21.1", DateTime.Now);
            for (var i = 0; i < 700; i++) mon.PushSampleForTest(Sample(i));

            var points = mon.Samples(1000);
            Assert.Equal(GameRuntimeMonitor.SampleCapacity, points.Count);
            Assert.Equal(100d, points[0].CpuPercent);     // 最早的 100 个已被挤掉
            Assert.Equal(699d, points[^1].CpuPercent);
        }
        finally
        {
            mon.Dispose();
        }
    }

    // ── 12. 启动上下文往返 ──

    [Fact]
    public void 启动上下文写读一致且特殊字符不丢()
    {
        using var dotMc = new TempDotMc();
        var ctx = Context("1.21.1", maxMem: 4096) with
        {
            ExtraJvmArgs = ["-Dfoo=\"a b=c\"", "-Xmx2G", "--add-opens=java.base/java.lang=ALL-UNNAMED"],
        };

        GameLaunchContextStore.Write(dotMc.Path, 4242, ctx);
        var read = GameLaunchContextStore.Read(dotMc.Path, 4242, "1.21.1");

        Assert.NotNull(read);
        Assert.Equal(ctx.VersionId, read!.VersionId);
        Assert.Equal(ctx.JavaPath, read.JavaPath);
        Assert.Equal(ctx.MaxMemoryMb, read.MaxMemoryMb);
        Assert.Equal(ctx.MinMemoryMb, read.MinMemoryMb);
        Assert.Equal(ctx.WindowWidth, read.WindowWidth);
        Assert.Equal(ctx.WindowHeight, read.WindowHeight);
        Assert.Equal(ctx.Fullscreen, read.Fullscreen);
        Assert.Equal(ctx.Isolation, read.Isolation);
        Assert.Equal(ctx.ModCount, read.ModCount);
        Assert.Equal(ctx.ShaderPackCount, read.ShaderPackCount);
        Assert.Equal(ctx.ResourcePackCount, read.ResourcePackCount);
        Assert.Equal(ctx.ExtraJvmArgs, read.ExtraJvmArgs);   // 引号 / = / 空格逐字保留
    }

    // ── 13. 上下文损坏 / 版本不符 → null ──

    [Fact]
    public void 上下文损坏或版本不符时读到null()
    {
        using var dotMc = new TempDotMc();
        var file = GameLaunchContextStore.FileFor(dotMc.Path, 4242);
        Directory.CreateDirectory(Path.GetDirectoryName(file)!);
        File.WriteAllText(file, "{ 坏掉的 json");

        Assert.Null(GameLaunchContextStore.Read(dotMc.Path, 4242, "1.21.1"));

        GameLaunchContextStore.Write(dotMc.Path, 4242, Context("1.21.1", maxMem: 4096));
        Assert.NotNull(GameLaunchContextStore.Read(dotMc.Path, 4242, "1.21.1"));
        Assert.Null(GameLaunchContextStore.Read(dotMc.Path, 4242, "1.20.1"));   // 上一局残留 → 丢弃

        // 上下文按 pid 分文件：别的一局读不到这一局的
        Assert.Null(GameLaunchContextStore.Read(dotMc.Path, 4243, "1.21.1"));
    }

    // ── 14. 完整命令行含敏感令牌时绝不落盘 ──

    [Fact]
    public void 完整命令行含敏感令牌时绝不落盘()
    {
        using var dotMc = new TempDotMc();
        using var data = new TempDataDir();
        const string secret = "SECRET123";

        var mon = NewMonitor(dotMc.Path, data.Path, _ => false);
        try
        {
            mon.AttachForTest(Environment.ProcessId, "1.21.1", DateTime.Now.AddMinutes(-1),
                session: null, context: Context("1.21.1", maxMem: 2048),
                commandLine: $"--username Steve --accessToken {secret} --uuid abc");

            Assert.Contains(secret, mon.CommandLine!);   // 只在内存里

            mon.RaiseSessionExitedForTest(0);            // 落一条历史后再全盘扫描

            var files = FilesUnder(dotMc.Path).Concat(FilesUnder(data.Path)).ToList();
            Assert.NotEmpty(files);                      // 确认真的扫到了落盘产物
            foreach (var f in files)
                Assert.DoesNotContain(secret, File.ReadAllText(f));
        }
        finally
        {
            mon.Dispose();
        }
    }

    // ── 15. 多开：注册表按 pid 分别记账，同版本多局互不影响 ──

    [Fact]
    public void 同一版本可并存多局且注册表按实例记账()
    {
        using var dotMc = new TempDotMc();
        var t0 = DateTime.Now.AddMinutes(-6);

        // 探针注入，不依赖机器上真的跑着 java
        static bool Alive(int pid) => pid is 101 or 202;

        WriteLockFile(dotMc, 101, "1.21.1", t0);
        WriteLockFile(dotMc, 202, "1.21.1", t0.AddMinutes(1));

        var all = GameSessionGuard.All(dotMc.Path, Alive);
        Assert.Equal(new[] { 101, 202 }, all.Select(l => l.Pid));   // 同版本两局并存，按启动时刻升序
        Assert.All(all, l => Assert.Equal("1.21.1", l.VersionId));

        // 登记 / 注销是「一实例一文件」，一个实例收口不触碰另一个
        GameSessionGuard.Unpublish(dotMc.Path, 101);
        Assert.False(File.Exists(LauncherPaths.GameSessionFile(dotMc.Path, 101)));
        Assert.True(File.Exists(LauncherPaths.GameSessionFile(dotMc.Path, 202)));
        var rest = Assert.Single(GameSessionGuard.All(dotMc.Path, Alive));
        Assert.Equal(202, rest.Pid);

        // 全部收口 → 注册表清空，版本也不再是「在跑」
        GameSessionGuard.Unpublish(dotMc.Path, 202);
        Assert.Empty(GameSessionGuard.All(dotMc.Path, Alive));
        Assert.False(GameSessionGuard.IsAnyGameAlive(dotMc.Path));
        Assert.False(GameSessionGuard.IsVersionAlive(dotMc.Path, "1.21.1"));
    }

    // ── 16. 多开：监控器可切换选中，收口只针对对应实例 ──

    [Fact]
    public void 多开时监控器能切换选中且只收口对应实例()
    {
        using var data = new TempDataDir();
        var store = new GameRunHistoryStore(data.Path);
        var t0 = DateTime.Now.AddMinutes(-10);

        var mon = NewMonitor("", data.Path, _ => true, history: store);
        try
        {
            mon.AttachForTest(101, "A", t0);
            mon.AttachForTest(202, "B", t0.AddMinutes(1));

            var instances = mon.Instances;
            Assert.Equal(new[] { 101, 202 }, instances.Select(i => i.Pid));
            Assert.Equal(202, mon.SelectedPid);            // 后挂上的成为选中
            Assert.Equal("B", mon.Current!.VersionId);

            Assert.True(mon.Select(101));                  // 下拉切换
            Assert.Equal(101, mon.SelectedPid);
            Assert.Equal("A", mon.Current!.VersionId);
            Assert.False(mon.Select(999));                 // 不存在的 pid 不切

            mon.RaiseSessionExitedForTest(0, 101);         // 只结束切换后的这一局
            Assert.Equal("B", mon.Current!.VersionId);     // 选中自动落到还在跑的那局
            var rec = Assert.Single(store.Records);
            Assert.Equal("A", rec.VersionId);
            Assert.Single(mon.Instances);

            mon.RaiseSessionExitedForTest(7, 202);         // 剩下的那局以非零退出码收口
            Assert.Null(mon.Current);
            Assert.Empty(mon.Instances);
            Assert.Equal(2, store.Records.Count);
            Assert.Equal("B", store.Records[0].VersionId);
            Assert.True(store.Records[0].Crashed);
        }
        finally
        {
            mon.Dispose();
        }
    }

    // ── 测试基础设施 ──

    private static GameRuntimeMonitor NewMonitor(string dotMc, string dataDir, Func<int, bool> probe,
        GameRunHistoryStore? history = null)
    {
        var mon = new GameRuntimeMonitor { CollectGpu = false, IntervalMs = 250 };
        mon.Configure(() => dotMc, history ?? new GameRunHistoryStore(dataDir), probe);
        return mon;
    }

    /// <summary>轮询等待出「运行中」快照（心跳周期 250ms，给 3s 余量）</summary>
    private static GameRunInfo? WaitForRunning(GameRuntimeMonitor mon, int timeoutMs = 3000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (mon.Current is { } info) return info;
            Thread.Sleep(25);
        }
        return mon.Current;
    }

    /// <summary>等足观察窗口；一旦出现「运行中」立即返回（让断言失败得更快）</summary>
    private static void WaitForNotRunning(GameRuntimeMonitor mon, int timeoutMs)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (mon.Current is not null) return;
            Thread.Sleep(25);
        }
    }

    private static void WriteLockFile(TempDotMc dotMc, int pid, string versionId, DateTime startedAt) =>
        GameSessionGuard.WriteLock(LauncherPaths.GameSessionFile(dotMc.Path, pid), new GameSessionLock
        {
            Pid = pid,
            VersionId = versionId,
            StartedAt = startedAt,
        });

    private static GameRunRecord Record(string versionId, int index) =>
        new(versionId, DateTime.Now.AddMinutes(-index - 1), DateTime.Now.AddMinutes(-index),
            index + 1, 0, 1024 * 1024, false);

    private static GameRunSample Sample(double cpu) =>
        new(DateTime.Now, cpu, 256L * 1024 * 1024, 256L * 1024 * 1024, 8, null);

    private static GameLaunchContext Context(string versionId, int maxMem) =>
        new(versionId, @"C:\java\bin\javaw.exe", maxMem, 512, 1280, 720, false,
            ["-XX:+UseG1GC"], "isolated", 12, 2, 5, DateTime.Now);

    private static IEnumerable<string> FilesUnder(string dir) =>
        Directory.Exists(dir) ? Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories) : [];

    private class TempDir : IDisposable
    {
        protected TempDir(string prefix)
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(),
                prefix + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try { Directory.Delete(Path, recursive: true); } catch { }
        }
    }

    private sealed class TempDataDir : TempDir
    {
        public TempDataDir() : base("sc-rt-data-") { }
    }

    private sealed class TempDotMc : TempDir
    {
        public TempDotMc() : base("sc-rt-mc-") { }
    }
}
