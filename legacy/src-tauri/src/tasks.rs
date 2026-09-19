//! 调度中心 - 任务模型与持久化（tasks.json）
//!
//! 任务由安装/启动流程创建，支持拖拽调整优先级（priority 升序，0 最高）。
//! 正在执行（status = running）的任务禁止调整优先级。

use serde::{Deserialize, Serialize};
use std::path::PathBuf;

/// 任务清单文件名（数据目录下 starcloud-launcher/tasks.json）
pub const TASKS_FILE: &str = "tasks.json";

/// 保留的最大任务条数（超出时丢弃最旧的历史任务）
const MAX_TASKS: usize = 50;

/// 任务信息
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "camelCase")]
pub struct TaskInfo {
    /// 唯一 ID（kind + 创建时间戳）
    pub id: String,
    /// 任务类型：install | fabric | launch
    pub kind: String,
    /// 显示标题
    pub title: String,
    /// 附加说明（错误信息 / 结果信息）
    pub detail: Option<String>,
    /// 状态：running（执行中，禁止拖拽）| done | error
    pub status: String,
    /// 优先级（0 最高，列表顺序 = 优先级升序）
    pub priority: u32,
    /// 发起来源（如 user / plugin / system）
    #[serde(default = "default_source")]
    pub source: String,
    /// 任务日志行（阶段变更 / 关键事件，保留最近 50 条）
    #[serde(default)]
    pub log: Vec<String>,
    /// 进度百分比 0-100（仅 running 任务有意义）
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub progress: Option<f64>,
    /// 当前阶段名（如「下载库文件」）
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub stage: Option<String>,
    /// 实时下载速度（字节/秒）
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub speed: Option<f64>,
    /// 估算剩余秒数
    #[serde(default, skip_serializing_if = "Option::is_none")]
    pub eta: Option<f64>,
    /// 创建时间（毫秒时间戳）
    pub created_at: u64,
    /// 最后更新时间（毫秒时间戳）
    pub updated_at: u64,
}

/// 发起来源默认值（兼容旧数据）
fn default_source() -> String {
    "user".into()
}

/// 任务日志保留的最大行数
const MAX_LOG_LINES: usize = 50;

impl TaskInfo {
    /// 创建新任务（priority 0 = 置顶，running，来源默认 user）
    pub fn new(kind: &str, title: impl Into<String>) -> Self {
        let now = now_millis();
        Self {
            id: format!("{}-{}", kind, now),
            kind: kind.to_string(),
            title: title.into(),
            detail: None,
            status: "running".into(),
            priority: 0,
            source: default_source(),
            log: vec!["任务已创建".into()],
            progress: None,
            stage: None,
            speed: None,
            eta: None,
            created_at: now,
            updated_at: now,
        }
    }

    /// 追加一条日志并截断至上限
    pub fn push_log(&mut self, line: impl Into<String>) {
        self.log.push(line.into());
        if self.log.len() > MAX_LOG_LINES {
            let overflow = self.log.len() - MAX_LOG_LINES;
            self.log.drain(..overflow);
        }
    }
}

/// 数据目录下的任务文件路径
pub fn tasks_file_path() -> PathBuf {
    dirs::data_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("starcloud-launcher")
        .join(TASKS_FILE)
}

/// 从磁盘加载任务（文件不存在或损坏则返回空列表）
pub fn load_tasks() -> Vec<TaskInfo> {
    let path = tasks_file_path();
    match std::fs::read_to_string(&path) {
        Ok(text) => serde_json::from_str::<Vec<TaskInfo>>(&text).unwrap_or_default(),
        Err(_) => Vec::new(),
    }
}

/// 保存任务到磁盘（按 priority 排序后截断至 MAX_TASKS）
pub fn save_tasks(tasks: &[TaskInfo]) {
    let mut sorted: Vec<TaskInfo> = tasks.to_vec();
    sorted.sort_by_key(|t| t.priority);
    sorted.truncate(MAX_TASKS);
    if let Some(dir) = tasks_file_path().parent() {
        let _ = std::fs::create_dir_all(dir);
    }
    if let Ok(text) = serde_json::to_string_pretty(&sorted) {
        let _ = std::fs::write(tasks_file_path(), text);
    }
}

/// 当前毫秒时间戳
pub fn now_millis() -> u64 {
    std::time::SystemTime::now()
        .duration_since(std::time::UNIX_EPOCH)
        .map(|d| d.as_millis() as u64)
        .unwrap_or(0)
}
