import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/kernel/descriptor.dart';

void main() {
  group('描述符校验（§3 / §13）', () {
    test('id 必须小写、只允许 [a-z0-9._-]、长度 3..64', () {
      expect(() => PluginDescriptor.create(
            id: 'StarCloud.Download',
            name: 'x',
            version: '1.0.0',
            apiVersion: '3.0',
          ), throwsA(isA<DescriptorException>()));
      expect(() => PluginDescriptor.create(
            id: 'ab',
            name: 'x',
            version: '1.0.0',
            apiVersion: '3.0',
          ), throwsA(isA<DescriptorException>()));
      expect(() => PluginDescriptor.create(
            id: 'has space',
            name: 'x',
            version: '1.0.0',
            apiVersion: '3.0',
          ), throwsA(isA<DescriptorException>()));
      expect(
        PluginDescriptor.create(
          id: 'starcloud.download',
          name: 'x',
          version: '1.0.0',
          apiVersion: '3.0',
        ).id,
        'starcloud.download',
      );
    });

    test('id 不允许路径成分（反斜杠/绝对路径）', () {
      expect(() => PluginDescriptor.create(
            id: r'a\b',
            name: 'x',
            version: '1.0.0',
            apiVersion: '3.0',
          ), throwsA(isA<DescriptorException>()));
    });

    test('version 必须是 major.minor.patch', () {
      expect(() => PluginDescriptor.create(
            id: 'starcloud.a',
            name: 'x',
            version: '1.0',
            apiVersion: '3.0',
          ), throwsA(isA<DescriptorException>()));
    });

    test('apiVersion 接受 3 / 3.0 / 3.0.0 三种写法', () {
      for (final api in ['3', '3.0', '3.0.0']) {
        expect(
          PluginDescriptor.create(
            id: 'starcloud.a',
            name: 'x',
            version: '1.0.0',
            apiVersion: api,
          ).apiVersion,
          api,
        );
      }
      expect(majorOf('3.0'), 3);
    });

    test('depends/conflicts 的键必须是小写 id、值必须可解析', () {
      expect(() => PluginDescriptor.create(
            id: 'starcloud.a',
            name: 'x',
            version: '1.0.0',
            apiVersion: '3.0',
            depends: {'BadId': '^1.0.0'},
          ), throwsA(isA<DescriptorException>()));
      expect(() => PluginDescriptor.create(
            id: 'starcloud.a',
            name: 'x',
            version: '1.0.0',
            apiVersion: '3.0',
            conflicts: {'starcloud.b': 'not-a-range'},
          ), throwsA(isA<DescriptorException>()));
    });
  });

  group('版本范围（§5.6）', () {
    final v = SemVer.parse('1.4.2');

    test('* 匹配任意', () {
      expect(VersionRange.parse('*').satisfies(v), isTrue);
    });

    test('^1.2.3 → >=1.2.3 <2.0.0', () {
      final r = VersionRange.parse('^1.2.3');
      expect(r.satisfies(SemVer.parse('1.2.3')), isTrue);
      expect(r.satisfies(SemVer.parse('1.9.9')), isTrue);
      expect(r.satisfies(SemVer.parse('2.0.0')), isFalse);
      expect(r.satisfies(SemVer.parse('1.2.2')), isFalse);
    });

    test('^0.2.3 锁次版本（npm 语义）', () {
      final r = VersionRange.parse('^0.2.3');
      expect(r.satisfies(SemVer.parse('0.2.9')), isTrue);
      expect(r.satisfies(SemVer.parse('0.3.0')), isFalse);
    });

    test('>=2 <3 组合', () {
      final r = VersionRange.parse('>=2.0.0 <3.0.0');
      expect(r.satisfies(SemVer.parse('2.5.0')), isTrue);
      expect(r.satisfies(SemVer.parse('3.0.0')), isFalse);
    });

    test('精确版本与比较符', () {
      expect(VersionRange.parse('1.4.2').satisfies(v), isTrue);
      expect(VersionRange.parse('>1.4.2').satisfies(v), isFalse);
      expect(VersionRange.parse('<=1.4.2').satisfies(v), isTrue);
    });
  });
}
