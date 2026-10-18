/// 实验室：litematic / 结构 NBT 投影解析 + 方块统计 + 等距 3D 预览。
///
/// 解析与统计在 C#（VoxelExporter）；这里用 CustomPainter 画一个不依赖纹理的
/// 等距体素预览（按方块 id 着色）。Windows 端的 deepslate 纹理渲染不在本实现内。
library;

import 'dart:math' as math;

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/lab.dart';

class LabPage extends ConsumerStatefulWidget {
  const LabPage({super.key});

  @override
  ConsumerState<LabPage> createState() => _LabPageState();
}

class _LabPageState extends ConsumerState<LabPage> {
  final _path = TextEditingController();
  ProjectionPreview? _preview;
  bool _loading = false;
  String? _error;
  String? _notice;

  LabService get _lab => ref.read(kernelDepsProvider).lab;

  @override
  void dispose() {
    _path.dispose();
    super.dispose();
  }

  Future<void> _load([String? path]) async {
    final target = (path ?? _path.text).trim();
    if (target.isEmpty) return;
    setState(() {
      _loading = true;
      _error = null;
      _notice = null;
    });
    try {
      final preview = await _lab.load(target);
      if (!mounted) return;
      setState(() {
        _preview = preview;
        _path.text = preview.path;
        _loading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _error = '$e';
      });
    }
  }

  Future<void> _generateSample() async {
    setState(() {
      _loading = true;
      _error = null;
      _notice = null;
    });
    try {
      final path = await _lab.generateWorldEater();
      if (!mounted) return;
      setState(() => _notice = '已生成世吞模板：$path');
      await _load(path);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _error = '生成失败：$e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    final preview = _preview;
    return ScaffoldPage(
      header: PageHeader(
        title: const Text('实验室'),
        commandBar: CommandBar(
          mainAxisAlignment: MainAxisAlignment.end,
          primaryItems: [
            CommandBarButton(
              icon: const Icon(FluentIcons.lightning_bolt),
              label: const Text('生成世吞模板'),
              onPressed: _loading ? null : _generateSample,
            ),
          ],
        ),
      ),
      content: Padding(
        padding: const EdgeInsets.fromLTRB(24, 0, 24, 16),
        child: ListView(children: [
          Card(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.stretch,
              children: [
                Text('投影文件', style: typography.subtitle),
                const SizedBox(height: 8),
                Row(children: [
                  Expanded(
                    child: TextBox(
                      controller: _path,
                      placeholder: 'litematic / 结构 .nbt 的绝对路径',
                      onSubmitted: (_) => _load(),
                    ),
                  ),
                  const SizedBox(width: 8),
                  FilledButton(
                    onPressed: _loading ? null : () => _load(),
                    child: const Text('加载'),
                  ),
                  const SizedBox(width: 8),
                  Button(
                    onPressed: _loading ? null : _generateSample,
                    child: const Text('生成示例'),
                  ),
                ]),
                const SizedBox(height: 6),
                Text(
                  '支持 .litematic 与结构 .nbt；超过 20 万方块会降采样并标记。'
                  '3D 预览按方块 id 着色，不使用纹理。',
                  style: typography.caption,
                ),
              ],
            ),
          ),
          if (_error != null) ...[
            const SizedBox(height: 12),
            InfoBar(
              title: const Text('出错了'),
              content: Text(_error!),
              severity: InfoBarSeverity.error,
              isLong: true,
              onClose: () => setState(() => _error = null),
            ),
          ],
          if (_notice != null) ...[
            const SizedBox(height: 12),
            InfoBar(
              title: Text(_notice!),
              severity: InfoBarSeverity.success,
              isLong: true,
              onClose: () => setState(() => _notice = null),
            ),
          ],
          if (_loading) ...[
            const SizedBox(height: 24),
            const Center(child: ProgressRing()),
          ],
          if (preview != null && !_loading) ...[
            const SizedBox(height: 12),
            if (preview.unsupported != null)
              InfoBar(
                title: const Text('暂不支持该格式'),
                content: Text(preview.unsupported!),
                severity: InfoBarSeverity.warning,
                isLong: true,
              )
            else ...[
              Card(
                padding: const EdgeInsets.all(16),
                child: Wrap(
                  spacing: 24,
                  runSpacing: 8,
                  children: [
                    _info(typography, '尺寸',
                        '${preview.sizeX} × ${preview.sizeY} × ${preview.sizeZ}'),
                    _info(typography, '非空气方块',
                        '${preview.totalBlocks}'),
                    _info(typography, '方块种类', '${preview.distinctBlocks}'),
                    _info(typography, '预览采样',
                        '${preview.preview.length}'
                            '${preview.previewTruncated ? '（已抽样）' : ''}'),
                    if (preview.truncated)
                      _info(typography, '注意', '体量超限，统计基于降采样'),
                  ],
                ),
              ),
              const SizedBox(height: 12),
              Card(
                padding: const EdgeInsets.all(8),
                child: SizedBox(
                  height: 440,
                  child: ClipRect(
                    child: CustomPaint(
                      painter: _VoxelPainter(
                        preview.preview,
                        preview.sizeX,
                        preview.sizeY,
                        preview.sizeZ,
                      ),
                      child: const SizedBox.expand(),
                    ),
                  ),
                ),
              ),
              const SizedBox(height: 12),
              Card(
                padding: const EdgeInsets.all(16),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text('方块统计（前 ${preview.stats.length}）',
                        style: typography.subtitle),
                    const SizedBox(height: 8),
                    for (final stat in preview.stats)
                      Padding(
                        padding: const EdgeInsets.only(bottom: 4),
                        child: Row(children: [
                          Container(
                            width: 10,
                            height: 10,
                            margin: const EdgeInsets.only(right: 8),
                            color: _VoxelPainter.colorFor(stat.id),
                          ),
                          Expanded(
                            child: Text(stat.id, style: typography.body),
                          ),
                          Text('${stat.count}', style: typography.caption),
                        ]),
                      ),
                  ],
                ),
              ),
            ],
          ],
          if (preview == null && !_loading) ...[
            const SizedBox(height: 24),
            Center(
              child: Text(
                '加载一个投影文件，或点「生成示例」创建一个世吞模板。',
                style: typography.body,
              ),
            ),
          ],
        ]),
      ),
    );
  }

  Widget _info(Typography typography, String label, String value) {
    return SizedBox(
      width: 220,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label, style: typography.caption),
          Text(value, style: typography.body),
        ],
      ),
    );
  }
}

