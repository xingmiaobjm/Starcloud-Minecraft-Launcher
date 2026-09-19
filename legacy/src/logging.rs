//! 日志系统初始化
//!
//! 组装 tracing 日志栈：stdout 格式化输出 + 滚动文件（按日滚动），
//! 并通过 `tracing-log` 桥接标准 `log` 宏（库内大量使用 log::info 等）。
//!
//! 可通过环境变量 `RUST_LOG` 覆盖日志级别（默认 info）。

use std::path::{Path, PathBuf};
use std::sync::OnceLock;

use tracing_appender::non_blocking::WorkerGuard;
use tracing_subscriber::prelude::*;
use tracing_subscriber::EnvFilter;

use crate::error::{LauncherError, Result};

/// 持有 non-blocking 写入器的 guard，防止日志线程被提前回收
static LOG_GUARD: OnceLock<WorkerGuard> = OnceLock::new();

/// 初始化日志系统（幂等：重复调用直接返回）
///
/// - `log_dir`：日志文件目录（滚动文件名为 `launcher.log`，按日滚动）
/// - `level`：默认日志级别，如 "info"、"debug"；被 `RUST_LOG` 覆盖
pub fn init_logging(log_dir: impl AsRef<Path>, level: &str) -> Result<()> {
    if tracing::dispatcher::has_been_set() {
        return Ok(());
    }

    let log_dir = log_dir.as_ref();
    std::fs::create_dir_all(log_dir)?;

    // 滚动文件：daily 保留 30 天左右（tracing-appender 默认按文件数清理）
    let file_appender = tracing_appender::rolling::daily(log_dir, "launcher.log");
    let (file_writer, guard) = tracing_appender::non_blocking(file_appender);
    let _ = LOG_GUARD.set(guard);

    let filter = EnvFilter::try_from_default_env().unwrap_or_else(|_| EnvFilter::new(level));

    let subscriber = tracing_subscriber::registry()
        .with(filter)
        .with(
            tracing_subscriber::fmt::layer()
                .with_writer(std::io::stdout)
                .with_target(false)
                .with_ansi(true),
        )
        .with(
            tracing_subscriber::fmt::layer()
                .with_writer(file_writer)
                .with_target(false)
                .with_ansi(false),
        );

    tracing::subscriber::set_global_default(subscriber)
        .map_err(|e| LauncherError::Other(format!("初始化日志订阅器失败: {}", e)))?;

    // 桥接 log 宏（log::info! / log::warn! 等转发到 tracing）
    tracing_log::LogTracer::init()
        .map_err(|e| LauncherError::Other(format!("初始化日志桥接失败: {}", e)))?;

    log::info!("日志系统已初始化，日志目录: {}", log_dir.display());
    Ok(())
}

/// 获取默认日志目录（数据目录下的 starcloud-launcher/logs）
pub fn default_log_dir() -> PathBuf {
    dirs::data_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("starcloud-launcher")
        .join("logs")
}
