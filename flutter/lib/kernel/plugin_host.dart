/// 内核（规范 §4/§5.6/§6/§8 的宿主侧实现）。
///
/// 登记 → 激活（依赖拓扑 + 环保护 + 懒激活 + 失败隔离）→ 停用（反向级联，幂等）。
/// **零业务词**：这里只有"插件 / 契约 / 贡献"，没有任何下载、版本、账户。
library;

import 'dart:async';

import 'contribution_registry.dart';
import 'descriptor.dart';
import 'event_bus.dart';
import 'paths.dart';
import 'plugin_context.dart';
import 'scheduler.dart';
import 'service_registry.dart';

/// 内核声明的 API 版本（规范 §5.5：同主版本内允许新增，跨主版本拒绝）。
const String kernelApiVersion = '3.0';

/// 内核错误（不冒泡到宿主启动流程之外；规范 §4.5）。
class KernelException implements Exception {
  KernelException(this.message);
  final String message;

  @override
  String toString() => 'KernelException: $message';
}

/// 单个插件的可观测快照（规范 §4.1：状态变化必须可观测）。
class PluginSnapshot {
  const PluginSnapshot({
    required this.id,
    required this.name,
    required this.kind,
    required this.state,
    required this.essential,
    required this.activationMs,
    required this.contributionCount,
    required this.contractCount,
    this.error,
  });

  final String id;
  final String name;
  final PluginKind kind;
  final PluginState state;
  final bool essential;
  final int activationMs;
  final int contributionCount;
  final int contractCount;
  final String? error;
}

typedef PluginFactory = Plugin Function();

/// 内核事件（事实广播；载荷不可变，规范 §6）。
class PluginActivated {
  const PluginActivated(this.pluginId, this.activationMs);
  final String pluginId;
  final int activationMs;
}

class PluginDeactivated {
  const PluginDeactivated(this.pluginId);
  final String pluginId;
}

class PluginFailed {
  const PluginFailed(this.pluginId, this.error);
  final String pluginId;
  final String error;
}

class _Record {
  _Record(this.descriptor, this.factory, this.registrationOrder);

  final PluginDescriptor descriptor;
  final PluginFactory factory;
  final int registrationOrder;

  PluginState state = PluginState.registered;
  Plugin? instance;
  PluginContext? context;
  int activationMs = 0;
  String? error;

  bool get isActive => state == PluginState.active;
}

/// 内核。装配见 bootstrap（宿主注入三根路径 + 调度器 + 日志出口）。
class Kernel implements KernelGate {
  Kernel({
    required this.paths,
    required this.scheduler,
    this.logSink,
  }) {
    _log('info', '内核初始化（apiVersion $kernelApiVersion）');
  }

  final KernelPaths paths;
  final Scheduler scheduler;
  final void Function(String level, String message)? logSink;

  final Map<String, _Record> _records = {};
  int _registrationSerial = 0;

  final ServiceRegistry services = ServiceRegistry();
  late final EventBus events = EventBus(onError: (m) => _log('warn', m));
  final ContributionRegistry contributions = ContributionRegistry();

  // ── 登记（规范 §3/§5.5）───────────────────────────────────────────────────

  /// 登记内置插件。重复 id → 拒绝并报错（不覆盖）；apiVersion 主版本不符 → 拒绝。
  void registerPlugin(PluginDescriptor descriptor, PluginFactory factory) {
    if (_records.containsKey(descriptor.id)) {
      throw KernelException('插件 id 重复：${descriptor.id}（拒绝第二个登记，不覆盖）');
    }
    if (majorOf(descriptor.apiVersion) != majorOf(kernelApiVersion)) {
      throw KernelException(
        'apiVersion 主版本不符：插件 ${descriptor.id} 声明 '
        '${descriptor.apiVersion}，内核是 $kernelApiVersion（跨主版本拒绝加载）',
      );
    }
    _records[descriptor.id] =
        _Record(descriptor, factory, _registrationSerial++);
    _log('info', '已登记插件 ${descriptor.id} v${descriptor.version} '
        '(${descriptor.kind.name}, ${descriptor.activation.name})');
  }

