use crate::download::http_client::HttpClient;
use crate::download::progress::ProgressTracker;
use crate::error::{LauncherError, Result};
use crate::version::manifest::AssetIndex;
use sha1::Digest;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use tokio::fs;
use tokio::io::AsyncWriteExt;

/// 资源文件下载器
pub struct AssetDownloader {
    /// 资源根目录 (.minecraft/assets)
    base_dir: PathBuf,
    /// 并发下载数
    concurrency: usize,
    /// 进度追踪器（可选）
    tracker: Option<Arc<ProgressTracker>>,
}

impl AssetDownloader {
    /// 创建资源下载器
    /// `base_dir` 应为 `.minecraft/assets` 目录
    pub fn new(base_dir: impl Into<PathBuf>) -> Self {
        Self {
            base_dir: base_dir.into(),
            concurrency: 8,
            tracker: None,
        }
    }

    /// 设置并发下载数
    pub fn concurrency(mut self, n: usize) -> Self {
        self.concurrency = n.max(1);
        self
    }

    /// 接入进度追踪器
    pub fn with_tracker(mut self, tracker: Arc<ProgressTracker>) -> Self {
        self.tracker = Some(tracker);
        self
    }

    /// 下载资源索引对应的所有资源文件
    pub async fn download_assets(&self, asset_index: &AssetIndex) -> Result<()> {
        let objects_dir = self.base_dir.join("objects");
        fs::create_dir_all(&objects_dir).await?;

        let total = asset_index.objects.len();
        let total_size: u64 = asset_index.objects.values().map(|o| o.size).sum();
        log::info!(
            "开始下载 {} 个资源文件（约 {} bytes）...",
            total,
            total_size
        );

        let http = HttpClient::new()?;
        let mut tasks = Vec::new();

        if let Some(tracker) = &self.tracker {
            tracker.begin_stage("下载资源", total_size);
        }

        for obj in asset_index.objects.values() {
            if tasks.len() >= self.concurrency {
                // 等待一批完成
                for task in tasks.drain(..) {
                    let size = task.await??;
                    if let Some(tracker) = &self.tracker {
                        tracker.add_progress(size);
                    }
                }
            }
            let dir = objects_dir.clone();
            let http = http.clone();
            let obj_hash = obj.hash.clone();
            let obj_size = obj.size;
            tasks.push(tokio::spawn(async move {
                download_single_asset(&http, &dir, &obj_hash, obj_size).await
            }));
        }

        // 等待剩余任务
        for task in tasks {
            let size = task.await??;
            if let Some(tracker) = &self.tracker {
                tracker.add_progress(size);
            }
        }

        if let Some(tracker) = &self.tracker {
            tracker.set_status(crate::download::progress::ProgressStatus::Completed);
        }

        log::info!("资源文件下载完成");
        Ok(())
    }

    /// 获取资源索引 JSON 并下载
    pub async fn download_from_url(&self, index_url: &str) -> Result<()> {
        let http = HttpClient::new()?;
        let index: AssetIndex = http.get_json_with_retry(index_url).await?;
        self.download_assets(&index).await
    }
}

/// 下载单个资源文件，返回写入字节数
///
/// 下载后校验 SHA1 与大小；`hash` 长度不足 2 时视为无效并报错。
async fn download_single_asset(
    http: &HttpClient,
    objects_dir: &Path,
    hash: &str,
    size: u64,
) -> Result<u64> {
    if hash.len() < 2 {
        return Err(LauncherError::Other(format!(
            "资源哈希长度无效: {:?}",
            hash
        )));
    }

    let sub_dir = &hash[..2];
    let asset_dir = objects_dir.join(sub_dir);
    fs::create_dir_all(&asset_dir).await?;

    let file_path = asset_dir.join(hash);

    // 已存在且大小一致则跳过（快速路径；完整校验见下载分支）
    if file_path.exists() {
        let existing_size = file_path.metadata().map(|m| m.len()).unwrap_or(0);
        if existing_size == size {
            return Ok(0);
        }
    }

    let url = format!(
        "https://resources.download.minecraft.net/{}/{}",
        sub_dir, hash
    );

    let bytes = http.get_bytes_with_retry(&url).await?;

    // SHA1 + size 校验
    let actual_hash = format!("{:x}", sha1::Sha1::digest(&bytes));
    if actual_hash != hash {
        return Err(LauncherError::ValidationError {
            expected: hash.to_string(),
            actual: actual_hash,
        });
    }
    if size > 0 && bytes.len() as u64 != size {
        return Err(LauncherError::ValidationError {
            expected: format!("size {}", size),
            actual: format!("size {}", bytes.len()),
        });
    }

    // 写入临时文件后重命名，保证原子性
    let mut temp_path = file_path.clone();
    temp_path.set_extension("tmp");
    let mut file = fs::File::create(&temp_path).await?;
    file.write_all(&bytes).await?;
    file.flush().await?;

    if file_path.exists() {
        fs::remove_file(&file_path).await?;
    }
    fs::rename(&temp_path, &file_path).await?;

    Ok(bytes.len() as u64)
}
