use crate::download::engine::DownloadEngine;
use crate::error::{LauncherError, Result};
use crate::launch::arguments::ArgumentsBuilder;
use crate::launch::java::JavaRuntime;
use crate::version::manifest::VersionDetails;
use std::path::PathBuf;
use std::process::{Child, Command, Stdio};

/// Minecraft 启动配置
#[derive(Debug, Clone)]
pub struct LaunchConfig {
    /// 版本 ID
    pub version_id: String,
    /// .minecraft 目录
    pub dot_minecraft: PathBuf,
    /// 玩家用户名
    pub username: String,
    /// 玩家 UUID
    pub uuid: String,
    /// 最小/最大内存 (MB)
    pub min_memory_mb: u32,
    pub max_memory_mb: u32,
    /// 访问令牌 (正版登录时填写)
    pub access_token: Option<String>,
    /// 客户端令牌
    pub client_token: Option<String>,
    /// XUID (Xbox Live)
    pub xuid: Option<String>,
    /// 用户类型: "mojang" 或 "msa"
    pub user_type: String,
    /// 额外 JVM 参数
    pub extra_jvm_args: Vec<String>,
    /// 额外游戏参数
    pub extra_game_args: Vec<String>,
    /// Java 可执行文件路径（可选，留空自动检测）
    pub java_executable: Option<PathBuf>,
    /// Java 是否启用高性能显卡（Windows 图形偏好：独立显卡优先）
    pub use_high_performance_gpu: bool,
    /// 是否启用内置 JVM 优化参数（G1GC / 内存预触 / IPv4 等）
    pub use_builtin_jvm_args: bool,
}

impl LaunchConfig {
    /// 创建一个基础配置
    pub fn basic(
        version_id: impl Into<String>,
        dot_minecraft: impl Into<PathBuf>,
        username: impl Into<String>,
    ) -> Self {
        Self {
            version_id: version_id.into(),
            dot_minecraft: dot_minecraft.into(),
            username: username.into(),
            uuid: uuid::Uuid::new_v4().to_string(),
            min_memory_mb: 1024,
            max_memory_mb: 4096,
            access_token: None,
            client_token: None,
            xuid: None,
            user_type: "mojang".to_string(),
            extra_jvm_args: Vec::new(),
            extra_game_args: Vec::new(),
            java_executable: None,
            use_high_performance_gpu: false,
            use_builtin_jvm_args: true,
        }
    }

    /// 设置账户信息
    pub fn with_account(
        mut self,
        uuid: impl Into<String>,
        access_token: impl Into<String>,
        user_type: impl Into<String>,
    ) -> Self {
        self.uuid = uuid.into();
        self.access_token = Some(access_token.into());
        self.user_type = user_type.into();
        self
    }

    /// 设置内存
    pub fn with_memory(mut self, min_mb: u32, max_mb: u32) -> Self {
        self.min_memory_mb = min_mb;
        self.max_memory_mb = max_mb;
        self
    }
}

/// Windows：为 Java 进程写入「高性能 GPU 优先」系统图形偏好
///
/// 官方机制为 HKCU\Software\Microsoft\DirectX\UserGpuPreferences 下按
/// 可执行文件绝对路径写入 `GpuPreference=2;`（0 默认 / 1 节能 / 2 高性能），
/// 与 Windows 设置中「图形偏好」的行为一致。best-effort，失败仅告警。
#[cfg(target_os = "windows")]
fn apply_gpu_preference_windows(java_exe: &std::path::Path) {
    let status = std::process::Command::new("reg")
        .args([
            "add",
            r"HKCU\Software\Microsoft\DirectX\UserGpuPreferences",
            "/v",
            &java_exe.to_string_lossy(),
            "/t",
            "REG_SZ",
            "/d",
            "GpuPreference=2;",
            "/f",
        ])
        .status();
    match status {
        Ok(s) if s.success() => {
            log::info!("已为 Java 设置高性能显卡偏好: {}", java_exe.display());
        }
        Ok(s) => log::warn!(
            "设置 Java 高性能显卡偏好失败（退出码 {:?}）",
            s.code()
        ),
        Err(e) => log::warn!("设置 Java 高性能显卡偏好失败: {}", e),
    }
}

