use std::sync::atomic::{AtomicU64, Ordering};
use std::collections::HashMap;
use tokio::sync::RwLock;

/// 下载进度追踪器 - 线程安全
pub struct ProgressTracker {
    /// 当前进度（字节）
    current: AtomicU64,
    /// 总大小（字节）
    total: AtomicU64,
    /// 当前进度百分比 (0.0 - 1.0)
    percentage: AtomicU64, // 存储为 u64，实际使用时除以 1e6
    /// 状态
    status: RwLock<ProgressStatus>,
    /// 额外的阶段进度（用于细分不同下载阶段）
    #[allow(dead_code)]
    stages: RwLock<HashMap<String, StageProgress>>,
    /// 回调函数列表
    callbacks: RwLock<Vec<Box<dyn Fn(&ProgressInfo) + Send + Sync>>>,
}

/// 进度信息快照
#[derive(Debug, Clone)]
pub struct ProgressInfo {
    /// 已下载字节
    pub downloaded_bytes: u64,
    /// 总字节
    pub total_bytes: u64,
    /// 百分比 (0.0 ~ 100.0)
    pub percentage: f64,
    /// 当前阶段
    pub stage: String,
    /// 状态
    pub status: ProgressStatus,
    /// 估算剩余时间（秒）
    pub eta_secs: Option<f64>,
    /// 速度（字节/秒）
    pub speed_bytes_per_sec: f64,
}

/// 进度状态
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum ProgressStatus {
    /// 等待开始
    Pending,
    /// 下载中
    Downloading,
    /// 校验中
    Verifying,
    /// 解压中
    Extracting,
    /// 已完成
    Completed,
    /// 已失败
    Failed(String),
    /// 已取消
    Cancelled,
}

/// 阶段进度
#[derive(Debug, Clone)]
#[allow(dead_code)]
struct StageProgress {
    total: u64,
    current: u64,
    start_time: Option<std::time::Instant>,
}

impl ProgressTracker {
    /// 创建进度追踪器
    pub fn new() -> Self {
        Self {
            current: AtomicU64::new(0),
            total: AtomicU64::new(1), // 避免除零
            percentage: AtomicU64::new(0),
            status: RwLock::new(ProgressStatus::Pending),
            stages: RwLock::new(HashMap::new()),
            callbacks: RwLock::new(Vec::new()),
        }
    }

    /// 设置总大小
    pub fn set_total(&self, bytes: u64) {
        self.total.store(bytes.max(1), Ordering::SeqCst);
        self.update_percentage();
    }

    /// 添加已下载字节
    pub fn add_progress(&self, bytes: u64) {
        self.current.fetch_add(bytes, Ordering::SeqCst);
        self.update_percentage();
    }

    /// 设置当前已下载字节
    pub fn set_progress(&self, bytes: u64) {
        self.current.store(bytes, Ordering::SeqCst);
        self.update_percentage();
    }

    /// 设置状态
    pub async fn set_status(&self, status: ProgressStatus) {
        *self.status.write().await = status;
    }

    /// 获取状态
    pub async fn status(&self) -> ProgressStatus {
        self.status.read().await.clone()
    }

    /// 获取进度信息快照
    pub fn snapshot(&self) -> ProgressInfo {
        let downloaded = self.current.load(Ordering::SeqCst);
        let total = self.total.load(Ordering::SeqCst);
        let pct = self.percentage.load(Ordering::SeqCst) as f64 / 1_000_000.0;

        ProgressInfo {
            downloaded_bytes: downloaded,
            total_bytes: total,
            percentage: (pct * 100.0).min(100.0),
            stage: String::new(),
            status: ProgressStatus::Downloading,
            eta_secs: if pct > 0.0 {
                Some(total as f64 / (downloaded.max(1) as f64) * 1.0) // 简化
            } else {
                None
            },
            speed_bytes_per_sec: 0.0,
        }
    }

    /// 是否已完成
    pub fn is_complete(&self) -> bool {
        let total = self.total.load(Ordering::SeqCst);
        self.current.load(Ordering::SeqCst) >= total
    }

    /// 注册回调
    pub async fn on_progress<F>(&self, callback: F)
    where
        F: Fn(&ProgressInfo) + Send + Sync + 'static,
    {
        self.callbacks.write().await.push(Box::new(callback));
    }

    /// 触发所有回调
    pub async fn fire_callbacks(&self) {
        let info = self.snapshot();
        let callbacks = self.callbacks.read().await;
        for cb in callbacks.iter() {
            cb(&info);
        }
    }

    /// 重置
    pub fn reset(&self) {
        self.current.store(0, Ordering::SeqCst);
        self.total.store(1, Ordering::SeqCst);
        self.percentage.store(0, Ordering::SeqCst);
    }

    fn update_percentage(&self) {
        let current = self.current.load(Ordering::SeqCst);
        let total = self.total.load(Ordering::SeqCst).max(1);
        let pct = ((current as f64 / total as f64) * 1_000_000.0) as u64;
        self.percentage.store(pct.min(1_000_000), Ordering::SeqCst);
    }
}

impl Default for ProgressTracker {
    fn default() -> Self {
        Self::new()
    }
}

impl std::fmt::Debug for ProgressTracker {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.debug_struct("ProgressTracker")
            .field("current", &self.current.load(Ordering::SeqCst))
            .field("total", &self.total.load(Ordering::SeqCst))
            .finish()
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn test_progress_tracking() {
        let tracker = ProgressTracker::new();
        tracker.set_total(100);
        assert_eq!(tracker.snapshot().downloaded_bytes, 0);
        assert_eq!(tracker.snapshot().percentage, 0.0);

        tracker.set_progress(50);
        assert_eq!(tracker.snapshot().downloaded_bytes, 50);
        assert_eq!(tracker.snapshot().percentage, 50.0);

        tracker.add_progress(50);
        assert!(tracker.is_complete());
    }
}
