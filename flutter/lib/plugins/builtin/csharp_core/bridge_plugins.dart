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

  @override
  Future<List<PluginPanel>> panels(String id) async {
    final result = await _bridge.requestWithTimeout(
      'plugins.panels',
      {'id': id},
      const Duration(seconds: 60),
    );
    if (result is! Map<String, Object?>) return const [];
    final panels = result['panels'];
    if (panels is! List<Object?>) return const [];
    final out = <PluginPanel>[];
    for (final item in panels) {
      if (item is! Map<String, Object?>) continue;
      final panelId = item['id'] as String?;
      if (panelId == null || panelId.isEmpty) continue;
      out.add(PluginPanel(
        id: panelId,
        title: item['title'] as String? ?? panelId,
        declarative: item['declarative'] == true,
        entry: item['entry'] as String? ?? '',
      ));
    }
    return out;
  }

  @override
  Future<PluginViewData> view(String id, String panelId) async {
    final result = await _bridge.requestWithTimeout(
      'plugins.view',
      {'id': id, 'panelId': panelId},
      const Duration(seconds: 60),
    );
    if (result is! Map<String, Object?>) {
      return const PluginViewData(errors: ['桥接未返回视图']);
    }
    final errors = result['errors'];
    final bindings = <String, Object?>{};
    final rawBindings = result['bindings'];
    if (rawBindings is Map<String, Object?>) {
      for (final entry in rawBindings.entries) {
        bindings[entry.key] = entry.value;
      }
    }
    return PluginViewData(
      node: _nodeFrom(result['view']),
      errors: errors is List<Object?> ? errors.whereType<String>().toList() : const [],
      bindings: bindings,
    );
  }

  @override
  Future<String?> invoke(String id, String api, Object? args) async {
    final result = await _bridge.requestWithTimeout(
      'plugins.invoke',
      {'id': id, 'api': api, 'args': args ?? const <Object?>[]},
      const Duration(minutes: 2),
    );
    if (result is Map<String, Object?>) {
      if (result['ok'] == true) return result['result'] as String?;
      throw StateError(result['error'] as String? ?? '动作执行失败');
    }
    return null;
  }

  static PluginViewNode? _nodeFrom(Object? raw) {
    if (raw is! Map<String, Object?>) return null;
    final type = raw['type'] as String?;
    if (type == null || type.isEmpty) return null;
    final valueRaw = raw['value'];
    var value = const PluginViewValue('');
    if (valueRaw is Map<String, Object?>) {
      value = PluginViewValue(
        valueRaw['text'] as String? ?? '',
        _bindingFrom(valueRaw['bind']),
      );
    }
    final actionRaw = raw['action'];
    final children = <PluginViewNode>[];
    final childrenRaw = raw['children'];
    if (childrenRaw is List<Object?>) {
      for (final child in childrenRaw) {
        final node = _nodeFrom(child);
        if (node != null) children.add(node);
      }
    }
    return PluginViewNode(
      type: type,
      value: value,
      itemsBind: _bindingFrom(raw['itemsBind']),
      bind: _bindingFrom(raw['bind']),
      itemView: raw['itemView'] as String? ?? '',
      style: raw['style'] as String? ?? '',
      direction: raw['direction'] as String? ?? 'column',
      kind: raw['kind'] as String? ?? 'normal',
      placeholder: raw['placeholder'] as String? ?? '',
      source: raw['source'] as String? ?? '',
      href: raw['href'] as String? ?? '',
      language: raw['language'] as String? ?? '',
      wrap: raw['wrap'] != false,
      multiline: raw['multiline'] == true,
      spacing: (raw['spacing'] as num?)?.toDouble() ?? 0,
      height: (raw['height'] as num?)?.toDouble() ?? 0,
      progress: (raw['progress'] as num?)?.toDouble() ?? 0,
      options: _strings(raw['options']),
      columns: _strings(raw['columns']),
      action: actionRaw is Map<String, Object?>
          ? PluginViewAction(
              api: actionRaw['api'] as String? ?? '',
              argsJson: actionRaw['argsJson'] as String? ?? '[]',
              handler: actionRaw['handler'] as String? ?? '',
            )
          : null,
      children: children,
    );
  }

  static PluginViewBinding? _bindingFrom(Object? raw) {
    if (raw is! Map<String, Object?>) return null;
    final scope = raw['scope'] as String?;
    final key = raw['key'] as String?;
    if (scope == null || key == null || scope.isEmpty) return null;
    return PluginViewBinding(scope: scope, key: key);
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
      loaded: raw['loaded'] == true,
      panelCount: (raw['panelCount'] as num?)?.toInt() ?? 0,
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
