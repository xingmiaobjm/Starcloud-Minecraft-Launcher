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

  Future<void> _openSettings() async {
    if (_content == null) return;
    try {
      final settings = await _manager.settings(widget.version.id);
      if (!mounted) return;
      final saved = await showDialog<bool>(
        context: context,
        builder: (context) => _InstanceSettingsDialog(
          versionId: widget.version.id,
          initial: settings,
        ),
      );
      if (saved == true && mounted) {
        setState(() => _notice = '实例设置已保存');
        await _load();
      }
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    }
  }

  Future<void> _checkUpdates() async {
    setState(() {
      _busyFile = '__updates__';
      _error = null;
      _notice = null;
    });
    try {
      final updates = await _manager.checkUpdates(widget.version.id);
      if (!mounted) return;
      if (updates.isEmpty) {
        setState(() => _notice = '所有模组都是最新版本');
        return;
      }
      final changed = await showDialog<bool>(
        context: context,
        builder: (context) => _UpdatesDialog(
          versionId: widget.version.id,
          updates: updates,
          manager: _manager,
        ),
      );
      if (changed == true && mounted) {
        setState(() => _notice = '模组已更新');
        await _load();
      }
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '检查更新失败：$e');
    } finally {
      if (mounted) setState(() => _busyFile = null);
    }
  }

  Future<void> _checkDependencies() async {
    setState(() {
      _busyFile = '__deps__';
      _error = null;
      _notice = null;
    });
    try {
      final report = await _manager.checkDependencies(widget.version.id);
      if (!mounted) return;
      final changed = await showDialog<bool>(
        context: context,
        builder: (context) => _DependencyDialog(
          versionId: widget.version.id,
          report: report,
          manager: _manager,
        ),
      );
      if (changed == true && mounted) {
        setState(() => _notice = '缺失依赖已安装');
        await _load();
      }
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '依赖检查失败：$e');
    } finally {
      if (mounted) setState(() => _busyFile = null);
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
              icon: const Icon(FluentIcons.settings),
              label: const Text('实例设置'),
              onPressed: content == null ? null : _openSettings,
            ),
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
              if (_kind == ContentKind.mod) ...[
                const SizedBox(height: 8),
                Wrap(spacing: 8, runSpacing: 8, children: [
                  Button(
                    onPressed: _busyFile != null ? null : _checkUpdates,
                    child: const Text('检查更新'),
                  ),
                  Button(
                    onPressed: _busyFile != null ? null : _checkDependencies,
                    child: const Text('检查依赖'),
                  ),
                ]),
              ],
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

/// 实例参数编辑：隔离三态 + 内存/Java/窗口档位。
class _InstanceSettingsDialog extends ConsumerStatefulWidget {
  const _InstanceSettingsDialog({
    required this.versionId,
    required this.initial,
  });

  final String versionId;
  final VersionSettings initial;

  @override
  ConsumerState<_InstanceSettingsDialog> createState() =>
      _InstanceSettingsDialogState();
}

