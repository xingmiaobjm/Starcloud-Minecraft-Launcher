# legacy/ — 旧 Rust + Tauri 实现（仅作参考）

本目录是 2026-09 之前的主实现：Rust 核心库 + CLI、Tauri 2 桌面壳、Preact 前端。
2026-09-06 起主实现迁移到 **.NET 8 + WinUI 3**（[`dotnet/`](../dotnet)），本目录
**不再随新版本演进**，仅作为迁移期的行为参考与历史归档保留。

## 内容

| 路径 | 说明 |
| --- | --- |
| `Cargo.toml` / `Cargo.lock` | Rust workspace（根包为核心库 + CLI，成员含 `src-tauri`） |
| `src/` | Rust 核心库与 CLI（下载 / 启动 / 版本管理 / 账户 / ModLoader / 插件） |
| `src-tauri/` | Tauri 2 桌面壳（命令 / 事件 / 插件宿主） |
| `ui/` | Preact + Vite 前端 |
| `tests/` `benches/` | Rust 集成测试与 criterion 基准 |
| `examples/` | 迁移期示例（`basic_launcher.rs`、`gui-plugin-demo`） |
| `scripts/` | Tauri 时代的版本同步与打包脚本（`node scripts/*.mjs`，由 `package.json` 调用） |
| `clippy.toml` `rustfmt.toml` | Rust 静态检查与格式化配置 |
| `docs/` | 归档文档：机场导视设计语言、旧 GUI 插件开发指南 |

## 构建（一般不需要）

```bash
cd legacy
cargo build --release    # 需要 Rust 工具链
cargo test --workspace
```

依赖 Node 的打包流程（`legacy/package.json` + `legacy/scripts/`）同样在未维护状态下运行。

> `legacy/target/` 是旧构建缓存（可达数十 GB）。确认不再重建旧版后可直接删除，`cargo` 会重新生成。

## 当前实现

新实现位于 [`dotnet/`](../dotnet)（Windows / WinUI 3）与
[`crossplatform/`](../crossplatform)（Uno，macOS / Linux / Windows）。
插件系统已重写为 `.mclplug` 形态，见 [docs/plugin-spec.md](../docs/plugin-spec.md)。
