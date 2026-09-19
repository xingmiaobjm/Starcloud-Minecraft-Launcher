//! Tauri 后端命令 - 版本/安装/启动/账户/设置/插件/日志/Java
//!
//! 长耗时操作（安装/启动）采用后台任务 + 事件推送，前端免轮询。

use crate::config_file_path;
use crate::events::{
    ErrorPayload, GameExitedPayload, GameLogPayload, InstallDonePayload, ProgressPayload,
    StatusPayload, TasksUpdatedPayload, TerracottaStatePayload, EVENT_DOWNLOAD_PROGRESS,
    EVENT_ERROR, EVENT_GAME_EXITED, EVENT_GAME_LOG, EVENT_INSTALL_DONE, EVENT_STATUS,
    EVENT_TASKS_UPDATED, EVENT_TERRACOTTA_STATE,
};
use crate::plugin_host::{self, GuiPluginInfo};
use crate::state::AppState;
use crate::tasks::{save_tasks, now_millis, TaskInfo};
use serde::Serialize;
use starcloud_minecraft::account::microsoft::DeviceCodeResponse;
use starcloud_minecraft::account::store::StoredAccount;
use starcloud_minecraft::config::{LauncherConfig, VersionSettings, VersionSettingsStore};
use starcloud_minecraft::core::{
    AccountService, InstallService, LaunchService, MicrosoftLoginPoll, PreflightIssue,
};
use starcloud_minecraft::download::{background_refresh_cache, fetch_version_manifest_cached, fetch_version_manifest_cache_only, DownloadEngine};
use starcloud_minecraft::error::Result as CoreResult;
use starcloud_minecraft::launch::java::{
    cache_java, cached_java, clear_java_cache, JavaDetector, JavaRuntime,
};
use starcloud_minecraft::launch::java_download::{java_download_root, JavaDownloader};
use starcloud_minecraft::version_manager::VersionInstaller;
use tauri::{AppHandle, Emitter, Manager, State};

// ---------------------------------------------------------------------------
// DTO（序列化给前端）
// ---------------------------------------------------------------------------

/// 版本条目（远程清单 + 本地安装状态）
#[derive(Debug, Clone, Serialize)]
pub struct VersionInfo {
    pub id: String,
    pub version_type: String,
    pub release_time: String,
    pub installed: bool,
    pub size_bytes: u64,
}

/// 已安装版本
#[derive(Debug, Clone, Serialize)]
pub struct InstalledVersion {
    pub id: String,
    pub size_bytes: u64,
}

/// 设置快照
#[derive(Debug, Clone, Serialize, serde::Deserialize)]
pub struct SettingsDto {
    pub dot_minecraft: String,
    pub language: String,
    pub default_min_memory_mb: u32,
    pub default_max_memory_mb: u32,
    pub java_executable: Option<String>,
    pub extra_jvm_args: Vec<String>,
    /// 游戏优化启动参数（追加到游戏主类参数之后）
    pub extra_game_args: Vec<String>,
    /// JAVA 启用高性能显卡（Windows 图形偏好：独立显卡优先）
    pub use_high_performance_gpu: bool,
    pub download_concurrency: usize,
    pub download_max_retries: u32,
    /// 上次启动的版本 ID（首页默认选中）
    pub last_version: Option<String>,
    /// 强制游戏语言为中文
    pub force_chinese: bool,
    /// 全局版本隔离：每个版本使用独立游戏目录 versions/<id>/game（默认开启）
    pub version_isolated: bool,
    /// 开发者模式（开启后可调整最小内存等高级参数）
    pub dev_mode: bool,
    /// 动画倍率：0 关闭，0.1-2.0 倍率
    pub animation_scale: f64,
    /// 游戏运行时是否显示右下角日志按钮
    pub show_log_button: bool,
    /// 版本清单本地缓存：开启后缓冲版本列表，关闭即「联机模式」始终从网络拉取
    pub version_cache_enabled: bool,
    /// 是否启用内置 JVM 优化参数（G1GC / 内存预触 / IPv4 等）
    pub use_builtin_jvm_args: bool,
}

impl SettingsDto {
    fn from_config(c: &LauncherConfig) -> Self {
        Self {
            dot_minecraft: c.dot_minecraft.to_string_lossy().to_string(),
            language: c.language.clone(),
            default_min_memory_mb: c.default_min_memory_mb,
            default_max_memory_mb: c.default_max_memory_mb,
            java_executable: c
                .java_executable
                .as_ref()
                .map(|p| p.to_string_lossy().to_string()),
            extra_jvm_args: c.extra_jvm_args.clone(),
            extra_game_args: c.extra_game_args.clone(),
            use_high_performance_gpu: c.use_high_performance_gpu,
            download_concurrency: c.download_concurrency,
            download_max_retries: c.download_max_retries,
            last_version: c.last_version.clone(),
            force_chinese: c.force_chinese,
            version_isolated: c.version_isolated,
            dev_mode: c.dev_mode,
            animation_scale: c.animation_scale,
            show_log_button: c.show_log_button,
            version_cache_enabled: c.version_cache_enabled,
            use_builtin_jvm_args: c.use_builtin_jvm_args,
        }
    }

    fn apply_to(self, c: &mut LauncherConfig) {
        c.dot_minecraft = self.dot_minecraft.into();
        c.language = self.language;
        c.default_min_memory_mb = self.default_min_memory_mb.max(128);
        c.default_max_memory_mb = self.default_max_memory_mb.max(128);
        // 最小内存不可超过最大内存
        if c.default_min_memory_mb > c.default_max_memory_mb {
            c.default_min_memory_mb = c.default_max_memory_mb;
        }
        c.java_executable = self.java_executable.map(Into::into);
        c.extra_jvm_args = self.extra_jvm_args;
        c.extra_game_args = self.extra_game_args;
        c.use_high_performance_gpu = self.use_high_performance_gpu;
        c.download_concurrency = self.download_concurrency.max(1);
        c.download_max_retries = self.download_max_retries.max(1);
        c.last_version = self.last_version;
        c.force_chinese = self.force_chinese;
        c.version_isolated = self.version_isolated;
        c.dev_mode = self.dev_mode;
        // 动画倍率：0 关闭；其余限制在 0.1-2.0
        c.animation_scale = if self.animation_scale <= 0.0 {
            0.0
        } else {
            self.animation_scale.clamp(0.1, 2.0)
        };
        c.show_log_button = self.show_log_button;
        c.version_cache_enabled = self.version_cache_enabled;
        c.use_builtin_jvm_args = self.use_builtin_jvm_args;
    }
}

/// 版本级启动设置（未设置字段表示跟随全局默认）
#[derive(Debug, Clone, Serialize, serde::Deserialize)]
pub struct VersionSettingsDto {
    pub min_memory_mb: Option<u32>,
    pub max_memory_mb: Option<u32>,
    pub language: Option<String>,
    pub isolated: bool,
    pub java_executable: Option<String>,
    pub jvm_args: Vec<String>,
    /// 是否启用内置 JVM 优化参数（None = 跟随全局）
    pub use_builtin_jvm_args: Option<bool>,
}

impl VersionSettingsDto {
    fn from_settings(s: &VersionSettings) -> Self {
        Self {
            min_memory_mb: s.min_memory_mb,
            max_memory_mb: s.max_memory_mb,
            language: s.language.clone(),
            isolated: s.isolated,
            java_executable: s
                .java_executable
                .as_ref()
                .map(|p| p.to_string_lossy().to_string()),
            jvm_args: s.jvm_args.clone(),
            use_builtin_jvm_args: s.use_builtin_jvm_args,
        }
    }

    fn to_settings(self) -> VersionSettings {
        VersionSettings {
            min_memory_mb: self.min_memory_mb,
            max_memory_mb: self.max_memory_mb,
            language: self.language,
            isolated: self.isolated,
            java_executable: self.java_executable.map(Into::into),
            jvm_args: self.jvm_args,
            use_builtin_jvm_args: self.use_builtin_jvm_args,
        }
    }
}

/// 内存推荐（按系统物理内存计算）
#[derive(Debug, Clone, Serialize)]
pub struct MemorySuggestion {
    /// 物理内存总量 (MB)
    pub total_mb: u64,
    /// 推荐最小内存 (MB)
    pub min_mb: u32,
    /// 推荐最大内存 (MB)
    pub max_mb: u32,
}

/// Java 检测结果
#[derive(Debug, Clone, Serialize)]
pub struct JavaInfo {
    pub executable: String,
    pub major_version: u32,
    pub version_string: String,
    pub is_64bit: bool,
}

impl JavaInfo {
    fn from_runtime(r: &JavaRuntime) -> Self {
        Self {
            executable: r.executable.to_string_lossy().to_string(),
            major_version: r.major_version,
            version_string: r.version_string.clone(),
            is_64bit: r.is_64bit,
        }
    }
}

/// 微软设备码（前端展示 user_code + verification_uri）
#[derive(Debug, Clone, Serialize)]
pub struct DeviceCodeDto {
    pub user_code: String,
    pub device_code: String,
    pub verification_uri: String,
    pub message: String,
    pub interval: u64,
    pub expires_in: u64,
}

impl DeviceCodeDto {
    fn from_response(r: DeviceCodeResponse) -> Self {
        Self {
            user_code: r.user_code,
            device_code: r.device_code,
            verification_uri: r.verification_uri,
            message: r.message,
            interval: r.interval,
            expires_in: r.expires_in,
        }
    }
}

/// 微软登录轮询结果
#[derive(Debug, Clone, Serialize)]
pub struct LoginPollDto {
    /// "pending"（继续轮询）| "success" | "error"（终止）
    pub state: String,
    pub message: String,
    pub account: Option<StoredAccount>,
}

// ---------------------------------------------------------------------------
// 辅助
// ---------------------------------------------------------------------------

/// 取当前配置（clone，避免持锁跨 await）
fn config_snapshot(state: &State<'_, AppState>) -> LauncherConfig {
    state.config.lock().unwrap().clone()
}

/// 保存配置到磁盘
async fn persist_config(config: &LauncherConfig) -> CoreResult<()> {
    config.save(config_file_path()).await
}

