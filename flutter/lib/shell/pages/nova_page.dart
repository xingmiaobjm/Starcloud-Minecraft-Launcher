/// Nova 页：AI 助手对话（流式）+ 自接模型配置。
///
/// 最小实现：只读工具（版本/下载/诊断/冲突/联机指引）；写操作确认清单暂不执行。
library;

import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:flutter_riverpod/flutter_riverpod.dart';

import '../../../bootstrap.dart';
import '../../../contracts/nova.dart';

class NovaPage extends ConsumerStatefulWidget {
  const NovaPage({super.key});

  @override
  ConsumerState<NovaPage> createState() => _NovaPageState();
}

class _NovaPageState extends ConsumerState<NovaPage> {
  final List<_ChatMessage> _messages = [];
  final _input = TextEditingController();
  final _scroll = ScrollController();
  NovaConfig? _config;
  bool _loadingConfig = true;
  bool _busy = false;
  String? _tool;
  String? _error;
  StreamSubscription<NovaEvent>? _sub;

  NovaClient get _nova => ref.read(kernelDepsProvider).nova;

  @override
  void initState() {
    super.initState();
    unawaited(_loadConfig());
  }

  @override
  void dispose() {
    unawaited(_sub?.cancel());
    _input.dispose();
    _scroll.dispose();
    super.dispose();
  }

  Future<void> _loadConfig() async {
    try {
      final config = await _nova.config();
      if (!mounted) return;
      setState(() {
        _config = config;
        _loadingConfig = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        _loadingConfig = false;
        _error = '$e';
      });
    }
  }

