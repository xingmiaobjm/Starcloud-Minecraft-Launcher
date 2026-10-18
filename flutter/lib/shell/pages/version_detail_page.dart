/// 版本详情页：实例信息 + 模组/资源包/光影管理 + Modrinth 搜索安装。
///
/// 本地文件与在线安装都经 `ContentManager` 落到 C#；页面只做列表与交互。
library;

import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/content.dart';
import '../../../contracts/minecraft.dart';

class VersionDetailPage extends ConsumerStatefulWidget {
  const VersionDetailPage({
    super.key,
    required this.version,
    required this.onBack,
  });

  final InstalledVersion version;
  final VoidCallback onBack;

  @override
  ConsumerState<VersionDetailPage> createState() => _VersionDetailPageState();
}

class _VersionDetailPageState extends ConsumerState<VersionDetailPage> {
  VersionContent? _content;
  bool _loading = true;
  String? _error;
  String? _notice;
  ContentKind _kind = ContentKind.mod;
  String? _busyFile;

  ContentManager get _manager => ref.read(kernelDepsProvider).content;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  Future<void> _load() async {
    try {
      final content = await _manager.detail(widget.version.id);
      if (!mounted) return;
      setState(() {
        _content = content;
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

  Future<void> _toggle(ContentFile file) async {
    setState(() => _busyFile = file.fileName);
    try {
      final files = await _manager.toggle(
        widget.version.id,
        _kind,
        file.fileName,
        !file.enabled,
      );
      if (!mounted) return;
      _replaceFiles(files);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busyFile = null);
    }
  }

  Future<void> _delete(ContentFile file) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => ContentDialog(
        title: Text('删除 ${file.displayName}？'),
        content: const Text('文件会从版本目录中永久删除，且不可恢复。'),
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
    if (confirmed != true || !mounted) return;
    setState(() => _busyFile = file.fileName);
    try {
      final files = await _manager.delete(
        widget.version.id,
        _kind,
        file.fileName,
      );
      if (!mounted) return;
      _replaceFiles(files);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busyFile = null);
    }
  }

  void _replaceFiles(List<ContentFile> files) {
    final content = _content;
    if (content == null) return;
    setState(() {
      _content = VersionContent(
        versionId: content.versionId,
        gameRoot: content.gameRoot,
        isolated: content.isolated,
        mcVersion: content.mcVersion,
        loader: content.loader,
        loaderVersion: content.loaderVersion,
        lastPlayedAt: content.lastPlayedAt,
        mods: _kind == ContentKind.mod ? files : content.mods,
        resourcepacks:
            _kind == ContentKind.resourcepack ? files : content.resourcepacks,
        shaderpacks:
            _kind == ContentKind.shader ? files : content.shaderpacks,
      );
    });
  }

  Future<void> _openFolder() async {
    try {
      await _manager.openFolder(widget.version.id, _kind);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    }
  }

  Future<void> _install() async {
    final content = _content;
    if (content == null) return;
    final installed = await showDialog<bool>(
      context: context,
      builder: (context) => _ContentSearchDialog(
        versionId: widget.version.id,
        mcVersion: content.mcVersion,
        loader: content.loader,
        kind: _kind,
      ),
    );
    if (installed == true && mounted) {
      setState(() => _notice = '安装完成');
      await _load();
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    final content = _content;
    return ScaffoldPage(
      header: PageHeader(
        title: Row(children: [
          IconButton(
            icon: const Icon(FluentIcons.back),
            onPressed: widget.onBack,
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Text(
              '版本详情 · ${widget.version.id}',
              overflow: TextOverflow.ellipsis,
            ),
          ),
        ]),
        commandBar: CommandBar(
          mainAxisAlignment: MainAxisAlignment.end,
          primaryItems: [
            CommandBarButton(
              icon: const Icon(FluentIcons.folder_open),
              label: const Text('打开目录'),
              onPressed: content == null ? null : _openFolder,
            ),
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
            if (_loading || content == null)
              const Expanded(child: Center(child: ProgressRing()))
            else ...[
              Card(
                padding: const EdgeInsets.all(16),
                child: Wrap(
                  spacing: 24,
                  runSpacing: 8,
                  children: [
                    _info(typography, '游戏版本', content.mcVersion),
                    _info(typography, '加载器',
                        content.loaderVersion == null
                            ? content.loader
                            : '${content.loader} ${content.loaderVersion}'),
                    _info(typography, '隔离', content.isolated ? '是' : '否'),
                    _info(typography, '目录', content.gameRoot),
                    _info(typography, '最近游玩',
                        content.lastPlayedAt == null
                            ? '未记录'
                            : _formatTime(content.lastPlayedAt!)),
                  ],
                ),
              ),
              const SizedBox(height: 12),
              Wrap(
                spacing: 8,
                runSpacing: 8,
                crossAxisAlignment: WrapCrossAlignment.center,
                children: [
                  for (final kind in ContentKind.values)
                    _kind == kind
                        ? FilledButton(
                            onPressed: () => setState(() => _kind = kind),
                            child: Text(kind.label),
                          )
                        : Button(
                            onPressed: () => setState(() => _kind = kind),
                            child: Text(kind.label),
                          ),
                  FilledButton(
                    onPressed: _install,
                    child: const Text('安装内容'),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              Expanded(child: _fileList(content, typography)),
            ],
          ],
        ),
      ),
    );
  }

  Widget _info(Typography typography, String label, String value) {
    return SizedBox(
      width: 220,
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(label, style: typography.caption),
          Text(value, style: typography.body),
        ],
      ),
    );
  }

  Widget _fileList(VersionContent content, Typography typography) {
    final files = content.filesFor(_kind);
    if (files.isEmpty) {
      return Center(
        child: Text(
          '还没有${_kind.label}。点「安装内容」从 Modrinth 搜索安装。',
          style: typography.body,
        ),
      );
    }
    return ListView.builder(
      itemCount: files.length,
      itemBuilder: (context, index) {
        final file = files[index];
        final busy = _busyFile != null;
        return Padding(
          padding: const EdgeInsets.only(bottom: 8),
          child: Card(
            padding: const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
            child: Row(children: [
              Icon(file.enabled ? FluentIcons.checkbox : FluentIcons.circle_ring),
              const SizedBox(width: 12),
              Expanded(
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(file.displayName, style: typography.bodyStrong),
                    Text(
                      '${_formatSize(file.sizeBytes)}'
                      '${file.enabled ? '' : ' · 已停用'}',
                      style: typography.caption,
                    ),
                  ],
                ),
              ),
              const SizedBox(width: 12),
              Button(
                onPressed: busy ? null : () => _toggle(file),
                child: Text(file.enabled ? '停用' : '启用'),
              ),
              const SizedBox(width: 8),
              Button(
                onPressed: busy ? null : () => _delete(file),
                child: Text(_busyFile == file.fileName ? '处理中…' : '删除'),
              ),
            ]),
          ),
        );
      },
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

/// Modrinth 搜索与安装对话框。
class _ContentSearchDialog extends ConsumerStatefulWidget {
  const _ContentSearchDialog({
    required this.versionId,
    required this.mcVersion,
    required this.loader,
    required this.kind,
  });

  final String versionId;
  final String mcVersion;
  final String loader;
  final ContentKind kind;

  @override
  ConsumerState<_ContentSearchDialog> createState() =>
      _ContentSearchDialogState();
}

class _ContentSearchDialogState extends ConsumerState<_ContentSearchDialog> {
  final _query = TextEditingController();
  List<ContentSearchHit> _hits = const [];
  bool _searching = false;
  String? _error;
  String? _stage;
  String? _installing;
  bool _installed = false;

  ContentManager get _manager => ref.read(kernelDepsProvider).content;

  @override
  void dispose() {
    _query.dispose();
    super.dispose();
  }

  Future<void> _search() async {
    setState(() {
      _searching = true;
      _error = null;
    });
    try {
      final hits = await _manager.search(
        kind: widget.kind,
        query: _query.text.trim(),
        gameVersion: widget.mcVersion,
        loader: widget.kind == ContentKind.mod ? widget.loader : null,
      );
      if (!mounted) return;
      setState(() => _hits = hits);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _searching = false);
    }
  }

  Future<void> _install(ContentSearchHit hit) async {
    setState(() {
      _installing = hit.projectId;
      _stage = '准备安装 ${hit.title}…';
      _error = null;
    });
    try {
      final result = await _manager.install(
        kind: widget.kind,
        projectId: hit.projectId,
        title: hit.title,
        targetVersionId: widget.versionId,
        onProgress: (stage) {
          if (!mounted) return;
          setState(() => _stage = stage);
        },
      );
      if (!mounted) return;
      setState(() {
        _installed = true;
        _stage = result.missing.isEmpty
            ? '已安装 ${result.installed.length} 个文件'
            : '已安装 ${result.installed.length} 个；${result.missing.length} 个依赖未找到';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _installing = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    return ContentDialog(
      title: Text('安装${widget.kind.label}（Modrinth）'),
      content: SizedBox(
        width: 720,
        height: 480,
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Row(children: [
              Expanded(
                child: TextBox(
                  controller: _query,
                  placeholder: '搜索${widget.kind.label}（留空 = 热门）',
                  onSubmitted: (_) => unawaited(_search()),
                ),
              ),
              const SizedBox(width: 8),
              FilledButton(
                onPressed: _searching ? null : _search,
                child: Text(_searching ? '搜索中…' : '搜索'),
              ),
            ]),
            const SizedBox(height: 8),
            if (_stage != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Text(_stage!, style: typography.caption),
              ),
            if (_error != null)
              InfoBar(
                title: const Text('出错了'),
                content: Text(_error!),
                severity: InfoBarSeverity.error,
                isLong: true,
              ),
            Expanded(
              child: _hits.isEmpty
                  ? Center(
                      child: Text(
                        _searching ? '正在搜索…' : '输入关键词后点「搜索」',
                        style: typography.body,
                      ),
                    )
                  : ListView.builder(
                      itemCount: _hits.length,
                      itemBuilder: (context, index) {
                        final hit = _hits[index];
                        return Padding(
                          padding: const EdgeInsets.only(bottom: 6),
                          child: Card(
                            padding: const EdgeInsets.symmetric(
                                horizontal: 12, vertical: 8),
                            child: Row(children: [
                              SizedBox(
                                width: 40,
                                height: 40,
                                child: hit.iconUrl == null
                                    ? const Icon(FluentIcons.puzzle)
                                    : Image.network(
                                        hit.iconUrl!,
                                        fit: BoxFit.cover,
                                        errorBuilder: (_, _, _) =>
                                            const Icon(FluentIcons.puzzle),
                                      ),
                              ),
                              const SizedBox(width: 10),
                              Expanded(
                                child: Column(
                                  crossAxisAlignment: CrossAxisAlignment.start,
                                  children: [
                                    Text(hit.title,
                                        style: typography.bodyStrong),
                                    Text(
                                      '${hit.author} · ${hit.downloads} 次下载',
                                      style: typography.caption,
                                    ),
                                    Text(
                                      hit.description,
                                      maxLines: 2,
                                      overflow: TextOverflow.ellipsis,
                                      style: typography.caption,
                                    ),
                                  ],
                                ),
                              ),
                              const SizedBox(width: 8),
                              Button(
                                onPressed: _installing != null
                                    ? null
                                    : () => _install(hit),
                                child: Text(
                                    _installing == hit.projectId ? '安装中…' : '安装'),
                              ),
                            ]),
                          ),
                        );
                      },
                    ),
            ),
          ],
        ),
      ),
      actions: [
        Button(
          onPressed: () => Navigator.pop(context, _installed),
          child: Text(_installed ? '完成' : '关闭'),
        ),
      ],
    );
  }
}
