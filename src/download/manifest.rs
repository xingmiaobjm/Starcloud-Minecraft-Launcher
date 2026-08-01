use crate::version::manifest::{VersionDetails, VersionManifest};
use crate::error::{LauncherError, Result};

const MANIFEST_URL: &str = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

/// 从 Mojang 官方获取版本清单
pub async fn fetch_version_manifest() -> Result<VersionManifest> {
    let client = reqwest::Client::new();
    let response = client
        .get(MANIFEST_URL)
        .timeout(std::time::Duration::from_secs(30))
        .send()
        .await?;

    if !response.status().is_success() {
        return Err(LauncherError::DownloadError(format!(
            "获取版本清单失败: HTTP {}",
            response.status()
        )));
    }

    let manifest: VersionManifest = response.json().await?;
    Ok(manifest)
}

/// 获取特定版本的详细信息 (version.json)
pub async fn fetch_version_details(url: &str) -> Result<VersionDetails> {
    let client = reqwest::Client::new();
    let response = client
        .get(url)
        .timeout(std::time::Duration::from_secs(30))
        .send()
        .await?;

    if !response.status().is_success() {
        return Err(LauncherError::DownloadError(format!(
            "获取版本详情失败: HTTP {}",
            response.status()
        )));
    }

    let details: VersionDetails = response.json().await?;
    Ok(details)
}
