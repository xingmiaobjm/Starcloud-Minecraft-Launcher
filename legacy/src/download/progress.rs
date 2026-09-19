use std::collections::HashMap;
use std::sync::atomic::{AtomicU64, Ordering};
use std::sync::Mutex;
use std::time::{Duration, Instant};

/// 进度回调类型别名
type ProgressCallback = Box<dyn Fn(&ProgressInfo) + Send + Sync>;

/// 下载进度追踪器 - 线程安全
///
/// 提供字节级进度、状态、阶段、速度（EMA 滑动窗口）与 ETA 估算。
/// 所有修改方法均为同步调用，回调触发默认节流（≥100ms）避免高频刷新。
pub struct ProgressTracker {
    /// 当前进度（字节）
    current: AtomicU64,
    /// 总大小（字节）
    total: AtomicU64,
    /// 状态
    status: Mutex<ProgressStatus>,
    /// 当前阶段描述
    stage: Mutex<String>,
    /// 额外的阶段进度（用于细分不同下载阶段）
    #[allow(dead_code)]
    stages: Mutex<HashMap<String, StageProgress>>,
    /// 回调函数列表（同步锁：允许在 async 上下文安全注册/触发）
    callbacks: Mutex<Vec<ProgressCallback>>,
    /// 速度/ETA 状态
    speed_state: Mutex<SpeedState>,
    /// 回调触发节流最小间隔
    min_callback_interval: Duration,
    /// 上次触发回调的时间
    last_emit: Mutex<Option<Instant>>,
}

/// 速度与 ETA 计算状态（EMA 指数滑动平均）
struct SpeedState {
    last_time: Option<Instant>,
    last_bytes: u64,
    /// EMA 速度（字节/秒）
    ema_bytes_per_sec: f64,
}

impl Default for SpeedState {
    fn default() -> Self {
        Self {
            last_time: None,
            last_bytes: 0,
            ema_bytes_per_sec: 0.0,
        }
    }
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

impl ProgressStatus {
    /// 是否为终态
    pub fn is_terminal(&self) -> bool {
        matches!(
            self,
            ProgressStatus::Completed | ProgressStatus::Failed(_) | ProgressStatus::Cancelled
        )
    }
}

/// 阶段进度
#[derive(Debug, Clone)]
#[allow(dead_code)] // 预留细分阶段进度能力，供服务层按阶段聚合使用
struct StageProgress {
    total: u64,
    current: u64,
    start_time: Option<Instant>,
}

impl ProgressTracker {
    /// 创建进度追踪器
    pub fn new() -> Self {
        Self {
            current: AtomicU64::new(0),
            total: AtomicU64::new(1), // 避免除零
            status: Mutex::new(ProgressStatus::Pending),
            stage: Mutex::new(String::new()),
            stages: Mutex::new(HashMap::new()),
            callbacks: Mutex::new(Vec::new()),
            speed_state: Mutex::new(SpeedState::default()),
            min_callback_interval: Duration::from_millis(100),
            last_emit: Mutex::new(None),
        }
    }

    /// 设置回调触发节流间隔（默认 100ms；测试可调小）
    pub fn with_callback_interval(mut self, interval: Duration) -> Self {
        self.min_callback_interval = interval;
        self
    }

    /// 设置总大小（0 视为 1，避免除零）
    pub fn set_total(&self, bytes: u64) {
        self.total.store(bytes.max(1), Ordering::SeqCst);
        self.fire();
    }

    /// 添加已下载字节
    pub fn add_progress(&self, bytes: u64) {
        self.current.fetch_add(bytes, Ordering::SeqCst);
        self.fire();
    }

    /// 设置当前已下载字节
    pub fn set_progress(&self, bytes: u64) {
        self.current.store(bytes, Ordering::SeqCst);
        self.fire();
    }

    /// 设置状态（同步）
    pub fn set_status(&self, status: ProgressStatus) {
        *self.status.lock().unwrap() = status;
        // 进入下载/验证等阶段时记录起点
        self.speed_state.lock().unwrap().last_time = Some(Instant::now());
        self.fire();
    }

    /// 获取状态（同步）
    pub fn status(&self) -> ProgressStatus {
        self.status.lock().unwrap().clone()
    }

    /// 设置阶段描述（同步）
    pub fn set_stage(&self, stage: impl Into<String>) {
        *self.stage.lock().unwrap() = stage.into();
        self.fire();
    }

    /// 获取阶段描述
    pub fn stage(&self) -> String {
        self.stage.lock().unwrap().clone()
    }