  // ── 启动（规范 §4.3：按 kind 升序，同种按登记顺序；只激活 eager）─────────
  Future<void> start() async {
    final eager = _records.values
        .where((r) => r.descriptor.activation == PluginActivation.eager)
        .toList()
      ..sort((a, b) {
        final byKind = a.descriptor.kind.index.compareTo(b.descriptor.kind.index);
        return byKind != 0
            ? byKind
            : a.registrationOrder.compareTo(b.registrationOrder);
      });
    for (final r in eager) {
      // 失败隔离：单插件失败不阻止其它插件，也不阻止宿主启动（§4.5）。
      await activate(r.descriptor.id);
    }
  }

  // ── 激活（规范 §4.2 + §5.6.2 合并流程）───────────────────────────────────

  /// 激活。返回是否 Active；失败原因记进记录（诊断页可读），不抛出宿主。
  Future<bool> activate(String id) async {
    final record = _records[id];
    if (record == null) {
      _log('error', '激活失败：插件 $id 不存在');
      return false;
    }
    // 1) 状态检查。Activating = 环保护命中（§14 坑 1：递归前必须先置位）。
    switch (record.state) {
      case PluginState.active:
        return true;
      case PluginState.activating:
        record.error = '循环依赖：$id 正在激活中又被请求激活';
        return false;
      case PluginState.disabled:
        record.error = '已被用户禁用';
        return false;
      case PluginState.failed:
        record.error = record.error ?? '此前激活失败（本进程内不自动重试，需显式重试）';
        return false;
      case PluginState.registered:
      case PluginState.stopped:
      case PluginState.stopping:
        break;
    }

    // 2) **先置 Activating**，再递归依赖（环保护的正确顺序）。
    record.state = PluginState.activating;
    record.error = null;
    final watch = Stopwatch()..start();

    Future<bool> fail(String reason) async {
      record.state = PluginState.failed;
      record.error = reason;
      _log('error', '插件 $id 激活失败：$reason');
      events.emit(PluginFailed(id, reason));
      await _cleanupPartial(id);
      return false;
    }

    try {
      // 3) depends：插件 id → 必须在场且版本满足（不满足不降级，§5.6.2 第 2 步）。
      for (final entry in record.descriptor.depends.entries) {
        final dep = _records[entry.key];
        if (dep == null) {
          return await fail('缺少依赖插件 ${entry.key}');
        }
        if (!entry.value.satisfies(dep.descriptor.version)) {
          return await fail('依赖插件 ${entry.key} 版本不满足：'
              '需要 ${entry.value.text}，实际 v${dep.descriptor.version}');
        }
        if (dep.state == PluginState.disabled) {
          return await fail('依赖插件 ${entry.key} 已被禁用');
        }
        if (dep.state == PluginState.failed) {
          return await fail('依赖插件 ${entry.key} 激活失败：${dep.error ?? "未知原因"}');
        }
        if (dep.state != PluginState.active) {
          final ok = await activate(entry.key);
          if (!ok) {
            return await fail('依赖插件 ${entry.key} 激活失败：${dep.error ?? "未知原因"}');
          }
        }
      }

      // 4) requires：契约依赖。已有提供者 → 继续；没有 → 按 **登记顺序** 依次
      //    尝试激活 provides 该契约的插件，第一个成功提供者为胜（§5.6.2 第 3 步）。
      for (final contract in record.descriptor.requires) {
        if (services.tryGetByType(contract) != null) continue;
        final providers = _records.values
            .where((r) =>
                r.descriptor.providesContract(contract) &&
                r.descriptor.id != id)
            .toList()
          ..sort((a, b) => a.registrationOrder.compareTo(b.registrationOrder));
        var satisfied = services.tryGetByType(contract) != null;
        for (final provider in providers) {
          if (await activate(provider.descriptor.id) &&
              services.tryGetByType(contract) != null) {
            satisfied = true;
            break;
          }
        }
        if (!satisfied) {
          return await fail('缺少契约提供者 $contract'
              '（候选 ${providers.length} 个均不可用）');
        }
      }

      // 5) conflicts：命中则本插件失败，先激活者不动（§5.6.2 第 4 步）。
      for (final entry in record.descriptor.conflicts.entries) {
        final other = _records[entry.key];
        if (other != null &&
            other.isActive &&
            entry.value.satisfies(other.descriptor.version)) {
          return await fail('与插件 ${entry.key} 冲突（需要 ≠ ${entry.value.text}，'
              '实际 v${other.descriptor.version} 且已激活）');
        }
      }

      // 6) 构造上下文 → 初始化（§4.2 第 4 步起）。
      final context = _buildContext(record);
      final plugin = record.factory();
      record.instance = plugin;
      record.context = context;
      await plugin.initialize(context);
      // 关闭契约写入窗口（规范 §5.2：仅激活期可写）。
      // initialize 可能是同步的 FutureOr ——microtask 后再关，让同步路径也能 add。
      await Future<void>.delayed(Duration.zero);
      context.closeServiceWindow();

      record.state = PluginState.active;
      record.activationMs = watch.elapsedMilliseconds;
      _log('info', '插件 $id 已激活（${record.activationMs}ms，'
          '贡献 ${contributions.countForPlugin(id)}，'
          '契约 ${services.countForProvider(id)}）');
      events.emit(PluginActivated(id, record.activationMs));
      return true;
    } catch (e, st) {
      // 失败隔离：不冒泡（§4.5）。
      record.state = PluginState.failed;
      record.error = '$e';
      _log('error', '插件 $id 初始化抛异常：$e\n$st');
      events.emit(PluginFailed(id, '$e'));
      await _cleanupPartial(id);
      return false;
    } finally {
      watch.stop();
    }
  }

