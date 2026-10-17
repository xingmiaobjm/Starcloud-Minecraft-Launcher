/// 宿主注入的调度器（规范 §7 `scheduler` / §11.3）。
///
/// 插件 **MUST NOT** 自己起线程/定时器/事件循环——一切经此（规范七条禁令 4）。
/// 理由：六端事件循环各不相同；停用时只有宿主知道怎么收敛插件排下的活。
///
/// Dart 对应物：`post` 回到事件循环微任务；`delay` 定时；`spawn_background`
/// 用 `Isolate.run` 把 CPU 活挪出主隔离——结果按值返回，**不跨隔离共享可变状态**
/// （这也是 Dart 版"注册表线程安全"的前提，见 flutter/README.md 已知项）。
library;

import 'dart:async';
import 'dart:isolate';

abstract class Scheduler {
  /// 尽快在宿主主事件循环上执行。
  void post(void Function() task);

  /// 延迟执行。返回的句柄可取消（页面关闭 / 停用插件时用）。
  CancelHandle delay(Duration duration, void Function() task);

  /// 把阻塞/CPU 密集的活放到后台隔离执行，结果按值带回。
  Future<R> spawnBackground<R>(FutureOr<R> Function() task);
}

/// 可取消句柄（dispose 幂等）。
class CancelHandle {
  CancelHandle(this._onCancel);
  final void Function() _onCancel;
  bool _cancelled = false;

  void cancel() {
    if (_cancelled) return;
    _cancelled = true;
    _onCancel();
  }
}

/// 基于 Dart 事件循环 / Isolate 的默认实现。
class EventLoopScheduler implements Scheduler {
  @override
  void post(void Function() task) {
    scheduleMicrotask(() {
      try {
        task();
      } catch (e) {
        // post 的任务是"回到主循环"，异常交给 zone / 宿主日志，不打断循环。
        // 这里不吞：FlutterError / runZonedGuarded 在宿主装配层兜底。
        rethrow;
      }
    });
  }

  @override
  CancelHandle delay(Duration duration, void Function() task) {
    final timer = Timer(duration, () {
      try {
        task();
      } catch (e) {
        rethrow;
      }
    });
    return CancelHandle(timer.cancel);
  }

  @override
  Future<R> spawnBackground<R>(FutureOr<R> Function() task) => Isolate.run(task);
}
