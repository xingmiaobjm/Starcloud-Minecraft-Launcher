use crate::error::{LauncherError, Result};
use crate::version::manifest::AssetIndex;
use std::path::{Path, PathBuf};
use tokio::fs;
use tokio::io::AsyncWriteExt;

/// 资源文件下载器
pub struct AssetDownloader {
    /// 资源根目录
    base_dir: PathBuf,
    /// 并发下载数
    concurrency: usize,
}

impl AssetDownloader {
    /// 创建资源下载器
    /// `base_dir` 应为 `.minecraft/assets` 目录
    pub fn new(base_dir: impl Into<PathBuf>) -> Self {
        Self {
            base_dir: base_dir.into(),
            concurrency: 8,
        }
    }

    /// 设置并发下载数
    pub fn concurrency(mut self, n: usize) -> Self {
        self.concurrency = n;
        self
    }

    /// 下载资源索引对应的所有资源文件
    pub async fn download_assets(&self, asset_index: &AssetIndex) -> Result<()> {
        let objects_dir = self.base_dir.join("objects");
        fs::create_dir_all(&objects_dir).await?;

        let total = asset_index.objects.len();
        log::info!("开始下载 {} 个资源文件...", total);

        let client = reqwest::Client::new();
        let mut tasks = Vec::new();

        for obj in asset_index.objects.values() {
            if tasks.len() >= self.concurrency {
                // 等待一批完成
                for task in tasks.drain(..) {
                    task.await??;
                }
            }
            let dir = objects_dir.clone();
            let client = client.clone();
            let obj_hash = obj.hash.clone();
            let obj_size = obj.size;
            tasks.push(tokio::spawn(async move {
                download_single_asset(&client, &dir, &obj_hash, obj_size).await
            }));
        }

        // 等待剩余任务
        for task in tasks {
            task.await??;
        }

        log::info!("资源文件下载完成");
        Ok(())
    }

    /// 获取资源索引 JSON 并下载
    pub async fn download_from_url(&self, index_url: &str) -> Result<()> {
        let client = reqwest::Client::new();
        let response = client.get(index_url).send().await?;
        let index: AssetIndex = response.json().await?;
        self.download_assets(&index).await
    }
}

/// 下载单个资源文件
async fn download_single_asset(
    client: &reqwest::Client,
    objects_dir: &Path,
    hash: &str,
    _size: u64,
) -> Result<()> {
    let sub_dir = &hash[..2];
    let asset_dir = objects_dir.join(sub_dir);
    fs::create_dir_all(&asset_dir).await?;

    let file_path = asset_dir.join(hash);
    if file_path.exists() {
        return Ok(());
    }

    let url = format!(
        "https://resources.download.minecraft.net/{}/{}",
        sub_dir, hash
    );

    let response = client.get(&url).send().await?;
    if !response.status().is_success() {
        return Err(LauncherError::DownloadError(format!(
            "下载资源失败: {} (HTTP {})",
            hash,
            response.status()
        )));
    }

    let bytes = response.bytes().await?;

    // 写入临时文件后重命名，保证原子性
    let mut temp_path = file_path.clone();
    temp_path.set_extension("tmp");
    let mut file = fs::File::create(&temp_path).await?;
    file.write_all(&bytes).await?;
    file.flush().await?;

    fs::rename(&temp_path, &file_path).await?;
    Ok(())
}
