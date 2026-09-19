use crate::download::http_client::HttpClient;
use crate::error::{LauncherError, Result};
use std::path::{Path, PathBuf};
use tokio::fs;

/// 插件安装源类型
#[derive(Debug, Clone)]
pub enum PluginSource {
    /// 本地文件路径
    Local(PathBuf),
    /// 远程 URL
    Remote(String),
    /// 插件注册表（通过 ID 查找）
    Registry {
        plugin_id: String,
        registry_url: String,
    },
}

/// 已安装插件的清单条目（用于精确卸载）
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct PluginManifestEntry {
    /// 插件 ID（动态库内嵌，安装后不可知时为空）
    pub plugin_id: Option<String>,
    /// 插件文件名
    pub file_name: String,
    /// 插件版本（未知时为空）
    pub version: Option<String>,
    /// 安装时间（RFC3339）
    pub installed_at: String,
}

/// 插件安装器 - 支持从多种来源安装插件，并维护安装清单
pub struct PluginInstaller {
    /// 插件安装目标目录
    plugins_dir: PathBuf,
    /// HTTP 客户端（带重试）
    http: HttpClient,
}

impl PluginInstaller {
    /// 创建插件安装器
    pub fn new(plugins_dir: impl Into<PathBuf>) -> Self {
        let http = HttpClient::new().unwrap_or_else(|e| {
            // HttpClient 构建失败属于极端情况，退回默认 reqwest 客户端
            log::error!("创建 HTTP 客户端失败: {}", e);
            panic!("无法创建 HTTP 客户端: {}", e);
        });
        Self {
            plugins_dir: plugins_dir.into(),
            http,
        }
    }

    /// 安装清单文件路径
    fn manifest_path(&self) -> PathBuf {
        self.plugins_dir.join("manifest.json")
    }

    /// 读取安装清单（文件不存在返回空列表）
    async fn load_manifest(&self) -> Result<Vec<PluginManifestEntry>> {
        let path = self.manifest_path();
        if !path.exists() {
            return Ok(Vec::new());
        }
        let content = fs::read_to_string(&path).await?;
        let entries: Vec<PluginManifestEntry> = serde_json::from_str(&content)
            .map_err(|e| LauncherError::PluginError(format!("解析插件清单失败: {}", e)))?;
        Ok(entries)
    }

    /// 写入安装清单（原子）
    async fn save_manifest(&self, entries: &[PluginManifestEntry]) -> Result<()> {
        fs::create_dir_all(&self.plugins_dir).await?;
        let content = serde_json::to_string_pretty(entries)
            .map_err(|e| LauncherError::PluginError(format!("序列化插件清单失败: {}", e)))?;
        let path = self.manifest_path();
        let tmp = path.with_extension("json.tmp");
        fs::write(&tmp, content).await?;
        if cfg!(windows) {
            let _ = fs::remove_file(&path).await;
        }
        fs::rename(&tmp, &path).await?;
        Ok(())
    }

    /// 记录安装条目（追加或按文件名覆盖）
    async fn record_install(&self, entry: PluginManifestEntry) -> Result<()> {
        let mut entries = self.load_manifest().await?;
        if let Some(existing) = entries.iter_mut().find(|e| e.file_name == entry.file_name) {
            *existing = entry;
        } else {
            entries.push(entry);
        }
        self.save_manifest(&entries).await
    }

    /// 安装插件
    pub async fn install(&self, source: &PluginSource) -> Result<PathBuf> {
        match source {
            PluginSource::Local(path) => self.install_from_local(path).await,
            PluginSource::Remote(url) => self.install_from_remote(url).await,
            PluginSource::Registry {
                plugin_id,
                registry_url,
            } => self.install_from_registry(plugin_id, registry_url).await,
        }
    }

    /// 从本地文件安装
    async fn install_from_local(&self, path: &Path) -> Result<PathBuf> {
        if !path.exists() {
            return Err(LauncherError::PluginNotFound(format!(
                "插件文件不存在: {}",
                path.display()
            )));
        }

        let file_name = path
            .file_name()
            .ok_or_else(|| LauncherError::PluginError("无效的文件路径".to_string()))?
            .to_string_lossy()
            .into_owned();

        let dest = self.plugins_dir.join(&file_name);
        fs::create_dir_all(&self.plugins_dir).await?;
        fs::copy(path, &dest).await?;

        self.record_install(PluginManifestEntry {
            plugin_id: None,
            file_name,
            version: None,
            installed_at: chrono::Utc::now().to_rfc3339(),
        })
        .await?;

        log::info!("从本地安装插件: {}", dest.display());
        Ok(dest)
    }

    /// 从远程 URL 安装（走 HttpClient 重试链路）
    async fn install_from_remote(&self, url: &str) -> Result<PathBuf> {
        // 从 URL 提取文件名
        let file_name = extract_filename_from_url(url)
            .unwrap_or_else(|| uuid::Uuid::new_v4().to_string() + ".dll");

        let bytes = self.http.get_bytes_with_retry(url).await?;

        let dest = self.plugins_dir.join(&file_name);
        fs::create_dir_all(&self.plugins_dir).await?;
        fs::write(&dest, bytes).await?;

        self.record_install(PluginManifestEntry {
            plugin_id: None,
            file_name,
            version: None,
            installed_at: chrono::Utc::now().to_rfc3339(),
        })
        .await?;

        log::info!("从远程安装插件: {} -> {}", url, dest.display());
        Ok(dest)
    }