/// 等距体素预览：每个方块画顶面 + 右侧面，按深度排序。
class _VoxelPainter extends CustomPainter {
  _VoxelPainter(this.blocks, this.sizeX, this.sizeY, this.sizeZ);

  final List<VoxelPreviewBlock> blocks;
  final int sizeX;
  final int sizeY;
  final int sizeZ;

  static const _cos30 = 0.8660254037844386;
  static const _sin30 = 0.5;

  @override
  void paint(Canvas canvas, Size size) {
    if (blocks.isEmpty || size.width <= 0 || size.height <= 0) return;

    double px(int x, int z) => (x - z) * _cos30;
    double py(int x, int y, int z) => (x + z) * _sin30 - y;

    var minX = double.infinity, maxX = -double.infinity;
    var minY = double.infinity, maxY = -double.infinity;
    for (final block in blocks) {
      final x = px(block.x, block.z);
      final y = py(block.x, block.y, block.z);
      if (x < minX) minX = x;
      if (x > maxX) maxX = x;
      if (y < minY) minY = y;
      if (y > maxY) maxY = y;
    }
    final spanX = math.max(maxX - minX, 1.0);
    final spanY = math.max(maxY - minY, 1.0);
    final scale = math.min(size.width / spanX, size.height / spanY) * 0.86;
    final offsetX = size.width / 2 - (minX + maxX) / 2 * scale;
    final offsetY = size.height / 2 - (minY + maxY) / 2 * scale;

    // 远 → 近：x + z + y 越大越靠近观察者。
    final sorted = [...blocks]
      ..sort((a, b) =>
          (a.x + a.z + a.y).compareTo(b.x + b.z + b.y));

    final radius = math.max(scale * 0.5, 0.6);
    final sidePaints = <int, Paint>{};
    final topPaints = <int, Paint>{};

    for (final block in sorted) {
      final cx = offsetX + px(block.x, block.z) * scale;
      final cy = offsetY + py(block.x, block.y, block.z) * scale;
      final color = colorFor(block.id);
      final topPaint = topPaints.putIfAbsent(
        color.toARGB32(),
        () => Paint()..color = color,
      );
      final sidePaint = sidePaints.putIfAbsent(
        color.toARGB32(),
        () => Paint()
          ..color = Color.lerp(color, const Color(0xFF000000), 0.28)!,
      );

      final top = Path()
        ..moveTo(cx, cy - radius)
        ..lineTo(cx + radius * _cos30, cy - radius * _sin30)
        ..lineTo(cx, cy)
        ..lineTo(cx - radius * _cos30, cy - radius * _sin30)
        ..close();
      canvas.drawPath(top, topPaint);

      final side = Path()
        ..moveTo(cx + radius * _cos30, cy - radius * _sin30)
        ..lineTo(cx + radius * _cos30, cy + radius * _sin30)
        ..lineTo(cx, cy + radius)
        ..lineTo(cx, cy)
        ..close();
      canvas.drawPath(side, sidePaint);
    }
  }

  /// 稳定颜色：常见方块给固定色，其余按 id 哈希到色相。
  static Color colorFor(String id) {
    const known = {
      'minecraft:stone': Color(0xFF8A8A8A),
      'minecraft:cobblestone': Color(0xFF6E6E6E),
      'minecraft:dirt': Color(0xFF8B5A2B),
      'minecraft:grass_block': Color(0xFF5C9E3A),
      'minecraft:oak_planks': Color(0xFFB08A4F),
      'minecraft:sand': Color(0xFFE0D6A8),
      'minecraft:water': Color(0xFF3A6FD8),
      'minecraft:lava': Color(0xFFE25822),
      'minecraft:tnt': Color(0xFFD8352A),
      'minecraft:glass': Color(0xFFBDE3F0),
      'minecraft:bedrock': Color(0xFF3B3B3B),
      'minecraft:deepslate': Color(0xFF4A4A4A),
    };
    final fixed = known[id];
    if (fixed != null) return fixed;
    var hash = 2166136261;
    for (final unit in id.codeUnits) {
      hash ^= unit;
      hash = (hash * 16777619) & 0xFFFFFFFF;
    }
    return HSLColor.fromAHSL(1, (hash % 360).toDouble(), 0.45, 0.58).toColor();
  }

  @override
  bool shouldRepaint(_VoxelPainter oldDelegate) =>
      oldDelegate.blocks != blocks ||
      oldDelegate.sizeX != sizeX ||
      oldDelegate.sizeY != sizeY ||
      oldDelegate.sizeZ != sizeZ;
}
