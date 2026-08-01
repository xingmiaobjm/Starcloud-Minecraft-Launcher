//! 安装服务 - 版本安装的高层封装

use crate::config::LauncherConfig;
use crate::download::http_client::{HttpClient, RetryConfig};
use crate::download::progress::{ProgressInfo, ProgressStatus, ProgressTracker};
use crate::download::{
    ensure_logging_config, fetch_version_details, fetch_version_manifest, DownloadEngine,
};
use crate::error::{LauncherError, Result};
use crate::plugin::{PluginContext, PluginManager, PluginStage};
use crate::version::manifest::{AssetIndex, VersionDetails};
use crate::version_manager::VersionInstaller;
use std::sync::Arc;

/// 版本安装服务 - 完整安装流程（清单→详情→jar→库→资源→索引→version.json→插件事件）
pub struct InstallService {
    config: LauncherConfig,
    plugin_manager: Option<Arc<PluginManager>>,
}

impl InstallService {
    /// 创建安装服务
    pub fn new(config: LauncherConfig) -> Self {
        Self {
            config,
            plugin_manager: None,
        }
    }

    /// 关联插件管理器（事件广播）
    pub fn with_plugin_manager(mut self, plugin_manager: Arc<PluginManager>) -> Self {
        self.plugin_manager = Some(plugin_manager);
        self
    }

    /// 获取配置（只读，供内部使用）
    pub fn config(&self) -> &LauncherConfig {
        &self.config
    }

    /// 根据配置构建重试策略
    fn retry_config(&self) -> RetryConfig {
        RetryConfig {
            max_retries: self.config.download_max_retries.max(1),
            ..RetryConfig::default()
        }
    }

    /// 广播插件事件（失败仅记录警告，不影响主流程）
    fn fire(&self, stage: PluginStage) {
        if let Some(pm) = &self.plugin_manager {
            let mut ctx = PluginContext::new(&self.config.dot_minecraft);
            if let Err(e) = pm.fire_event(stage, &mut ctx) {
                log::warn!("插件事件广播失败: {}", e);
            }
        }
    }

    /// 完整安装版本
    ///
    /// `progress_cb` 接收进度快照（节流 ≥100ms，终态强制触发）。
    pub async fn install_version(
        &self,
        version_id: &str,
        progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
    ) -> Result<()> {
        let engine = DownloadEngine::new(&self.config.dot_minecraft);
        let tracker = Arc::new(ProgressTracker::new());
        tracker.on_progress(progress_cb);

        self.fire(PluginStage::PreVersionManifest);

        // 1. 版本清单
        tracker.set_status(ProgressStatus::Downloading);
        tracker.set_stage("获取版本清单");
        log::info!("开始安装 Minecraft {}", version_id);
        let manifest = fetch_version_manifest().await?;
        let entry = manifest
            .versions
            .iter()
            .find(|v| v.id == version_id)
            .ok_or_else(|| LauncherError::VersionNotFound(version_id.to_string()))?;

        // 2. 版本详情
        tracker.set_stage("获取版本详情");
        let details = fetch_version_details(&entry.url).await?;
        log::debug!("版本详情: {} 库 {} 个", details.id, details.libraries.len());
        self.fire(PluginStage::PostVersionDetails);

        // 3. 客户端 jar
        tracker.begin_stage("下载客户端 jar", 1);
        let installer = VersionInstaller::new(DownloadEngine::new(&self.config.dot_minecraft));
        installer.install_client_jar(&details).await?;

        // 4. 库文件（含 native 解压）
        tracker.set_stage("下载库文件");
        let libs = engine
            .library_downloader()
            .concurrency(self.config.download_concurrency.max(1))
            .natives_dir(engine.natives_dir(&details.id))
            .with_tracker(tracker.clone())
            .download_libraries(&details.libraries)
            .await?;
        log::info!("库文件就绪: {} 个 classpath 条目", libs.len());

        // 5. 资源文件
        if let Some(ref ai) = details.asset_index {
            tracker.set_stage("下载资源索引");
            let client = HttpClient::new()?.with_retry(self.retry_config());
            let index: AssetIndex = client.get_json_with_retry(&ai.url).await?;
            let total = index.objects.len() as u64;
            tracker.begin_stage("下载资源文件", total);
            engine
                .asset_downloader()
                .with_tracker(tracker.clone())
                .download_assets(&index)
                .await?;
            log::info!("资源文件就绪: {} 个", total);

            // 6. 资源索引
            let assets_id = details.assets.as_deref().unwrap_or(&details.id);
            installer.install_asset_index(&index, assets_id).await?;
        }

        // 7. version.json
        installer.install_version(&details).await?;

        // 8. 日志配置文件（log4j2，供 ${path} 替换）
        ensure_logging_config(&self.config.dot_minecraft, &details).await?;

        tracker.set_status(ProgressStatus::Completed);
        tracker.force_fire();
        self.fire(PluginStage::VersionInstalled);
        log::info!("Minecraft {} 安装完成", version_id);
        Ok(())
    }

    /// 仅下载客户端 jar（供启动前 EnsureInstalled 使用）
    pub async fn ensure_client_jar(&self, details: &VersionDetails) -> Result<()> {
        let installer = VersionInstaller::new(DownloadEngine::new(&self.config.dot_minecraft));
        installer.install_client_jar(details).await?;
        Ok(())
    }

    /// 确保日志配置文件就绪（供启动前解析使用）
    pub async fn ensure_logging_config(&self, details: &VersionDetails) -> Result<()> {
        ensure_logging_config(&self.config.dot_minecraft, details).await
    }
}