  // ── 停用（规范 §4.2 第 3 步顺序 + §5.6.3 反向级联）────────────────────────

  /// 停用。essential 被拒（含"被必需插件依赖"）；级联停用幂等。
  /// 返回 null = 成功；否则为拒绝/失败原因（诊断可读）。
  Future<String?> deactivate(String id) async {
    final record = _records[id];
    if (record == null) return '插件 $id 不存在';
    if (record.state == PluginState.stopped ||
        record.state == PluginState.registered ||
        record.state == PluginState.failed) {
      return null; // 幂等：重复停用不报错
    }
    if (record.state == PluginState.activating) {
      return '插件 $id 正在激活，不能停用';
    }
    if (record.descriptor.essential) {
      return '插件 $id 是内核必需插件，不允许停用';
    }
    // 被必需插件依赖 → 也拒绝（规范 §5.6.3）。
    for (final other in _records.values) {
      if (!other.descriptor.essential || !other.isActive) continue;
      if (other.descriptor.depends.containsKey(id)) {
        return '插件 $id 被必需插件 ${other.descriptor.id} 依赖，不允许停用';
      }
    }

    // 反向闭包：depends 含 X 的活跃插件 + requires 的提供者只剩 X 的活跃插件。
    // 依赖者先停（逆拓扑），递归天然处理链式 A→B→C；visited 由状态机保证幂等。
    for (final other in _records.values.where((r) => r.isActive).toList()) {
      if (other.descriptor.id == id) continue;
      final dependsOnX = other.descriptor.depends.containsKey(id);
      final soleProvider = other.descriptor.requires.any((contract) {
        // 只统计"活跃"实现：撤下 X 后该契约是否还有提供者。
        final providers = _activeProvidersOf(contract);
        return providers.length == 1 && providers.single == id;
      });
      if (dependsOnX || soleProvider) {
        _log('info', '插件 ${other.descriptor.id} 因 $id 停用而级联停用');
        final reason = await deactivate(other.descriptor.id);
        if (reason != null) {
          return '级联停用 ${other.descriptor.id} 失败：$reason';
        }
      }
    }

    // 本体停用；固定收尾顺序：撤贡献 → 注销契约 → 断开代管订阅 → 清实例。
    record.state = PluginState.stopping;
    try {
      await record.instance?.shutdown();
    } catch (e, st) {
      _log('warn', '插件 $id 的 shutdown 抛异常（内核吞掉继续收尾）：$e\n$st');
    }
    contributions.removeByPlugin(id);
    services.removeByProvider(id);
    record.context?.disposeManagedSubscriptions();
    record.instance = null;
    record.context = null;
    record.state = PluginState.stopped;
    _log('info', '插件 $id 已停用');
    events.emit(PluginDeactivated(id));
    return null;
  }

  // ── 禁用 / 启用（用户侧开关；持久化由宿主设置层做，基座期在内存）──────────

  Future<String?> disable(String id) async {
    final record = _records[id];
    if (record == null) return '插件 $id 不存在';
    if (record.descriptor.essential) return '插件 $id 是内核必需插件，不允许禁用';
    if (record.state == PluginState.active ||
        record.state == PluginState.stopping) {
      final reason = await deactivate(id);
      if (reason != null) return reason;
    }
    record.state = PluginState.disabled;
    return null;
  }

