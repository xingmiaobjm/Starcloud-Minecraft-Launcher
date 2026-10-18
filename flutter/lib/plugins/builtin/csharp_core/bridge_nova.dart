/// 桥接版 Nova：配置与流式对话都落到 C# 的 NovaService。
library;

import 'dart:async';

import '../../../bridge/core_bridge.dart';
import '../../../contracts/nova.dart';

class BridgeNova implements NovaClient {
  BridgeNova(this._bridge);

  final CoreBridge _bridge;

  @override
  Future<NovaConfig> config() async =>
      _configFrom(await _bridge.request('nova.config.get'));

  @override
  Future<NovaConfig> saveConfig(NovaConfig config) async =>
      _configFrom(await _bridge.request('nova.config.set', {
        'baseUrl': config.baseUrl,
        'apiKey': config.apiKey,
        'model': config.model,
      }));

  @override
  Stream<NovaEvent> chat({
    required String message,
    List<NovaMessage> history = const [],
    String? versionId,
  }) {
    late StreamController<NovaEvent> controller;
    StreamSubscription<BridgeEvent>? subscription;
    var closed = false;

    Future<void> finish() async {
      if (closed) return;
      closed = true;
      await subscription?.cancel();
      await controller.close();
    }

    controller = StreamController<NovaEvent>(
      onListen: () async {
        subscription = _bridge.events.listen((event) {
          if (closed) return;
          switch (event.name) {
            case 'nova.delta':
              controller.add(
                  NovaDelta(event.params['text'] as String? ?? ''));
            case 'nova.tool':
              controller.add(NovaTool(event.params['tool'] as String? ?? ''));
            case 'nova.done':
              controller.add(NovaDone(
                text: event.params['text'] as String? ?? '',
                fromCloud: event.params['fromCloud'] == true,
                planId: event.params['planId'] as String?,
                planTitle: event.params['planTitle'] as String?,
                plan: _planFrom(event.params['plan']),
              ));
              unawaited(finish());
            case 'nova.failed':
              controller.add(
                  NovaFailed(event.params['error'] as String? ?? 'Nova 出错了'));
              unawaited(finish());
          }
        });

        try {
          await _bridge.requestWithTimeout(
            'nova.chat',
            {
              'message': message,
              'history': [
                for (final turn in history)
                  {'role': turn.role, 'content': turn.content},
              ],
              'versionId': ?versionId,
            },
            const Duration(seconds: 30),
          );
        } catch (e) {
          if (!closed) controller.add(NovaFailed('$e'));
          await finish();
        }
      },
      onCancel: () async {
        closed = true;
        await subscription?.cancel();
      },
    );
    return controller.stream;
  }

  @override
  Future<String> executePlan(String planId) async {
    final result = await _bridge.requestWithTimeout(
      'nova.plan.execute',
      {'id': planId},
      const Duration(minutes: 5),
    );
    if (result is Map<String, Object?>) {
      return result['message'] as String? ?? '已执行';
    }
    return '已执行';
  }

  @override
  Future<void> discardPlan(String planId) async {
    await _bridge.request('nova.plan.discard', {'id': planId});
  }

  static NovaConfig _configFrom(Object? raw) {
    if (raw is! Map<String, Object?>) return const NovaConfig();
    return NovaConfig(
      baseUrl: raw['baseUrl'] as String? ?? '',
      apiKey: raw['apiKey'] as String? ?? '',
      model: raw['model'] as String? ?? '',
    );
  }

  static List<NovaPlanItem> _planFrom(Object? raw) {
    if (raw is! List<Object?>) return const [];
    final out = <NovaPlanItem>[];
    for (final item in raw) {
      if (item is! Map<String, Object?>) continue;
      out.add(NovaPlanItem(
        title: item['title'] as String? ?? '',
        detail: item['detail'] as String? ?? '',
      ));
    }
    return out;
  }
}
