/// 插件诊断页（规范 §4.1 的可观测性要求，也是"万物皆插件"的直观展示）。
///
/// 它自己由 `starcloud.plugin_manager` 插件贡献；停用该插件，这一页与对应导航项
/// 会一起消失。页面直接读内核快照，并演示启停/重试 API。
library;

import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../bootstrap.dart';
import '../../contracts/plugins.dart';
import '../../kernel/descriptor.dart';
import '../../kernel/plugin_host.dart';
import 'plugin_view.dart';

class PluginDiagnosticsPage extends ConsumerStatefulWidget {
  const PluginDiagnosticsPage({super.key});

  @override
  ConsumerState<PluginDiagnosticsPage> createState() =>
      _PluginDiagnosticsPageState();
}

class _PluginDiagnosticsPageState extends ConsumerState<PluginDiagnosticsPage> {
  List<PluginSnapshot> _snapshots = const [];
  String? _lastAction;
  List<ExternalPlugin> _external = const [];
  bool _loadingExternal = true;
  String? _externalError;
  String? _externalNotice;
  final _installPath = TextEditingController();

  PluginStore get _store => ref.read(kernelDepsProvider).plugins;

  @override
  void initState() {
    super.initState();
    _refresh();
    unawaited(_loadExternal());
  }

  @override
  void dispose() {
    _installPath.dispose();
    super.dispose();
  }

  void _refresh() {
    if (!mounted) return;
    setState(() => _snapshots = ref.read(kernelProvider).snapshots());
  }

  Future<void> _loadExternal() async {
    try {
      final list = await _store.list();
      if (!mounted) return;
      setState(() {
        _external = list;
        _loadingExternal = false;
        _externalError = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingExternal = false;
        _externalError = '$e';
      });
    }
  }

  Future<void> _install() async {
    final path = _installPath.text.trim();
    if (path.isEmpty) return;
    setState(() {
      _externalError = null;
      _externalNotice = null;
    });
    try {
      final plugin = await _store.install(path);
      if (!mounted) return;
      _installPath.clear();
      setState(() => _externalNotice = '已安装 ${plugin.name} ${plugin.version}');
      await _loadExternal();
    } catch (e) {
      if (!mounted) return;
      setState(() => _externalError = '安装失败：$e');
    }
  }

  Future<void> _toggle(ExternalPlugin plugin, bool enabled) async {
    setState(() {
      _externalError = null;
      _externalNotice = null;
    });
    try {
      if (enabled && !plugin.official && !plugin.disclaimerAccepted) {
        final accepted = await showDialog<bool>(
          context: context,
          builder: (context) => ContentDialog(
            title: const Text('启用第三方插件？'),
            content: const Text(
              '插件由第三方独立开发，启动器不对其内容与合法性负责。\n\n'
              'Linux 端当前只管理插件包与启用状态；插件运行时视图（WebView）'
              '由 Windows 宿主提供。',
            ),
            actions: [
              Button(
                onPressed: () => Navigator.pop(context, false),
                child: const Text('取消'),
              ),
              FilledButton(
                onPressed: () => Navigator.pop(context, true),
                child: const Text('确认并启用'),
              ),
            ],
          ),
        );
        if (accepted != true || !mounted) return;
        await _store.setEnabled(plugin.id, true, acceptDisclaimer: true);
      } else {
        await _store.setEnabled(plugin.id, enabled);
      }
      if (!mounted) return;
      setState(() =>
          _externalNotice = enabled ? '已启用 ${plugin.name}' : '已停用 ${plugin.name}');
      await _loadExternal();
    } catch (e) {
      if (!mounted) return;
      setState(() => _externalError = '$e');
    }
  }

