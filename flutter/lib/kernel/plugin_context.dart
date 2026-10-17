/// 插件接口与上下文（规范 §7）。
///
/// 插件能拿到的一切都从 [PluginContext] 来，**不许用全局单例**（否则卸载残留、
/// 多实例冲突）。上下文是一次性的：停用后不得再用；重新启用拿到新实例。
library;

import 'dart:async';

import 'contribution_registry.dart';
import 'descriptor.dart';
import 'event_bus.dart';
import 'paths.dart';
import 'scheduler.dart';
import 'service_registry.dart';

/// 插件实现的生命周期（规范 §4）。构造必须纯内存（铁律 2：eager 不阻塞启动）。
abstract class Plugin {
  /// 激活：注册契约实现、挂贡献点、订阅事件——只做**登记**，
  /// I/O 一律丢给 [PluginContext.scheduler]（铁律 2）。
  FutureOr<void> initialize(PluginContext ctx);

  /// 停用：释放自己的资源。可抛异常（内核吞掉并记日志）；
  /// 贡献 / 契约 / 代管订阅由内核统一收尾，插件不用重复做（规范 §4.2 第 3 步）。
  FutureOr<void> shutdown();
}

/// 内核对上下文暴露的门面（解析可能触发懒激活，规范 §4.4，所以 get 返回 Future）。
/// 由 plugin_host.dart 的 Kernel 实现；分开定义避免循环 import。
abstract class KernelGate {
  /// 取优先级最高的实现；没有提供者时**自动激活** `provides` 该契约的插件；
  /// 仍取不到 → 抛 [MissingContractException]（带可读原因）。
  Future<T> get<T>();

  /// 不触发懒激活的查询（规范 §5.2：解析不得触发懒激活——只有 Get 壳可以）。
  T? tryGet<T>();

  /// 全部实现，优先级降序。
  List<T> getAll<T>();

  /// 当前最高优先级实现的提供者插件 id（诊断用；没有则 null）。
  String? providerOf<T>();
}

/// 日志（带插件 id 前缀；走宿主日志系统）。
typedef PluginLog = void Function(String message, {String level});

/// 本插件被授予的权限（只读；规范 §9：内置插件也必须登记用于审计）。
class PluginPermissions {
  const PluginPermissions(this.granted, this.optional);

  final List<String> granted;
  final List<String> optional;

  bool has(String permission) => granted.contains(permission);
}

/// 本插件的专属路径（规范 §10：按 id 隔离，互不可见）。
class PluginPaths {
  const PluginPaths({required this.configFile, required this.dataDir, required this.cacheDir});

  final String configFile;
  final String dataDir;
  final String cacheDir;
}

/// 契约登记的写入窗口（规范 §5.2：仅激活期可写）。
/// 内核在 initialize 返回后调 [close]，此后再 add 直接抛错（不允许绕过声明期）。
///
/// 构造是**内核专用**（插件从 `ctx.services` 拿到的是同一个对象的受限视图）。
class PluginServicesScope {
  PluginServicesScope(this._registry, this._pluginId);

  final ServiceRegistry _registry;
  final String _pluginId;
  bool _open = true;

  void add<T extends Object>(T implementation, {int priority = 0}) {
    if (!_open) {
      throw StateError('插件 $_pluginId 已完成激活，不能再登记契约'
          '（规范 §5.2：仅激活期可写；运行期需要就声明新的提供者插件）');
    }
    _registry.add<T>(implementation, priority: priority, providerId: _pluginId);
  }

  /// 内核专用：关闭写入窗口。
  void close() => _open = false;
}

/// 贡献点入口：pluginId 由内核绑定，插件改不了（防冒名，规范 §8）。
///
/// 构造是**内核专用**。
class ContributionScope {
  ContributionScope(this._registry, this._pluginId);

  final ContributionRegistry _registry;
  final String _pluginId;

  void upsert(Contribution contribution) {
    if (contribution.pluginId != _pluginId) {
      throw StateError(
        '贡献的 pluginId（${contribution.pluginId}）必须等于本插件 id（$_pluginId）',
      );
    }
    _registry.upsert(contribution);
  }

  void withdraw({required ContributionKind kind, required String id}) =>
      _registry.withdraw(kind: kind, pluginId: _pluginId, id: id);
}

/// 上下文（规范 §7 全成员）。由内核在激活时构造，停用时整体作废。
///
/// 构造是**内核专用**（见 plugin_host.dart 的 `_buildContext`）。
class PluginContext implements KernelGate {
  PluginContext({
    required this.descriptor,
    required this.kernel,
    required this.services,
    required this.events,
    required this.contributions,
    required this.settings,
    required this.log,
    required this.paths,
    required this.permissions,
    required this.scheduler,
  }) : _managedSubscriptions = [];

  final PluginDescriptor descriptor;

  /// 内核门面（规范 §7 的 `kernel` 成员）。
  final KernelGate kernel;

  /// 契约登记入口（规范 §7 的 `services` 成员；写入窗口：激活期后关闭）。
  final PluginServicesScope services;
  final EventBus events;

  /// 贡献点入口（pluginId 已由内核绑定，见 [ContributionScope]）。
  final ContributionScope contributions;
  final PluginConfigSection settings;
  final PluginLog log;
  final PluginPaths paths;
  final PluginPermissions permissions;
  final Scheduler scheduler;

  final List<Subscription> _managedSubscriptions;

  /// 内核专用：激活完成、关闭契约写入窗口（规范 §5.2）。
  void closeServiceWindow() => services.close();

  /// 代管订阅（规范 §14 坑 6）：内核在停用时统一 dispose，插件忘记也不泄漏。
  Subscription subscribe<T>(void Function(T event) handler) {
    final sub = events.subscribe<T>(handler);
    _managedSubscriptions.add(sub);
    return sub;
  }

  /// 内核专用：断开全部代管订阅（§4.2 第 3 步）。
  void disposeManagedSubscriptions() {
    for (final s in _managedSubscriptions) {
      s.dispose();
    }
    _managedSubscriptions.clear();
  }

  // ── KernelFacade ──────────────────────────────────────────────────────────
  @override
  Future<T> get<T>() => kernel.get<T>();

  @override
  T? tryGet<T>() => kernel.tryGet<T>();

  @override
  List<T> getAll<T>() => kernel.getAll<T>();

  @override
  String? providerOf<T>() => kernel.providerOf<T>();
}
