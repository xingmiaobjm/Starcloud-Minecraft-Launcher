use crate::error::{LauncherError, Result};
use std::path::{Path, PathBuf};
use tokio::fs;

/// 启动器全局配置 - 可序列化到 TOML 文件
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct LauncherConfig {
    /// 配置版本（用于迁移）
    pub config_version: u32,
    /// .minecraft 目录
    pub dot_minecraft: PathBuf,
    /// 语言
    pub language: String,
    /// 默认内存分配 (MB)
    pub default_min_memory_mb: u32,
    pub default_max_memory_mb: u32,
    /// Java 可执行文件（手动指定）
    pub java_executable: Option<PathBuf>,
    /// 额外 JVM 参数
    pub extra_jvm_args: Vec<String>,
    /// 游戏优化启动参数（追加到游戏主类参数之后）
    #[serde(default)]
    pub extra_game_args: Vec<String>,
    /// Java 是否启用高性能显卡（Windows 图形偏好：独立显卡优先）
    #[serde(default)]
    pub use_high_performance_gpu: bool,
    /// 下载并发数
    pub download_concurrency: usize,
    /// 下载重试次数
    pub download_max_retries: u32,
    /// 最大缓存条目数
    pub max_cache_entries: usize,
    /// 是否启用自动更新
    pub auto_update: bool,
    /// 登录方式 "offline" / "mojang" / "microsoft"
    pub login_method: String,
    /// 上次使用的用户名
    pub last_username: Option<String>,
    /// 上次启动的版本 ID（首页默认选中）
    #[serde(default)]
    pub last_version: Option<String>,
    /// 强制游戏语言为中文（写 options.txt + 注入 JVM 参数；由语言同步逻辑派生）
    #[serde(default = "default_true")]
    pub force_chinese: bool,
    /// 全局版本隔离：每个版本使用独立游戏目录 versions/<id>/game（默认开启）
    #[serde(default = "default_true")]
    pub version_isolated: bool,
    /// 已同步到游戏的语言（仅当与 language 不同时才重新应用游戏语言）
    #[serde(default)]
    pub synced_language: Option<String>,
    /// 开发者模式（开启后可调整最小内存等高级参数）
    #[serde(default)]
    pub dev_mode: bool,
    /// 动画倍率：0 表示关闭动画；0.1-2.0 为播放速度倍率（1.0 正常）
    #[serde(default = "default_anim_scale")]
    pub animation_scale: f64,
    /// 游戏运行时是否在右下角显示日志按钮（点击查看启动器日志）
    #[serde(default = "default_true")]
    pub show_log_button: bool,
    /// 已安装的插件目录
    pub plugins_dir: PathBuf,
    /// 陶瓦联机（Terracotta）核心安装目录
    #[serde(default = "default_terracotta_dir")]
    pub terracotta_dir: PathBuf,
    /// 是否启用实验性功能
    pub experimental: bool,
    /// 下载源选择（"auto" 自动测速 / "official" / "bmclapi" / "fastmirror" / "rainyun" / "custom"）
    #[serde(default = "default_download_source")]
    pub download_source: String,
    /// 自定义镜像根 URL（download_source == "custom" 时生效）
    #[serde(default)]
    pub custom_mirror_url: Option<String>,
    /// 版本清单本地缓存：开启后本地缓冲版本列表，每次只拉取新版本加入；关闭即「联机模式」始终从网络拉取
    #[serde(default = "default_true")]
    pub version_cache_enabled: bool,
    /// 是否启用内置 JVM 优化参数（G1GC / 内存预触 / IPv4 等），默认开启
    #[serde(default = "default_true")]
    pub use_builtin_jvm_args: bool,
    /// 运行时游戏目录覆盖（版本隔离用，仅本次启动生效，不持久化）
    #[serde(skip)]
    pub runtime_game_dir: Option<PathBuf>,
}

/// serde 默认值辅助：布尔默认 true
fn default_true() -> bool {
    true
}

