/// 桥接版实验室：投影解析与世吞模板生成都落到 C# 的 Lab 服务。
library;

import '../../../bridge/core_bridge.dart';
import '../../../contracts/lab.dart';

class BridgeLab implements LabService {
  BridgeLab(this._bridge);

  final CoreBridge _bridge;

  @override
  Future<ProjectionPreview> load(String path) async {
    final result = await _bridge.requestWithTimeout(
      'lab.load',
      {'path': path},
      const Duration(minutes: 2),
    );
    if (result is! Map<String, Object?>) {
      return ProjectionPreview(path: path, unsupported: '桥接未返回数据');
    }
    final stats = <BlockStat>[];
    final statsRaw = result['stats'];
    if (statsRaw is List<Object?>) {
      for (final item in statsRaw) {
        if (item is! Map<String, Object?>) continue;
        final id = item['id'] as String?;
        if (id == null || id.isEmpty) continue;
        stats.add(BlockStat(
          id: id,
          count: (item['count'] as num?)?.toInt() ?? 0,
        ));
      }
    }
    final preview = <VoxelPreviewBlock>[];
    final previewRaw = result['preview'];
    if (previewRaw is List<Object?>) {
      for (final item in previewRaw) {
        if (item is! Map<String, Object?>) continue;
        preview.add(VoxelPreviewBlock(
          x: (item['x'] as num?)?.toInt() ?? 0,
          y: (item['y'] as num?)?.toInt() ?? 0,
          z: (item['z'] as num?)?.toInt() ?? 0,
          id: item['id'] as String? ?? 'minecraft:stone',
        ));
      }
    }
    return ProjectionPreview(
      path: result['path'] as String? ?? path,
      sizeX: (result['sizeX'] as num?)?.toInt() ?? 0,
      sizeY: (result['sizeY'] as num?)?.toInt() ?? 0,
      sizeZ: (result['sizeZ'] as num?)?.toInt() ?? 0,
      truncated: result['truncated'] == true,
      totalBlocks: (result['totalBlocks'] as num?)?.toInt() ?? 0,
      distinctBlocks: (result['distinctBlocks'] as num?)?.toInt() ?? 0,
      stats: stats,
      preview: preview,
      previewTruncated: result['previewTruncated'] == true,
      unsupported: result['unsupported'] as String?,
    );
  }

  @override
  Future<String> generateWorldEater({
    int length = 64,
    int width = 16,
    int layers = 3,
    int spacing = 2,
    int layerGap = 1,
    int offset = 2,
  }) async {
    final result = await _bridge.requestWithTimeout(
      'lab.worldEater',
      {
        'length': length,
        'width': width,
        'layers': layers,
        'spacing': spacing,
        'layerGap': layerGap,
        'offset': offset,
      },
      const Duration(minutes: 2),
    );
    if (result is Map<String, Object?> && result['path'] is String) {
      return result['path'] as String;
    }
    throw StateError('桥接未返回生成文件路径');
  }
}
