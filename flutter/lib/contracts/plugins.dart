/// 外部插件（v2 沙箱）契约：清单、权限、安装/启停/卸载。
///
/// 运行时 UI（JS/Python + WebView 视图）由 Windows 端宿主提供；其它端先做到
/// 插件包管理与权限展示，启用状态与 Windows 共享 plugin-state.json。
library;

class ExternalPlugin {
  const ExternalPlugin({
    required this.id,
    required this.name,
    required this.version,
    required this.apiVersion,
    required this.author,
    required this.description,
    this.permissions = const [],
    this.optionalPermissions = const [],
    this.runtime,
    this.official = false,
    this.enabled = false,
    this.disclaimerAccepted = false,
    this.dir = '',
    this.dependencies = const {},
    this.conflicts = const {},
    this.homepage,
    this.license,
    this.keywords = const [],
    this.api2 = false,
  });

  final String id;
  final String name;
  final String version;
  final String apiVersion;
  final String author;
  final String description;
  final List<String> permissions;
  final List<String> optionalPermissions;

  /// js / py / wasm
  final String? runtime;
  final bool official;
  final bool enabled;
  final bool disclaimerAccepted;
  final String dir;
  final Map<String, String> dependencies;
  final Map<String, String> conflicts;
  final String? homepage;
  final String? license;
  final List<String> keywords;
  final bool api2;
}

abstract class PluginStore {
  Future<List<ExternalPlugin>> list();

  /// 安装 .mclplug（zip）；失败抛异常（消息可直接展示）。
  Future<ExternalPlugin> install(String path);

  Future<void> uninstall(String id);

  /// 启用/停用。第三方插件首次启用需要 `acceptDisclaimer: true`。
  Future<void> setEnabled(
    String id,
    bool enabled, {
    bool acceptDisclaimer = false,
  });

  Future<void> openFolder(String id);
}