/// 内置 JVM 优化参数 — 解决 Minecraft 闪退/启动失败等常见问题
///
/// 参考 Mojang 官方启动器与社区最佳实践，按优先级排列：
/// 1. G1 垃圾回收器调优 — 减少 GC 暂停导致的闪退
/// 2. 网络栈优化 — 避免 IPv6 解析超时
/// 3. 内存预触 — 减少运行时 page fault
/// 4. 日志安全 — 修复 log4j2 漏洞
/// 5. 兼容性 — 忽略不识别的 VM 参数
pub fn get_builtin_jvm_args() -> Vec<String> {
    vec![
        // ── G1 GC 优化 ──
        "-XX:+UseG1GC".to_string(),
        "-XX:+ParallelRefProcEnabled".to_string(),
        "-XX:MaxGCPauseMillis=200".to_string(),
        "-XX:+UnlockExperimentalVMOptions".to_string(),
        "-XX:+DisableExplicitGC".to_string(),
        "-XX:+AlwaysPreTouch".to_string(),
        "-XX:G1NewSizePercent=30".to_string(),
        "-XX:G1MaxNewSizePercent=40".to_string(),
        "-XX:G1HeapRegionSize=8M".to_string(),
        "-XX:G1ReservePercent=20".to_string(),
        "-XX:G1HeapWastePercent=5".to_string(),
        "-XX:G1MixedGCCountTarget=4".to_string(),
        "-XX:InitiatingHeapOccupancyPercent=15".to_string(),
        "-XX:G1MixedGCLiveThresholdPercent=90".to_string(),
        "-XX:+UseStringDeduplication".to_string(),
        // ── 网络优化 ──
        "-Djava.net.preferIPv4Stack=true".to_string(),
        // ── 编码 ──
        "-Dfile.encoding=UTF-8".to_string(),
        // ── 日志安全 ──
        "-Dlog4j2.formatMsgNoLookups=true".to_string(),
        // ── 兼容性 ──
        "-XX:+IgnoreUnrecognizedVMOptions".to_string(),
        "-XX:-OmitStackTraceInFastThrow".to_string(),
    ]
}

/// 启动引擎 - 负责组装参数并启动 Minecraft 进程
pub struct LaunchEngine {
    /// 下载引擎（提供目录管理）
    download_engine: DownloadEngine,
    /// Java 运行时
    java_runtime: JavaRuntime,
}

impl LaunchEngine {
    /// 创建启动引擎（自动检测 Java，命中缓存直接复用）
    pub fn new(download_engine: DownloadEngine) -> Result<Self> {
        let detector = crate::launch::java::JavaDetector::new();
        let java_runtime = detector.detect_or_cached()?;
        Ok(Self {
            download_engine,
            java_runtime,
        })
    }

    /// 使用指定 Java 运行时创建启动引擎
    pub fn with_java(download_engine: DownloadEngine, java_runtime: JavaRuntime) -> Self {
        Self {
            download_engine,
            java_runtime,
        }
    }

    /// 获取下载引擎
    pub fn download_engine(&self) -> &DownloadEngine {
        &self.download_engine
    }

    /// 获取 Java 运行时
    pub fn java_runtime(&self) -> &JavaRuntime {
        &self.java_runtime
    }

