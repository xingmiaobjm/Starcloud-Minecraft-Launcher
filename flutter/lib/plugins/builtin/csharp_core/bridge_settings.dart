/// 桥接版启动器设置。
///
/// 设置的权威副本在 C# 侧（`launcher.settings.json`，Windows 端同一份模型）：
/// 下载源、游戏目录、Java、内存都必须与安装/启动引擎看到的一致，否则
/// UI 改了设置而 C# 仍按旧值下载，属于最典型的"两套配置漂移"。
///
/// 契约的 getter 是同步的，所以这里缓存一份快照；写入先落到 C# 再更新缓存
/// 并通知监听者（写失败时 UI 不会显示成功）。
library;

import '../../../contracts/minecraft.dart';
import '../../../bridge/core_bridge.dart';

class BridgeLauncherSettings implements LauncherSettings {
  BridgeLauncherSettings(this._bridge);

  final CoreBridge _bridge;
  final List<void Function()> _listeners = [];

  String _gameDir = '';
  String _javaPath = '';
  int _memoryMb = 4096;
  DownloadSource _source = DownloadSource.auto;

  /// 拉取 C# 侧当前设置（插件激活时调用一次）。
  Future<void> refresh() async {
    final result = await _bridge.request('settings.get');
    _apply(result);
  }

  void _apply(Object? result) {
    if (result is! Map<String, Object?>) return;
    final gameDir = result['gameDir'];
    if (gameDir is String && gameDir.isNotEmpty) _gameDir = gameDir;
    _javaPath = (result['javaExecutable'] as String?) ?? '';
    _memoryMb = (result['maxMemoryMb'] as num?)?.toInt() ?? _memoryMb;
    _source = switch (result['downloadSource']) {
      'bmclapi' => DownloadSource.bmclapi,
      'official' => DownloadSource.official,
      // auto 以及 fastmirror/rainyun/custom 等其它 C# 源：Dart 侧统一按自动处理，
      // 避免"读不懂就当成 official"把多源竞速悄悄关掉。
      _ => DownloadSource.auto,
    };
  }

  Future<void> _patch(Map<String, Object?> values) async {
    final result = await _bridge.request('settings.apply', values);
    _apply(result);
    _notify();
  }

  void _notify() {
    for (final listener in List<void Function()>.of(_listeners)) {
      listener();
    }
  }

  @override
  String gameDir() => _gameDir;

  @override
  Future<void> setGameDir(String path) async {
    final value = path.trim();
    if (value.isEmpty) throw ArgumentError('游戏目录不能为空');
    await _patch({'gameDir': value});
  }

  @override
  String javaPath() => _javaPath;

  @override
  Future<void> setJavaPath(String path) =>
      _patch({'javaExecutable': path.trim()});

  @override
  int memoryMb() => _memoryMb;

  @override
  Future<void> setMemoryMb(int mb) => _patch({'maxMemoryMb': mb});

  @override
  DownloadSource downloadSource() => _source;

  @override
  Future<void> setDownloadSource(DownloadSource source) => _patch({
        'downloadSource': switch (source) {
          DownloadSource.bmclapi => 'bmclapi',
          DownloadSource.official => 'official',
          DownloadSource.auto => 'auto',
        },
      });

  @override
  void addListener(void Function() listener) => _listeners.add(listener);

  @override
  void removeListener(void Function() listener) => _listeners.remove(listener);
}