  Future<void> _uninstall(ExternalPlugin plugin) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => ContentDialog(
        title: Text('卸载 ${plugin.name}？'),
        content: const Text('插件目录与启用状态会被删除，不可恢复。'),
        actions: [
          Button(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('取消'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('卸载'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    try {
      await _store.uninstall(plugin.id);
      if (!mounted) return;
      setState(() => _externalNotice = '已卸载 ${plugin.name}');
      await _loadExternal();
    } catch (e) {
      if (!mounted) return;
      setState(() => _externalError = '$e');
    }
  }

  Future<void> _openFolder(ExternalPlugin plugin) async {
    try {
      await _store.openFolder(plugin.id);
    } catch (e) {
      if (!mounted) return;
      setState(() => _externalError = '$e');
    }
  }

  Future<void> _openView(ExternalPlugin plugin) async {
    setState(() {
      _externalError = null;
      _externalNotice = null;
    });
    try {
      final panels = await _store.panels(plugin.id);
      if (!mounted) return;
      if (panels.isEmpty) {
        setState(() => _externalError = '插件没有声明式面板');
        return;
      }
      if (!panels.first.declarative) {
        setState(() =>
            _externalError = '该面板是 HTML 视图，需要 WebView 宿主（暂不支持）');
        return;
      }
      final data = await _store.view(plugin.id, panels.first.id);
      if (!mounted) return;
      final node = data.node;
      if (node == null) {
        setState(() =>
            _externalError = '视图解析失败：${data.errors.join('；')}');
        return;
      }
      await showDialog<void>(
        context: context,
        builder: (context) => ContentDialog(
          title: Text(panels.first.title),
          content: SizedBox(
            width: 760,
            height: 560,
            child: PluginViewRenderer(
              node: node,
              bindings: data.bindings,
              onInvoke: (api, args) => _store.invoke(plugin.id, api, args),
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
      setState(() => _externalError = '$e');
    }
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
              onPressed: () {
                _refresh();
                unawaited(_loadExternal());
              },
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
                Text('外部插件（v2 沙箱）', style: typography.subtitle),
                const SizedBox(height: 6),
                Text(
                  '安装 .mclplug、启停与卸载；权限/依赖按清单展示。'
                  'JS / Python / 纯声明式运行时已在本进程启动，声明式视图直接渲染；'
                  '1.x HTML 面板需要 WebView 宿主（暂不支持）。',
                  style: typography.caption,
                ),
                const SizedBox(height: 10),
                Row(children: [
                  Expanded(
                    child: TextBox(
                      controller: _installPath,
                      placeholder: '.mclplug 安装包绝对路径',
                      onSubmitted: (_) => _install(),
                    ),
                  ),
                  const SizedBox(width: 8),
                  FilledButton(
                    onPressed: _install,
                    child: const Text('安装'),
                  ),
                ]),
                if (_externalError != null) ...[
                  const SizedBox(height: 8),
                  InfoBar(
                    title: const Text('操作失败'),
                    content: Text(_externalError!),
                    severity: InfoBarSeverity.error,
                    isLong: true,
                    onClose: () => setState(() => _externalError = null),
                  ),
                ],
                if (_externalNotice != null) ...[
                  const SizedBox(height: 8),
                  InfoBar(
                    title: Text(_externalNotice!),
                    severity: InfoBarSeverity.success,
                    isLong: true,
                    onClose: () => setState(() => _externalNotice = null),
                  ),
                ],
                if (_loadingExternal)
                  const Padding(
                    padding: EdgeInsets.all(16),
                    child: Center(child: ProgressRing()),
                  )
                else if (_external.isEmpty)
                  const Padding(
                    padding: EdgeInsets.only(top: 8),
                    child: Text('还没有安装外部插件。'),
                  )
                else
                  for (final plugin in _external)
                    _externalCard(plugin, typography),
              ],
            ),
          ),
          const SizedBox(height: 12),
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

  Widget _externalCard(ExternalPlugin plugin, Typography typography) {
    return Padding(
      padding: const EdgeInsets.only(top: 10),
      child: Card(
        padding: const EdgeInsets.all(12),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Wrap(
              spacing: 8,
              runSpacing: 2,
              crossAxisAlignment: WrapCrossAlignment.center,
              children: [
                Text('${plugin.name} ${plugin.version}',
                    style: typography.bodyStrong),
                Text('[${plugin.id}]', style: typography.caption),
                Text(plugin.official ? '[官方]' : '[未验证]',
                    style: typography.caption),
                Text(plugin.enabled ? '[已启用]' : '[已停用]',
                    style: typography.caption),
              ],
            ),
            const SizedBox(height: 4),
            Text(plugin.description, style: typography.body),
            Text(
              '作者 ${plugin.author} · api ${plugin.apiVersion}'
              '${plugin.runtime != null ? ' · ${plugin.runtime}' : ''}',
              style: typography.caption,
            ),
            if (plugin.permissions.isNotEmpty) ...[
              const SizedBox(height: 6),
              Wrap(
                spacing: 6,
                runSpacing: 4,
                children: [
                  for (final permission in plugin.permissions)
                    Text('[$permission]', style: typography.caption),
                ],
              ),
            ],
            if (plugin.dependencies.isNotEmpty)
              Text(
                '依赖：${plugin.dependencies.entries.map((e) => '${e.key} ${e.value}').join('、')}',
                style: typography.caption,
              ),
            const SizedBox(height: 8),
            Wrap(spacing: 8, runSpacing: 8, children: [
              FilledButton(
                onPressed: () => _toggle(plugin, !plugin.enabled),
                child: Text(plugin.enabled ? '停用' : '启用'),
              ),
              if (plugin.panelCount > 0)
                Button(
                  onPressed:
                      plugin.enabled ? () => _openView(plugin) : null,
                  child: const Text('打开视图'),
                ),
              Button(
                onPressed: () => _uninstall(plugin),
                child: const Text('卸载'),
              ),
              Button(
                onPressed: () => _openFolder(plugin),
                child: const Text('打开目录'),
              ),
            ]),
          ],
        ),
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
