/// 插件诊断页（规范 §4.1 的可观测性要求，也是"万物皆插件"的直观展示）。
///
/// 它自己由 `starcloud.plugin_manager` 插件贡献；停用该插件，这一页与对应导航项
/// 会一起消失。页面直接读内核快照，并演示启停/重试 API。
library;

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../bootstrap.dart';
import '../../kernel/descriptor.dart';
import '../../kernel/plugin_host.dart';

class PluginDiagnosticsPage extends ConsumerStatefulWidget {
  const PluginDiagnosticsPage({super.key});

  @override
  ConsumerState<PluginDiagnosticsPage> createState() =>
      _PluginDiagnosticsPageState();
}

class _PluginDiagnosticsPageState extends ConsumerState<PluginDiagnosticsPage> {
  List<PluginSnapshot> _snapshots = const [];
  String? _lastAction;

  @override
  void initState() {
    super.initState();
    _refresh();
  }

  void _refresh() {
    if (!mounted) return;
    setState(() => _snapshots = ref.read(kernelProvider).snapshots());
  }

  Future<void> _run(Future<String?> Function() action, String label) async {
    final reason = await action();
    if (!mounted) return;
    setState(() => _lastAction = reason == null ? '$label 成功' : '$label 被拒绝：$reason');
    _refresh();
  }

  String _stateLabel(PluginState state) => switch (state) {
        PluginState.registered => '已登记',
        PluginState.activating => '激活中',
        PluginState.active => '运行中',
        PluginState.stopping => '停用中',
        PluginState.stopped => '已停用',
        PluginState.failed => '失败',
        PluginState.disabled => '已禁用',
      };

  InfoBarSeverity _severity(PluginState state) => switch (state) {
        PluginState.active => InfoBarSeverity.success,
        PluginState.failed => InfoBarSeverity.error,
        PluginState.disabled => InfoBarSeverity.warning,
        _ => InfoBarSeverity.info,
      };

  @override
  Widget build(BuildContext context) {
    final kernel = ref.read(kernelProvider);
    final typography = FluentTheme.of(context).typography;
    return ScaffoldPage(
      header: PageHeader(
        title: const Text('插件'),
        commandBar: CommandBar(
          mainAxisAlignment: MainAxisAlignment.end,
          primaryItems: [
            CommandBarButton(
              icon: const Icon(FluentIcons.refresh),
              label: const Text('刷新'),
              onPressed: _refresh,
            ),
          ],
        ),
      ),
      content: ListView(
        padding: const EdgeInsets.symmetric(horizontal: 24),
        children: [
          Card(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('内核 apiVersion $kernelApiVersion', style: typography.subtitle),
                const SizedBox(height: 6),
                Text(
                  '共 ${_snapshots.length} 个插件，'
                  '${_snapshots.where((s) => s.state == PluginState.active).length} 个运行中；'
                  '贡献点 ${kernel.contributions.length} 条，'
                  '契约登记 ${kernel.services.length} 条。',
                  style: typography.body,
                ),
                if (_lastAction != null) ...[
                  const SizedBox(height: 10),
                  Text(_lastAction!, style: typography.caption),
                ],
              ],
            ),
          ),
          const SizedBox(height: 12),
          for (final s in _snapshots) _pluginCard(s, kernel),
        ],
      ),
    );
  }

  Widget _pluginCard(PluginSnapshot s, Kernel kernel) {
    final typography = FluentTheme.of(context).typography;
    return Padding(
      padding: const EdgeInsets.only(bottom: 10),
      child: Card(
        padding: const EdgeInsets.all(16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Row(
              children: [
                Expanded(
                  child: Text('${s.name}  ·  ${s.id}', style: typography.subtitle),
                ),
                InfoBar(
                  title: Text(_stateLabel(s.state)),
                  severity: _severity(s.state),
                  isLong: false,
                ),
              ],
            ),
            const SizedBox(height: 8),
            Text(
              '种类 ${s.kind.name}  ·  激活 ${s.activationMs}ms  ·  '
              '贡献 ${s.contributionCount}  ·  契约 ${s.contractCount}'
              '${s.essential ? "  ·  必需" : ""}',
              style: typography.caption,
            ),
            if (s.error != null) ...[
              const SizedBox(height: 8),
              InfoBar(
                title: const Text('错误'),
                content: Text(s.error!),
                severity: InfoBarSeverity.error,
                isLong: true,
              ),
            ],
            const SizedBox(height: 10),
            Row(
              children: [
                if (s.state == PluginState.active)
                  Button(
                    onPressed: s.essential
                        ? null
                        : () => _run(() => kernel.deactivate(s.id), '停用 ${s.id}'),
                    child: const Text('停用'),
                  ),
                if (s.state == PluginState.stopped)
                  Button(
                    onPressed: () => _run(
                      () async =>
                          await kernel.activate(s.id) ? null : '激活失败（见错误）',
                      '启用 ${s.id}',
                    ),
                    child: const Text('启用'),
                  ),
                if (s.state == PluginState.failed)
                  Button(
                    onPressed: () {
                      kernel.retry(s.id);
                      _refresh();
                    },
                    child: const Text('重试'),
                  ),
                if (s.state == PluginState.disabled)
                  Button(
                    onPressed: () {
                      kernel.enable(s.id);
                      _refresh();
                    },
                    child: const Text('解除禁用'),
                  ),
              ],
            ),
          ],
        ),
      ),
    );
  }
}
