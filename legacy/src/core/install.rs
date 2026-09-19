//! 安装服务 - 版本安装的高层封装

use crate::config::LauncherConfig;
use crate::download::http_client::{HttpClient, RetryConfig};
use crate::download::progress::{ProgressInfo, ProgressStatus, ProgressTracker};
use crate::download::{
    ensure_logging_config, fetch_version_details, fetch_version_manifest, stream_download_file,
    DownloadEngine,
};
use crate::error::{LauncherError, Result};
use crate::modloader::LoaderKind;
use crate::plugin::{PluginContext, PluginManager, PluginStage};
use crate::version::manifest::{AssetIndex, VersionDetails};
use crate::version_manager::VersionInstaller;
use std::io::Read as _;
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

    /// 下载并发数（上限 64，防止极端配置拖垮网络）
    fn concurrency(&self) -> usize {
        self.config.download_concurrency.clamp(1, 64)
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
            .concurrency(self.concurrency())
            .natives_dir(engine.version_natives_dir(&details))
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
                .concurrency(self.concurrency())
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

    /// 下载 Minecraft 服务端 jar 到 `.minecraft/server/<id>/server.jar`
    ///
    /// 带 SHA1 校验 + 流式进度；已存在且校验通过时跳过。
    pub async fn download_server(
        &self,
        version_id: &str,
        progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
    ) -> Result<std::path::PathBuf> {
        let tracker = Arc::new(ProgressTracker::new());
        tracker.on_progress(progress_cb);

        tracker.set_status(ProgressStatus::Downloading);
        tracker.set_stage("获取版本详情");
        let manifest = fetch_version_manifest().await?;
        let entry = manifest
            .versions
            .iter()
            .find(|v| v.id == version_id)
            .ok_or_else(|| LauncherError::VersionNotFound(version_id.to_string()))?;
        let details = fetch_version_details(&entry.url).await?;
        let server = details
            .downloads
            .server
            .clone()
            .ok_or_else(|| LauncherError::DownloadError(format!("版本 {} 没有服务端文件", version_id)))?;

        let dest = self
            .config
            .dot_minecraft
            .join("server")
            .join(version_id)
            .join("server.jar");
        tracker.begin_stage("下载服务端 jar", server.size);
        let client = HttpClient::new()?.with_retry(self.retry_config());
        stream_download_file(&client, &server.url, &server.sha1, &dest, Some(&tracker)).await?;

        tracker.set_status(ProgressStatus::Completed);
        tracker.force_fire();
        log::info!("服务端 jar 已下载: {}", dest.display());
        Ok(dest)
    }

    /// 确保日志配置文件就绪（供启动前解析使用）
    pub async fn ensure_logging_config(&self, details: &VersionDetails) -> Result<()> {
        ensure_logging_config(&self.config.dot_minecraft, details).await
    }

    /// 下载 Mod 文件到 `.minecraft/mods/`（流式 + 进度 + 原子写入）
    pub async fn download_mod_file(
        &self,
        url: &str,
        file_name: &str,
        size: u64,
        progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
    ) -> Result<()> {
        let tracker = Arc::new(ProgressTracker::new());
        tracker.on_progress(progress_cb);
        tracker.set_status(ProgressStatus::Downloading);
        tracker.begin_stage("下载 Mod 文件", size);
        let dest = self.config.dot_minecraft.join("mods").join(file_name);
        let client = HttpClient::new()?.with_retry(self.retry_config());
        stream_download_file(&client, url, "", &dest, Some(&tracker)).await?;
        tracker.set_status(ProgressStatus::Completed);
        tracker.force_fire();
        log::info!("Mod 已下载: {}", dest.display());
        Ok(())
    }

    /// 安装指定加载器（可扩展注册表分发）
    ///
    /// `loader` 为注册表 ID（vanilla/fabric/forge/quilt/neoforge，见 [`LoaderKind`]）。
    /// 加载器版本号自动取该 MC 版本可用的最新版；安装后的版本 ID 固定包含加载器标记
    /// （如 `1.20.1-forge-47.2.0`），此后不可为此版本添加或更改加载器。
    pub async fn install_loader(
        &self,
        mc_version: &str,
        loader: LoaderKind,
        progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
    ) -> Result<String> {
        match loader {
            LoaderKind::Vanilla => {
                self.install_version(mc_version, progress_cb).await?;
                Ok(mc_version.to_string())
            }
            LoaderKind::Fabric => self.install_fabric(mc_version, progress_cb).await,
            LoaderKind::Quilt => self.install_quilt(mc_version, progress_cb).await,
            LoaderKind::Forge => {
                let ver = LoaderKind::Forge.latest_version(mc_version).await?;
                let url = format!(
                    "https://maven.minecraftforge.net/net/minecraftforge/forge/{}-{}/forge-{}-{}-installer.jar",
                    mc_version, ver, mc_version, ver
                );
                self.install_from_installer(mc_version, &url, progress_cb).await
            }
            LoaderKind::NeoForge => {
                let ver = LoaderKind::NeoForge.latest_version(mc_version).await?;
                let url = format!(
                    "https://maven.neoforged.net/releases/net/neoforged/neoforge/{}-{}/neoforge-{}-{}-installer.jar",
                    mc_version, ver, mc_version, ver
                );
                self.install_from_installer(mc_version, &url, progress_cb).await
            }
        }
    }

    /// 安装 Fabric 加载器（复用 modloader 模块的版本查询 + 完整安装流程）
    ///
    /// Fabric 官方提供完整 profile JSON（`inheritsFrom` 指向原版），
    /// 此处将其与父版本合并（内联继承），得到自包含的 version.json，再走常规安装流程。
    pub async fn install_fabric(
        &self,
        mc_version: &str,
        progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
    ) -> Result<String> {
        let tracker = Arc::new(ProgressTracker::new());
        tracker.on_progress(progress_cb);

        // 1. 查询 Fabric Loader 版本并获取 profile JSON
        tracker.set_status(ProgressStatus::Downloading);
        tracker.set_stage("查询 Fabric Loader");
        let loader = crate::modloader::loader::fabric::get_latest_loader(mc_version).await?;
        let profile =
            crate::modloader::loader::fabric::get_profile_json(mc_version, &loader).await?;
        let mut details: VersionDetails = serde_json::from_value(profile)?;
        let fabric_id = details.id.clone();

        // 2. 获取父版本（原版）详情并内联合并
        tracker.set_stage("获取原版版本详情");
        let parent = fetch_parent_details(mc_version).await?;
        merge_inherited_details(&mut details, &parent);

        // 3. 常规安装流程（jar → 库 → 资源 → 索引 → version.json）
        self.install_details(&details, &tracker).await?;

        tracker.set_status(ProgressStatus::Completed);
        tracker.force_fire();
        log::info!("Fabric {} (MC {}) 安装完成", fabric_id, mc_version);
        Ok(fabric_id)
    }

    /// 安装 Quilt 加载器（meta API 提供 profile JSON，与 Fabric 相同的内联合并流程）
    pub async fn install_quilt(
        &self,
        mc_version: &str,
        progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
    ) -> Result<String> {
        let tracker = Arc::new(ProgressTracker::new());
        tracker.on_progress(progress_cb);

        tracker.set_status(ProgressStatus::Downloading);
        tracker.set_stage("查询 Quilt Loader");
        let loader = crate::modloader::loader::quilt::get_latest_loader(mc_version).await?;
        let profile = crate::modloader::loader::quilt::get_profile_json(mc_version, &loader).await?;
        let mut details: VersionDetails = serde_json::from_value(profile)?;
        let quilt_id = details.id.clone();

        tracker.set_stage("获取原版版本详情");
        let parent = fetch_parent_details(mc_version).await?;
        merge_inherited_details(&mut details, &parent);

        self.install_details(&details, &tracker).await?;

        tracker.set_status(ProgressStatus::Completed);
        tracker.force_fire();
        log::info!("Quilt {} (MC {}) 安装完成", quilt_id, mc_version);
        Ok(quilt_id)
    }

    /// 通过官方 installer jar 安装 Forge / NeoForge（无需本机 Java）
    ///
    /// 1. 下载 installer jar（Maven，带缓存）；2. 解压其中的 `version.json`；
    /// 3. 与父版本（原版）内联合并；4. 走常规安装流程。
    async fn install_from_installer(
        &self,
        mc_version: &str,
        installer_url: &str,
        progress_cb: impl Fn(&ProgressInfo) + Send + Sync + 'static,
    ) -> Result<String> {
        let tracker = Arc::new(ProgressTracker::new());
        tracker.on_progress(progress_cb);
        tracker.set_status(ProgressStatus::Downloading);

        // 1. 下载 installer jar（官方 Maven，无 SHA1，已存在即复用缓存）
        tracker.set_stage("下载加载器安装包");
        let client = HttpClient::new()?.with_retry(self.retry_config());
        let file_name = installer_url.rsplit('/').next().unwrap_or("installer.jar");
        let installer_path = self
            .config
            .dot_minecraft
            .join("versions")
            .join(".cache")
            .join(file_name);
        stream_download_file(&client, installer_url, "", &installer_path, Some(&tracker)).await?;

        // 2. 解压 version.json（同步完成，作用域内释放 archive）
        tracker.set_stage("解析加载器安装包");
        let mut details: VersionDetails;
        let loader_id: String;
        {
            let file = std::fs::File::open(&installer_path)?;
            let mut archive = zip::ZipArchive::new(file)?;
            let mut entry = archive.by_name("version.json")?;
            let mut buf = Vec::new();
            entry.read_to_end(&mut buf)?;
            details = serde_json::from_slice(&buf)?;
            loader_id = details.id.clone();
        }

        // 3. 获取父版本（原版）详情并内联合并
        tracker.set_stage("获取原版版本详情");
        let parent = fetch_parent_details(mc_version).await?;
        merge_inherited_details(&mut details, &parent);

        // 4. 常规安装流程（jar → 库 → 资源 → 索引 → version.json）
        self.install_details(&details, &tracker).await?;

        tracker.set_status(ProgressStatus::Completed);
        tracker.force_fire();
        log::info!("{} (MC {}) 安装完成", loader_id, mc_version);
        Ok(loader_id)
    }

    /// 常规安装流程：客户端 jar → 库（含 native 解压）→ 资源 → 索引 → version.json → 日志配置
    async fn install_details(
        &self,
        details: &VersionDetails,
        tracker: &Arc<ProgressTracker>,
    ) -> Result<()> {
        let engine = DownloadEngine::new(&self.config.dot_minecraft);
        let installer = VersionInstaller::new(engine.clone());

        tracker.set_stage("下载客户端 jar");
        installer.install_client_jar(details).await?;

        tracker.set_stage("下载库文件");
        let libs = engine
            .library_downloader()
            .concurrency(self.concurrency())
            .natives_dir(engine.version_natives_dir(&details))
            .with_tracker(tracker.clone())
            .download_libraries(&details.libraries)
            .await?;
        log::info!("库文件就绪: {} 个 classpath 条目", libs.len());

        if let Some(ref ai) = details.asset_index {
            tracker.set_stage("下载资源索引");
            let client = HttpClient::new()?.with_retry(self.retry_config());
            let index: AssetIndex = client.get_json_with_retry(&ai.url).await?;
            let total = index.objects.len() as u64;
            tracker.begin_stage("下载资源文件", total);
            engine
                .asset_downloader()
                .concurrency(self.concurrency())
                .with_tracker(tracker.clone())
                .download_assets(&index)
                .await?;
            let assets_id = details.assets.as_deref().unwrap_or(&details.id);
            installer.install_asset_index(&index, assets_id).await?;
        }

        installer.install_version(details).await?;
        ensure_logging_config(&self.config.dot_minecraft, details).await?;
        Ok(())
    }
}

