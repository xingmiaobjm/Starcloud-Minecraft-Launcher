/// 桥接版下载引擎：所有 HTTP / 落盘 / 校验都在 C# 的 DownloadService 里完成。
///
/// 这里只做协议翻译，不重写任何下载逻辑——这正是"Windows 端写一次、
/// 其它端复用"的落点。
///
/// 注意：`events` 目前只在宿主主动广播 `download.progress` 时才有数据；
/// 当前 C# 侧只在安装任务里发 `install.progress`，单文件下载暂无字节级进度。
/// 依赖该流的插件应把"无事件"当作正常情况处理。
library;

import 'dart:async';
import 'dart:convert';

import '../../../contracts/download_engine.dart';
import '../../../bridge/core_bridge.dart';

class BridgeDownloadEngine implements DownloadEngine {
  BridgeDownloadEngine(this._bridge) {
    _subscription = _bridge.events.listen((event) {
      if (event.name != 'download.progress') return;
      final params = event.params;
      if (_events.isClosed) return;
      _events.add(DownloadEvent(
        url: params['url'] as String? ?? '',
        received: (params['received'] as num?)?.toInt() ?? 0,
        total: (params['total'] as num?)?.toInt() ?? -1,
        done: params['done'] == true,
        error: params['error'] as String?,
      ));
    }, onError: (Object e) {
      if (!_events.isClosed) _events.addError(e);
    });
  }

  final CoreBridge _bridge;
  final StreamController<DownloadEvent> _events =
      StreamController<DownloadEvent>.broadcast();
  late final StreamSubscription<BridgeEvent> _subscription;

  @override
  Stream<DownloadEvent> get events => _events.stream;

  @override
  Future<String> fetchText(String url) async {
    final result = await _bridge.request('download.text', {'url': url});
    if (result is! String) {
      throw DownloadException('桥接返回的不是文本：$url');
    }
    return result;
  }

  @override
  Future<Object?> fetchJson(String url) async => jsonDecode(await fetchText(url));

  @override
  Future<void> downloadFile(
    String url,
    String savePath, {
    String? sha1Expected,
  }) async {
    try {
      await _bridge.requestWithTimeout(
        'download.file',
        {
          'url': url,
          'dest': savePath,
          'sha1': ?sha1Expected,
        },
        const Duration(minutes: 30),
      );
    } on BridgeException catch (e) {
      throw DownloadException(e.message);
    }
  }

  Future<void> dispose() async {
    await _subscription.cancel();
    await _events.close();
  }
}
