/// 首页：选版本 → 选账户 → 启动 → 实时日志。
///
/// 由 `starcloud.home` 插件贡献（导航项 + 页面），按契约消费：
/// MinecraftCatalog（已装列表）/ GameLauncher（启动）/ AccountStore（玩家）/ LauncherSettings（内存）。
/// 会话与日志状态在 [gameSessionProvider]（应用级），切页不丢、也不能重复启动。
library;

import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/minecraft.dart';
import '../game_session_controller.dart';

class HomePage extends ConsumerStatefulWidget {
  const HomePage({super.key});

  @override
  ConsumerState<HomePage> createState() => _HomePageState();
}

class _HomePageState extends ConsumerState<HomePage> {
  List<InstalledVersion> _versions = const [];
  List<GameAccount> _accounts = const [];
  String? _selectedVersion;
  String? _selectedAccount;
  String? _loadError;
  String? _hint;

  KernelDeps get _deps => ref.read(kernelDepsProvider);

  @override
  void initState() {
    super.initState();
    unawaited(_reload());
  }

  Future<void> _reload() async {
    // 先取依赖再 await：页面在 await 期间被销毁时不再碰 ref。
    final deps = _deps;
    try {
      final versions = await deps.catalog.installedVersions();
      if (!mounted) return;
      final accounts = deps.accounts.accounts();
      final current = deps.accounts.current();
      setState(() {
        _versions = versions;
        _accounts = accounts;
        _loadError = null;
        if (!versions.any((v) => v.id == _selectedVersion)) {
          _selectedVersion = versions.isNotEmpty ? versions.first.id : null;
        }
        if (!accounts.any((a) => a.uuid == _selectedAccount)) {
          _selectedAccount = current?.uuid ??
              (accounts.isNotEmpty ? accounts.first.uuid : null);
        }
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _loadError = '读取已安装版本失败：$e');
    }
  }

  void _launch() {
    final version = _selectedVersion;
    if (version == null) {
      setState(() => _hint = '没有已安装的版本 —— 先去「下载」页装一个');
      return;
    }
    GameAccount? account;
    for (final a in _accounts) {
      if (a.uuid == _selectedAccount) {
        account = a;
        break;
      }
    }
    if (account == null) {
      setState(() => _hint = '还没有游戏账户 —— 先去「账户」页添加离线账户');
      return;
    }
    setState(() => _hint = null);
    unawaited(ref.read(gameSessionProvider.notifier).launch(
          launcher: _deps.launcher,
          versionId: version,
          playerName: account.name,
        ));
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    final session = ref.watch(gameSessionProvider);
    final status = session.status ?? _hint;
    return ScaffoldPage(
      header: const PageHeader(title: Text('首页')),
      content: ListView(
        padding: const EdgeInsets.symmetric(horizontal: 24),
        children: [
          Card(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('启动', style: typography.subtitle),
                const SizedBox(height: 12),
                Row(children: [
                  Expanded(
                    child: InfoLabel(
                      label: '版本',
                      child: ComboBox<String>(
                        value: _selectedVersion,
                        items: [
                          for (final v in _versions)
                            ComboBoxItem(value: v.id, child: Text(v.id)),
                        ],
                        onChanged: (v) => setState(() => _selectedVersion = v),
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Expanded(
                    child: InfoLabel(
                      label: '账户',
                      child: ComboBox<String>(
                        value: _selectedAccount,
                        items: [
                          for (final a in _accounts)
                            ComboBoxItem(value: a.uuid, child: Text(a.name)),
                        ],
                        onChanged: (v) => setState(() => _selectedAccount = v),
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  FilledButton(
                    onPressed: session.launching ? null : _launch,
                    child: Padding(
                      padding:
                          const EdgeInsets.symmetric(vertical: 8, horizontal: 12),
                      child: Text(
                          session.launching ? '启动中…' : '启动游戏',
                          style: typography.bodyStrong),
                    ),
                  ),
                ]),
                if (status != null) ...[
                  const SizedBox(height: 10),
                  Text(status, style: typography.caption),
                ],
              ],
            ),
          ),
          if (_loadError != null) ...[
            const SizedBox(height: 12),
            InfoBar(
              title: const Text('版本列表读取失败'),
              content: Text(_loadError!),
              severity: InfoBarSeverity.error,
              isLong: true,
            ),
          ],
          if (session.logs.isNotEmpty) ...[
            const SizedBox(height: 12),
            Card(
              padding: const EdgeInsets.all(12),
              child: SizedBox(
                height: 260,
                child: SingleChildScrollView(
                  reverse: true,
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      for (final line in session.logs)
                        Text(line,
                            style: typography.caption
                                ?.copyWith(fontFamily: 'monospace')),
                    ],
                  ),
                ),
              ),
            ),
          ],
          if (_versions.isEmpty) ...[
            const SizedBox(height: 12),
            const InfoBar(
              title: Text('还没有已安装的版本'),
              content: Text('去「下载」页选择一个版本安装；或把已有的 .minecraft 放到设置里指定的目录。'),
              severity: InfoBarSeverity.warning,
              isLong: true,
            ),
          ],
        ],
      ),
    );
  }
}
