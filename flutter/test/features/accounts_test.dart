import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:path/path.dart' as p;
import 'package:starcloud_launcher/plugins/builtin/accounts/accounts_plugin.dart';

/// 离线账户的持久化行为（不依赖内核，直接构造实现并注入文件路径）。
///
/// 设置已迁到 C# 核心（BridgeLauncherSettings），其正确性由桥接 E2E 覆盖。
void main() {
  late Directory dir;

  setUp(() => dir = Directory.systemTemp.createTempSync('starcloud_accounts'));
  tearDown(() => dir.deleteSync(recursive: true));

  group('离线账户存储（OfflineAccountStore）', () {
    test('添加离线账户：UUID = md5(OfflinePlayer:name) v3；新增即选中', () async {
      final store =
          OfflineAccountStore(p.join(dir.path, 'accounts.json').toString());
      await store.addOffline('Notch');
      final current = store.current();
      expect(current, isNotNull);
      expect(current!.name, 'Notch');
      expect(
        RegExp(r'^[0-9a-f]{8}-[0-9a-f]{4}-3[0-9a-f]{3}-[89ab][0-9a-f]{3}-[0-9a-f]{12}$')
            .hasMatch(current.uuid),
        isTrue,
      );
      expect(store.accounts().length, 1);
    });

    test('重复添加不重复登记（幂等）', () async {
      final store =
          OfflineAccountStore(p.join(dir.path, 'accounts.json').toString());
      await store.addOffline('Notch');
      await store.addOffline('Notch');
      expect(store.accounts().length, 1);
    });

    test('落盘可读回；current 持久化', () async {
      final file = p.join(dir.path, 'accounts.json');
      await OfflineAccountStore(file).addOffline('Dream');
      final reopened = OfflineAccountStore(file);
      expect(reopened.accounts().single.name, 'Dream');
      expect(reopened.current()!.name, 'Dream');
    });

    test('setCurrent / remove', () async {
      final store =
          OfflineAccountStore(p.join(dir.path, 'accounts.json').toString());
      await store.addOffline('A');
      await store.addOffline('B');
      final b = store.accounts().firstWhere((a) => a.name == 'B');
      await store.setCurrent(b.uuid);
      expect(store.current()!.name, 'B');
      await store.remove(b.uuid);
      expect(store.current(), isNull);
      expect(store.accounts().map((a) => a.name), ['A']);
    });

    test('损坏文件 → 按空表启动（规范 §10 损坏容忍）', () {
      final file = File(p.join(dir.path, 'accounts.json'));
      file.parent.createSync(recursive: true);
      file.writeAsStringSync('{{{');
      final store = OfflineAccountStore(file.path);
      expect(store.accounts(), isEmpty);
    });

    test('非法玩家名拒绝（空 / 超 16 字符）', () async {
      final store =
          OfflineAccountStore(p.join(dir.path, 'accounts.json').toString());
      await expectLater(store.addOffline(''), throwsArgumentError);
      await expectLater(store.addOffline('a' * 17), throwsArgumentError);
    });
  });
}
