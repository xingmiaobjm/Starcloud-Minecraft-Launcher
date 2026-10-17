/// 内置插件：首页（启动入口）。
library;

import 'package:fluent_ui/fluent_ui.dart';

import '../../../contracts/minecraft.dart';
import '../../../kernel/contribution_registry.dart';
import '../../../kernel/descriptor.dart';
import '../../../kernel/plugin_context.dart';
import '../../../shell/pages/home_page.dart';

class HomePlugin implements Plugin {
  @override
  Future<void> initialize(PluginContext ctx) async {
    ctx.contributions.upsert(Contribution(
      kind: ContributionKind.navItem,
      pluginId: ctx.descriptor.id,
      id: 'home',
      title: '首页',
      icon: FluentIcons.home,
      order: 0,
    ));
    ctx.contributions.upsert(Contribution(
      kind: ContributionKind.page,
      pluginId: ctx.descriptor.id,
      id: 'home',
      title: '首页',
      order: 0,
      view: (_) => const HomePage(),
    ));
  }

  @override
  Future<void> shutdown() async {}
}

PluginDescriptor homeDescriptor() => PluginDescriptor.create(
      id: 'starcloud.home',
      name: '首页',
      version: '1.0.0',
      apiVersion: '3.0',
      kind: PluginKind.feature,
      requires: const [MinecraftCatalog, GameLauncher, AccountStore, LauncherSettings],
      permissions: const ['ui.contribute', 'game.launch'],
      description: '版本选择 / 账户 / 启动 / 日志尾',
      author: 'Starcloud',
    );
