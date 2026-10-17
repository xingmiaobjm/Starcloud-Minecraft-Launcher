/// 路径三根（规范 §11.1）：config（配置）/ data（持久数据）/ cache（可清理）。
///
/// 内核不猜路径——由宿主注入；本类是 Linux 端的注入实现（macOS 端将提供
/// `KernelPaths.macos()`）。规范：
/// - Linux：`$XDG_CONFIG_HOME`（缺 `~/.config`）、`$XDG_DATA_HOME`（缺
///   `~/.local/share`）、`$XDG_CACHE_HOME`（缺 `~/.cache`），下接产品目录；
/// - 环境变量可整体覆盖（`STARCLOUD_CONFIG_DIR` / `STARCLOUD_DATA_DIR` /
///   `STARCLOUD_CACHE_DIR`），与 Windows 参考实现的 `STARCLOUD_DATA_DIR` 对齐；
/// - 程序安装目录只读：一切写入走这三根（七条禁令 6：不出现绝对路径/反斜杠）。
library;

import 'dart:convert';
import 'dart:io' as io;

import 'package:path/path.dart' as p;

class KernelPaths {
  KernelPaths._({
    required this.configRoot,
    required this.dataRoot,
    required this.cacheRoot,
  });

  /// Linux / XDG 布局。`env` 便于测试注入；缺省读真实环境。
  factory KernelPaths.linux({
    String productName = 'starcloud-launcher',
    Map<String, String> env = const {},
  }) {
    String home = env['HOME'] ?? io.Platform.environment['HOME'] ?? '/';
    String resolve({
      required String overrideKey,
      required String xdgKey,
      required String defaultSegment,
    }) {
      final override = env[overrideKey] ?? io.Platform.environment[overrideKey];
      if (override != null && override.isNotEmpty) {
        return p.normalize(override);
      }
      final xdg = env[xdgKey] ?? io.Platform.environment[xdgKey];
      if (xdg != null && xdg.isNotEmpty) {
        return p.normalize(p.join(xdg, productName));
      }
      return p.normalize(p.join(home, defaultSegment, productName));
    }

    return KernelPaths._(
      configRoot: resolve(
        overrideKey: 'STARCLOUD_CONFIG_DIR',
        xdgKey: 'XDG_CONFIG_HOME',
        defaultSegment: '.config',
      ),
      dataRoot: resolve(
        overrideKey: 'STARCLOUD_DATA_DIR',
        xdgKey: 'XDG_DATA_HOME',
        defaultSegment: '.local/share',
      ),
      cacheRoot: resolve(
        overrideKey: 'STARCLOUD_CACHE_DIR',
        xdgKey: 'XDG_CACHE_HOME',
        defaultSegment: '.cache',
      ),
    );
  }

  final String configRoot;
  final String dataRoot;
  final String cacheRoot;

  // 规范 §11.1：插件配置 <config>/plugin-config/<id>.json；
  // 插件数据 <data>/plugin-data/<id>/；缓存 <cache>/plugin-cache/<id>/。
  String pluginConfigFile(String pluginId) =>
      p.join(configRoot, 'plugin-config', '$pluginId.json');

  String pluginDataDir(String pluginId) =>
      p.join(dataRoot, 'plugin-data', pluginId);

  String pluginCacheDir(String pluginId) =>
      p.join(cacheRoot, 'plugin-cache', pluginId);

  /// 保证目录存在（初始化时一次性调用；失败上抛——路径不可用应尽早暴露）。
  void ensureDirectories() {
    for (final dir in [
      p.join(configRoot, 'plugin-config'),
      dataRoot,
      cacheRoot,
    ]) {
      io.Directory(dir).createSync(recursive: true);
    }
  }
}

/// 插件配置分区（规范 §10）：键值 JSON，**写入原子**（临时文件 + rename），
/// **容忍损坏**（读失败按默认值，不崩溃），每个插件一个文件、互不可见。
///
/// **惰性读取**：构造不做任何 I/O（规范铁律 2：激活不得在 UI 线程上做 I/O）；
/// 首次读/写才落盘。插件若在 initialize 里读配置，建议经 `scheduler.spawnBackground`。
class PluginConfigSection {
  PluginConfigSection._(this._file);

  /// 只记路径；真正的读取延迟到第一次访问（见类注释）。
  factory PluginConfigSection.load(String file) => PluginConfigSection._(file);

  final String _file;
  Map<String, Object?>? _cache;

  Map<String, Object?> get _values => _cache ??= _read();

  /// 文件缺失或损坏 → 空表（规范 §10：读失败按默认值）。
  Map<String, Object?> _read() {
    try {
      final decoded = jsonDecode(io.File(_file).readAsStringSync());
      if (decoded is Map<String, Object?>) return decoded;
    } catch (_) {
      // 损坏/缺失：按默认值起步，下次 set 会整体覆写。
    }
    return <String, Object?>{};
  }

  Object? operator [](String key) => _values[key];

  T get<T>(String key, T fallback) {
    final value = _values[key];
    return value is T ? value : fallback;
  }

  bool contains(String key) => _values.containsKey(key);

  /// 写入并落盘（原子）。
  void set(String key, Object? value) {
    _values[key] = value;
    _flush();
  }

  /// 删除键并落盘（原子）。
  void remove(String key) {
    _values.remove(key);
    _flush();
  }

  /// 写入：先写同目录临时文件再 rename（同目录内 rename 原子，规范 §10）。
  void _flush() {
    final file = io.File(_file);
    file.parent.createSync(recursive: true);
    final tmp = '$_file.${DateTime.now().microsecondsSinceEpoch}.tmp';
    io.File(tmp).writeAsStringSync(
      const JsonEncoder.withIndent('  ').convert(_values),
      flush: true,
    );
    io.File(tmp).renameSync(_file);
  }

  /// 键值快照（诊断用；会触发一次惰性读取）。
  Map<String, Object?> get values => Map.unmodifiable(_values);
}