class _InstanceSettingsDialogState
    extends ConsumerState<_InstanceSettingsDialog> {
  late String _isolation = widget.initial.isolation ?? 'global';
  late String _memoryMode = widget.initial.memoryMode;
  late final _min = TextEditingController(
      text: '${widget.initial.memoryMinMb ?? widget.initial.effective.minMem}');
  late final _max = TextEditingController(
      text: '${widget.initial.memoryMaxMb ?? widget.initial.effective.maxMem}');
  late String _javaMode = widget.initial.javaMode;
  late final _javaPath =
      TextEditingController(text: widget.initial.javaPath ?? '');
  late String _windowMode = widget.initial.windowMode;
  late final _width = TextEditingController(
      text: '${widget.initial.windowWidth ?? widget.initial.effective.windowW ?? 854}');
  late final _height = TextEditingController(
      text: '${widget.initial.windowHeight ?? widget.initial.effective.windowH ?? 480}');
  late bool _fullscreen =
      widget.initial.fullscreen ?? widget.initial.effective.fullscreen ?? false;
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _min.dispose();
    _max.dispose();
    _javaPath.dispose();
    _width.dispose();
    _height.dispose();
    super.dispose();
  }

  Future<void> _save() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      await ref.read(kernelDepsProvider).content.saveSettings(
        widget.versionId,
        {
          'isolation': _isolation == 'global' ? null : _isolation,
          'memoryMode': _memoryMode,
          'memoryMinMb':
              _memoryMode == 'manual' ? int.tryParse(_min.text.trim()) : null,
          'memoryMaxMb':
              _memoryMode == 'manual' ? int.tryParse(_max.text.trim()) : null,
          'javaMode': _javaMode,
          'javaPath': _javaMode == 'manual' ? _javaPath.text.trim() : null,
          'windowMode': _windowMode,
          'windowWidth':
              _windowMode == 'manual' ? int.tryParse(_width.text.trim()) : null,
          'windowHeight': _windowMode == 'manual'
              ? int.tryParse(_height.text.trim())
              : null,
          'fullscreen': _windowMode == 'manual' ? _fullscreen : null,
        },
      );
      if (!mounted) return;
      Navigator.pop(context, true);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    final eff = widget.initial.effective;
    return ContentDialog(
      title: Text('实例设置 · ${widget.versionId}'),
      content: SizedBox(
        width: 560,
        child: SingleChildScrollView(
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.stretch,
            children: [
              InfoLabel(
                label: '版本隔离',
                child: ComboBox<String>(
                  value: _isolation,
                  items: const [
                    ComboBoxItem(value: 'global', child: Text('跟随全局')),
                    ComboBoxItem(value: 'shared', child: Text('共享 .minecraft')),
                    ComboBoxItem(value: 'isolated', child: Text('独立版本目录')),
                  ],
                  onChanged: _busy
                      ? null
                      : (v) => setState(() => _isolation = v ?? 'global'),
                ),
              ),
              const SizedBox(height: 12),
              InfoLabel(
                label: '内存档位',
                child: ComboBox<String>(
                  value: _memoryMode,
                  items: const [
                    ComboBoxItem(value: 'global', child: Text('跟随全局')),
                    ComboBoxItem(value: 'manual', child: Text('自定义')),
                    ComboBoxItem(value: 'auto', child: Text('自动推荐')),
                  ],
                  onChanged: _busy
                      ? null
                      : (v) => setState(() => _memoryMode = v ?? 'global'),
                ),
              ),
              if (_memoryMode == 'manual')
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Row(children: [
                    Expanded(
                      child: InfoLabel(
                        label: '最小内存（MB）',
                        child: TextBox(controller: _min),
                      ),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: InfoLabel(
                        label: '最大内存（MB）',
                        child: TextBox(controller: _max),
                      ),
                    ),
                  ]),
                ),
              const SizedBox(height: 12),
              InfoLabel(
                label: 'Java 档位',
                child: ComboBox<String>(
                  value: _javaMode,
                  items: const [
                    ComboBoxItem(value: 'global', child: Text('跟随全局')),
                    ComboBoxItem(value: 'manual', child: Text('自定义路径')),
                    ComboBoxItem(value: 'auto', child: Text('按版本 JSON 推荐')),
                  ],
                  onChanged: _busy
                      ? null
                      : (v) => setState(() => _javaMode = v ?? 'global'),
                ),
              ),
              if (_javaMode == 'manual')
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: TextBox(
                    controller: _javaPath,
                    placeholder: 'java 可执行文件绝对路径',
                  ),
                ),
              const SizedBox(height: 12),
              InfoLabel(
                label: '窗口档位',
                child: ComboBox<String>(
                  value: _windowMode,
                  items: const [
                    ComboBoxItem(value: 'global', child: Text('跟随全局')),
                    ComboBoxItem(value: 'manual', child: Text('自定义')),
                    ComboBoxItem(value: 'auto', child: Text('854×480 窗口化')),
                  ],
                  onChanged: _busy
                      ? null
                      : (v) => setState(() => _windowMode = v ?? 'global'),
                ),
              ),
              if (_windowMode == 'manual')
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Row(children: [
                    Expanded(
                      child: InfoLabel(
                        label: '宽',
                        child: TextBox(controller: _width),
                      ),
                    ),
                    const SizedBox(width: 8),
                    Expanded(
                      child: InfoLabel(
                        label: '高',
                        child: TextBox(controller: _height),
                      ),
                    ),
                    const SizedBox(width: 12),
                    Row(children: [
                      Checkbox(
                        checked: _fullscreen,
                        onChanged: _busy
                            ? null
                            : (v) => setState(() => _fullscreen = v ?? false),
                      ),
                      const Text('全屏'),
                    ]),
                  ]),
                ),
              const SizedBox(height: 12),
              Text(
                '当前生效：内存 ${eff.minMem}–${eff.maxMem} MB'
                ' · Java ${eff.java ?? '自动探测'}'
                ' · 窗口 ${eff.windowW ?? 854}×${eff.windowH ?? 480}'
                '${eff.fullscreen == true ? '（全屏）' : ''}',
                style: typography.caption,
              ),
              if (_error != null) ...[
                const SizedBox(height: 8),
                InfoBar(
                  title: const Text('保存失败'),
                  content: Text(_error!),
                  severity: InfoBarSeverity.error,
                  isLong: true,
                ),
              ],
            ],
          ),
        ),
      ),
      actions: [
        Button(
          onPressed: _busy ? null : () => Navigator.pop(context, false),
          child: const Text('取消'),
        ),
        FilledButton(
          onPressed: _busy ? null : _save,
          child: Text(_busy ? '保存中…' : '保存'),
        ),
      ],
    );
  }
}

