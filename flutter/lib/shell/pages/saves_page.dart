/// 存档页：跨版本世界列表 + 打开目录 / 备份 / 删除。
///
/// 扫描与 level.dat 解析在 C#（SaveLibraryService），共享根与各隔离版本根
/// 会合并成一份列表；删除是不可逆操作，UI 必须二次确认。
library;

import 'dart:async';
import 'dart:io' as io;

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/minecraft.dart';

class SavesPage extends ConsumerStatefulWidget {
  const SavesPage({super.key});

  @override
  ConsumerState<SavesPage> createState() => _SavesPageState();
}

class _SavesPageState extends ConsumerState<SavesPage> {
  List<SaveInfo> _saves = const [];
  bool _loading = true;
  String? _error;
  String? _notice;
  String? _busyKey;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  Future<void> _load() async {
    final catalog = ref.read(kernelDepsProvider).saves;
    try {
      final saves = await catalog.listSaves();
      if (!mounted) return;
      setState(() {
        _saves = saves;
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

  String _key(SaveInfo save) => '${save.gameRoot}|${save.folderName}';

  Future<void> _run(
    SaveInfo save,
    Future<void> Function(SaveCatalog catalog) action, {
    String? success,
  }) async {
    final catalog = ref.read(kernelDepsProvider).saves;
    setState(() {
      _busyKey = _key(save);
      _notice = null;
    });
    try {
      await action(catalog);
      if (!mounted) return;
      if (success != null) setState(() => _notice = success);
      await _load();
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busyKey = null);
    }
  }

  Future<void> _backup(SaveInfo save) async {
    final catalog = ref.read(kernelDepsProvider).saves;
    setState(() {
      _busyKey = _key(save);
      _notice = null;
    });
    try {
      final path = await catalog.backupSave(save);
      if (!mounted) return;
      setState(() => _notice = '已备份到 $path');
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '备份失败：$e');
    } finally {
      if (mounted) setState(() => _busyKey = null);
    }
  }

  Future<void> _confirmDelete(SaveInfo save) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => ContentDialog(
        title: Text('删除世界「${save.levelName}」？'),
        content: const Text('世界目录会被永久删除，且不可恢复。需要保留请先点「备份」。'),
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
      await _run(save, (catalog) => catalog.deleteSave(save), success: '世界已删除');
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    return ScaffoldPage(
      header: PageHeader(
        title: const Text('存档'),
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
            if (_notice != null) ...[
              InfoBar(
                title: Text(_notice!),
                severity: InfoBarSeverity.success,
                isLong: true,
                onClose: () => setState(() => _notice = null),
              ),
              const SizedBox(height: 12),
            ],
            if (_loading)
              const Expanded(child: Center(child: ProgressRing()))
            else if (_saves.isEmpty)
              Expanded(
                child: Center(
                  child: Text(
                    '还没有存档。进游戏创建一个世界后，这里会自动出现。',
                    style: typography.body,
                  ),
                ),
              )
            else
              Expanded(
                child: ListView.builder(
                  itemCount: _saves.length,
                  itemBuilder: (context, index) =>
                      _saveCard(_saves[index], typography),
                ),
              ),
          ],
        ),
      ),
    );
  }

  Widget _saveCard(SaveInfo save, Typography typography) {
    final busy = _busyKey != null;
    final iconPath = save.iconPath;
    final hasIcon = iconPath != null && io.File(iconPath).existsSync();
    return Padding(
      padding: const EdgeInsets.only(bottom: 8),
      child: Card(
        padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 12),
        child: Row(children: [
          SizedBox(
            width: 48,
            height: 48,
            child: hasIcon
                ? ClipRRect(
                    borderRadius: BorderRadius.circular(4),
                    child: Image.file(io.File(iconPath), fit: BoxFit.cover),
                  )
                : const Icon(FluentIcons.globe),
          ),
          const SizedBox(width: 12),
          Expanded(
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Wrap(
                  crossAxisAlignment: WrapCrossAlignment.center,
                  spacing: 8,
                  runSpacing: 2,
                  children: [
                    Text(save.levelName, style: typography.bodyStrong),
                    Text(
                      save.versionId.isEmpty ? '[共享]' : '[${save.versionId}]',
                      style: typography.caption,
                    ),
                    if (save.isolated)
                      Text('[隔离]', style: typography.caption),
                  ],
                ),
                const SizedBox(height: 2),
                Text(
                  '${_formatSize(save.sizeBytes)}'
                  '${save.lastPlayedAt != null ? ' · 最近游玩 ${_formatTime(save.lastPlayedAt!)}' : ''}'
                  ' · ${save.folderName}',
                  style: typography.caption,
                ),
              ],
            ),
          ),
          const SizedBox(width: 12),
          Button(
            onPressed: busy
                ? null
                : () => _run(
                    save,
                    (catalog) => catalog.openSaveFolder(save),
                  ),
            child: const Text('打开目录'),
          ),
          const SizedBox(width: 8),
          Button(
            onPressed: busy ? null : () => _backup(save),
            child: Text(_busyKey == _key(save) ? '处理中…' : '备份'),
          ),
          const SizedBox(width: 8),
          Button(
            onPressed: busy ? null : () => _confirmDelete(save),
            child: const Text('删除'),
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
