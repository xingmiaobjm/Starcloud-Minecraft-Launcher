use crate::download::http_client::HttpClient;
use crate::download::version_cache;
use crate::error::Result;
use crate::version::manifest::{VersionDetails, VersionManifest};
use std::path::PathBuf;

const MANIFEST_URL: &str = "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json";

/// 版本清单 + 缓存来源标记
#[derive(Debug, Clone)]
pub struct CachedManifest {
    pub manifest: VersionManifest,
    /// true = 来自本地缓存（尚未合并远程数据）
    pub from_cache: bool,
    /// 本地缓存文件路径（用于后续覆盖写入）
    pub cache_path: PathBuf,
}

impl CachedManifest {
    pub fn from_cache(m: VersionManifest) -> Self {
        Self {
            manifest: m,
            from_cache: true,
            cache_path: version_cache::cache_file_path(),
        }
    }

    pub fn from_remote(m: VersionManifest) -> Self {
        Self {
            manifest: m,
            from_cache: false,
            cache_path: version_cache::cache_file_path(),
        }
    }
}

/// 从 Mojang 官方获取版本清单（带重试）
pub async fn fetch_version_manifest() -> Result<VersionManifest> {
    fetch_version_manifest_from(MANIFEST_URL).await
}

/// 从指定地址获取版本清单（测试可注入 mock server 地址）
pub async fn fetch_version_manifest_from(url: &str) -> Result<VersionManifest> {
    let client = HttpClient::new()?;
    let manifest: VersionManifest = client.get_json_with_retry(url).await?;
    log::info!("获取版本清单成功，共 {} 个版本", manifest.versions.len());
    Ok(manifest)
}

/// 获取版本清单（带本地缓存增量更新）
///
/// - `cache_enabled = true`：先加载本地缓存，再拉取远程新版本增量合并，保存缓存；
///   若网络不通则降级返回本地缓存（弱网/离线可用）
/// - `cache_enabled = false`（联机模式）：始终从网络全量拉取，不读写缓存
pub async fn fetch_version_manifest_cached(cache_enabled: bool) -> Result<CachedManifest> {
    if !cache_enabled {
        log::info!("联机模式：跳过本地缓存，从网络拉取版本清单");
        return fetch_version_manifest().await.map(CachedManifest::from_remote);
    }

    // 1. 加载本地缓存
    let cached = version_cache::load_cache();

    // 2. 尝试拉取远程最新清单
    match fetch_version_manifest().await {
        Ok(remote) => {
            // 3. 增量合并
            if let Some(mut c) = cached {
                version_cache::merge_cache(&mut c, &remote);
                version_cache::save_cache(&c);
                Ok(CachedManifest::from_remote(c))
            } else {
                // 首次使用，直接保存远程清单
                version_cache::save_cache(&remote);
                Ok(CachedManifest::from_remote(remote))
            }
        }
        Err(e) => {
            // 网络失败，尝试降级到本地缓存
            if let Some(c) = cached {
                log::warn!(
                    "网络拉取版本清单失败，降级使用本地缓存 ({} 个版本): {}",
                    c.versions.len(),
                    e
                );
                Ok(CachedManifest::from_cache(c))
            } else {
                // 没有缓存兜底，向上抛出错误
                Err(e)
            }
        }
    }
}

/// 后台异步拉取远程版本清单并覆盖本地缓存
///
/// 用于「先返回缓存，后台刷新」模式：调用方立即返回本地缓存给前端，
/// 同时后台拉取远程数据，完成后返回合并后的清单。
pub async fn background_refresh_cache() -> Result<VersionManifest> {
    let remote = fetch_version_manifest().await?;
    let cached = version_cache::load_cache();
    if let Some(mut c) = cached {
        version_cache::merge_cache(&mut c, &remote);
        version_cache::save_cache(&c);
        Ok(c)
    } else {
        version_cache::save_cache(&remote);
        Ok(remote)
    }
}

/// 仅从本地缓存加载版本清单，不触发网络请求
///
/// 用于「先返回缓存，后台刷新」模式的热路径：调用方立即拿到缓存数据，
/// 同时 spawn 后台任务拉取远程并推送更新事件。
pub fn fetch_version_manifest_cache_only() -> Option<VersionManifest> {
    version_cache::load_cache()
}

/// 获取特定版本的详细信息 version.json（带重试）
pub async fn fetch_version_details(url: &str) -> Result<VersionDetails> {
    let client = HttpClient::new()?;
    let details: VersionDetails = client.get_json_with_retry(url).await?;
    log::debug!("获取版本详情成功: {}", details.id);
    Ok(details)
}
