/// 桥接版联机控制：状态/操作全部落到 C# 的 MultiplayerService。
library;

import 'dart:async';

import '../../../bridge/core_bridge.dart';
import '../../../contracts/multiplayer.dart';

class BridgeMultiplayer implements MultiplayerControl {
  BridgeMultiplayer(this._bridge) {
    _sub = _bridge.events.listen((event) {
      if (event.name != 'multiplayer.changed') return;
      if (!_changes.isClosed) _changes.add(_snapshotFrom(event.params));
    }, onError: (Object e) {
      if (!_changes.isClosed) _changes.addError(e);
    });
  }

  final CoreBridge _bridge;
  final StreamController<MultiplayerSnapshot> _changes =
      StreamController<MultiplayerSnapshot>.broadcast();
  late final StreamSubscription<BridgeEvent> _sub;

  @override
  Stream<MultiplayerSnapshot> get changes => _changes.stream;

  @override
  Future<MultiplayerSnapshot> snapshot() async =>
      _snapshotFrom(await _bridge.request('multiplayer.state'));

  Future<MultiplayerSnapshot> _action(
    String method, [
    Map<String, Object?> params = const {},
  ]) async {
    final result = await _bridge.requestWithTimeout(
      method,
      params,
      const Duration(minutes: 2),
    );
    return _snapshotFrom(result);
  }

  @override
  Future<MultiplayerSnapshot> selectProvider(String id) =>
      _action('multiplayer.selectProvider', {'id': id});

  @override
  Future<MultiplayerSnapshot> setNickname(String name) =>
      _action('multiplayer.setNickname', {'name': name});

  @override
  Future<MultiplayerSnapshot> setNodeRegion(String region) =>
      _action('multiplayer.setNodeRegion', {'region': region});

  @override
  Future<MultiplayerSnapshot> host() => _action('multiplayer.host');

  @override
  Future<MultiplayerSnapshot> join(String room) =>
      _action('multiplayer.join', {'room': room});

  @override
  Future<MultiplayerSnapshot> idle() => _action('multiplayer.idle');

  @override
  Future<MultiplayerSnapshot> stop() => _action('multiplayer.stop');

  @override
  Future<void> installCore({
    void Function(MultiplayerProgress progress)? onProgress,
  }) async {
    final done = Completer<void>();
    final sub = _bridge.events.listen((event) {
      if (done.isCompleted) return;
      if (event.name == 'multiplayer.progress') {
        final params = event.params;
        onProgress?.call(MultiplayerProgress(
          stage: params['stage'] as String? ?? '安装',
          bytes: (params['bytes'] as num?)?.toInt() ?? 0,
          total: (params['total'] as num?)?.toInt(),
          speedBps: (params['speedBps'] as num?)?.toDouble() ?? 0,
        ));
      } else if (event.name == 'multiplayer.install.done') {
        done.complete();
      } else if (event.name == 'multiplayer.install.failed') {
        done.completeError(
          StateError(event.params['error'] as String? ?? '联机核心安装失败'),
        );
      }
    }, onError: (Object e) {
      if (!done.isCompleted) done.completeError(e);
    });

    try {
      await _bridge.requestWithTimeout(
        'multiplayer.install',
        const {},
        const Duration(seconds: 15),
      );
    } catch (_) {
      await sub.cancel();
      done.future.ignore();
      rethrow;
    }

    try {
      await done.future;
    } finally {
      await sub.cancel();
    }
  }

  @override
  Future<String> logs() async {
    final result = await _bridge.requestWithTimeout(
      'multiplayer.logs',
      const {},
      const Duration(seconds: 60),
    );
    if (result is Map<String, Object?>) {
      return result['logs'] as String? ?? '';
    }
    return '';
  }

  @override
  Future<List<String>> nodes() async {
    final result = await _bridge.requestWithTimeout(
      'multiplayer.nodes',
      const {},
      const Duration(seconds: 60),
    );
    if (result is Map<String, Object?>) {
      final nodes = result['nodes'];
      if (nodes is List<Object?>) return nodes.whereType<String>().toList();
    }
    return const [];
  }

  Future<void> dispose() async {
    await _sub.cancel();
    await _changes.close();
  }

  static MultiplayerSnapshot _snapshotFrom(Object? raw) {
    final map = raw is Map<String, Object?> ? raw : const <String, Object?>{};

    final providers = <MultiplayerProviderInfo>[];
    final list = map['providers'];
    if (list is List<Object?>) {
      for (final item in list) {
        if (item is! Map<String, Object?>) continue;
        final id = item['id'] as String?;
        if (id == null || id.isEmpty) continue;
        providers.add(MultiplayerProviderInfo(
          id: id,
          displayName: item['displayName'] as String? ?? id,
          vendor: item['vendor'] as String? ?? '',
          vendorUrl: item['vendorUrl'] as String?,
          description: item['description'] as String?,
          builtIn: item['builtIn'] == true,
        ));
      }
    }

    final core = map['core'] is Map<String, Object?>
        ? map['core'] as Map<String, Object?>
        : const <String, Object?>{};
    final state = map['state'] is Map<String, Object?>
        ? map['state'] as Map<String, Object?>
        : const <String, Object?>{};
    final players = state['players'];
    return MultiplayerSnapshot(
      providers: providers,
      activeId: map['activeId'] as String? ?? '',
      nickname: map['nickname'] as String? ?? '',
      nodeRegion: map['nodeRegion'] as String? ?? 'auto',
      core: MultiplayerCoreStatus(
        installed: core['installed'] == true,
        version: core['version'] as String?,
        sessionRunning: core['sessionRunning'] == true,
      ),
      state: MultiplayerState(
        phase: _phase(state['phase'] as String?),
        room: state['room'] as String?,
        address: state['address'] as String?,
        difficulty: state['difficulty'] as String?,
        players:
            players is List<Object?> ? players.whereType<String>().toList() : const [],
        errorKind: state['errorKind'] as String?,
        error: state['error'] as String?,
        isBusy: state['isBusy'] == true,
      ),
      lastError: map['lastError'] as String?,
    );
  }

  static MultiplayerPhase _phase(String? key) => switch (key) {
        'hostScanning' => MultiplayerPhase.hostScanning,
        'hostStarting' => MultiplayerPhase.hostStarting,
        'hostReady' => MultiplayerPhase.hostReady,
        'guestConnecting' => MultiplayerPhase.guestConnecting,
        'guestEntering' => MultiplayerPhase.guestEntering,
        'guestReady' => MultiplayerPhase.guestReady,
        'failed' => MultiplayerPhase.failed,
        _ => MultiplayerPhase.idle,
      };
}
