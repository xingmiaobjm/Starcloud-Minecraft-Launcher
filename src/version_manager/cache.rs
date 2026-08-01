use crate::error::{LauncherError, Result};
use crate::version::manifest::{VersionDetails, VersionManifest};
use lru::LruCache;
use std::num::NonZero;
use std::path::PathBuf;
use std::sync::Arc;
use std::sync::Mutex;
use tokio::fs;

/// 版本缓存（内存 LRU + 文件持久化）
pub struct VersionCache {
    /// .minecraft 目录
    dot_minecraft: PathBuf,
    /// 内存缓存（LRU 淘汰）
    memory_cache: Arc<Mutex<LruCache<String, CachedEntry>>>,
    /// 最大内存缓存条目数
    max_memory_entries: usize,
}

#[derive(Clone)]
#[allow(dead_code)]
#[allow(clippy::large_enum_variant)] // Details 天然较大，缓存场景可接受
enum CachedEntry {
    Manifest(VersionManifest),
    Details(VersionDetails),
    Index(crate::version::manifest::AssetIndex),
}

impl VersionCache {
    /// 创建版本缓存
    pub fn new(dot_minecraft: impl Into<PathBuf>, max_memory_entries: usize) -> Self {
        Self {
            dot_minecraft: dot_minecraft.into(),
            memory_cache: Arc::new(Mutex::new(LruCache::new(
                NonZero::new(max_memory_entries.max(1)).unwrap(),
            ))),
            max_memory_entries,
        }
    }

    /// 获取缓存目录
    fn cache_dir(&self) -> PathBuf {
        self.dot_minecraft.join("cache")
    }

    /// 获取清单缓存文件路径
    fn manifest_cache_path(&self) -> PathBuf {
        self.cache_dir().join("version_manifest.json")
    }

    /// 读取版本清单（优先内存缓存 → 文件缓存 → 网络）
    pub async fn get_manifest<F, Fut>(&self, fetch: F) -> Result<VersionManifest>
    where
        F: FnOnce() -> Fut,
        Fut: std::future::Future<Output = Result<VersionManifest>>,
    {
        let key = "__manifest__".to_string();

        // 1. 内存缓存
        {
            let mut cache = self
                .memory_cache
                .lock()
                .map_err(|e| LauncherError::Other(e.to_string()))?;
            if let Some(CachedEntry::Manifest(m)) = cache.get(&key) {
                log::debug!("版本清单命中内存缓存");
                return Ok(m.clone());
            }
        }

        // 2. 文件缓存
        if let Ok(manifest) = self.load_manifest_from_disk().await {
            let mut cache = self
                .memory_cache
                .lock()
                .map_err(|e| LauncherError::Other(e.to_string()))?;
            cache.put(key.clone(), CachedEntry::Manifest(manifest.clone()));
            log::debug!("版本清单命中文件缓存");
            return Ok(manifest);
        }

        // 3. 网络获取
        let manifest = fetch().await?;
        self.save_manifest_to_disk(&manifest).await?;

        let mut cache = self
            .memory_cache
            .lock()
            .map_err(|e| LauncherError::Other(e.to_string()))?;
        cache.put(key, CachedEntry::Manifest(manifest.clone()));

        Ok(manifest)
    }

    /// 读取版本详情（优先内存缓存 → 文件缓存 → 自定义获取）
    pub async fn get_version_details<F, Fut>(
        &self,
        version_id: &str,
        fetch: F,
    ) -> Result<VersionDetails>
    where
        F: FnOnce() -> Fut,
        Fut: std::future::Future<Output = Result<VersionDetails>>,
    {
        let key = format!("version_{}", version_id);

        // 1. 内存
        {
            let mut cache = self
                .memory_cache
                .lock()
                .map_err(|e| LauncherError::Other(e.to_string()))?;
            if let Some(CachedEntry::Details(d)) = cache.get(&key) {
                log::debug!("版本详情 '{}' 命中内存缓存", version_id);
                return Ok(d.clone());
            }
        }

        // 2. 文件
        if let Ok(details) = self.load_version_details_from_disk(version_id).await {
            let mut cache = self
                .memory_cache
                .lock()
                .map_err(|e| LauncherError::Other(e.to_string()))?;
            cache.put(key.clone(), CachedEntry::Details(details.clone()));
            log::debug!("版本详情 '{}' 命中文件缓存", version_id);
            return Ok(details);
        }

        // 3. 网络
        let details = fetch().await?;
        self.save_version_details_to_disk(version_id, &details)
            .await?;

        let mut cache = self
            .memory_cache
            .lock()
            .map_err(|e| LauncherError::Other(e.to_string()))?;
        cache.put(key, CachedEntry::Details(details.clone()));

        Ok(details)
    }

