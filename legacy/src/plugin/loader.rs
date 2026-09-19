use crate::error::{LauncherError, Result};
use crate::plugin::api::{CreatePluginFn, Plugin, PLUGIN_ENTRY};
use std::collections::HashMap;
use std::path::Path;

/// 已加载的动态库句柄封装
///
/// 仅持有 `libloading::Library` 即可：只要库句柄存活，
/// 由其构造出的 `Box<dyn Plugin>` 实例就不会悬垂。
struct DynamicLib {
    _lib: libloading::Library,
}

/// 插件加载器 - 负责动态加载 .dll/.so/.dylib 插件
pub struct PluginLoader {
    /// 当前已加载的动态库（按插件 ID 索引）
    loaded_libs: HashMap<String, DynamicLib>,
}

impl Default for PluginLoader {
    fn default() -> Self {
        Self::new()
    }
}

impl PluginLoader {
    /// 创建插件加载器
    pub fn new() -> Self {
        Self {
            loaded_libs: HashMap::new(),
        }
    }

    /// 加载动态库插件
    ///
    /// # Safety
    ///
    /// 此函数加载用户提供的动态库并执行其中的代码。
    /// 请只加载可信来源的插件。
    pub unsafe fn load(&mut self, path: &Path) -> Result<Box<dyn Plugin>> {
        // 验证文件存在
        if !path.exists() {
            return Err(LauncherError::PluginLoadError(format!(
                "插件文件不存在: {}",
                path.display()
            )));
        }

        // 加载动态库
        let lib = libloading::Library::new(path).map_err(|e| {
            LauncherError::PluginLoadError(format!("无法加载插件库 '{}': {}", path.display(), e))
        })?;

        // 获取构造函数
        let constructor: libloading::Symbol<CreatePluginFn> =
            lib.get(PLUGIN_ENTRY.as_bytes()).map_err(|e| {
                LauncherError::PluginLoadError(format!(
                    "插件 '{}' 缺少导出函数 '{}': {}",
                    path.display(),
                    PLUGIN_ENTRY,
                    e
                ))
            })?;

        // 创建插件实例
        let plugin = constructor();

        if plugin.is_null() {
            return Err(LauncherError::PluginLoadError(format!(
                "插件 '{}' 构造函数返回空指针",
                path.display()
            )));
        }

        let plugin: Box<dyn Plugin> = Box::from_raw(plugin);

        // 获取元信息以便用 ID 索引
        let meta = plugin.meta();

        // 保存库句柄
        self.loaded_libs
            .insert(meta.id.clone(), DynamicLib { _lib: lib });

        Ok(plugin)
    }

    /// 卸载指定插件的动态库
    pub fn unload(&mut self, plugin_id: &str) {
        if let Some(_lib) = self.loaded_libs.remove(plugin_id) {
            log::info!("已释放插件动态库: {}", plugin_id);
        }
    }

    /// 已加载的插件数量
    pub fn loaded_count(&self) -> usize {
        self.loaded_libs.len()
    }
}
