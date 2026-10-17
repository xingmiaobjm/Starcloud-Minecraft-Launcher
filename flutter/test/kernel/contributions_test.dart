import 'package:flutter/widgets.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:starcloud_launcher/kernel/contribution_registry.dart';

Contribution nav(String plugin, String id, {int order = 0, bool footer = false}) =>
    Contribution(
      kind: ContributionKind.navItem,
      pluginId: plugin,
      id: id,
      title: id,
      order: order,
      footer: footer,
    );

void main() {
  test('同插件同 id 重复挂 → 先撤旧再挂新（热重载）', () {
    final r = ContributionRegistry();
    r.upsert(nav('p', 'home', order: 0));
    r.upsert(Contribution(
      kind: ContributionKind.navItem,
      pluginId: 'p',
      id: 'home',
      title: '首页 v2',
      order: 5,
    ));
    expect(r.length, 1);
    expect(r.navItems().single.title, '首页 v2');
    expect(r.navItems().single.order, 5);
  });

  test('order 升序；同 order 按登记先后', () {
    final r = ContributionRegistry();
    r.upsert(nav('p', 'c', order: 2));
    r.upsert(nav('p', 'a', order: 1));
    r.upsert(nav('p', 'b', order: 1));
    expect(r.navItems().map((c) => c.id).toList(), ['a', 'b', 'c']);
  });

  test('footer 项单独查询', () {
    final r = ContributionRegistry();
    r.upsert(nav('p', 'home'));
    r.upsert(nav('p', 'settings', footer: true));
    expect(r.navItems().map((c) => c.id), ['home']);
    expect(r.footerNavItems().map((c) => c.id), ['settings']);
  });

  test('停用插件 → 其全部贡献撤下（含其它种类）', () {
    final r = ContributionRegistry();
    r.upsert(nav('p.a', 'home'));
    r.upsert(Contribution(
      kind: ContributionKind.page,
      pluginId: 'p.a',
      id: 'home',
      title: '首页',
      view: (_) => const SizedBox(),
    ));
    r.upsert(nav('p.b', 'versions'));
    r.removeByPlugin('p.a');
    expect(r.length, 1);
    expect(r.navItems().single.pluginId, 'p.b');
  });

  testWidgets('页面工厂惰性：注册不调用，首次取用才调用', (tester) async {
    final r = ContributionRegistry();
    var built = 0;
    r.upsert(Contribution(
      kind: ContributionKind.page,
      pluginId: 'p',
      id: 'home',
      title: '首页',
      view: (_) {
        built++;
        return const SizedBox();
      },
    ));
    expect(built, 0, reason: '注册时不得创建页面');
    await tester.pumpWidget(Builder(builder: (ctx) {
      r.byKind(ContributionKind.page).single.view!(ctx);
      return const SizedBox();
    }));
    expect(built, 1);
  });

  test('变更通知：增/删/撤都会通知监听者', () {
    final r = ContributionRegistry();
    var notified = 0;
    r.addListener(() => notified++);
    r.upsert(nav('p', 'a'));
    r.withdraw(kind: ContributionKind.navItem, pluginId: 'p', id: 'a');
    r.upsert(nav('p', 'b'));
    r.removeByPlugin('p');
    expect(notified, 4);
    // 撤不存在的项不通知（幂等）。
    r.withdraw(kind: ContributionKind.navItem, pluginId: 'p', id: 'zzz');
    expect(notified, 4);
  });

  test('withdraw 幂等', () {
    final r = ContributionRegistry();
    r.upsert(nav('p', 'a'));
    r.withdraw(kind: ContributionKind.navItem, pluginId: 'p', id: 'a');
    r.withdraw(kind: ContributionKind.navItem, pluginId: 'p', id: 'a');
    expect(r.length, 0);
  });

  test('按 target 查询挂载位内容（section/slot 用）', () {
    final r = ContributionRegistry();
    r.upsert(Contribution(
      kind: ContributionKind.section,
      pluginId: 'p',
      id: 's1',
      title: '下载',
      target: 'settings.download',
    ));
    r.upsert(Contribution(
      kind: ContributionKind.section,
      pluginId: 'p',
      id: 's2',
      title: '外观',
      target: 'settings.appearance',
    ));
    expect(
      r.byKind(ContributionKind.section, target: 'settings.download').length,
      1,
    );
  });
}
