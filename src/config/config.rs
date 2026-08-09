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
    /// 默认窗口大小
    pub default_window_width: u32,
    pub default_window_height: u32,
    /// Java 可执行文件（手动指定）
    pub java_executable: Option<PathBuf>,
    /// 额外 JVM 参数
    pub extra_jvm_args: Vec<String>,
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
    /// 已安装的插件目录
    pub plugins_dir: PathBuf,
    /// 是否启用实验性功能
    pub experimental: bool,
}

impl Default for LauncherConfig {
    fn default() -> Self {
        Self {
            config_version: 2,
            dot_minecraft: dirs::data_dir()
                .unwrap_or_else(|| PathBuf::from("."))
                .join(".minecraft"),
            language: "zh_CN".into(),
            default_min_memory_mb: 1024,
            default_max_memory_mb: 4096,
            default_window_width: 854,
            default_window_height: 480,
            java_executable: None,
            extra_jvm_args: Vec::new(),
            download_concurrency: 8,
            download_max_retries: 3,
            max_cache_entries: 128,
            auto_update: false,
            login_method: "offline".into(),
            last_username: None,
            plugins_dir: dirs::data_dir()
                .unwrap_or_else(|| PathBuf::from("."))
                .join("starcloud-launcher")
                .join("plugins"),
            experimental: false,
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
    pub fn migrate(&mut self) -> Result<()> {
        if self.config_version < 1 {
            // 从 v0 → v1 迁移
            self.download_concurrency = 8;
            self.download_max_retries = 3;
            self.max_cache_entries = 128;
        }
        if self.config_version < 2 {
            // 从 v1 → v2 迁移：新增字段/旧配置缺失时补齐安全默认值
            if self.download_concurrency == 0 {
                self.download_concurrency = 8;
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
            if self.default_window_width == 0 {
                self.default_window_width = 854;
            }
            if self.default_window_height == 0 {
                self.default_window_height = 480;
            }
            self.config_version = 2;
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
    /// 窗口大小
    pub window_width: u32,
    pub window_height: u32,
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
            window_width: 854,
            window_height: 480,
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
        .with_window(self.window_width, self.window_height)
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
