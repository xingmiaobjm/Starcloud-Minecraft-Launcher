/// Nova AI 助手契约：LLM 配置 + 流式对话 + 工具调用提示。
library;

class NovaMessage {
  const NovaMessage({required this.role, required this.content});

  /// user / assistant
  final String role;
  final String content;
}

class NovaConfig {
  const NovaConfig({this.baseUrl = '', this.apiKey = '', this.model = ''});

  /// OpenAI 兼容端点（如 http://127.0.0.1:1234/v1）。
  final String baseUrl;
  final String apiKey;
  final String model;

  bool get configured => baseUrl.trim().isNotEmpty && model.trim().isNotEmpty;
}

class NovaPlanItem {
  const NovaPlanItem({required this.title, required this.detail});
  final String title;
  final String detail;
}

sealed class NovaEvent {
  const NovaEvent();
}

class NovaDelta extends NovaEvent {
  const NovaDelta(this.text);
  final String text;
}

/// 模型正在调用某个工具（UI 可显示"正在查询…"）。
class NovaTool extends NovaEvent {
  const NovaTool(this.tool);
  final String tool;
}

class NovaDone extends NovaEvent {
  const NovaDone({
    required this.text,
    this.fromCloud = false,
    this.plan = const [],
  });

  final String text;
  final bool fromCloud;

  /// 非空 = 模型给出了需要确认的操作清单（最小实现只展示，不执行）。
  final List<NovaPlanItem> plan;
}

class NovaFailed extends NovaEvent {
  const NovaFailed(this.error);
  final String error;
}

abstract class NovaClient {
  Future<NovaConfig> config();

  /// 保存/清除自接模型配置（三项都留空 = 清除）。
  Future<NovaConfig> saveConfig(NovaConfig config);

  /// 发送一条消息。事件流只包含本次对话的增量/工具/终态。
  Stream<NovaEvent> chat({
    required String message,
    List<NovaMessage> history = const [],
    String? versionId,
  });
}
