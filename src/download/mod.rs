pub mod assets;
pub mod engine;
pub mod http_client;
pub mod library;
pub mod manifest;
pub mod progress;

pub use assets::AssetDownloader;
pub use engine::DownloadEngine;
pub use http_client::{HttpClient, RetryConfig};
pub use library::LibraryDownloader;
pub use manifest::{fetch_version_details, fetch_version_manifest, fetch_version_manifest_from};
pub use progress::{ProgressInfo, ProgressStatus, ProgressTracker};

use crate::error::Result;
use crate::version::manifest::VersionDetails;
use sha1::Digest;
use std::path::Path;

/// 日志配置文件的目标文件名（versions/{id}/{id}-log4j2.xml）
pub fn logging_config_file_name(version_id: &str) -> String {
    format!("{}-log4j2.xml", version_id)
}

/// 确保日志配置文件已下载（存在且 SHA1 匹配则跳过）
///
/// 将 version.json 中的 `logging.client.file` 下载到
/// `versions/{id}/{id}-log4j2.xml`，供 `${path}` 替换使用。
pub async fn ensure_logging_config(dot_minecraft: &Path, details: &VersionDetails) -> Result<()> {
    let file = &details.logging.client.file;
    if file.url.is_empty() || file.sha1.is_empty() {
        return Ok(()); // 无日志配置（旧版或第三方版本）
    }

    let dest_dir = dot_minecraft.join("versions").join(&details.id);
    let dest = dest_dir.join(logging_config_file_name(&details.id));

    // 已存在且 SHA1 匹配则跳过
    if dest.exists() {
        if let Ok(bytes) = std::fs::read(&dest) {
            let hash = format!("{:x}", sha1::Sha1::digest(&bytes));
            if hash == file.sha1 {
                return Ok(());
            }
        }
        log::warn!("日志配置文件已损坏，重新下载: {}", dest.display());
    }

    tokio::fs::create_dir_all(&dest_dir).await?;
    let client = HttpClient::new()?;
    let bytes = client.get_bytes_with_retry(&file.url).await?;
    let hash = format!("{:x}", sha1::Sha1::digest(&bytes));
    if hash != file.sha1 {
        return Err(crate::error::LauncherError::ValidationError {
            expected: file.sha1.clone(),
            actual: hash,
        });
    }
    tokio::fs::write(&dest, &bytes).await?;
    log::debug!("日志配置文件已下载: {}", dest.display());
    Ok(())
}