/// 将版本级设置覆盖到配置上（内存 / 语言 / Java / JVM / 版本隔离）
///
/// 未设置的字段保持全局默认；隔离时创建独立游戏目录 versions/<id>/game。
async fn apply_version_settings(config: &mut LauncherConfig, version_id: &str) {
    let store = VersionSettingsStore::new(config.dot_minecraft.clone());
    let vs = store.load(version_id).await.unwrap_or_default();
    if let Some(min) = vs.min_memory_mb {
        config.default_min_memory_mb = min.max(128);
    }
    if let Some(max) = vs.max_memory_mb {
        config.default_max_memory_mb = max.max(128);
    }
    if let Some(lang) = vs.language {
        if !lang.is_empty() {
            config.language = lang;
        }
    }
    if let Some(java) = vs.java_executable {
        config.java_executable = Some(java);
    }
    if !vs.jvm_args.is_empty() {
        config.extra_jvm_args = vs.jvm_args;
    }
    if let Some(use_builtin) = vs.use_builtin_jvm_args {
        config.use_builtin_jvm_args = use_builtin;
    }
    // 版本隔离：无版本设置文件时跟随全局设置（默认开启）；有文件则以版本级设置覆盖
    let has_version_override = store.path(version_id).exists();
    let isolated = if has_version_override {
        vs.isolated
    } else {
        config.version_isolated
    };
    if isolated {
        // 版本隔离：独立游戏目录 versions/<id>/game（资源/库仍共享 .minecraft）
        let dir = config
            .dot_minecraft
            .join("versions")
            .join(version_id)
            .join("game");
        config.runtime_game_dir = Some(dir.clone());
        let _ = tokio::fs::create_dir_all(&dir).await;
    }
}

// ---------------------------------------------------------------------------
// 版本命令
// ---------------------------------------------------------------------------

/// 远程版本列表（含本地安装状态）
#[tauri::command]
pub async fn list_versions(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<Vec<VersionInfo>, String> {
    let config = config_snapshot(&state);
    let dot_minecraft = config.dot_minecraft.clone();

    // 1. 优先从本地缓存加载（同步，毫秒级，无网络）
    let manifest = match fetch_version_manifest_cache_only() {
        Some(m) => {
            log::info!("从本地缓存加载版本清单，共 {} 个版本", m.versions.len());
            m
        }
        None => {
            // 首次使用无缓存，退化为网络拉取
            log::info!("无本地缓存，从网络拉取版本清单");
            let cached = fetch_version_manifest_cached(true)
                .await
                .map_err(|e| format!("获取版本清单失败: {}", e))?;
            cached.manifest
        }
    };

    // 2. 并行处理所有版本：sync 检查安装状态 + async 计算已安装版本大小
    let versions = manifest.versions;
    let mut items: Vec<VersionInfo> = Vec::with_capacity(versions.len());

    // Phase 1: 同步检查安装状态（极快，纯文件存在性检查）
    let installer = VersionInstaller::new(DownloadEngine::new(&dot_minecraft));
    for entry in &versions {
        let installed = installer.is_installed(&entry.id);
        items.push(VersionInfo {
            id: entry.id.clone(),
            version_type: format!("{:?}", entry.version_type),
            release_time: entry.release_time.clone(),
            installed,
            size_bytes: 0,
        });
    }

    // Phase 2: 并行计算已安装版本的大小（仅已安装版本，通常 < 10 个）
    let mut size_handles = Vec::new();
    for v in items.iter().filter(|v| v.installed) {
        let id = v.id.clone();
        let dm = dot_minecraft.clone();
        size_handles.push(tokio::spawn(async move {
            let installer = VersionInstaller::new(DownloadEngine::new(&dm));
            let size = installer.version_size(&id).await.unwrap_or(0);
            (id, size)
        }));
    }
    for handle in size_handles {
        if let Ok((id, size)) = handle.await {
            if let Some(item) = items.iter_mut().find(|v| v.id == id) {
                item.size_bytes = size;
            }
        }
    }

    // 3. 后台异步刷新远程清单（仅在缓存存在时触发）
    let app2 = app.clone();
    let dm2 = dot_minecraft.clone();
    tauri::async_runtime::spawn(async move {
        match background_refresh_cache().await {
            Ok(manifest) => {
                let installer = VersionInstaller::new(DownloadEngine::new(&dm2));
                let mut refreshed = Vec::with_capacity(manifest.versions.len());
                for entry in &manifest.versions {
                    let installed = installer.is_installed(&entry.id);
                    let size_bytes = if installed {
                        installer.version_size(&entry.id).await.unwrap_or(0)
                    } else {
                        0
                    };
                    refreshed.push(VersionInfo {
                        id: entry.id.clone(),
                        version_type: format!("{:?}", entry.version_type),
                        release_time: entry.release_time.clone(),
                        installed,
                        size_bytes,
                    });
                }
                log::info!("后台刷新版本清单完成，共 {} 个版本", refreshed.len());
                let _ = app2.emit("versions-updated", refreshed);
            }
            Err(e) => {
                log::warn!("后台刷新版本清单失败: {}", e);
            }
        }
    });

    Ok(items)
}

/// 已安装版本列表
#[tauri::command]
pub async fn list_installed(state: State<'_, AppState>) -> Result<Vec<InstalledVersion>, String> {
    let config = config_snapshot(&state);
    let installer = VersionInstaller::new(DownloadEngine::new(&config.dot_minecraft));
    let ids = installer
        .list_installed()
        .await
        .map_err(|e| format!("读取已安装版本失败: {}", e))?;
    let mut items = Vec::with_capacity(ids.len());
    for id in ids {
        let size_bytes = installer.version_size(&id).await.unwrap_or(0);
        items.push(InstalledVersion { id, size_bytes });
    }
    Ok(items)
}

/// 安装版本（后台任务 + 事件推送，支持取消）
#[tauri::command]
pub async fn install_version(
    app: AppHandle,
    state: State<'_, AppState>,
    version_id: String,
) -> Result<(), String> {
    let config = config_snapshot(&state);

    // 任务键（注册表 / 进度事件 / install-done job_id）
    let job_key = version_id.clone();
    // 登记取消句柄；同一版本已在安装中则拒绝
    let (cancel_tx, cancel_rx) = tokio::sync::oneshot::channel::<()>();
    {
        let mut jobs = state.install_jobs.lock().unwrap();
        if jobs.contains_key(&job_key) {
            return Err("该版本正在安装中".into());
        }
        jobs.insert(job_key.clone(), cancel_tx);
    }

    let _ = app.emit(
        EVENT_STATUS,
        StatusPayload {
            status: "installing".into(),
            detail: Some(format!("安装 {}", version_id)),
        },
    );

    // 调度中心：创建安装任务（running，置顶）
    let task = TaskInfo::new("install", format!("安装 {}", version_id));
    let task_id = task.id.clone();
    push_task(&app, task);

    let emit_version_id = job_key.clone(); // 进度事件键（移入闭包）
    let install_id = version_id.clone(); // 安装参数借用（避免与 match 中的 move 冲突）
    let verbose = config.dev_mode; // 开发者模式：调度中心记录更详细进度日志
    tauri::async_runtime::spawn(async move {
        let svc = InstallService::new(config);
        let progress_app = app.clone();
        let log_app = app.clone();
        let log_task_id = task_id.clone();
        let progress_cb =
            task_progress_cb(progress_app, log_app, log_task_id, emit_version_id, verbose);
        // 取消信号到达时中止安装任务（下载为原子写入，残留临时文件可安全重试）
        let cancel_fut = async move {
            let _ = cancel_rx.await;
        };
        let install_fut = svc.install_version(&install_id, progress_cb);
        tokio::pin!(install_fut);
        let result: Result<(), String> = tokio::select! {
            r = install_fut => r.map_err(|e| e.to_string()),
            _ = cancel_fut => Err("已取消安装".into()),
        };
        // 移除取消句柄（无论成败）
        app.state::<AppState>()
            .install_jobs
            .lock()
            .unwrap()
            .remove(&job_key);
        match result {
            Ok(()) => {
                update_task(&app, &task_id, |t| {
                    t.status = "done".into();
                    t.detail = Some("安装完成".into());
                    t.push_log("安装完成");
                });
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id,
                        success: true,
                        message: "安装完成".into(),
                    },
                );
            }
            Err(e) => {
                update_task(&app, &task_id, |t| {
                    t.status = "error".into();
                    t.detail = Some(e.clone());
                    t.push_log(format!("错误：{}", e));
                });
                let _ = app.emit(
                    EVENT_ERROR,
                    ErrorPayload {
                        context: "安装失败".into(),
                        message: e.clone(),
                    },
                );
                let _ = app.emit(
                    EVENT_STATUS,
                    StatusPayload {
                        status: "error".into(),
                        detail: Some(e.clone()),
                    },
                );
                // 失败/取消也补发 install-done（success=false），前端据此清理"正在下载"状态
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id,
                        success: false,
                        message: e,
                    },
                );
            }
        }
    });
    Ok(())
}

/// 取消进行中的版本安装（发送取消信号，后台任务在下个 await 点中止）
#[tauri::command]
pub fn cancel_install(state: State<'_, AppState>, version_id: String) -> Result<(), String> {
    let tx = state.install_jobs.lock().unwrap().remove(&version_id);
    match tx {
        Some(tx) => {
            let _ = tx.send(()); // 接收方已结束则 Err，忽略
            Ok(())
        }
        None => Err("该版本没有进行中的安装".into()),
    }
}

/// 下载 Minecraft 服务端 jar（后台任务 + 进度 + 取消）
///
/// 任务键（job_id / 进度事件键）为 `server:<版本ID>`，与版本安装互不冲突。
#[tauri::command]
pub async fn download_server(
    app: AppHandle,
    state: State<'_, AppState>,
    version_id: String,
) -> Result<(), String> {
    let config = config_snapshot(&state);

    // 任务键 = server:<版本ID>
    let job_key = format!("server:{}", version_id);
    let (cancel_tx, cancel_rx) = tokio::sync::oneshot::channel::<()>();
    {
        let mut jobs = state.install_jobs.lock().unwrap();
        if jobs.contains_key(&job_key) {
            return Err("该服务端正在下载中".into());
        }
        jobs.insert(job_key.clone(), cancel_tx);
    }

    let _ = app.emit(
        EVENT_STATUS,
        StatusPayload {
            status: "installing".into(),
            detail: Some(format!("下载 {} 服务端", version_id)),
        },
    );

    // 调度中心：创建下载任务（running，置顶）
    let task = TaskInfo::new("download", format!("下载 {} 服务端", version_id));
    let task_id = task.id.clone();
    push_task(&app, task);

    let emit_version_id = job_key.clone();
    let install_id = version_id.clone();
    let verbose = config.dev_mode; // 开发者模式：调度中心记录更详细进度日志
    tauri::async_runtime::spawn(async move {
        let svc = InstallService::new(config);
        let progress_app = app.clone();
        let log_app = app.clone();
        let log_task_id = task_id.clone();
        let progress_cb =
            task_progress_cb(progress_app, log_app, log_task_id, emit_version_id, verbose);
        // 取消信号到达时中止下载
        let cancel_fut = async move {
            let _ = cancel_rx.await;
        };
        let fut = svc.download_server(&install_id, progress_cb);
        tokio::pin!(fut);
        let result: Result<(), String> = tokio::select! {
            r = fut => r.map(|_| ()).map_err(|e| e.to_string()),
            _ = cancel_fut => Err("已取消下载".into()),
        };
        // 移除取消句柄（无论成败）
        app.state::<AppState>()
            .install_jobs
            .lock()
            .unwrap()
            .remove(&job_key);
        match result {
            Ok(_) => {
                update_task(&app, &task_id, |t| {
                    t.status = "done".into();
                    t.detail = Some("服务端下载完成".into());
                    t.push_log("服务端下载完成");
                });
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id: install_id.clone(),
                        success: true,
                        message: format!("{} 服务端下载完成", install_id),
                    },
                );
            }
            Err(e) => {
                update_task(&app, &task_id, |t| {
                    t.status = "error".into();
                    t.detail = Some(e.clone());
                    t.push_log(format!("错误：{}", e));
                });
                let _ = app.emit(
                    EVENT_ERROR,
                    ErrorPayload {
                        context: "服务端下载失败".into(),
                        message: e.clone(),
                    },
                );
                let _ = app.emit(
                    EVENT_STATUS,
                    StatusPayload {
                        status: "error".into(),
                        detail: Some(e.clone()),
                    },
                );
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id: install_id.clone(),
                        success: false,
                        message: e,
                    },
                );
            }
        }
    });
    Ok(())
}

