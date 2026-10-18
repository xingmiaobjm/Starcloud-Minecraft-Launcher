/// 联机页：陶瓦联机 / 第三方 SDK 的归一化状态与操作。
///
/// 页面不解析任何 SDK 协议：状态来自 `MultiplayerControl`，点击翻译成一次操作。
library;

import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/multiplayer.dart';

class MultiplayerPage extends ConsumerStatefulWidget {
  const MultiplayerPage({super.key});

  @override
  ConsumerState<MultiplayerPage> createState() => _MultiplayerPageState();
}

class _MultiplayerPageState extends ConsumerState<MultiplayerPage> {
  MultiplayerSnapshot? _snapshot;
  bool _loading = true;
  String? _error;
  String? _busy;
  String _installProgress = '';
  StreamSubscription<MultiplayerSnapshot>? _sub;
  final _nickname = TextEditingController();

  MultiplayerControl get _control => ref.read(kernelDepsProvider).multiplayer;

  @override
  void initState() {
    super.initState();
    _sub = _control.changes.listen(_apply);
    unawaited(_load());
  }

  @override
  void dispose() {
    unawaited(_sub?.cancel());
    _nickname.dispose();
    super.dispose();
  }

  Future<void> _load() async {
    try {
      final snapshot = await _control.snapshot();
      if (!mounted) return;
      _apply(snapshot);
      setState(() => _loading = false);
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _error = '$e';
      });
    }
  }

  void _apply(MultiplayerSnapshot snapshot) {
    if (!mounted) return;
    setState(() {
      _snapshot = snapshot;
      if (_nickname.text.isEmpty) _nickname.text = snapshot.nickname;
    });
  }

  Future<void> _run(
    String label,
    Future<MultiplayerSnapshot> Function(MultiplayerControl control) action,
  ) async {
    setState(() {
      _busy = label;
      _error = null;
    });
    try {
      final snapshot = await action(_control);
      _apply(snapshot);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busy = null);
    }
  }

  Future<void> _install() async {
    setState(() {
      _busy = '安装核心';
      _installProgress = '准备下载…';
      _error = null;
    });
    try {
      await _control.installCore(onProgress: (progress) {
        if (!mounted) return;
        setState(() {
          _installProgress = progress.total != null && progress.total! > 0
              ? '${progress.stage}（${(progress.bytes / 1048576).toStringAsFixed(1)}'
                  '/${(progress.total! / 1048576).toStringAsFixed(1)} MB）'
              : progress.stage;
        });
      });
      if (!mounted) return;
      await _load();
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '安装失败：$e');
    } finally {
      if (mounted) setState(() => _busy = null);
    }
  }

  Future<void> _join() async {
    final controller = TextEditingController();
    final room = await showDialog<String>(
      context: context,
      builder: (context) => ContentDialog(
        title: const Text('加入房间'),
        content: SizedBox(
          width: 360,
          child: TextBox(
            controller: controller,
            placeholder: '粘贴房主分享的邀请码',
            onSubmitted: (v) => Navigator.pop(context, v.trim()),
          ),
        ),
        actions: [
          Button(
            onPressed: () => Navigator.pop(context),
            child: const Text('取消'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, controller.text.trim()),
            child: const Text('加入'),
          ),
        ],
      ),
    );
    controller.dispose();
    if (room == null || room.isEmpty || !mounted) return;
    await _run('加入房间', (control) => control.join(room));
  }

  Future<void> _showLogs() async {
    try {
      final logs = await _control.logs();
      if (!mounted) return;
      await showDialog<void>(
        context: context,
        builder: (context) => ContentDialog(
          title: const Text('联机日志'),
          content: SizedBox(
            width: 640,
            height: 420,
            child: SingleChildScrollView(
              child: SelectableText(logs.isEmpty ? '暂无日志' : logs),
            ),
          ),
          actions: [
            Button(
              onPressed: () => Navigator.pop(context),
              child: const Text('关闭'),
            ),
          ],
        ),
      );
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    final snapshot = _snapshot;
    if (_loading || snapshot == null) {
      return ScaffoldPage(
        header: PageHeader(title: const Text('联机')),
        content: _error == null
            ? const Center(child: ProgressRing())
            : Center(child: Text(_error!)),
      );
    }

    final busy = _busy != null || snapshot.state.isBusy;
    final state = snapshot.state;
    return ScaffoldPage(
      header: PageHeader(
        title: const Text('联机'),
        commandBar: CommandBar(
          mainAxisAlignment: MainAxisAlignment.end,
          primaryItems: [
            CommandBarButton(
              icon: const Icon(FluentIcons.refresh),
              label: const Text('刷新'),
              onPressed: _busy == null ? _load : null,
            ),
            CommandBarButton(
              icon: const Icon(FluentIcons.history),
              label: const Text('日志'),
              onPressed: _busy == null ? _showLogs : null,
            ),
          ],
        ),
      ),
      content: Padding(
        padding: const EdgeInsets.fromLTRB(24, 0, 24, 16),
        child: ListView(children: [
          if (_error != null) ...[
            InfoBar(
              title: const Text('操作失败'),
              content: Text(_error!),
              severity: InfoBarSeverity.error,
              isLong: true,
              onClose: () => setState(() => _error = null),
            ),
            const SizedBox(height: 12),
          ],
          Card(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('提供方', style: typography.subtitle),
                const SizedBox(height: 8),
                Row(children: [
                  Expanded(
                    child: ComboBox<String>(
                      value: snapshot.activeId,
                      items: [
                        for (final provider in snapshot.providers)
                          ComboBoxItem(
                            value: provider.id,
                            child: Text(provider.displayName),
                          ),
                      ],
                      onChanged: busy
                          ? null
                          : (id) {
                              if (id != null && id != snapshot.activeId) {
                                unawaited(_run(
                                  '切换提供方',
                                  (control) => control.selectProvider(id),
                                ));
                              }
                            },
                    ),
                  ),
                  const SizedBox(width: 12),
                  Text(
                    snapshot.core.installed
                        ? '核心已安装${snapshot.core.version != null ? '（${snapshot.core.version}）' : ''}'
                        : '核心未安装',
                    style: typography.caption,
                  ),
                ]),
                const SizedBox(height: 12),
                Row(children: [
                  Expanded(
                    child: InfoLabel(
                      label: '联机玩家名',
                      child: TextBox(
                        controller: _nickname,
                        placeholder: '留空 = 跟随当前账户名',
                      ),
                    ),
                  ),
                  const SizedBox(width: 12),
                  Button(
                    onPressed: busy
                        ? null
                        : () => _run(
                              '保存玩家名',
                              (control) =>
                                  control.setNickname(_nickname.text.trim()),
                            ),
                    child: const Text('保存'),
                  ),
                  const SizedBox(width: 12),
                  InfoLabel(
                    label: '节点地区',
                    child: ComboBox<String>(
                      value: snapshot.nodeRegion,
                      items: const [
                        ComboBoxItem(value: 'auto', child: Text('自动')),
                        ComboBoxItem(value: 'cn', child: Text('国内')),
                        ComboBoxItem(value: 'global', child: Text('海外')),
                      ],
                      onChanged: busy
                          ? null
                          : (region) {
                              if (region != null) {
                                unawaited(_run(
                                  '切换节点',
                                  (control) =>
                                      control.setNodeRegion(region),
                                ));
                              }
                            },
                    ),
                  ),
                ]),
              ],
            ),
          ),
          const SizedBox(height: 12),
          Card(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('状态', style: typography.subtitle),
                const SizedBox(height: 8),
                InfoBar(
                  title: Text(_phaseLabel(state.phase)),
                  content: Text(_phaseDetail(snapshot)),
                  severity: _severity(state.phase),
                  isLong: true,
                ),
                if (state.room != null) ...[
                  const SizedBox(height: 8),
                  Row(children: [
                    Text('邀请码：', style: typography.bodyStrong),
                    SelectableText(state.room!, style: typography.title),
                  ]),
                ],
                if (state.players.isNotEmpty) ...[
                  const SizedBox(height: 8),
                  Text('房间玩家：${state.players.join('、')}',
                      style: typography.caption),
                ],
                if (_installProgress.isNotEmpty && _busy == '安装核心') ...[
                  const SizedBox(height: 8),
                  Text(_installProgress, style: typography.caption),
                ],
                const SizedBox(height: 12),
                Wrap(spacing: 8, runSpacing: 8, children: [
                  if (!snapshot.core.installed)
                    FilledButton(
                      onPressed: busy ? null : _install,
                      child: const Text('安装联机核心'),
                    ),
                  FilledButton(
                    onPressed: busy || !snapshot.core.installed
                        ? null
                        : () => _run('创建房间', (control) => control.host()),
                    child: const Text('创建房间'),
                  ),
                  Button(
                    onPressed: busy || !snapshot.core.installed ? null : _join,
                    child: const Text('加入房间'),
                  ),
                  Button(
                    onPressed: busy || !snapshot.core.sessionRunning
                        ? null
                        : () => _run('停止联机', (control) => control.stop()),
                    child: const Text('停止联机'),
                  ),
                ]),
              ],
            ),
          ),
          const SizedBox(height: 12),
          Card(
            padding: const EdgeInsets.all(16),
            child: Text(
              '房主：在游戏内对局域网开放世界后点「创建房间」，把邀请码发给朋友。\n'
              '加入方：输入邀请码后进入虚拟局域网，在游戏的多人列表里即可看到房主世界。',
              style: typography.body,
            ),
          ),
        ]),
      ),
    );
  }

  static String _phaseLabel(MultiplayerPhase phase) => switch (phase) {
        MultiplayerPhase.idle => '空闲',
        MultiplayerPhase.hostScanning => '正在扫描局域网世界',
        MultiplayerPhase.hostStarting => '房间启动中',
        MultiplayerPhase.hostReady => '房间已开启',
        MultiplayerPhase.guestConnecting => '正在连接房间',
        MultiplayerPhase.guestEntering => '正在进入房间',
        MultiplayerPhase.guestReady => '联机成功',
        MultiplayerPhase.failed => '联机出错',
      };

  static String _phaseDetail(MultiplayerSnapshot snapshot) {
    final state = snapshot.state;
    if (state.error != null) return state.error!;
    if (snapshot.lastError != null) return snapshot.lastError!;
    if (state.address != null) return '虚拟局域网地址：${state.address}';
    if (state.phase == MultiplayerPhase.hostScanning) {
      return '请在游戏内「对局域网开放」一个世界。';
    }
    if (state.phase == MultiplayerPhase.idle && !snapshot.core.installed) {
      return '首次使用需要先安装联机核心。';
    }
    return '核心${snapshot.core.sessionRunning ? '运行中' : '未运行'}。';
  }

  static InfoBarSeverity _severity(MultiplayerPhase phase) => switch (phase) {
        MultiplayerPhase.failed => InfoBarSeverity.error,
        MultiplayerPhase.hostReady || MultiplayerPhase.guestReady =>
          InfoBarSeverity.success,
        MultiplayerPhase.idle => InfoBarSeverity.info,
        _ => InfoBarSeverity.warning,
      };
}