/// serde 默认值辅助：动画倍率默认 1.0
fn default_anim_scale() -> f64 {
    1.0
}

/// serde 默认值辅助：下载源默认 "auto"
fn default_download_source() -> String {
    "auto".to_string()
}

/// serde 默认值辅助：陶瓦联机核心目录（数据目录/starcloud-launcher/terracotta）
fn default_terracotta_dir() -> PathBuf {
    dirs::data_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("starcloud-launcher")
        .join("terracotta")
}

impl Default for LauncherConfig {
    fn default() -> Self {
        Self {
            config_version: 10,
            dot_minecraft: dirs::data_dir()
                .unwrap_or_else(|| PathBuf::from("."))
                .join(".minecraft"),
            language: "zh_CN".into(),
            default_min_memory_mb: 128,
            default_max_memory_mb: 4096,
            java_executable: None,
            extra_jvm_args: Vec::new(),
            extra_game_args: Vec::new(),
            use_high_performance_gpu: false,
            download_concurrency: 16,
            download_max_retries: 3,
            max_cache_entries: 128,
            auto_update: false,
            login_method: "offline".into(),
            last_username: None,
            last_version: None,
            force_chinese: true,
            version_isolated: true,
            synced_language: None,
            dev_mode: false,
            animation_scale: 1.0,
            show_log_button: true,
            plugins_dir: dirs::data_dir()
                .unwrap_or_else(|| PathBuf::from("."))
                .join("starcloud-launcher")
                .join("plugins"),
            terracotta_dir: default_terracotta_dir(),
            experimental: false,
            download_source: default_download_source(),
            custom_mirror_url: None,
            version_cache_enabled: true,
            use_builtin_jvm_args: true,
            runtime_game_dir: None,
        }
    }
}

impl LauncherConfig {
    /// 从文件加载配置
    pub async fn load(path: impl Into<PathBuf>) -> Result<Self> {
        let path = path.into();
        if !path.exists() {
            return Ok(Self::default());
        }
        let data = fs::read_to_string(&path).await?;
        let config: Self = toml::from_str(&data)
            .map_err(|e| LauncherError::ConfigError(format!("配置解析失败: {}", e)))?;
        Ok(config)
    }

