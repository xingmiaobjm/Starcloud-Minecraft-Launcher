import 'dart:io';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/bootstrap.dart';
import 'package:starcloud_launcher/contracts/plugins.dart';
import 'package:starcloud_launcher/kernel/paths.dart';
import 'package:starcloud_launcher/kernel/plugin_host.dart';
import 'package:starcloud_launcher/kernel/scheduler.dart';
import 'package:starcloud_launcher/shell/pages/plugin_diagnostics_page.dart';

import 'support.dart';

/// 插件页：外部插件安装/启停（免责确认）/卸载 + 内核插件诊断。
void main() {
  testWidgets('外部插件安装、启用、卸载', (tester) async {
    final dir = Directory.systemTemp.createTempSync('starcloud_plugins_page');
    addTearDown(() => dir.deleteSync(recursive: true));
    final kernel = Kernel(
      paths: KernelPaths.linux(env: {'HOME': dir.path}),
      scheduler: EventLoopScheduler(),
    );
    final store = _FakeStore();

    await tester.pumpWidget(ProviderScope(
      overrides: [
        kernelProvider.overrideWithValue(kernel),
        kernelDepsProvider.overrideWithValue(fakeDeps(plugins: store)),
      ],
      child: const FluentApp(home: PluginDiagnosticsPage()),
    ));
    await tester.pumpAndSettle();

    expect(find.text('还没有安装外部插件。'), findsOneWidget);

    await tester.enterText(find.byType(TextBox), '/tmp/e2e.mclplug');
    await tester.tap(find.text('安装'));
    await tester.pumpAndSettle();
    expect(store.actions, contains('install:/tmp/e2e.mclplug'));
    expect(find.textContaining('E2E Plugin'), findsWidgets);
    expect(find.text('[ui.contribute]'), findsOneWidget);

    await tester.tap(find.text('启用'));
    await tester.pumpAndSettle();
    expect(find.text('确认并启用'), findsOneWidget);
    await tester.tap(find.text('确认并启用'));
    await tester.pumpAndSettle();
    expect(store.actions, contains('setEnabled:e2e.plugin:true:true'));
    expect(find.text('[已启用]'), findsOneWidget);

    await tester.tap(find.text('卸载'));
    await tester.pumpAndSettle();
    final dialog = find.byType(ContentDialog);
    expect(dialog, findsOneWidget);
    await tester.tap(find.descendant(of: dialog, matching: find.text('卸载')));
    await tester.pumpAndSettle();
    expect(store.actions, contains('uninstall:e2e.plugin'));
    expect(find.text('还没有安装外部插件。'), findsOneWidget);
  });
}

class _FakeStore implements PluginStore {
  bool _installed = false;
  bool _enabled = false;
  bool _accepted = false;
  final List<String> actions = [];

  ExternalPlugin get _plugin => ExternalPlugin(
        id: 'e2e.plugin',
        name: 'E2E Plugin',
        version: '1.0.0',
        apiVersion: '1.0',
        author: 'E2E',
        description: '测试插件',
        permissions: const ['ui.contribute'],
        enabled: _enabled,
        disclaimerAccepted: _accepted,
        dir: '/tmp/e2e.plugin',
      );

  @override
  Future<List<ExternalPlugin>> list() async =>
      _installed ? [_plugin] : const [];

  @override
  Future<ExternalPlugin> install(String path) async {
    actions.add('install:$path');
    _installed = true;
    return _plugin;
  }

  @override
  Future<void> uninstall(String id) async {
    actions.add('uninstall:$id');
    _installed = false;
  }

  @override
  Future<void> setEnabled(
    String id,
    bool enabled, {
    bool acceptDisclaimer = false,
  }) async {
    actions.add('setEnabled:$id:$enabled:$acceptDisclaimer');
    _enabled = enabled;
    if (acceptDisclaimer) _accepted = true;
  }

  @override
  Future<void> openFolder(String id) async {
    actions.add('open:$id');
  }
}
