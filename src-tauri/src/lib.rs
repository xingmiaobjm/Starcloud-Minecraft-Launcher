//! Starcloud Minecraft Launcher - Tauri GUI 入口
//!
//! 后端命令/事件与核心服务层（starcloud-minecraft）桥接。
//! 核心库保持无 tauri 依赖；本 crate 负责 GUI 外壳、命令、事件与插件宿主。

mod commands;
mod events;
mod plugin_host;
mod state;

use state::AppState;
use tauri::Manager;

/// 全局配置文件名（存放于系统配置目录）
const CONFIG_FILE: &str = "config.toml";

/// 应用入口
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // furry:// 静态资源协议（GUI 插件 view/replacement 前端）
        .register_uri_scheme_protocol("furry", plugin_host::handle_furry_request)
        .setup(|app| {
            let config = load_config();
            app.manage(AppState::new(config));
            log::info!("Starcloud Launcher GUI 已启动");
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::list_versions,
            commands::list_installed,
            commands::install_version,
            commands::remove_version,
            commands::install_fabric,
            commands::launch_game,
            commands::stop_game,
            commands::list_accounts,
            commands::add_offline_account,
            commands::remove_account,
            commands::set_default_account,
            commands::login_microsoft_device,
            commands::poll_microsoft_login,
            commands::get_settings,
            commands::set_settings,
            commands::get_gui_plugins,
            commands::install_plugin,
            commands::remove_plugin,
            commands::set_plugin_enabled,
            commands::list_plugins,
            commands::get_game_logs,
            commands::check_java,
        ])
        .run(tauri::generate_context!())
        .expect("运行 Tauri 应用失败");
}

/// 加载启动器配置（默认路径：系统配置目录/starcloud-launcher/config.toml）
fn load_config() -> starcloud_minecraft::config::LauncherConfig {
    let path = config_file_path();
    let mut config = tauri::async_runtime::block_on(async move {
        match starcloud_minecraft::config::LauncherConfig::load(&path).await {
            Ok(c) => c,
            Err(e) => {
                log::warn!("加载配置失败，使用默认配置: {}", e);
                starcloud_minecraft::config::LauncherConfig::default()
            }
        }
    });
    if let Err(e) = config.migrate() {
        log::warn!("配置迁移失败: {}", e);
    }
    // 环境变量覆盖插件目录（开发调试 / 自定义插件路径）
    if let Ok(dir) = std::env::var("STARCLOUD_PLUGINS_DIR") {
        if !dir.trim().is_empty() {
            config.plugins_dir = dir.into();
            log::info!(
                "插件目录由 STARCLOUD_PLUGINS_DIR 覆盖: {}",
                config.plugins_dir.display()
            );
        }
    }
    config
}

/// 配置文件完整路径
pub fn config_file_path() -> std::path::PathBuf {
    dirs_config_dir()
        .join("starcloud-launcher")
        .join(CONFIG_FILE)
}

/// 系统配置目录（dirs crate）
fn dirs_config_dir() -> std::path::PathBuf {
    dirs::config_dir().unwrap_or_else(|| std::path::PathBuf::from("."))
}
