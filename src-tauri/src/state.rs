//! 全局应用状态 - 配置、游戏进程句柄

use starcloud_minecraft::config::LauncherConfig;
use std::sync::Mutex;

/// 全局应用状态
pub struct AppState {
    /// 启动器配置（设置页可读写）
    pub config: Mutex<LauncherConfig>,
    /// 运行中的游戏进程 PID（用于停止游戏）
    pub game_pid: Mutex<Option<u32>>,
    /// 游戏运行状态
    pub game_running: Mutex<bool>,
}

impl AppState {
    /// 创建应用状态
    pub fn new(config: LauncherConfig) -> Self {
        Self {
            config: Mutex::new(config),
            game_pid: Mutex::new(None),
            game_running: Mutex::new(false),
        }
    }
}
