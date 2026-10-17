import 'dart:convert';
import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:starcloud_launcher/kernel/paths.dart';

void main() {
  group('三根（§11.1）', () {
    test('默认落到 XDG 位置（HOME 下）', () {
      final paths = KernelPaths.linux(env: {'HOME': '/home/dev'});
      expect(paths.configRoot, p.join('/home/dev', '.config', 'starcloud-launcher'));
      expect(paths.dataRoot, p.join('/home/dev', '.local/share', 'starcloud-launcher'));
      expect(paths.cacheRoot, p.join('/home/dev', '.cache', 'starcloud-launcher'));
    });

    test('XDG_* 环境变量被尊重', () {
      final paths = KernelPaths.linux(env: {
        'HOME': '/home/dev',
        'XDG_CONFIG_HOME': '/xdg/config',
        'XDG_DATA_HOME': '/xdg/data',
        'XDG_CACHE_HOME': '/xdg/cache',
      });
      expect(paths.configRoot, p.join('/xdg/config', 'starcloud-launcher'));
      expect(paths.dataRoot, p.join('/xdg/data', 'starcloud-launcher'));
      expect(paths.cacheRoot, p.join('/xdg/cache', 'starcloud-launcher'));
    });

    test('STARCLOUD_* 覆盖优先于 XDG', () {
      final paths = KernelPaths.linux(env: {
        'HOME': '/home/dev',
        'XDG_DATA_HOME': '/xdg/data',
        'STARCLOUD_DATA_DIR': '/custom/data',
      });
      expect(paths.dataRoot, p.normalize('/custom/data'));
    });

    test('插件配置/数据/缓存路径按 id 隔离', () {
      final paths = KernelPaths.linux(env: {'HOME': '/home/dev'});
      expect(paths.pluginConfigFile('starcloud.a'),
          p.join(paths.configRoot, 'plugin-config', 'starcloud.a.json'));
      expect(paths.pluginDataDir('starcloud.a'),
          p.join(paths.dataRoot, 'plugin-data', 'starcloud.a'));
      expect(paths.pluginCacheDir('starcloud.a'),
          p.join(paths.cacheRoot, 'plugin-cache', 'starcloud.a'));
    });
  });

  group('插件配置分区（§10）', () {
    late Directory dir;
    setUp(() => dir = Directory.systemTemp.createTempSync('starcloud_cfg'));
    tearDown(() => dir.deleteSync(recursive: true));

    test('写入原子（临时文件 rename）且可读回', () {
      final file = p.join(dir.path, 'plugin-config', 'p.json');
      final section = PluginConfigSection.load(file);
      section.set('theme', 'dark');
      section.set('memoryMb', 4096);
      // 没有残留的 .tmp 文件（rename 掉了）。
      final leftovers = Directory(p.dirname(file))
          .listSync()
          .whereType<File>()
          .where((f) => f.path.endsWith('.tmp'))
          .toList();
      expect(leftovers, isEmpty);

      final reread = PluginConfigSection.load(file);
      expect(reread.get<String>('theme', ''), 'dark');
      expect(reread.get<int>('memoryMb', 0), 4096);
    });

    test('文件损坏 → 按默认值启动，不崩溃', () {
      final file = p.join(dir.path, 'plugin-config', 'p.json');
      File(file).createSync(recursive: true);
      File(file).writeAsStringSync('{ not json');
      final section = PluginConfigSection.load(file);
      expect(section.get<String>('theme', 'fallback'), 'fallback');
      // 仍可正常写入并修复文件。
      section.set('theme', 'light');
      final decoded = jsonDecode(File(file).readAsStringSync());
      expect(decoded['theme'], 'light');
    });

    test('两个插件的分区互不可见', () {
      final a = PluginConfigSection.load(p.join(dir.path, 'a.json'))..set('k', 1);
      final b = PluginConfigSection.load(p.join(dir.path, 'b.json'));
      expect(a.get<int>('k', 0), 1);
      expect(b.get<int>('k', 0), 0);
    });
  });
}
