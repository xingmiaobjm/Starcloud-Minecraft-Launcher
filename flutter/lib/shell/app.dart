/// 外壳：Fluent 主题 + 导航（**完全由贡献点表驱动**，不硬编码任何导航项）。
///
/// 壳只做三件事：把 navItem 贡献渲染成导航项、把 page 贡献按需实例化、把选中态
/// 记在本组件里。插件停用/启用时贡献点表变化 → 监听重建，不重建整窗（规范 §8.1）。
library;

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../bootstrap.dart';
import '../kernel/contribution_registry.dart';
import 'theme.dart';

class StarcloudApp extends StatelessWidget {
  const StarcloudApp({super.key});

  @override
  Widget build(BuildContext context) {
    return FluentApp(
      title: 'Starcloud Launcher',
      debugShowCheckedModeBanner: false,
      theme: buildLauncherTheme(),
      home: const LauncherShell(),
    );
  }
}

class LauncherShell extends ConsumerStatefulWidget {
  const LauncherShell({super.key});

  @override
  ConsumerState<LauncherShell> createState() => _LauncherShellState();
}

class _LauncherShellState extends ConsumerState<LauncherShell> {
  /// 选中的页面 id（用 id 而不是下标：贡献点可能被增删，下标会错位）。
  String? _selectedId;

  @override
  Widget build(BuildContext context) {
    final kernel = ref.read(kernelProvider);
    // 贡献点表变化（插件启停/热重载）→ 只重建导航与内容区，不重建窗口。
    return ListenableBuilder(
      listenable: kernel.contributions,
      builder: (context, _) {
        final registry = kernel.contributions;
        final top = registry.navItems();
        final footer = registry.footerNavItems();
        final all = [...top, ...footer];
        if (all.isEmpty) {
          return const ScaffoldPage(
            content: Center(child: Text('没有可用的页面插件')),
          );
        }
        // 选中项被撤下（贡献者停用）→ 回落到第一个。
        final selectedId =
            all.any((c) => c.id == _selectedId) ? _selectedId! : all.first.id;
        _selectedId = selectedId;

        // 页面贡献按 id 索引；导航项没有对应页面时降级为说明（不崩、不空白）。
        final pages = {
          for (final c in registry.byKind(ContributionKind.page)) c.id: c,
        };

        return NavigationView(
          pane: NavigationPane(
            selected: all.indexWhere((c) => c.id == selectedId),
            onChanged: (index) => setState(() => _selectedId = all[index].id),
            items: [for (final c in top) _paneItem(c, pages)],
            footerItems: [for (final c in footer) _paneItem(c, pages)],
          ),
        );
      },
    );
  }

  PaneItem _paneItem(
    Contribution navItem,
    Map<String, Contribution> pages,
  ) {
    final page = pages[navItem.id];
    return PaneItem(
      icon: Icon(navItem.icon ?? FluentIcons.page),
      title: Text(navItem.title),
      // body 只在该项被选中时挂载 → 页面工厂惰性调用（规范 §8.2）；
      // 离开即卸载（释放）。单贡献异常降级为占位（规范 §13）。
      body: page?.view == null
          ? const _MissingPage()
          : _LazyView(builder: page!.view!),
    );
  }
}

/// 惰性视图：首次挂载时才调用插件给的工厂。
class _LazyView extends StatelessWidget {
  const _LazyView({required this.builder});

  final WidgetBuilder builder;

  @override
  Widget build(BuildContext context) {
    try {
      return builder(context);
    } catch (e) {
      return ScaffoldPage(
        content: InfoBar(
          title: const Text('页面渲染失败'),
          content: Text('$e'),
          severity: InfoBarSeverity.error,
          isLong: true,
        ),
      );
    }
  }
}

class _MissingPage extends StatelessWidget {
  const _MissingPage();

  @override
  Widget build(BuildContext context) {
    return const ScaffoldPage(
      content: Center(child: Text('该导航项没有对应的页面贡献')),
    );
  }
}
