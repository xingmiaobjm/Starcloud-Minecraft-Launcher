use std::path::PathBuf;
use std::process::{Child, Command, Stdio};
use crate::error::{LauncherError, Result};
use crate::launch::java::JavaRuntime;
use crate::launch::arguments::ArgumentsBuilder;
use crate::version::manifest::VersionDetails;
use crate::download::engine::DownloadEngine;

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
    /// 窗口宽高
    pub window_width: u32,
    pub window_height: u32,
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
            window_width: 854,
            window_height: 480,
            access_token: None,
            client_token: None,
            xuid: None,
            user_type: "mojang".to_string(),
            extra_jvm_args: Vec::new(),
            extra_game_args: Vec::new(),
            java_executable: None,
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

    /// 设置窗口大小
    pub fn with_window(mut self, width: u32, height: u32) -> Self {
        self.window_width = width;
        self.window_height = height;
        self
    }
}

/// 启动引擎 - 负责组装参数并启动 Minecraft 进程
pub struct LaunchEngine {
    /// 下载引擎（提供目录管理）
    download_engine: DownloadEngine,
    /// Java 运行时
    java_runtime: JavaRuntime,
}

impl LaunchEngine {
    /// 创建启动引擎（自动检测 Java）
    pub fn new(download_engine: DownloadEngine) -> Result<Self> {
        let detector = crate::launch::java::JavaDetector::new();
        let java_runtime = detector.detect()?;
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

        // 构建替换变量表
        let assets_id = version.assets.clone().unwrap_or_else(|| config.version_id.clone());

        // 先计算 classpath
        let classpath = library_paths
            .iter()
            .map(|s| s.as_str())
            .chain(std::iter::once(version.id.as_str()))
            .collect::<Vec<_>>()
            .join(if cfg!(target_os = "windows") { ";" } else { ":" });

        let builder = ArgumentsBuilder::new(version.clone(), os)
            .with_replacement("auth_player_name", &config.username)
            .with_replacement("version_name", &config.version_id)
            .with_replacement("game_directory", config.dot_minecraft.to_string_lossy())
            .with_replacement("assets_root", self.download_engine.assets_dir().to_string_lossy())
            .with_replacement("assets_index_name", &assets_id)
            .with_replacement("auth_uuid", &config.uuid)
            .with_replacement("auth_access_token", config.access_token.as_deref().unwrap_or("0"))
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
            .with_replacement("launcher_name", "furry-minecraft")
            .with_replacement("launcher_version", env!("CARGO_PKG_VERSION"))
            .with_replacement("classpath_separator", if cfg!(target_os = "windows") { ";" } else { ":" })
            .with_replacement(
                "library_directory",
                self.download_engine.libraries_dir().to_string_lossy(),
            )
            .with_replacement("natives_directory",
                &self.download_engine.natives_dir(&config.version_id).to_string_lossy().to_string(),
            )
            .with_replacement("classpath", &classpath);

        let java_exe = config
            .java_executable
            .clone()
            .unwrap_or_else(|| self.java_runtime.executable.clone());

        let mut cmd = Command::new(java_exe);

        // JVM 参数
        cmd.arg(format!("-Xmx{}M", config.max_memory_mb));
        cmd.arg(format!("-Xms{}M", config.min_memory_mb));

        for jvm_arg in builder.build_jvm_args() {
            cmd.arg(&jvm_arg);
        }

        // 额外 JVM 参数
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

        // 版本目录作为工作目录
        cmd.current_dir(
            self.download_engine
                .versions_dir()
                .join(&config.version_id),
        );

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
        let mut parts: Vec<String> = vec![
            cmd.get_program().to_string_lossy().to_string(),
        ];
        parts.extend(cmd.get_args().map(|a| a.to_string_lossy().to_string()));
        Ok(parts.join(" "))
    }
}