/// 删除已安装版本
#[tauri::command]
pub async fn remove_version(state: State<'_, AppState>, version_id: String) -> Result<(), String> {
    let config = config_snapshot(&state);
    let installer = VersionInstaller::new(DownloadEngine::new(&config.dot_minecraft));
    installer
        .delete_version(&version_id)
        .await
        .map_err(|e| format!("删除版本失败: {}", e))
}

/// 安装 Fabric 加载器（后台任务 + 事件推送，支持取消，成功后通过 install-done 返回 fabric 版本 id）
#[tauri::command]
pub async fn install_fabric(
    app: AppHandle,
    state: State<'_, AppState>,
    mc_version: String,
) -> Result<(), String> {
    let config = config_snapshot(&state);

    // 任务键（注册表 / 进度事件 / install-done job_id，均用 MC 版本号）
    let job_key = mc_version.clone();
    // 登记取消句柄（键 = MC 版本）；同一版本已在安装中则拒绝
    let (cancel_tx, cancel_rx) = tokio::sync::oneshot::channel::<()>();
    {
        let mut jobs = state.install_jobs.lock().unwrap();
        if jobs.contains_key(&job_key) {
            return Err("该版本正在安装中".into());
        }
        jobs.insert(job_key.clone(), cancel_tx);
    }

    let _ = app.emit(
        EVENT_STATUS,
        StatusPayload {
            status: "installing".into(),
            detail: Some(format!("为 {} 安装 Fabric", mc_version)),
        },
    );

    // 调度中心：创建 Fabric 安装任务（running，置顶）
    let task = TaskInfo::new("fabric", format!("为 {} 安装 Fabric", mc_version));
    let task_id = task.id.clone();
    push_task(&app, task);

    let emit_version_id = job_key.clone(); // 进度事件键（移入闭包）
    let install_mc = mc_version.clone(); // 安装参数借用（避免与 match 中的 move 冲突）
    let verbose = config.dev_mode; // 开发者模式：调度中心记录更详细进度日志
    tauri::async_runtime::spawn(async move {
        let svc = InstallService::new(config);
        let progress_app = app.clone();
        let log_app = app.clone();
        let log_task_id = task_id.clone();
        let progress_cb =
            task_progress_cb(progress_app, log_app, log_task_id, emit_version_id, verbose);
        // 取消信号到达时中止安装任务
        let cancel_fut = async move {
            let _ = cancel_rx.await;
        };
        let install_fut = svc.install_fabric(&install_mc, progress_cb);
        tokio::pin!(install_fut);
        let result: Result<String, String> = tokio::select! {
            r = install_fut => r.map_err(|e| e.to_string()),
            _ = cancel_fut => Err("已取消安装".into()),
        };
        // 移除取消句柄（无论成败）
        app.state::<AppState>()
            .install_jobs
            .lock()
            .unwrap()
            .remove(&job_key);
        match result {
            Ok(fabric_id) => {
                update_task(&app, &task_id, |t| {
                    t.status = "done".into();
                    t.detail = Some("Fabric 安装完成".into());
                    t.push_log("Fabric 安装完成");
                });
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id: fabric_id,
                        success: true,
                        message: "Fabric 安装完成".into(),
                    },
                );
            }
            Err(e) => {
                update_task(&app, &task_id, |t| {
                    t.status = "error".into();
                    t.detail = Some(e.clone());
                    t.push_log(format!("错误：{}", e));
                });
                let _ = app.emit(
                    EVENT_ERROR,
                    ErrorPayload {
                        context: "Fabric 安装失败".into(),
                        message: e.clone(),
                    },
                );
                let _ = app.emit(
                    EVENT_STATUS,
                    StatusPayload {
                        status: "error".into(),
                        detail: Some(e.clone()),
                    },
                );
                // 失败/取消也补发 install-done（success=false）
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id: mc_version,
                        success: false,
                        message: e,
                    },
                );
            }
        }
    });
    Ok(())
}

// ---------------------------------------------------------------------------
// 加载器安装命令
// ---------------------------------------------------------------------------

/// 列出可用的 Mod 加载器（来自可扩展注册表，前端弹窗自动读取，新增加载器无需改前端）
#[tauri::command]
pub fn list_loaders() -> Vec<starcloud_minecraft::modloader::LoaderInfo> {
    starcloud_minecraft::modloader::LoaderKind::all()
        .iter()
        .map(|k| k.info())
        .collect()
}

/// 安装指定加载器版本（后台任务 + 事件推送，支持取消）
///
/// - `mc_version`：目标 MC 版本号
/// - `loader`：加载器 ID（vanilla/fabric/forge/quilt/neoforge，见 list_loaders）
///
/// 加载器版本自动取最新版；安装后的版本 ID 固定含加载器标记（如 `1.20.1-forge-47.2.0`），
/// 成功后通过 install-done 返回实际安装的版本 ID。任务键为 `{loader}:{mc}`。
#[tauri::command]
pub async fn install_loader(
    app: AppHandle,
    state: State<'_, AppState>,
    mc_version: String,
    loader: String,
) -> Result<(), String> {
    let kind = starcloud_minecraft::modloader::LoaderKind::from_id(&loader)
        .map_err(|e| e.to_string())?;
    let config = config_snapshot(&state);

    // 任务键 = {loader}:{mc}（与版本安装 / 服务端下载互不冲突）
    let job_key = format!("{}:{}", kind.id(), mc_version);
    let (cancel_tx, cancel_rx) = tokio::sync::oneshot::channel::<()>();
    {
        let mut jobs = state.install_jobs.lock().unwrap();
        if jobs.contains_key(&job_key) {
            return Err("该版本正在安装中".into());
        }
        jobs.insert(job_key.clone(), cancel_tx);
    }

    let _ = app.emit(
        EVENT_STATUS,
        StatusPayload {
            status: "installing".into(),
            detail: Some(format!("为 {} 安装 {}", mc_version, kind.label())),
        },
    );

    // 调度中心：创建加载器安装任务（running，置顶）
    let task = TaskInfo::new("install", format!("为 {} 安装 {}", mc_version, kind.label()));
    let task_id = task.id.clone();
    push_task(&app, task);

    let emit_version_id = job_key.clone();
    let install_mc = mc_version.clone();
    let verbose = config.dev_mode; // 开发者模式：调度中心记录更详细进度日志
    tauri::async_runtime::spawn(async move {
        let svc = InstallService::new(config);
        let progress_app = app.clone();
        let log_app = app.clone();
        let log_task_id = task_id.clone();
        let progress_cb =
            task_progress_cb(progress_app, log_app, log_task_id, emit_version_id, verbose);
        // 取消信号到达时中止安装任务
        let cancel_fut = async move {
            let _ = cancel_rx.await;
        };
        let install_fut = svc.install_loader(&install_mc, kind, progress_cb);
        tokio::pin!(install_fut);
        let result: Result<String, String> = tokio::select! {
            r = install_fut => r.map_err(|e| e.to_string()),
            _ = cancel_fut => Err("已取消安装".into()),
        };
        // 移除取消句柄（无论成败）
        app.state::<AppState>()
            .install_jobs
            .lock()
            .unwrap()
            .remove(&job_key);
        match result {
            Ok(installed_id) => {
                update_task(&app, &task_id, |t| {
                    t.status = "done".into();
                    t.detail = Some("安装完成".into());
                    t.push_log("安装完成");
                });
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id: installed_id,
                        success: true,
                        message: "安装完成".into(),
                    },
                );
            }
            Err(e) => {
                update_task(&app, &task_id, |t| {
                    t.status = "error".into();
                    t.detail = Some(e.clone());
                    t.push_log(format!("错误：{}", e));
                });
                let _ = app.emit(
                    EVENT_ERROR,
                    ErrorPayload {
                        context: "加载器安装失败".into(),
                        message: e.clone(),
                    },
                );
                let _ = app.emit(
                    EVENT_STATUS,
                    StatusPayload {
                        status: "error".into(),
                        detail: Some(e.clone()),
                    },
                );
                // 失败/取消也补发 install-done（success=false），前端据此清理"正在下载"状态
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id: mc_version.clone(),
                        success: false,
                        message: e,
                    },
                );
            }
        }
    });
    Ok(())
}

// ---------------------------------------------------------------------------
// Mod 下载命令（Modrinth 主 + CurseForge 无 key 镜像）
// ---------------------------------------------------------------------------

/// 搜索 Mod / 整合包 / 数据包 / 光影包 / 材质包
///
/// `category` 取值：`mod` / `modpack` / `datapack` / `shader` / `resourcepack`，
/// 为空表示不限（各平台按自身分类体系转换）。
/// `categories` 为逗号分隔的 Modrinth 类型标签（如 `magic,adventure`），
/// 仅 Modrinth 生效，CurseForge 公开接口不支持类型过滤。
/// 中文关键词会经内置词典/机器翻译转成英文再搜索（参考 PCL2）；
/// 返回结果的中文标题（title_zh）按 内置词典 → MCIM → MC 百科 → 机器翻译 的顺序填充。
#[tauri::command]
pub async fn search_mods(
    query: String,
    mc_version: String,
    loader: String,
    category: String,
    categories: String,
    platform: String,
    offset: u32,
) -> Result<Vec<starcloud_minecraft::download::ModSearchHit>, String> {
    use starcloud_minecraft::download::{search_curseforge, search_modrinth, translate_query_zh_to_en};
    // 每页 50 条（Modrinth limit 上限 100，CurseForge pageSize 上限 50），
    // 配合客户端连接池复用减少翻页往返次数
    let limit = 50;
    // 中文关键词 → 英文，提升命中率
    let search_query = translate_query_zh_to_en(&query).await;
    let hits = match platform.as_str() {
        "curseforge" => search_curseforge(&search_query, &mc_version, &loader, &category, limit, offset)
            .await
            .map_err(|e| e.to_string())?,
        _ => search_modrinth(
            &search_query,
            &mc_version,
            &loader,
            &category,
            &categories,
            limit,
            offset,
        )
        .await
        .map_err(|e| e.to_string())?,
    };
    // 注意：结果标题不在此处翻译。翻译（MC 百科/机翻）耗时较长，
    // 若同步执行会拖慢滚动加载；由前端调用 translate_mod_titles 异步补译。
    Ok(hits)
}

