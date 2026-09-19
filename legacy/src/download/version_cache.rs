//! 版本清单本地缓存：增量更新 + 删除重置
//!
//! 将 Mojang 版本清单 JSON 缓存到本地文件，每次列出版本时只拉取新版本增量合并，
//! 避免全量网络请求。开启「联机模式」时跳过缓存，始终从网络拉取。

use crate::version::manifest::VersionManifest;
use std::path::{Path, PathBuf};

/// 缓存文件名
const CACHE_FILE: &str = "version_manifest_cache.json";

/// 获取缓存文件路径（数据目录/starcloud-launcher/version_manifest_cache.json）
pub fn cache_file_path() -> PathBuf {
    dirs::data_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("starcloud-launcher")
        .join(CACHE_FILE)
}

/// 从本地缓存文件加载版本清单
pub fn load_cache() -> Option<VersionManifest> {
    let path = cache_file_path();
    load_cache_from(&path)
}

/// 从指定路径加载缓存
fn load_cache_from(path: &Path) -> Option<VersionManifest> {
    if !path.exists() {
        return None;
    }
    match std::fs::read_to_string(path) {
        Ok(data) => match serde_json::from_str::<VersionManifest>(&data) {
            Ok(m) => {
                log::info!("从本地缓存加载版本清单，共 {} 个版本", m.versions.len());
                Some(m)
            }
            Err(e) => {
                log::warn!("版本清单缓存解析失败，将重新拉取: {}", e);
                None
            }
        },
        Err(e) => {
            log::warn!("读取版本清单缓存失败: {}", e);
            None
        }
    }
}

/// 保存版本清单到本地缓存
pub fn save_cache(manifest: &VersionManifest) {
    let path = cache_file_path();
    save_cache_to(manifest, &path);
}

/// 保存到指定路径
fn save_cache_to(manifest: &VersionManifest, path: &Path) {
    if let Some(parent) = path.parent() {
        if let Err(e) = std::fs::create_dir_all(parent) {
            log::warn!("创建缓存目录失败: {}", e);
            return;
        }
    }
    match serde_json::to_string(manifest) {
        Ok(data) => {
            if let Err(e) = std::fs::write(path, &data) {
                log::warn!("写入版本清单缓存失败: {}", e);
            } else {
                log::info!("版本清单缓存已保存，共 {} 个版本", manifest.versions.len());
            }
        }
        Err(e) => log::warn!("版本清单序列化失败: {}", e),
    }
}

/// 增量合并：将远程拉取到的新版本合并进本地缓存
///
/// - 以 `id` 为键，本地已有的版本不动，只添加远程有而本地没有的版本
/// - 如果远程版本 ID 已存在于本地，用远程数据覆盖（更新时间等字段）
/// - `latest` 字段始终用远程数据覆盖
pub fn merge_cache(cached: &mut VersionManifest, remote: &VersionManifest) {
    // 用远程 latest 覆盖
    cached.latest = remote.latest.clone();

    // 构建本地版本 ID 索引（使用 owned String 避免借用冲突）
    let mut existing: std::collections::HashMap<String, usize> =
        cached.versions.iter().enumerate().map(|(i, v)| (v.id.clone(), i)).collect();

    let mut added = 0usize;
    let mut updated = 0usize;

    for entry in &remote.versions {
        if let Some(&idx) = existing.get(&entry.id) {
            // 已存在，用远程数据覆盖
            cached.versions[idx] = entry.clone();
            updated += 1;
        } else {
            // 新版本，追加
            let new_idx = cached.versions.len();
            cached.versions.push(entry.clone());
            existing.insert(entry.id.clone(), new_idx);
            added += 1;
        }
    }

    log::info!(
        "版本清单增量合并: 新增 {} 个, 更新 {} 个, 总计 {} 个",
        added,
        updated,
        cached.versions.len()
    );
}

/// 删除本地版本清单缓存文件
pub fn clear_cache() {
    let path = cache_file_path();
    if let Err(e) = std::fs::remove_file(&path) {
        if e.kind() != std::io::ErrorKind::NotFound {
            log::warn!("删除版本清单缓存失败: {}", e);
        }
    } else {
        log::info!("版本清单缓存已删除: {}", path.display());
    }
}