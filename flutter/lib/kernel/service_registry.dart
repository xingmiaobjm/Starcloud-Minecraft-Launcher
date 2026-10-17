/// 契约注册表（规范 §5.2）。
///
/// 语义（MUST 与规范一致）：
/// - `add`：登记一条实现，归属"当前正在激活的插件"；
/// - `get`：取优先级最高者；**同优先级时后注册者胜**（这是"新插件覆盖内置"的机制）；
/// - `getAll`：按优先级降序（同优先级按登记时间倒序）；
/// - 停用插件 → 按归属批量注销；
/// - 快照可枚举全部登记，供插件页与诊断。
///
/// 线程安全：内核运行在 Dart 单事件循环上，注册/解析都是同步段（无 await），
/// 由事件循环串行化保证（规范 §11.3 的 Dart 对应物，见 flutter/README.md）。
library;

/// 一条登记的快照（诊断 / 插件页用）。
class ServiceSnapshot {
  const ServiceSnapshot({
    required this.contract,
    required this.providerId,
    required this.priority,
    required this.registeredAt,
  });

  /// 契约（Dart Type；内置插件编译期登记，见 README 取舍 2）。
  final Type contract;
  final String providerId;
  final int priority;

  /// 登记序号（同优先级"后注册者胜"的裁决依据）。
  final int registeredAt;
}

class _Entry {
  _Entry({
    required this.contract,
    required this.implementation,
    required this.providerId,
    required this.priority,
    required this.registeredAt,
  });

  final Type contract;
  final Object implementation;
  final String providerId;
  final int priority;
  final int registeredAt;
}

/// "没有提供者"错误（规范 §13：停用后 Get 要给出可读错误）。
class MissingContractException implements Exception {
  MissingContractException(this.contract, [this.hint = '']);
  final Type contract;
  final String hint;

  @override
  String toString() =>
      'MissingContractException: 没有契约 ${contract.toString()} 的提供者'
      '${hint.isEmpty ? '' : '（$hint）'}';
}

class ServiceRegistry {
  final List<_Entry> _entries = [];
  int _nextSerial = 0;

  /// 当前登记总数（诊断用）。
  int get length => _entries.length;

  /// 登记一条实现。`providerId` = 当前正在激活的插件 id（由上下文绑定）。
  void add<T extends Object>(T implementation, {int priority = 0, required String providerId}) {
    _entries.add(_Entry(
      contract: T,
      implementation: implementation,
      providerId: providerId,
      priority: priority,
      registeredAt: _nextSerial++,
    ));
  }

  T? tryGet<T>([String? hint]) {
    final best = _bestEntry(T);
    if (best == null) return null;
    return best.implementation as T;
  }

  /// 按**运行期 Type** 查询（内核解析 `requires` 时契约是 Type 值而不是泛型参数）。
  Object? tryGetByType(Type contract) => _bestEntry(contract)?.implementation;

  _Entry? _bestEntry(Type contract) {
    _Entry? best;
    for (final e in _entries) {
      if (e.contract != contract) continue;
      // 优先级降序；同优先级取登记序号更大者（后注册者胜，规范 §14 坑 3）。
      if (best == null ||
          e.priority > best.priority ||
          (e.priority == best.priority && e.registeredAt > best.registeredAt)) {
        best = e;
      }
    }
    return best;
  }

  /// 取优先级最高者；没有提供者时抛 [MissingContractException]。
  T get<T>([String? hint]) {
    final value = tryGet<T>(hint);
    if (value == null) {
      throw MissingContractException(T, hint ?? '');
    }
    return value;
  }

  /// 该契约的全部实现：优先级降序；同优先级按登记时间倒序。
  List<T> getAll<T>() {
    final matched =
        _entries.where((e) => e.contract == T).toList()
          ..sort((a, b) {
            final byPriority = b.priority.compareTo(a.priority);
            return byPriority != 0 ? byPriority : b.registeredAt.compareTo(a.registeredAt);
          });
    return matched.map((e) => e.implementation as T).toList(growable: false);
  }

  /// 某契约当前最高优先级的提供者插件 id（诊断用；没有则 null）。
  String? providerOf<T>() {
    final value = tryGet<T>();
    for (final e in _entries) {
      if (e.contract == T && identical(e.implementation, value)) {
        return e.providerId;
      }
    }
    return null;
  }

  /// 停用插件时按归属批量注销（规范 §5.2）。
  void removeByProvider(String pluginId) {
    _entries.removeWhere((e) => e.providerId == pluginId);
  }

  /// 该插件当前登记的条数（诊断 / "回到基线"断言用）。
  int countForProvider(String pluginId) =>
      _entries.where((e) => e.providerId == pluginId).length;

  /// 全部登记的只读快照。
  List<ServiceSnapshot> snapshot() => _entries
      .map((e) => ServiceSnapshot(
            contract: e.contract,
            providerId: e.providerId,
            priority: e.priority,
            registeredAt: e.registeredAt,
          ))
      .toList(growable: false);
}