    /// 开始某个阶段（重置速度采样，避免跨阶段瞬时速度失真）
    pub fn begin_stage(&self, stage: impl Into<String>, total_bytes: u64) {
        self.set_total(total_bytes);
        self.set_progress(0);
        self.set_stage(stage);
        let mut s = self.speed_state.lock().unwrap();
        s.last_time = Some(Instant::now());
        s.last_bytes = 0;
        s.ema_bytes_per_sec = 0.0;
    }

    /// 获取进度信息快照
    ///
    /// 每次调用会基于时间窗更新 EMA 速度与 ETA（无新增字节时不产生噪声）。
    pub fn snapshot(&self) -> ProgressInfo {
        let downloaded = self.current.load(Ordering::SeqCst);
        let total = self.total.load(Ordering::SeqCst).max(1);
        let percentage = downloaded as f64 / total as f64 * 100.0;

        let (speed, eta) = self.update_speed(downloaded, total);
        let status = self.status.lock().unwrap().clone();
        let stage = self.stage.lock().unwrap().clone();

        ProgressInfo {
            downloaded_bytes: downloaded,
            total_bytes: total,
            percentage: percentage.min(100.0),
            stage,
            status,
            eta_secs: eta,
            speed_bytes_per_sec: speed,
        }
    }

    /// 更新速度与 ETA（EMA）
    fn update_speed(&self, downloaded: u64, total: u64) -> (f64, Option<f64>) {
        let now = Instant::now();
        let mut state = self.speed_state.lock().unwrap();

        let (speed, eta) = match state.last_time {
            Some(prev_time) => {
                let elapsed = now.duration_since(prev_time);
                let delta_bytes = downloaded.saturating_sub(state.last_bytes);

                // 时间窗太短时直接复用上次 EMA
                let mut current_speed = state.ema_bytes_per_sec;
                if elapsed >= Duration::from_millis(50) && delta_bytes > 0 {
                    let instant_speed = delta_bytes as f64 / elapsed.as_secs_f64();
                    current_speed = if state.ema_bytes_per_sec <= 0.0 {
                        instant_speed
                    } else {
                        // EMA 平滑：70% 旧 + 30% 新
                        state.ema_bytes_per_sec * 0.7 + instant_speed * 0.3
                    };
                    state.ema_bytes_per_sec = current_speed;
                    state.last_bytes = downloaded;
                    state.last_time = Some(now);
                }

                let eta = if current_speed > 0.0 {
                    let remaining = total.saturating_sub(downloaded) as f64;
                    Some((remaining / current_speed).max(0.0))
                } else {
                    None
                };
                (current_speed, eta)
            }
            None => {
                state.last_time = Some(now);
                state.last_bytes = downloaded;
                (0.0, None)
            }
        };

        (speed, eta)
    }

    /// 是否已完成
    pub fn is_complete(&self) -> bool {
        let total = self.total.load(Ordering::SeqCst);
        self.current.load(Ordering::SeqCst) >= total
    }

    /// 注册回调
    pub fn on_progress<F>(&self, callback: F)
    where
        F: Fn(&ProgressInfo) + Send + Sync + 'static,
    {
        let mut callbacks = self.callbacks.lock().unwrap();
        callbacks.push(Box::new(callback));
    }

    /// 触发所有回调（节流）
    pub fn fire(&self) {
        // 节流：距上次触发不足 min_callback_interval 且未完成时跳过
        {
            let mut last = self.last_emit.lock().unwrap();
            let now = Instant::now();
            let throttled = last
                .map(|t| now.duration_since(t) < self.min_callback_interval)
                .unwrap_or(false);
            if throttled && !self.is_complete() {
                return;
            }
            *last = Some(now);
        }

        self.fire_callbacks_sync();
    }

    /// 强制触发所有回调（忽略节流）
    pub fn force_fire(&self) {
        *self.last_emit.lock().unwrap() = Some(Instant::now());
        self.fire_callbacks_sync();
    }

    fn fire_callbacks_sync(&self) {
        let info = self.snapshot();
        let callbacks = self.callbacks.lock().unwrap();
        for cb in callbacks.iter() {
            cb(&info);
        }
    }

    /// 触发所有回调（兼容旧异步 API；内部为同步语义）
    pub async fn fire_callbacks(&self) {
        self.fire_callbacks_sync();
    }

