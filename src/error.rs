use thiserror::Error;

/// 启动器核心错误类型
#[derive(Error, Debug)]
pub enum LauncherError {
    #[error("下载失败: {0}")]
    DownloadError(String),

    #[error("HTTP 请求失败: {0}")]
    HttpError(#[from] reqwest::Error),

    #[error("IO 错误: {0}")]
    IoError(#[from] std::io::Error),

    #[error("JSON 解析错误: {0}")]
    JsonError(#[from] serde_json::Error),

    #[error("ZIP 解析错误: {0}")]
    ZipError(#[from] zip::result::ZipError),

    #[error("验证失败: 期望 {expected}，实际 {actual}")]
    ValidationError { expected: String, actual: String },

    #[error("版本未找到: {0}")]
    VersionNotFound(String),

    #[error("Java 未找到，请安装 Java {0} 或更高版本")]
    JavaNotFound(u32),

    #[error("启动失败: {0}")]
    LaunchError(String),

    #[error("插件错误: {0}")]
    PluginError(String),

    #[error("插件加载失败: {0}")]
    PluginLoadError(String),

    #[error("插件未找到: {0}")]
    PluginNotFound(String),

    #[error("配置文件错误: {0}")]
    ConfigError(String),

    #[error("参数构建错误: {0}")]
    ArgumentError(String),

    #[error("异步任务错误: {0}")]
    JoinError(String),

    #[error("未分类错误: {0}")]
    Other(String),
}

impl From<tokio::task::JoinError> for LauncherError {
    fn from(e: tokio::task::JoinError) -> Self {
        LauncherError::JoinError(e.to_string())
    }
}

/// 启动器核心 Result 别名
pub type Result<T> = std::result::Result<T, LauncherError>;
