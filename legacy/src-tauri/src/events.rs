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
/// 调度中心任务列表更新事件（任务创建/状态变更/拖拽重排后广播）
pub const EVENT_TASKS_UPDATED: &str = "tasks-updated";
/// 陶瓦联机状态事件（核心会话轮询结果广播）
pub const EVENT_TERRACOTTA_STATE: &str = "terracotta-state";
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
    /// 实时速度（字节/秒，EMA 平滑）
    pub speed_bytes_per_sec: f64,
    /// 估算剩余时间（秒）
    pub eta_secs: Option<f64>,
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
            speed_bytes_per_sec: info.speed_bytes_per_sec,
            eta_secs: info.eta_secs,
        }
    }
}

/// 安装完成负载
#[derive(Debug, Clone, Serialize)]
pub struct InstallDonePayload {
    /// 前端"正在下载"状态的键（= 安装命令的版本 ID，Fabric 为 MC 版本号）
    pub job_id: String,
    /// 实际安装的版本 ID（失败时回退为 job_id）
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

/// 调度中心任务列表负载
#[derive(Debug, Clone, Serialize)]
pub struct TasksUpdatedPayload {
    pub tasks: Vec<crate::tasks::TaskInfo>,
}

/// 陶瓦联机状态负载（轮询器周期性广播 + `terracotta_status` 命令快照）
#[derive(Debug, Clone, Serialize)]
pub struct TerracottaStatePayload {
    /// 核心是否已安装
    pub installed: bool,
    /// 已安装的核心版本（未安装为 None）
    pub version: Option<String>,
    /// 核心会话是否运行中
    pub running: bool,
    /// 当前联机状态（kebab-case；会话未运行时为 None）
    pub state: Option<starcloud_minecraft::terracotta::TerracottaState>,
    /// 最近一次查询错误（如核心无响应）
    pub error: Option<String>,
}
