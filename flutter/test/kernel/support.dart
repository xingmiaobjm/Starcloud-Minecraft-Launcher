/// 内核测试支撑：临时目录内核、可配置探针插件、探针契约。
library;

import 'dart:io';

import 'package:starcloud_launcher/kernel/contribution_registry.dart';
import 'package:starcloud_launcher/kernel/descriptor.dart';
import 'package:starcloud_launcher/kernel/paths.dart';
import 'package:starcloud_launcher/kernel/plugin_context.dart';
import 'package:starcloud_launcher/kernel/plugin_host.dart';
import 'package:starcloud_launcher/kernel/scheduler.dart';

/// 探针契约 A/B（Dart 里契约就是抽象类型）。
abstract class IProbeA {
  String get value;
}

abstract class IProbeB {
  String get value;
}

class ProbeA implements IProbeA {
  ProbeA(this.value);
  @override
  final String value;
}

class ProbeB implements IProbeB {
  ProbeB(this.value);
  @override
  final String value;
}

/// 可配置探针插件：记录生命周期调用、可选提供契约/挂贡献/订阅事件。
class ProbePlugin implements Plugin {
  ProbePlugin({
    this.onInitialize,
    this.onShutdown,
    this.provides = const [],
    this.contributeNav = false,
    this.subscribeEvents = false,
  });

  final void Function(PluginContext ctx)? onInitialize;
  final void Function()? onShutdown;
  final List<Type> provides;
  final bool contributeNav;
  final bool subscribeEvents;

  int initializeCount = 0;
  int shutdownCount = 0;

  @override
  Future<void> initialize(PluginContext ctx) async {
    initializeCount++;
    for (final contract in provides) {
      if (contract == IProbeA) {
        ctx.services.add<IProbeA>(ProbeA(ctx.descriptor.id));
      } else if (contract == IProbeB) {
        ctx.services.add<IProbeB>(ProbeB(ctx.descriptor.id));
      }
    }
    if (contributeNav) {
      ctx.contributions.upsert(Contribution(
        kind: ContributionKind.navItem,
        pluginId: ctx.descriptor.id,
        id: 'nav-${ctx.descriptor.id}',
        title: ctx.descriptor.name,
      ));
    }
    if (subscribeEvents) {
      ctx.subscribe<Object>((_) {});
    }
    onInitialize?.call(ctx);
  }

  @override
  Future<void> shutdown() async {
    shutdownCount++;
    onShutdown?.call();
  }
}

/// 建一个用临时目录做三根的内核（测试之间互不干扰）。
Kernel makeKernel({Directory? root}) {
  final dir = root ?? Directory.systemTemp.createTempSync('starcloud_kernel');
  return Kernel(
    paths: KernelPaths.linux(env: {'HOME': dir.path}),
    scheduler: EventLoopScheduler(),
  );
}

/// 描述符便捷构造（默认合法字段，测试只关心差异项）。
PluginDescriptor descriptor(
  String id, {
  String version = '1.0.0',
  String apiVersion = '3.0',
  PluginKind kind = PluginKind.feature,
  PluginActivation activation = PluginActivation.eager,
  List<Type> provides = const [],
  List<Type> requires = const [],
  Map<String, String> depends = const {},
  Map<String, String> conflicts = const {},
  bool essential = false,
}) {
  return PluginDescriptor.create(
    id: id,
    name: id,
    version: version,
    apiVersion: apiVersion,
    kind: kind,
    activation: activation,
    provides: provides,
    requires: requires,
    depends: depends,
    conflicts: conflicts,
    essential: essential,
  );
}
