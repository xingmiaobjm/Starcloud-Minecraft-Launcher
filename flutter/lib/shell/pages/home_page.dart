/// 首页：左侧已安装版本列表 + 右侧账户/启动卡 + 底部运行日志。
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
      content: Padding(
        padding: const EdgeInsets.fromLTRB(24, 8, 24, 24),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            if (_loadError != null) ...[
              InfoBar(
                title: const Text('版本列表读取失败'),
                content: Text(_loadError!),
                severity: InfoBarSeverity.error,
                isLong: true,
                action: Button(onPressed: _reload, child: const Text('重试')),
              ),
              const SizedBox(height: 12),
            ],
            Expanded(
              child: _loadingVersions
                  ? const Center(child: ProgressRing())
                  : Row(
                      crossAxisAlignment: CrossAxisAlignment.stretch,
                      children: [
                        Expanded(
                          flex: 5,
                          child: _versionsCard(typography),
                        ),
                        const SizedBox(width: 16),
                        SizedBox(
                          width: 360,
                          child: _accountCard(session, typography),
                        ),
                      ],
                    ),
            ),
            if (session.logs.isNotEmpty) ...[
              const SizedBox(height: 12),
              _logCard(session, typography),
            ],
          ],
        ),
      ),
    );
  }

  // ── 左栏：已安装版本 ──

  Widget _versionsCard(Typography typography) {
    final versions = _versions;
    return Card(
      padding: EdgeInsets.zero,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 12, 16, 10),
            child: Row(children: [
              Text('已安装版本', style: typography.subtitle),
              const SizedBox(width: 8),
              Text('(${versions.length})',
                  style: typography.caption?.copyWith(
                      color: FluentTheme.of(context)
                          .resources
                          .textFillColorSecondary)),
            ]),
          ),
          Container(
              height: 1,
              color:
                  FluentTheme.of(context).resources.cardStrokeColorDefault),
          Expanded(
            child: versions.isEmpty
                ? Center(
                    child: Text(
                      // 错误已经在上方单独提示条里显示过了；
                      // 这里再显示一次会把「加载失败」说成「没有版本」，语义混淆。
                      _loadError != null ? '版本列表加载失败' : '还没有已安装的版本 —— 去「下载」页安装',
                      style: typography.body,
                      textAlign: TextAlign.center,
                    ),
                  )
                : ListView.builder(
                    padding: const EdgeInsets.symmetric(vertical: 6),
                    itemCount: versions.length,
                    itemBuilder: (context, index) {
                      final version = versions[index];
                      final selected = version.id == _selectedVersion;
                      return _versionRow(version, selected, typography,
                          () => setState(() => _selectedVersion = version.id));
                    },
                  ),
          ),
        ],
      ),
    );
  }

  Widget _versionRow(
    InstalledVersion version,
    bool selected,
    Typography typography,
    VoidCallback onTap,
  ) {
    final theme = FluentTheme.of(context);
    return Padding(
      padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 3),
      child: HoverButton(
        onPressed: onTap,
        builder: (context, states) {
          final hovered = states.contains(WidgetState.hovered);
          final bg = selected
              ? theme.accentColor.withValues(alpha: 0.16)
              : hovered
                  ? theme.resources.subtleFillColorSecondary
                  : Colors.transparent;
          return Container(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 8),
            decoration: BoxDecoration(
              color: bg,
              borderRadius: BorderRadius.circular(6),
              border: Border.all(
                color: selected
                    ? theme.accentColor.normal
                    : Colors.transparent,
                width: 1.2,
              ),
            ),
            child: Row(children: [
              _versionIcon(version),
              const SizedBox(width: 12),
              Expanded(
                child: Text(
                  version.id,
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: typography.bodyStrong,
                ),
              ),
              const SizedBox(width: 10),
              Text(_tagFor(version),
                  style: typography.caption?.copyWith(
                      color: theme.resources.textFillColorSecondary)),
              const SizedBox(width: 12),
              SizedBox(
                width: 92,
                child: Text(
                  version.lastPlayedAt == null
                      ? '从未启动'
                      : '最近 ${_formatTime(version.lastPlayedAt!)}',
                  textAlign: TextAlign.right,
                  style: typography.caption?.copyWith(
                      color: theme.resources.textFillColorSecondary),
                ),
              ),
            ]),
          );
        },
      ),
    );
  }

  Widget _versionIcon(InstalledVersion version) {
    final kind = _tagFor(version);
    final colors = switch (kind) {
      'Fabric' => const Color(0xFFB08A4F),
      'Forge' => const Color(0xFF5A5A66),
      'NeoForge' => const Color(0xFFE8905A),
      'Quilt' => const Color(0xFF9A8ABF),
      _ => const Color(0xFF6FA852),
    };
    return Container(
      width: 36,
      height: 36,
      decoration: BoxDecoration(
        color: colors.withValues(alpha: 0.22),
        borderRadius: BorderRadius.circular(6),
        border: Border.all(color: colors, width: 1),
      ),
      child: Center(
        child: Text(
          version.id.isNotEmpty ? version.id[0].toUpperCase() : '?',
          style: TextStyle(
            color: colors,
            fontWeight: FontWeight.bold,
            fontSize: 15,
          ),
        ),
      ),
    );
  }

  String _tagFor(InstalledVersion version) {
    if (version.modLoader != null) {
      return switch (version.modLoader) {
        'fabric' => 'Fabric',
        'forge' => 'Forge',
        'neoforge' => 'NeoForge',
        'quilt' => 'Quilt',
        final other => other ?? 'Modded',
      };
    }
    if (version.isModded) return 'Modded';
    // 快照版本号带横杠加字母段（如 26.3-rc-3 / 25w14a）。
    final id = version.id.toLowerCase();
    final looksSnapshot = RegExp(r'^\d+w\d+[a-z]').hasMatch(id) ||
        id.contains('-rc') ||
        id.contains('-pre');
    return looksSnapshot ? '快照版' : '正式版';
  }

  // ── 右栏：账户 / 启动 ──

  Widget _accountCard(GameSessionState session, Typography typography) {
    final theme = FluentTheme.of(context);
    final statusText = (session.status?.isNotEmpty ?? false)
        ? session.status!
        : (_hint ?? '');
    final account = _accountOf(_selectedAccount);
    final launching = session.launching;
    final running = session.pid != null;
    final canLaunch = !launching &&
        !running &&
        _selectedVersion != null &&
        account != null;

    return Card(
      padding: const EdgeInsets.all(16),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.stretch,
        children: [
          Text('游戏账户', style: typography.subtitle),
          const SizedBox(height: 12),
          // 账户选择（下拉，保持与列表一致的信息源）
          ComboBox<String>(
            value: _selectedAccount,
            items: [
              for (final account in _accounts)
                ComboBoxItem(value: account.uuid, child: Text(account.name)),
            ],
            onChanged: (v) => setState(() => _selectedAccount = v),
          ),
          const SizedBox(height: 12),
          Expanded(
            child: Center(
              child: Column(
                mainAxisSize: MainAxisSize.min,
                children: [
                  Container(
                    width: 84,
                    height: 84,
                    decoration: BoxDecoration(
                      color: theme.resources.controlFillColorDisabled,
                      borderRadius: BorderRadius.circular(10),
                    ),
                    child: Icon(
                      FluentIcons.contact,
                      size: 44,
                      color: theme.resources.textFillColorSecondary,
                    ),
                  ),
                  const SizedBox(height: 10),
                  Text(
                    account?.name ?? '未选择账户',
                    style: typography.subtitle,
                  ),
                  const SizedBox(height: 4),
                  Text(
                    account == null
                        ? '去「账户」页添加离线账户'
                        : switch (account.kind) {
                            AccountKind.offline => '离线账户',
                            AccountKind.microsoft => '微软正版',
                            AccountKind.external =>
                              account.serverName ?? '第三方账户',
                          },
                    style: typography.caption?.copyWith(
                        color: theme.resources.textFillColorSecondary),
                  ),
                ],
              ),
            ),
          ),
          if (statusText.isNotEmpty) ...[
            Row(children: [
              Container(
                width: 8,
                height: 8,
                decoration: BoxDecoration(
                  color: launching
                      ? const Color(0xFFE8B31A)
                      : running
                          ? const Color(0xFF3FBF6F)
                          : theme.resources.textFillColorSecondary,
                  shape: BoxShape.circle,
                ),
              ),
              const SizedBox(width: 8),
              Expanded(
                child: Text(
                  statusText,
                  maxLines: 2,
                  overflow: TextOverflow.ellipsis,
                  style: typography.caption,
                ),
              ),
            ]),
            const SizedBox(height: 10),
          ],
          SizedBox(
            height: 56,
            child: FilledButton(
              onPressed: canLaunch ? _launch : null,
              child: launching
                  ? Row(mainAxisSize: MainAxisSize.min, children: [
                      const SizedBox(
                        width: 16,
                        height: 16,
                        child: ProgressRing(strokeWidth: 2),
                      ),
                      const SizedBox(width: 10),
                      Text('启动中…', style: typography.subtitle),
                    ])
                  : Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      children: [
                        Row(mainAxisSize: MainAxisSize.min, children: [
                          const Icon(FluentIcons.play_solid, size: 15),
                          const SizedBox(width: 8),
                          Text(running ? '游戏运行中' : '启动游戏',
                              style: typography.subtitle),
                        ]),
                        if (running)
                          Text('pid ${session.pid}',
                              style: typography.caption)
                        else if (_selectedVersion != null)
                          Text(_selectedVersion!, style: typography.caption),
                      ],
                    ),
            ),
          ),
        ],
      ),
    );
  }

  GameAccount? _accountOf(String? uuid) {
    for (final account in _accounts) {
      if (account.uuid == uuid) return account;
    }
    return _accounts.isEmpty ? null : _accounts.first;
  }

  // ── 底部：运行日志 ──

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
                  onPressed: () =>
                      ref.read(gameSessionProvider.notifier).clearLogs(),
                  child: const Text('清空'),
                ),
              ],
            ]),
          ),
          Container(
            height: 1,
            color: FluentTheme.of(context).resources.cardStrokeColorDefault,
          ),
          Container(
            height: 220,
            color: FluentTheme.of(context).resources.controlFillColorDisabled,
            child: session.logs.isEmpty
                ? Center(child: Text('暂无日志', style: typography.caption))
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

  static String _formatTime(String iso) {
    final parsed = DateTime.tryParse(iso);
    if (parsed == null) return iso;
    final local = parsed.toLocal();
    String two(int n) => n.toString().padLeft(2, '0');
    return '${local.year}-${two(local.month)}-${two(local.day)}';
  }
}
