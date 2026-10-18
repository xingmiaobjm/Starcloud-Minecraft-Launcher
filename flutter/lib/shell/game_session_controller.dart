/// 游戏会话状态（跨页面存活）。
///
/// 之前这些状态放在 HomePage 的 State 里，而 fluent_ui 的 NavigationView 只在
/// 选中时挂载页面：切到下载/账户页会销毁 HomePage，日志订阅被取消、`_launching`
/// 归零——回到首页既能重复点"启动"（双开），也看不到后台游戏的日志。
/// 会话属于应用级状态，放到 Riverpod 里，页面只是它的视图。
library;

import 'dart:async';

import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../contracts/minecraft.dart';

class GameSessionState {
  const GameSessionState({
    this.launching = false,
    this.pid,
    this.status,
    this.logs = const [],
  });

  final bool launching;
  final int? pid;
  final String? status;

  /// 最近日志（尾部截断，避免长时间运行无限增长）。
  final List<String> logs;

  GameSessionState copyWith({
    bool? launching,
    int? pid,
    String? status,
    List<String>? logs,
  }) {
    return GameSessionState(
      launching: launching ?? this.launching,
      pid: pid ?? this.pid,
      status: status ?? this.status,
      logs: logs ?? this.logs,
    );
  }
}

final gameSessionProvider =
    NotifierProvider<GameSessionController, GameSessionState>(
  GameSessionController.new,
);

class GameSessionController extends Notifier<GameSessionState> {
  static const int _maxLogLines = 500;

  StreamSubscription<String>? _logSub;
  GameSession? _session;
  bool _disposed = false;

  @override
  GameSessionState build() {
    ref.onDispose(() {
      _disposed = true;
      unawaited(_logSub?.cancel());
    });
    return const GameSessionState();
  }

  /// 启动游戏；已在启动/运行中时忽略（防双开）。
  Future<void> launch({
    required GameLauncher launcher,
    required String versionId,
    required String playerName,
    String? accountUuid,
  }) async {
    if (state.launching || _session != null) {
      state = state.copyWith(
        status: '已有游戏会话在运行，请先关闭游戏',
      );
      return;
    }
    await _logSub?.cancel();
    _logSub = null;
    _session = null;
    state = const GameSessionState(launching: true, status: '启动中…');

    try {
      final session = await launcher.launch(
        versionId: versionId,
        playerName: playerName,
        accountUuid: accountUuid,
      );
      if (_disposed) return;
      _session = session;
      state = state.copyWith(
        launching: false,
        pid: session.pid,
        status: '游戏运行中（pid ${session.pid}）',
      );
      _logSub = session.logLines.listen(
        (line) {
          if (_disposed) return;
          final logs = [...state.logs, line];
          if (logs.length > _maxLogLines) {
            logs.removeRange(0, logs.length - _maxLogLines);
          }
          state = state.copyWith(logs: logs);
        },
        onError: (Object e) {
          if (_disposed) return;
          state = state.copyWith(launching: false, status: '日志流中断：$e');
        },
      );
      unawaited(session.exitCode.then((code) {
        if (_disposed) return;
        _session = null;
        state = state.copyWith(
          launching: false,
          status: '游戏退出（exit=$code）',
        );
      }).catchError((Object e) {
        if (_disposed) return;
        _session = null;
        state = state.copyWith(
          launching: false,
          status: '游戏进程异常：$e',
        );
      }));
    } catch (e) {
      if (_disposed) return;
      state = state.copyWith(launching: false, status: '启动失败：$e');
    }
  }

  /// 清空日志显示（不影响游戏进程）。
  void clearLogs() {
    if (_disposed) return;
    state = state.copyWith(logs: const []);
  }
}