    /// 从插件注册表安装
    async fn install_from_registry(&self, plugin_id: &str, registry_url: &str) -> Result<PathBuf> {
        // 请求注册表查找插件
        let url = format!(
            "{}/plugins/{}",
            registry_url.trim_end_matches('/'),
            plugin_id
        );
        let response = self.http.get_with_retry(&url).await?;

        if !response.status().is_success() {
            return Err(LauncherError::PluginNotFound(format!(
                "在注册表中未找到插件: {}",
                plugin_id
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

        let dest = self.install_from_remote(&entry.download_url).await?;
        // 补充清单中的插件 ID 与版本
        if let Some(file_name) = dest.file_name().map(|s| s.to_string_lossy().into_owned()) {
            let mut entries = self.load_manifest().await?;
            if let Some(existing) = entries.iter_mut().find(|e| e.file_name == file_name) {
                existing.plugin_id = Some(plugin_id.to_string());
                existing.version = Some(entry.version);
            }
            self.save_manifest(&entries).await?;
        }
        Ok(dest)
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
                let valid_ext = matches!(ext.as_deref(), Some("dll") | Some("so") | Some("dylib"));
                if valid_ext {
                    files.push(path);
                }
            }
        }

        Ok(files)
    }

    /// 卸载（删除）插件文件并同步清理清单
    pub async fn uninstall(&self, path: &Path) -> Result<()> {
        if !path.exists() {
            return Err(LauncherError::PluginNotFound(format!(
                "插件文件不存在: {}",
                path.display()
            )));
        }
        // 先删除文件，成功后再更新清单
        fs::remove_file(path).await?;

        let file_name = path
            .file_name()
            .map(|s| s.to_string_lossy().into_owned())
            .unwrap_or_default();
        let mut entries = self.load_manifest().await?;
        entries.retain(|e| e.file_name != file_name);
        self.save_manifest(&entries).await?;

        log::info!("已卸载插件文件: {}", path.display());
        Ok(())
    }

    /// 按插件 ID 卸载（精确匹配清单中的 plugin_id，兼容旧文件名匹配）
    pub async fn uninstall_by_id(&self, plugin_id: &str) -> Result<()> {
        // 1. 精确匹配清单
        let entries = self.load_manifest().await?;
        if let Some(entry) = entries
            .iter()
            .find(|e| e.plugin_id.as_deref() == Some(plugin_id))
        {
            let path = self.plugins_dir.join(&entry.file_name);
            if path.exists() {
                return self.uninstall(&path).await;
            }
            // 文件已丢失，仅清理清单
            let mut entries = entries;
            entries.retain(|e| e.plugin_id.as_deref() != Some(plugin_id));
            self.save_manifest(&entries).await?;
            log::info!("已清理清单中失效的插件记录: {}", plugin_id);
            return Ok(());
        }

        // 2. 回退：文件名包含匹配（旧版本插件无清单记录）
        let installed = self.list_installed().await?;
        for path in installed {
            let stem = path.file_stem().unwrap_or_default().to_string_lossy();
            if stem.contains(plugin_id) {
                return self.uninstall(&path).await;
            }
        }

        Err(LauncherError::PluginNotFound(format!(
            "未找到已安装的插件: {}",
            plugin_id
        )))
    }

    /// 获取安装清单（供 GUI/CLI 展示精确信息）
    pub async fn manifest(&self) -> Result<Vec<PluginManifestEntry>> {
        self.load_manifest().await
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

#[cfg(test)]
mod tests {
    use super::*;

    #[tokio::test]
    async fn test_local_install_and_uninstall_by_id() {
        let dir = tempfile::tempdir().unwrap();
        let installer = PluginInstaller::new(dir.path().join("plugins"));

        // 构造本地插件文件
        let src = dir.path().join("my-plugin.dll");
        std::fs::write(&src, b"fake dll content").unwrap();

        let dest = installer
            .install(&PluginSource::Local(src.clone()))
            .await
            .unwrap();
        assert!(dest.exists());
        assert_eq!(installer.list_installed().await.unwrap().len(), 1);

        // 通过文件名卸载
        let installed = installer.list_installed().await.unwrap();
        installer.uninstall(&installed[0]).await.unwrap();
        assert!(installer.list_installed().await.unwrap().is_empty());
        assert!(installer.manifest().await.unwrap().is_empty());
    }

    #[tokio::test]
    async fn test_uninstall_by_id_fallback() {
        let dir = tempfile::tempdir().unwrap();
        let installer = PluginInstaller::new(dir.path().join("plugins"));

        // 无清单记录的文件（模拟旧版本安装），文件名包含插件 ID
        std::fs::create_dir_all(dir.path().join("plugins")).unwrap();
        std::fs::write(
            dir.path().join("plugins").join("com-example-hello.dll"),
            b"content",
        )
        .unwrap();

        installer
            .uninstall_by_id("com-example-hello")
            .await
            .unwrap();
        assert!(installer.list_installed().await.unwrap().is_empty());

        // 不存在的插件报错
        assert!(installer
            .uninstall_by_id("nonexistent-plugin")
            .await
            .is_err());
    }
}