    /// 缓存版本详情
    pub async fn cache_version_details(
        &self,
        version_id: &str,
        details: &VersionDetails,
    ) -> Result<()> {
        let key = format!("version_{}", version_id);
        self.save_version_details_to_disk(version_id, details)
            .await?;

        let mut cache = self
            .memory_cache
            .lock()
            .map_err(|e| LauncherError::Other(e.to_string()))?;
        cache.put(key, CachedEntry::Details(details.clone()));
        Ok(())
    }

    /// 失效某个缓存
    pub fn invalidate(&self, key: &str) -> Result<()> {
        let mut cache = self
            .memory_cache
            .lock()
            .map_err(|e| LauncherError::Other(e.to_string()))?;
        cache.pop(key);
        Ok(())
    }

    /// 清空所有内存缓存
    pub fn clear_memory(&self) -> Result<()> {
        let mut cache = self
            .memory_cache
            .lock()
            .map_err(|e| LauncherError::Other(e.to_string()))?;
        cache.clear();
        Ok(())
    }

    /// 清空文件缓存
    pub async fn clear_disk_cache(&self) -> Result<()> {
        let cache_dir = self.cache_dir();
        if cache_dir.exists() {
            fs::remove_dir_all(&cache_dir).await?;
        }
        fs::create_dir_all(&cache_dir).await?;
        Ok(())
    }

    // --- 内部实现 ---

    async fn ensure_cache_dir(&self) -> Result<PathBuf> {
        let dir = self.cache_dir();
        fs::create_dir_all(&dir).await?;
        Ok(dir)
    }

    async fn load_manifest_from_disk(&self) -> Result<VersionManifest> {
        let path = self.manifest_cache_path();
        if !path.exists() {
            return Err(LauncherError::Other("缓存未命中".to_string()));
        }
        let data = fs::read_to_string(&path).await?;
        let manifest: VersionManifest = serde_json::from_str(&data)?;
        Ok(manifest)
    }

    async fn save_manifest_to_disk(&self, manifest: &VersionManifest) -> Result<()> {
        let dir = self.ensure_cache_dir().await?;
        let path = dir.join("version_manifest.json");
        let json = serde_json::to_string_pretty(manifest)?;
        fs::write(&path, json).await?;
        Ok(())
    }

    async fn load_version_details_from_disk(&self, version_id: &str) -> Result<VersionDetails> {
        let path = self
            .cache_dir()
            .join("versions")
            .join(format!("{}.json", version_id));
        if !path.exists() {
            return Err(LauncherError::Other("缓存未命中".to_string()));
        }
        let data = fs::read_to_string(&path).await?;
        let details: VersionDetails = serde_json::from_str(&data)?;
        Ok(details)
    }

    async fn save_version_details_to_disk(
        &self,
        version_id: &str,
        details: &VersionDetails,
    ) -> Result<()> {
        let dir = self.ensure_cache_dir().await?.join("versions");
        fs::create_dir_all(&dir).await?;
        let path = dir.join(format!("{}.json", version_id));
        let json = serde_json::to_string_pretty(details)?;
        fs::write(&path, json).await?;
        Ok(())
    }

    /// 缓存统计
    pub fn stats(&self) -> Result<CacheStats> {
        let cache = self
            .memory_cache
            .lock()
            .map_err(|e| LauncherError::Other(e.to_string()))?;
        Ok(CacheStats {
            memory_entries: cache.len(),
            max_memory_entries: self.max_memory_entries,
        })
    }
}

/// 缓存统计信息
#[derive(Debug, Clone)]
pub struct CacheStats {
    pub memory_entries: usize,
    pub max_memory_entries: usize,
}