/// 获取原版父版本详情（供各加载器内联合并使用）
async fn fetch_parent_details(mc_version: &str) -> Result<VersionDetails> {
    let manifest = fetch_version_manifest().await?;
    let entry = manifest
        .versions
        .iter()
        .find(|v| v.id == mc_version)
        .ok_or_else(|| LauncherError::VersionNotFound(mc_version.to_string()))?;
    fetch_version_details(&entry.url).await
}

/// 将父版本详情内联合并进 Fabric 子版本（去掉 inheritsFrom 语义）
///
/// - 库：并集去重（按 name），子版本优先
/// - 参数：子版本没有 `arguments` 时继承父级；老版本 `minecraftArguments` 同理
/// - 客户端 jar / 资源索引 / Java 版本 / 日志配置：子版本缺失时从父级补齐
/// - `main_class`：保留子版本（Fabric 的 KnotClient）
fn merge_inherited_details(child: &mut VersionDetails, parent: &VersionDetails) {
    // 库：父 ∪ 子（去重，子在前）
    let mut seen: std::collections::HashSet<String> =
        child.libraries.iter().map(|l| l.name.clone()).collect();
    for lib in &parent.libraries {
        if seen.insert(lib.name.clone()) {
            child.libraries.push(lib.clone());
        }
    }

    // 参数：子版本有则用子版本，否则继承父级
    if child.arguments.is_none() {
        child.arguments = parent.arguments.clone();
    }
    if child.minecraft_arguments.is_none() {
        child.minecraft_arguments = parent.minecraft_arguments.clone();
    }

    // 客户端 jar：Fabric profile 无 downloads，继承父级
    if child.downloads.client.is_none() {
        child.downloads = parent.downloads.clone();
    }
    if child.asset_index.is_none() {
        child.asset_index = parent.asset_index.clone();
    }
    if child.assets.is_none() {
        child.assets = parent.assets.clone();
    }
    if child.java_version.is_none() {
        child.java_version = parent.java_version.clone();
    }
    // 日志配置：父级未空时补齐
    if child.logging.client.file.id.is_empty() {
        child.logging = parent.logging.clone();
    }
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::version::manifest::*;

    /// 构造一个含 `inheritsFrom` 语义的 Fabric 子版本（大部分字段缺失）
    fn fabric_child() -> VersionDetails {
        serde_json::from_value(serde_json::json!({
            "id": "fabric-loader-0.16.9-1.21.4",
            "mainClass": "net.fabricmc.loader.impl.launch.knot.KnotClient",
            "libraries": [
                { "name": "net.fabricmc:fabric-loader:0.16.9" },
                { "name": "net.fabricmc:intermediary:1.21.4" }
            ],
            "minimumLauncherVersion": 21,
            "releaseTime": "2025-01-01T00:00:00Z",
            "time": "2025-01-01T00:00:00Z",
            "type": "release",
            "downloads": {},
            "logging": { "client": { "argument": "", "file": { "id": "", "sha1": "", "size": 0, "url": "" }, "type": "" } }
        }))
        .unwrap()
    }

    /// 构造原版父版本（含完整库/参数/下载）
    fn vanilla_parent() -> VersionDetails {
        serde_json::from_value(serde_json::json!({
            "id": "1.21.4",
            "mainClass": "net.minecraft.client.main.Main",
            "libraries": [
                { "name": "com.mojang:brigadier:1.3.0" },
                { "name": "net.fabricmc:intermediary:1.21.4" }
            ],
            "arguments": {
                "game": ["--gameDir", "${game_directory}"],
                "jvm": ["-Xmx${max_memory}"]
            },
            "minecraftArguments": null,
            "downloads": {
                "client": { "sha1": "abc", "size": 100, "url": "https://example.com/client.jar" }
            },
            "assetIndex": { "id": "21", "sha1": "def", "size": 50, "totalSize": 1000, "url": "https://example.com/index.json" },
            "assets": "21",
            "javaVersion": { "component": "java-runtime-delta", "majorVersion": 21 },
            "logging": {
                "client": {
                    "argument": "-Dlog4j.configurationFile=${path}",
                    "file": { "id": "client-1.12.xml", "sha1": "xyz", "size": 200, "url": "https://example.com/log.xml" },
                    "type": "log4j2-xml"
                }
            },
            "minimumLauncherVersion": 21,
            "releaseTime": "2024-12-03T00:00:00Z",
            "time": "2024-12-03T00:00:00Z",
            "type": "release"
        }))
        .unwrap()
    }

    #[test]
    fn merge_union_libraries_dedup() {
        let mut child = fabric_child();
        let parent = vanilla_parent();
        merge_inherited_details(&mut child, &parent);

        let names: Vec<&str> = child.libraries.iter().map(|l| l.name.as_str()).collect();
        // 子版本库在前，父版本补入，重复的 intermediary 只保留一次
        assert!(names.starts_with(&[
            "net.fabricmc:fabric-loader:0.16.9",
            "net.fabricmc:intermediary:1.21.4",
            "com.mojang:brigadier:1.3.0"
        ]));
        assert_eq!(child.libraries.len(), 3);
    }

    #[test]
    fn merge_inherits_arguments_and_downloads() {
        let mut child = fabric_child();
        let parent = vanilla_parent();
        merge_inherited_details(&mut child, &parent);

        // 子版本无 arguments，继承父级
        assert!(child.arguments.is_some());
        // 客户端 jar / 资源索引 / Java 版本从父级补齐
        assert!(child.downloads.client.is_some());
        assert!(child.asset_index.is_some());
        assert_eq!(child.assets.as_deref(), Some("21"));
        assert!(child.java_version.is_some());
        // 日志配置补齐
        assert_eq!(child.logging.client.file.id, "client-1.12.xml");
        // 主类保留子版本（Fabric KnotClient）
        assert_eq!(
            child.main_class,
            "net.fabricmc.loader.impl.launch.knot.KnotClient"
        );
    }

    #[test]
    fn merge_keeps_child_precedence() {
        let mut child = fabric_child();
        let parent = vanilla_parent();
        // 子版本显式提供 arguments 时应优先保留
        let explicit: VersionArguments = serde_json::from_value(serde_json::json!({
            "game": ["--fabric-only"],
            "jvm": []
        }))
        .unwrap();
        child.arguments = Some(explicit.clone());
        merge_inherited_details(&mut child, &parent);
        assert!(matches!(
            child.arguments.as_ref().unwrap().game.first(),
            Some(ArgumentValue::Simple(s)) if s == "--fabric-only"
        ));
    }
}
