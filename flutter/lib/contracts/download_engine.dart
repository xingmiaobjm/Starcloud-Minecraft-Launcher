/// 下载引擎契约（规范 §5：按能力切，存在第二种实现的可能）。
///
/// 实现方：`starcloud.downloader` 插件（并发、镜像改写、SHA1 校验、进度事件）。
/// 消费方：安装器、资源下载等一切需要网络的插件。
library;

/// 单个下载任务的事件（事实广播；不可变）。
class DownloadEvent {
  const DownloadEvent({
    required this.url,
    required this.received,
    required this.total,
    required this.done,
    this.error,
  });

  final String url;
  final int received;
  final int total; // -1 = 未知长度
  final bool done;
  final String? error;
}

abstract class DownloadEngine {
  /// 取文本（UTF-8）。非 2xx 抛 [DownloadException]。
  Future<String> fetchText(String url);

  /// 取文本并 JSON 解码。
  Future<Object?> fetchJson(String url);

  /// 下载到文件（先写临时文件、校验后 rename）。`sha1Expected` 非空时校验。
  Future<void> downloadFile(String url, String savePath, {String? sha1Expected});

  /// 下载事件流（进度/完成/失败）。历史不回放，订阅后的任务才可见。
  Stream<DownloadEvent> get events;
}

class DownloadException implements Exception {
  DownloadException(this.message);
  final String message;

  @override
  String toString() => 'DownloadException: $message';
}

/// 便捷判断：`is DownloadFailure` 语义等同，命名留给实现自由。
typedef DownloadFailure = DownloadException;
