/// 外部插件（v2 沙箱）契约：清单、权限、安装/启停/卸载、声明式视图。
///
/// 运行时由 C# 的 PluginHost 启动（JS / Python / 纯声明式），声明式视图在 Flutter
/// 侧原生渲染；1.x HTML 面板与 ui:html 仍需要 WebView 宿主。
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
    this.loaded = false,
    this.panelCount = 0,
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

  /// 运行时是否已加载（JS/Python/声明式）。
  final bool loaded;

  /// 清单里声明的声明式面板数量。
  final int panelCount;
}

/// 声明式视图的数据绑定：storage.&lt;key&gt; / state.&lt;key&gt; / host.&lt;字段&gt; / item.&lt;字段&gt;。
class PluginViewBinding {
  const PluginViewBinding({required this.scope, required this.key});
  final String scope;
  final String key;
}

class PluginViewValue {
  const PluginViewValue(this.text, [this.bind]);
  final String text;
  final PluginViewBinding? bind;
}

class PluginViewAction {
  const PluginViewAction({
    this.api = '',
    this.argsJson = '[]',
    this.handler = '',
  });

  final String api;
  final String argsJson;
  final String handler;
  bool get isHandler => handler.isNotEmpty;
}

class PluginViewNode {
  const PluginViewNode({
    required this.type,
    this.value = const PluginViewValue(''),
    this.itemsBind,
    this.bind,
    this.itemView = '',
    this.style = '',
    this.direction = 'column',
    this.kind = 'normal',
    this.placeholder = '',
    this.source = '',
    this.href = '',
    this.language = '',
    this.wrap = true,
    this.multiline = false,
    this.spacing = 0,
    this.height = 0,
    this.progress = 0,
    this.options = const [],
    this.columns = const [],
    this.action,
    this.children = const [],
  });

  final String type;
  final PluginViewValue value;
  final PluginViewBinding? itemsBind;
  final PluginViewBinding? bind;
  final String itemView;
  final String style;
  final String direction;
  final String kind;
  final String placeholder;
  final String source;
  final String href;
  final String language;
  final bool wrap;
  final bool multiline;
  final double spacing;
  final double height;
  final double progress;
  final List<String> options;
  final List<String> columns;
  final PluginViewAction? action;
  final List<PluginViewNode> children;
}

class PluginPanel {
  const PluginPanel({
    required this.id,
    required this.title,
    this.declarative = false,
    this.entry = '',
  });

  final String id;
  final String title;
  final bool declarative;
  final String entry;
}

class PluginViewData {
  const PluginViewData({
    this.node,
    this.errors = const [],
    this.bindings = const {},
  });

  final PluginViewNode? node;
  final List<String> errors;
  final Map<String, Object?> bindings;
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

  /// 插件的声明式面板（需要插件已启用；内部会确保运行时已加载）。
  Future<List<PluginPanel>> panels(String id);

  /// 读取并解析一个声明式面板视图，返回节点树 + storage 绑定快照。
  Future<PluginViewData> view(String id, String panelId);

  /// 执行视图动作（api + 位置参数数组），返回结果 JSON；失败抛异常。
  Future<String?> invoke(String id, String api, Object? args);
}
