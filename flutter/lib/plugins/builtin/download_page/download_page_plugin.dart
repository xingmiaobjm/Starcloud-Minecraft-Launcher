/// 内置插件：下载页（原版清单 → 安装，进度可见）。
library;

import 'package:fluent_ui/fluent_ui.dart';

import '../../../contracts/minecraft.dart';
import '../../../kernel/contribution_registry.dart';
import '../../../kernel/descriptor.dart';
import '../../../kernel/plugin_context.dart';
import '../../../shell/pages/download_page.dart';

class DownloadPagePlugin implements Plugin {
  @override
  Future<void> initialize(PluginContext ctx) async {
    ctx.contributions.upsert(Contribution(
      kind: ContributionKind.navItem,
      pluginId: ctx.descriptor.id,
      id: 'download',
      title: '下载',
      icon: FluentIcons.download,
      order: 2,
    ));
    ctx.contributions.upsert(Contribution(
      kind: ContributionKind.page,
      pluginId: ctx.descriptor.id,
      id: 'download',
      title: '下载',
      order: 2,
      view: (_) => const DownloadPage(),
    ));
  }

  @override
  Future<void> shutdown() async {}
}

PluginDescriptor downloadPageDescriptor() => PluginDescriptor.create(
      id: 'starcloud.download_page',
      name: '下载页',
      version: '1.0.0',
      apiVersion: '3.0',
      kind: PluginKind.feature,
      requires: const [MinecraftCatalog, MinecraftInstaller],
      permissions: const ['ui.contribute', 'net.http'],
      description: '官方版本清单与一键安装',
      author: 'Starcloud',
    );
