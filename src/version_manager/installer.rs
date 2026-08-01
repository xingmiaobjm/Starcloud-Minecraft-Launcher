use crate::download::engine::DownloadEngine;
use crate::download::http_client::HttpClient;
use crate::error::{LauncherError, Result};
use crate::version::manifest::{AssetIndex, VersionDetails};
use sha1::Digest;
use std::path::{Path, PathBuf};
use tokio::fs;
use tokio::io::AsyncWriteExt;

/// 版本安装器 - 管理版本目录的安装、验证、删除
pub struct VersionInstaller {
    /// 下载引擎
    download_engine: DownloadEngine,
}

impl VersionInstaller {
    /// 创建版本安装器
    pub fn new(download_engine: DownloadEngine) -> Self {
        Self { download_engine }
    }

    /// 检查版本是否已安装（json 文件存在）
    pub fn is_installed(&self, version_id: &str) -> bool {
        self.version_json_path(version_id).exists()
    }

    /// 列出所有已安装的版本
    pub async fn list_installed(&self) -> Result<Vec<String>> {
        let versions_dir = self.download_engine.versions_dir();
        if !versions_dir.exists() {
            return Ok(Vec::new());
        }

        let mut entries = fs::read_dir(&versions_dir).await?;
        let mut versions = Vec::new();

        while let Some(entry) = entries.next_entry().await? {
            if entry.file_type().await.map(|t| t.is_dir()).unwrap_or(false) {
                let name = entry.file_name().to_string_lossy().to_string();
                let json_path = entry.path().join(format!("{}.json", name));
                if json_path.exists() {
                    versions.push(name);
                }
            }
        }

        versions.sort_by(|a, b| crate::version_manager::VersionComparator::compare(a, b));
        Ok(versions)
    }

    /// 安装版本（创建目录、写出 version.json）
    pub async fn install_version(&self, details: &VersionDetails) -> Result<()> {
        let version_dir = self.download_engine.versions_dir().join(&details.id);
        fs::create_dir_all(&version_dir).await?;

        // 写入 version.json
        let json_path = self.version_json_path(&details.id);
        let json = serde_json::to_string_pretty(details)?;
        fs::write(&json_path, json).await?;

        log::info!("版本 '{}' 已安装到 {}", details.id, version_dir.display());
        Ok(())
    }

    /// 下载客户端 jar 到 `versions/{id}/{id}.jar`（SHA1 校验 + 原子写入）
    ///
    /// 若 `details.downloads.client` 缺失则返回明确错误（该版本无客户端包）。
    pub async fn install_client_jar(&self, details: &VersionDetails) -> Result<PathBuf> {
        let client_info = details.downloads.client.as_ref().ok_or_else(|| {
            LauncherError::DownloadError(format!(
                "版本 '{}' 缺少客户端下载信息 (downloads.client)",
                details.id
            ))
        })?;

        let dest = self.download_engine.client_jar_path(&details.id);
        if let Some(parent) = dest.parent() {
            fs::create_dir_all(parent).await?;
        }

        // 已存在且 SHA1 匹配则跳过
        if dest.exists() {
            if let Ok(hash) = sha1_of_file(&dest) {
                if hash == client_info.sha1 {
                    log::debug!("客户端 jar 已存在且校验通过: {}", dest.display());
                    return Ok(dest);
                }
            }
            log::info!("客户端 jar 校验失败，重新下载: {}", dest.display());
            fs::remove_file(&dest).await?;
        }

        log::info!(
            "下载客户端 jar: {} ({} bytes)",
            dest.display(),
            client_info.size
        );
        let http = HttpClient::new()?;
        let bytes = http.get_bytes_with_retry(&client_info.url).await?;

        // SHA1 校验
        let actual = format!("{:x}", sha1::Sha1::digest(&bytes));
        if actual != client_info.sha1 {
            return Err(LauncherError::ValidationError {
                expected: client_info.sha1.clone(),
                actual,
            });
        }

        // 原子写入
        let temp = dest.with_extension("tmp");
        let mut file = fs::File::create(&temp).await?;
        file.write_all(&bytes).await?;
        file.flush().await?;
        if dest.exists() {
            fs::remove_file(&dest).await?;
        }
        fs::rename(&temp, &dest).await?;

        Ok(dest)
    }

    /// 客户端 jar 是否已安装且校验通过
    pub fn is_client_jar_installed(&self, version_id: &str, sha1: &str) -> bool {
        let path = self.download_engine.client_jar_path(version_id);
        if !path.exists() {
            return false;
        }
        if sha1.is_empty() {
            return true;
        }
        sha1_of_file(&path).map(|h| h == sha1).unwrap_or(false)
    }

    /// 安装资源索引
    pub async fn install_asset_index(
        &self,
        asset_index: &AssetIndex,
        assets_id: &str,
    ) -> Result<()> {
        let indexes_dir = self.download_engine.assets_dir().join("indexes");
        fs::create_dir_all(&indexes_dir).await?;

        let path = indexes_dir.join(format!("{}.json", assets_id));
        let json = serde_json::to_string_pretty(asset_index)?;
        fs::write(&path, json).await?;

        log::debug!("资源索引 '{}' 已写出", assets_id);
        Ok(())
    }

    /// 删除版本
    pub async fn delete_version(&self, version_id: &str) -> Result<()> {
        let version_dir = self.download_engine.versions_dir().join(version_id);
        if version_dir.exists() {
            fs::remove_dir_all(&version_dir).await?;
            log::info!("已删除版本: {}", version_id);
        }
        Ok(())
    }

    /// 获取版本目录磁盘大小（字节）
    pub async fn version_size(&self, version_id: &str) -> Result<u64> {
        let dir = self.download_engine.versions_dir().join(version_id);
        if !dir.exists() {
            return Ok(0);
        }
        dir_size(&dir).await
    }

    /// 获取版本 JSON 路径
    fn version_json_path(&self, version_id: &str) -> PathBuf {
        self.download_engine
            .versions_dir()
            .join(version_id)
            .join(format!("{}.json", version_id))
    }

    /// 读取已安装的版本详情
    pub async fn read_installed_version(&self, version_id: &str) -> Result<VersionDetails> {
        let path = self.version_json_path(version_id);
        if !path.exists() {
            return Err(LauncherError::VersionNotFound(version_id.to_string()));
        }
        let data = fs::read_to_string(&path).await?;
        let details: VersionDetails = serde_json::from_str(&data)?;
        Ok(details)
    }
}

/// 递归计算目录大小
async fn dir_size(path: &Path) -> Result<u64> {
    let mut total = 0u64;
    let mut entries = fs::read_dir(path).await?;
    while let Some(entry) = entries.next_entry().await? {
        let meta = entry.metadata().await?;
        if meta.is_file() {
            total += meta.len();
        } else if meta.is_dir() {
            total += Box::pin(dir_size(&entry.path())).await?;
        }
    }
    Ok(total)
}

/// 计算文件 SHA1（十六进制小写）
fn sha1_of_file(path: &Path) -> Result<String> {
    let bytes = std::fs::read(path)?;
    Ok(format!("{:x}", sha1::Sha1::digest(&bytes)))
}
