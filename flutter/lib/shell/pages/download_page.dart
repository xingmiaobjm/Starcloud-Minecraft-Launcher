/// 下载页：官方清单 → 选版本 → 安装（进度可见）。
library;

import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/minecraft.dart';

class DownloadPage extends ConsumerStatefulWidget {
  const DownloadPage({super.key});

  @override
  ConsumerState<DownloadPage> createState() => _DownloadPageState();
}

class _DownloadPageState extends ConsumerState<DownloadPage> {
  List<RemoteVersion> _remote = const [];
  /// 当前显示的条数（可继续加载）：原来直接 take(80) 且没有「加载更多」，
  /// 版本多的时候用户以为只有 80 个。
  int _limit = 80;
  bool _loading = true;
  String _error = '';
  String _filter = '';
  bool _releasesOnly = true;
  String? _installing;
  String _progress = '';
  bool _failed = false;

  @override
  void initState() {
    super.initState();
    unawaited(_load());
  }

  Future<void> _load() async {
    setState(() {
      _loading = true;
      _error = '';
    });
    try {
      final remote = await ref.read(kernelDepsProvider).catalog.remoteVersions();
      if (!mounted) return;
      setState(() {
        _remote = remote;
        _loading = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loading = false;
        _error = '清单获取失败：$e（可在设置里切换下载源）';
      });
    }
  }

  Future<void> _install(RemoteVersion v) async {
    setState(() {
      _installing = v.id;
      _progress = '开始…';
      _failed = false;
    });
    try {
      await ref.read(kernelDepsProvider).installer.install(
            v.id,
            onProgress: (p) {
              if (!mounted) return;
              setState(() {
                _progress = p.total != null
                    ? '${p.stage} · ${p.detail}（${p.current}/${p.total}）'
                    : '${p.stage} · ${p.detail}';
              });
            },
          );
      if (!mounted) return;
      setState(() {
        _progress = '完成 ✓（可回首页启动）';
        _failed = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _progress = '失败：$e';
        _failed = true;
      });
    } finally {
      // 无论成功失败都要复位：否则所有"安装"按钮永久禁用，只能切页重进。
      if (mounted) setState(() => _installing = null);
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    final shown = _remote.where((v) {
      if (_releasesOnly && v.type != 'release') return false;
      if (_filter.isNotEmpty &&
          !v.id.toLowerCase().contains(_filter.toLowerCase())) {
        return false;
      }
      return true;
    }).toList();

    return ScaffoldPage(
      header: PageHeader(title: const Text('下载')),
      content: ListView(
        padding: const EdgeInsets.symmetric(horizontal: 24),
        children: [
          Card(
            padding: const EdgeInsets.all(12),
            child: Row(children: [
              Row(children: [
                Checkbox(
                  checked: _releasesOnly,
                  onChanged: (v) => setState(() => _releasesOnly = v ?? true),
                ),
                const Text('只看正式版'),
              ]),
              const SizedBox(width: 12),
              Expanded(
                child: TextBox(
                  placeholder: '搜索版本号（如 1.20.4）',
                  onChanged: (v) => setState(() => _filter = v),
                ),
              ),
              const SizedBox(width: 12),
              Button(onPressed: _loading ? null : _load, child: const Text('刷新')),
            ]),
          ),
          const SizedBox(height: 12),
          if (_loading) const Center(child: ProgressRing()),
          if (_error.isNotEmpty)
            InfoBar(
              title: const Text('出错了'),
              content: Text(_error),
              severity: InfoBarSeverity.error,
              isLong: true,
            ),
          if (_progress.isNotEmpty)
            InfoBar(
              title: Text(_installing != null
                  ? '正在安装 $_installing'
                  : (_failed ? '安装失败' : '安装完成')),
              content: Text(_progress),
              severity: _failed
                  ? InfoBarSeverity.error
                  : (_installing != null
                      ? InfoBarSeverity.info
                      : InfoBarSeverity.success),
              isLong: true,
            ),
          if (shown.isEmpty && !_loading)
            Padding(
              padding: const EdgeInsets.symmetric(vertical: 24),
              child: Center(
                child: Text(
                  _remote.isEmpty
                      ? '没有拉取到版本清单（点右上角「刷新」重试）'
                      : '没有匹配的版本',
                  style: TextStyle(
                    color: FluentTheme.of(context).disabledColor,
                  ),
                ),
              ),
            ),
          if (shown.length > _limit)
            Padding(
              padding: const EdgeInsets.only(bottom: 8),
              child: TextButton(
                onPressed: () => setState(() => _limit += 80),
                child: Text('还有 ${shown.length - _limit} 个，加载更多'),
              ),
            ),
          for (final v in shown.take(_limit))
            Padding(
              padding: const EdgeInsets.only(bottom: 6),
              child: Card(
                padding:
                    const EdgeInsets.symmetric(horizontal: 16, vertical: 10),
                child: Row(children: [
                  Expanded(
                    child: Column(
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(v.id, style: typography.bodyStrong),
                        Text('${v.type} · ${v.releaseTime.split('T').first}',
                            style: typography.caption),
                      ],
                    ),
                  ),
                  Button(
                    onPressed: _installing != null ? null : () => _install(v),
                    child: Text(_installing == v.id ? '安装中' : '安装'),
                  ),
                ]),
              ),
            ),
          if (shown.length > 80)
            Padding(
              padding: const EdgeInsets.all(8),
              child: Text('还有 ${shown.length - 80} 个未显示，用搜索缩小范围',
                  style: typography.caption),
            ),
        ],
      ),
    );
  }
}