/// 批量翻译 Mod 标题为中文（供前端在搜索返回后异步补齐 title_zh）。
/// 返回与输入等长的数组：Some(中文译名) / None（无需翻译或未收录）。
#[tauri::command]
pub async fn translate_mod_titles(
    titles: Vec<String>,
) -> Result<Vec<Option<String>>, String> {
    use starcloud_minecraft::download::translate_title_zh;
    use std::sync::Arc;
    use tokio::sync::Semaphore;

    // 词典命中（本地零网络）的标题不占用并发
    let mut out: Vec<Option<String>> = Vec::with_capacity(titles.len());
    let mut pending: Vec<usize> = Vec::new();
    for (i, t) in titles.iter().enumerate() {
        if let Some(zh) = starcloud_minecraft::download::translate_title_zh_cache_hit(t) {
            out.push(zh);
        } else {
            out.push(None);
            pending.push(i);
        }
    }
    let sem = Arc::new(Semaphore::new(8));
    let mut handles = Vec::with_capacity(pending.len());
    for &i in &pending {
        let t = titles[i].clone();
        let sem = sem.clone();
        handles.push(tokio::spawn(async move {
            let _permit = sem.acquire().await.ok();
            translate_title_zh(&t).await
        }));
    }
    for (k, h) in handles.into_iter().enumerate() {
        let i = pending[k];
        out[i] = h.await.ok().flatten();
    }
    Ok(out)
}

/// 批量翻译 Mod 描述为中文（通过 MCIM 翻译 API）。
/// 输入为 (platform, id) 数组，返回与输入等长的 Option<String> 数组。
#[tauri::command]
pub async fn translate_mod_descriptions(
    items: Vec<(String, String)>,
) -> Result<Vec<Option<String>>, String> {
    use starcloud_minecraft::download::mcim_translate_description;
    use std::sync::Arc;
    use tokio::sync::Semaphore;

    let sem = Arc::new(Semaphore::new(6));
    let mut handles = Vec::with_capacity(items.len());
    for (platform, id) in items {
        let sem = sem.clone();
        handles.push(tokio::spawn(async move {
            let _permit = sem.acquire().await.ok();
            mcim_translate_description(&platform, &id).await
        }));
    }
    let mut out = Vec::with_capacity(handles.len());
    for h in handles {
        out.push(h.await.ok().flatten());
    }
    Ok(out)
}

/// 通过 MC 百科搜索页查找资源详情页 URL（未收录时返回 null，前端回退到搜索页）
#[tauri::command]
pub async fn mcmod_lookup(title: String) -> Result<Option<String>, String> {
    Ok(starcloud_minecraft::download::mcmod_lookup(&title).await)
}

/// 列出 Mod 可用版本文件
#[tauri::command]
pub async fn list_mod_versions(
    platform: String,
    mod_id: String,
    mc_version: String,
    loader: String,
) -> Result<Vec<starcloud_minecraft::download::ModFile>, String> {
    match platform.as_str() {
        "curseforge" => {
            starcloud_minecraft::download::list_curseforge_files(&mod_id, &mc_version, &loader)
                .await
                .map_err(|e| e.to_string())
        }
        _ => {
            starcloud_minecraft::download::list_modrinth_versions(&mod_id, &mc_version, &loader)
                .await
                .map_err(|e| e.to_string())
        }
    }
}

/// 下载 Mod 文件到 `.minecraft/mods/`（后台任务 + 进度 + 取消）
///
/// `file_id` 同时用作任务键：`mod:{platform}:{file_id}`。
#[tauri::command]
pub async fn download_mod(
    app: AppHandle,
    state: State<'_, AppState>,
    platform: String,
    file_id: String,
    file_name: String,
    url: String,
    size: u64,
    title: String,
) -> Result<(), String> {
    let config = config_snapshot(&state);

    let job_key = format!("mod:{}:{}", platform, file_id);
    let (cancel_tx, cancel_rx) = tokio::sync::oneshot::channel::<()>();
    {
        let mut jobs = state.install_jobs.lock().unwrap();
        if jobs.contains_key(&job_key) {
            return Err("该 Mod 正在下载中".into());
        }
        jobs.insert(job_key.clone(), cancel_tx);
    }

    // 调度中心：创建 Mod 下载任务（running，置顶）
    let task = TaskInfo::new("download", format!("下载 Mod：{}", title));
    let task_id = task.id.clone();
    push_task(&app, task);

    let emit_version_id = job_key.clone();
    let install_file_name = file_name.clone();
    let install_url = url.clone();
    let verbose = config.dev_mode; // 开发者模式：调度中心记录更详细进度日志
    tauri::async_runtime::spawn(async move {
        let svc = InstallService::new(config);
        let progress_app = app.clone();
        let log_app = app.clone();
        let log_task_id = task_id.clone();
        let progress_cb =
            task_progress_cb(progress_app, log_app, log_task_id, emit_version_id, verbose);
        let cancel_fut = async move {
            let _ = cancel_rx.await;
        };
        let fut = svc.download_mod_file(&install_url, &install_file_name, size, progress_cb);
        tokio::pin!(fut);
        let result: Result<(), String> = tokio::select! {
            r = fut => r.map_err(|e| e.to_string()),
            _ = cancel_fut => Err("已取消下载".into()),
        };
        // 移除取消句柄（无论成败）
        app.state::<AppState>()
            .install_jobs
            .lock()
            .unwrap()
            .remove(&job_key);
        match result {
            Ok(()) => {
                update_task(&app, &task_id, |t| {
                    t.status = "done".into();
                    t.detail = Some("Mod 下载完成".into());
                    t.push_log("Mod 下载完成");
                });
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id: format!("mod:{}", file_name),
                        success: true,
                        message: format!("{} 下载完成", file_name),
                    },
                );
            }
            Err(e) => {
                update_task(&app, &task_id, |t| {
                    t.status = "error".into();
                    t.detail = Some(e.clone());
                    t.push_log(format!("错误：{}", e));
                });
                let _ = app.emit(
                    EVENT_ERROR,
                    ErrorPayload {
                        context: "Mod 下载失败".into(),
                        message: e.clone(),
                    },
                );
                let _ = app.emit(
                    EVENT_STATUS,
                    StatusPayload {
                        status: "error".into(),
                        detail: Some(e.clone()),
                    },
                );
                // 失败/取消也补发 install-done（success=false）
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id: format!("mod:{}", file_name),
                        success: false,
                        message: e,
                    },
                );
            }
        }
    });
    Ok(())
}

/// 在系统默认浏览器中打开外部链接（如 MC 百科），供详情页「打开百科」使用
#[tauri::command]
pub fn open_url(url: String) -> Result<(), String> {
    if url.is_empty() {
        return Err("链接为空".into());
    }
    #[cfg(target_os = "windows")]
    {
        std::process::Command::new("cmd")
            .args(["/c", "start", "", &url])
            .spawn()
            .map_err(|e| format!("打开链接失败: {}", e))?;
        Ok(())
    }
    #[cfg(target_os = "macos")]
    {
        std::process::Command::new("open")
            .arg(&url)
            .spawn()
            .map_err(|e| format!("打开链接失败: {}", e))?;
        Ok(())
    }
    #[cfg(not(any(target_os = "windows", target_os = "macos")))]
    {
        std::process::Command::new("xdg-open")
            .arg(&url)
            .spawn()
            .map_err(|e| format!("打开链接失败: {}", e))?;
        Ok(())
    }
}

/// 打开系统原生「选择文件夹」对话框（Windows 资源管理器式目录选择），
/// 返回所选目录绝对路径；用户取消时返回 None。
#[tauri::command]
pub async fn pick_directory() -> Result<Option<String>, String> {
    tauri::async_runtime::spawn_blocking(|| {
        rfd::FileDialog::new()
            .set_title("选择 Minecraft 游戏目录")
            .pick_folder()
            .map(|p| p.to_string_lossy().to_string())
    })
    .await
    .map_err(|e| e.to_string())
}

// ---------------------------------------------------------------------------
// 启动命令
// ---------------------------------------------------------------------------

