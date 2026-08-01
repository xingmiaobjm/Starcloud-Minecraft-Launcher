pub mod api;
pub mod manager;
pub mod loader;
pub mod installer;

pub use api::{
    Plugin, PluginMeta, PluginStage, PluginContext, PluginRegisterContext,
    LauncherStatus, DownloadProgressInfo, CrashInfo,
};
pub use manager::PluginManager;
pub use loader::PluginLoader;
pub use installer::{PluginInstaller, PluginSource};
