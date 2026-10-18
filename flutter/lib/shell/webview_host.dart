/// 嵌入式 WebView（Linux 用 CEF 离屏渲染到 Flutter 纹理）。
///
/// 用途：1.x HTML 插件面板、实验室 deepslate（WebGL）页面。
/// CEF 首次初始化较慢（解压运行时），因此只初始化一次并复用。
library;

import 'dart:async';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:webview_cef/webview_cef.dart';

class WebViewHost extends StatefulWidget {
  const WebViewHost({
    super.key,
    required this.url,
    this.onReady,
    this.onPageFinished,
    this.onConsole,
  });

  final String url;

  /// 视图创建完成（可调用 evaluateJavascript）。
  final void Function(WebViewController controller)? onReady;

  /// 页面加载完成。
  final void Function(WebViewController controller)? onPageFinished;

  /// 页面 console 输出（调试用）。
  final void Function(String message)? onConsole;

  @override
  State<WebViewHost> createState() => _WebViewHostState();
}

class _WebViewHostState extends State<WebViewHost> {
  static Future<void>? _initializing;

  WebViewController? _controller;
  bool _ready = false;
  Object? _error;

  @override
  void initState() {
    super.initState();
    unawaited(_start());
  }

  Future<void> _start() async {
    try {
      _initializing ??= WebviewManager().initialize();
      await _initializing;
      final controller = WebviewManager().createWebView(
        loading: const Center(child: ProgressRing()),
      );
      controller.setWebviewListener(WebviewEventsListener(
        onConsoleMessage: (level, message, source, line) =>
            widget.onConsole?.call(message),
        onLoadEnd: (controller, url) =>
            widget.onPageFinished?.call(controller),
      ));
      await controller.initialize(widget.url);
      if (!mounted) {
        unawaited(controller.dispose());
        return;
      }
      setState(() {
        _controller = controller;
        _ready = true;
      });
      widget.onReady?.call(controller);
    } catch (e) {
      if (!mounted) return;
      setState(() => _error = e);
    }
  }

  @override
  void dispose() {
    unawaited(_controller?.dispose());
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final error = _error;
    if (error != null) {
      return InfoBar(
        title: const Text('WebView 初始化失败'),
        content: Text('$error'),
        severity: InfoBarSeverity.error,
        isLong: true,
      );
    }
    if (!_ready || _controller == null) {
      return const Center(child: ProgressRing());
    }
    return _controller!.webviewWidget;
  }
}