  void enable(String id) {
    final record = _records[id];
    if (record == null) return;
    if (record.state == PluginState.disabled) {
      record.state = PluginState.registered;
      record.error = null;
    }
  }

  /// 显式重试：Failed 在本次进程内不自动重试（规范 §4.1），只有用户点重试才
  /// 重置回 Registered。返回是否重置成功（非 Failed 状态返回 false）。
  bool retry(String id) {
    final record = _records[id];
    if (record == null || record.state != PluginState.failed) return false;
    record.state = PluginState.registered;
    record.error = null;
    _log('info', '插件 $id 由用户显式重试，状态重置为 Registered');
    return true;
  }

  /// 是否有 essential 插件处于 Failed（宿主可据此提示后退出，规范 §4.5）。
  bool get hasEssentialFailure => _records.values.any(
        (r) => r.descriptor.essential && r.state == PluginState.failed,
      );

  // ── KernelFacade（懒激活只发生在这一层壳上，规范 §5.2）────────────────────

  @override
  T? tryGet<T>() => services.tryGet<T>();

  @override
  Future<T> get<T>() async {
    final direct = services.tryGet<T>();
    if (direct != null) return direct;
    // 找提供者（可以还没激活）→ 激活第一个成功的 → 再取一次。
    final providers = _records.values
        .where((r) => r.descriptor.providesContract(T))
        .toList()
      ..sort((a, b) => a.registrationOrder.compareTo(b.registrationOrder));
    for (final provider in providers) {
      if (await activate(provider.descriptor.id)) {
        final value = services.tryGet<T>();
        if (value != null) return value;
      }
    }
    throw MissingContractException(
      T,
      providers.isEmpty
          ? '登记的插件里没有任何提供者'
          : '候选提供者 ${providers.map((p) => p.descriptor.id).join(", ")} '
              '均未成功激活（状态见诊断页）',
    );
  }

  @override
  List<T> getAll<T>() => services.getAll<T>();

  @override
  String? providerOf<T>() => services.providerOf<T>();

  // ── 诊断 ──────────────────────────────────────────────────────────────────

  List<PluginSnapshot> snapshots() {
    final records = _records.values.toList()
      ..sort((a, b) => a.registrationOrder.compareTo(b.registrationOrder));
    return records
        .map((r) => PluginSnapshot(
              id: r.descriptor.id,
              name: r.descriptor.name,
              kind: r.descriptor.kind,
              state: r.state,
              essential: r.descriptor.essential,
              activationMs: r.activationMs,
              contributionCount: contributions.countForPlugin(r.descriptor.id),
              contractCount: services.countForProvider(r.descriptor.id),
              error: r.error,
            ))
        .toList(growable: false);
  }

  // ── 内部 ──────────────────────────────────────────────────────────────────

  List<String> _activeProvidersOf(Type contract) => services
      .snapshot()
      .where((s) => s.contract == contract)
      .map((s) => s.providerId)
      .toSet()
      .toList();

  PluginContext _buildContext(_Record record) {
    final d = record.descriptor;
    final id = d.id;
    return PluginContext(
      descriptor: d,
      kernel: this,
      services: PluginServicesScope(services, id),
      events: events,
      contributions: ContributionScope(contributions, id),
      settings: PluginConfigSection.load(paths.pluginConfigFile(id)),
      log: (String message, {String level = 'info'}) =>
          _log(level, '[$id] $message'),
      paths: PluginPaths(
        configFile: paths.pluginConfigFile(id),
        dataDir: paths.pluginDataDir(id),
        cacheDir: paths.pluginCacheDir(id),
      ),
      permissions: PluginPermissions(d.permissions, d.optionalPermissions),
      scheduler: scheduler,
    );
  }

  /// 激活失败的现场清理（贡献 / 契约 / 代管订阅一并撤，规范 §8.1）。
  Future<void> _cleanupPartial(String id) async {
    contributions.removeByPlugin(id);
    services.removeByProvider(id);
    final context = _records[id]?.context;
    context?.disposeManagedSubscriptions();
    _records[id]?.instance = null;
    _records[id]?.context = null;
  }

  void _log(String level, String message) {
    logSink?.call(level, message);
  }
}
