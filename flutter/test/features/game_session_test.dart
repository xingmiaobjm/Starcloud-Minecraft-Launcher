import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/contracts/minecraft.dart';
import 'package:starcloud_launcher/shell/game_session_controller.dart';

/// 会话状态上移后的行为：防双开、日志累积、退出状态。
void main() {
  late ProviderContainer container;
  late _FakeLauncher launcher;

  setUp(() {
    container = ProviderContainer();
    launcher = _FakeLauncher();
  });
  tearDown(() => container.dispose());

  GameSessionController controller() =>
      container.read(gameSessionProvider.notifier);

  test('启动成功后记录 pid 与日志；重复启动被忽略（防双开）', () async {
    await controller().launch(
      launcher: launcher,
      versionId: '1.20.4',
      playerName: 'Notch',
    );
    expect(launcher.launches, 1);
    expect(container.read(gameSessionProvider).launching, isFalse);
    expect(container.read(gameSessionProvider).pid, 42);

    launcher.output.add('hello');
    await Future<void>.delayed(Duration.zero);
    expect(container.read(gameSessionProvider).logs, ['hello']);

    await controller().launch(
      launcher: launcher,
      versionId: '1.20.4',
      playerName: 'Notch',
    );
    expect(launcher.launches, 1, reason: '已有会话时不得再次拉起进程');
    expect(container.read(gameSessionProvider).status, contains('已有游戏会话'));
  });

  test('退出码完成 → 状态更新，且允许再次启动', () async {
    await controller().launch(
      launcher: launcher,
      versionId: '1.20.4',
      playerName: 'Notch',
    );
    launcher.exit.complete(0);
    await Future<void>.delayed(Duration.zero);
    expect(container.read(gameSessionProvider).status, contains('exit=0'));

    launcher.exit = Completer<int>();
    await controller().launch(
      launcher: launcher,
      versionId: '1.20.4',
      playerName: 'Notch',
    );
    expect(launcher.launches, 2);
  });

  test('启动抛异常 → 状态可读，不冒泡', () async {
    launcher.error = StateError('boom');
    await controller().launch(
      launcher: launcher,
      versionId: '1.20.4',
      playerName: 'Notch',
    );
    expect(container.read(gameSessionProvider).launching, isFalse);
    expect(container.read(gameSessionProvider).status, contains('boom'));
  });

  test('clearLogs 清空显示但不影响会话', () async {
    await controller().launch(
      launcher: launcher,
      versionId: '1.20.4',
      playerName: 'Notch',
    );
    launcher.output.add('line');
    await Future<void>.delayed(Duration.zero);
    controller().clearLogs();
    expect(container.read(gameSessionProvider).logs, isEmpty);
    expect(container.read(gameSessionProvider).pid, 42);
  });
}

class _FakeLauncher implements GameLauncher {
  final output = StreamController<String>();
  Completer<int> exit = Completer<int>();
  Object? error;
  int launches = 0;

  @override
  Future<JavaRuntime?> detectJava() async => null;

  @override
  Future<GameSession> launch({
    required String versionId,
    required String playerName,
  }) async {
    launches++;
    if (error != null) throw error!;
    return GameSession(
      pid: 42,
      logLines: output.stream,
      exitCode: exit.future,
    );
  }
}
