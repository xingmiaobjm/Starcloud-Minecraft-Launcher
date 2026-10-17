# Starcloud Launcher（Flutter 跨平台版）

> 微软 Fluent 设计系统（[fluent_ui](https://pub.dev/packages/fluent_ui)）+ Flutter。
> **Linux 端先行**，macOS 共用同一套 UI 源码与插件内核。
> 插件模型规范：[`docs/plugin-spec-v3.md`](../docs/plugin-spec-v3.md)（内核 + 插件，六端共用一份）。

## 运行

```bash
# 需要中国镜像时（WSL 里已写入 /etc/profile.d/flutter.sh）
export FLUTTER_STORAGE_BASE_URL=https://storage.flutter-io.cn
export PUB_HOSTED_URL=https://pub.flutter-io.cn

flutter pub get
flutter run -d linux      # WSLg 下直接弹窗
flutter test              # 内核单元测试（离线，无窗口）
flutter analyze
```

## 分层（规范 §12 的落地）

```
lib/
  kernel/     内核：零业务词。描述符/状态机/契约注册表/事件总线/贡献点注册表/
              调度器抽象/路径三根/插件上下文/插件宿主
  contracts/  宿主契约定义（按能力切、纯抽象类）
  plugins/
    builtin/  内置插件（编译期登记 —— 规范 §0.3 决策①）
    external/ 外部（沙箱）插件宿主 —— 基座期占位，未实现
  shell/      外壳：Fluent 主题、导航、页面宿主、插件诊断页
```

**"万物皆插件"**：壳的导航项**不是硬编码**，由内置插件 `starcloud.shell_pages`
经贡献点注册表挂上来；壳只消费贡献点表。导航最后的"插件"诊断页实时展示每个
插件的状态 / 激活耗时 / 贡献数 / 错误（规范 §4.1 的可观测性要求）。

### 已声明的取舍（与规范的差异）

1. 内核允许 import Flutter（贡献点里的页面工厂是 `WidgetBuilder`）——本端就是
   Flutter 宿主；"引擎无关"由"内核零业务词、壳零硬编码导航"保证。
2. 契约键用 Dart `Type`（内置插件编译期登记）。外部插件 manifest 的契约名字符串
   将来由宿主做"名字 → Type"映射（随 v2 沙箱合并一起做）。

### 基座验收（2026-10-18 实测）

- `flutter analyze`：**No issues found**
- `flutter test`：**61 用例全绿**（覆盖规范 §13 可离线验证的全部条目：
  描述符校验 / 注册表语义 / 生命周期与激活次序 / 循环依赖 / 懒激活 /
  requires·depends 自动处理与反向级联 / essential / conflicts / 事件总线隔离 /
  贡献点增删与懒创建 / 三根路径 / 配置原子写与损坏容忍）
- WSLg `flutter build linux --debug` + 运行：窗口弹出，11 个导航项由
  `starcloud.shell_pages`（10 项）与 `starcloud.plugin_manager`（1 项）两个内置
  插件贡献；诊断页显示两者 Active。截图：[`docs/images/flutter-base-linux.png`](../docs/images/flutter-base-linux.png)
- 启动日志（WSLg 实测）：

  ```
  [kernel/info] 内核初始化（apiVersion 3.0）
  [kernel/info] 插件 starcloud.shell_pages 已激活（11ms，贡献 20，契约 0）
  [kernel/info] 插件 starcloud.plugin_manager 已激活（1ms，贡献 2，契约 0）
  ```

### 已知项

- fluent_ui 在 Linux(GTK3) 的窗口装饰由 WM 提供，与 Windows 标题栏样式有差异。
- 内核跑在 Dart 单事件循环上，注册表"线程安全"由此保证；后台计算一律走注入的
  调度器 `spawn_background`（Isolate），不跨隔离共享可变状态（规范 §11.3）。