/// 启动游戏（后台任务 + 事件推送）
#[tauri::command]
pub async fn launch_game(
    app: AppHandle,
    state: State<'_, AppState>,
    version_id: String,
    username: String,
    dry_run: bool,
) -> Result<(), String> {
    // 防重复启动
    {
        let mut running = state.game_running.lock().unwrap();
        if *running {
            return Err("游戏已在运行中".into());
        }
        *running = true;
    }

    let mut config = config_snapshot(&state);

    // 版本级设置覆盖（内存 / 语言 / Java / JVM / 版本隔离），未设置的字段跟随全局默认
    apply_version_settings(&mut config, &version_id).await;

    // 游戏语言与启动器语言同步：仅首次启动或语言变更后写 options.txt
    // （同步上下文 await 安全；失败仅告警，不阻断启动；隔离时写入独立游戏目录）
    if config.synced_language.as_deref() != Some(config.language.as_str()) {
        let lang_target = config
            .runtime_game_dir
            .clone()
            .unwrap_or_else(|| config.dot_minecraft.clone());
        match LaunchService::apply_game_language(&lang_target, &config.language).await {
            Ok(()) => {
                config.synced_language = Some(config.language.clone());
                state.config.lock().unwrap().synced_language = Some(config.language.clone());
                let _ = persist_config(&config).await;
            }
            Err(e) => log::warn!("设置游戏语言失败（不影响启动）: {}", e),
        }
    }

    // 记录上次启动版本（首页默认选中），异步落盘不阻塞启动
    {
        let snap = {
            let mut guard = state.config.lock().unwrap();
            guard.last_version = Some(version_id.clone());
            guard.clone()
        };
        let path = config_file_path();
        tauri::async_runtime::spawn(async move {
            let _ = snap.save(path).await;
        });
    }

    let _ = app.emit(
        EVENT_STATUS,
        StatusPayload {
            status: "launching".into(),
            detail: Some(format!("启动 {}", version_id)),
        },
    );

    // 调度中心：创建启动任务（running，置顶；游戏运行期间禁止调整优先级）
    let task = TaskInfo::new("launch", format!("启动 {}", version_id));
    let task_id = task.id.clone();
    push_task(&app, task);

    let emit_version_id = version_id.clone();
    let progress_app = app.clone();
    let log_app = app.clone();
    let pid_app = app.clone();
    let start_task_id = task_id.clone();
    let start_app = app.clone();
    tauri::async_runtime::spawn(async move {
        update_task(&start_app, &start_task_id, |t| {
            t.push_log("正在启动游戏");
        });

        let svc = LaunchService::new(config);
        let run_task_id = task_id.clone();
        let result = svc
            .launch(
                &version_id,
                &username,
                None,
                dry_run,
                move |info| {
                    let _ = progress_app.emit(
                        EVENT_DOWNLOAD_PROGRESS,
                        ProgressPayload::from_progress(&emit_version_id, info),
                    );
                },
                Some(move |line: &str, is_err: bool| {
                    let _ = log_app.emit(
                        EVENT_GAME_LOG,
                        GameLogPayload {
                            line: line.to_string(),
                            is_stderr: is_err,
                        },
                    );
                }),
                Some(move |pid: u32| {
                    *pid_app.state::<AppState>().game_pid.lock().unwrap() = Some(pid);
                    // 进程成功拉起 = 启动成功，推送运行中状态（Home 绿色信号灯/停止按钮依赖它）
                    log::info!("游戏进程已拉起, 推送运行中状态 (PID {})", pid);
                    update_task(&pid_app, &run_task_id, |t| {
                        t.detail = Some(format!("游戏运行中（PID {}）", pid));
                    });
                    let emit_result = pid_app.emit(
                        EVENT_STATUS,
                        StatusPayload {
                            status: "running".into(),
                            detail: Some(format!("游戏运行中（PID {}）", pid)),
                        },
                    );
                    if let Err(e) = emit_result {
                        log::error!("推送游戏运行中状态失败: {}", e);
                    }
                }),
            )
            .await;
        match result {
            Ok(outcome) => {
                let message = if outcome.crashed {
                    format!("游戏异常退出（退出码 {:?}）", outcome.exit_code)
                } else {
                    "游戏已正常退出".into()
                };
                update_task(&app, &task_id, |t| {
                    t.status = "done".into();
                    t.detail = Some(message.clone());
                    t.push_log(message.clone());
                });
                let _ = app.emit(
                    EVENT_GAME_EXITED,
                    GameExitedPayload {
                        exit_code: outcome.exit_code,
                        crashed: outcome.crashed,
                        message,
                    },
                );
            }
            Err(e) => {
                update_task(&app, &task_id, |t| {
                    t.status = "error".into();
                    t.detail = Some(e.to_string());
                    t.push_log(format!("启动失败：{}", e));
                });
                let _ = app.emit(
                    EVENT_ERROR,
                    ErrorPayload {
                        context: "启动失败".into(),
                        message: e.to_string(),
                    },
                );
            }
        }
        *app.state::<AppState>().game_pid.lock().unwrap() = None;
        *app.state::<AppState>().game_running.lock().unwrap() = false;
        let _ = app.emit(
            EVENT_STATUS,
            StatusPayload {
                status: "idle".into(),
                detail: None,
            },
        );
    });
    Ok(())
}

/// 停止游戏进程
#[tauri::command]
pub fn stop_game(state: State<'_, AppState>) -> Result<(), String> {
    let mut running = state.game_running.lock().unwrap();
    let mut pid_guard = state.game_pid.lock().unwrap();
    if let Some(pid) = *pid_guard {
        let _ = std::process::Command::new("taskkill")
            .args(["/F", "/T", "/PID", &pid.to_string()])
            .status();
        *pid_guard = None;
        *running = false;
        Ok(())
    } else {
        Err("没有运行中的游戏".into())
    }
}

// ---------------------------------------------------------------------------
// 账户命令
// ---------------------------------------------------------------------------

/// 账户列表
#[tauri::command]
pub async fn list_accounts() -> Result<Vec<StoredAccount>, String> {
    AccountService::new()
        .list()
        .await
        .map_err(|e| format!("读取账户失败: {}", e))
}

/// 添加离线账户
#[tauri::command]
pub async fn add_offline_account(username: String) -> Result<StoredAccount, String> {
    AccountService::new()
        .add_offline(&username)
        .await
        .map_err(|e| format!("添加离线账户失败: {}", e))
}

/// 删除账户
#[tauri::command]
pub async fn remove_account(uuid: String) -> Result<bool, String> {
    AccountService::new()
        .remove(&uuid)
        .await
        .map_err(|e| format!("删除账户失败: {}", e))
}

/// 设置默认账户
#[tauri::command]
pub async fn set_default_account(uuid: String) -> Result<(), String> {
    AccountService::new()
        .set_default(&uuid)
        .await
        .map_err(|e| format!("切换默认账户失败: {}", e))
}

/// 发起微软设备码登录（返回 user_code + verification_uri 供用户授权）
#[tauri::command]
pub async fn login_microsoft_device() -> Result<DeviceCodeDto, String> {
    let dc = AccountService::new()
        .start_microsoft_login()
        .await
        .map_err(|e| format!("获取设备码失败: {}", e))?;
    Ok(DeviceCodeDto::from_response(dc))
}

/// 单次轮询微软登录状态（前端按 interval 间隔调用，直至 success / error）
#[tauri::command]
pub async fn poll_microsoft_login(device_code: String) -> Result<LoginPollDto, String> {
    let svc = AccountService::new();
    match svc.poll_microsoft_login(&device_code).await {
        Ok(MicrosoftLoginPoll::Pending(msg)) => Ok(LoginPollDto {
            state: "pending".into(),
            message: msg,
            account: None,
        }),
        Ok(MicrosoftLoginPoll::Success(acc)) => Ok(LoginPollDto {
            state: "success".into(),
            message: "登录成功".into(),
            account: Some(acc),
        }),
        Err(e) => Ok(LoginPollDto {
            state: "error".into(),
            message: e.to_string(),
            account: None,
        }),
    }
}

// ---------------------------------------------------------------------------
// 设置命令
// ---------------------------------------------------------------------------

/// 读取设置
#[tauri::command]
pub fn get_settings(state: State<'_, AppState>) -> Result<SettingsDto, String> {
    let config = config_snapshot(&state);
    Ok(SettingsDto::from_config(&config))
}

/// 保存设置
#[tauri::command]
pub async fn set_settings(state: State<'_, AppState>, settings: SettingsDto) -> Result<(), String> {
    // Java 路径等配置可能变化，使已缓存的检测结果失效
    clear_java_cache();
    // 锁作用域内完成修改并克隆快照，释放锁后再 await 持久化
    let config = {
        let mut guard = state.config.lock().unwrap();
        settings.apply_to(&mut guard);
        guard.download_concurrency = guard.download_concurrency.clamp(1, 64);
        guard.clone()
    };
    persist_config(&config)
        .await
        .map_err(|e| format!("保存配置失败: {}", e))
}

/// 清除版本清单本地缓存
#[tauri::command]
pub fn clear_version_cache() -> Result<(), String> {
    starcloud_minecraft::download::version_cache::clear_cache();
    log::info!("版本清单缓存已清除");
    Ok(())
}

/// 应用窗口毛玻璃（浅色 = 白色玻璃；深色 = 灰色玻璃）
///
/// Win11（build ≥22523）使用官方 DWM SystemBackdrop（DWMSBT_TRANSIENTWINDOW，Acrylic），
/// 这是当前 Win11 上唯一可靠的原生毛玻璃（SWCA 已被微软在 Win11 停用）。
/// 透明窗口（transparent: true）是 acrylic 生效的前提；tint 色由 DWM 深色模式决定
/// （浅色 = 白玻璃，深色 = 灰玻璃）；WebView2 默认背景须透明，半透明 CSS 才能透出毛玻璃。
pub fn apply_window_glass(app: &AppHandle, dark: bool) {
    #[cfg(target_os = "windows")]
    {
        use raw_window_handle::{HasWindowHandle, RawWindowHandle};
        use window_vibrancy::apply_acrylic;
        use windows_sys::Win32::Foundation::HWND;
        use windows_sys::Win32::Graphics::Dwm::{
            DwmSetWindowAttribute, DWMWA_USE_IMMERSIVE_DARK_MODE, DWMWA_WINDOW_CORNER_PREFERENCE,
            DWMWCP_ROUND,
        };

        if let Some(window) = app.get_webview_window("main") {
            // 0) WebView2 默认背景设为透明：半透明 CSS 才能透出 Acrylic
            if let Err(e) = window.set_background_color(Some(tauri::webview::Color(0, 0, 0, 0))) {
                log::warn!("设置 WebView2 透明背景失败: {}", e);
            }
            // 1) Acrylic（Win11 DWMSBT_TRANSIENTWINDOW；tint 供旧系统 SWCA 分支使用）
            //    浅色 = 白色 tint alpha 40（很淡）；深色 = 灰色 tint alpha 80
            let tint = if dark {
                (30, 36, 48, 80) // 灰色毛玻璃
            } else {
                (226, 231, 240, 40) // 白色毛玻璃（不透明度 40）
            };
            if let Err(e) = apply_acrylic(&window, Some(tint)) {
                log::warn!("应用窗口毛玻璃失败: {}", e);
            }
            // 2) 深浅主题：DWM 深色模式 → acrylic 灰玻璃 / 白玻璃
            // 3) DWM 圆角：窗口层裁剪四角，消除 CSS 圆角外的 blur 方形边缘
            if let Ok(handle) = window.window_handle() {
                if let RawWindowHandle::Win32(h) = handle.as_raw() {
                    let hwnd = h.hwnd.get() as HWND;
                    unsafe {
                        let dark_value: u32 = if dark { 1 } else { 0 };
                        let _ = DwmSetWindowAttribute(
                            hwnd,
                            DWMWA_USE_IMMERSIVE_DARK_MODE as u32,
                            &dark_value as *const u32 as *const _,
                            4,
                        );
                        let round: u32 = DWMWCP_ROUND as u32;
                        let _ = DwmSetWindowAttribute(
                            hwnd,
                            DWMWA_WINDOW_CORNER_PREFERENCE as u32,
                            &round as *const u32 as *const _,
                            4,
                        );
                    }
                }
            }
        }
    }
    let _ = dark;
}

/// 切换窗口毛玻璃主题（浅色 = 白色玻璃；深色 = 灰色玻璃）
#[tauri::command]
pub fn set_window_theme(app: AppHandle, dark: bool) -> Result<(), String> {
    apply_window_glass(&app, dark);
    Ok(())
}

/// 内存一键推荐（按系统物理内存计算）
#[tauri::command]
pub fn suggest_memory() -> Result<MemorySuggestion, String> {
    let mut sys = sysinfo::System::new_all();
    sys.refresh_memory();
    let total_mb = (sys.total_memory() / 1024 / 1024) as u64;
    // 推荐最大内存 = 物理内存一半（512 对齐），最小 = 最大 1/4（256 对齐）
    let raw_max = (total_mb / 2) as u32;
    let max_mb = (raw_max / 512 * 512).clamp(1024, 16384);
    let min_mb = (max_mb / 4 / 256 * 256).clamp(512, max_mb);
    Ok(MemorySuggestion {
        total_mb,
        min_mb,
        max_mb,
    })
}

