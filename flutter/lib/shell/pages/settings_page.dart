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
  late final LauncherSettings _settings = ref.read(kernelDepsProvider).settings;
  late final _gameDir = TextEditingController(text: _settings.gameDir());
  late final _javaPath = TextEditingController(text: _settings.javaPath());
  late int _memory = _settings.memoryMb();
  late DownloadSource _source = _settings.downloadSource();
  JavaProbeResult? _javaProbe;
  bool _probing = false;

  Future<void> _probeJava() async {
    setState(() => _probing = true);
    final java = await ref.read(kernelDepsProvider).launcher.detectJava();
    if (!mounted || !context.mounted) return;
    setState(() {
      _probing = false;
      _javaProbe = java == null
          ? const JavaProbeResult(path: '', version: '未找到', major: 0)
          : JavaProbeResult(
              path: java.path, version: java.version, major: java.major);
    });
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
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
                    onPressed: () async {
                      await _settings.setGameDir(_gameDir.text.trim());
                      if (!mounted || !context.mounted) return;
                      _toast(context, '游戏目录已保存');
                    },
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
                    onPressed: () async {
                      await _settings.setJavaPath(_javaPath.text.trim());
                      if (!mounted || !context.mounted) return;
                      _toast(context, 'Java 路径已保存');
                    },
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
                  onPressed: () async {
                    await _settings.setMemoryMb(_memory);
                    if (!mounted || !context.mounted) return;
                    _toast(context, '内存已保存（$_memory MB）');
                  },
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
                        value: DownloadSource.official,
                        child: const Text('官方（piston-meta）')),
                    ComboBoxItem(
                        value: DownloadSource.bmclapi,
                        child: const Text('BMCLAPI 镜像')),
                  ],
                  onChanged: (v) => setState(() => _source = v ?? DownloadSource.official),
                ),
                const SizedBox(height: 8),
                FilledButton(
                  onPressed: () async {
                    await _settings.setDownloadSource(_source);
                    if (!mounted || !context.mounted) return;
                      _toast(context, '下载源已保存（即时生效）');
                  },
                  child: const Text('保存'),
                ),
              ],
            ),
          ),
        ],
      ),
    );
  }

  void _toast(BuildContext context, String message) {
    displayInfoBar(
      context,
      builder: (context, close) => InfoBar(
        title: Text(message),
        severity: InfoBarSeverity.success,
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
