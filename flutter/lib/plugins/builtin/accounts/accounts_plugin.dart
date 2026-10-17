/// 内置插件：离线账户（微软设备码登录后续迭代，届时提供第二种 AccountStore
/// 实现并按"同契约后注册者胜"升级，离线账户保留为兜底）。
library;

import 'dart:convert';
import 'dart:io' as io;

import 'package:path/path.dart' as p;

import '../../../contracts/minecraft.dart';
import '../../../kernel/descriptor.dart';
import '../../../kernel/plugin_context.dart';
import 'offline_uuid.dart';

class OfflineAccountStore implements AccountStore {
  /// 构造时读一次文件，之后内存为准、set 后原子落盘。
  /// （曾经 `_state` 是每次重读文件的 getter —— addOffline 的修改落在
  /// 一次性临时副本上，_flush 又重读旧文件，等于什么都没存。）
  OfflineAccountStore(this._file) : _state = _read(_file);

  final String _file;
  final Map<String, Object?> _state;

  static Map<String, Object?> _read(String file) {
    try {
      final decoded =
          jsonDecode(io.File(file).readAsStringSync()) as Map<String, Object?>;
      return decoded;
    } catch (_) {
      return <String, Object?>{'accounts': <Object?>[], 'current': null};
    }
  }

  List<Map<String, Object?>> get _list =>
      (_state['accounts'] as List<Object?>? ?? const [])
          .whereType<Map<String, Object?>>()
          .toList();

  void _flush() {
    final file = io.File(_file);
    file.parent.createSync(recursive: true);
    // 临时文件名必须唯一：固定 .tmp 在并发写（或测试并行跑）时会被
    // 另一次写入先 rename 掉，导致这里的 rename 找不到源文件。
    final tmp = '$_file.${io.pid}.${DateTime.now().microsecondsSinceEpoch}.tmp';
    io.File(tmp)
        .writeAsStringSync(const JsonEncoder.withIndent('  ').convert(_state));
    io.File(tmp).renameSync(_file);
  }

  @override
  List<GameAccount> accounts() => _list
      .map((a) => GameAccount(
            uuid: a['uuid'] as String? ?? '',
            name: a['name'] as String? ?? '',
            kind: AccountKind.offline,
          ))
      .where((a) => a.uuid.isNotEmpty)
      .toList();

  @override
  GameAccount? current() {
    final uuid = _state['current'] as String?;
    if (uuid == null) return null;
    for (final a in accounts()) {
      if (a.uuid == uuid) return a;
    }
    return null;
  }

  @override
  Future<void> addOffline(String name) async {
    final trimmed = name.trim();
    if (trimmed.isEmpty) {
      throw ArgumentError('玩家名不能为空');
    }
    if (trimmed.length > 16) {
      throw ArgumentError('离线玩家名最长 16 字符');
    }
    final uuid = offlineUuid(trimmed);
    final state = _state;
    final list = (state['accounts'] as List<Object?>? ?? const []).toList();
    if (!list.any((a) => a is Map && a['uuid'] == uuid)) {
      list.add({'uuid': uuid, 'name': trimmed, 'kind': 'offline'});
      state['accounts'] = list;
    }
    state['current'] = uuid; // 新增即选中（最常见操作）
    _flush();
  }

  @override
  Future<void> setCurrent(String uuid) async {
    final state = _state;
    if (!accounts().any((a) => a.uuid == uuid)) {
      throw StateError('账户不存在：$uuid');
    }
    state['current'] = uuid;
    _flush();
  }

  @override
  Future<void> remove(String uuid) async {
    final state = _state;
    state['accounts'] = _list.where((a) => a['uuid'] != uuid).toList();
    if (state['current'] == uuid) state['current'] = null;
    _flush();
  }
}

class AccountsPlugin implements Plugin {
  @override
  Future<void> initialize(PluginContext ctx) async {
    // 账户数据在本插件的数据目录（规范 §10：按插件隔离）。
    // 注意不能落在内核数据根的 accounts.json：C# 核心的 AccountStore 也用
    // 那个文件，两边 schema 不同，混用会互相覆盖。
    final storeFile = p.join(ctx.paths.dataDir, 'accounts.json');
    ctx.services.add<AccountStore>(OfflineAccountStore(storeFile));
    ctx.log('离线账户存储就绪（$storeFile）');
  }

  @override
  Future<void> shutdown() async {}
}

PluginDescriptor accountsDescriptor() => PluginDescriptor.create(
      id: 'starcloud.accounts',
      name: '游戏账户',
      version: '1.0.0',
      apiVersion: '3.0',
      kind: PluginKind.feature,
      provides: const [AccountStore],
      description: '离线账户（微软登录后续迭代）',
      author: 'Starcloud',
    );
