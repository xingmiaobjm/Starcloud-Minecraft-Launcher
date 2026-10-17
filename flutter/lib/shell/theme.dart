/// 主题：Fluent 暗色 + 星云品牌黄（与原 Windows 版视觉语言一致）。
library;

import 'package:fluent_ui/fluent_ui.dart';

/// 品牌黄（启动按钮 / 强调色；取自原启动器视觉）。
const Color kBrandYellow = Color(0xFFFFC21F);

/// 暗色底（与原应用一致的近黑层次）。
const Color kWindowBackground = Color(0xFF141414);
const Color kSurface = Color(0xFF1E1E1E);

FluentThemeData buildLauncherTheme() {
  return FluentThemeData(
    brightness: Brightness.dark,
    accentColor: AccentColor.swatch({
      'darkest': const Color(0xFFB8860B),
      'dark': const Color(0xFFE0A800),
      'normal': kBrandYellow,
      'light': const Color(0xFFFFD75E),
      'lightest': const Color(0xFFFFE9A8),
    }),
    scaffoldBackgroundColor: kWindowBackground,
    // 卡片/控件表面的暗色层次（Fluent 的 mica/acrylic 在 Linux 上由壳自绘，
    // 见 flutter/README.md 已知项）。
    acrylicBackgroundColor: kSurface.withValues(alpha: 0.86),
    micaBackgroundColor: kSurface,
  );
}
