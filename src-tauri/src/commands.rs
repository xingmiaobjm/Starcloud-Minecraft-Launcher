//! Tauri 后端命令 - 版本/安装/启动/账户/设置/插件/日志/Java
//!
//! 长耗时操作（安装/启动）采用后台任务 + 事件推送，前端免轮询。

use crate::config_file_path;
use crate::events::{
    ErrorPayload, GameExitedPayload, GameLogPayload, InstallDonePayload, ProgressPayload,
    StatusPayload, EVENT_DOWNLOAD_PROGRESS, EVENT_ERROR, EVENT_GAME_EXITED, EVENT_GAME_LOG,
    EVENT_INSTALL_DONE, EVENT_STATUS,
};
use crate::plugin_host::{self, GuiPluginInfo};
use crate::state::AppState;
use serde::Serialize;
use starcloud_minecraft::account::microsoft::DeviceCodeResponse;
use starcloud_minecraft::account::store::StoredAccount;
use starcloud_minecraft::config::LauncherConfig;
use starcloud_minecraft::core::{
    AccountService, InstallService, LaunchService, MicrosoftLoginPoll,
};
use starcloud_minecraft::download::{fetch_version_manifest, DownloadEngine};
use starcloud_minecraft::error::Result as CoreResult;
use starcloud_minecraft::launch::java::{JavaDetector, JavaRuntime};
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
    pub default_window_width: u32,
    pub default_window_height: u32,
    pub java_executable: Option<String>,
    pub extra_jvm_args: Vec<String>,
    pub download_concurrency: usize,
    pub download_max_retries: u32,
}

impl SettingsDto {
    fn from_config(c: &LauncherConfig) -> Self {
        Self {
            dot_minecraft: c.dot_minecraft.to_string_lossy().to_string(),
            language: c.language.clone(),
            default_min_memory_mb: c.default_min_memory_mb,
            default_max_memory_mb: c.default_max_memory_mb,
            default_window_width: c.default_window_width,
            default_window_height: c.default_window_height,
            java_executable: c
                .java_executable
                .as_ref()
                .map(|p| p.to_string_lossy().to_string()),
            extra_jvm_args: c.extra_jvm_args.clone(),
            download_concurrency: c.download_concurrency,
            download_max_retries: c.download_max_retries,
        }
    }

