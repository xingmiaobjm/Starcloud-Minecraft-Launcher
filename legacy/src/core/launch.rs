//! 启动服务 - 游戏启动的高层封装

use crate::account::{
    AccountCredentials, AccountStore, AccountType, MicrosoftAuth, OfflineAccount,
};
use crate::config::{LaunchProfile, LauncherConfig};
use crate::core::install::InstallService;
use crate::download::engine::DownloadEngine;
use crate::download::progress::{ProgressInfo, ProgressStatus, ProgressTracker};
use crate::error::Result;
use crate::launch::java::{cache_java, pick_for, JavaDetector};
use crate::launch::{LaunchConfig, LaunchEngine};
use crate::plugin::{CrashInfo, PluginContext, PluginManager, PluginStage};
use crate::version::manifest::VersionDetails;
use crate::version_manager::{VersionInstaller, VersionValidator};
use std::io::BufRead;
use std::path::Path;
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

/// 启动前自检结果项
#[derive(Debug, Clone, serde::Serialize)]
pub struct PreflightIssue {
    /// "error"（阻断启动）| "warning"（提示不阻断）| "info"
    pub level: String,
    pub title: String,
    pub message: String,
}

impl PreflightIssue {
    fn error(title: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            level: "error".into(),
            title: title.into(),
            message: message.into(),
        }
    }

    fn warning(title: impl Into<String>, message: impl Into<String>) -> Self {
        Self {
            level: "warning".into(),
            title: title.into(),
            message: message.into(),
        }
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
        let (min_mem, max_mem) = profile
            .map(|p| (p.min_memory_mb, p.max_memory_mb))
            .unwrap_or((
                self.config.default_min_memory_mb,
                self.config.default_max_memory_mb,
            ));

        // 版本隔离时使用独立游戏目录（资源/库仍取自 .minecraft）
        let game_dir = self
            .config
            .runtime_game_dir
            .as_ref()
            .unwrap_or(&self.config.dot_minecraft);

        let mut cfg = LaunchConfig::basic(version_id, game_dir.clone(), username)
            .with_memory(min_mem, max_mem);

        cfg.java_executable = self.config.java_executable.clone();
        cfg.extra_jvm_args = self.config.extra_jvm_args.clone();
        cfg.extra_game_args = self.config.extra_game_args.clone();
        cfg.use_high_performance_gpu = self.config.use_high_performance_gpu;
        cfg.use_builtin_jvm_args = self.config.use_builtin_jvm_args;

        if let Some(p) = profile {
            cfg.extra_jvm_args = p.extra_jvm_args.clone();
            if let Some(dir) = &p.custom_game_dir {
                cfg.dot_minecraft = dir.clone();
            }
        }

        // 游戏语言与启动器语言同步：按配置语言注入 JVM 本地化参数（置于最后，避免被 profile 覆盖）
        let (lang_tag, country) = if self.config.language.starts_with("en") {
            ("en_US", "US")
        } else {
            ("zh_CN", "CN")
        };
        cfg.extra_jvm_args.push(format!("-Duser.language={}", lang_tag));
        cfg.extra_jvm_args.push(format!("-Duser.country={}", country));
        cfg
    }

    /// 将游戏语言写入 options.txt 的 lang 键（游戏语言与启动器语言同步）
    ///
    /// 语言映射：zh* → zh_cn；en* → en_us；其他 → 语言码小写转下划线。
    /// 仅首次启动或启动器语言变更后调用（由命令层判断 `synced_language`）。
    pub async fn apply_game_language(dot_minecraft: &Path, language: &str) -> crate::error::Result<()> {
        use tokio::fs;
        let lang_key = if language.starts_with("zh") {
            "zh_cn".to_string()
        } else if language.starts_with("en") {
            "en_us".to_string()
        } else {
            language.to_lowercase().replace('-', "_")
        };
        let path = dot_minecraft.join("options.txt");
        let mut lines: Vec<String> = if path.exists() {
            fs::read_to_string(&path)
                .await?
                .lines()
                .map(|s| s.to_string())
                .collect()
        } else {
            Vec::new()
        };
        let target = format!("lang:{}", lang_key);
        let mut replaced = false;
        for line in lines.iter_mut() {
            if line.starts_with("lang:") {
                if line != &target {
                    *line = target.clone();
                }
                replaced = true;
                break;
            }
        }
        if !replaced {
            lines.push(target);
        }
        fs::create_dir_all(dot_minecraft).await?;
        fs::write(&path, lines.join("\n") + "\n").await?;
        log::info!("已设置游戏语言 {} (lang={}, {})", language, lang_key, path.display());
        Ok(())
    }

    /// 启动前自检：Java / 磁盘空间 / 内存设置 / 版本安装状态
    ///
    /// 返回结果列表；`level == "error"` 表示应阻断启动。
    pub async fn preflight(&self, version_id: &str) -> Vec<PreflightIssue> {
        let mut issues = Vec::new();

        // 1. Java 可用性（按版本要求的 Java 版本检查）
        let required_java = VersionInstaller::new(DownloadEngine::new(&self.config.dot_minecraft))
            .read_installed_version(version_id)
            .await
            .ok()
            .and_then(|d| d.java_version.map(|j| j.major_version))
            .unwrap_or(8);
        let mut java_named = false;
        if let Some(java) = &self.config.java_executable {
            java_named = true;
            if !java.exists() {
                issues.push(PreflightIssue::error(
                    "Java 路径无效",
                    format!("配置的 Java 不存在: {}", java.display()),
                ));
            } else if JavaDetector::detect_at(java).is_err() {
                issues.push(PreflightIssue::error(
                    "Java 校验失败",
                    format!("无法识别 Java: {}", java.display()),
                ));
            }
        }
        if !java_named
            && JavaDetector::new()
                .with_min_version(required_java)
                .detect_or_cached()
                .is_err()
        {
            issues.push(PreflightIssue::warning(
                "未找到满足要求的 Java",
                format!(
                    "需要 Java {} 及以上；启动时将自动下载内置 Java（Adoptium JRE），或可在设置中手动指定 Java 路径",
                    required_java
                ),
            ));
        }

        // 2. 磁盘空间（.minecraft 所在盘）
        let target = self
            .config
            .dot_minecraft
            .to_string_lossy()
            .to_lowercase();
        let mut free_bytes: Option<u64> = None;
        let disks = sysinfo::Disks::new_with_refreshed_list();
        for disk in &disks {
            let mount = disk.mount_point().to_string_lossy().to_lowercase();
            if target.starts_with(&mount) {
                free_bytes = Some(disk.available_space());
                break;
            }
        }
        if let Some(free) = free_bytes {
            let free_mb = free / 1024 / 1024;
            if free_mb < 512 {
                issues.push(PreflightIssue::error(
                    "磁盘空间不足",
                    format!(".minecraft 所在磁盘仅剩 {} MB", free_mb),
                ));
            } else if free_mb < 2048 {
                issues.push(PreflightIssue::warning(
                    "磁盘空间紧张",
                    format!(".minecraft 所在磁盘仅剩 {} MB", free_mb),
                ));
            }
        }

        // 3. 内存设置合理性
        let mut sys = sysinfo::System::new_all();
        sys.refresh_memory();
        let total_mb = sys.total_memory() / 1024 / 1024;
        let max_mb = u64::from(self.config.default_max_memory_mb);
        if total_mb > 0 && max_mb > total_mb {
            issues.push(PreflightIssue::error(
                "内存设置过高",
                format!("最大内存 {} MB 超过物理内存 {} MB", max_mb, total_mb),
            ));
        } else if total_mb > 0 && max_mb > total_mb * 3 / 4 {
            issues.push(PreflightIssue::warning(
                "内存设置偏高",
                format!("最大内存 {} MB 超过物理内存的 75%（共 {} MB）", max_mb, total_mb),
            ));
        }

        // 4. 版本安装状态
        let installer = VersionInstaller::new(DownloadEngine::new(&self.config.dot_minecraft));
        if !installer.is_installed(version_id) {
            issues.push(PreflightIssue::warning(
                "版本未安装",
                "启动时将自动下载并安装该版本",
            ));
        }

        issues
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

    /// 构造启动引擎（多 Java 智能选择）
    ///
    /// 选择顺序：
    /// 1. 显式配置的 Java（全局/版本级覆盖，已并入 self.config）
    /// 2. 启动器已下载 + 系统检测中「版本最接近需求」的运行时（pick_for）
    /// 3. 都没有 → 自动下载需求版本（Adoptium；个别版本无 JRE 时按推荐 LTS 兜底）
    async fn build_launch_engine(&self, engine: DownloadEngine, required_java: u32) -> Result<LaunchEngine> {
        // 1. 显式配置优先
        if let Some(java) = &self.config.java_executable {
            if java.exists() {
                let runtime = JavaDetector::detect_at(java)?;
                log::info!(
                    "启动使用配置的 Java {}（{}）",
                    runtime.major_version,
                    runtime.executable.display()
                );
                return Ok(LaunchEngine::with_java(engine, runtime));
            }
            log::warn!("配置的 Java 路径不存在: {}，回退智能选择", java.display());
        }

        // 2. 多 Java 匹配：已下载 + 系统检测（去重），选最接近需求版本
        let downloader = crate::launch::java_download::JavaDownloader::new()?;
        let root = crate::launch::java_download::java_download_root();
        let mut candidates = downloader.list_downloaded_runtimes();
        candidates.extend(
            JavaDetector::new()
                .detect_all()
                .into_iter()
                .filter(|r| !r.executable.starts_with(&root)),
        );
        if let Some(runtime) = pick_for(&candidates, required_java) {
            cache_java(&runtime);
            log::info!(
                "多 Java 匹配: 用 Java {}（{}）满足要求 >= {}",
                runtime.major_version,
                runtime.executable.display(),
                required_java
            );
            return Ok(LaunchEngine::with_java(engine, runtime));
        }

        // 3. 自动下载需求版本（个别版本无独立 JRE 包时回退 LTS 推荐）
        log::info!(
            "未找到满足要求（>= {}）的 Java，开始自动下载",
            required_java
        );
        let runtime = match downloader.download_java(required_java).await {
            Ok(rt) => rt,
            Err(e) => {
                log::warn!("下载 Java {} 失败（{}），按 LTS 推荐兜底", required_java, e);
                downloader.auto_download(required_java).await?
            }
        };
        cache_java(&runtime);
        log::info!(
            "自动下载使用 Java {}（{}）",
            runtime.major_version,
            runtime.executable.display()
        );
        Ok(LaunchEngine::with_java(engine, runtime))
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
            .natives_dir(engine.version_natives_dir(&details))
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

        // 启动引擎（按版本要求的 Java 智能匹配；version.json 无声明时默认 Java 8）
        let required_java = details
            .java_version
            .as_ref()
            .map(|j| j.major_version)
            .unwrap_or(8);
        let launcher = self.build_launch_engine(engine, required_java).await?;
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