/// 读取版本级设置（未保存过返回空默认）
#[tauri::command]
pub async fn get_version_settings(
    state: State<'_, AppState>,
    version_id: String,
) -> Result<VersionSettingsDto, String> {
    let config = config_snapshot(&state);
    let store = VersionSettingsStore::new(config.dot_minecraft);
    let vs = store
        .load(&version_id)
        .await
        .map_err(|e| format!("读取版本设置失败: {}", e))?;
    let mut dto = VersionSettingsDto::from_settings(&vs);
    // 未保存过版本级设置时，版本隔离跟随全局设置（默认开启）
    if !store.path(&version_id).exists() {
        dto.isolated = config.version_isolated;
    }
    Ok(dto)
}

/// 保存版本级设置（覆盖全局默认；传空字段可恢复默认）
#[tauri::command]
pub async fn set_version_settings(
    state: State<'_, AppState>,
    version_id: String,
    settings: VersionSettingsDto,
) -> Result<(), String> {
    let config = config_snapshot(&state);
    let store = VersionSettingsStore::new(config.dot_minecraft);
    store
        .save(&version_id, &settings.to_settings())
        .await
        .map_err(|e| format!("保存版本设置失败: {}", e))
}

/// 调度中心 - 删除任务
#[tauri::command]
pub fn remove_task(app: AppHandle, task_id: String) -> Result<(), String> {
    let st = app.state::<AppState>();
    let mut tasks = st.tasks.lock().unwrap();
    tasks.retain(|t| t.id != task_id);
    drop(tasks);
    emit_tasks(&app);
    Ok(())
}

/// 启动前自检（Java / 磁盘空间 / 内存设置 / 版本状态；应用版本级覆盖保持一致）
#[tauri::command]
pub async fn preflight_check(
    state: State<'_, AppState>,
    version_id: String,
) -> Result<Vec<PreflightIssue>, String> {
    let mut config = config_snapshot(&state);
    apply_version_settings(&mut config, &version_id).await;
    Ok(LaunchService::new(config).preflight(&version_id).await)
}

// ---------------------------------------------------------------------------
// 插件命令
// ---------------------------------------------------------------------------

/// GUI 插件列表（view/replacement）
#[tauri::command]
pub fn get_gui_plugins(state: State<'_, AppState>) -> Result<Vec<GuiPluginInfo>, String> {
    let config = config_snapshot(&state);
    Ok(plugin_host::scan_gui_plugins(&config.plugins_dir))
}

/// 安装 GUI 插件（source 为 URL 或本地 zip 路径）
#[tauri::command]
pub async fn install_plugin(
    state: State<'_, AppState>,
    source: String,
) -> Result<GuiPluginInfo, String> {
    let config = config_snapshot(&state);
    plugin_host::install_gui_plugin(&config.plugins_dir, &source).await
}

/// 删除 GUI 插件
#[tauri::command]
pub fn remove_plugin(state: State<'_, AppState>, plugin_id: String) -> Result<(), String> {
    let config = config_snapshot(&state);
    plugin_host::remove_gui_plugin(&config.plugins_dir, &plugin_id)
}

/// 启用/禁用 GUI 插件
#[tauri::command]
pub fn set_plugin_enabled(
    state: State<'_, AppState>,
    plugin_id: String,
    enabled: bool,
) -> Result<(), String> {
    let config = config_snapshot(&state);
    plugin_host::set_gui_plugin_enabled(&config.plugins_dir, &plugin_id, enabled)
}

/// 核心插件列表
#[tauri::command]
pub fn list_plugins(state: State<'_, AppState>) -> Result<Vec<String>, String> {
    let config = config_snapshot(&state);
    let manager = starcloud_minecraft::plugin::PluginManager::new(&config.plugins_dir);
    match manager.load_from_dir(&config.plugins_dir) {
        Ok(_) => manager
            .list_plugins()
            .map(|metas| {
                metas
                    .into_iter()
                    .map(|m| format!("{} v{}", m.id, m.version))
                    .collect()
            })
            .map_err(|e| format!("读取插件失败: {}", e)),
        Err(e) => {
            if e.to_string().contains("不存在") || e.to_string().contains("No such") {
                Ok(Vec::new())
            } else {
                Err(format!("读取插件失败: {}", e))
            }
        }
    }
}

// ---------------------------------------------------------------------------
// 日志 / Java
// ---------------------------------------------------------------------------

/// 最近游戏日志（读 .minecraft/logs/latest.log，返回末尾最多 400 行）
#[tauri::command]
pub async fn get_game_logs(state: State<'_, AppState>) -> Result<String, String> {
    let config = config_snapshot(&state);
    let log_path = config.dot_minecraft.join("logs").join("latest.log");
    if !log_path.exists() {
        return Ok("（暂无游戏日志）".into());
    }
    let content = tokio::fs::read_to_string(&log_path)
        .await
        .map_err(|e| format!("读取日志失败: {}", e))?;
    let lines: Vec<&str> = content.lines().rev().take(400).collect();
    let tail = lines.into_iter().rev().collect::<Vec<_>>().join("\n");
    Ok(tail)
}

/// 启动器本体日志（开发者模式；读 launcher.log，返回末尾最多 800 行）
#[tauri::command]
pub async fn get_launcher_logs() -> Result<String, String> {
    let log_dir = starcloud_minecraft::logging::default_log_dir();
    let log_path = log_dir.join("launcher.log");
    if !log_path.exists() {
        return Ok("（暂无启动器日志）".into());
    }
    let content = tokio::fs::read_to_string(&log_path)
        .await
        .map_err(|e| format!("读取启动器日志失败: {}", e))?;
    let lines: Vec<&str> = content.lines().rev().take(800).collect();
    let tail = lines.into_iter().rev().collect::<Vec<_>>().join("\n");
    Ok(tail)
}

/// 启动器版本号（单一来源：src-tauri/Cargo.toml 的 `version`，cargo 编译期注入）。
///
/// 前端通过 `invoke("app_version")` 获取显示版本，与 Cargo.toml 自动保持一致。
#[tauri::command]
pub fn app_version() -> String {
    env!("CARGO_PKG_VERSION").to_string()
}

/// 检测 Java（优先配置的 java_executable，否则自动检测）
///
/// 行为：
/// - 已检测过（进程级缓存命中）→ 直接复用缓存，不再重新搜索
/// - 未检测过 → 在调度中心创建「检测系统 Java」任务并后台执行，完成后写缓存
///
/// 检测耗时较长（扫描注册表 / Uninstall 键 / 候选 java 版本解析），
/// 放到阻塞线程池执行，避免卡住主线程导致前端一直显示「检测中」。
#[tauri::command]
pub async fn check_java(app: AppHandle) -> Result<Option<JavaInfo>, String> {
    let config = config_snapshot(&app.state::<AppState>());

    // 配置了显式路径则优先检测（并刷新缓存）
    if let Some(exe) = config.java_executable.clone() {
        let run = tauri::async_runtime::spawn_blocking(move || JavaDetector::detect_at(&exe))
            .await
            .map_err(|e| format!("Java 检测任务失败: {}", e))?;
        match run {
            Ok(runtime) => {
                cache_java(&runtime);
                return Ok(Some(JavaInfo::from_runtime(&runtime)));
            }
            Err(e) => log::warn!("配置的 Java 路径不可用: {}", e),
        }
    }

    // 缓存命中（校验可执行文件仍存在，避免返回失效路径）→ 直接复用，不重新搜索
    if let Some(runtime) = cached_java() {
        if runtime.executable.is_file() && runtime.major_version >= 8 {
            return Ok(Some(JavaInfo::from_runtime(&runtime)));
        }
        log::warn!(
            "Java 缓存失效（{} 不存在或版本过低），清除后重新搜索",
            runtime.executable.display()
        );
        clear_java_cache();
    }

    // 调度中心：创建「检测系统 Java」任务（running，置顶）
    let task = TaskInfo::new("java", "检测系统 Java");
    let task_id = task.id.clone();
    push_task(&app, task);

    let app2 = app.clone();
    let log_task_id = task_id.clone();
    let run = tauri::async_runtime::spawn_blocking(move || {
        update_task(&app2, &log_task_id, |t| {
            t.stage = Some("扫描注册表与系统目录".to_string());
            t.push_log("正在搜索系统 Java…");
        });
        JavaDetector::new().detect()
    })
    .await
    .map_err(|e| format!("Java 检测任务失败: {}", e))?;

    match run {
        Ok(runtime) => {
            cache_java(&runtime);
            let info = JavaInfo::from_runtime(&runtime);
            update_task(&app, &task_id, |t| {
                t.status = "done".into();
                t.detail = Some(format!(
                    "检测到 {}（Java {} · {} 位）",
                    info.version_string,
                    info.major_version,
                    if info.is_64bit { "64" } else { "32" }
                ));
                t.push_log(format!(
                    "检测到 {}（Java {}）",
                    info.version_string, info.major_version
                ));
            });
            Ok(Some(info))
        }
        Err(e) => {
            log::info!("未检测到系统 Java: {}", e);
            update_task(&app, &task_id, |t| {
                t.status = "error".into();
                t.detail = Some(format!("未检测到系统 Java：{}", e));
                t.push_log(format!("检测失败：{}", e));
            });
            Ok(None)
        }
    }
}

/// 可用的 Java 运行时（Java 管理器列表项）
#[derive(Debug, Clone, Serialize)]
pub struct JavaRuntimeDto {
    pub executable: String,
    pub major_version: u32,
    pub version_string: String,
    pub is_64bit: bool,
    /// 是否由启动器管理（位于 {data}/starcloud-launcher/java/，可删除）
    pub managed: bool,
    /// 来源："launcher"（启动器已下载）| "system"（系统检测）
    pub source: String,
}

impl JavaRuntimeDto {
    fn from_runtime(r: &JavaRuntime, managed: bool) -> Self {
        Self {
            executable: r.executable.to_string_lossy().to_string(),
            major_version: r.major_version,
            version_string: r.version_string.clone(),
            is_64bit: r.is_64bit,
            managed,
            source: if managed { "launcher" } else { "system" }.into(),
        }
    }
}

