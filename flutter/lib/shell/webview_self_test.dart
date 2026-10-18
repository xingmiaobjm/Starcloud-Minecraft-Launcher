/// WebView 自检入口：`STARCLOUD_WEBVIEW_SELFTEST=1` 时由 main 调用。
///
/// 加载一页本地 HTML，等 onLoadEnd 后读回 DOM 文本，打印
/// `WEBVIEW_SELFTEST_OK <text>` 并退出，用于无头/半无头环境验证 CEF 嵌入渲染。
library;

import 'dart:async';
import 'dart:io';

import 'package:fluent_ui/fluent_ui.dart';
import 'package:webview_cef/webview_cef.dart';

import 'theme.dart';
import 'webview_host.dart';

Future<void> runWebviewSelfTest() async {
  final dir = await Directory.systemTemp.createTemp('starcloud_webview_selftest');
  final file = File('${dir.path}/index.html');
  file.writeAsStringSync('''<!doctype html>
<html><head><meta charset="utf-8"><title>cef-selftest</title></head>
<body style="background:#1b1b1b;color:#f5f5f5;font-family:sans-serif">
  <h1 id="t">CEF OK</h1>
  <p id="s">embedded webview self test</p>
  <script>console.log('selftest-console-line');</script>
</body></html>''');
  final url = Uri.file(file.path).toString();
  stdout.writeln('[webview] selftest url=$url');

  final timeout = Timer(const Duration(seconds: 90), () {
    stdout.writeln('WEBVIEW_SELFTEST_TIMEOUT');
    exit(1);
  });

  runApp(_SelfTestApp(
    url: url,
    onFinished: (controller) async {
      try {
        final value = await controller.evaluateJavascript(
            "document.getElementById('t').innerText");
        stdout.writeln('WEBVIEW_SELFTEST_OK $value');
      } catch (e) {
        stdout.writeln('WEBVIEW_SELFTEST_EVAL_FAILED $e');
      }
      timeout.cancel();
      await stdout.flush();
      exit(0);
    },
  ));
}

class _SelfTestApp extends StatelessWidget {
  const _SelfTestApp({required this.url, required this.onFinished});

  final String url;
  final Future<void> Function(WebViewController controller) onFinished;

  @override
  Widget build(BuildContext context) {
    return FluentApp(
      title: 'Starcloud WebView Self Test',
      theme: buildLauncherTheme(),
      home: ScaffoldPage(
        content: SizedBox.expand(
          child: WebViewHost(
            url: url,
            onPageFinished: (controller) => unawaited(onFinished(controller)),
            onConsole: (message) =>
                stdout.writeln('[webview/console] $message'),
          ),
        ),
      ),
    );
  }
}
