import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/kernel/event_bus.dart';

class Ping {
  const Ping(this.n);
  final int n;
}

class Pong {
  const Pong();
}

void main() {
  test('按类型分发', () {
    final bus = EventBus();
    final got = <int>[];
    bus.subscribe<Ping>((e) => got.add(e.n));
    bus.subscribe<Pong>((_) => got.add(-1));
    bus.emit(const Ping(1));
    bus.emit(const Pong());
    bus.emit(const Ping(2));
    expect(got, [1, -1, 2]);
  });

  test('退订后不再收到；dispose 幂等', () {
    final bus = EventBus();
    var count = 0;
    final sub = bus.subscribe<Ping>((_) => count++);
    bus.emit(const Ping(1));
    sub.dispose();
    sub.dispose();
    bus.emit(const Ping(2));
    expect(count, 1);
    expect(bus.subscriberCount<Ping>(), 0);
  });

  test('一个订阅者抛异常，其它订阅者照常收到', () {
    final errors = <String>[];
    final bus = EventBus(onError: errors.add);
    final got = <int>[];
    bus.subscribe<Ping>((_) => throw StateError('boom'));
    bus.subscribe<Ping>((e) => got.add(e.n));
    bus.emit(const Ping(7));
    expect(got, [7]);
    expect(errors, hasLength(1));
    expect(errors.single, contains('boom'));
  });

  test('订阅者在回调里退订不影响本轮其余订阅者', () {
    final bus = EventBus();
    final got = <int>[];
    late Subscription first;
    first = bus.subscribe<Ping>((e) {
      got.add(e.n);
      first.dispose();
    });
    bus.subscribe<Ping>((e) => got.add(e.n * 10));
    bus.emit(const Ping(1));
    expect(got, [1, 10]);
  });
}
