use serde::{Deserialize, Serialize};

/// Minecraft 官方版本清单（顶层）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VersionManifest {
    pub latest: LatestVersion,
    pub versions: Vec<VersionEntry>,
}

/// 最新版本信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LatestVersion {
    pub release: String,
    pub snapshot: String,
}

/// 版本条目
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VersionEntry {
    pub id: String,
    #[serde(rename = "type")]
    pub version_type: VersionType,
    pub url: String,
    pub time: String,
    #[serde(rename = "releaseTime")]
    pub release_time: String,
    pub sha1: Option<String>,
    #[serde(rename = "complianceLevel")]
    pub compliance_level: Option<u32>,
}

/// 版本类型
#[derive(Debug, Clone, Serialize, Deserialize, PartialEq)]
#[serde(rename_all = "snake_case")]
pub enum VersionType {
    Release,
    Snapshot,
    #[serde(rename = "old_beta")]
    OldBeta,
    #[serde(rename = "old_alpha")]
    OldAlpha,
    Other(String),
}

/// 具体版本详情（version.json）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VersionDetails {
    #[serde(default)]
    #[serde(rename = "assetIndex")]
    pub asset_index: Option<AssetIndexInfo>,
    pub assets: Option<String>,
    #[serde(default)]
    pub downloads: Downloads,
    pub id: String,
    pub libraries: Vec<Library>,
    #[serde(default)]
    pub logging: LoggingConfig,
    #[serde(rename = "mainClass")]
    pub main_class: String,
    #[serde(rename = "minecraftArguments")]
    pub minecraft_arguments: Option<String>,
    /// 新版（1.21.4+）version.json 已移除该字段，需可缺省
    #[serde(rename = "minimumLauncherVersion", default)]
    pub minimum_launcher_version: Option<i32>,
    #[serde(rename = "releaseTime")]
    pub release_time: String,
    pub time: String,
    #[serde(rename = "type")]
    pub version_type: VersionType,
    #[serde(default)]
    pub arguments: Option<VersionArguments>,
    #[serde(rename = "javaVersion")]
    pub java_version: Option<JavaVersionInfo>,
}

/// 资源索引信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AssetIndexInfo {
    pub id: String,
    pub sha1: String,
    pub size: u64,
    #[serde(rename = "totalSize")]
    pub total_size: u64,
    pub url: String,
}

/// 下载信息
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct Downloads {
    #[serde(default)]
    pub client: Option<DownloadInfo>,
    #[serde(rename = "client_mappings")]
    pub client_mappings: Option<DownloadInfo>,
    pub server: Option<DownloadInfo>,
    #[serde(rename = "server_mappings")]
    pub server_mappings: Option<DownloadInfo>,
}

/// 单个下载条目
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct DownloadInfo {
    pub sha1: String,
    pub size: u64,
    pub url: String,
    pub path: Option<String>,
}

/// 库文件定义
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Library {
    pub name: String,
    #[serde(default)]
    pub downloads: Option<LibraryDownloads>,
    pub url: Option<String>,
    #[serde(default)]
    pub rules: Option<Vec<Rule>>,
    #[serde(default)]
    pub natives: Option<std::collections::HashMap<String, String>>,
    #[serde(default)]
    pub extract: Option<ExtractConfig>,
}

/// 库文件下载信息
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LibraryDownloads {
    pub artifact: Option<LibraryArtifact>,
    pub classifiers: Option<std::collections::HashMap<String, LibraryArtifact>>,
}

/// 库文件制品
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct LibraryArtifact {
    pub path: String,
    pub sha1: String,
    pub size: u64,
    pub url: String,
}

/// 规则定义（用于条件加载）
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct Rule {
    pub action: RuleAction,
    #[serde(default)]
    pub os: Option<OsRule>,
    #[serde(default)]
    pub features: Option<std::collections::HashMap<String, bool>>,
}

/// 规则动作
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(rename_all = "lowercase")]
pub enum RuleAction {
    Allow,
    Disallow,
}

/// 操作系统规则
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct OsRule {
    /// 系统名（如 windows/osx/linux）；可缺省（如仅按 arch 匹配）
    #[serde(default)]
    pub name: Option<String>,
    #[serde(default)]
    pub version: Option<String>,
    #[serde(default)]
    pub arch: Option<String>,
}

/// 解压配置
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ExtractConfig {
    pub exclude: Vec<String>,
}

/// 日志配置
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct LoggingConfig {
    pub client: LogConfig,
}

/// 日志文件配置
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct LogConfig {
    pub argument: String,
    pub file: LogFileInfo,
    #[serde(rename = "type")]
    pub logger_type: String,
}

/// 日志文件信息
#[derive(Debug, Clone, Serialize, Deserialize, Default)]
pub struct LogFileInfo {
    pub id: String,
    pub sha1: String,
    pub size: u64,
    pub url: String,
}

/// 版本参数
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct VersionArguments {
    pub game: Vec<ArgumentValue>,
    pub jvm: Vec<ArgumentValue>,
}

/// 参数值（可以是字符串或带条件的参数对象）
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(untagged)]
pub enum ArgumentValue {
    Simple(String),
    Conditional(ConditionalArgument),
}

/// 条件参数
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct ConditionalArgument {
    pub rules: Vec<Rule>,
    pub value: ArgumentValueInner,
}

/// 条件参数值
#[derive(Debug, Clone, Serialize, Deserialize)]
#[serde(untagged)]
pub enum ArgumentValueInner {
    Single(String),
    Multiple(Vec<String>),
}

/// Java 版本要求
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct JavaVersionInfo {
    pub component: String,
    #[serde(rename = "majorVersion")]
    pub major_version: u32,
}

/// 资源索引
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AssetIndex {
    pub objects: std::collections::HashMap<String, AssetObject>,
}

/// 资源对象
#[derive(Debug, Clone, Serialize, Deserialize)]
pub struct AssetObject {
    pub hash: String,
    pub size: u64,
}
