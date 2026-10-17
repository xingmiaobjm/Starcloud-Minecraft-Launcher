/// 内置插件：启动器主页面集（规范 §3 kind=feature）。
///
/// 这是"万物皆插件"的第一块地基：壳**不硬编码**任何导航项，全部由本插件在
/// `initialize` 里经贡献点注册。导航顺序与原 Windows 版一致（首页/版本/下载/
/// 存档/联机/运行状态/账户/插件/Nova + 底部 启动器日志/设置）。
///
/// 基座期每个页面是占位实现（说明该页将来做什么）；业务插件接入时，把对应
/// 页面换成真实实现即可，壳与内核一行不用改。
library;

import 'package:fluent_ui/fluent_ui.dart';

import '../../../kernel/contribution_registry.dart';
import '../../../kernel/descriptor.dart';
import '../../../kernel/plugin_context.dart';
import '../../../shell/pages/placeholder_page.dart';

/// 页面定义：id / 标题 / 图标 / 说明（占位页正文）。
class _PageSpec {
  const _PageSpec(this.id, this.title, this.icon, this.description, this.order,
      {this.footer = false});

  final String id;
  final String title;
  final IconData icon;
  final String description;

  /// 导航排序权重（"插件"页由 starcloud.plugin_manager 提供，占 7）。
  final int order;
  final bool footer;
}

/// 与原 Windows 版 MainWindow 的导航顺序一致。
const List<_PageSpec> _pages = [
  _PageSpec('home', '首页', FluentIcons.home, '版本列表、启动按钮、账户卡与启动覆盖层。', 0),
  _PageSpec('versions', '版本', FluentIcons.list, '已安装版本列表：搜索、分类过滤、实例档案、右键操作。', 1),
  _PageSpec('download', '下载', FluentIcons.download, '原版版本 → 加载器（Forge/NeoForge/Fabric）→ 社区资源。', 2),
  _PageSpec('saves', '存档', FluentIcons.save, '跨版本世界列表：备份、删除、跨版本复制启动。', 3),
  _PageSpec('multiplayer', '联机', FluentIcons.people, '陶瓦联机与第三方联机 SDK：建房、进房、状态与日志。', 4),
  _PageSpec('running', '运行状态', FluentIcons.speed_high, '本局运行态：CPU/内存曲线、日志尾、多开切换。', 5),
  _PageSpec('accounts', '账户', FluentIcons.contact, 'Starcloud ID 与游戏账户：微软设备码 / 第三方 / 离线。', 6),
  // 7 留给 starcloud.plugin_manager（插件页由它提供——跨插件拼导航的实证）。
  _PageSpec('nova', 'Nova', FluentIcons.chat, 'Nova 助手：模型档案、工具许可、审计日志。', 8),
  _PageSpec('logs', '启动器日志', FluentIcons.history, '启动器日志与每局游戏日志切换查看。', 9,
      footer: true),
  _PageSpec('settings', '设置', FluentIcons.settings, '游戏目录、内存、Java、下载源、外观与语言。', 10,
      footer: true),
];

class ShellPagesPlugin implements Plugin {
  @override
  Future<void> initialize(PluginContext ctx) async {
    for (final page in _pages) {
      // 导航项（壳消费贡献点表渲染，规范 §8.2 navItem）。
      ctx.contributions.upsert(Contribution(
        kind: ContributionKind.navItem,
        pluginId: ctx.descriptor.id,
        id: page.id,
        title: page.title,
        icon: page.icon,
        order: page.order,
        footer: page.footer,
      ));
      // 页面（惰性工厂：注册时不创建，首次进入才创建，规范 §8.2 page）。
      ctx.contributions.upsert(Contribution(
        kind: ContributionKind.page,
        pluginId: ctx.descriptor.id,
        id: page.id,
        title: page.title,
        order: page.order,
        view: (_) => PlaceholderPage(
          title: page.title,
          description: page.description,
          pluginId: ctx.descriptor.id,
        ),
      ));
    }
    ctx.log('已贡献 ${_pages.length} 个导航项与页面');
  }

  @override
  Future<void> shutdown() async {
    // 贡献由内核统一撤下（规范 §4.2 第 3 步），这里无需重复。
  }
}

/// 编译期登记（规范 §0.3 决策①：内置插件不引入 ABI/动态加载问题）。
PluginDescriptor shellPagesDescriptor() => PluginDescriptor.create(
      id: 'starcloud.shell_pages',
      name: '启动器页面',
      version: '1.0.0',
      apiVersion: '3.0',
      kind: PluginKind.feature,
      builtin: true,
      activation: PluginActivation.eager,
      essential: true, // 主页面集缺失 = 宿主不可用
      permissions: const ['ui.contribute'],
      description: '首页/版本/下载/存档/联机/运行状态/账户/插件/Nova/日志/设置 的导航与占位页',
      author: 'Starcloud',
    );
