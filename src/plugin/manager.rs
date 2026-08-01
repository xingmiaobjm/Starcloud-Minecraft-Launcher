use crate::error::{LauncherError, Result};
use crate::plugin::api::*;
use crate::plugin::loader::PluginLoader;
use crate::version_manager::VersionComparator;
use std::cmp::Ordering;
use std::collections::HashMap;
use std::path::PathBuf;
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
    source_path: Option<PathBuf>,
}

unsafe impl Send for PluginInstance {}
unsafe impl Sync for PluginInstance {}

/// 插件快照条目（fire_event 拓扑排序用）
type PluginEntry = (String, PluginMeta, Arc<Box<dyn Plugin>>);

/// 插件管理器 - 负责插件的注册、生命周期管理
///
/// 锁约定：`loader` 锁与 `plugins` 锁不得同时嵌套持有（先释放再取另一把），
/// 回调（`on_enable`/`on_event`）一律在锁外执行，防止插件回调死锁。
pub struct PluginManager {
    /// 已加载的插件（按 ID 索引）
    plugins: RwLock<HashMap<String, PluginInstance>>,
    /// 注册上下文
    register_ctx: PluginRegisterContext,
    /// 插件加载器
    loader: RwLock<PluginLoader>,
    /// 插件目录
    plugins_dir: PathBuf,
}

