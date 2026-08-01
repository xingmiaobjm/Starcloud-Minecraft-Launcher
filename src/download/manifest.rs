use crate::download::http_client::HttpClient;
use crate::error::Result;
use crate::version::manifest::{VersionDetails, VersionManifest};

const MANIFEST_URL: &str = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

/// 从 Mojang 官方获取版本清单（带重试）
pub async fn fetch_version_manifest() -> Result<VersionManifest> {
    let client = HttpClient::new()?;
    let manifest: VersionManifest = client.get_json_with_retry(MANIFEST_URL).await?;
    log::info!("获取版本清单成功，共 {} 个版本", manifest.versions.len());
    Ok(manifest)
}

/// 获取特定版本的详细信息 version.json（带重试）
pub async fn fetch_version_details(url: &str) -> Result<VersionDetails> {
    let client = HttpClient::new()?;
    let details: VersionDetails = client.get_json_with_retry(url).await?;
    log::debug!("获取版本详情成功: {}", details.id);
    Ok(details)
}
