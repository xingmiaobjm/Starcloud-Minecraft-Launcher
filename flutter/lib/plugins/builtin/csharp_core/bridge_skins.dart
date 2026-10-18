/// 桥接版皮肤管理：本地皮肤库与皮肤站衣柜都落到 C#。
library;

import '../../../bridge/core_bridge.dart';
import '../../../contracts/skins.dart';
import 'bridge_accounts.dart';

class BridgeSkinManager implements SkinManager {
  BridgeSkinManager(this._bridge, this._accounts);

  final CoreBridge _bridge;
  final BridgeAccountStore _accounts;

  @override
  Future<List<SkinEntry>> list() async {
    final result = await _bridge.request('skins.list');
    if (result is! Map<String, Object?>) return const [];
    final skins = result['skins'];
    if (skins is! List<Object?>) return const [];
    return [for (final item in skins) ?_skinFrom(item)];
  }

  @override
  Future<SkinEntry> importPath(String path, {String? name}) async {
    final result = await _bridge.requestWithTimeout(
      'skins.importPath',
      {'path': path, 'name': ?name},
      const Duration(seconds: 30),
    );
    return _requireSkin(result);
  }

  @override
  Future<SkinEntry> importUrl(String url, {String? name}) async {
    final result = await _bridge.requestWithTimeout(
      'skins.importUrl',
      {'url': url, 'name': ?name},
      const Duration(minutes: 2),
    );
    return _requireSkin(result);
  }

  @override
  Future<SkinEntry> rename(String id, String name) async {
    final result = await _bridge.request(
      'skins.rename',
      {'id': id, 'name': name},
    );
    return _requireSkin(result);
  }

  @override
  Future<SkinEntry> setModel(String id, String model) async {
    final result = await _bridge.request(
      'skins.setModel',
      {'id': id, 'model': model},
    );
    return _requireSkin(result);
  }

  @override
  Future<void> delete(String id) async {
    await _bridge.request('skins.delete', {'id': id});
  }

  @override
  Future<void> assignToAccount(String uuid, String? skinId, {String? model}) async {
    await _bridge.request('accounts.setSkin', {
      'uuid': uuid,
      'skinId': ?skinId,
      'model': ?model,
    });
    // 账户缓存里的 SkinId 需要同步，账户页/详情页刷新时才能看到新状态。
    await _accounts.refresh();
  }

  @override
  Future<List<StationSkin>> stationList(String accountUuid) async {
    final result = await _bridge.requestWithTimeout(
      'skins.station.list',
      {'uuid': accountUuid},
      const Duration(seconds: 60),
    );
    if (result is! Map<String, Object?>) return const [];
    final skins = result['skins'];
    if (skins is! List<Object?>) return const [];
    final out = <StationSkin>[];
    for (final item in skins) {
      if (item is! Map<String, Object?>) continue;
      final id = item['id'] as String?;
      if (id == null || id.isEmpty) continue;
      out.add(StationSkin(
        id: id,
        name: item['name'] as String? ?? id,
        model: item['model'] as String? ?? 'steve',
        url: item['url'] as String?,
        slim: item['slim'] == true,
      ));
    }
    return out;
  }

  @override
  Future<String> stationApply(String accountUuid, String skinId) async {
    final result = await _bridge.requestWithTimeout(
      'skins.station.apply',
      {'uuid': accountUuid, 'skinId': skinId},
      const Duration(minutes: 2),
    );
    if (result is Map<String, Object?>) {
      return result['message'] as String? ?? '已应用';
    }
    return '已应用';
  }

  static SkinEntry _requireSkin(Object? result) {
    final skin = _skinFrom(result is Map<String, Object?> ? result['skin'] : null);
    if (skin == null) throw StateError('桥接未返回皮肤信息');
    return skin;
  }

  static SkinEntry? _skinFrom(Object? raw) {
    if (raw is! Map<String, Object?>) return null;
    final id = raw['id'] as String?;
    final path = raw['path'] as String?;
    if (id == null || id.isEmpty || path == null || path.isEmpty) return null;
    return SkinEntry(
      id: id,
      name: raw['name'] as String? ?? id,
      model: raw['model'] as String? ?? SkinModels.auto,
      fileName: raw['fileName'] as String? ?? '',
      path: path,
      source: raw['source'] as String? ?? 'file',
      sourceUrl: raw['sourceUrl'] as String?,
      width: (raw['width'] as num?)?.toInt() ?? 0,
      height: (raw['height'] as num?)?.toInt() ?? 0,
      addedAt: raw['addedAt'] as String?,
    );
  }
}
