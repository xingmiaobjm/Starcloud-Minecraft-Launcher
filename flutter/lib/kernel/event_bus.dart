/// 事件总线（规范 §6）。
///
/// - 订阅返回可释放句柄；上下文提供**代管**订阅（停用时自动断开，见 plugin_context）。
/// - 分发默认同步；**单个订阅者抛异常不影响其他订阅者**——异常被捕获后交给
///   `onError`（宿主日志），分发继续。
/// - 事件是"事实广播"，载荷必须是不可变值；事件类型属于发布方。
/// - 事件不得用来替代请求-响应（那是契约的职责）。
library;

/// 一次订阅的句柄。dispose 幂等。
class Subscription {
  Subscription(void Function() onDispose) : _onDispose = onDispose;

  final void Function() _onDispose;
  bool _disposed = false;
  bool get isDisposed => _disposed;

  void dispose() {
    if (_disposed) return;
    _disposed = true;
    _onDispose();
  }
}

class _Handler {
  _Handler(this.type, this.test, this.handler);
  final Type type;
  final bool Function(Object event) test;
  final void Function(Object event) handler;
}

class EventBus {
  EventBus({this.onError});

  final void Function(String message)? onError;
  final List<_Handler> _handlers = [];

  /// 订阅某类型的事件。返回的句柄可退订（幂等）。
  Subscription subscribe<T>(void Function(T event) handler) {
    final h = _Handler(
      T,
      (Object event) => event is T,
      (Object event) => handler(event as T),
    );
    _handlers.add(h);
    final sub = Subscription(() => _handlers.remove(h));
    return sub;
  }

  /// 同步广播。事件按类型匹配（`event is T`）。
  void emit(Object event) {
    // 先取快照再分发：订阅者在回调里退订/新订阅不影响本轮（可预期语义）。
    final snapshot = List<_Handler>.of(_handlers);
    for (final h in snapshot) {
      if (!_handlers.contains(h)) continue; // 本轮开始前已被退订
      if (!h.test(event)) continue;
      try {
        h.handler(event);
      } catch (e, st) {
        // 单个订阅者抛异常 MUST NOT 影响其他订阅者（规范 §6）。
        onError?.call(
          '事件 ${event.runtimeType} 的一个订阅者处理失败：$e\n$st',
        );
      }
    }
  }

  /// 某类型当前的订阅数（诊断 / "回到基线"断言用）。
  int subscriberCount<T>() => _handlers.where((h) => h.type == T).length;

  /// 全部订阅数。
  int get length => _handlers.length;
}
