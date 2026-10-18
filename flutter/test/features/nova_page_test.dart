import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/nova.dart';
import 'package:starcloud_launcher/shell/pages/nova_page.dart';

import 'support.dart';

/// Nova 页：流式对话 + 模型设置。
void main() {
  testWidgets('流式对话与模型配置', (tester) async {
    final nova = _ScriptedNova();
    await tester.pumpWidget(ProviderScope(
      overrides: [kernelDepsProvider.overrideWithValue(fakeDeps(nova: nova))],
      child: const FluentApp(home: NovaPage()),
    ));
    await tester.pumpAndSettle();

    expect(find.textContaining('未配置模型'), findsOneWidget);

    await tester.enterText(find.byType(TextBox), '诊断 1.20.4');
    await tester.tap(find.text('发送'));
    await tester.pumpAndSettle();
    expect(find.text('诊断 1.20.4'), findsOneWidget);
    expect(find.text('体检完成：一切正常。'), findsOneWidget);

    await tester.tap(find.text('模型设置'));
    await tester.pumpAndSettle();
    expect(find.text('Nova 模型设置'), findsOneWidget);
    final boxes = find.descendant(
      of: find.byType(ContentDialog),
      matching: find.byType(TextBox),
    );
    await tester.enterText(boxes.at(0), 'http://127.0.0.1:1234/v1');
    await tester.enterText(boxes.at(2), 'local-model');
    await tester.tap(find.text('保存'));
    await tester.pumpAndSettle();
    expect(nova.saved.single.baseUrl, 'http://127.0.0.1:1234/v1');
    expect(nova.saved.single.model, 'local-model');
  });
}

class _ScriptedNova implements NovaClient {
  NovaConfig _config = const NovaConfig();
  final List<NovaConfig> saved = [];

  @override
  Future<NovaConfig> config() async => _config;

  @override
  Future<NovaConfig> saveConfig(NovaConfig config) async {
    saved.add(config);
    _config = config;
    return config;
  }

  @override
  Stream<NovaEvent> chat({
    required String message,
    List<NovaMessage> history = const [],
    String? versionId,
  }) async* {
    yield const NovaTool('diagnose_version');
    yield const NovaDelta('体检完成：');
    yield const NovaDelta('一切正常。');
    yield const NovaDone(text: '体检完成：一切正常。');
  }
}
