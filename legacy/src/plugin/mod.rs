pub mod api;
pub mod installer;
pub mod loader;
pub mod manager;

pub use api::{
    CrashInfo, DownloadProgressInfo, LauncherStatus, Plugin, PluginContext, PluginMeta,
    PluginRegisterContext, PluginStage,
};
pub use installer::{PluginInstaller, PluginManifestEntry, PluginSource};
pub use loader::PluginLoader;
pub use manager::PluginManager;
