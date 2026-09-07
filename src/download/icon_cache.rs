//! Mod 图标本地缓存
//!
//! 图标下载到 `%APPDATA%/starcloud-launcher/icons/` 目录，
//! 以 URL 的 SHA256 哈希为文件名。首次访问时异步下载并缓存，
//! 后续直接返回本地文件路径，实现秒开。

use std::path::PathBuf;

fn data_dir() -> PathBuf {
    dirs::data_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("starcloud-launcher")
}

fn icons_dir() -> PathBuf {
    data_dir().join("icons")
}

/// 用 URL 的 SHA256 作为缓存文件名（保留扩展名）
fn cache_file_name(url: &str) -> String {
    use sha2::{Digest, Sha256};
    let hash = format!("{:x}", Sha256::digest(url.as_bytes()));
    // 尝试从 URL 提取扩展名
    let ext = url
        .rsplit('.')
        .next()
        .and_then(|e| {
            let e = e.split('?').next().unwrap_or("");
            if e.len() <= 5 && e.chars().all(|c| c.is_alphanumeric()) {
                Some(e)
            } else {
                None
            }
        })
        .unwrap_or("png");
    format!("{}.{}", hash, ext)
}

/// 获取图标本地缓存路径（若已缓存）
pub fn get_cached_icon_path(url: &str) -> Option<PathBuf> {
    let path = icons_dir().join(cache_file_name(url));
    if path.exists() {
        Some(path)
    } else {
        None
    }
}

/// 异步下载图标到本地缓存，返回本地文件路径。
/// 若已缓存则直接返回路径（不重复下载）。
///
/// 多源回退：优先尝试原始 CDN 地址，失败时尝试 MCIM 国内镜像，
/// 只要任一源可达即可缓存成功（缓存文件名按原始 URL 哈希，跨源稳定）。
pub async fn cache_icon(url: &str) -> Result<PathBuf, String> {
    let path = icons_dir().join(cache_file_name(url));

    // 已缓存 → 直接返回
    if path.exists() {
        return Ok(path);
    }

    // 下载
    let dir = icons_dir();
    if let Err(e) = std::fs::create_dir_all(&dir) {
        return Err(format!("创建图标缓存目录失败: {}", e));
    }

    let client = crate::download::HttpClient::new().map_err(|e| e.to_string())?;

    // 候选源：原始地址 → MCIM 国内镜像（cdn.modrinth.com → mod.mcimirror.top）
    let mut sources: Vec<String> = vec![url.to_string()];
    if url.starts_with("https://cdn.modrinth.com/") {
        sources.push(url.replacen("https://cdn.modrinth.com/", "https://mod.mcimirror.top/", 1));
    }

    let mut last_err = "下载图标失败".to_string();
    for src in &sources {
        let bytes = match client.get_bytes_with_retry(src).await {
            Ok(b) => b,
            Err(e) => {
                last_err = e.to_string();
                log::warn!("图标源不可用 {}: {}", src, e);
                continue;
            }
        };

        // 原子写入
        let temp = path.with_extension("tmp");
        if let Err(e) = std::fs::write(&temp, &bytes) {
            return Err(format!("写入图标缓存失败: {}", e));
        }
        if let Err(e) = std::fs::rename(&temp, &path) {
            let _ = std::fs::remove_file(&temp);
            return Err(format!("图标缓存重命名失败: {}", e));
        }

        log::debug!("图标已缓存: {} → {}", src, path.display());
        return Ok(path);
    }
    Err(last_err)
}