/// 列出所有可用 Java 运行时（启动器已下载 + 系统检测，按版本降序，去重）
///
/// 系统检测较慢（扫描注册表等），放阻塞线程池执行。
#[tauri::command]
pub async fn list_java_runtimes() -> Result<Vec<JavaRuntimeDto>, String> {
    let run = tauri::async_runtime::spawn_blocking(move || -> Result<Vec<JavaRuntimeDto>, String> {
        let downloader = JavaDownloader::new().map_err(|e| e.to_string())?;
        // 1. 启动器已下载
        let mut all: Vec<JavaRuntimeDto> = downloader
            .list_downloaded_runtimes()
            .iter()
            .map(|r| JavaRuntimeDto::from_runtime(r, true))
            .collect();
        // 2. 系统检测（detect_all 含启动器目录，剔除重复）
        let root = java_download_root();
        let seen: std::collections::HashSet<String> =
            all.iter().map(|r| r.executable.clone()).collect();
        for r in JavaDetector::new().detect_all() {
            let exe = r.executable.to_string_lossy().to_lowercase();
            if exe.starts_with(&root.to_string_lossy().to_lowercase())
                || seen.contains(&r.executable.to_string_lossy().to_string())
            {
                continue;
            }
            all.push(JavaRuntimeDto::from_runtime(&r, false));
        }
        // 版本降序，64 位优先
        all.sort_by(|a, b| {
            b.major_version
                .cmp(&a.major_version)
                .then(b.is_64bit.cmp(&a.is_64bit))
        });
        Ok(all)
    })
    .await
    .map_err(|e| format!("Java 列表任务失败: {}", e))?;
    run
}

/// 下载指定版本的 Java（后台任务，进度走事件；任务键 = java）
///
/// 已存在同版本时直接返回；下载完成后写入进程缓存，重启后由
/// 启动器 Java 目录扫描直接命中。
#[tauri::command]
pub async fn download_java_runtime(app: AppHandle, version: u32) -> Result<(), String> {
    let downloader = JavaDownloader::new().map_err(|e| e.to_string())?;
    if downloader.get_java_path(version).is_some() {
        return Ok(());
    }

    let task = TaskInfo::new("java", &format!("下载 Java {}", version));
    let task_id = task.id.clone();
    push_task(&app, task);

    let app2 = app.clone();
    let task_id2 = task_id.clone();
    tauri::async_runtime::spawn(async move {
        update_task(&app2, &task_id2, |t| {
            t.stage = Some("下载并解压 Adoptium JRE".to_string());
            t.push_log(format!("开始下载 Java {}…", version));
        });
        let result = match JavaDownloader::new() {
            Ok(d) => d.download_java(version).await,
            Err(e) => Err(e),
        };
        match result {
            Ok(rt) => {
                cache_java(&rt);
                update_task(&app2, &task_id2, |t| {
                    t.status = "done".into();
                    t.detail = Some(format!("Java {} 就绪（{}）", version, rt.version_string));
                    t.push_log(format!("Java {} 下载完成：{}", version, rt.executable.display()));
                });
            }
            Err(e) => {
                log::warn!("下载 Java {} 失败: {}", version, e);
                update_task(&app2, &task_id2, |t| {
                    t.status = "error".into();
                    t.detail = Some(format!("下载失败：{}", e));
                    t.push_log(format!("下载失败：{}", e));
                });
            }
        }
    });
    Ok(())
}

/// 删除已下载的 Java 版本（同时清理缓存，避免失效路径被复用）
#[tauri::command]
pub fn remove_java_runtime(version: u32) -> Result<(), String> {
    let downloader = JavaDownloader::new().map_err(|e| e.to_string())?;
    downloader.remove_java(version).map_err(|e| e.to_string())
}

// ---------------------------------------------------------------------------
// 陶瓦联机（Terracotta）命令
// ---------------------------------------------------------------------------

/// 联机核心目录（config.terracotta_dir/core）
fn terracotta_core_dir(config: &LauncherConfig) -> std::path::PathBuf {
    config.terracotta_dir.join("core")
}

/// 构建联机状态快照（`terracotta_status` 命令返回值 / 轮询器广播共用）
fn terracotta_snapshot(state: &State<'_, AppState>) -> TerracottaStatePayload {
    let config = config_snapshot(state);
    let core_dir = terracotta_core_dir(&config);
    TerracottaStatePayload {
        installed: starcloud_minecraft::terracotta::is_installed(&core_dir),
        version: starcloud_minecraft::terracotta::installed_version(&core_dir),
        running: state.terracotta.lock().unwrap().is_some(),
        state: None,
        error: None,
    }
}

/// 后台轮询器：会话运行期间每 500ms 查询核心状态并广播；
/// 核心子进程意外退出时自动回收会话（停止广播）。
pub fn start_terracotta_poller(app: AppHandle) {
    tauri::async_runtime::spawn(async move {
        loop {
            tokio::time::sleep(std::time::Duration::from_millis(500)).await;

            // 会话已不存在则休眠（未启动联机）
            let port = {
                let st = app.state::<AppState>();
                let guard = st.terracotta.lock().unwrap();
                guard.as_ref().map(|s| s.port)
            };
            let Some(port) = port else { continue };

            // 子进程已退出则回收会话
            let exited = {
                let st = app.state::<AppState>();
                let mut guard = st.terracotta.lock().unwrap();
                let mut out = false;
                if let Some(s) = guard.as_mut() {
                    if let Ok(Some(_)) = s.child.try_wait() {
                        guard.take();
                        out = true;
                    }
                }
                out
            };
            if exited {
                continue;
            }

            let base = terracotta_snapshot(&app.state::<AppState>());
            match starcloud_minecraft::terracotta::fetch_state(port).await {
                Ok(Some(st)) => {
                    let _ = app.emit(
                        EVENT_TERRACOTTA_STATE,
                        TerracottaStatePayload {
                            running: true,
                            state: Some(st),
                            ..base
                        },
                    );
                }
                // 核心就绪但查询为空 / 非 2xx：保留当前状态，不重复广播
                Ok(None) => {}
                Err(e) => {
                    let _ = app.emit(
                        EVENT_TERRACOTTA_STATE,
                        TerracottaStatePayload {
                            running: true,
                            error: Some(e.to_string()),
                            ..base
                        },
                    );
                }
            }
        }
    });
}

/// 确保联机核心会话已启动（未启动则拉起进程），返回本地 HTTP 端口
async fn terracotta_ensure_session(state: &State<'_, AppState>) -> Result<u16, String> {
    {
        let guard = state.terracotta.lock().unwrap();
        if let Some(s) = guard.as_ref() {
            return Ok(s.port);
        }
    }
    let config = config_snapshot(state);
    let core_dir = terracotta_core_dir(&config);
    if !starcloud_minecraft::terracotta::is_installed(&core_dir) {
        return Err("联机核心未安装，请先点击「安装联机核心」".into());
    }
    // 进程启动 + 等待端口文件可能阻塞至多 30s，放到阻塞线程池执行
    let session = tauri::async_runtime::spawn_blocking(move || {
        starcloud_minecraft::terracotta::launch_session(&core_dir)
    })
    .await
    .map_err(|e| format!("联机核心启动任务失败: {}", e))?
    .map_err(|e| e.to_string())?;
    let port = session.port;
    *state.terracotta.lock().unwrap() = Some(session);
    Ok(port)
}

/// 联机状态快照（安装 / 运行 / 当前状态）
#[tauri::command]
pub async fn terracotta_status(
    state: State<'_, AppState>,
) -> Result<TerracottaStatePayload, String> {
    let mut payload = terracotta_snapshot(&state);
    // 会话运行中时补一次实时状态
    if payload.running {
        let port = {
            let guard = state.terracotta.lock().unwrap();
            guard.as_ref().map(|s| s.port)
        };
        if let Some(port) = port {
            match starcloud_minecraft::terracotta::fetch_state(port).await {
                Ok(Some(st)) => payload.state = Some(st),
                Ok(None) => {}
                Err(e) => payload.error = Some(e.to_string()),
            }
        }
    }
    Ok(payload)
}

/// 安装陶瓦联机核心（后台任务 + 进度事件，支持取消）
///
/// 任务键（进度事件 / install-done job_id）= `terracotta:core`。
#[tauri::command]
pub async fn terracotta_install(
    app: AppHandle,
    state: State<'_, AppState>,
) -> Result<(), String> {
    let config = config_snapshot(&state);

    let job_key = "terracotta:core".to_string();
    let (cancel_tx, cancel_rx) = tokio::sync::oneshot::channel::<()>();
    {
        let mut jobs = state.install_jobs.lock().unwrap();
        if jobs.contains_key(&job_key) {
            return Err("联机核心正在安装中".into());
        }
        jobs.insert(job_key.clone(), cancel_tx);
    }

    let _ = app.emit(
        EVENT_STATUS,
        StatusPayload {
            status: "installing".into(),
            detail: Some("安装陶瓦联机核心".into()),
        },
    );

    // 调度中心：创建安装任务（running，置顶）
    let task = TaskInfo::new("install", "安装陶瓦联机核心");
    let task_id = task.id.clone();
    push_task(&app, task);

    let emit_id = job_key.clone();
    let install_dir = config.terracotta_dir.clone();
    let verbose = config.dev_mode; // 开发者模式：调度中心记录更详细进度日志
    tauri::async_runtime::spawn(async move {
        let tracker = starcloud_minecraft::download::ProgressTracker::new();
        let progress_app = app.clone();
        let log_app = app.clone();
        let log_task_id = task_id.clone();
        let progress_cb =
            task_progress_cb(progress_app, log_app, log_task_id, emit_id, verbose);
        tracker.on_progress(progress_cb);
        // 取消信号到达时中止安装
        let cancel_fut = async move {
            let _ = cancel_rx.await;
        };
        let install_fut = starcloud_minecraft::terracotta::install_core(&install_dir, &tracker);
        tokio::pin!(install_fut);
        let result: Result<(), String> = tokio::select! {
            r = install_fut => r.map(|_| ()).map_err(|e| e.to_string()),
            _ = cancel_fut => Err("已取消安装".into()),
        };
        // 移除取消句柄（无论成败）
        app.state::<AppState>()
            .install_jobs
            .lock()
            .unwrap()
            .remove(&job_key);
        match result {
            Ok(()) => {
                update_task(&app, &task_id, |t| {
                    t.status = "done".into();
                    t.detail = Some("联机核心安装完成".into());
                    t.push_log("联机核心安装完成");
                });
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id: "terracotta:core".into(),
                        success: true,
                        message: "陶瓦联机核心安装完成".into(),
                    },
                );
            }
            Err(e) => {
                update_task(&app, &task_id, |t| {
                    t.status = "error".into();
                    t.detail = Some(e.clone());
                    t.push_log(format!("错误：{}", e));
                });
                let _ = app.emit(
                    EVENT_ERROR,
                    ErrorPayload {
                        context: "联机核心安装失败".into(),
                        message: e.clone(),
                    },
                );
                let _ = app.emit(
                    EVENT_STATUS,
                    StatusPayload {
                        status: "error".into(),
                        detail: Some(e.clone()),
                    },
                );
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        job_id: job_key,
                        version_id: "terracotta:core".into(),
                        success: false,
                        message: e,
                    },
                );
            }
        }
    });
    Ok(())
}

/// 创建联机房间（游戏内先开启局域网开放，随后等待打洞成功）
#[tauri::command]
pub async fn terracotta_start_host(state: State<'_, AppState>) -> Result<(), String> {
    let port = terracotta_ensure_session(&state).await?;
    let player = {
        let config = config_snapshot(&state);
        config
            .last_username
            .clone()
            .unwrap_or_else(|| "Player".into())
    };
    // 国内用户优先接入国内节点（include_cn = true）
    let nodes = starcloud_minecraft::terracotta::fetch_nodes(true).await;
    starcloud_minecraft::terracotta::set_scanning(port, &player, &nodes)
        .await
        .map_err(|e| format!("进入创建房间流程失败: {}", e))
}

