/// 内置插件：暂未插件化的占位页（版本/存档/联机/运行状态/Nova/日志）。
///
/// 首页/下载/账户/设置已经由各自的功能插件提供（真实实现）；这里保留的页面
/// 会在后续迭代里逐个插件化。壳**始终零硬编码**——导航只来自贡献点表。
library;

import 'package:fluent_ui/fluent_ui.dart';

import '../../../kernel/contribution_registry.dart';
import '../../../kernel/descriptor.dart';
import '../../../kernel/plugin_context.dart';
import '../../../shell/pages/placeholder_page.dart';
import '../../../shell/pages/session_logs_page.dart';

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
  _PageSpec('versions', '版本', FluentIcons.list, '已安装版本列表：搜索、分类过滤、实例档案、右键操作。', 1),
  _PageSpec('saves', '存档', FluentIcons.save, '跨版本世界列表：备份、删除、跨版本复制启动。', 3),
  _PageSpec('multiplayer', '联机', FluentIcons.people, '陶瓦联机与第三方联机 SDK：建房、进房、状态与日志。', 4),
  _PageSpec('running', '运行状态', FluentIcons.speed_high, '本局运行态：CPU/内存曲线、日志尾、多开切换。', 5),
  // 6 账户 / 7 插件 / 8 Nova 由各自插件贡献。
  _PageSpec('logs', '启动器日志', FluentIcons.history, '启动器日志与每局游戏日志切换查看。', 9,
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
      // 运行状态/日志两页已经接上应用级会话状态（gameSessionProvider），
      // 其余页面仍是占位。
      ctx.contributions.upsert(Contribution(
        kind: ContributionKind.page,
        pluginId: ctx.descriptor.id,
        id: page.id,
        title: page.title,
        order: page.order,
        view: (_) => switch (page.id) {
          'running' =>
            SessionLogsPage(title: page.title, showSessionInfo: true),
          'logs' => SessionLogsPage(title: page.title),
          _ => PlaceholderPage(
              title: page.title,
              description: page.description,
              pluginId: ctx.descriptor.id,
            ),
        },
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
      essential: false, // 只是占位页集：缺失时其它插件照样工作
      permissions: const ['ui.contribute'],
      description: '版本/存档/联机/运行状态/日志 的占位页（后续逐个插件化）',
      author: 'Starcloud',
    );
