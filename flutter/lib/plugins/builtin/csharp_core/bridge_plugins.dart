/// 桥接版外部插件存储：清单/权限/安装/启停/卸载都落到 C#。
library;

import '../../../bridge/core_bridge.dart';
import '../../../contracts/plugins.dart';

class BridgePluginStore implements PluginStore {
  BridgePluginStore(this._bridge);

  final CoreBridge _bridge;

  @override
  Future<List<ExternalPlugin>> list() async {
    final result = await _bridge.request('plugins.list');
    if (result is! Map<String, Object?>) return const [];
    final plugins = result['plugins'];
    if (plugins is! List<Object?>) return const [];
    return [for (final item in plugins) ?_pluginFrom(item)];
  }

  @override
  Future<ExternalPlugin> install(String path) async {
    final result = await _bridge.requestWithTimeout(
      'plugins.install',
      {'path': path},
      const Duration(minutes: 2),
    );
    if (result is Map<String, Object?>) {
      final plugin = _pluginFrom(result['plugin']);
      if (plugin != null) return plugin;
    }
    throw StateError('桥接未返回插件信息');
  }

  @override
  Future<void> uninstall(String id) async {
    await _bridge.request('plugins.uninstall', {'id': id});
  }

  @override
  Future<void> setEnabled(
    String id,
    bool enabled, {
    bool acceptDisclaimer = false,
  }) async {
    await _bridge.request('plugins.setEnabled', {
      'id': id,
      'enabled': enabled,
      'acceptDisclaimer': acceptDisclaimer,
    });
  }

  @override
  Future<void> openFolder(String id) async {
    await _bridge.request('plugins.openFolder', {'id': id});
  }

  static ExternalPlugin? _pluginFrom(Object? raw) {
    if (raw is! Map<String, Object?>) return null;
    final id = raw['id'] as String?;
    if (id == null || id.isEmpty) return null;
    return ExternalPlugin(
      id: id,
      name: raw['name'] as String? ?? id,
      version: raw['version'] as String? ?? '',
      apiVersion: raw['apiVersion'] as String? ?? '',
      author: raw['author'] as String? ?? '',
      description: raw['description'] as String? ?? '',
      permissions: _strings(raw['permissions']),
      optionalPermissions: _strings(raw['optionalPermissions']),
      runtime: raw['runtime'] as String?,
      official: raw['official'] == true,
      enabled: raw['enabled'] == true,
      disclaimerAccepted: raw['disclaimerAccepted'] == true,
      dir: raw['dir'] as String? ?? '',
      dependencies: _stringMap(raw['dependencies']),
      conflicts: _stringMap(raw['conflicts']),
      homepage: raw['homepage'] as String?,
      license: raw['license'] as String?,
      keywords: _strings(raw['keywords']),
      api2: raw['api2'] == true,
    );
  }

  static List<String> _strings(Object? raw) =>
      raw is List<Object?> ? raw.whereType<String>().toList() : const [];

  static Map<String, String> _stringMap(Object? raw) {
    if (raw is! Map<String, Object?>) return const {};
    final out = <String, String>{};
    for (final entry in raw.entries) {
      if (entry.value is String) out[entry.key] = entry.value as String;
    }
    return out;
  }
}
