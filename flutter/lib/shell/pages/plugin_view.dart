/// 插件声明式视图渲染器（v2 规范 §7）：JSON 节点 → Fluent 控件。
///
/// 白名单组件：stack/text/button/toggle/input/select/list/table/image/progress/
/// badge/divider/link/code。动作经宿主 `plugins.invoke` 执行（权限校验在 C# 侧）。
library;

import 'dart:async';
import 'dart:convert';

import 'package:fluent_ui/fluent_ui.dart';

import '../../contracts/plugins.dart';

class PluginViewRenderer extends StatefulWidget {
  const PluginViewRenderer({
    super.key,
    required this.node,
    required this.bindings,
    required this.onInvoke,
  });

  final PluginViewNode node;
  final Map<String, Object?> bindings;

  /// (api, args) → 结果 JSON / 错误（错误抛异常）。
  final Future<String?> Function(String api, Object? args) onInvoke;

  @override
  State<PluginViewRenderer> createState() => _PluginViewRendererState();
}

class _PluginViewRendererState extends State<PluginViewRenderer> {
  final Map<String, Object?> _values = {};
  final Map<String, TextEditingController> _controllers = {};
  String? _notice;
  bool _busy = false;

  @override
  void dispose() {
    for (final controller in _controllers.values) {
      controller.dispose();
    }
    super.dispose();
  }

  Object? _resolve(PluginViewBinding? bind) {
    if (bind == null) return null;
    final key = '${bind.scope}.${bind.key}';
    return _values.containsKey(key) ? _values[key] : widget.bindings[key];
  }

  String _textOf(PluginViewValue value) {
    if (value.bind != null) {
      final resolved = _resolve(value.bind);
      if (resolved == null) return '';
      return resolved is String ? resolved : jsonEncode(resolved);
    }
    return value.text;
  }

  bool _boolOf(PluginViewNode node) {
    final resolved = _resolve(node.bind);
    return resolved == true;
  }

  String _stringOf(PluginViewNode node) {
    final resolved = _resolve(node.bind);
    if (resolved == null) return _textOf(node.value);
    return resolved is String ? resolved : jsonEncode(resolved);
  }

