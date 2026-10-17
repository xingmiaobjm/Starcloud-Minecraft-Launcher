/// 插件描述符（规范 §3）与版本范围（§5.6 `depends`/`conflicts` 的语法）。
///
/// 本文件零依赖（不 import Flutter），可在纯 Dart 测试里跑。
library;

/// 插件种类（规范 §3.1）。枚举顺序 = 激活次序（§4.3）：engine → feature → assistant → ui。
enum PluginKind { engine, feature, assistant, ui }

/// 激活方式（规范 §4.4）：eager 随内核启动；lazy 在首次按契约取用时激活。
enum PluginActivation { eager, lazy }

/// 生命周期状态（规范 §4.1 状态机）。
enum PluginState {
  registered,
  activating,
  active,
  stopping,
  stopped,
  failed,
  disabled,
}

/// 描述符校验失败（规范 §3 规则：重复 id / 非小写 / 主版本不符 / 非法字符 …）。
class DescriptorException implements Exception {
  DescriptorException(this.message);
  final String message;

  @override
  String toString() => 'DescriptorException: $message';
}

/// 语义化版本的最小形态：`major.minor.patch`（可带 `-预发布` 后缀，比较时忽略）。
class SemVer implements Comparable<SemVer> {
  const SemVer(this.major, this.minor, this.patch);

  factory SemVer.parse(String text) {
    final core = text.split('-').first.trim();
    final parts = core.split('.');
    if (parts.length != 3) {
      throw DescriptorException('版本号必须是 major.minor.patch：" $text"');
    }
    final numbers = parts.map((p) => int.tryParse(p)).toList();
    if (numbers.any((n) => n == null || n < 0)) {
      throw DescriptorException('版本号段必须是非负整数："$text"');
    }
    return SemVer(numbers[0]!, numbers[1]!, numbers[2]!);
  }

  final int major;
  final int minor;
  final int patch;

  @override
  int compareTo(SemVer other) {
    if (major != other.major) return major.compareTo(other.major);
    if (minor != other.minor) return minor.compareTo(other.minor);
    return patch.compareTo(other.patch);
  }

  @override
  bool operator ==(Object other) => other is SemVer && compareTo(other) == 0;

  @override
  int get hashCode => Object.hash(major, minor, patch);

  @override
  String toString() => '$major.$minor.$patch';
}

/// 取版本号的主版本（apiVersion 允许 `3` / `3.0` / `3.0.0` 三种写法，
/// 规范 §3 的示例就是 `"apiVersion": "3.0"`）。
int majorOf(String text) {
  final match = RegExp(r'^(\d+)(\.\d+){0,2}$').firstMatch(text.trim());
  if (match == null) {
    throw DescriptorException('版本号非法："$text"（允许 3 / 3.0 / 3.0.0）');
  }
  return int.parse(match.group(1)!);
}

/// npm 风格版本范围（规范 §3：`^1.0.0`、`>=2 <3`、`*`）。
///
/// 支持的记法（空格分隔 = AND）：
///   *                    任意
///   ^1.2.3               >=1.2.3 <2.0.0（0 主版本时锁次版本：>=0.2.3 <0.3.0）
///   >=1.0.0  >  <=  <    比较符
///   1.2.3 / =1.2.3       精确
class VersionRange {
  VersionRange._(this.text, this._atoms);

  factory VersionRange.parse(String text) {
    final trimmed = text.trim();
    if (trimmed.isEmpty) {
      throw DescriptorException('版本范围不能为空');
    }
    if (trimmed == '*') {
      return VersionRange._(trimmed, const []);
    }
    final atoms = <bool Function(SemVer)>[];
    for (final token in trimmed.split(RegExp(r'\s+'))) {
      atoms.add(_parseAtom(token));
    }
    return VersionRange._(trimmed, atoms);
  }

  static bool Function(SemVer) _parseAtom(String token) {
    if (token.startsWith('^')) {
      final base = SemVer.parse(token.substring(1));
      final upper = base.major == 0
          ? SemVer(0, base.minor + 1, 0) // 0.x：锁次版本（npm 语义）
          : SemVer(base.major + 1, 0, 0);
      return (v) => v.compareTo(base) >= 0 && v.compareTo(upper) < 0;
    }
    final match = RegExp(r'^(>=|<=|>|<|=)?(.+)$').firstMatch(token);
    if (match == null) {
      throw DescriptorException('无法解析版本约束："$token"');
    }
    final op = match.group(1) ?? '=';
    final bound = SemVer.parse(match.group(2)!);
    int cmp(SemVer v) => v.compareTo(bound);
    switch (op) {
      case '>=':
        return (v) => cmp(v) >= 0;
      case '<=':
        return (v) => cmp(v) <= 0;
      case '>':
        return (v) => cmp(v) > 0;
      case '<':
        return (v) => cmp(v) < 0;
      default:
        return (v) => cmp(v) == 0;
    }
  }

