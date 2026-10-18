/// 内置插件：账户页（离线账户增/选/删；微软登录并入此页）。
library;

import 'package:fluent_ui/fluent_ui.dart';

import '../../../contracts/minecraft.dart';
import '../../../kernel/contribution_registry.dart';
import '../../../kernel/descriptor.dart';
import '../../../kernel/plugin_context.dart';
import '../../../shell/pages/accounts_page.dart';

class AccountsPagePlugin implements Plugin {
  @override
  Future<void> initialize(PluginContext ctx) async {
    ctx.contributions.upsert(Contribution(
      kind: ContributionKind.navItem,
      pluginId: ctx.descriptor.id,
      id: 'accounts',
      title: '账户',
      icon: FluentIcons.contact,
      order: 6,
    ));
    ctx.contributions.upsert(Contribution(
      kind: ContributionKind.page,
      pluginId: ctx.descriptor.id,
      id: 'accounts',
      title: '账户',
      order: 6,
      view: (_) => const AccountsPage(),
    ));
  }

  @override
  Future<void> shutdown() async {}
}

PluginDescriptor accountsPageDescriptor() => PluginDescriptor.create(
      id: 'starcloud.accounts_page',
      name: '账户页',
      version: '1.0.0',
      apiVersion: '3.0',
      kind: PluginKind.feature,
      requires: const [AccountStore, AccountAuth],
      permissions: const ['ui.contribute'],
      description: '游戏账户列表与管理界面',
      author: 'Starcloud',
    );
