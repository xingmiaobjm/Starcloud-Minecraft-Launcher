import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/kernel/service_registry.dart';

abstract class IA {
  String get v;
}

abstract class IB {
  String get v;
}

class ImplA implements IA {
  ImplA(this.v);
  @override
  final String v;
}

class ImplB implements IB {
  ImplB(this.v);
  @override
  final String v;
}

void main() {
  test('同契约多实现：取优先级最高者', () {
    final r = ServiceRegistry();
    r.add<IA>(ImplA('low'), priority: 0, providerId: 'p.low');
    r.add<IA>(ImplA('high'), priority: 10, providerId: 'p.high');
    expect(r.get<IA>().v, 'high');
  });

  test('同优先级：后注册者胜；停用后回到次高', () {
    final r = ServiceRegistry();
    r.add<IA>(ImplA('first'), providerId: 'p.first');
    r.add<IA>(ImplA('second'), providerId: 'p.second');
    expect(r.get<IA>().v, 'second');
    r.removeByProvider('p.second');
    expect(r.get<IA>().v, 'first');
  });

  test('getAll 按优先级降序，同优先级后注册在前', () {
    final r = ServiceRegistry();
    r.add<IA>(ImplA('a'), priority: 1, providerId: 'p.a');
    r.add<IA>(ImplA('b'), priority: 5, providerId: 'p.b');
    r.add<IA>(ImplA('c'), priority: 5, providerId: 'p.c');
    expect(r.getAll<IA>().map((x) => x.v).toList(), ['c', 'b', 'a']);
  });

  test('停用插件 → 其登记全部消失，Get 抛可读错误', () {
    final r = ServiceRegistry();
    r.add<IA>(ImplA('x'), providerId: 'p.x');
    r.add<IB>(ImplB('y'), providerId: 'p.x');
    r.removeByProvider('p.x');
    expect(r.tryGet<IA>(), isNull);
    expect(() => r.get<IA>(), throwsA(isA<MissingContractException>()));
    expect(r.countForProvider('p.x'), 0);
  });

  test('providerOf 报告当前最高优先级提供者', () {
    final r = ServiceRegistry();
    r.add<IA>(ImplA('x'), priority: 2, providerId: 'p.x');
    expect(r.providerOf<IA>(), 'p.x');
    expect(r.providerOf<IB>(), isNull);
  });

  test('快照可枚举契约/提供者/优先级', () {
    final r = ServiceRegistry();
    r.add<IA>(ImplA('x'), priority: 3, providerId: 'p.x');
    final snap = r.snapshot();
    expect(snap.length, 1);
    expect(snap.single.contract, IA);
    expect(snap.single.providerId, 'p.x');
    expect(snap.single.priority, 3);
  });

  test('并发登记/解析不崩溃（事件循环串行化的压测）', () async {
    final r = ServiceRegistry();
    final futures = <Future<void>>[];
    for (var t = 0; t < 2; t++) {
      futures.add(Future(() async {
        for (var i = 0; i < 1000; i++) {
          r.add<IA>(ImplA('v$t-$i'), providerId: 'p.$t');
          r.get<IA>();
        }
      }));
    }
    await Future.wait(futures);
    expect(r.snapshot().length, 2000);
  });
}
