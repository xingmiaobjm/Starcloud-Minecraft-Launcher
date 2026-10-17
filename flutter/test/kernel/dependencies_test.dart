import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/kernel/descriptor.dart';

import 'support.dart';

void main() {
  test('requires：提供者未激活 → 激活插件时自动激活提供者（隐式懒激活）', () async {
    final k = makeKernel();
    final provider = ProbePlugin(provides: [IProbeA]);
    k.registerPlugin(
      descriptor('starcloud.provider',
          activation: PluginActivation.lazy, provides: [IProbeA]),
      () => provider,
    );
    k.registerPlugin(
      descriptor('starcloud.consumer', requires: [IProbeA]),
      ProbePlugin.new,
    );
    await k.start();
    // consumer 是 eager：它激活时会自动拉起 lazy 的 provider。
    final snaps = {for (final s in k.snapshots()) s.id: s};
    expect(snaps['starcloud.consumer']!.state, PluginState.active);
    expect(snaps['starcloud.provider']!.state, PluginState.active);
    expect(provider.initializeCount, 1);
  });

  test('requires：一个提供者都没有 → Failed，原因含缺失契约名', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.consumer', requires: [IProbeB]),
      ProbePlugin.new,
    );
    await k.start();
    final s = k.snapshots().single;
    expect(s.state, PluginState.failed);
    expect(s.error, contains('缺少契约提供者'));
    expect(s.error, contains('IProbeB'));
  });

  test('requires：多提供者 → 按登记顺序取第一个成功者；停掉后能换下一个', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.p1', provides: [IProbeA]),
      () => ProbePlugin(provides: [IProbeA]),
    );
    k.registerPlugin(
      descriptor('starcloud.p2', provides: [IProbeA]),
      () => ProbePlugin(provides: [IProbeA]),
    );
    k.registerPlugin(
      descriptor('starcloud.consumer', requires: [IProbeA]),
      ProbePlugin.new,
    );
    await k.start();
    expect(k.services.providerOf<IProbeA>(), 'starcloud.p2',
        reason: '同优先级后注册者胜（p2 后登记）');
    // 停掉 p2（consumer 只 requires，不 depends，不会级联）→ 回落到 p1。
    expect(await k.deactivate('starcloud.p2'), isNull);
    expect(k.services.providerOf<IProbeA>(), 'starcloud.p1');
  });

  test('停用提供者 → 依赖它的插件按 requires 级联停用（不留僵尸）', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.provider', provides: [IProbeA]),
      () => ProbePlugin(provides: [IProbeA]),
    );
    k.registerPlugin(
      descriptor('starcloud.consumer', requires: [IProbeA]),
      ProbePlugin.new,
    );
    await k.start();
    expect(await k.deactivate('starcloud.provider'), isNull);
    final snaps = {for (final s in k.snapshots()) s.id: s};
    expect(snaps['starcloud.provider']!.state, PluginState.stopped);
    expect(snaps['starcloud.consumer']!.state, PluginState.stopped,
        reason: 'requires 的提供者没了，消费者必须被级联停用');
  });

  test('依赖链 A→B→C：激活 A 拉起 B、C；停 C 反向级联停 A、B', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.c'),
      () => ProbePlugin(provides: [IProbeA]),
    );
    k.registerPlugin(
      descriptor('starcloud.b', depends: {'starcloud.c': '^1.0.0'}),
      ProbePlugin.new,
    );
    k.registerPlugin(
      descriptor('starcloud.a', depends: {'starcloud.b': '^1.0.0'}),
      ProbePlugin.new,
    );
    await k.start();
    final states = {for (final s in k.snapshots()) s.id: s.state};
    expect(states.values.every((s) => s == PluginState.active), isTrue);

    expect(await k.deactivate('starcloud.c'), isNull);
    final after = {for (final s in k.snapshots()) s.id: s.state};
    expect(after['starcloud.a'], PluginState.stopped);
    expect(after['starcloud.b'], PluginState.stopped);
    expect(after['starcloud.c'], PluginState.stopped);
  });

  test('级联停用幂等：重复停用不报错、不重复撤贡献', () async {
    final k = makeKernel();
    k.registerPlugin(descriptor('starcloud.c'), ProbePlugin.new);
    k.registerPlugin(
      descriptor('starcloud.b', depends: {'starcloud.c': '*'}),
      ProbePlugin.new,
    );
    await k.start();
    expect(await k.deactivate('starcloud.c'), isNull);
    expect(await k.deactivate('starcloud.c'), isNull);
    expect(await k.deactivate('starcloud.b'), isNull);
  });

  test('essential 插件禁止停用/禁用', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.essential', essential: true),
      ProbePlugin.new,
    );
    await k.start();
    expect(await k.deactivate('starcloud.essential'), contains('必需'));
    expect(await k.disable('starcloud.essential'), contains('必需'));
  });

  test('被 essential 插件依赖的插件也禁止停用', () async {
    final k = makeKernel();
    k.registerPlugin(descriptor('starcloud.base'), ProbePlugin.new);
    k.registerPlugin(
      descriptor('starcloud.core',
          essential: true, depends: {'starcloud.base': '*'}),
      ProbePlugin.new,
    );
    await k.start();
    final reason = await k.deactivate('starcloud.base');
    expect(reason, isNotNull);
    expect(reason, contains('必需'));
    expect(
      k.snapshots().firstWhere((s) => s.id == 'starcloud.base').state,
      PluginState.active,
    );
  });

  test('conflicts 命中 → 后激活者 Failed，先激活者不受影响', () async {
    final k = makeKernel();
    k.registerPlugin(
      descriptor('starcloud.a', kind: PluginKind.engine),
      ProbePlugin.new,
    );
    k.registerPlugin(
      descriptor('starcloud.b',
          kind: PluginKind.feature, conflicts: {'starcloud.a': '*'}),
      ProbePlugin.new,
    );
    await k.start();
    final snaps = {for (final s in k.snapshots()) s.id: s};
    expect(snaps['starcloud.a']!.state, PluginState.active);
    expect(snaps['starcloud.b']!.state, PluginState.failed);
    expect(snaps['starcloud.b']!.error, contains('冲突'));
  });

  test('Get 未登记的契约 → 抛可读错误（不静默）', () async {
    final k = makeKernel();
    await k.start();
    expect(() => k.get<IProbeB>(), throwsA(isA<Exception>()));
  });

  test('disable → enable 回到可用；disabled 时激活请求被拒', () async {
    final k = makeKernel();
    k.registerPlugin(descriptor('starcloud.a'), ProbePlugin.new);
    await k.start();
    expect(await k.disable('starcloud.a'), isNull);
    expect(k.snapshots().single.state, PluginState.disabled);
    expect(await k.activate('starcloud.a'), isFalse);
    k.enable('starcloud.a');
    expect(await k.activate('starcloud.a'), isTrue);
  });
}