    fn apply_to(self, c: &mut LauncherConfig) {
        c.dot_minecraft = self.dot_minecraft.into();
        c.language = self.language;
        c.default_min_memory_mb = self.default_min_memory_mb.max(512);
        c.default_max_memory_mb = self.default_max_memory_mb.max(512);
        c.default_window_width = self.default_window_width.max(640);
        c.default_window_height = self.default_window_height.max(480);
        c.java_executable = self.java_executable.map(Into::into);
        c.extra_jvm_args = self.extra_jvm_args;
        c.download_concurrency = self.download_concurrency.max(1);
        c.download_max_retries = self.download_max_retries.max(1);
    }
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

// ---------------------------------------------------------------------------
// 版本命令
// ---------------------------------------------------------------------------

/// 远程版本列表（含本地安装状态）
#[tauri::command]
pub async fn list_versions(state: State<'_, AppState>) -> Result<Vec<VersionInfo>, String> {
    let config = config_snapshot(&state);
    let installer = VersionInstaller::new(DownloadEngine::new(&config.dot_minecraft));

    let manifest = fetch_version_manifest()
        .await
        .map_err(|e| format!("获取版本清单失败: {}", e))?;

    let mut items = Vec::with_capacity(manifest.versions.len());
    for entry in manifest.versions.iter().take(300) {
        let installed = installer.is_installed(&entry.id);
        let size_bytes = if installed {
            installer.version_size(&entry.id).await.unwrap_or(0)
        } else {
            0
        };
        items.push(VersionInfo {
            id: entry.id.clone(),
            version_type: format!("{:?}", entry.version_type),
            release_time: entry.release_time.clone(),
            installed,
            size_bytes,
        });
    }
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

/// 安装版本（后台任务 + 事件推送）
#[tauri::command]
pub async fn install_version(
    app: AppHandle,
    state: State<'_, AppState>,
    version_id: String,
) -> Result<(), String> {
    let config = config_snapshot(&state);
    let _ = app.emit(
        EVENT_STATUS,
        StatusPayload {
            status: "installing".into(),
            detail: Some(format!("安装 {}", version_id)),
        },
    );

    let emit_version_id = version_id.clone();
    tauri::async_runtime::spawn(async move {
        let svc = InstallService::new(config);
        let progress_app = app.clone();
        let result = svc
            .install_version(&version_id, move |info| {
                let _ = progress_app.emit(
                    EVENT_DOWNLOAD_PROGRESS,
                    ProgressPayload::from_progress(&emit_version_id, info),
                );
            })
            .await;
        match result {
            Ok(()) => {
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        version_id,
                        success: true,
                        message: "安装完成".into(),
                    },
                );
            }
            Err(e) => {
                let _ = app.emit(
                    EVENT_ERROR,
                    ErrorPayload {
                        context: "安装失败".into(),
                        message: e.to_string(),
                    },
                );
                let _ = app.emit(
                    EVENT_STATUS,
                    StatusPayload {
                        status: "error".into(),
                        detail: Some(e.to_string()),
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

/// 安装 Fabric 加载器（后台任务 + 事件推送，成功后通过 install-done 返回 fabric 版本 id）
#[tauri::command]
pub async fn install_fabric(
    app: AppHandle,
    state: State<'_, AppState>,
    mc_version: String,
) -> Result<(), String> {
    let config = config_snapshot(&state);
    let _ = app.emit(
        EVENT_STATUS,
        StatusPayload {
            status: "installing".into(),
            detail: Some(format!("为 {} 安装 Fabric", mc_version)),
        },
    );

    let emit_version_id = mc_version.clone();
    tauri::async_runtime::spawn(async move {
        let svc = InstallService::new(config);
        let progress_app = app.clone();
        let result = svc
            .install_fabric(&mc_version, move |info| {
                let _ = progress_app.emit(
                    EVENT_DOWNLOAD_PROGRESS,
                    ProgressPayload::from_progress(&emit_version_id, info),
                );
            })
            .await;
        match result {
            Ok(fabric_id) => {
                let _ = app.emit(
                    EVENT_INSTALL_DONE,
                    InstallDonePayload {
                        version_id: fabric_id,
                        success: true,
                        message: "Fabric 安装完成".into(),
                    },
                );
            }
            Err(e) => {
                let _ = app.emit(
                    EVENT_ERROR,
                    ErrorPayload {
                        context: "Fabric 安装失败".into(),
                        message: e.to_string(),
                    },
                );
                let _ = app.emit(
                    EVENT_STATUS,
                    StatusPayload {
                        status: "error".into(),
                        detail: Some(e.to_string()),
                    },
                );
            }
        }
    });
    Ok(())
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

    let config = config_snapshot(&state);
    let _ = app.emit(
        EVENT_STATUS,
        StatusPayload {
            status: "launching".into(),
            detail: Some(format!("启动 {}", version_id)),
        },
    );

    let emit_version_id = version_id.clone();
    let progress_app = app.clone();
    let log_app = app.clone();
    let pid_app = app.clone();
    tauri::async_runtime::spawn(async move {
        let svc = LaunchService::new(config);
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
    // 锁作用域内完成修改并克隆快照，释放锁后再 await 持久化
    let config = {
        let mut guard = state.config.lock().unwrap();
        settings.apply_to(&mut guard);
        guard.clone()
    };
    persist_config(&config)
        .await
        .map_err(|e| format!("保存配置失败: {}", e))
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

/// 检测 Java（优先配置的 java_executable）
#[tauri::command]
pub fn check_java(state: State<'_, AppState>) -> Result<Option<JavaInfo>, String> {
    let config = config_snapshot(&state);

    // 配置了显式路径则优先检测
    if let Some(exe) = &config.java_executable {
        match JavaDetector::detect_at(exe) {
            Ok(runtime) => return Ok(Some(JavaInfo::from_runtime(&runtime))),
            Err(e) => log::warn!("配置的 Java 路径不可用: {}", e),
        }
    }

    match JavaDetector::new().detect() {
        Ok(runtime) => Ok(Some(JavaInfo::from_runtime(&runtime))),
        Err(e) => {
            log::info!("未检测到系统 Java: {}", e);
            Ok(None)
        }
    }
}
