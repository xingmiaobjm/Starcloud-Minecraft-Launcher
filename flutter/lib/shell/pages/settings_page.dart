/// 设置页：游戏目录 / Java 路径 / 内存 / 下载源。
library;

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/minecraft.dart';

class SettingsPage extends ConsumerStatefulWidget {
  const SettingsPage({super.key});

  @override
  ConsumerState<SettingsPage> createState() => _SettingsPageState();
}

class _SettingsPageState extends ConsumerState<SettingsPage> {
  // 核心未就绪时读契约会抛 MissingContractException（内核在后台启动，
  // 页面可能先一步构建）：这里先探一次，失败就显示提示而不是让 build 崩掉。
  LauncherSettings? _settings;
  String? _loadError;
  final _gameDir = TextEditingController();
  final _javaPath = TextEditingController();
  int _memory = 2048;
  DownloadSource _source = DownloadSource.auto;
  JavaProbeResult? _javaProbe;
  bool _probing = false;

  @override
  void initState() {
    super.initState();
    _load();
  }

  void _load() {
    try {
      final settings = ref.read(kernelDepsProvider).settings;
      _settings = settings;
      _gameDir.text = settings.gameDir();
      _javaPath.text = settings.javaPath();
      _memory = settings.memoryMb();
      _source = settings.downloadSource();
    } catch (e) {
      _loadError = '$e';
    }
  }

  @override
  void dispose() {
    _gameDir.dispose();
    _javaPath.dispose();
    super.dispose();
  }

  Future<void> _probeJava() async {
    setState(() => _probing = true);
    try {
      final java = await ref.read(kernelDepsProvider).launcher.detectJava();
      if (!mounted) return;
      setState(() {
        _javaProbe = java == null
            ? const JavaProbeResult(path: '', version: '未找到', major: 0)
            : JavaProbeResult(
                path: java.path, version: java.version, major: java.major);
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _javaProbe = JavaProbeResult(path: '', version: '探测失败：$e', major: 0);
      });
    } finally {
      // 失败也要复位，否则"探测"按钮永久禁用。
      if (mounted) setState(() => _probing = false);
    }
  }

  /// 写入设置：成功回读服务端值（避免本地显示与持久化状态漂移），失败给出可读提示。
  Future<void> _save(
    Future<void> Function() action,
    String successMessage,
  ) async {
    try {
      await action();
      if (!mounted || !context.mounted) return;
      final settings = _settings;
      if (settings == null) return;
      setState(() {
        _memory = settings.memoryMb();
        _source = settings.downloadSource();
      });
      _toast(context, successMessage, InfoBarSeverity.success);
    } catch (e) {
      if (!mounted || !context.mounted) return;
      _toast(context, '保存失败：$e', InfoBarSeverity.error);
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    // 核心未就绪：显示提示而不是崩在 build 里
    if (_loadError != null || _settings == null) {
      return ScaffoldPage(
        header: PageHeader(title: const Text('设置')),
        content: Center(
          child: InfoBar(
            title: const Text('核心尚未就绪'),
            content: Text(_loadError ?? '正在连接核心，请稍候'),
            severity: InfoBarSeverity.warning,
            isLong: true,
          ),
        ),
      );
    }
    return ScaffoldPage(
      header: PageHeader(title: const Text('设置')),
      content: ListView(
        padding: const EdgeInsets.symmetric(horizontal: 24),
        children: [
          Card(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('游戏目录（.minecraft）', style: typography.subtitle),
                const SizedBox(height: 8),
                Row(children: [
                  Expanded(
                    child: TextBox(
                      controller: _gameDir,
                      placeholder: '~/.minecraft',
                    ),
                  ),
                  const SizedBox(width: 12),
                  Button(
                    onPressed: () => _save(
                      () => (_settings ?? (throw StateError('核心未就绪'))).setGameDir(_gameDir.text.trim()),
                      '游戏目录已保存',
                    ),
                    child: const Text('保存'),
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
                Text('Java', style: typography.subtitle),
                const SizedBox(height: 8),
                Row(children: [
                  Expanded(
                    child: TextBox(
                      controller: _javaPath,
                      placeholder: '留空 = 自动探测（JAVA_HOME / PATH / /usr/lib/jvm）',
                    ),
                  ),
                  const SizedBox(width: 12),
                  Button(
                    onPressed: _probing ? null : _probeJava,
                    child: Text(_probing ? '探测中…' : '探测'),
                  ),
                  const SizedBox(width: 12),
                  Button(
                    onPressed: () => _save(
                      () => (_settings ?? (throw StateError('核心未就绪'))).setJavaPath(_javaPath.text.trim()),
                      'Java 路径已保存',
                    ),
                    child: const Text('保存'),
                  ),
                ]),
                const SizedBox(height: 8),
                if (_javaProbe != null)
                  InfoBar(
                    title: Text(_javaProbe!.version),
                    content: Text(_javaProbe!.path.isEmpty
                        ? '没有找到可用的 Java —— 请安装 JRE 17+ 或在上方指定路径'
                        : _javaProbe!.path),
                    severity: _javaProbe!.major >= 17
                        ? InfoBarSeverity.success
                        : InfoBarSeverity.warning,
                    isLong: true,
                  ),
              ],
            ),
          ),
          const SizedBox(height: 12),
          Card(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('内存（游戏最大堆）', style: typography.subtitle),
                const SizedBox(height: 8),
                Slider(
                  value: _memory.toDouble(),
                  min: 1024,
                  max: 16384,
                  divisions: ((16384 - 1024) / 512).round(),
                  label: '$_memory MB',
                  onChanged: (v) => setState(() => _memory = v.round()),
                ),
                FilledButton(
                  onPressed: () => _save(
                    () => (_settings ?? (throw StateError('核心未就绪'))).setMemoryMb(_memory),
                    '内存已保存（$_memory MB）',
                  ),
                  child: const Text('保存'),
                ),
              ],
            ),
          ),
          const SizedBox(height: 12),
          Card(
            padding: const EdgeInsets.all(16),
            child: Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text('下载源', style: typography.subtitle),
                const SizedBox(height: 8),
                ComboBox<DownloadSource>(
                  value: _source,
                  items: [
                    ComboBoxItem(
                        value: DownloadSource.auto,
                        child: const Text('自动（多源竞速）')),
                    ComboBoxItem(
                        value: DownloadSource.official,
                        child: const Text('官方（piston-meta）')),
                    ComboBoxItem(
                        value: DownloadSource.bmclapi,
                        child: const Text('BMCLAPI 镜像')),
                  ],
                  onChanged: (v) =>
                      setState(() => _source = v ?? DownloadSource.auto),
                ),
                const SizedBox(height: 8),
                FilledButton(
                  onPressed: () => _save(
                    () => (_settings ?? (throw StateError('核心未就绪'))).setDownloadSource(_source),
                    '下载源已保存（即时生效）',
                  ),
                  child: const Text('保存'),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  void _toast(BuildContext context, String message, InfoBarSeverity severity) {
    displayInfoBar(
      context,
      builder: (context, close) => InfoBar(
        title: Text(message),
        severity: severity,
        onClose: close,
      ),
    );
  }
}

class JavaProbeResult {
  const JavaProbeResult({
    required this.path,
    required this.version,
    required this.major,
  });
  final String path;
  final String version;
  final int major;
}
