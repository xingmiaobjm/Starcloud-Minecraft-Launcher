/// 内置插件：插件管理器（规范 §3 kind=ui）。
///
/// 它自己也是一个插件——停用它，"插件"导航项与诊断页会随之消失（贡献点被撤下），
/// 这是"万物皆插件"最直观的演示。非 essential：允许用户停用。
library;

import 'package:fluent_ui/fluent_ui.dart';

import '../../../kernel/contribution_registry.dart';
import '../../../kernel/descriptor.dart';
import '../../../kernel/plugin_context.dart';
import '../../../shell/pages/plugin_diagnostics_page.dart';

class PluginManagerPlugin implements Plugin {
  @override
  Future<void> initialize(PluginContext ctx) async {
    ctx.contributions.upsert(Contribution(
      kind: ContributionKind.navItem,
      pluginId: ctx.descriptor.id,
      id: 'plugin-manager',
      title: '插件',
      icon: FluentIcons.puzzle,
      order: 7, // 与 shell_pages 的"插件"项同位（先撤后挂，见下）
    ));
    ctx.contributions.upsert(Contribution(
      kind: ContributionKind.page,
      pluginId: ctx.descriptor.id,
      id: 'plugin-manager',
      title: '插件',
      order: 7,
      view: (_) => const PluginDiagnosticsPage(),
    ));
    ctx.log('插件管理器已贡献导航项与诊断页');
  }

  @override
  Future<void> shutdown() async {}
}

PluginDescriptor pluginManagerDescriptor() => PluginDescriptor.create(
      id: 'starcloud.plugin_manager',
      name: '插件管理器',
      version: '1.0.0',
      apiVersion: '3.0',
      kind: PluginKind.ui,
      builtin: true,
      activation: PluginActivation.eager,
      // 非 essential：停用它即可演示"导航项随插件消失"。
      essential: false,
      permissions: const ['ui.contribute'],
      description: '插件状态诊断与启停界面（本身也是一个插件）',
      author: 'Starcloud',
    );
