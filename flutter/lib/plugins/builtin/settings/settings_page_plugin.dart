/// 内置插件：设置页（游戏目录 / Java / 内存 / 下载源）。
library;

import 'package:fluent_ui/fluent_ui.dart';

import '../../../contracts/minecraft.dart';
import '../../../kernel/contribution_registry.dart';
import '../../../kernel/descriptor.dart';
import '../../../kernel/plugin_context.dart';
import '../../../shell/pages/settings_page.dart';

class SettingsPagePlugin implements Plugin {
  @override
  Future<void> initialize(PluginContext ctx) async {
    ctx.contributions.upsert(Contribution(
      kind: ContributionKind.navItem,
      pluginId: ctx.descriptor.id,
      id: 'settings',
      title: '设置',
      icon: FluentIcons.settings,
      order: 10,
      footer: true,
    ));
    ctx.contributions.upsert(Contribution(
      kind: ContributionKind.page,
      pluginId: ctx.descriptor.id,
      id: 'settings',
      title: '设置',
      order: 10,
      view: (_) => const SettingsPage(),
    ));
  }

  @override
  Future<void> shutdown() async {}
}

PluginDescriptor settingsPageDescriptor() => PluginDescriptor.create(
      id: 'starcloud.settings_page',
      name: '设置页',
      version: '1.0.0',
      apiVersion: '3.0',
      kind: PluginKind.feature,
      requires: const [LauncherSettings, GameLauncher],
      permissions: const ['ui.contribute'],
      description: '设置界面（写入 starcloud.settings 的配置分区）',
      author: 'Starcloud',
    );
