#[allow(clippy::module_inception)] // config/config.rs 结构清晰，保留现状
pub mod config;

pub use config::{
    LaunchProfile, LauncherConfig, ProfileManager, VersionSettings, VersionSettingsStore,
};
