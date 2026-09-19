//! 模组搜索结果 + 翻译持久化缓存
//!
//! - 热门模组搜索结果缓存到磁盘，启动器打开时立即展示
//! - 翻译缓存持久化，避免每次重启重新请求翻译服务
//! - 缓存文件位于 `%APPDATA%/starcloud-launcher/`

use crate::download::ModSearchHit;
use std::collections::HashMap;
use std::path::PathBuf;
use std::sync::Mutex;

/// 热门模组缓存文件名
const MOD_CACHE_FILE: &str = "mod_cache.json";
/// 翻译缓存文件名
const TRANSLATE_CACHE_FILE: &str = "translate_cache.json";

fn data_dir() -> PathBuf {
    dirs::data_dir()
        .unwrap_or_else(|| PathBuf::from("."))
        .join("starcloud-launcher")
}

// ---------------------------------------------------------------------------
// 热门模组缓存
// ---------------------------------------------------------------------------

/// 热门模组缓存结构
#[derive(Debug, Clone, serde::Serialize, serde::Deserialize)]
pub struct PopularModsCache {
    pub mods: Vec<ModSearchHit>,
    pub total_count: u32,
    pub last_updated: String,
}

/// 加载热门模组缓存
pub fn load_popular_mods_cache() -> Option<PopularModsCache> {
    let path = data_dir().join(MOD_CACHE_FILE);
    if !path.exists() {
        return None;
    }
    match std::fs::read_to_string(&path) {
        Ok(data) => match serde_json::from_str::<PopularModsCache>(&data) {
            Ok(c) => {
                log::info!("从缓存加载热门模组，共 {} 个", c.mods.len());
                Some(c)
            }
            Err(e) => {
                log::warn!("热门模组缓存解析失败: {}", e);
                None
            }
        },
        Err(e) => {
            log::warn!("读取热门模组缓存失败: {}", e);
            None
        }
    }
}

/// 保存热门模组缓存
pub fn save_popular_mods_cache(cache: &PopularModsCache) {
    let dir = data_dir();
    if let Err(e) = std::fs::create_dir_all(&dir) {
        log::warn!("创建缓存目录失败: {}", e);
        return;
    }
    let path = dir.join(MOD_CACHE_FILE);
    match serde_json::to_string(cache) {
        Ok(data) => {
            if let Err(e) = std::fs::write(&path, &data) {
                log::warn!("写入热门模组缓存失败: {}", e);
            } else {
                log::info!("热门模组缓存已保存，共 {} 个模组", cache.mods.len());
            }
        }
        Err(e) => log::warn!("热门模组序列化失败: {}", e),
    }
}

// ---------------------------------------------------------------------------
// 翻译持久化缓存
// ---------------------------------------------------------------------------

/// 全局翻译缓存（内存 + 磁盘持久化，Mutex 保护）
static PERSIST_CACHE: std::sync::LazyLock<Mutex<HashMap<String, String>>> =
    std::sync::LazyLock::new(|| {
        let map = load_translate_cache_from_disk();
        Mutex::new(map)
    });

fn load_translate_cache_from_disk() -> HashMap<String, String> {
    let path = data_dir().join(TRANSLATE_CACHE_FILE);
    if !path.exists() {
        return HashMap::with_capacity(512);
    }
    match std::fs::read_to_string(&path) {
        Ok(data) => match serde_json::from_str::<HashMap<String, String>>(&data) {
            Ok(m) => {
                log::info!("从磁盘加载翻译缓存，共 {} 条", m.len());
                m
            }
            Err(e) => {
                log::warn!("翻译缓存解析失败: {}", e);
                HashMap::with_capacity(512)
            }
        },
        Err(e) => {
            log::warn!("读取翻译缓存失败: {}", e);
            HashMap::with_capacity(512)
        }
    }
}

fn save_translate_cache_to_disk(map: &HashMap<String, String>) {
    let dir = data_dir();
    let _ = std::fs::create_dir_all(&dir);
    let path = dir.join(TRANSLATE_CACHE_FILE);
    match serde_json::to_string(map) {
        Ok(data) => {
            let _ = std::fs::write(&path, &data);
        }
        Err(e) => log::warn!("翻译缓存序列化失败: {}", e),
    }
}

/// 从持久化翻译缓存读取
pub fn translate_cache_get(key: &str) -> Option<String> {
    PERSIST_CACHE.lock().ok().and_then(|m| m.get(key).cloned())
}

/// 写入持久化翻译缓存（同时异步写磁盘）
pub fn translate_cache_put(key: String, val: String) {
    if let Ok(mut m) = PERSIST_CACHE.lock() {
        if m.len() >= 4096 {
            // 达到上限时删除最旧的 1/4 条目
            let remove_count = m.len() / 4;
            let keys_to_remove: Vec<String> = m.keys().take(remove_count).cloned().collect();
            for k in keys_to_remove {
                m.remove(&k);
            }
        }
        m.insert(key, val);
        // 异步写磁盘（每 10 次写入执行一次，避免频繁 IO）
        if m.len() % 10 == 0 {
            let snapshot = m.clone();
            std::thread::spawn(move || save_translate_cache_to_disk(&snapshot));
        }
    }
}

/// 强制刷新翻译缓存到磁盘（应用退出前调用）
pub fn flush_translate_cache() {
    if let Ok(m) = PERSIST_CACHE.lock() {
        save_translate_cache_to_disk(&m);
        log::info!("翻译缓存已刷新到磁盘，共 {} 条", m.len());
    }
}