    /// 重置
    pub fn reset(&self) {
        self.current.store(0, Ordering::SeqCst);
        self.total.store(1, Ordering::SeqCst);
        *self.status.lock().unwrap() = ProgressStatus::Pending;
        self.stage.lock().unwrap().clear();
        *self.speed_state.lock().unwrap() = SpeedState::default();
        *self.last_emit.lock().unwrap() = None;
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
            .field("status", &self.status())
            .finish()
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use std::sync::atomic::AtomicUsize;
    use std::sync::Arc;

    #[test]
    fn test_progress_tracking() {
        let tracker = ProgressTracker::new();
        tracker.set_total(100);
        assert_eq!(tracker.snapshot().downloaded_bytes, 0);
        assert_eq!(tracker.snapshot().percentage, 0.0);
        assert_eq!(tracker.snapshot().status, ProgressStatus::Pending);

        tracker.set_status(ProgressStatus::Downloading);
        tracker.set_stage("下载库文件");

        tracker.set_progress(50);
        assert_eq!(tracker.snapshot().downloaded_bytes, 50);
        assert_eq!(tracker.snapshot().percentage, 50.0);
        assert_eq!(tracker.snapshot().stage, "下载库文件");
        assert_eq!(tracker.snapshot().status, ProgressStatus::Downloading);

        tracker.add_progress(50);
        assert!(tracker.is_complete());
        assert_eq!(tracker.snapshot().percentage, 100.0);

        tracker.set_status(ProgressStatus::Completed);
        assert_eq!(tracker.snapshot().status, ProgressStatus::Completed);
    }

    #[test]
    fn test_progress_speed_and_eta() {
        let tracker = ProgressTracker::new();
        tracker.set_total(1000);
        tracker.set_status(ProgressStatus::Downloading);

        // 推进 500 字节
        tracker.set_progress(500);
        std::thread::sleep(Duration::from_millis(120));
        // 再次推进触发速度采样（下载完成）
        tracker.set_progress(1000);
        let snap = tracker.snapshot();
        assert!(
            snap.speed_bytes_per_sec > 0.0,
            "速度应为正: {:?}",
            snap.speed_bytes_per_sec
        );
        // 已下载全部，ETA 应为 0
        assert_eq!(snap.eta_secs, Some(0.0));

        // 未完成时 ETA 存在
        tracker.reset();
        tracker.set_total(1000);
        tracker.set_progress(100);
        std::thread::sleep(Duration::from_millis(120));
        tracker.set_progress(400);
        let snap2 = tracker.snapshot();
        assert!(snap2.speed_bytes_per_sec > 0.0);
        assert!(snap2.eta_secs.is_some());
    }

    #[test]
    fn test_progress_callback_fired() {
        let tracker = ProgressTracker::new().with_callback_interval(Duration::from_millis(1));
        let calls = Arc::new(AtomicUsize::new(0));
        let calls_cb = calls.clone();
        tracker.on_progress(move |info: &ProgressInfo| {
            calls_cb.fetch_add(1, Ordering::SeqCst);
            assert!(info.percentage >= 0.0);
        });

        tracker.set_total(10);
        tracker.set_progress(5);
        tracker.set_progress(10);

        // 回调应在进度推进时触发（节流 1ms，进度为终态强制触发）
        assert!(calls.load(Ordering::SeqCst) >= 1, "回调应至少触发一次");
    }

    #[test]
    fn test_progress_throttle() {
        let tracker = ProgressTracker::new().with_callback_interval(Duration::from_millis(500));
        let calls = Arc::new(AtomicUsize::new(0));
        let calls_cb = calls.clone();
        tracker.on_progress(move |_: &ProgressInfo| {
            calls_cb.fetch_add(1, Ordering::SeqCst);
        });

        tracker.set_total(1000);
        // 高频推进，未到终态时被节流
        for i in 1..=100 {
            tracker.set_progress(i * 10);
        }
        let throttled = calls.load(Ordering::SeqCst);
        // 终态强制触发一次
        tracker.set_progress(1000);
        assert!(
            calls.load(Ordering::SeqCst) > throttled,
            "终态应强制触发回调"
        );
        assert!(calls.load(Ordering::SeqCst) <= 3, "节流后回调次数应很少");
    }

    #[test]
    fn test_progress_reset() {
        let tracker = ProgressTracker::new();
        tracker.set_total(100);
        tracker.set_progress(100);
        tracker.set_status(ProgressStatus::Completed);
        assert!(tracker.is_complete());

        tracker.reset();
        assert!(!tracker.is_complete());
        assert_eq!(tracker.snapshot().downloaded_bytes, 0);
        assert_eq!(tracker.snapshot().status, ProgressStatus::Pending);
        assert_eq!(tracker.snapshot().stage, "");
    }

    #[test]
    fn test_begin_stage() {
        let tracker = ProgressTracker::new();
        tracker.begin_stage("下载资源", 500);
        assert_eq!(tracker.snapshot().stage, "下载资源");
        assert_eq!(tracker.snapshot().total_bytes, 500);
        assert_eq!(tracker.snapshot().downloaded_bytes, 0);
    }
}
