/// 桥接版 Minecraft 目录 / 安装器 / 启动器。
///
/// 版本清单解析、依赖合并、natives、assets、启动参数、Java 探测与进程管理
/// 全部在 C#（Starcloud.Core）里完成；Dart 侧只做 DTO 映射与事件转发。
library;

import 'dart:async';

import 'package:path/path.dart' as p;

import '../../../contracts/minecraft.dart';
import '../../../bridge/core_bridge.dart';
import 'bridge_settings.dart';

/// 本地版本按 id 前缀识别整合包/加载器版本（与 C# 侧展示口径一致）。
({bool modded, String? loader}) _classify(String versionId) {
  final id = versionId.toLowerCase();
  if (id.contains('neoforge')) return (modded: true, loader: 'neoforge');
  if (id.contains('fabric')) return (modded: true, loader: 'fabric');
  if (id.contains('quilt')) return (modded: true, loader: 'quilt');
  if (id.contains('forge')) return (modded: true, loader: 'forge');
  return (modded: false, loader: null);
}

class BridgeCatalog implements MinecraftCatalog {
  BridgeCatalog(this._bridge, this._settings);

  final CoreBridge _bridge;
  final BridgeLauncherSettings _settings;

  @override
  Future<List<RemoteVersion>> remoteVersions() async {
    final result = await _bridge.request('catalog.versions');
    if (result is! Map<String, Object?>) return const [];
    final remote = result['remote'];
    if (remote is! List<Object?>) return const [];
    final versions = <RemoteVersion>[];
    for (final item in remote) {
      if (item is! Map<String, Object?>) continue;
      final id = item['id'] as String?;
      if (id == null || id.isEmpty) continue;
      versions.add(RemoteVersion(
        id: id,
        type: item['type'] as String? ?? 'release',
        url: item['url'] as String? ?? '',
        releaseTime: item['releaseTime'] as String? ?? '',
      ));
    }
    return versions;
  }

  @override
  Future<List<InstalledVersion>> installedVersions() async {
    final result = await _bridge.request('catalog.installed');
    if (result is! Map<String, Object?>) return const [];
    final installed = result['installed'];
    if (installed is! List<Object?>) return const [];
    final versionsRoot = p.join(_settings.gameDir(), 'versions');
    final versions = <InstalledVersion>[];
    for (final item in installed) {
      if (item is! Map<String, Object?>) continue;
      final id = item['id'] as String?;
      if (id == null || id.isEmpty) continue;
      final kind = _classify(id);
      versions.add(InstalledVersion(
        id: id,
        // 目录以 C# 返回为准：外部改了设置时，Dart 缓存的 gameDir 可能已过期。
        directory: item['directory'] as String? ?? p.join(versionsRoot, id),
        isModded: kind.modded,
        modLoader: kind.loader,
        sizeBytes: (item['sizeBytes'] as num?)?.toInt() ?? 0,
        jsonPath: item['jsonPath'] as String?,
        jarExists: item['jarExists'] == true,
        lastPlayedAt: item['lastPlayedAt'] as String?,
        isDefault: item['isDefault'] == true,
        isolated: item['isolated'] != false,
        inheritsFrom: item['inheritsFrom'] as String?,
      ));
    }
    return versions;
  }

  @override
  Future<void> deleteVersion(String versionId) async {
    await _bridge.request('versions.delete', {'versionId': versionId});
  }

  @override
  Future<void> openVersionFolder(String versionId) async {
    await _bridge.request('versions.openFolder', {'versionId': versionId});
  }

  @override
  Future<void> setDefaultVersion(String? versionId) async {
    await _bridge.request('versions.setDefault', {
      'versionId': ?versionId,
    });
  }
}

class BridgeInstaller implements MinecraftInstaller {
  BridgeInstaller(this._bridge);

  final CoreBridge _bridge;

