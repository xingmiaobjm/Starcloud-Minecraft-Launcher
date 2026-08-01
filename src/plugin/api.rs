use crate::error::Result;
use std::any::Any;
use std::path::PathBuf;

/// 插件生命周期阶段枚举
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum PluginStage {
    // ---- 核心生命周期 ----
    /// 插件初始化（注册时立即触发）
    Init,
    /// 版本清单下载前
    PreVersionManifest,
    /// 版本清单下载完成
    PostVersionManifest,
    /// 版本详情下载后
    PostVersionDetails,
    /// 资源下载前
    PreAssetDownload,
    /// 资源下载后
    PostAssetDownload,
    /// 库文件下载前
    PreLibraryDownload,
    /// 库文件下载后
    PostLibraryDownload,
    /// JVM 参数构建后、实际启动前
    PreLaunch,
    /// 进程启动后
    PostLaunch,
    /// 游戏进程正常退出
    OnGameExit,

    // ---- GUI 专用钩子 ----
    /// 启动器就绪（核心模块初始化完毕，GUI 可开始渲染）
    LauncherReady,
    /// 启动器即将关闭（GUI 应保存状态、释放句柄）
    LauncherShutdown,
    /// 下载进度更新（高频触发，回调中不要做重操作）
    DownloadProgress,
    /// 启动器状态变更（idle → downloading → launching → running → idle）
    StatusChanged,
    /// 游戏进程崩溃（退出码非0时触发，ctx 包含崩溃详情）
    GameCrashed,
    /// 配置变更（用户修改了 LauncherConfig）
    ConfigChanged,
    /// 账户切换（登录/登出/切换用户）
    AccountChanged,
    /// 版本已安装完成
    VersionInstalled,
    /// 版本已删除
    VersionUninstalled,
    /// 错误发生（任意异步操作失败，用于 GUI 集中展示错误）
    ErrorOccurred,
}

/// 插件事件上下文 - 在事件触发时传递给插件
#[derive(Debug, Clone)]
pub struct PluginContext {
    /// .minecraft 目录
    pub dot_minecraft: PathBuf,
    /// 当前版本 ID
    pub version_id: Option<String>,
    /// 玩家用户名
    pub username: Option<String>,
    /// 游戏进程 PID
    pub game_pid: Option<u32>,
    /// 额外数据（键值对，方便插件间传递数据）
    pub extra: std::collections::HashMap<String, String>,

    // ---- GUI 专用字段 ----
    /// 当前启动器状态
    pub launcher_status: LauncherStatus,
    /// 下载进度信息（DownloadProgress 事件时填充）
    pub download_progress: Option<DownloadProgressInfo>,
    /// 游戏崩溃信息（GameCrashed 事件时填充）
    pub crash_info: Option<CrashInfo>,
    /// 最近一次错误信息（ErrorOccurred 事件时填充）
    pub error_message: Option<String>,
}

/// 启动器运行状态（GUI 据此切换界面）
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum LauncherStatus {
    /// 空闲
    Idle,
    /// 初始化中
    Initializing,
    /// 版本清单获取中
    FetchingManifest,
    /// 下载中
    Downloading,
    /// 启动中
    Launching,
    /// 游戏运行中
    Running,
    /// 发生错误
    Error,
}

/// 下载进度快照（避免持有锁，定期拷贝给 GUI）
#[derive(Debug, Clone)]
pub struct DownloadProgressInfo {
    /// 当前阶段描述（如 "下载资源文件"）
    pub stage: String,
    /// 已下载字节
    pub downloaded_bytes: u64,
    /// 总字节
    pub total_bytes: u64,
    /// 百分比 0.0~100.0
    pub percentage: f64,
    /// 速度（字节/秒）
    pub speed_bytes_per_sec: f64,
    /// 估算剩余时间（秒）
    pub eta_secs: Option<f64>,
}

/// 游戏崩溃信息
#[derive(Debug, Clone)]
pub struct CrashInfo {
    /// 进程退出码
    pub exit_code: i32,
    /// 版本 ID
    pub version_id: String,
    /// 是否因内存不足而崩溃
    pub out_of_memory: bool,
    /// 最近几行 stderr（最多 2KB）
    pub last_stderr: String,
}

