# Phase 0 POC 结论（选型验证）

> 状态：本机（Windows）验证已完成；Linux / macOS 冒烟结果待 `crossplatform-ci.yml` 首次运行后回填。
> 用户推送 main 分支即触发 CI，截图与日志见 Actions artifact `smoke-ubuntu-latest` / `smoke-macos-latest`。

## 计划中的三项 POC 验收

### a. macOS 头选型 —— ✅ 已定稿（比计划预期更优）

Uno.Templates 6.7.30 的 `desktop` 平台生成**单项目统一桌面头**（`net10.0-desktop`），
窗口宿主按 OS 原生分发（见 `src/Starcloud.Cross/Platforms/Desktop/Program.cs`）：

| OS | 宿主 | 说明 |
|---|---|---|
| Windows | `UseWin32()` | 本机开发/调试用（产品 Windows 版仍为独立 WinUI 3 应用） |
| Linux | `UseX11()` + `UseLinuxFrameBuffer()` | X11 直连（Wayland 会话经 XWayland 兼容）；无需 GTK3 运行时 |
| macOS | `UseMacOS()` | 原生 AppKit 宿主，非 Catalyst、非 GTK |

**结论**：计划中「Catalyst vs Skia.GTK」的选型题消失——统一头即原生宿主，
并连带消除了 Linux AppImage 打包 GTK3 的风险。macOS runner 冒烟为软门槛，结果回填下表。

| 验证项 | 状态 |
|---|---|
| Windows 本机构建 | ✅ 0 警告 0 错误 |
| Windows 本机拉起（Win32 宿主，8s 存活） | ✅ |
| ubuntu-latest 构建 + Xvfb 冒烟截图 | ⏳ 待 CI |
| macos-latest 构建 + AppKit 冒烟 | ⏳ 待 CI（软门槛） |

### b. 图标字体（Uno.Fonts.Fluent 2.9.4）—— ⏳ 待 CI 截图确认

- 包已引入（`Directory.Packages.props`），POC 页含 `E768 / E8B7 / E713` 三个字形与中文文案。
- **待确认**：非 Windows 头上裸 `<FontIcon Glyph>` 是否自动应用该字体；
  若 CI 截图出现「豆腐块」，修复方向是为 FontIcon 显式指定 FontFamily（Phase 4 有 95 字形全量核对兜底）。

### c. Uno WebView 能力（3D 皮肤 / 插件面板）—— 🔜 推迟到 Phase 3

- 构建产物含 `WebView2.dll`（Windows 头的 WebView 支撑已就位）。
- 非 Windows 头（X11/AppKit）上 `WebView` 控件的可用性与截帧等效 API，在移植
  `Skin3DView` 时做小步探针；失败则按计划降级为 2D 头像（crafatar 面部），不阻塞主链路。

## 其他记录

- **Windows 零回归基线**（Phase 0 时点）：`dotnet build dotnet/Starcloud.slnx` 0 警告 0 错误；
  Core 测试 173/173 通过。后续每个 Phase 以此为门槛。
- **TFFE/目标框架**：跨平台应用 net10.0（Uno 6.7 最低支持线），Core 保持 net8.0 不动
  （net10 应用引用 net8 库为前向兼容）。
- **测试矩阵**：Phase 0 的 core-tests 仅 Windows——`SelfUpdateServiceTests.DetectInstallForm_*`
  依赖注册表；Phase 1 平台化后扩为 windows / ubuntu / macos 三矩阵。
- **macOS 分发提示**：无 Apple 开发者账号，产物未签名——Gatekeeper 会拦，用户需
  `xattr -dr com.apple.quarantine Starcloud.Cross.app`（详见 Phase 5 的 CROSSPLATFORM.md）。
