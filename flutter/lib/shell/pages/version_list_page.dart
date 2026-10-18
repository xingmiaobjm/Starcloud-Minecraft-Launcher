/// 版本页：已安装版本列表 + 打开目录 / 设为默认 / 删除。
///
/// 所有操作都经 `MinecraftCatalog` 契约落到 C# 核心；删除的安全检查
/// （运行中、被 inheritsFrom 依赖、路径穿越）在宿主侧完成，UI 只负责提示。
library;

import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/minecraft.dart';

class VersionListPage extends ConsumerStatefulWidget {
  const VersionListPage({super.key});

  @override
  ConsumerState<VersionListPage> createState() => _VersionListPageState();
}

class _VersionListPageState extends ConsumerState<VersionListPage> {
  List<InstalledVersion> _versions = const [];
  bool _loading = true;
  String? _error;
  String? _busyId;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  Future<void> _load() async {
    final catalog = ref.read(kernelDepsProvider).catalog;
    try {
      final versions = await catalog.installedVersions();
      if (!mounted) return;
      setState(() {
        _versions = versions;
        _loading = false;
        _error = null;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _error = '$e';
      });
    }
  }

  Future<void> _run(
    String id,
    Future<void> Function(MinecraftCatalog catalog) action,
  ) async {
    final catalog = ref.read(kernelDepsProvider).catalog;
    setState(() => _busyId = id);
    try {
      await action(catalog);
      if (!mounted) return;
      await _load();
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busyId = null);
    }
  }

  Future<void> _confirmDelete(InstalledVersion version) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => ContentDialog(
        title: Text('删除 ${version.id}？'),
        content: Text(version.isolated
            ? '该版本启用了隔离：版本目录里的存档、mods、配置会一起删除，且不可恢复。'
            : '只删除版本目录；共享的 libraries/assets 与 .minecraft 根下的存档不受影响。'),
        actions: [
          Button(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('取消'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('删除'),
          ),
        ],
      ),
    );
    if (confirmed == true && mounted) {
      await _run(version.id, (catalog) => catalog.deleteVersion(version.id));
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    return ScaffoldPage(
      header: PageHeader(
        title: const Text('版本'),
        commandBar: CommandBar(
          mainAxisAlignment: MainAxisAlignment.end,
          primaryItems: [
            CommandBarButton(
              icon: const Icon(FluentIcons.refresh),
              label: const Text('刷新'),
              onPressed: _loading ? null : _load,
            ),
          ],
        ),
      ),
      content: Padding(
        padding: const EdgeInsets.fromLTRB(24, 0, 24, 16),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
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
            if (_loading)
              const Expanded(child: Center(child: ProgressRing()))
            else if (_versions.isEmpty)
              Expanded(
                child: Center(
                  child: Text(
                    '还没有已安装的版本 —— 去「下载」页装一个。',
                    style: typography.body,
                  ),
                ),
              )
            else
              Expanded(
                child: ListView.builder(
                  itemCount: _versions.length,
                  itemBuilder: (context, index) =>
                      _versionCard(_versions[index], typography),
                ),
              ),
          ],
        ),
      ),
    );
  }

  Widget _versionCard(InstalledVersion version, Typography typography) {
    final busy = _busyId != null;
    final tags = <String>[
      if (version.isDefault) '默认',
      if (version.isolated) '隔离',
      if (version.isModded) version.modLoader ?? 'modded',
      if (version.inheritsFrom != null) '继承 ${version.inheritsFrom}',
    ];
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Card(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        child: Row(children: [
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Row(children: [
                  Text(version.id, style: typography.bodyStrong),
                  const SizedBox(width: 8),
                  for (final tag in tags)
                    Padding(
                      padding: const EdgeInsets.only(right: 6),
                      child: Text('[$tag]', style: typography.caption),
                    ),
                ]),
                const SizedBox(height: 2),
                Text(
                  '${_formatSize(version.sizeBytes)}'
                  '${version.lastPlayedAt != null ? ' · 最近游玩 ${_formatTime(version.lastPlayedAt!)}' : ''}'
                  '${version.jarExists ? '' : ' · 缺少客户端 jar'}',
                  style: typography.caption,
                ),
                Text(version.directory, style: typography.caption),
              ],
            ),
          ),
          const SizedBox(width: 12),
          Button(
            onPressed: busy
                ? null
                : () => _run(
                    version.id,
                    (catalog) => catalog.openVersionFolder(version.id),
                  ),
            child: const Text('打开目录'),
          ),
          const SizedBox(width: 8),
          Button(
            onPressed: busy || version.isDefault
                ? null
                : () => _run(
                    version.id,
                    (catalog) => catalog.setDefaultVersion(version.id),
                  ),
            child: const Text('设为默认'),
          ),
          const SizedBox(width: 8),
          Button(
            onPressed: busy ? null : () => _confirmDelete(version),
            child: Text(_busyId == version.id ? '处理中…' : '删除'),
          ),
        ]),
      ),
    );
  }

  static String _formatSize(int bytes) {
    if (bytes <= 0) return '大小未知';
    if (bytes < 1024 * 1024) return '${(bytes / 1024).toStringAsFixed(0)} KB';
    return '${(bytes / 1024 / 1024).toStringAsFixed(1)} MB';
  }

  static String _formatTime(String iso) {
    final parsed = DateTime.tryParse(iso);
    if (parsed == null) return iso;
    final local = parsed.toLocal();
    String two(int n) => n.toString().padLeft(2, '0');
    return '${local.year}-${two(local.month)}-${two(local.day)} '
        '${two(local.hour)}:${two(local.minute)}';
  }
}