impl PluginContext {
    /// 创建基础上下文
    pub fn new(dot_minecraft: impl Into<PathBuf>) -> Self {
        Self {
            dot_minecraft: dot_minecraft.into(),
            version_id: None,
            username: None,
            game_pid: None,
            extra: std::collections::HashMap::new(),
            launcher_status: LauncherStatus::Idle,
            download_progress: None,
            crash_info: None,
            error_message: None,
        }
    }

    /// 设置下载进度（便捷方法）
    pub fn with_download_progress(mut self, info: DownloadProgressInfo) -> Self {
        self.download_progress = Some(info);
        self
    }

    /// 设置崩溃信息（便捷方法）
    pub fn with_crash_info(mut self, info: CrashInfo) -> Self {
        self.crash_info = Some(info);
        self
    }

    /// 设置错误消息（便捷方法）
    pub fn with_error(mut self, msg: impl Into<String>) -> Self {
        self.error_message = Some(msg.into());
        self
    }

    /// 设置启动器状态（便捷方法）
    pub fn with_status(mut self, status: LauncherStatus) -> Self {
        self.launcher_status = status;
        self
    }
}

/// 插件注册上下文 - 插件初始化时传入
#[derive(Debug, Clone)]
pub struct PluginRegisterContext {
    /// 启动器版本
    pub launcher_version: String,
    /// 启动器数据目录
    pub data_dir: PathBuf,
    /// 启动器缓存目录
    pub cache_dir: PathBuf,
    /// 启动器配置目录
    pub config_dir: PathBuf,
}

/// 插件元信息
#[derive(Debug, Clone)]
pub struct PluginMeta {
    /// 插件 ID（全局唯一，建议使用倒置域名）
    pub id: String,
    /// 插件名称
    pub name: String,
    /// 插件版本
    pub version: String,
    /// 作者
    pub author: String,
    /// 描述
    pub description: String,
    /// 依赖的插件 ID 列表
    pub dependencies: Vec<String>,
    /// 最低启动器版本
    pub min_launcher_version: String,
}

/// 插件 trait - 所有插件必须实现的接口
///
/// # 示例
///
/// ```ignore
/// use furry_minecraft::plugin::{Plugin, PluginMeta, PluginRegisterContext, PluginContext, Result};
///
/// pub struct MyPlugin;
///
/// impl Plugin for MyPlugin {
///     fn meta(&self) -> PluginMeta {
///         PluginMeta {
///             id: "com.example.myplugin".into(),
///             name: "My Plugin".into(),
///             version: "1.0.0".into(),
///             author: "Example".into(),
///             description: "An example plugin".into(),
///             dependencies: vec![],
///             min_launcher_version: "0.1.0".into(),
///         }
///     }
///
///     fn on_enable(&self, ctx: &PluginRegisterContext) -> Result<()> {
///         println!("MyPlugin enabled!");
///         Ok(())
///     }
/// }
/// ```
pub trait Plugin: Send + Sync {
    /// 返回插件元信息
    fn meta(&self) -> PluginMeta;

    /// 插件启用时调用（注册时）
    fn on_enable(&self, _ctx: &PluginRegisterContext) -> Result<()> {
        Ok(())
    }

    /// 插件禁用时调用
    fn on_disable(&self) -> Result<()> {
        Ok(())
    }

    /// 生命周期事件钩子
    fn on_event(&self, _stage: PluginStage, _ctx: &mut PluginContext) -> Result<()> {
        Ok(())
    }

    /// 获取任意类型数据（用于插件间交互）
    fn as_any(&self) -> Option<&dyn Any> {
        None
    }

    /// 获取可变的任意类型数据
    fn as_any_mut(&mut self) -> Option<&mut dyn Any> {
        None
    }
}

/// 动态库插件的导出函数类型
///
/// 注意：这不是 FFI 安全的（trait objects 没有 C 等价物），
/// 但这是我们定义的插件约定，在同一编译器版本下可安全使用。
#[allow(improper_ctypes_definitions)]
pub type CreatePluginFn = unsafe extern "C" fn() -> *mut dyn Plugin;

/// 动态库插件的模块构造函数名
pub const PLUGIN_ENTRY: &str = "_plugin_create";
