//! 启动服务 - 游戏启动的高层封装

use crate::account::{
    AccountCredentials, AccountStore, AccountType, MicrosoftAuth, OfflineAccount,
};
use crate::config::{LaunchProfile, LauncherConfig};
use crate::core::install::InstallService;
use crate::download::engine::DownloadEngine;
use crate::download::progress::{ProgressInfo, ProgressStatus, ProgressTracker};
use crate::error::Result;
use crate::launch::java::JavaDetector;
use crate::launch::{LaunchConfig, LaunchEngine};
use crate::plugin::{CrashInfo, PluginContext, PluginManager, PluginStage};
use crate::version::manifest::VersionDetails;
use crate::version_manager::{VersionInstaller, VersionValidator};
use std::io::BufRead;
use std::sync::Arc;

/// 启动结果
#[derive(Debug, Clone)]
pub struct LaunchOutcome {
    /// 进程退出码（被信号终止或 dry-run 时为 None）
    pub exit_code: Option<i32>,
    /// 是否异常退出（非 0 或信号终止）
    pub crashed: bool,
    /// 崩溃详情（crashed 时填充）
    pub crash_info: Option<CrashInfo>,
}

impl LaunchOutcome {
    /// 是否成功退出
    pub fn succeeded(&self) -> bool {
        self.exit_code == Some(0)
    }
}

/// 启动服务 - 确保安装 → 构建配置 → 校验 → 启动 → 输出流转发 → 退出码/崩溃判定
pub struct LaunchService {
    config: LauncherConfig,
    plugin_manager: Option<Arc<PluginManager>>,
}

impl LaunchService {
    /// 创建启动服务
    pub fn new(config: LauncherConfig) -> Self {
        Self {
            config,
            plugin_manager: None,
        }
    }

    /// 关联插件管理器（事件广播）
    pub fn with_plugin_manager(mut self, plugin_manager: Arc<PluginManager>) -> Self {
        self.plugin_manager = Some(plugin_manager);
        self
    }

    /// 广播插件事件（失败仅记录警告）
    fn fire(&self, stage: PluginStage, ctx: &mut PluginContext) {
        if let Some(pm) = &self.plugin_manager {
            if let Err(e) = pm.fire_event(stage, ctx) {
                log::warn!("插件事件广播失败: {}", e);
            }
        }
    }

    /// 确保版本完整安装（未安装则自动安装）
    async fn ensure_installed(&self, version_id: &str) -> Result<()> {
        let installer = VersionInstaller::new(DownloadEngine::new(&self.config.dot_minecraft));
        if !installer.is_installed(version_id) {
            log::info!("版本 {} 未安装，自动安装", version_id);
            let mut svc = InstallService::new(self.config.clone());
            if let Some(pm) = &self.plugin_manager {
                svc = svc.with_plugin_manager(pm.clone());
            }
            svc.install_version(version_id, |_| {}).await?;
        }
        Ok(())
    }

    /// 构建启动配置（应用配置默认值与 profile 覆盖）
    fn build_launch_config(
        &self,
        version_id: &str,
        username: &str,
        profile: Option<&LaunchProfile>,
    ) -> LaunchConfig {
        let (min_mem, max_mem, width, height) = profile
            .map(|p| {
                (
                    p.min_memory_mb,
                    p.max_memory_mb,
                    p.window_width,
                    p.window_height,
                )
            })
            .unwrap_or((
                self.config.default_min_memory_mb,
                self.config.default_max_memory_mb,
                self.config.default_window_width,
                self.config.default_window_height,
            ));

        let mut cfg = LaunchConfig::basic(version_id, &self.config.dot_minecraft, username)
            .with_memory(min_mem, max_mem)
            .with_window(width, height);

        cfg.java_executable = self.config.java_executable.clone();
        cfg.extra_jvm_args = self.config.extra_jvm_args.clone();

        if let Some(p) = profile {
            cfg.extra_jvm_args = p.extra_jvm_args.clone();
            if let Some(dir) = &p.custom_game_dir {
                cfg.dot_minecraft = dir.clone();
            }
        }
        cfg
    }

