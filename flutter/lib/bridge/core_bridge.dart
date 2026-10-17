/// Starcloud.Bridge（C# 宿主进程）的 Dart 客户端。
///
/// 架构（2026-10-18 决定）：下载 / 安装 / 启动能力**全部在 C# 侧实现**
/// （`dotnet/src/Starcloud.Bridge`，复用 Starcloud.Core），其它端不重写——
/// 本类拉起该进程，经 stdin/stdout 行式 JSON 复用（与仓库 Python 插件
/// 运行时、以及 spec v3 的跨进程约定同一模式）。
///
/// 协议（每行一个 JSON）：
///   请求 {"id":N,"method":"...","params":{...}}
///   响应 {"id":N,"ok":true,"result":...} | {"id":N,"ok":false,"error":"..."}
///   事件 {"event":"...","params":{...}}
library;

import 'dart:async';
import 'dart:convert';
import 'dart:io';

/// 桥不可用（进程启动失败 / 可执行文件缺失）。
class BridgeException implements Exception {
  BridgeException(this.message);
  final String message;

  @override
  String toString() => 'BridgeException: $message';
}

class BridgeEvent {
  const BridgeEvent(this.name, this.params);
  final String name;
  final Map<String, Object?> params;
}

class CoreBridge {
  CoreBridge._({
    required this.dllPath,
    required this.dataDir,
    this.dotnetExecutable = 'dotnet',
  });

  /// 拉起桥接进程并等待 `bridge.ready`。
  ///
  /// `dllPath` 以 `.dll` 结尾时经 `dotnet` 运行；否则当作原生可执行文件
  /// （Windows 的 apphost `.exe`）直接执行。
  static Future<CoreBridge> start({
    required String dllPath,
    required String dataDir,
    String dotnetExecutable = 'dotnet',
    Duration readyTimeout = const Duration(seconds: 30),
  }) async {
    if (!File(dllPath).existsSync()) {
      throw BridgeException('桥接宿主不存在：$dllPath（先发布：'
          'dotnet publish dotnet/src/Starcloud.Bridge -r linux-x64）');
    }
    final bridge = CoreBridge._(
      dllPath: dllPath,
      dataDir: dataDir,
      dotnetExecutable: dotnetExecutable,
    );
    await bridge._spawn(readyTimeout);
    return bridge;
  }

  final String dllPath;
  final String dataDir;
  final String dotnetExecutable;

  Process? _process;
  final Map<int, Completer<Object?>> _pending = {};
  final StreamController<BridgeEvent> _events =
      StreamController<BridgeEvent>.broadcast();
  int _nextId = 0;
  Completer<void>? _ready;

  /// 事件流（install.progress / install.done / install.failed /
  /// game.output / game.exited / download.progress …）。
  Stream<BridgeEvent> get events => _events.stream;

  bool get isRunning => _process != null;

  Future<void> _spawn(Duration readyTimeout) async {
    final isDll = dllPath.toLowerCase().endsWith('.dll');
    // 数据根经环境变量注入 → C# 与 Flutter 共享同一份 accounts/设置/缓存。
    final process = await Process.start(
      isDll ? dotnetExecutable : dllPath,
      isDll ? [dllPath] : const [],
      environment: {'STARCLOUD_DATA_DIR': dataDir},
    );
    _process = process;

    _ready = Completer<void>();

    process.stdout
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen(
          _onLine,
          onError: (Object e) => _fail(e),
          cancelOnError: false,
        );
    process.stderr
        .transform(utf8.decoder)
        .transform(const LineSplitter())
        .listen((line) {
      // .NET 的运行期警告走 stderr；不当作协议数据，保留在调试输出。
      // ignore: avoid_print
      print('[bridge/stderr] $line');
    }, onError: (Object e) => _fail(e));
    process.exitCode.then((code) {
      _fail(BridgeException('桥接进程退出（$code）'));
    });

    await _ready!.future.timeout(readyTimeout);
  }

  void _onLine(String line) {
    Map<String, Object?> decoded;
    try {
      decoded = jsonDecode(line) as Map<String, Object?>;
    } catch (_) {
      return; // 非协议行（如 stdout 上的警告），忽略
    }
    if (decoded.containsKey('event')) {
      final name = decoded['event'] as String? ?? '';
      // 兼容两种字段名：新宿主用 params（与请求一致），旧宿主用 parameters。
      final params =
          (decoded['params'] ?? decoded['parameters']) as Map<String, Object?>? ??
              const {};
      if (name == 'bridge.ready' && !(_ready?.isCompleted ?? true)) {
        _ready!.complete();
      }
      if (!_events.isClosed) _events.add(BridgeEvent(name, params));
      return;
    }
    final id = decoded['id'] as int?;
    if (id == null) return;
    final completer = _pending.remove(id);
    if (completer == null) return; // 同一 id 的补充响应（install 终态）忽略
    if (decoded['ok'] == true) {
      completer.complete(decoded['result']);
    } else {
      completer.completeError(
        BridgeException(decoded['error'] as String? ?? '未知桥接错误'),
      );
    }
  }

  void _fail(Object e) {
    // 进程死亡：所有在途请求失败；后续请求立即失败（重启由上层决定）。
    _process = null;
    for (final completer in _pending.values) {
      if (!completer.isCompleted) completer.completeError(e);
    }
    _pending.clear();
    if (!(_ready?.isCompleted ?? true)) _ready!.completeError(e);
    if (!_events.isClosed) _events.addError(e);
  }

  /// 发送请求并等待响应。默认 120s 超时（长任务用事件驱动）。
  Future<Object?> request(
    String method, [
    Map<String, Object?> params = const {},
  ]) =>
      requestWithTimeout(method, params, const Duration(seconds: 120));

  Future<Object?> requestWithTimeout(
    String method,
    Map<String, Object?> params,
    Duration timeout,
  ) {
    final process = _process;
    if (process == null) {
      return Future.error(BridgeException('桥接进程未运行'));
    }
    final id = ++_nextId;
    final completer = Completer<Object?>();
    _pending[id] = completer;
    try {
      process.stdin
          .writeln(jsonEncode({'id': id, 'method': method, 'params': params}));
    } catch (e) {
      _pending.remove(id);
      return Future.error(BridgeException('写入桥接进程失败：$e'));
    }
    return completer.future.timeout(timeout, onTimeout: () {
      _pending.remove(id);
      throw BridgeException('桥接请求超时：$method（${timeout.inSeconds}s）');
    });
  }

  Future<void> dispose() async {
    final process = _process;
    _process = null;
    if (process != null) {
      try {
        process.stdin.writeln(''); // 空行 = 无操作，促使对端刷新
        await process.stdin.flush();
      } catch (_) {
        // 进程可能已退出
      }
      process.kill();
    }
    if (!_events.isClosed) await _events.close();
  }
}
