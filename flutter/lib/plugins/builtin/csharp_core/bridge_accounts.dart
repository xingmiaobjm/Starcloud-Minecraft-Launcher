/// 桥接版账户存储与登录。
///
/// 账户身份（离线 / 微软 / 第三方）与内存会话都由 C# 的 AccountStore 管理；
/// token 只在桥接进程内存里，不落盘。Dart 侧缓存一份列表供同步 getter 使用，
/// 每次增删/登录后刷新。
library;

import '../../../bridge/core_bridge.dart';
import '../../../contracts/minecraft.dart';

class BridgeAccountStore implements AccountStore {
  BridgeAccountStore(this._bridge);

  final CoreBridge _bridge;
  List<GameAccount> _accounts = const [];
  String? _current;

  Future<void> refresh() async {
    final result = await _bridge.request('accounts.list');
    if (result is! Map<String, Object?>) return;
    final list = result['accounts'];
    if (list is! List<Object?>) return;
    final accounts = <GameAccount>[];
    for (final item in list) {
      if (item is! Map<String, Object?>) continue;
      final uuid = item['uuid'] as String?;
      final name = item['name'] as String?;
      if (uuid == null || uuid.isEmpty || name == null) continue;
      accounts.add(GameAccount(
        uuid: uuid,
        name: name,
        kind: _kind(item['type'] as String?),
        serverName: item['serverName'] as String?,
        serverAlias: item['serverAlias'] as String?,
        hasSession: item['hasSession'] == true,
      ));
    }
    _accounts = accounts;
    _current = result['current'] as String?;
  }

  static AccountKind _kind(String? type) => switch (type) {
        'microsoft' => AccountKind.microsoft,
        'external' => AccountKind.external,
        _ => AccountKind.offline,
      };

  @override
  List<GameAccount> accounts() => List.unmodifiable(_accounts);

  @override
  GameAccount? current() {
    final uuid = _current;
    if (uuid != null) {
      for (final account in _accounts) {
        if (account.uuid == uuid) return account;
      }
    }
    return _accounts.isEmpty ? null : _accounts.first;
  }

  @override
  Future<void> addOffline(String name) async {
    await _bridge.request('accounts.addOffline', {'name': name});
    await refresh();
  }

  @override
  Future<void> setCurrent(String uuid) async {
    await _bridge.request('accounts.setCurrent', {'uuid': uuid});
    await refresh();
  }

  @override
  Future<void> remove(String uuid) async {
    await _bridge.request('accounts.remove', {'uuid': uuid});
    await refresh();
  }
}

class BridgeAccountAuth implements AccountAuth {
  BridgeAccountAuth(this._bridge, this._store);

  final CoreBridge _bridge;
  final BridgeAccountStore _store;

  @override
  Future<MicrosoftDeviceCode> startMicrosoftLogin() async {
    final result = await _bridge.requestWithTimeout(
      'auth.microsoft.start',
      const {},
      const Duration(seconds: 60),
    );
    if (result is! Map<String, Object?>) {
      throw StateError('桥接未返回设备码');
    }
    return MicrosoftDeviceCode(
      userCode: result['userCode'] as String? ?? '',
      deviceCode: result['deviceCode'] as String? ?? '',
      verificationUri: result['verificationUri'] as String? ?? '',
      interval: (result['interval'] as num?)?.toInt() ?? 5,
    );
  }

  @override
  Future<bool> pollMicrosoftLogin(MicrosoftDeviceCode code) async {
    final result = await _bridge.requestWithTimeout(
      'auth.microsoft.poll',
      {'deviceCode': code.deviceCode},
      const Duration(seconds: 120),
    );
    if (result is Map<String, Object?> && result['state'] == 'success') {
      await _store.refresh();
      return true;
    }
    return false;
  }

  @override
  Future<void> loginExternal({
    required String url,
    required String username,
    required String password,
  }) async {
    await _bridge.requestWithTimeout(
      'auth.yggdrasil.login',
      {'url': url, 'username': username, 'password': password},
      const Duration(seconds: 90),
    );
    await _store.refresh();
  }
}
