/// 基座占位页：说明该页面将来做什么。
///
/// 业务插件接入后，用真实页面替换对应贡献点的 `view` 工厂即可——壳与内核不动。
library;

import 'package:fluent_ui/fluent_ui.dart';

class PlaceholderPage extends StatelessWidget {
  const PlaceholderPage({
    super.key,
    required this.title,
    required this.description,
    required this.pluginId,
  });

  final String title;
  final String description;

  /// 提供该页面的插件 id（诊断用：一眼看出"这一页是谁贡献的"）。
  final String pluginId;

  @override
  Widget build(BuildContext context) {
    return ScaffoldPage(
      header: PageHeader(title: Text(title)),
      content: Padding(
        padding: const EdgeInsets.symmetric(horizontal: 24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Card(
              padding: const EdgeInsets.all(20),
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(description, style: FluentTheme.of(context).typography.body),
                  const SizedBox(height: 12),
                  InfoBar(
                    title: const Text('基座占位页'),
                    content: Text(
                      '此页由插件 $pluginId 贡献。业务实现接入后，'
                      '只需替换该贡献点的页面工厂，壳与内核无需改动。',
                    ),
                    severity: InfoBarSeverity.info,
                    isLong: true,
                  ),
                ],
              ),
            ),
          ],
        ),
      ),
    );
  }
}