impl PluginManager {
    /// 创建插件管理器
    pub fn new(data_dir: impl Into<PathBuf>) -> Self {
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

    /// 获取启动器版本（与插件元信息比较用）
    pub fn launcher_version(&self) -> &str {
        &self.register_ctx.launcher_version
    }

    /// 校验插件元信息与兼容性（ID/名称/版本合法性、API 版本、最低启动器版本）
    fn validate_compat(&self, meta: &PluginMeta) -> Result<()> {
        crate::version_manager::VersionValidator::validate_plugin_meta(meta)?;

        // API 版本兼容性
        if meta.api_version == 0 || meta.api_version > PLUGIN_API_VERSION {
            return Err(LauncherError::PluginLoadError(format!(
                "插件 '{}' 要求 API 版本 {}，当前启动器仅支持 {}",
                meta.id, meta.api_version, PLUGIN_API_VERSION
            )));
        }

        // 最低启动器版本
        if !meta.min_launcher_version.is_empty()
            && VersionComparator::compare(
                meta.min_launcher_version.as_str(),
                self.launcher_version(),
            ) == Ordering::Greater
        {
            return Err(LauncherError::PluginLoadError(format!(
                "插件 '{}' 要求启动器版本 ≥ {}，当前版本 {}",
                meta.id,
                meta.min_launcher_version,
                self.launcher_version()
            )));
        }

        Ok(())
    }

    /// 校验依赖是否全部已加载，返回首个缺失的依赖
    fn missing_dependency(&self, meta: &PluginMeta) -> Option<String> {
        let plugins = self
            .plugins
            .read()
            .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))
            .ok()?;
        meta.dependencies
            .iter()
            .find(|dep| !plugins.contains_key(dep.as_str()))
            .cloned()
    }

    /// 注册一个静态插件（编译时嵌入），返回插件 ID
    pub fn register_static(&self, plugin: Box<dyn Plugin>) -> Result<String> {
        let meta = plugin.meta();
        self.validate_compat(&meta)?;

        if let Some(dep) = self.missing_dependency(&meta) {
            return Err(LauncherError::PluginLoadError(format!(
                "插件 '{}' 缺少依赖 '{}'",
                meta.id, dep
            )));
        }

        log::info!("注册静态插件: {} v{}", meta.name, meta.version);

        // 锁外回调，防止插件回调 manager 方法时死锁
        plugin.on_enable(&self.register_ctx)?;

        let id = meta.id.clone();
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
            id.clone(),
            PluginInstance {
                plugin: Arc::new(plugin),
                meta,
                enabled: true,
                source_path: None,
            },
        );

        Ok(id)
    }

    /// 加载单个动态库插件，返回插件 ID
    pub fn load_plugin(&self, path: impl AsRef<std::path::Path>) -> Result<String> {
        let path = path.as_ref();

        // 1. 加载动态库（仅持有 loader 锁）
        let plugin = {
            let mut loader = self
                .loader
                .write()
                .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))?;
            unsafe { loader.load(path)? }
        };
        let meta = plugin.meta();
        let id = meta.id.clone();

        // 2. 兼容性校验（失败时释放已加载的库句柄）
        if let Err(e) = self.validate_compat(&meta) {
            let mut loader = self
                .loader
                .write()
                .map_err(|_| LauncherError::PluginError("锁获取失败".into()))?;
            loader.unload(&id);
            return Err(e);
        }

        // 3. 依赖检查
        if let Some(dep) = self.missing_dependency(&meta) {
            let mut loader = self
                .loader
                .write()
                .map_err(|_| LauncherError::PluginError("锁获取失败".into()))?;
            loader.unload(&id);
            return Err(LauncherError::PluginLoadError(format!(
                "插件 '{}' 缺少依赖 '{}'（请先安装依赖插件）",
                id, dep
            )));
        }

        log::info!(
            "加载动态插件: {} v{} (来自 {})",
            meta.name,
            meta.version,
            path.display()
        );

        // 4. 锁外回调
        plugin.on_enable(&self.register_ctx)?;

        // 5. 插入（再次检查 ID 冲突）
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
            id.clone(),
            PluginInstance {
                plugin: Arc::new(plugin),
                meta,
                enabled: true,
                source_path: Some(path.to_path_buf()),
            },
        );

        Ok(id)
    }

    /// 扫描目录并加载所有插件（按依赖拓扑顺序，多轮加载）
    ///
    /// 返回成功加载的插件 ID 列表；依赖缺失的插件会被跳过并记录警告。
    pub fn load_from_dir(&self, dir: impl AsRef<std::path::Path>) -> Result<Vec<String>> {
        let dir = dir.as_ref();
        if !dir.exists() {
            return Ok(Vec::new());
        }

        // 收集合法插件文件
        let mut remaining: Vec<PathBuf> = Vec::new();
        for entry in std::fs::read_dir(dir)? {
            let entry = entry?;
            let path = entry.path();
            if !path.is_file() {
                continue;
            }
            let ext = path.extension().map(|e| e.to_string_lossy().to_lowercase());
            if matches!(ext.as_deref(), Some("dll") | Some("so") | Some("dylib")) {
                remaining.push(path);
            }
        }

        // 多轮加载：依赖可能在其他文件中，直到一轮无进展
        let mut loaded_ids = Vec::new();
        let mut progress = true;
        while progress && !remaining.is_empty() {
            progress = false;
            let mut deferred = Vec::new();
            for path in remaining.drain(..) {
                match self.load_plugin(&path) {
                    Ok(id) => {
                        loaded_ids.push(id);
                        progress = true;
                    }
                    Err(e) => {
                        // 依赖缺失错误 → 延后到下一轮；其他错误直接记录
                        if matches!(
                            e,
                            LauncherError::PluginLoadError(ref msg) if msg.contains("缺少依赖")
                        ) {
                            deferred.push(path);
                        } else {
                            log::warn!("加载插件 {} 失败: {}", path.display(), e);
                        }
                    }
                }
            }
            remaining = deferred;
        }

        if !remaining.is_empty() {
            let names: Vec<String> = remaining
                .iter()
                .map(|p| {
                    p.file_name()
                        .unwrap_or_default()
                        .to_string_lossy()
                        .into_owned()
                })
                .collect();
            log::warn!("以下插件因依赖缺失或循环依赖未加载: {}", names.join(", "));
        }

        Ok(loaded_ids)
    }

    /// 卸载插件
    pub fn unload_plugin(&self, plugin_id: &str) -> Result<()> {
        let mut plugins = self
            .plugins
            .write()
            .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))?;

        // 检查是否有其他插件依赖它
        let dependents: Vec<String> = plugins
            .iter()
            .filter(|(id, p)| {
                id.as_str() != plugin_id && p.meta.dependencies.contains(&plugin_id.to_string())
            })
            .map(|(id, _)| id.clone())
            .collect();
        if !dependents.is_empty() {
            return Err(LauncherError::PluginError(format!(
                "无法卸载插件 '{}'：以下插件依赖它: {}",
                plugin_id,
                dependents.join(", ")
            )));
        }

        if let Some(instance) = plugins.remove(plugin_id) {
            instance.plugin.on_disable()?;
            drop(instance);
            log::info!("已卸载插件: {}", plugin_id);

            // 释放动态库（独立于 plugins 锁）
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

    /// 触发事件给所有插件（锁外回调，按依赖拓扑排序保证顺序）
    pub fn fire_event(&self, stage: PluginStage, ctx: &mut PluginContext) -> Result<()> {
        // 快照插件（Arc 克隆），释放读锁后再回调
        let snapshot: Vec<(String, Arc<Box<dyn Plugin>>)> = {
            let plugins = self
                .plugins
                .read()
                .map_err(|e| LauncherError::PluginError(format!("锁获取失败: {}", e)))?;
            let mut items: Vec<PluginEntry> = plugins
                .iter()
                .filter(|(_, p)| p.enabled)
                .map(|(id, p)| (id.clone(), p.meta.clone(), p.plugin.clone()))
                .collect();
            topo_sort(&mut items);
            items.into_iter().map(|(id, _, p)| (id, p)).collect()
        };

        for (id, plugin) in snapshot {
            if let Err(e) = plugin.on_event(stage.clone(), ctx) {
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

/// 按依赖关系对插件列表做拓扑排序（Kahn 算法），依赖先于被依赖者
///
/// 循环依赖或缺失依赖不影响排序（按原顺序保留），由加载期检查兜底。
fn topo_sort(items: &mut Vec<PluginEntry>) {
    let n = items.len();
    if n <= 1 {
        return;
    }
    // 构建 ID → 索引映射
    let index_of: HashMap<&str, usize> = items
        .iter()
        .enumerate()
        .map(|(i, (id, _, _))| (id.as_str(), i))
        .collect();

    let mut indegree = vec![0usize; n];
    let mut dependents: Vec<Vec<usize>> = vec![Vec::new(); n];
    for i in 0..n {
        for dep in &items[i].1.dependencies {
            if let Some(&j) = index_of.get(dep.as_str()) {
                if j != i {
                    dependents[j].push(i);
                    indegree[i] += 1;
                }
            }
        }
    }

    // 队列初始化：入度为 0 的节点
    let mut queue: Vec<usize> = (0..n).filter(|&i| indegree[i] == 0).collect();
    let mut order = Vec::with_capacity(n);
    while let Some(i) = queue.pop() {
        order.push(i);
        for &j in &dependents[i] {
            indegree[j] -= 1;
            if indegree[j] == 0 {
                queue.push(j);
            }
        }
    }

    if order.len() != n {
        // 存在循环依赖，按原顺序（避免 panic）
        return;
    }

    // 按拓扑顺序重排（原地覆盖）
    let sorted = order
        .into_iter()
        .map(|i| items[i].clone())
        .collect::<Vec<_>>();
    *items = sorted;
}

#[cfg(test)]
mod tests {
    use super::*;

    struct TestPlugin {
        id: String,
        deps: Vec<String>,
    }

    impl Plugin for TestPlugin {
        fn meta(&self) -> PluginMeta {
            PluginMeta::new(
                self.id.clone(),
                self.id.clone(),
                "1.0.0",
                "test",
                "test plugin",
                self.deps.clone(),
                "0.0.0",
            )
        }
    }

    #[test]
    fn test_register_and_unload() {
        let manager = PluginManager::new(tempfile::tempdir().unwrap().path());
        let id = manager
            .register_static(Box::new(TestPlugin {
                id: "com.test.base".into(),
                deps: vec![],
            }))
            .unwrap();
        assert_eq!(id, "com.test.base");
        assert_eq!(manager.list_plugins().unwrap().len(), 1);

        // 重复注册报错
        assert!(manager
            .register_static(Box::new(TestPlugin {
                id: "com.test.base".into(),
                deps: vec![],
            }))
            .is_err());

        manager.unload_plugin("com.test.base").unwrap();
        assert!(manager.list_plugins().unwrap().is_empty());
    }

    #[test]
    fn test_dependency_check() {
        let manager = PluginManager::new(tempfile::tempdir().unwrap().path());

        // 依赖未加载时报错
        let err = manager
            .register_static(Box::new(TestPlugin {
                id: "com.test.consumer".into(),
                deps: vec!["com.test.base".into()],
            }))
            .unwrap_err();
        assert!(err.to_string().contains("缺少依赖"));

        // 先加载依赖再加载消费者
        manager
            .register_static(Box::new(TestPlugin {
                id: "com.test.base".into(),
                deps: vec![],
            }))
            .unwrap();
        manager
            .register_static(Box::new(TestPlugin {
                id: "com.test.consumer".into(),
                deps: vec!["com.test.base".into()],
            }))
            .unwrap();

        // 卸载被依赖插件失败
        assert!(manager.unload_plugin("com.test.base").is_err());
        // 卸载消费者成功，然后可以卸载 base
        manager.unload_plugin("com.test.consumer").unwrap();
        manager.unload_plugin("com.test.base").unwrap();
    }

    #[test]
    fn test_api_version_rejected() {
        let manager = PluginManager::new(tempfile::tempdir().unwrap().path());

        struct ApiV2Plugin;
        impl Plugin for ApiV2Plugin {
            fn meta(&self) -> PluginMeta {
                let mut m = PluginMeta::new(
                    "com.test.api2",
                    "api2",
                    "1.0.0",
                    "test",
                    "",
                    vec![],
                    "0.0.0",
                );
                m.api_version = 99;
                m
            }
        }
        let err = manager.register_static(Box::new(ApiV2Plugin)).unwrap_err();
        assert!(err.to_string().contains("API 版本"));
    }

    #[test]
    fn test_min_launcher_version_rejected() {
        let manager = PluginManager::new(tempfile::tempdir().unwrap().path());

        struct FuturePlugin;
        impl Plugin for FuturePlugin {
            fn meta(&self) -> PluginMeta {
                PluginMeta::new(
                    "com.test.future",
                    "future",
                    "1.0.0",
                    "test",
                    "",
                    vec![],
                    "99.0.0", // 远超当前启动器版本
                )
            }
        }
        let err = manager.register_static(Box::new(FuturePlugin)).unwrap_err();
        assert!(err.to_string().contains("要求启动器版本"));
    }

    #[test]
    fn test_fire_event_order_and_snapshot() {
        use std::sync::atomic::{AtomicUsize, Ordering as AtomicOrdering};

        struct OrderPlugin {
            id: String,
            deps: Vec<String>,
            counter: Arc<AtomicUsize>,
            log: Arc<std::sync::Mutex<Vec<String>>>,
        }

        impl Plugin for OrderPlugin {
            fn meta(&self) -> PluginMeta {
                PluginMeta::new(
                    self.id.clone(),
                    self.id.clone(),
                    "1.0.0",
                    "test",
                    "",
                    self.deps.clone(),
                    "0.0.0",
                )
            }

            fn on_event(&self, _stage: PluginStage, _ctx: &mut PluginContext) -> Result<()> {
                let n = self.counter.fetch_add(1, AtomicOrdering::SeqCst);
                self.log.lock().unwrap().push(format!("{}:{}", n, self.id));
                Ok(())
            }
        }

        let manager = PluginManager::new(tempfile::tempdir().unwrap().path());
        let counter = Arc::new(AtomicUsize::new(0));
        let log = Arc::new(std::sync::Mutex::new(Vec::new()));

        manager
            .register_static(Box::new(OrderPlugin {
                id: "dep".into(),
                deps: vec![],
                counter: counter.clone(),
                log: log.clone(),
            }))
            .unwrap();
        manager
            .register_static(Box::new(OrderPlugin {
                id: "consumer".into(),
                deps: vec!["dep".into()],
                counter: counter.clone(),
                log: log.clone(),
            }))
            .unwrap();

        let mut ctx = PluginContext::new(".");
        manager.fire_event(PluginStage::Init, &mut ctx).unwrap();

        // 消费者依赖 dep，dep 必须先于 consumer 收到事件
        let entries = log.lock().unwrap().clone();
        assert_eq!(entries.len(), 2);
        let dep_pos = entries.iter().position(|e| e.ends_with(":dep")).unwrap();
        let consumer_pos = entries
            .iter()
            .position(|e| e.ends_with(":consumer"))
            .unwrap();
        assert!(dep_pos < consumer_pos);
    }
}
