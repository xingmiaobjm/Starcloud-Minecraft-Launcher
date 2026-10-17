/// 入口：先启动插件内核，再挂 Fluent 壳。
///
/// 顺序是刻意的：内核 `start()` 之后贡献点表已经就绪，壳的第一帧就能渲染出
/// 完整的导航（不会先闪一下空窗口）。
library;

import 'package:flutter/widgets.dart' show WidgetsFlutterBinding, runApp;
import 'package:flutter_riverpod/flutter_riverpod.dart';

import 'bootstrap.dart';
import 'shell/app.dart';

Future<void> main() async {
  WidgetsFlutterBinding.ensureInitialized();
  final kernel = await bootstrapKernel();
  runApp(
    ProviderScope(
      overrides: [kernelProvider.overrideWithValue(kernel)],
      child: const StarcloudApp(),
    ),
  );
}
