/// 桥接版存档库：扫描/备份/删除全部由 C# 的 SaveService / SaveLibraryService 完成。
///
/// 根目录与目录名的合法性由宿主校验（必须位于游戏目录内、不得含路径符号），
/// Dart 侧只负责传回列表里拿到的 `gameRoot + folderName`。
library;

import '../../../bridge/core_bridge.dart';
import '../../../contracts/minecraft.dart';

class BridgeSaveCatalog implements SaveCatalog {
  BridgeSaveCatalog(this._bridge);

  final CoreBridge _bridge;

  @override
  Future<List<SaveInfo>> listSaves() async {
    final result = await _bridge.request('saves.list');
    if (result is! Map<String, Object?>) return const [];
    final saves = result['saves'];
    if (saves is! List<Object?>) return const [];
    final out = <SaveInfo>[];
    for (final item in saves) {
      if (item is! Map<String, Object?>) continue;
      final folder = item['folderName'] as String?;
      final root = item['gameRoot'] as String?;
      if (folder == null || folder.isEmpty || root == null || root.isEmpty) {
        continue;
      }
      out.add(SaveInfo(
        folderName: folder,
        levelName: item['levelName'] as String? ?? folder,
        gameRoot: root,
        versionId: item['versionId'] as String? ?? '',
        isolated: item['isolated'] == true,
        sizeBytes: (item['sizeBytes'] as num?)?.toInt() ?? 0,
        iconPath: item['iconPath'] as String?,
        lastPlayedAt: item['lastPlayed'] as String?,
      ));
    }
    return out;
  }

  Map<String, Object?> _target(SaveInfo save) => {
        'gameRoot': save.gameRoot,
        'folderName': save.folderName,
      };

  @override
  Future<void> openSaveFolder(SaveInfo save) async {
    await _bridge.request('saves.openFolder', _target(save));
  }

  @override
  Future<String> backupSave(SaveInfo save) async {
    final result = await _bridge.requestWithTimeout(
      'saves.backup',
      _target(save),
      const Duration(minutes: 5),
    );
    if (result is Map<String, Object?> && result['path'] is String) {
      return result['path'] as String;
    }
    throw StateError('桥接未返回备份路径');
  }

  @override
  Future<void> deleteSave(SaveInfo save) async {
    await _bridge.request('saves.delete', _target(save));
  }
}