    /// 解析账户凭据：默认账户优先（微软账户尝试续期），否则离线账户
    async fn resolve_credentials(&self, username: &str) -> Result<AccountCredentials> {
        let store = AccountStore::new(AccountStore::default_path());
        if let Some(acc) = store.default_account().await? {
            match acc.account_type {
                AccountType::Microsoft => {
                    if let Some(refresh_token) = acc.refresh_token {
                        let auth = MicrosoftAuth::new();
                        if let Ok(result) = auth.refresh_login(&refresh_token).await {
                            log::info!("微软账户续期成功: {}", result.username);
                            return Ok(AccountCredentials {
                                uuid: result.uuid,
                                username: result.username,
                                access_token: result.access_token,
                                user_type: "msa".to_string(),
                                xuid: Some(result.xuid),
                                client_id: None,
                            });
                        }
                        log::warn!("微软账户续期失败，回退离线启动");
                    }
                }
                _ => {
                    return Ok(AccountCredentials {
                        uuid: acc.uuid,
                        username: acc.username,
                        access_token: acc.access_token.unwrap_or_default(),
                        user_type: "mojang".to_string(),
                        xuid: None,
                        client_id: None,
                    });
                }
            }
        }
        OfflineAccount::new(username).map(|a| a.credentials())
    }

    /// 构造启动引擎（配置指定 Java 时精确检测，否则全局检测）
    fn build_launch_engine(&self, engine: DownloadEngine) -> Result<LaunchEngine> {
        if let Some(java) = &self.config.java_executable {
            if java.exists() {
                let runtime = JavaDetector::detect_at(java)?;
                return Ok(LaunchEngine::with_java(engine, runtime));
            }
            log::warn!("配置的 Java 路径不存在: {}，回退自动检测", java.display());
        }
        LaunchEngine::new(engine)
    }

    /// 解析启动所需全部部件（确保安装→库/jar 就绪→配置→账户→引擎）
    ///
    /// 供 `launch` 与 `dry_run_command` 复用，避免重复下载/校验。
    async fn resolve_launch_parts(
        &self,
        version_id: &str,
        username: &str,
        profile: Option<&LaunchProfile>,
        tracker: &Arc<ProgressTracker>,
    ) -> Result<(LaunchConfig, VersionDetails, Vec<String>, LaunchEngine)> {
        self.ensure_installed(version_id).await?;

        let engine = DownloadEngine::new(&self.config.dot_minecraft);
        let installer = VersionInstaller::new(engine.clone());
        let details = installer.read_installed_version(version_id).await?;

        // 确保库文件与客户端 jar 就绪
        tracker.set_status(ProgressStatus::Downloading);
        tracker.set_stage("校验库文件");
        let libs = engine
            .library_downloader()
            .concurrency(self.config.download_concurrency.max(1))
            .natives_dir(engine.natives_dir(version_id))
            .with_tracker(tracker.clone())
            .download_libraries(&details.libraries)
            .await?;

        // 客户端 jar 缺失时补下载
        let client_jar = engine.client_jar_path(version_id);
        if !client_jar.exists() {
            log::info!("客户端 jar 缺失，自动补下载");
            let install = InstallService::new(self.config.clone());
            install.ensure_client_jar(&details).await?;
        }

        // 日志配置文件缺失时补下载（供 ${path} 替换）
        let install = InstallService::new(self.config.clone());
        install.ensure_logging_config(&details).await?;

        // 构建启动配置与账户
        let mut cfg = self.build_launch_config(version_id, username, profile);
        let cred = self.resolve_credentials(username).await?;
        cfg.uuid = cred.uuid;
        cfg.access_token = Some(cred.access_token);
        cfg.user_type = cred.user_type;
        cfg.xuid = cred.xuid;

        // 校验
        VersionValidator::validate_launch_config(&cfg)?;

        // 启动引擎
        let launcher = self.build_launch_engine(engine)?;
        Ok((cfg, details, libs, launcher))
    }

    /// 生成启动命令行（dry-run 用，不启动游戏）
    pub async fn dry_run_command(
        &self,
        version_id: &str,
        username: &str,
        profile: Option<&LaunchProfile>,
        progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
    ) -> Result<String> {
        let tracker = Arc::new(ProgressTracker::new());
        tracker.on_progress(progress_cb);
        let (cfg, details, libs, launcher) = self
            .resolve_launch_parts(version_id, username, profile, &tracker)
            .await?;
        launcher.print_command(&cfg, &details, &libs)
    }