/// 模组更新列表。
class _UpdatesDialog extends ConsumerStatefulWidget {
  const _UpdatesDialog({
    required this.versionId,
    required this.updates,
    required this.manager,
  });

  final String versionId;
  final List<ContentUpdate> updates;
  final ContentManager manager;

  @override
  ConsumerState<_UpdatesDialog> createState() => _UpdatesDialogState();
}

class _UpdatesDialogState extends ConsumerState<_UpdatesDialog> {
  final Set<String> _done = {};
  String? _busy;
  String? _error;

  Future<void> _update(ContentUpdate update) async {
    setState(() {
      _busy = update.fileName;
      _error = null;
    });
    try {
      await widget.manager.updateMod(widget.versionId, update.fileName);
      if (!mounted) return;
      setState(() => _done.add(update.fileName));
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '${update.fileName}：$e');
    } finally {
      if (mounted) setState(() => _busy = null);
    }
  }

  Future<void> _updateAll() async {
    for (final update in widget.updates) {
      if (_done.contains(update.fileName)) continue;
      await _update(update);
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    return ContentDialog(
      title: Text('模组更新（${widget.updates.length}）'),
      content: SizedBox(
        width: 640,
        height: 420,
        child: Column(
          children: [
            if (_error != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: InfoBar(
                  title: const Text('更新失败'),
                  content: Text(_error!),
                  severity: InfoBarSeverity.error,
                  isLong: true,
                ),
              ),
            Expanded(
              child: ListView.builder(
                itemCount: widget.updates.length,
                itemBuilder: (context, index) {
                  final update = widget.updates[index];
                  final done = _done.contains(update.fileName);
                  return Padding(
                    padding: const EdgeInsets.only(bottom: 6),
                    child: Card(
                      padding: const EdgeInsets.symmetric(
                          horizontal: 12, vertical: 8),
                      child: Row(children: [
                        Expanded(
                          child: Column(
                            crossAxisAlignment: CrossAxisAlignment.start,
                            children: [
                              Text(update.fileName,
                                  style: typography.bodyStrong),
                              Text(
                                '${update.currentVersion} → ${update.latestVersion}',
                                style: typography.caption,
                              ),
                            ],
                          ),
                        ),
                        Button(
                          onPressed: done || _busy != null
                              ? null
                              : () => _update(update),
                          child: Text(
                            done
                                ? '已更新'
                                : (_busy == update.fileName ? '更新中…' : '更新'),
                          ),
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
          onPressed: _busy != null
              ? null
              : () => Navigator.pop(context, _done.isNotEmpty),
          child: const Text('关闭'),
        ),
        FilledButton(
          onPressed: _busy != null || _done.length == widget.updates.length
              ? null
              : _updateAll,
          child: const Text('全部更新'),
        ),
      ],
    );
  }
}

/// 依赖完整性报告 + 一键安装缺失依赖。
class _DependencyDialog extends ConsumerStatefulWidget {
  const _DependencyDialog({
    required this.versionId,
    required this.report,
    required this.manager,
  });

  final String versionId;
  final DependencyReport report;
  final ContentManager manager;

  @override
  ConsumerState<_DependencyDialog> createState() => _DependencyDialogState();
}

class _DependencyDialogState extends ConsumerState<_DependencyDialog> {
  bool _busy = false;
  bool _installed = false;
  String? _stage;
  String? _error;

  Future<void> _installMissing() async {
    setState(() {
      _busy = true;
      _error = null;
    });
    try {
      for (final dependency in widget.report.missing) {
        if (!mounted) return;
        setState(() => _stage = '安装 ${dependency.title}…');
        await widget.manager.install(
          kind: ContentKind.mod,
          projectId: dependency.projectId,
          title: dependency.title,
          targetVersionId: widget.versionId,
        );
      }
      if (!mounted) return;
      setState(() {
        _installed = true;
        _stage = '已安装 ${widget.report.missing.length} 个依赖';
      });
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    final report = widget.report;
    final clean = report.missing.isEmpty && report.incompatible.isEmpty;
    return ContentDialog(
      title: const Text('依赖检查'),
      content: SizedBox(
        width: 640,
        height: 420,
        child: ListView(children: [
          if (clean)
            const InfoBar(
              title: Text('依赖完整'),
              content: Text('没有缺失的必需依赖，也没有已安装的不兼容模组。'),
              severity: InfoBarSeverity.success,
              isLong: true,
            ),
          if (report.missing.isNotEmpty) ...[
            Text('缺失的必需依赖（${report.missing.length}）',
                style: typography.subtitle),
            const SizedBox(height: 8),
            for (final dependency in report.missing)
              Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: Card(
                  padding:
                      const EdgeInsets.symmetric(horizontal: 12, vertical: 8),
                  child: Text(dependency.title, style: typography.body),
                ),
              ),
          ],
          if (report.incompatible.isNotEmpty) ...[
            const SizedBox(height: 8),
            InfoBar(
              title: const Text('已安装但不兼容'),
              content: Text(report.incompatible.join('、')),
              severity: InfoBarSeverity.error,
              isLong: true,
            ),
          ],
          if (report.optional.isNotEmpty) ...[
            const SizedBox(height: 8),
            Text('可选依赖 ${report.optional.length} 个（未安装不影响启动）',
                style: typography.caption),
          ],
          if (report.truncated) ...[
            const SizedBox(height: 8),
            Text('模组数量超过 200，只检查了前 200 个。',
                style: typography.caption),
          ],
          if (_stage != null) ...[
            const SizedBox(height: 8),
            Text(_stage!, style: typography.caption),
          ],
          if (_error != null) ...[
            const SizedBox(height: 8),
            InfoBar(
              title: const Text('安装失败'),
              content: Text(_error!),
              severity: InfoBarSeverity.error,
              isLong: true,
            ),
          ],
        ]),
      ),
      actions: [
        Button(
          onPressed: _busy ? null : () => Navigator.pop(context, _installed),
          child: const Text('关闭'),
        ),
        if (report.missing.isNotEmpty)
          FilledButton(
            onPressed: _busy ? null : _installMissing,
            child: Text(_busy ? '安装中…' : '安装缺失依赖'),
          ),
      ],
    );
  }
}