    /// 保存配置到文件
    pub async fn save(&self, path: impl Into<PathBuf>) -> Result<()> {
        let path = path.into();
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).await?;
        }
        let data = toml::to_string_pretty(self)
            .map_err(|e| LauncherError::ConfigError(format!("配置序列化失败: {}", e)))?;
        fs::write(&path, data).await?;
        log::info!("配置已保存到 {}", path.display());
        Ok(())
    }

    /// 重置为默认配置
    pub fn reset(&mut self) {
        *self = Self::default();
    }

    /// 迁移旧版本配置
    ///
    /// - v0 → v1：补齐下载并发/重试/缓存默认值
    /// - v1 → v2：补齐内存/窗口/下载字段安全默认（零值回退）
    /// - v2 → v3：补齐游戏语言强制中文等新字段默认
    /// - v3 → v4：补齐陶瓦联机核心目录默认
    /// - v4 → v5：补齐高性能显卡 / 游戏优化启动参数字段默认（游戏窗口已移除）
    /// - v5 → v6：补齐全局版本隔离（默认开启）等新字段默认
    /// - v6 → v7：补齐下载源选择 / 自定义镜像 URL 字段默认
    /// - v7 → v8：补齐版本清单本地缓存开关字段默认
    /// - v8 → v9：补齐内置 JVM 优化参数开关字段默认
    pub fn migrate(&mut self) -> Result<()> {
        if self.config_version < 1 {
            // 从 v0 → v1 迁移
            self.download_concurrency = 16;
            self.download_max_retries = 3;
            self.max_cache_entries = 128;
        }
        if self.config_version < 2 {
            // 从 v1 → v2 迁移：新增字段/旧配置缺失时补齐安全默认值
            if self.download_concurrency == 0 {
                self.download_concurrency = 16;
            }
            if self.download_max_retries == 0 {
                self.download_max_retries = 3;
            }
            if self.max_cache_entries == 0 {
                self.max_cache_entries = 128;
            }
            if self.default_min_memory_mb == 0 {
                self.default_min_memory_mb = 1024;
            }
            if self.default_max_memory_mb == 0 {
                self.default_max_memory_mb = 4096;
            }
            self.config_version = 2;
        }
        if self.config_version < 3 {
            // 从 v2 → v3 迁移：serde default 已补齐缺失字段，这里仅更新版本号
            self.config_version = 3;
        }
        if self.config_version < 4 {
            // 从 v3 → v4 迁移：serde default 已补齐 terracotta_dir，这里仅更新版本号
            self.config_version = 4;
        }
        if self.config_version < 5 {
            // 从 v4 → v5 迁移：serde default 已补齐新字段，这里仅更新版本号
            self.config_version = 5;
        }
        if self.config_version < 6 {
            // 从 v5 → v6 迁移：serde default 已补齐 version_isolated（默认开启），这里仅更新版本号
            self.config_version = 6;
        }
        if self.config_version < 7 {
            // 从 v6 → v7 迁移：serde default 已补齐 download_source / custom_mirror_url，这里仅更新版本号
            self.config_version = 7;
        }
        if self.config_version < 8 {
            // 从 v7 → v8 迁移：serde default 已补齐 version_cache_enabled，这里仅更新版本号
            self.config_version = 8;
        }
        if self.config_version < 9 {
            // 从 v8 → v9 迁移：serde default 已补齐 use_builtin_jvm_args，这里仅更新版本号
            self.config_version = 9;
        }
        if self.config_version < 10 {
            // 从 v9 → v10 迁移：下载源 mcslapi/mcslsync 已移除，回退为 auto
            if self.download_source == "mcslapi" || self.download_source == "mcslsync" {
                self.download_source = "auto".to_string();
            }
            self.config_version = 10;
        }
        Ok(())
    }
}

/// 启动配置集（可保存多套启动配置）
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct LaunchProfile {
    /// 配置名称
    pub name: String,
    /// 版本 ID
    pub version_id: String,
    /// 用户名
    pub username: String,
    /// UUID
    pub uuid: String,
    /// 内存分配
    pub min_memory_mb: u32,
    pub max_memory_mb: u32,
    /// 额外 JVM 参数
    pub extra_jvm_args: Vec<String>,
    /// 是否启用模组目录隔离
    pub isolated_mods: bool,
    /// 自定义工作目录
    pub custom_game_dir: Option<PathBuf>,
}

impl LaunchProfile {
    /// 创建默认配置
    pub fn new(
        name: impl Into<String>,
        version_id: impl Into<String>,
        username: impl Into<String>,
    ) -> Self {
        Self {
            name: name.into(),
            version_id: version_id.into(),
            username: username.into(),
            uuid: uuid::Uuid::new_v4().to_string(),
            min_memory_mb: 2048,
            max_memory_mb: 4096,
            extra_jvm_args: Vec::new(),
            isolated_mods: false,
            custom_game_dir: None,
        }
    }

    /// 转换为 LaunchConfig
    pub fn to_launch_config(&self, dot_minecraft: &Path) -> crate::launch::LaunchConfig {
        crate::launch::LaunchConfig::basic(
            &self.version_id,
            self.custom_game_dir
                .clone()
                .unwrap_or_else(|| dot_minecraft.to_path_buf()),
            &self.username,
        )
        .with_memory(self.min_memory_mb, self.max_memory_mb)
    }
}

/// 启动配置管理器
pub struct ProfileManager {
    profiles: Vec<LaunchProfile>,
    config_dir: PathBuf,
}

impl ProfileManager {
    /// 创建配置管理器
    pub fn new(config_dir: impl Into<PathBuf>) -> Self {
        Self {
            profiles: Vec::new(),
            config_dir: config_dir.into(),
        }
    }