    /// 启动游戏并等待退出
    ///
    /// - `dry_run`：仅输出启动命令，不实际启动
    /// - `progress_cb`：下载/校验进度回调
    /// - `line_cb`：游戏进程输出行回调 `(行内容, 是否 stderr)`
    /// - `on_launched`：进程启动成功回调 `(PID)`，供 GUI 记录句柄以便停止
    #[allow(clippy::too_many_arguments)] // 服务层聚合入口，参数直接来自调用方语义
    pub async fn launch<F, L, O>(
        &self,
        version_id: &str,
        username: &str,
        profile: Option<&LaunchProfile>,
        dry_run: bool,
        progress_cb: F,
        line_cb: Option<L>,
        on_launched: Option<O>,
    ) -> Result<LaunchOutcome>
    where
        F: Fn(&ProgressInfo) + Send + Sync + 'static,
        L: Fn(&str, bool) + Send + Sync + 'static,
        O: Fn(u32) + Send + Sync + 'static,
    {
        let tracker = Arc::new(ProgressTracker::new());
        tracker.on_progress(progress_cb);
        let (cfg, details, libs, launcher) = self
            .resolve_launch_parts(version_id, username, profile, &tracker)
            .await?;

        let mut ctx = PluginContext::new(&self.config.dot_minecraft)
            .with_status(crate::plugin::LauncherStatus::Launching);
        ctx.version_id = Some(version_id.to_string());
        ctx.username = Some(username.to_string());
        self.fire(PluginStage::PreLaunch, &mut ctx);

        // dry-run：仅输出命令行
        if dry_run {
            let cmd = launcher.print_command(&cfg, &details, &libs)?;
            log::info!("启动命令: {}", cmd);
            return Ok(LaunchOutcome {
                exit_code: None,
                crashed: false,
                crash_info: None,
            });
        }

        let mut child = launcher.launch(&cfg, &details, &libs)?;
        ctx.game_pid = Some(child.id());
        if let Some(cb) = &on_launched {
            cb(child.id());
        }
        log::info!("游戏进程已启动, PID: {}", child.id());
        self.fire(PluginStage::PostLaunch, &mut ctx);

        // 输出流转发 + 等待退出（阻塞操作放到线程池）
        let stderr_collect: Arc<std::sync::Mutex<Vec<u8>>> =
            Arc::new(std::sync::Mutex::new(Vec::new()));
        let stderr_collect_cb = stderr_collect.clone();

        let line_cb_stdout = line_cb;
        let stdout_task = child.stdout.take().map(|stdout| {
            tokio::spawn(async move {
                let reader = std::io::BufReader::new(stdout);
                for line in reader.lines().map_while(|r| r.ok()) {
                    log::info!("[游戏] {}", line);
                    if let Some(cb) = &line_cb_stdout {
                        cb(&line, false);
                    }
                }
            })
        });

        let stderr = child.stderr.take();
        let stderr_task = tokio::spawn(async move {
            if let Some(stderr) = stderr {
                let reader = std::io::BufReader::new(stderr);
                for line in reader.lines().map_while(|r| r.ok()) {
                    log::error!("[游戏] {}", line);
                    let mut buf = stderr_collect_cb.lock().unwrap();
                    if buf.len() < 2048 {
                        buf.extend_from_slice(line.as_bytes());
                        buf.push(b'\n');
                    }
                }
            }
        });

        let status = tokio::task::spawn_blocking(move || child.wait()).await??;
        if let Some(t) = stdout_task {
            let _ = t.await;
        }
        let _ = stderr_task.await;

        // 判定结果
        let exit_code = status.code();
        let crashed = exit_code != Some(0);
        let last_stderr = String::from_utf8_lossy(&stderr_collect.lock().unwrap().clone())
            .trim()
            .to_string();

        let mut crash_info = None;
        if crashed {
            crash_info = Some(CrashInfo {
                exit_code: exit_code.unwrap_or(-1),
                version_id: version_id.to_string(),
                out_of_memory: last_stderr.contains("OutOfMemoryError")
                    || last_stderr.contains("unable to create native thread"),
                last_stderr,
            });
            ctx.crash_info = crash_info.clone();
            self.fire(PluginStage::GameCrashed, &mut ctx);
        } else {
            self.fire(PluginStage::OnGameExit, &mut ctx);
        }

        Ok(LaunchOutcome {
            exit_code,
            crashed,
            crash_info,
        })
    }
}
