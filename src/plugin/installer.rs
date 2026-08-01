use std::path::{Path, PathBuf};
use reqwest::header::{HeaderValue, CONTENT_TYPE};
use tokio::fs;
use tokio::io::AsyncWriteExt;
use crate::error::{LauncherError, Result};

/// 插件安装源类型
#[derive(Debug, Clone)]
pub enum PluginSource {
    /// 本地文件路径
    Local(PathBuf),
    /// 远程 URL
    Remote(String),
    /// 插件注册表（通过 ID 查找）
    Registry { plugin_id: String, registry_url: String },
}

/// 插件安装器 - 支持从多种来源安装插件
pub struct PluginInstaller {
    /// 插件安装目标目录
    plugins_dir: PathBuf,
}

impl PluginInstaller {
    /// 创建插件安装器
    pub fn new(plugins_dir: impl Into<PathBuf>) -> Self {
        Self {
            plugins_dir: plugins_dir.into(),
        }
    }

    /// 安装插件
    pub async fn install(&self, source: &PluginSource) -> Result<PathBuf> {
        match source {
            PluginSource::Local(path) => self.install_from_local(path).await,
            PluginSource::Remote(url) => self.install_from_remote(url).await,
            PluginSource::Registry { plugin_id, registry_url } => {
                self.install_from_registry(plugin_id, registry_url).await
            }
        }
    }

    /// 从本地文件安装
    async fn install_from_local(&self, path: &Path) -> Result<PathBuf> {
        if !path.exists() {
            return Err(LauncherError::PluginNotFound(format!(
                "插件文件不存在: {}", path.display()
            )));
        }

        let file_name = path
            .file_name()
            .ok_or_else(|| LauncherError::PluginError("无效的文件路径".to_string()))?;

        let dest = self.plugins_dir.join(file_name);
        fs::create_dir_all(&self.plugins_dir).await?;
        fs::copy(path, &dest).await?;

        log::info!("从本地安装插件: {}", dest.display());
        Ok(dest)
    }

    /// 从远程 URL 安装
    async fn install_from_remote(&self, url: &str) -> Result<PathBuf> {
        let client = reqwest::Client::new();

        let response = client
            .get(url)
            .timeout(std::time::Duration::from_secs(120))
            .send()
            .await?;

        if !response.status().is_success() {
            return Err(LauncherError::DownloadError(format!(
                "下载插件失败: HTTP {}",
                response.status()
            )));
        }

        // 从 URL 或 Content-Disposition 头提取文件名
        let file_name = extract_filename_from_url(url).unwrap_or_else(|| {
            uuid::Uuid::new_v4().to_string() + ".dll"
        });

        let bytes = response.bytes().await?;
        let dest = self.plugins_dir.join(&file_name);

        fs::create_dir_all(&self.plugins_dir).await?;
        let mut file = fs::File::create(&dest).await?;
        file.write_all(&bytes).await?;
        file.flush().await?;

        log::info!("从远程安装插件: {} -> {}", url, dest.display());
        Ok(dest)
    }

    /// 从插件注册表安装
    async fn install_from_registry(&self, plugin_id: &str, registry_url: &str) -> Result<PathBuf> {
        let client = reqwest::Client::new();

        // 请求注册表查找插件
        let url = format!("{}/plugins/{}", registry_url, plugin_id);
        let response = client
            .get(&url)
            .header(CONTENT_TYPE, HeaderValue::from_static("application/json"))
            .timeout(std::time::Duration::from_secs(30))
            .send()
            .await?;

        if !response.status().is_success() {
            return Err(LauncherError::PluginNotFound(format!(
                "在注册表中未找到插件: {}", plugin_id
            )));
        }

        #[derive(serde::Deserialize)]
        struct RegistryEntry {
            download_url: String,
            name: String,
            version: String,
        }

        let entry: RegistryEntry = response.json().await?;

        log::info!("在注册表中找到插件: {} v{}", entry.name, entry.version);

        self.install_from_remote(&entry.download_url).await
    }

    /// 列出已安装的插件文件
    pub async fn list_installed(&self) -> Result<Vec<PathBuf>> {
        if !self.plugins_dir.exists() {
            return Ok(Vec::new());
        }

        let mut entries = fs::read_dir(&self.plugins_dir).await?;
        let mut files = Vec::new();

        while let Some(entry) = entries.next_entry().await? {
            let path = entry.path();
            if path.is_file() {
                let ext = path.extension().map(|e| e.to_string_lossy().to_lowercase());
                let valid_ext = matches!(
                    ext.as_deref(),
                    Some("dll") | Some("so") | Some("dylib")
                );
                if valid_ext {
                    files.push(path);
                }
            }
        }

        Ok(files)
    }

    /// 卸载（删除）插件文件
    pub async fn uninstall(&self, path: &Path) -> Result<()> {
        if !path.exists() {
            return Err(LauncherError::PluginNotFound(format!(
                "插件文件不存在: {}", path.display()
            )));
        }
        fs::remove_file(path).await?;
        log::info!("已卸载插件文件: {}", path.display());
        Ok(())
    }

    /// 按插件 ID 卸载
    pub async fn uninstall_by_id(&self, plugin_id: &str) -> Result<()> {
        let installed = self.list_installed().await?;
        for path in installed {
            let stem = path
                .file_stem()
                .unwrap_or_default()
                .to_string_lossy();
            if stem.contains(plugin_id) {
                return self.uninstall(&path).await;
            }
        }
        Err(LauncherError::PluginNotFound(format!(
            "未找到已安装的插件: {}", plugin_id
        )))
    }
}

/// 从 URL 提取文件名
fn extract_filename_from_url(url: &str) -> Option<String> {
    // 去掉 query 参数
    let base = url.split('?').next().unwrap_or(url);
    // 取最后一段
    let segments: Vec<&str> = base.rsplitn(2, '/').collect();
    let name = segments.first()?;
    if name.is_empty() {
        None
    } else {
        Some(name.to_string())
    }
}