  Future<void> _run(PluginViewAction? action) async {
    if (action == null) return;
    if (action.isHandler) {
      setState(() => _notice = '处理器回调 ${action.handler} 暂未接入');
      return;
    }
    setState(() {
      _busy = true;
      _notice = null;
    });
    try {
      Object? args;
      try {
        args = jsonDecode(action.argsJson);
      } catch (_) {
        args = const <Object?>[];
      }
      final result = await widget.onInvoke(action.api, args);
      if (!mounted) return;
      setState(() => _notice =
          (result == null || result.isEmpty) ? '已执行 ${action.api}' : result);
    } catch (e) {
      if (!mounted) return;
      setState(() => _notice = '执行失败：$e');
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  Future<void> _writeStorage(PluginViewBinding bind, Object? value) async {
    final key = '${bind.scope}.${bind.key}';
    setState(() => _values[key] = value);
    try {
      await widget.onInvoke('storage.set', <Object?>[bind.key, value]);
      if (!mounted) return;
      setState(() => _notice = '已保存 ${bind.key}');
    } catch (e) {
      if (!mounted) return;
      setState(() => _notice = '保存失败：$e');
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        if (_notice != null)
          Padding(
            padding: const EdgeInsets.only(bottom: 8),
            child: InfoBar(
              title: Text(_notice!),
              severity: InfoBarSeverity.info,
              isLong: true,
              onClose: () => setState(() => _notice = null),
            ),
          ),
        if (_busy)
          const Padding(
            padding: EdgeInsets.only(bottom: 8),
            child: ProgressBar(),
          ),
        Expanded(
          child: SingleChildScrollView(
            child: _node(widget.node, typography),
          ),
        ),
      ],
    );
  }

  Widget _node(PluginViewNode node, Typography typography) {
    switch (node.type) {
      case 'stack':
        final children = <Widget>[];
        for (var i = 0; i < node.children.length; i++) {
          if (i > 0) {
            final gap = node.spacing > 0 ? node.spacing : 8.0;
            children.add(node.direction == 'row'
                ? SizedBox(width: gap)
                : SizedBox(height: gap));
          }
          children.add(_node(node.children[i], typography));
        }
        return Flex(
          direction: node.direction == 'row' ? Axis.horizontal : Axis.vertical,
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: children,
        );
      case 'text':
        return Text(_textOf(node.value), style: _textStyle(node.style, typography));
      case 'code':
        return SelectableText(
          _textOf(node.value),
          style: typography.body?.copyWith(fontFamily: 'monospace'),
        );
      case 'button':
        final label = Text(_textOf(node.value));
        if (node.kind == 'accent' || node.kind == 'danger') {
          return FilledButton(
            onPressed: _busy ? null : () => unawaited(_run(node.action)),
            child: label,
          );
        }
        return Button(
          onPressed: _busy ? null : () => unawaited(_run(node.action)),
          child: label,
        );
      case 'toggle':
        return Row(mainAxisSize: MainAxisSize.min, children: [
          ToggleSwitch(
            checked: _boolOf(node),
            onChanged: _busy
                ? null
                : (value) {
                    final bind = node.bind;
                    if (bind != null && bind.scope == 'storage') {
                      unawaited(_writeStorage(bind, value));
                    }
                    if (node.action != null) unawaited(_run(node.action));
                  },
          ),
          if (_textOf(node.value).isNotEmpty) ...[
            const SizedBox(width: 8),
            Text(_textOf(node.value), style: typography.body),
          ],
        ]);
      case 'input':
        final bind = node.bind;
        final key = bind == null
            ? 'input.${identityHashCode(node)}'
            : '${bind.scope}.${bind.key}';
        final controller = _controllers.putIfAbsent(
          key,
          () => TextEditingController(text: _stringOf(node)),
        );
        return SizedBox(
          width: node.wrap ? double.infinity : 320,
          child: TextBox(
            controller: controller,
            placeholder: node.placeholder,
            maxLines: node.multiline ? null : 1,
            onSubmitted: bind != null && bind.scope == 'storage'
                ? (value) => unawaited(_writeStorage(bind, value))
                : null,
          ),
        );
      case 'select':
        final bind = node.bind;
        final current = _stringOf(node);
        return SizedBox(
          width: 320,
          child: ComboBox<String>(
            value: node.options.contains(current) ? current : null,
            items: [
              for (final option in node.options)
                ComboBoxItem(value: option, child: Text(option)),
            ],
            onChanged: bind != null && bind.scope == 'storage'
                ? (value) {
                    if (value != null) unawaited(_writeStorage(bind, value));
                  }
                : null,
          ),
        );
      case 'list':
        final items = _resolve(node.itemsBind);
        if (items is! List<Object?>) {
          return Text('列表为空', style: typography.caption);
        }
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            for (final item in items)
              Padding(
                padding: const EdgeInsets.only(bottom: 4),
                child: Text(
                  item is String ? item : jsonEncode(item),
                  style: typography.body,
                ),
              ),
            if (node.itemView.isNotEmpty)
              Text('列表模板 ${node.itemView} 暂未展开', style: typography.caption),
          ],
        );
      case 'table':
        final rows = _resolve(node.itemsBind);
        if (rows is! List<Object?>) {
          return Text('表格为空', style: typography.caption);
        }
        return Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (node.columns.isNotEmpty)
              Row(children: [
                for (final column in node.columns)
                  Expanded(
                    child: Text(column,
                        style: typography.caption
                            ?.copyWith(fontWeight: FontWeight.bold)),
                  ),
              ]),
            for (final row in rows)
              Padding(
                padding: const EdgeInsets.only(top: 4),
                child: Text(
                  row is Map<String, Object?>
                      ? node.columns
                          .map((column) => '${row[column] ?? ''}')
                          .join('  ·  ')
                      : jsonEncode(row),
                  style: typography.body,
                ),
              ),
          ],
        );
      case 'image':
        if (node.source.startsWith('http')) {
          return Image.network(
            node.source,
            height: node.height > 0 ? node.height : 160,
            fit: BoxFit.contain,
            errorBuilder: (_, _, _) =>
                Text('图片加载失败：${node.source}', style: typography.caption),
          );
        }
        return Text('包内图片 ${node.source}（需要本地资源服务）',
            style: typography.caption);
      case 'progress':
        return ProgressBar(value: node.progress.clamp(0, 100).toDouble());
      case 'badge':
        return Container(
          padding: const EdgeInsets.symmetric(horizontal: 8, vertical: 2),
          decoration: BoxDecoration(
            color: FluentTheme.of(context).accentColor.withValues(alpha: 0.15),
            borderRadius: BorderRadius.circular(10),
          ),
          child: Text(_textOf(node.value), style: typography.caption),
        );
      case 'divider':
        return const Divider();
      case 'link':
        return SelectableText(node.href, style: typography.body);
      default:
        return Text('[${node.type}]', style: typography.caption);
    }
  }

  TextStyle? _textStyle(String style, Typography typography) => switch (style) {
        'title' => typography.subtitle,
        'caption' => typography.caption,
        'code' => typography.body?.copyWith(fontFamily: 'monospace'),
        _ => typography.body,
      };
}
