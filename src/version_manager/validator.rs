use crate::error::{LauncherError, Result};
use crate::launch::LaunchConfig;
use crate::version::manifest::VersionDetails;

/// 启动配置完整性校验器
pub struct VersionValidator;

impl VersionValidator {
    /// 校验 LaunchConfig
    pub fn validate_launch_config(config: &LaunchConfig) -> Result<()> {
        // 检查版本 ID
        if config.version_id.is_empty() {
            return Err(LauncherError::ArgumentError("版本 ID 不能为空".to_string()));
        }

        // 检查 .minecraft 目录
        if config.dot_minecraft.as_os_str().is_empty() {
            return Err(LauncherError::ArgumentError(".minecraft 目录不能为空".to_string()));
        }

        // 检查用户名
        if config.username.is_empty() || config.username.len() > 32 {
            return Err(LauncherError::ArgumentError(
                "用户名长度必须在 1-32 字符之间".to_string()
            ));
        }
        if !config.username.chars().all(|c| c.is_alphanumeric() || c == '_') {
            return Err(LauncherError::ArgumentError(
                "用户名只能包含字母、数字和下划线".to_string()
            ));
        }

        // 检查 UUID
        if let Err(e) = uuid::Uuid::parse_str(&config.uuid) {
            return Err(LauncherError::ArgumentError(
                format!("无效的 UUID: {}", e)
            ));
        }

        // 检查内存
        if config.min_memory_mb < 256 || config.max_memory_mb < 256 {
            return Err(LauncherError::ArgumentError(
                "内存分配至少为 256MB".to_string()
            ));
        }
        if config.min_memory_mb > config.max_memory_mb {
            return Err(LauncherError::ArgumentError(
                "最小内存不能大于最大内存".to_string()
            ));
        }
        if config.max_memory_mb > 65536 {
            return Err(LauncherError::ArgumentError(
                "最大内存不能超过 65536MB (64GB)".to_string()
            ));
        }

        // 检查窗口
        if config.window_width < 320 || config.window_height < 240 {
            return Err(LauncherError::ArgumentError(
                "窗口大小至少为 320x240".to_string()
            ));
        }

        Ok(())
    }

    /// 校验 VersionDetails
    pub fn validate_version_details(details: &VersionDetails) -> Result<()> {
        if details.id.is_empty() {
            return Err(LauncherError::ArgumentError("版本 ID 不能为空".to_string()));
        }
        if details.main_class.is_empty() {
            return Err(LauncherError::ArgumentError("主类名不能为空".to_string()));
        }
        Ok(())
    }

    /// 校验 version.json 文件是否存在
    pub fn check_version_json(dot_minecraft: &std::path::Path, version_id: &str) -> Result<()> {
        let json_path = dot_minecraft
            .join("versions")
            .join(version_id)
            .join(format!("{}.json", version_id));
        if !json_path.exists() {
            return Err(LauncherError::VersionNotFound(version_id.to_string()));
        }
        Ok(())
    }

    /// 校验资源完整性（检查 indexes 目录）
    pub fn check_asset_indexes(dot_minecraft: &std::path::Path) -> Result<()> {
        let indexes_dir = dot_minecraft.join("assets").join("indexes");
        if !indexes_dir.exists() || indexes_dir.read_dir().map(|mut d| d.next().is_none()).unwrap_or(true) {
            return Err(LauncherError::ValidationError {
                expected: "资源索引文件".into(),
                actual: "空目录或不存在的目录".into(),
            });
        }
        Ok(())
    }

    /// 校验 URL 合法性
    pub fn validate_url(url: &str) -> Result<()> {
        if url.is_empty() {
            return Err(LauncherError::ArgumentError("URL 不能为空".to_string()));
        }
        if !url.starts_with("http://") && !url.starts_with("https://") {
            return Err(LauncherError::ArgumentError(
                "URL 必须以 http:// 或 https:// 开头".to_string()
            ));
        }
        Ok(())
    }

    /// 校验路径（检查是否包含非法字符）
    pub fn validate_path(path: &std::path::Path) -> Result<()> {
        let path_str = path.to_string_lossy();
        if path_str.contains('\0') || path_str.contains('\n') || path_str.contains('\r') {
            return Err(LauncherError::ArgumentError("路径包含非法字符".to_string()));
        }
        Ok(())
    }

    /// 校验插件元信息
    pub fn validate_plugin_meta(meta: &crate::plugin::api::PluginMeta) -> Result<()> {
        if meta.id.is_empty() || meta.id.len() > 128 {
            return Err(LauncherError::PluginError("插件 ID 长度必须在 1-128 字符".into()));
        }
        if meta.name.is_empty() || meta.name.len() > 64 {
            return Err(LauncherError::PluginError("插件名称长度必须在 1-64 字符".into()));
        }
        if meta.version.is_empty() {
            return Err(LauncherError::PluginError("插件版本不能为空".into()));
        }
        Ok(())
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::launch::LaunchConfig;

    #[test]
    fn test_validate_launch_config() {
        let config = LaunchConfig::basic("1.21", "/tmp/.minecraft", "Player");
        assert!(VersionValidator::validate_launch_config(&config).is_ok());

        let bad_config = LaunchConfig::basic("", "/tmp/.minecraft", "Player");
        assert!(VersionValidator::validate_launch_config(&bad_config).is_err());

        let bad_mem = LaunchConfig::basic("1.21", "/tmp/.minecraft", "Player")
            .with_memory(1024, 512);
        assert!(VersionValidator::validate_launch_config(&bad_mem).is_err());
    }

    #[test]
    fn test_validate_url() {
        assert!(VersionValidator::validate_url("https://example.com").is_ok());
        assert!(VersionValidator::validate_url("ftp://bad.com").is_err());
        assert!(VersionValidator::validate_url("").is_err());
    }
}
