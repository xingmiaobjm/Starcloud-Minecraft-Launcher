/// UI 贡献点注册表（规范 §8）。
///
/// - 宿主提供挂载位（slot）：每个挂载位有 id、可接受的贡献种类、排序规则；
/// - 插件**声明**贡献，宿主/壳负责渲染；插件不得直接操作视图树；
/// - 增删运行期生效且不重建整窗（壳监听本注册表的变更通知，只重建受影响部分）；
/// - 支持 priority（`order`）排序与"同插件内同 id → 先撤旧再挂新"（热重载）；
/// - 停用插件 → 按插件撤下全部贡献；
/// - 页面的 `view` 是**惰性**工厂（规范 §8.2：注册后不创建，首次进入才创建；
///   页面关闭由 Flutter 的常规卸载释放）。
///
/// 视图类型是 `WidgetBuilder`（flutter/widgets）——这是 flutter/README.md 里
/// 声明的取舍 1：本端内核允许 import Flutter，"引擎无关"由"内核零业务词、
/// 壳零硬编码导航"保证。
library;

import 'package:flutter/foundation.dart' show Listenable;
import 'package:flutter/widgets.dart'
    show IconData, VoidCallback, WidgetBuilder;

/// 贡献种类（规范 §8.2 最小集）。
enum ContributionKind { navItem, page, homeCard, section, slot, overlay }

/// 一条贡献。字段与规范 §8.2 同名同义。
class Contribution {
  const Contribution({
    required this.kind,
    required this.pluginId,
    required this.id,
    required this.title,
    this.icon,
    this.order = 0,
    this.footer = false,
    this.target,
    this.view,
  });

  final ContributionKind kind;

  /// 登记者插件 id（内核填，不由插件传——防冒名）。
  final String pluginId;

  /// **同一插件内**唯一；重复挂同 id = 先撤旧再挂新（热重载）。
  final String id;

  /// 展示文案（基座期直接文本；接入 i18n 后这里是键，规范 §8.2/§11.2）。
  final String title;

  /// 图标（fluent_ui 的 IconData；壳负责渲染）。
  final IconData? icon;

  /// 排序权重：升序在前，同值按登记顺序。
  final int order;

  /// 仅 navItem：是否挂在导航栏底部区（footer）。
  final bool footer;

  /// section/slot 的落点（宿主文档定义的挂载位 id，规范 §8.3：不得含启动器语义）。
  final String? target;

  /// 视图工厂。page/homeCard/section/slot/overlay 需要给出；
  /// **惰性**：注册时不调用，首次展示才调用（规范 §8.2）。
  final WidgetBuilder? view;
}

class _Record {
  _Record(this.contribution, this.serial);
  final Contribution contribution;
  final int serial;
}

class ContributionRegistry implements Listenable {
  final Map<String, _Record> _byKey = {};
  int _serial = 0;
  final List<VoidCallback> _listeners = [];

  String _key(Contribution c) => '${c.pluginId}::${c.kind.name}::${c.id}';

  /// 变更通知（壳据此刷新导航/页面，而不重建整窗）。
  /// 实现 [Listenable]，可直接喂给 `ListenableBuilder`。
  @override
  void addListener(VoidCallback listener) => _listeners.add(listener);

  @override
  void removeListener(VoidCallback listener) => _listeners.remove(listener);
  void _notify() {
    for (final l in List<VoidCallback>.of(_listeners)) {
      l();
    }
  }

  /// 挂一条贡献（同插件 + 同种类 + 同 id → 先撤旧再挂新）。
  /// pluginId 由内核绑定（见 [PluginContext.contributions]）。
  void upsert(Contribution contribution) {
    _byKey[_key(contribution)] = _Record(contribution, _serial++);
    _notify();
  }

  /// 撤一条（同键定位；不存在则静默——撤下要求幂等，规范 §5.6.3）。
  void withdraw({
    required ContributionKind kind,
    required String pluginId,
    required String id,
  }) {
    final key = '$pluginId::${kind.name}::$id';
    if (_byKey.remove(key) != null) {
      _notify();
    }
  }

  /// 停用插件：撤下其全部贡献（规范 §8.1）。
  void removeByPlugin(String pluginId) {
    final before = _byKey.length;
    _byKey.removeWhere((_, r) => r.contribution.pluginId == pluginId);
    if (_byKey.length != before) {
      _notify();
    }
  }

  /// 某挂载位的现有内容（规范 §8.1：支持查询），按 order 升序、同序按登记先后。
  List<Contribution> byKind(ContributionKind kind, {String? target}) {
    final matched = _byKey.values
        .where((r) =>
            r.contribution.kind == kind &&
            (target == null || r.contribution.target == target))
        .toList()
      ..sort((a, b) {
        final byOrder = a.contribution.order.compareTo(b.contribution.order);
        return byOrder != 0 ? byOrder : a.serial.compareTo(b.serial);
      });
    return matched.map((r) => r.contribution).toList(growable: false);
  }

  /// 主导航项（footer=false 顶部区）。
  List<Contribution> navItems() =>
      byKind(ContributionKind.navItem).where((c) => !c.footer).toList();

  /// 导航底部区项（设置 / 日志这类）。
  List<Contribution> footerNavItems() =>
      byKind(ContributionKind.navItem).where((c) => c.footer).toList();

  int countForPlugin(String pluginId) =>
      _byKey.values.where((r) => r.contribution.pluginId == pluginId).length;

  int get length => _byKey.length;
}