  Future<void> _openConfig() async {
    final saved = await showDialog<NovaConfig>(
      context: context,
      builder: (context) => _NovaConfigDialog(initial: _config ?? const NovaConfig()),
    );
    if (saved == null || !mounted) return;
    try {
      final config = await _nova.saveConfig(saved);
      if (!mounted) return;
      setState(() => _config = config);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = '$e');
    }
  }

  void _send() {
    final text = _input.text.trim();
    if (text.isEmpty || _busy) return;
    final history = [
      for (final message in _messages)
        if (!message.streaming && message.error == null)
          NovaMessage(role: message.role, content: message.text),
    ];
    final assistant = _ChatMessage(role: 'assistant', text: '', streaming: true);
    setState(() {
      _messages.add(_ChatMessage(role: 'user', text: text));
      _messages.add(assistant);
      _busy = true;
      _tool = null;
      _error = null;
    });
    _input.clear();
    _scrollToEnd();

    _sub = _nova.chat(message: text, history: history).listen((event) {
      if (!mounted) return;
      if (event is NovaDelta) {
        setState(() => assistant.text += event.text);
        _scrollToEnd();
      } else if (event is NovaTool) {
        setState(() => _tool = event.tool);
      } else if (event is NovaDone) {
        setState(() {
          if (event.text.isNotEmpty) assistant.text = event.text;
          assistant.streaming = false;
          assistant.plan = event.plan;
          assistant.planId = event.planId;
          assistant.planTitle = event.planTitle;
          _busy = false;
          _tool = null;
        });
        _scrollToEnd();
      } else if (event is NovaFailed) {
        setState(() {
          assistant.streaming = false;
          assistant.error = event.error;
          _busy = false;
          _tool = null;
        });
      }
    }, onError: (Object e) {
      if (!mounted) return;
      setState(() {
        assistant.streaming = false;
        assistant.error = '$e';
        _busy = false;
        _tool = null;
      });
    });
  }

  void _scrollToEnd() {
    WidgetsBinding.instance.addPostFrameCallback((_) {
      if (!_scroll.hasClients) return;
      _scroll.animateTo(
        _scroll.position.maxScrollExtent,
        duration: const Duration(milliseconds: 120),
        curve: Curves.easeOut,
      );
    });
  }

  Future<void> _executePlan(_ChatMessage message) async {
    final planId = message.planId;
    if (planId == null) return;
    setState(() {
      message.planBusy = true;
      _error = null;
    });
    try {
      final result = await _nova.executePlan(planId);
      if (!mounted) return;
      setState(() {
        message.planResult = result;
        message.planId = null;
        message.planBusy = false;
      });
    } catch (e) {
      if (!mounted) return;
      setState(() {
        message.planBusy = false;
        message.planResult = '执行失败：$e';
      });
    }
  }

  Future<void> _discardPlan(_ChatMessage message) async {
    final planId = message.planId;
    if (planId == null) return;
    setState(() => message.planId = null);
    try {
      await _nova.discardPlan(planId);
    } catch (_) {
      // 清单过期时丢弃失败无需打扰用户
    }
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    final config = _config;
    return ScaffoldPage(
      header: PageHeader(
        title: const Text('Nova'),
        commandBar: CommandBar(
          mainAxisAlignment: MainAxisAlignment.end,
          primaryItems: [
            CommandBarButton(
              icon: const Icon(FluentIcons.settings),
              label: const Text('模型设置'),
              onPressed: _openConfig,
            ),
            CommandBarButton(
              icon: const Icon(FluentIcons.delete),
              label: const Text('清空'),
              onPressed: _busy
                  ? null
                  : () => setState(() {
                        _messages.clear();
                        _error = null;
                      }),
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
                title: const Text('出错了'),
                content: Text(_error!),
                severity: InfoBarSeverity.error,
                isLong: true,
                onClose: () => setState(() => _error = null),
              ),
              const SizedBox(height: 12),
            ],
            if (!_loadingConfig && config != null && !config.configured) ...[
              InfoBar(
                title: const Text('未配置模型'),
                content: const Text(
                  '本地能力（诊断启动问题、冲突扫描、联机指引）无需模型即可使用；'
                  '接入 OpenAI 兼容模型后可以自由对话。点右上角「模型设置」。',
                ),
                severity: InfoBarSeverity.warning,
                isLong: true,
              ),
              const SizedBox(height: 12),
            ],
            Expanded(
              child: _messages.isEmpty
                  ? Center(
                      child: Text(
                        'Nova 可以诊断启动问题、解释崩溃、找模组/整合包、指导联机。\n'
                        '试试：「诊断 1.20.4」「检查冲突」「怎么和朋友联机」',
                        textAlign: TextAlign.center,
                        style: typography.body,
                      ),
                    )
                  : ListView.builder(
                      controller: _scroll,
                      itemCount: _messages.length,
                      itemBuilder: (context, index) =>
                          _bubble(_messages[index], typography),
                    ),
            ),
            if (_tool != null)
              Padding(
                padding: const EdgeInsets.only(bottom: 6),
                child: Row(children: [
                  const ProgressRing(strokeWidth: 2),
                  const SizedBox(width: 8),
                  Text('正在调用工具：$_tool', style: typography.caption),
                ]),
              ),
            Row(children: [
              Expanded(
                child: TextBox(
                  controller: _input,
                  placeholder: '问 Nova：诊断启动问题、找资源、联机指引…',
                  onSubmitted: (_) => _send(),
                ),
              ),
              const SizedBox(width: 8),
              FilledButton(
                onPressed: _busy ? null : _send,
                child: Text(_busy ? '回复中…' : '发送'),
              ),
            ]),
          ],
        ),
      ),
    );
  }

  Widget _bubble(_ChatMessage message, Typography typography) {
    final user = message.role == 'user';
    return Align(
      alignment: user ? Alignment.centerRight : Alignment.centerLeft,
      child: Container(
        constraints: const BoxConstraints(maxWidth: 720),
        margin: const EdgeInsets.only(bottom: 10),
        padding: const EdgeInsets.symmetric(horizontal: 14, vertical: 10),
        decoration: BoxDecoration(
          color: user
              ? FluentTheme.of(context).accentColor.withValues(alpha: 0.18)
              : FluentTheme.of(context).resources.cardBackgroundFillColorDefault,
          borderRadius: BorderRadius.circular(8),
        ),
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            if (message.streaming && message.text.isEmpty)
              const Row(children: [
                ProgressRing(strokeWidth: 2),
                SizedBox(width: 8),
                Text('思考中…'),
              ])
            else
              SelectableText(message.text, style: typography.body),
            if (message.error != null) ...[
              const SizedBox(height: 6),
              Text(message.error!,
                  style: typography.caption?.copyWith(color: Colors.red)),
            ],
            if (message.plan.isNotEmpty) ...[
              const SizedBox(height: 8),
              Card(
                padding: const EdgeInsets.all(10),
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(message.planTitle ?? '建议操作',
                        style: typography.bodyStrong),
                    const SizedBox(height: 4),
                    for (final item in message.plan)
                      Padding(
                        padding: const EdgeInsets.only(bottom: 2),
                        child: Text('• ${item.title}：${item.detail}',
                            style: typography.caption),
                      ),
                    if (message.planResult != null) ...[
                      const SizedBox(height: 6),
                      Text(message.planResult!, style: typography.caption),
                    ] else if (message.planId != null) ...[
                      const SizedBox(height: 8),
                      Row(children: [
                        FilledButton(
                          onPressed: message.planBusy || _busy
                              ? null
                              : () => _executePlan(message),
                          child: Text(message.planBusy ? '执行中…' : '执行'),
                        ),
                        const SizedBox(width: 8),
                        Button(
                          onPressed:
                              message.planBusy ? null : () => _discardPlan(message),
                          child: const Text('取消'),
                        ),
                      ]),
                    ],
                  ],
                ),
              ),
            ],
          ],
        ),
      ),
    );
  }
}

