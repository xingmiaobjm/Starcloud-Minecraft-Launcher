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
  bool _loadingVersions = true;

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
        _loadingVersions = false;
        if (!versions.any((v) => v.id == _selectedVersion)) {
          // 优先选"最近启动的版本"（C# 在 launch.start 时记录）。
          final preferred = versions.where((v) => v.isDefault);
          _selectedVersion = versions.isNotEmpty
              ? (preferred.isNotEmpty ? preferred.first.id : versions.first.id)
              : null;
        }
        if (!accounts.any((a) => a.uuid == _selectedAccount)) {
          _selectedAccount = current?.uuid ??
              (accounts.isNotEmpty ? accounts.first.uuid : null);
        }
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingVersions = false;
        _loadError = '读取已安装版本失败：$e';
      });
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
          accountUuid: account.uuid,
        ));
  }

  @override
  Widget build(BuildContext context) {
    final theme = FluentTheme.of(context);
    final typography = theme.typography;
    final session = ref.watch(gameSessionProvider);

    return ScaffoldPage(
      header: const PageHeader(title: Text('首页')),
      content: ListView(
        padding: const EdgeInsets.fromLTRB(24, 8, 24, 24),
        children: [
          _launchCard(theme, typography, session),
          if (_loadError != null) ...[
            const SizedBox(height: 12),
            InfoBar(
              title: const Text('版本列表读取失败'),
              content: Text(_loadError!),
              severity: InfoBarSeverity.error,
              isLong: true,
              action: Button(onPressed: _reload, child: const Text('重试')),
            ),
          ],
          if (_versions.isEmpty && !_loadingVersions && _loadError == null) ...[
            const SizedBox(height: 12),
            InfoBar(
              title: const Text('还没有已安装的版本'),
              content: const Text('去「下载」页选择一个版本安装；'
                  '或把已有的 .minecraft 放到设置里指定的目录。'),
              severity: InfoBarSeverity.warning,
              isLong: true,
            ),
          ],
          if (session.logs.isNotEmpty) ...[
            const SizedBox(height: 12),
            _logCard(session, typography),
          ],
        ],
      ),
    );
  }

  // ── 启动卡 ──

  Widget _launchCard(
      FluentThemeData theme, Typography typography, GameSessionState session) {
    final Color accent = theme.accentColor.normal;
    return Card(
      padding: const EdgeInsets.all(20),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Row(children: [
            Icon(FluentIcons.play_solid, color: accent, size: 18),
            const SizedBox(width: 8),
            Text('启动游戏', style: typography.subtitle),
            const Spacer(),
            Text(
              '已装 ${_versions.length} 个版本 · ${_accounts.length} 个账户',
              style: typography.caption,
            ),
          ]),
          const SizedBox(height: 16),
          Row(children: [
            Expanded(
              child: InfoLabel(
                label: '游戏版本',
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
            const SizedBox(width: 16),
            Expanded(
              child: InfoLabel(
                label: '游戏账户',
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
          ]),
          const SizedBox(height: 16),
          Row(children: [
            Expanded(
              child: _statusLine(session, typography, accent),
            ),
            const SizedBox(width: 16),
            _launchButton(session, typography),
          ]),
        ],
      ),
    );
  }

  Widget _statusLine(GameSessionState session, Typography typography,
      Color accent) {
    final status = session.status ?? _hint;
    if (status == null) {
      return Text('选择版本与账户后即可启动',
          style: typography.caption?.copyWith(
              color: FluentTheme.of(context)
                  .resources
                  .textFillColorSecondary));
    }
    final running = session.pid != null && session.status!.startsWith('游戏运行中');
    final dotColor = session.launching
        ? const Color(0xFFE8B31A)
        : running
            ? const Color(0xFF3FBF6F)
            : (session.status!.startsWith('启动失败') ||
                    session.status!.startsWith('游戏进程异常') ||
                    session.status!.startsWith('日志流中断'))
                ? const Color(0xFFE04B4B)
                : FluentTheme.of(context)
                    .resources
                    .textFillColorSecondary;
    return Row(children: [
      Container(
        width: 8,
        height: 8,
        decoration: BoxDecoration(color: dotColor, shape: BoxShape.circle),
      ),
      const SizedBox(width: 8),
      Expanded(
        child: Text(
          status,
          maxLines: 2,
          overflow: TextOverflow.ellipsis,
          style: typography.body,
        ),
      ),
    ]);
  }

  Widget _launchButton(GameSessionState session, Typography typography) {
    final launching = session.launching;
    final running = session.pid != null;
    return SizedBox(
      height: 40,
      child: FilledButton(
        onPressed: launching || running ? null : _launch,
        child: launching
            ? Row(mainAxisSize: MainAxisSize.min, children: [
                const SizedBox(
                  width: 14,
                  height: 14,
                  child: ProgressRing(strokeWidth: 2),
                ),
                const SizedBox(width: 10),
                Text('启动中…', style: typography.bodyStrong),
              ])
            : Row(mainAxisSize: MainAxisSize.min, children: [
                const Icon(FluentIcons.play_solid, size: 14),
                const SizedBox(width: 8),
                Text(running ? '游戏运行中' : '启动游戏',
                    style: typography.bodyStrong),
              ]),
      ),
    );
  }

  // ── 日志卡 ──

  Widget _logCard(GameSessionState session, Typography typography) {
    final running = session.pid != null;
    final dotColor = running ? const Color(0xFF3FBF6F) : const Color(0xFF8A8A8A);
    return Card(
      padding: EdgeInsets.zero,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 10),
            child: Row(children: [
              Container(
                width: 8,
                height: 8,
                decoration:
                    BoxDecoration(color: dotColor, shape: BoxShape.circle),
              ),
              const SizedBox(width: 8),
              Text('运行日志', style: typography.subtitle),
              const Spacer(),
              if (session.pid != null)
                Text('pid ${session.pid}', style: typography.caption),
              if (session.logs.isNotEmpty) ...[
                const SizedBox(width: 12),
                Button(
                  onPressed: () => ref
                      .read(gameSessionProvider.notifier)
                      .clearLogs(),
                  child: const Text('清空'),
                ),
              ],
            ]),
          ),
          Container(
            height: 1,
            color: FluentTheme.of(context)
                .resources
                .cardStrokeColorDefault,
          ),
          Container(
            height: 300,
            color: FluentTheme.of(context)
                .resources
                .controlFillColorDisabled,
            child: session.logs.isEmpty
                ? Center(
                    child: Text('暂无日志', style: typography.caption),
                  )
                : SingleChildScrollView(
                    reverse: true,
                    padding:
                        const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        for (final line in session.logs)
                          Padding(
                            padding: const EdgeInsets.only(bottom: 1),
                            child: SelectableText(
                              line,
                              style: typography.caption?.copyWith(
                                fontFamily: 'monospace',
                                height: 1.45,
                              ),
                            ),
                          ),
                      ],
                    ),
                  ),
          ),
        ],
      ),
    );
  }
}