    /// 加载配置文件
    pub async fn load(&mut self) -> Result<()> {
        let path = self.config_dir.join("profiles.json");
        if !path.exists() {
            return Ok(());
        }
        let data = fs::read_to_string(&path).await?;
        self.profiles = serde_json::from_str(&data)?;
        Ok(())
    }

    /// 保存配置文件
    pub async fn save(&self) -> Result<()> {
        let path = self.config_dir.join("profiles.json");
        fs::create_dir_all(&self.config_dir).await?;
        let data = serde_json::to_string_pretty(&self.profiles)?;
        fs::write(&path, data).await?;
        Ok(())
    }

    /// 添加配置
    pub fn add(&mut self, profile: LaunchProfile) {
        self.profiles.push(profile);
    }

    /// 删除配置
    pub fn remove(&mut self, name: &str) -> bool {
        let len = self.profiles.len();
        self.profiles.retain(|p| p.name != name);
        self.profiles.len() < len
    }

    /// 获取所有配置
    pub fn list(&self) -> &[LaunchProfile] {
        &self.profiles
    }

    /// 按名称查找配置
    pub fn find(&self, name: &str) -> Option<&LaunchProfile> {
        self.profiles.iter().find(|p| p.name == name)
    }

    /// 按名称查找（可变引用）
    pub fn find_mut(&mut self, name: &str) -> Option<&mut LaunchProfile> {
        self.profiles.iter_mut().find(|p| p.name == name)
    }
}

/// 版本级启动设置（覆盖全局默认；存于 .minecraft/versions/<id>/launcher.json）
///
/// 未设置的字段（None / false / 空列表）表示跟随全局默认。
#[derive(Debug, Clone, Default, serde::Serialize, serde::Deserialize)]
pub struct VersionSettings {
    /// 最小内存 (MB)
    pub min_memory_mb: Option<u32>,
    /// 最大内存 (MB)
    pub max_memory_mb: Option<u32>,
    /// 游戏语言覆盖（如 zh_CN / en_US）
    pub language: Option<String>,
    /// 版本隔离：使用独立游戏目录 versions/<id>/game
    pub isolated: bool,
    /// Java 可执行文件
    pub java_executable: Option<PathBuf>,
    /// 版本级额外 JVM 参数（覆盖全局）
    pub jvm_args: Vec<String>,
    /// 是否启用内置 JVM 优化参数（None = 跟随全局）
    pub use_builtin_jvm_args: Option<bool>,
}

/// 版本设置存取器（.minecraft/versions/<id>/launcher.json）
pub struct VersionSettingsStore {
    dot_minecraft: PathBuf,
}

impl VersionSettingsStore {
    /// 创建存取器
    pub fn new(dot_minecraft: PathBuf) -> Self {
        Self { dot_minecraft }
    }

    /// 版本设置文件路径
    pub fn path(&self, version_id: &str) -> PathBuf {
        self.dot_minecraft
            .join("versions")
            .join(version_id)
            .join("launcher.json")
    }

    /// 加载版本设置（文件不存在返回空默认）
    pub async fn load(&self, version_id: &str) -> Result<VersionSettings> {
        let path = self.path(version_id);
        if !path.exists() {
            return Ok(VersionSettings::default());
        }
        let data = fs::read_to_string(&path).await?;
        serde_json::from_str(&data)
            .map_err(|e| LauncherError::ConfigError(format!("版本设置解析失败: {}", e)))
    }

    /// 保存版本设置
    pub async fn save(&self, version_id: &str, settings: &VersionSettings) -> Result<()> {
        let path = self.path(version_id);
        if let Some(parent) = path.parent() {
            fs::create_dir_all(parent).await?;
        }
        let data = serde_json::to_string_pretty(settings)
            .map_err(|e| LauncherError::ConfigError(format!("版本设置序列化失败: {}", e)))?;
        fs::write(&path, data).await?;
        Ok(())
    }
}