/// 加入他人房间（room = 房主分享的邀请码）
#[tauri::command]
pub async fn terracotta_join_room(
    state: State<'_, AppState>,
    room: String,
) -> Result<(), String> {
    if room.trim().is_empty() {
        return Err("请输入房间邀请码".into());
    }
    let port = terracotta_ensure_session(&state).await?;
    let player = {
        let config = config_snapshot(&state);
        config
            .last_username
            .clone()
            .unwrap_or_else(|| "Player".into())
    };
    let nodes = starcloud_minecraft::terracotta::fetch_nodes(true).await;
    starcloud_minecraft::terracotta::set_guesting(port, room.trim(), &player, &nodes)
        .await
        .map_err(|e| format!("进入加入房间流程失败: {}", e))
}

/// 停止联机（回到空闲并关闭核心会话）
#[tauri::command]
pub fn terracotta_stop(state: State<'_, AppState>) -> Result<(), String> {
    let mut guard = state.terracotta.lock().unwrap();
    match guard.take() {
        Some(mut session) => {
            session.shutdown();
            Ok(())
        }
        None => Err("联机核心未在运行".into()),
    }
}

/// 拉取联机核心日志（调试用）
#[tauri::command]
pub async fn terracotta_logs(state: State<'_, AppState>) -> Result<String, String> {
    let port = {
        let guard = state.terracotta.lock().unwrap();
        match guard.as_ref() {
            Some(s) => s.port,
            None => return Err("联机核心未在运行".into()),
        }
    };
    starcloud_minecraft::terracotta::fetch_logs(port)
        .await
        .map_err(|e| format!("读取联机日志失败: {}", e))
}

// ---------------------------------------------------------------------------
// 调度中心 - 任务列表（创建/更新/重排 + 持久化 + 事件广播）
// ---------------------------------------------------------------------------

/// 构建任务进度回调：阶段切换必记日志；开发者模式下额外记录每跨 10% 的进度里程碑；
/// 同时广播下载进度事件（原各命令内联 progress_cb 的统一实现）。
fn task_progress_cb(
    progress_app: AppHandle,
    log_app: AppHandle,
    log_task_id: String,
    emit_version_id: String,
    verbose: bool,
) -> impl Fn(&starcloud_minecraft::download::ProgressInfo) + Send + Sync + 'static {
    let stage_guard = std::sync::Mutex::new(String::new());
    let bucket_guard = std::sync::Mutex::new(None::<u32>);
    move |info: &starcloud_minecraft::download::ProgressInfo| {
        let stage = info.stage.clone();
        let mut last = stage_guard.lock().unwrap();
        if *last != stage {
            *last = stage.clone();
            update_task(&log_app, &log_task_id, |t| {
                t.push_log(format!("阶段：{}", stage));
                t.progress = Some(info.percentage);
                t.stage = Some(stage.clone());
                t.speed = Some(info.speed_bytes_per_sec);
                t.eta = info.eta_secs;
            });
        } else {
            // 开发者模式：每跨过 10% 进度记录一次里程碑（更详细的调度日志）
            if verbose {
                let bucket = (info.percentage / 10.0) as u32;
                let mut last_bucket = bucket_guard.lock().unwrap();
                if *last_bucket != Some(bucket) {
                    *last_bucket = Some(bucket);
                    let stage_now = stage.clone();
                    update_task(&log_app, &log_task_id, |t| {
                        t.push_log(format!("进度：{}%（{}）", info.percentage.floor() as u32, stage_now));
                    });
                }
            }
            update_task_progress(&log_app, &log_task_id, |t| {
                t.progress = Some(info.percentage);
                t.stage = Some(stage.clone());
                t.speed = Some(info.speed_bytes_per_sec);
                t.eta = info.eta_secs;
            });
        }
        let _ = progress_app.emit(
            EVENT_DOWNLOAD_PROGRESS,
            ProgressPayload::from_progress(&emit_version_id, info),
        );
    }
}

/// 持久化当前任务列表并按优先级排序后广播给前端
fn emit_tasks(app: &AppHandle) {
    let tasks = {
        let state = app.state::<AppState>();
        let mut guard = state.tasks.lock().unwrap();
        guard.sort_by_key(|t| t.priority);
        let snapshot = guard.clone();
        save_tasks(&guard);
        snapshot
    };
    let _ = app.emit(EVENT_TASKS_UPDATED, TasksUpdatedPayload { tasks });
}

/// 创建新任务（priority 0 置顶）
fn push_task(app: &AppHandle, task: TaskInfo) {
    {
        let st = app.state::<AppState>();
        let mut tasks = st.tasks.lock().unwrap();
        tasks.push(task);
    }
    emit_tasks(app);
}

/// 按任务 id 更新状态/详情（不存在则忽略）
fn update_task(app: &AppHandle, id: &str, f: impl FnOnce(&mut TaskInfo)) {
    {
        let st = app.state::<AppState>();
        let mut tasks = st.tasks.lock().unwrap();
        if let Some(t) = tasks.iter_mut().find(|t| t.id == id) {
            f(t);
            t.updated_at = now_millis();
        }
    }
    emit_tasks(app);
}

/// 高频更新任务进度（仅内存 + 事件推送，不写盘；终态由 update_task 落盘）
///
/// 下载进度回调约每 100ms 触发一次，直接写盘会频繁磨损磁盘。
fn update_task_progress(app: &AppHandle, id: &str, f: impl FnOnce(&mut TaskInfo)) {
    {
        let st = app.state::<AppState>();
        let mut tasks = st.tasks.lock().unwrap();
        if let Some(t) = tasks.iter_mut().find(|t| t.id == id) {
            f(t);
            t.updated_at = now_millis();
        }
    }
    let tasks = {
        let st = app.state::<AppState>();
        let mut guard = st.tasks.lock().unwrap();
        guard.sort_by_key(|t| t.priority);
        guard.clone()
    };
    let _ = app.emit(EVENT_TASKS_UPDATED, TasksUpdatedPayload { tasks });
}

/// 调度中心 - 任务列表（priority 升序，0 最高）
#[tauri::command]
pub fn list_tasks(state: State<'_, AppState>) -> Result<Vec<TaskInfo>, String> {
    let mut tasks = state.tasks.lock().unwrap();
    tasks.sort_by_key(|t| t.priority);
    Ok(tasks.clone())
}

/// 调度中心 - 拖拽重排：按前端提交的 id 顺序重分配优先级（0 最高）
///
/// 未包含在 task_ids 中的任务保持原有相对顺序追加到尾部。
#[tauri::command]
pub fn set_task_order(
    app: AppHandle,
    _state: State<'_, AppState>,
    task_ids: Vec<String>,
) -> Result<(), String> {
    let st = app.state::<AppState>();
    let mut tasks = st.tasks.lock().unwrap();
    let mut ordered: Vec<TaskInfo> = Vec::with_capacity(tasks.len());
    for id in &task_ids {
        if let Some(pos) = tasks.iter().position(|t| &t.id == id) {
            ordered.push(tasks.remove(pos));
        }
    }
    ordered.append(&mut tasks); // 剩余任务保持原相对顺序
    for (i, t) in ordered.iter_mut().enumerate() {
        t.priority = i as u32;
        t.updated_at = now_millis();
    }
    save_tasks(&ordered);
    let snapshot = ordered.clone();
    drop(tasks);
    let _ = app.emit(EVENT_TASKS_UPDATED, TasksUpdatedPayload { tasks: snapshot });
    Ok(())
}

// ---------------------------------------------------------------------------
// 下载源（多源镜像：启动自动测速选源）
// ---------------------------------------------------------------------------

/// 下载源状态：当前活动源 + 最近一次测速结果
#[derive(Debug, Clone, Serialize)]
pub struct DownloadSourceStatus {
    /// 活动下载源 id（official / bmclapi / mcbbs）
    pub active: String,
    /// 各源测速结果（可达性 / 延迟 / 速度）
    pub results: Vec<starcloud_minecraft::download::sources::SourceProbe>,
}

/// 查询当前下载源与最近一次测速结果（应用启动时已自动测速并切换最快源）
#[tauri::command]
pub fn get_download_sources() -> Result<DownloadSourceStatus, String> {
    Ok(DownloadSourceStatus {
        active: starcloud_minecraft::download::sources::active_source()
            .id()
            .to_string(),
        results: starcloud_minecraft::download::sources::probe_results().unwrap_or_default(),
    })
}

/// 手动重新测速所有下载源并切换最快的可达源（返回最新状态）
#[tauri::command]
pub async fn probe_download_sources(
    app: AppHandle,
) -> Result<DownloadSourceStatus, String> {
    use starcloud_minecraft::download::sources::{
        active_source, pick_best, probe_all_sources, set_active_source,
    };
    let results = probe_all_sources().await;
    if let Some(best) = pick_best(&results) {
        set_active_source(best);
    }
    let _ = app.emit("download-sources", &results);
    Ok(DownloadSourceStatus {
        active: active_source().id().to_string(),
        results,
    })
}

/// 手动切换下载源（用户设置，覆盖自动测速的选择）
#[tauri::command]
pub fn set_download_source(source: String) -> Result<(), String> {
    use starcloud_minecraft::download::sources::SourceKind;
    let kind = SourceKind::from_id(&source).ok_or_else(|| format!("未知下载源: {}", source))?;
    starcloud_minecraft::download::sources::set_active_source(kind);
    Ok(())
}

/// 加载本地缓存的热门模组（启动器打开时立即展示，无需等待网络）
#[tauri::command]
pub async fn load_popular_mods() -> Result<Option<serde_json::Value>, String> {
    use starcloud_minecraft::download::mod_cache;
    match mod_cache::load_popular_mods_cache() {
        Some(cache) => {
            let json = serde_json::to_value(&cache).map_err(|e| e.to_string())?;
            Ok(Some(json))
        }
        None => Ok(None),
    }
}

/// 获取 Mod 图标本地缓存路径（若未缓存则异步下载）。
/// 返回本地文件绝对路径，前端用 `convertFileSrc` 转为 asset URL。
#[tauri::command]
pub async fn get_cached_icon(url: String) -> Result<String, String> {
    use starcloud_minecraft::download::icon_cache;

    // 空 URL → 返回空
    if url.is_empty() {
        return Ok(String::new());
    }

    match icon_cache::cache_icon(&url).await {
        Ok(path) => Ok(path.to_string_lossy().to_string()),
        Err(e) => {
            log::warn!("图标缓存失败 {}: {}", url, e);
            // 返回原始 URL 作为回退
            Ok(url)
        }
    }
}
