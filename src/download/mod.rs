pub mod assets;
pub mod engine;
pub mod http_client;
pub mod icon_cache;
pub mod library;
pub mod manifest;
pub mod mod_cache;
pub mod mod_platform;
pub mod progress;
pub mod sources;
pub mod version_cache;

pub use assets::AssetDownloader;
pub use engine::DownloadEngine;
pub use http_client::{HttpClient, RetryConfig};
pub use library::LibraryDownloader;
pub use manifest::{background_refresh_cache, fetch_version_details, fetch_version_manifest, fetch_version_manifest_cache_only, fetch_version_manifest_cached, fetch_version_manifest_from, CachedManifest};
pub use mod_platform::{
    list_curseforge_files, list_modrinth_versions, mcim_translate_description, mcmod_lookup,
    search_curseforge, search_modrinth, search_modrinth_total, translate_query_zh_to_en,
    translate_title_zh, translate_title_zh_cache_hit, ModFile, ModSearchHit,
    ModrinthSearchResult,
};
pub use progress::{ProgressInfo, ProgressStatus, ProgressTracker};
pub use sources::{
    custom_mirror_url, healthy_sources, probe_all_sources, record_source_failure,
    record_source_success, reset_source_health, set_active_source, set_custom_mirror_url,
    SourceKind, SourceProbe,
};

use crate::error::Result;
use crate::version::manifest::VersionDetails;
use futures_util::StreamExt;
use sha1::Digest;
use std::path::Path;
use tokio::io::AsyncWriteExt;

/// 流式下载文件到目标路径（带进度回调 + SHA1 校验 + 原子写入）
///
/// - `dest` 已存在且 SHA1 匹配时跳过（返回 `Ok(false)`）
/// - 进度经 `tracker` 上报（调用方需先 `begin_stage` 设置总量）
/// - 临时文件 + rename 原子写入，中断后残留临时文件可安全重试
pub async fn stream_download_file(
    client: &HttpClient,
    url: &str,
    expected_sha1: &str,
    dest: &Path,
    tracker: Option<&ProgressTracker>,
) -> Result<bool> {
    // 已存在且校验通过则跳过
    if dest.exists() {
        if let Ok(bytes) = std::fs::read(dest) {
            let hash = format!("{:x}", sha1::Sha1::digest(&bytes));
            if hash == expected_sha1 || expected_sha1.is_empty() {
                log::debug!("文件已存在且校验通过，跳过: {}", dest.display());
                return Ok(false);
            }
        }
        log::info!("文件校验失败，重新下载: {}", dest.display());
        tokio::fs::remove_file(dest).await?;
    }
    if let Some(parent) = dest.parent() {
        tokio::fs::create_dir_all(parent).await?;
    }

    log::info!("流式下载: {} → {}", url, dest.display());
    let response = client.get_with_retry(url).await?;
    let temp = dest.with_extension("tmp");
    let mut file = tokio::fs::File::create(&temp).await?;
    let mut stream = response.bytes_stream();
    while let Some(chunk) = stream.next().await {
        let chunk = chunk?;
        file.write_all(&chunk).await?;
        if let Some(t) = tracker {
            t.add_progress(chunk.len() as u64);
        }
    }
    file.flush().await?;

    // SHA1 校验（有期望值时）
    if !expected_sha1.is_empty() {
        let actual = sha1_of_file(&temp).await?;
        if actual != expected_sha1 {
            let _ = tokio::fs::remove_file(&temp).await;
            return Err(crate::error::LauncherError::ValidationError {
                expected: expected_sha1.to_string(),
                actual,
            });
        }
    }

    // 原子写入（Windows 上 rename 前需确保目标不存在）
    if dest.exists() {
        tokio::fs::remove_file(dest).await?;
    }
    tokio::fs::rename(&temp, dest).await?;
    Ok(true)
}

/// 计算文件 SHA1（十六进制小写，异步）
async fn sha1_of_file(path: &Path) -> Result<String> {
    let bytes = tokio::fs::read(path).await?;
    Ok(format!("{:x}", sha1::Sha1::digest(&bytes)))
}

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