class _ChatMessage {
  _ChatMessage({required this.role, required this.text, this.streaming = false});

  final String role;
  String text;
  bool streaming;
  String? error;
  List<NovaPlanItem> plan = const [];
  String? planId;
  String? planTitle;
  bool planBusy = false;
  String? planResult;
}

/// 自接模型配置（OpenAI 兼容端点）。
class _NovaConfigDialog extends StatefulWidget {
  const _NovaConfigDialog({required this.initial});

  final NovaConfig initial;

  @override
  State<_NovaConfigDialog> createState() => _NovaConfigDialogState();
}

class _NovaConfigDialogState extends State<_NovaConfigDialog> {
  late final _baseUrl = TextEditingController(text: widget.initial.baseUrl);
  late final _apiKey = TextEditingController(text: widget.initial.apiKey);
  late final _model = TextEditingController(text: widget.initial.model);

  @override
  void dispose() {
    _baseUrl.dispose();
    _apiKey.dispose();
    _model.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final typography = FluentTheme.of(context).typography;
    return ContentDialog(
      title: const Text('Nova 模型设置'),
      content: SizedBox(
        width: 520,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.stretch,
          children: [
            Text(
              'OpenAI 兼容端点，例如 http://127.0.0.1:1234/v1（LM Studio / Ollama）'
              ' 或 https://api.openai.com/v1。三项都留空 = 只用本地能力。',
              style: typography.caption,
            ),
            const SizedBox(height: 12),
            InfoLabel(
              label: 'Base URL',
              child: TextBox(
                controller: _baseUrl,
                placeholder: 'http://127.0.0.1:1234/v1',
              ),
            ),
            const SizedBox(height: 8),
            InfoLabel(
              label: 'API Key（本地推理可留空）',
              child: TextBox(
                controller: _apiKey,
                obscureText: true,
                placeholder: 'sk-…',
              ),
            ),
            const SizedBox(height: 8),
            InfoLabel(
              label: '模型名',
              child: TextBox(
                controller: _model,
                placeholder: '如 qwen2.5-7b-instruct / gpt-4o-mini',
              ),
            ),
          ],
        ),
      ),
      actions: [
        Button(
          onPressed: () => Navigator.pop(
            context,
            const NovaConfig(),
          ),
          child: const Text('清除配置'),
        ),
        Button(
          onPressed: () => Navigator.pop(context),
          child: const Text('取消'),
        ),
        FilledButton(
          onPressed: () => Navigator.pop(
            context,
            NovaConfig(
              baseUrl: _baseUrl.text.trim(),
              apiKey: _apiKey.text.trim(),
              model: _model.text.trim(),
            ),
          ),
          child: const Text('保存'),
        ),
      ],
    );
  }
}
