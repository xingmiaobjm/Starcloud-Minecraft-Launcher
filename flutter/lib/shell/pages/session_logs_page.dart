/// 会话日志页：显示当前/最近一局游戏的实时日志。
///
/// 数据来自 [gameSessionProvider]（应用级状态），因此切页、关页都不会丢日志。
/// 同一个页面同时服务「运行状态」和「启动器日志」两个导航项。
library;

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../game_session_controller.dart';

class SessionLogsPage extends ConsumerWidget {
  const SessionLogsPage({
    super.key,
    required this.title,
    this.showSessionInfo = false,
  });

  final String title;

  /// 运行状态页显示 pid / 状态；日志页只显示日志。
  final bool showSessionInfo;

  @override
  Widget build(BuildContext context, WidgetRef ref) {
    final typography = FluentTheme.of(context).typography;
    final session = ref.watch(gameSessionProvider);
    final logs = session.logs;

    return ScaffoldPage(
      header: PageHeader(
        title: Text(title),
        commandBar: CommandBar(
          mainAxisAlignment: MainAxisAlignment.end,
          primaryItems: [
            CommandBarButton(
              icon: const Icon(FluentIcons.clear),
              label: const Text('清空'),
              onPressed: logs.isEmpty
                  ? null
                  : () => ref.read(gameSessionProvider.notifier).clearLogs(),
            ),
          ],
        ),
      ),
      content: Padding(
        padding: const EdgeInsets.fromLTRB(24, 0, 24, 16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (showSessionInfo) ...[
              Card(
                padding: const EdgeInsets.all(16),
                child: Row(children: [
                  Icon(session.launching
                      ? FluentIcons.sync
                      : FluentIcons.player_settings),
                  const SizedBox(width: 10),
                  Expanded(
                    child: Text(
                      session.status ?? '当前没有运行中的游戏',
                      style: typography.bodyStrong,
                    ),
                  ),
                  if (session.pid != null)
                    Text('pid ${session.pid}', style: typography.caption),
                ]),
              ),
              const SizedBox(height: 12),
            ],
            Expanded(
              child: Card(
                padding: const EdgeInsets.all(12),
                child: logs.isEmpty
                    ? Center(
                        child: Text(
                          '暂无日志。启动游戏后，这里会实时显示输出。',
                          style: typography.body,
                        ),
                      )
                    : ListView.builder(
                        reverse: true,
                        itemCount: logs.length,
                        itemBuilder: (context, index) {
                          // reverse 列表：index 0 是最新一行，显示在底部。
                          final line = logs[logs.length - 1 - index];
                          return Text(
                            line,
                            style: typography.caption
                                ?.copyWith(fontFamily: 'monospace'),
                          );
                        },
                      ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
