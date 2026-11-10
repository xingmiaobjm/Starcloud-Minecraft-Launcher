using System.Text.Json;

namespace Starcloud.Bridge;

/// <summary>桥接方法的调用语义，决定主循环怎么执行它。</summary>
internal enum BridgeMethodKind
{
    /// <summary>主循环内顺序执行。只允许毫秒级的快方法（读设置、查内存态）。</summary>
    Request,

    /// <summary>丢到线程池并发执行。用于会阻塞的方法（网络、磁盘扫描、进程操作），
    /// 避免下载/扫 Java 期间 ping、game.kill 全部排队。</summary>
    Concurrent,

    /// <summary>单飞长任务：先回执 <c>accepted</c>，进度与终态走事件流。
    /// 同一方法同时只允许一个在跑，重复调用回 <c>busyError</c>。</summary>
    Streaming,
}

/// <summary>
/// 一条桥接方法登记项。名字、语义、实现三件事绑在一起，
/// 新增方法时只在一个地方写一行，不需要同时维护「路由表」和「长任务名单」两份清单。
/// </summary>
internal sealed class BridgeMethod
{
    private BridgeMethod(
        string name,
        BridgeMethodKind kind,
        Func<TextWriter, JsonElement, Task<object?>>? handler,
        Func<TextWriter, int, JsonElement, Task>? starter,
        string busyError)
    {
        Name = name;
        Kind = kind;
        Handler = handler;
        Starter = starter;
        BusyError = busyError;
    }

    /// <summary>协议里的方法名（<c>domain.action</c>，小写驼峰）。</summary>
    public string Name { get; }

    public BridgeMethodKind Kind { get; }

    /// <summary>Request / Concurrent 的执行体。</summary>
    public Func<TextWriter, JsonElement, Task<object?>>? Handler { get; }

    /// <summary>Streaming 的执行体：自己负责事件流与终态响应。</summary>
    public Func<TextWriter, int, JsonElement, Task>? Starter { get; }

    /// <summary>Streaming 撞上「已有任务在跑」时回给调用方的错误文案。</summary>
    public string BusyError { get; }

    /// <summary>Streaming 单飞状态：当前在跑的那次任务。</summary>
    internal Task? Running { get; set; }

    /// <summary>保护 <see cref="Running"/> 的锁（每个方法一把，互不干扰）。</summary>
    internal object Gate { get; } = new();

    internal static BridgeMethod ForRequest(
        string name,
        Func<TextWriter, JsonElement, Task<object?>> handler) =>
        new(name, BridgeMethodKind.Request, handler, null, string.Empty);

    internal static BridgeMethod ForConcurrent(
        string name,
        Func<TextWriter, JsonElement, Task<object?>> handler) =>
        new(name, BridgeMethodKind.Concurrent, handler, null, string.Empty);

    internal static BridgeMethod ForStreaming(
        string name,
        string busyError,
        Func<TextWriter, int, JsonElement, Task> starter) =>
        new(name, BridgeMethodKind.Streaming, null, starter, busyError);
}

/// <summary>
/// 桥接方法表：名字 → 语义 + 实现。
///
/// 设计要点：
/// ① 注册期就查重（重名直接抛，启动即失败，不留给运行时静默覆盖）；
/// ② 保留登记顺序，<c>bridge.methods</c> 按域顺序输出，方便壳端与文档对齐；
/// ③ 语义跟着方法走，主循环不再持有任何方法名清单。
/// </summary>
internal sealed class BridgeMethodTable
{
    private readonly Dictionary<string, BridgeMethod> _byName = new(StringComparer.Ordinal);
    private readonly List<BridgeMethod> _ordered = new();

    public IReadOnlyList<BridgeMethod> All => _ordered;

    public int Count => _ordered.Count;

    public BridgeMethodTable Request(
        string name,
        Func<TextWriter, JsonElement, Task<object?>> handler) =>
        Add(BridgeMethod.ForRequest(name, handler));

    public BridgeMethodTable Concurrent(
        string name,
        Func<TextWriter, JsonElement, Task<object?>> handler) =>
        Add(BridgeMethod.ForConcurrent(name, handler));

    public BridgeMethodTable Streaming(
        string name,
        string busyError,
        Func<TextWriter, int, JsonElement, Task> starter) =>
        Add(BridgeMethod.ForStreaming(name, busyError, starter));

    public bool TryGet(string name, out BridgeMethod method) =>
        _byName.TryGetValue(name, out method!);

    /// <summary>某个单飞方法当前是否在跑（跨域查询用，例如删版本前查安装任务）。</summary>
    public bool IsRunning(string name) =>
        _byName.TryGetValue(name, out var method) && method.Running is { IsCompleted: false };

    private BridgeMethodTable Add(BridgeMethod method)
    {
        if (!_byName.TryAdd(method.Name, method))
            throw new InvalidOperationException($"桥接方法重复登记：{method.Name}");
        _ordered.Add(method);
        return this;
    }
}
