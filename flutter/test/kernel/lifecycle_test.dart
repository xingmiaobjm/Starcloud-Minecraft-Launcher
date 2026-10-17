import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/kernel/descriptor.dart';
import 'package:starcloud_launcher/kernel/plugin_host.dart';
import 'package:starcloud_launcher/kernel/service_registry.dart';

import 'support.dart';

void main() {
  test('重复 id 登记 → 拒绝并报错（不覆盖）', () {
    final k = makeKernel();
    k.registerPlugin(descriptor('starcloud.a'), ProbePlugin.new);
    expect(
      () => k.registerPlugin(descriptor('starcloud.a'), ProbePlugin.new),
      throwsA(isA<KernelException>()),
    );
  });

  test('apiVersion 主版本不一致 → 拒绝加载', () {
    final k = makeKernel();
    expect(
      () => k.registerPlugin(
        descriptor('starcloud.a', apiVersion: '2.0'),
        ProbePlugin.new,
      ),
      throwsA(isA<KernelException>()),
    );
  });

  test('激活次序：engine → feature → assistant → ui', () async {
    final k = makeKernel();
    final order = <String>[];
    void reg(String id, PluginKind kind) {
      k.registerPlugin(
        descriptor(id, kind: kind),
        () => ProbePlugin(onInitialize: (_) => order.add(id)),
      );
    }

    reg('starcloud.ui', PluginKind.ui);
    reg('starcloud.feature', PluginKind.feature);
    reg('starcloud.engine', PluginKind.engine);
    reg('starcloud.assistant', PluginKind.assistant);
    await k.start();
    expect(order, [
      'starcloud.engine',
      'starcloud.feature',
      'starcloud.assistant',
      'starcloud.ui',
    ]);
  });

  test('依赖先于被依赖者激活', () async {
    final k = makeKernel();
    final order = <String>[];
    k.registerPlugin(
      descriptor('starcloud.a', depends: {'starcloud.b': '^1.0.0'}),
      () => ProbePlugin(onInitialize: (_) => order.add('a')),
    );
    k.registerPlugin(
      descriptor('starcloud.b'),
      () => ProbePlugin(onInitialize: (_) => order.add('b')),
    );
    await k.start();
    expect(order, ['b', 'a']);
  });

  test('循环依赖 A→B→A：不死循环、双方 Failed、原因可读', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.a', depends: {'starcloud.b': '*'}, essential: true),
      ProbePlugin.new,
    );
    k.registerPlugin(
      descriptor('starcloud.b', depends: {'starcloud.a': '*'}),
      ProbePlugin.new,
    );
    await k.start(); // 不崩 = 已通过
    final snaps = {for (final s in k.snapshots()) s.id: s};
    expect(snaps['starcloud.a']!.state, PluginState.failed);
    expect(snaps['starcloud.b']!.state, PluginState.failed);
    expect(
      '${snaps['starcloud.a']!.error} ${snaps['starcloud.b']!.error}',
      contains('循环依赖'),
    );
  });

  test('依赖缺失 → Failed 且原因可读；宿主启动不受影响', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.a', depends: {'starcloud.missing': '^1.0.0'}),
      ProbePlugin.new,
    );
    k.registerPlugin(descriptor('starcloud.ok'), ProbePlugin.new);
    await k.start();
    final snaps = {for (final s in k.snapshots()) s.id: s};
    expect(snaps['starcloud.a']!.state, PluginState.failed);
    expect(snaps['starcloud.a']!.error, contains('缺少依赖插件'));
    expect(snaps['starcloud.ok']!.state, PluginState.active);
  });

  test('depends 版本不满足 → Failed（不降级、不凑合）', () async {
    final k = makeKernel();
    k.registerPlugin(descriptor('starcloud.dep', version: '1.5.0'), ProbePlugin.new);
    k.registerPlugin(
      descriptor('starcloud.a', depends: {'starcloud.dep': '^2.0.0'}),
      ProbePlugin.new,
    );
    await k.start();
    final a = k.snapshots().firstWhere((s) => s.id == 'starcloud.a');
    expect(a.state, PluginState.failed);
    expect(a.error, contains('版本不满足'));
  });

  test('懒插件：启动后仍是 Registered，首次 Get 才激活', () async {
    final k = makeKernel();
    final plugin = ProbePlugin(provides: [IProbeA]);
    k.registerPlugin(
      descriptor('starcloud.lazy',
          activation: PluginActivation.lazy, provides: [IProbeA]),
      () => plugin,
    );
    await k.start();
    expect(
      k.snapshots().single.state,
      PluginState.registered,
      reason: 'lazy 插件启动时不得激活',
    );
    final a = await k.get<IProbeA>();
    expect(a.value, 'starcloud.lazy');
    expect(k.snapshots().single.state, PluginState.active);
    expect(plugin.initializeCount, 1);
  });

  test('激活抛异常 → Failed + 错误信息，不冒泡', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.boom'),
      () => ProbePlugin(onInitialize: (_) => throw StateError('kaboom')),
    );
    k.registerPlugin(descriptor('starcloud.ok'), ProbePlugin.new);
    await k.start(); // 不抛
    final snaps = {for (final s in k.snapshots()) s.id: s};
    expect(snaps['starcloud.boom']!.state, PluginState.failed);
    expect(snaps['starcloud.boom']!.error, contains('kaboom'));
    expect(snaps['starcloud.ok']!.state, PluginState.active);
  });

  test('停用 → 再启用：状态/贡献/契约回到基线（不翻倍）', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.a', provides: [IProbeA]),
      () => ProbePlugin(provides: [IProbeA], contributeNav: true, subscribeEvents: true),
    );
    await k.start();
    final baselineContrib = k.contributions.length;
    final baselineContracts = k.services.length;
    expect(baselineContrib, 1);
    expect(baselineContracts, 1);

    expect(await k.deactivate('starcloud.a'), isNull);
    expect(k.contributions.length, 0);
    expect(k.services.length, 0);
    expect(k.events.length, 0, reason: '代管订阅应被断开');
    expect(() => k.services.get<IProbeA>(), throwsA(isA<MissingContractException>()));

    expect(await k.activate('starcloud.a'), isTrue);
    expect(k.contributions.length, baselineContrib);
    expect(k.services.length, baselineContracts);
  });

  test('停用是幂等的', () async {
    final k = makeKernel();
    k.registerPlugin(descriptor('starcloud.a'), ProbePlugin.new);
    await k.start();
    expect(await k.deactivate('starcloud.a'), isNull);
    expect(await k.deactivate('starcloud.a'), isNull);
  });

  test('Failed 本进程内不自动重试；显式 retry 才回 Registered', () async {
    final k = makeKernel();
    var fail = true;
    k.registerPlugin(
      descriptor('starcloud.a'),
      () => ProbePlugin(
        onInitialize: (_) {
          if (fail) throw StateError('first');
        },
      ),
    );
    await k.start();
    expect(k.snapshots().single.state, PluginState.failed);
    expect(await k.activate('starcloud.a'), isFalse);
    fail = false;
    expect(k.retry('starcloud.a'), isTrue);
    expect(await k.activate('starcloud.a'), isTrue);
  });

  test('essential 插件失败会置 hasEssentialFailure', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.a', essential: true),
      () => ProbePlugin(onInitialize: (_) => throw StateError('x')),
    );
    await k.start();
    expect(k.hasEssentialFailure, isTrue);
  });

  test('宿主退出 stop()：按激活逆序停用（含 essential），且不再接受激活', () async {
    final k = makeKernel();
    final order = <String>[];
    void reg(
      String id, {
      PluginKind kind = PluginKind.feature,
      bool essential = false,
      Map<String, String> depends = const {},
    }) {
      k.registerPlugin(
        descriptor(id, kind: kind, essential: essential, depends: depends),
        () => ProbePlugin(onShutdown: () => order.add(id)),
      );
    }

    reg('starcloud.engine', kind: PluginKind.engine, essential: true);
    reg('starcloud.a');
    reg('starcloud.b', depends: {'starcloud.a': '*'});
    await k.start();

    // 用户侧仍然不允许停用 essential（退出路径才放行）。
    expect(await k.deactivate('starcloud.engine'), isNotNull);

    await k.stop();
    expect(order, ['starcloud.b', 'starcloud.a', 'starcloud.engine'],
        reason: '依赖者先停，essential 也随宿主退出停用');
    expect(k.snapshots().every((s) => s.state == PluginState.stopped), isTrue);
    expect(k.contributions.length, 0);
    expect(k.services.length, 0);
    expect(await k.activate('starcloud.a'), isFalse,
        reason: '停止后不允许被懒激活重新拉起');

    await k.stop(); // 幂等
    expect(order.length, 3);
  });
}
