//! 全局应用状态 - 配置、游戏进程句柄、调度任务、安装取消句柄

use crate::tasks::TaskInfo;
use starcloud_minecraft::config::LauncherConfig;
use starcloud_minecraft::terracotta::TerracottaSession;
use std::collections::HashMap;
use std::sync::Mutex;
use tokio::sync::oneshot;

/// 全局应用状态
pub struct AppState {
    /// 启动器配置（设置页可读写）
    pub config: Mutex<LauncherConfig>,
    /// 运行中的游戏进程 PID（用于停止游戏）
    pub game_pid: Mutex<Option<u32>>,
    /// 游戏运行状态
    pub game_running: Mutex<bool>,
    /// 调度中心任务列表（priority 升序，0 最高）
    pub tasks: Mutex<Vec<TaskInfo>>,
    /// 进行中的安装任务取消句柄（键 = 版本 ID，`cancel_install` 发送取消信号）
    pub install_jobs: Mutex<HashMap<String, oneshot::Sender<()>>>,
    /// 运行中的陶瓦联机核心会话（含子进程与本地 HTTP 端口）
    pub terracotta: Mutex<Option<TerracottaSession>>,
}

impl AppState {
    /// 创建应用状态
    pub fn new(config: LauncherConfig) -> Self {
        Self {
            config: Mutex::new(config),
            game_pid: Mutex::new(None),
            game_running: Mutex::new(false),
            tasks: Mutex::new(crate::tasks::load_tasks()),
            install_jobs: Mutex::new(HashMap::new()),
            terracotta: Mutex::new(None),
        }
    }
}
