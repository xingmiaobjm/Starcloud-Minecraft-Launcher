import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/lab.dart';
import 'package:starcloud_launcher/shell/pages/lab_page.dart';

import 'support.dart';

/// 实验室页：加载投影、统计与 3D 预览、生成示例。
void main() {
  testWidgets('加载投影并渲染预览', (tester) async {
    final lab = _LabFake();
    await tester.pumpWidget(ProviderScope(
      overrides: [kernelDepsProvider.overrideWithValue(fakeDeps(lab: lab))],
      child: const FluentApp(home: LabPage()),
    ));
    await tester.pumpAndSettle();

    expect(find.textContaining('加载一个投影文件'), findsOneWidget);

    await tester.enterText(find.byType(TextBox), '/tmp/demo.litematic');
    await tester.tap(find.text('加载'));
    await tester.pumpAndSettle();

    expect(find.text('2 × 2 × 2'), findsOneWidget);
    await tester.scrollUntilVisible(
      find.text('minecraft:tnt'),
      300,
      scrollable: find.byType(Scrollable).first,
    );
    expect(find.text('minecraft:tnt'), findsOneWidget);
    expect(find.text('8'), findsWidgets);
    expect(find.byType(CustomPaint), findsWidgets);

    await tester.tap(find.text('生成世吞模板'));
    await tester.pumpAndSettle();
    expect(lab.generated, 1);
  });
}

class _LabFake implements LabService {
  int generated = 0;

  @override
  Future<ProjectionPreview> load(String path) async => ProjectionPreview(
        path: path,
        sizeX: 2,
        sizeY: 2,
        sizeZ: 2,
        totalBlocks: 8,
        distinctBlocks: 1,
        stats: const [BlockStat(id: 'minecraft:tnt', count: 8)],
        preview: const [
          VoxelPreviewBlock(x: 0, y: 0, z: 0, id: 'minecraft:tnt'),
          VoxelPreviewBlock(x: 1, y: 0, z: 0, id: 'minecraft:tnt'),
          VoxelPreviewBlock(x: 0, y: 1, z: 0, id: 'minecraft:tnt'),
          VoxelPreviewBlock(x: 1, y: 1, z: 0, id: 'minecraft:tnt'),
          VoxelPreviewBlock(x: 0, y: 0, z: 1, id: 'minecraft:tnt'),
          VoxelPreviewBlock(x: 1, y: 0, z: 1, id: 'minecraft:tnt'),
          VoxelPreviewBlock(x: 0, y: 1, z: 1, id: 'minecraft:tnt'),
          VoxelPreviewBlock(x: 1, y: 1, z: 1, id: 'minecraft:tnt'),
        ],
      );

  @override
  Future<String> generateWorldEater({
    int length = 64,
    int width = 16,
    int layers = 3,
    int spacing = 2,
    int layerGap = 1,
    int offset = 2,
  }) async {
    generated++;
    return '/tmp/lab.litematic';
  }
}