  @override
  Future<void> install(
    String versionId, {
    void Function(InstallProgress progress)? onProgress,
  }) async {
    final done = Completer<void>();
    Timer? watchdog;

    // 事件丢失/宿主半死时不能让 UI 永远转圈：10 分钟无任何进度即判死。
    void armWatchdog() {
      watchdog?.cancel();
      watchdog = Timer(const Duration(minutes: 10), () {
        if (!done.isCompleted) {
          done.completeError(
            TimeoutException('安装长时间没有进度（10 分钟），已放弃等待'),
          );
        }
      });
    }

    armWatchdog();
    // 先订阅再发请求：C# 的进度事件可能早于 "已受理" 回执到达。
    final subscription = _bridge.events.listen((event) {
      if (done.isCompleted) return;
      if (!_matches(event, versionId)) return;
      if (event.name == 'install.progress') {
        armWatchdog();
        final params = event.params;
        onProgress?.call(InstallProgress(
          params['stage'] as String? ?? '安装',
          params['detail'] as String? ?? '',
          current: (params['current'] as num?)?.toInt(),
          total: (params['total'] as num?)?.toInt(),
        ));
      } else if (event.name == 'install.done') {
        done.complete();
      } else if (event.name == 'install.failed') {
        done.completeError(
          StateError(event.params['error'] as String? ?? '安装失败'),
        );
      }
    }, onError: (Object e) {
      if (!done.isCompleted) done.completeError(e);
    });

    try {
      // 只等"已受理"回执；真正的完成由事件驱动（安装可能持续几分钟）。
      await _bridge.requestWithTimeout(
        'install.vanilla',
        {'versionId': versionId},
        const Duration(seconds: 15),
      );
    } catch (_) {
      await subscription.cancel();
      watchdog?.cancel();
      done.future.ignore();
      rethrow;
    }

    try {
      await done.future;
    } finally {
      watchdog?.cancel();
      await subscription.cancel();
    }
  }

  bool _matches(BridgeEvent event, String versionId) {
    final id = event.params['versionId'] as String?;
    // 兼容旧宿主：进度事件不带 versionId 时视为当前任务（宿主侧单飞）。
    return id == null || id == versionId;
  }
}

class BridgeLauncher implements GameLauncher {
  BridgeLauncher(this._bridge);

  final CoreBridge _bridge;

  @override
  Future<JavaRuntime?> detectJava() async {
    final result = await _bridge.request('java.list');
    if (result is! Map<String, Object?>) return null;
    final runtimes = result['runtimes'];
    if (runtimes is! List<Object?>) return null;
    JavaRuntime? best;
    for (final item in runtimes) {
      if (item is! Map<String, Object?>) continue;
      final executable = item['executable'] as String?;
      if (executable == null || executable.isEmpty) continue;
      final runtime = JavaRuntime(
        path: executable,
        version: item['versionString'] as String? ?? '',
        major: (item['majorVersion'] as num?)?.toInt() ?? 0,
      );
      if (best == null ||
          runtime.major > best.major ||
          (runtime.major == best.major &&
              runtime.version.compareTo(best.version) > 0)) {
        best = runtime;
      }
    }
    return best;
  }

  @override
  Future<GameSession> launch({
    required String versionId,
    required String playerName,
    String? accountUuid,
  }) async {
    // 单订阅控制器会在首个 listener 之前缓冲事件：启动早期的日志（最需要
    // 的崩溃日志）不会因为调用方还没拿到 session 而丢失。
    final output = StreamController<String>();
    final exited = Completer<int>();
    final buffered = <BridgeEvent>[];
    int? sessionId;

    void handle(BridgeEvent event) {
      final id = (event.params['sessionId'] as num?)?.toInt();
      if (sessionId == null) {
        buffered.add(event);
        return;
      }
      if (id != sessionId) return;
      if (event.name == 'game.output') {
        if (!output.isClosed) {
          output.add(event.params['line'] as String? ?? '');
        }
      } else if (event.name == 'game.exited') {
        if (!exited.isCompleted) {
          exited.complete((event.params['code'] as num?)?.toInt() ?? -1);
        }
      }
    }

    // 先订阅：进程输出可能早于 launch.start 的响应。
    final subscription = _bridge.events.listen(handle, onError: (Object e) {
      if (!exited.isCompleted) exited.completeError(e);
      if (!output.isClosed) output.addError(e);
    });

    final int pid;
    try {
      final result = await _bridge.requestWithTimeout(
        'launch.start',
        {
          'versionId': versionId,
          'playerName': playerName,
          'accountUuid': ?accountUuid,
        },
        const Duration(seconds: 60),
      );
      if (result is! Map<String, Object?>) {
        throw StateError('桥接未返回会话信息');
      }
      sessionId = (result['sessionId'] as num?)?.toInt();
      if (sessionId == null) throw StateError('桥接未返回 sessionId');
      pid = (result['pid'] as num?)?.toInt() ?? -1;
    } catch (_) {
      await subscription.cancel();
      exited.future.ignore();
      await output.close();
      rethrow;
    }

    for (final event in buffered) {
      handle(event);
    }
    buffered.clear();

    unawaited(exited.future.whenComplete(() async {
      await subscription.cancel();
      if (!output.isClosed) await output.close();
    }));

    return GameSession(
      pid: pid,
      logLines: output.stream,
      exitCode: exited.future,
    );
  }

  /// 结束游戏进程（契约外的运维口子，供后续"运行状态"页使用）。
  Future<void> kill(int sessionId) async {
    await _bridge.request('game.kill', {'sessionId': sessionId});
  }
}
