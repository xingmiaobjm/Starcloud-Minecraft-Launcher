// 基座冒烟：内核能启动、导航贡献齐全、页面工厂惰性。
import 'dart:io';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/kernel/descriptor.dart';
import 'package:starcloud_launcher/kernel/plugin_host.dart';
import 'package:starcloud_launcher/kernel/scheduler.dart';
import 'package:starcloud_launcher/kernel/paths.dart';
import 'package:starcloud_launcher/plugins/builtin/plugin_manager/plugin_manager_plugin.dart';
import 'package:starcloud_launcher/plugins/builtin/shell_pages/shell_pages_plugin.dart';
import 'package:starcloud_launcher/kernel/contribution_registry.dart';

void main() {
  test('内核启动后导航由插件贡献（顶部 9 + 底部 2，页面 11）', () async {
    final dir = await Directory.systemTemp.createTemp('starcloud_smoke');
    final kernel = Kernel(
      paths: KernelPaths.linux(env: {'HOME': dir.path}),
      scheduler: EventLoopScheduler(),
    );
    kernel.registerPlugin(shellPagesDescriptor(), ShellPagesPlugin.new);
    kernel.registerPlugin(pluginManagerDescriptor(), PluginManagerPlugin.new);
    await kernel.start();

    final nav = kernel.contributions.navItems();
    final footer = kernel.contributions.footerNavItems();
    expect(nav.length, 9, reason: '顶部 9 项（含插件管理器贡献的“插件”）');
    expect(footer.length, 2, reason: '底部 日志/设置');
    expect(nav.map((c) => c.id), contains('plugin-manager'));
    expect(
      kernel.snapshots().every((s) => s.state == PluginState.active),
      isTrue,
    );
    // 页面贡献齐全（惰性工厂，不调用不创建）。
    expect(kernel.contributions.byKind(ContributionKind.page).length, 11);
  });
}
