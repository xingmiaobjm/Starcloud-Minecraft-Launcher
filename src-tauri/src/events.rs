//! 前端事件定义 - 事件名常量与负载结构
//!
//! 后端通过 `AppHandle::emit` 向所有窗口广播；前端用
//! `@tauri-apps/api/event` 的 `listen` 订阅。

use serde::Serialize;

/// 启动器状态事件（idle/downloading/installing/launching/running/error）
pub const EVENT_STATUS: &str = "launcher-status";
/// 下载进度事件
pub const EVENT_DOWNLOAD_PROGRESS: &str = "download-progress";
/// 安装完成事件
pub const EVENT_INSTALL_DONE: &str = "install-done";
/// 游戏输出行事件
pub const EVENT_GAME_LOG: &str = "game-log";
/// 游戏退出事件
pub const EVENT_GAME_EXITED: &str = "game-exited";
/// 错误事件
pub const EVENT_ERROR: &str = "error-occurred";
/// 启动器自身日志事件（预留：日志页可订阅）
#[allow(dead_code)]
pub const EVENT_LAUNCHER_LOG: &str = "launcher-log";

/// 启动器状态负载
#[derive(Debug, Clone, Serialize)]
pub struct StatusPayload {
    /// 状态标识
    pub status: String,
    /// 附加说明（如当前阶段）
    pub detail: Option<String>,
}

/// 下载进度负载（与 ProgressInfo 对应）
#[derive(Debug, Clone, Serialize)]
pub struct ProgressPayload {
    pub version_id: String,
    pub downloaded_bytes: u64,
    pub total_bytes: u64,
    pub percentage: f64,
    pub stage: String,
    pub status: String,
}

impl ProgressPayload {
    /// 从核心 ProgressInfo 构建
    pub fn from_progress(
        version_id: &str,
        info: &starcloud_minecraft::download::ProgressInfo,
    ) -> Self {
        Self {
            version_id: version_id.to_string(),
            downloaded_bytes: info.downloaded_bytes,
            total_bytes: info.total_bytes,
            percentage: info.percentage,
            stage: info.stage.clone(),
            status: format!("{:?}", info.status),
        }
    }
}

/// 安装完成负载
#[derive(Debug, Clone, Serialize)]
pub struct InstallDonePayload {
    pub version_id: String,
    pub success: bool,
    pub message: String,
}

/// 游戏输出行负载
#[derive(Debug, Clone, Serialize)]
pub struct GameLogPayload {
    pub line: String,
    pub is_stderr: bool,
}

/// 游戏退出负载
#[derive(Debug, Clone, Serialize)]
pub struct GameExitedPayload {
    pub exit_code: Option<i32>,
    pub crashed: bool,
    pub message: String,
}

/// 错误负载
#[derive(Debug, Clone, Serialize)]
pub struct ErrorPayload {
    pub context: String,
    pub message: String,
}
