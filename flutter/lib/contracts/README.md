# contracts/ — 宿主契约定义

规范：[`docs/plugin-spec-v3.md`](../../docs/plugin-spec-v3.md) §5（契约即能力）。

- 契约 = **纯抽象类**（Dart `abstract class`），按**能力**切、不按现有类切；
  判据："存在第二种实现的可能"。
- 契约**属于宿主**（谁需要谁定义），不得放进插件；插件不得私自扩展别人的契约。
- 键用 Dart `Type`（内置插件编译期登记，见 [`flutter/README.md`](../README.md) 取舍 2）。
- 外部（沙箱）插件的 manifest 用契约名字符串——将来由宿主提供"名字 → Type"
  映射表，与 v2 沙箱合并一起做（规范 §0.3 决策①）。

## 基座期状态

**空**是刻意的：还没有任何业务插件需要跨插件契约（导航/页面走贡献点，
不经过契约）。第一个真实契约预计是下载引擎（`IDownloadEngine` 的 Dart 对应物），
随"最小可玩闭环"里程碑一起定义，并配上"两种实现可替换"的用例。

## 命名

Dart 侧不需要 `I` 前缀（抽象类本身就是接口），但 manifest/日志里报契约名时
用与规范一致的 `IXxx` 形态（`descriptor.requires` 的可读输出走 `Type.toString()`，
因此测试探针契约命名为 `IProbeA` 这类，与未来 manifest 名保持对齐）。
