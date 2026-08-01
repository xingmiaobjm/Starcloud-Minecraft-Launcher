use crate::error::{LauncherError, Result};
use crate::plugin::api::*;
use crate::plugin::loader::PluginLoader;
use std::collections::HashMap;
use std::sync::{Arc, RwLock};

/// 插件的运行时包装
struct PluginInstance {
    /// 插件实例
    plugin: Arc<Box<dyn Plugin>>,
    /// 元信息快照
    meta: PluginMeta,
    /// 是否已启用
    enabled: bool,
    /// 来源路径（动态库文件路径，None 表示静态注册）
    #[allow(dead_code)]
    source_path: Option<std::path::PathBuf>,
}

unsafe impl Send for PluginInstance {}
unsafe impl Sync for PluginInstance {}

/// 插件管理器 - 负责插件的注册、生命周期管理
pub struct PluginManager {
    /// 已加载的插件（按 ID 索引）
    plugins: RwLock<HashMap<String, PluginInstance>>,
    /// 注册上下文
    register_ctx: PluginRegisterContext,
    /// 插件加载器
    loader: RwLock<PluginLoader>,
    /// 插件目录
    plugins_dir: std::path::PathBuf,
}

impl PluginManager {
    /// 创建插件管理器
    pub fn new(data_dir: impl Into<std::path::PathBuf>) -> Self {
        let data_dir = data_dir.into();
        let plugins_dir = data_dir.join("plugins");
        let config_dir = data_dir.join("config");
        let cache_dir = data_dir.join("cache");

        let register_ctx = PluginRegisterContext {
            launcher_version: env!("CARGO_PKG_VERSION").to_string(),
            data_dir: data_dir.clone(),
            cache_dir,
            config_dir,
        };

        let loader = PluginLoader::new();

        Self {
            plugins: RwLock::new(HashMap::new()),
            register_ctx,
            loader: RwLock::new(loader),
            plugins_dir,
        }
    }

    /// 获取插件目录
    pub fn plugins_dir(&self) -> &std::path::Path {
        &self.plugins_dir
    }

    /// 注册一个静态插件（编译时嵌入）
    pub fn register_static(&self, plugin: Box<dyn Plugin>) -> Result<()> {
        let meta = plugin.meta();
        let id = meta.id.clone();

        log::info!("注册静态插件: {} v{}", meta.name, meta.version);

        plugin.on_enable(&self.register_ctx)?;

        let mut plugins = self
            .plugins
            .write()
            .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))?;

        if plugins.contains_key(&id) {
            return Err(LauncherError::PluginError(format!(
                "插件 ID '{}' 已存在",
                id
            )));
        }

        plugins.insert(
            id,
            PluginInstance {
                plugin: Arc::new(plugin),
                meta,
                enabled: true,
                source_path: None,
            },
        );

        Ok(())
    }

    /// 加载单个动态库插件
    pub fn load_plugin(&self, path: impl AsRef<std::path::Path>) -> Result<()> {
        let path = path.as_ref();
        let mut loader = self
            .loader
            .write()
            .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))?;

        let plugin = unsafe { loader.load(path)? };
        let meta = plugin.meta();
        let id = meta.id.clone();

        log::info!(
            "加载动态插件: {} v{} (来自 {})",
            meta.name,
            meta.version,
            path.display()
        );

        plugin.on_enable(&self.register_ctx)?;

        let mut plugins = self
            .plugins
            .write()
            .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))?;

        if plugins.contains_key(&id) {
            return Err(LauncherError::PluginError(format!(
                "插件 ID '{}' 已存在",
                id
            )));
        }

        plugins.insert(
            id,
            PluginInstance {
                plugin: Arc::new(plugin),
                meta,
                enabled: true,
                source_path: Some(path.to_path_buf()),
            },
        );

        Ok(())
    }

    /// 卸载插件
    pub fn unload_plugin(&self, plugin_id: &str) -> Result<()> {
        let mut plugins = self
            .plugins
            .write()
            .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))?;

        if let Some(instance) = plugins.remove(plugin_id) {
            instance.plugin.on_disable()?;
            drop(instance);
            log::info!("已卸载插件: {}", plugin_id);

            // 从 loader 中释放动态库
            let mut loader = self
                .loader
                .write()
                .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))?;
            loader.unload(plugin_id);
        }

        Ok(())
    }

    /// 获取所有已加载的插件元信息
    pub fn list_plugins(&self) -> Result<Vec<PluginMeta>> {
        let plugins = self
            .plugins
            .read()
            .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))?;
        Ok(plugins.values().map(|p| p.meta.clone()).collect())
    }

    /// 触发事件给所有插件
    pub fn fire_event(&self, stage: PluginStage, ctx: &mut PluginContext) -> Result<()> {
        let plugins = self
            .plugins
            .read()
            .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))?;

        for (id, instance) in plugins.iter() {
            if !instance.enabled {
                continue;
            }
            if let Err(e) = instance.plugin.on_event(stage.clone(), ctx) {
                log::warn!("插件 {} 处理事件 {:?} 时出错: {}", id, stage, e);
            }
        }
        Ok(())
    }

    /// 获取某个插件实例
    pub fn get_plugin(&self, plugin_id: &str) -> Option<Arc<Box<dyn Plugin>>> {
        let plugins = self.plugins.read().ok()?;
        plugins.get(plugin_id).map(|p| p.plugin.clone())
    }

    /// 获取注册上下文
    pub fn register_context(&self) -> &PluginRegisterContext {
        &self.register_ctx
    }
}