  final String text;
  final List<bool Function(SemVer)> _atoms;

  bool satisfies(SemVer version) => _atoms.every((a) => a(version));

  @override
  String toString() => text;
}

/// 插件描述符（规范 §3）。字段同名同义；用 [PluginDescriptor.create] 构造以强制校验。
class PluginDescriptor {
  PluginDescriptor._({
    required this.id,
    required this.name,
    required this.version,
    required this.apiVersion,
    required this.kind,
    required this.builtin,
    required this.activation,
    required this.provides,
    required this.requires,
    required this.depends,
    required this.conflicts,
    required this.permissions,
    required this.optionalPermissions,
    required this.essential,
    required this.description,
    required this.author,
  });

  /// 构造并校验（规范 §3 规则 + §13 验收项）：
  /// - id 只允许 `[a-z0-9._-]`，长度 3..64（跨平台大小写一致 → 必须小写）；
  /// - version 必须可解析；apiVersion 必须可解析（主版本匹配在内核登记时校验）；
  /// - depends/conflicts 的键必须是小写插件 id、值必须可解析为版本范围。
  factory PluginDescriptor.create({
    required String id,
    required String name,
    required String version,
    required String apiVersion,
    PluginKind kind = PluginKind.feature,
    bool builtin = true,
    PluginActivation activation = PluginActivation.eager,
    List<Type> provides = const [],
    List<Type> requires = const [],
    Map<String, String> depends = const {},
    Map<String, String> conflicts = const {},
    List<String> permissions = const [],
    List<String> optionalPermissions = const [],
    bool essential = false,
    String description = '',
    String author = '',
  }) {
    if (!RegExp(r'^[a-z0-9._-]{3,64}$').hasMatch(id)) {
      throw DescriptorException(
        '插件 id 非法："$id"（只允许 [a-z0-9._-]，长度 3..64，必须小写）',
      );
    }
    if (id.contains(r'\') || id.startsWith('/') || id.contains('://')) {
      throw DescriptorException('插件 id 不允许携带路径成分："$id"');
    }
    if (name.trim().isEmpty) {
      throw DescriptorException('插件 $id 缺少展示名');
    }
    final parsedVersion = SemVer.parse(version);
    majorOf(apiVersion); // 只验形；主版本匹配在内核登记时做（规范 §5.5）
    final parsedDepends = <String, VersionRange>{};
    for (final entry in depends.entries) {
      if (!RegExp(r'^[a-z0-9._-]{3,64}$').hasMatch(entry.key)) {
        throw DescriptorException('插件 $id 的 depends 键非法："${entry.key}"');
      }
      parsedDepends[entry.key] = VersionRange.parse(entry.value);
    }
    final parsedConflicts = <String, VersionRange>{};
    for (final entry in conflicts.entries) {
      if (!RegExp(r'^[a-z0-9._-]{3,64}$').hasMatch(entry.key)) {
        throw DescriptorException('插件 $id 的 conflicts 键非法："${entry.key}"');
      }
      parsedConflicts[entry.key] = VersionRange.parse(entry.value);
    }
    return PluginDescriptor._(
      id: id,
      name: name,
      version: parsedVersion,
      apiVersion: apiVersion,
      kind: kind,
      builtin: builtin,
      activation: activation,
      provides: List.unmodifiable(provides),
      requires: List.unmodifiable(requires),
      depends: Map.unmodifiable(parsedDepends),
      conflicts: Map.unmodifiable(parsedConflicts),
      permissions: List.unmodifiable(permissions),
      optionalPermissions: List.unmodifiable(optionalPermissions),
      essential: essential,
      description: description,
      author: author,
    );
  }

  final String id;
  final String name;
  final SemVer version;
  final String apiVersion;
  final PluginKind kind;
  final bool builtin;
  final PluginActivation activation;

  /// 本插件对外提供的契约（规范 §3 `provides`；内置插件用 Dart `Type`，
  /// 外部插件的契约名字符串将来由宿主做"名字 → Type"映射）。
  final List<Type> provides;

  /// 本插件消费的契约（规范 §5.6.1：只要代码 Get 了就必须声明）。
  final List<Type> requires;

  /// 依赖的具体插件 id → 版本范围（规范 §5.6.1：仅"必须是这个插件"时使用）。
  final Map<String, VersionRange> depends;

  /// 冲突声明（规范 §4.2 第 4 步：命中则不激活）。
  final Map<String, VersionRange> conflicts;

  /// 权限（规范 §9；内置插件也必须登记，用于审计）。
  final List<String> permissions;
  final List<String> optionalPermissions;

  /// 内核必需插件：不允许用户禁用（规范 §5.6.3）。
  final bool essential;

  final String description;
  final String author;

  /// 提供某契约？（"懒激活时谁能提供"的查询，规范 §4.4）
  bool providesContract(Type contract) => provides.contains(contract);
}
