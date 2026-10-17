/// 首页：选版本 → 选账户 → 启动 → 实时日志。
///
/// 由 `starcloud.home` 插件贡献（导航项 + 页面），按契约消费：
/// MinecraftCatalog（已装列表）/ GameLauncher（启动）/ AccountStore（玩家）/ LauncherSettings（内存）。
library;

import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/minecraft.dart';

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
  final List<String> _logTail = [];
  bool _launching = false;
  String? _status;
  StreamSubscription<String>? _logSub;

  KernelDeps get _deps => ref.read(kernelDepsProvider);

  @override
  void initState() {
    super.initState();
    _reload();
  }

  @override
  void dispose() {
    unawaited(_logSub?.cancel());
    super.dispose();
  }

  Future<void> _reload() async {
    final versions = await _deps.catalog.installedVersions();
    final accounts = _deps.accounts.accounts();
    if (!mounted) return;
    setState(() {
      _versions = versions;
      _accounts = accounts;
      _selectedVersion ??=
          versions.isNotEmpty ? versions.first.id : null;
      _selectedAccount ??= _deps.accounts.current()?.uuid ??
          (accounts.isNotEmpty ? accounts.first.uuid : null);
    });
  }

  Future<void> _launch() async {
    if (_selectedVersion == null) {
      setState(() => _status = '没有已安装的版本 —— 先去「下载」页装一个');
      return;
    }
    if (_selectedAccount == null) {
      setState(() => _status = '还没有游戏账户 —— 先去「账户」页添加离线账户');
      return;
    }
    final account = _accounts.firstWhere((a) => a.uuid == _selectedAccount);
    setState(() {
      _launching = true;
      _status = '启动中…';
      _logTail.clear();
    });
    try {
      final session = await _deps.launcher.launch(
        versionId: _selectedVersion!,
        playerName: account.name,
      );
      setState(() => _status = '游戏运行中（pid ${session.pid}）');
      _logSub = session.logLines.listen((line) {
        if (!mounted) return;
        setState(() {
          _logTail.add(line);
          if (_logTail.length > 200) {
            _logTail.removeRange(0, _logTail.length - 200);
          }
        });
      }, onError: (Object e) {
        if (!mounted) return;
        setState(() => _status = '日志流中断：$e');
      });
      unawaited(session.exitCode.then((code) {
        if (!mounted) return;
        setState(() {
          _launching = false;
          _status = '游戏退出（exit=$code）';
        });
      }).catchError((Object e) {
        if (!mounted) return;
        setState(() {
          _launching = false;
          _status = '游戏进程异常：$e';
        });
      }));
    } catch (e) {
      setState(() {
        _launching = false;
        _status = '启动失败：$e';
      });
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
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
                    onPressed: _launching ? null : _launch,
                    child: Padding(
                      padding: const EdgeInsets.symmetric(vertical: 8, horizontal: 12),
                      child: Text(_launching ? '启动中…' : '启动游戏',
                          style: typography.bodyStrong),
                    ),
                  ),
                ]),
                if (_status != null) ...[
                  const SizedBox(height: 10),
                  Text(_status!, style: typography.caption),
                ],
              ],
            ),
          ),
          if (_logTail.isNotEmpty) ...[
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
                      for (final line in _logTail)
                        Text(line,
                            style: typography.caption?.copyWith(fontFamily: 'monospace')),
                    ],
                  ),
                ),
              ),
            ),
          ],
          if (_versions.isEmpty) ...[
            const SizedBox(height: 12),
            InfoBar(
              title: const Text('还没有已安装的版本'),
              content: const Text('去「下载」页选择一个版本安装；或把已有的 .minecraft 放到设置里指定的目录。'),
              severity: InfoBarSeverity.warning,
              isLong: true,
            ),
          ],
        ],
      ),
    );
  }
}
