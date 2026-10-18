/// 实验室契约：litematic / 结构 NBT 投影解析与 3D 预览数据。
library;

class VoxelPreviewBlock {
  const VoxelPreviewBlock({
    required this.x,
    required this.y,
    required this.z,
    required this.id,
  });

  final int x;
  final int y;
  final int z;

  /// 去掉 blockstate 属性后的方块 id（如 minecraft:stone）。
  final String id;
}

class BlockStat {
  const BlockStat({required this.id, required this.count});
  final String id;
  final int count;
}

class ProjectionPreview {
  const ProjectionPreview({
    required this.path,
    this.sizeX = 0,
    this.sizeY = 0,
    this.sizeZ = 0,
    this.truncated = false,
    this.totalBlocks = 0,
    this.distinctBlocks = 0,
    this.stats = const [],
    this.preview = const [],
    this.previewTruncated = false,
    this.unsupported,
  });

  final String path;
  final int sizeX;
  final int sizeY;
  final int sizeZ;

  /// 投影体量超过解析上限，统计与预览基于降采样结果。
  final bool truncated;
  final int totalBlocks;
  final int distinctBlocks;
  final List<BlockStat> stats;

  /// 用于 3D 预览的抽样方块（最多 8000）。
  final List<VoxelPreviewBlock> preview;
  final bool previewTruncated;

  /// 非空 = 该格式暂不支持。
  final String? unsupported;
}

abstract class LabService {
  /// 解析 .litematic / 结构 .nbt；格式不支持时返回 `unsupported` 而非抛错。
  Future<ProjectionPreview> load(String path);

  /// 生成世吞爆破阵列模板（.litematic），返回文件路径。
  Future<String> generateWorldEater({
    int length = 64,
    int width = 16,
    int layers = 3,
    int spacing = 2,
    int layerGap = 1,
    int offset = 2,
  });
}