    /// 构建启动进程（返回 Command，由调用者决定 spawn 或输出命令行）
    pub fn build_process(
        &self,
        config: &LaunchConfig,
        version: &VersionDetails,
        library_paths: &[String],
    ) -> Result<Command> {
        let os = crate::launch::arguments::OsKind::detect();

        // 客户端 jar 绝对路径（classpath 末尾），并校验存在
        let client_jar = self.download_engine.client_jar_path(&config.version_id);
        if !client_jar.exists() {
            return Err(LauncherError::LaunchError(format!(
                "客户端 jar 不存在: {}，请先安装该版本",
                client_jar.display()
            )));
        }

        // 构建替换变量表
        let assets_id = version
            .assets
            .clone()
            .unwrap_or_else(|| config.version_id.clone());

        // 计算 classpath：库文件 + 客户端 jar 绝对路径
        let separator = if cfg!(target_os = "windows") {
            ";"
        } else {
            ":"
        };
        let mut classpath_parts: Vec<String> = library_paths.to_vec();
        classpath_parts.push(client_jar.to_string_lossy().to_string());
        let classpath = classpath_parts.join(separator);

        // 游戏目录（.minecraft，支持自定义游戏目录）
        let game_dir = config.dot_minecraft.to_string_lossy().to_string();

        let builder = ArgumentsBuilder::new(version.clone(), os)
            .with_replacement("auth_player_name", &config.username)
            .with_replacement("version_name", &config.version_id)
            .with_replacement("game_directory", &game_dir)
            .with_replacement(
                "assets_root",
                self.download_engine.assets_dir().to_string_lossy(),
            )
            .with_replacement("assets_index_name", &assets_id)
            .with_replacement("auth_uuid", &config.uuid)
            .with_replacement(
                "auth_access_token",
                config.access_token.as_deref().unwrap_or("0"),
            )
            .with_replacement("clientid", config.client_token.as_deref().unwrap_or("0"))
            .with_replacement("auth_xuid", config.xuid.as_deref().unwrap_or("0"))
            .with_replacement("user_type", &config.user_type)
            .with_replacement(
                "version_type",
                match version.version_type {
                    crate::version::manifest::VersionType::Release => "release",
                    _ => "snapshot",
                },
            )
            .with_replacement("launcher_name", "starcloud-minecraft")
            .with_replacement("launcher_version", env!("CARGO_PKG_VERSION"))
            .with_replacement(
                "classpath_separator",
                if cfg!(target_os = "windows") {
                    ";"
                } else {
                    ":"
                },
            )
            .with_replacement(
                "library_directory",
                self.download_engine.libraries_dir().to_string_lossy(),
            )
            .with_replacement(
                "natives_directory",
                self.download_engine
                    .natives_dir(&config.version_id)
                    .to_string_lossy()
                    .to_string(),
            )
            .with_replacement("classpath", &classpath)
            .with_replacement(
                "path",
                self.download_engine
                    .logging_config_path(&config.version_id)
                    .to_string_lossy()
                    .to_string(),
            );

        let java_exe = config
            .java_executable
            .clone()
            .unwrap_or_else(|| self.java_runtime.executable.clone());

        // 高性能显卡偏好（Windows 图形偏好，须在进程启动前写入）
        if config.use_high_performance_gpu {
            #[cfg(target_os = "windows")]
            apply_gpu_preference_windows(&java_exe);
        }

        let mut cmd = Command::new(java_exe);

        // JVM 参数
        cmd.arg(format!("-Xmx{}M", config.max_memory_mb));
        cmd.arg(format!("-Xms{}M", config.min_memory_mb));

        for jvm_arg in builder.build_jvm_args() {
            cmd.arg(&jvm_arg);
        }

        // 内置 JVM 优化参数（G1GC / 内存预触 / IPv4 / 日志安全等）
        if config.use_builtin_jvm_args {
            for arg in get_builtin_jvm_args() {
                cmd.arg(&arg);
            }
        }

        // 额外 JVM 参数（用户自定义 / 版本级覆盖）
        for arg in &config.extra_jvm_args {
            cmd.arg(arg);
        }

        // 主类
        cmd.arg(builder.main_class());

        // 游戏参数
        for game_arg in builder.build_game_args() {
            cmd.arg(&game_arg);
        }

        // 额外游戏参数
        for arg in &config.extra_game_args {
            cmd.arg(arg);
        }

        // 工作目录：游戏目录（.minecraft），与 game_directory 参数一致
        cmd.current_dir(&config.dot_minecraft);

        // 输出到标准输出（方便日志捕获）
        cmd.stdout(Stdio::piped());
        cmd.stderr(Stdio::piped());

        Ok(cmd)
    }

    /// 启动 Minecraft 并返回进程句柄
    pub fn launch(
        &self,
        config: &LaunchConfig,
        version: &VersionDetails,
        library_paths: &[String],
    ) -> Result<Child> {
        let mut cmd = self.build_process(config, version, library_paths)?;

        log::info!("启动 Minecraft {} ...", config.version_id);

        let child = cmd
            .spawn()
            .map_err(|e| LauncherError::LaunchError(format!("启动进程失败: {}", e)))?;

        Ok(child)
    }

    /// 输出启动命令行（调试用）
    pub fn print_command(
        &self,
        config: &LaunchConfig,
        version: &VersionDetails,
        library_paths: &[String],
    ) -> Result<String> {
        let cmd = self.build_process(config, version, library_paths)?;
        let mut parts: Vec<String> = vec![cmd.get_program().to_string_lossy().to_string()];
        parts.extend(cmd.get_args().map(|a| a.to_string_lossy().to_string()));
        Ok(parts.join(" "))
    }
}
