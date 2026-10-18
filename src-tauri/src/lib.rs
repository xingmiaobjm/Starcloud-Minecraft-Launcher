// StarCloud Minecraft Launcher - LGPL-3.0-only
// 启动器核心库

mod commands;

use commands::auth::AuthState;
use commands::p2p::P2PState;
use std::sync::Mutex;
use tauri::Manager;

#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        .plugin(tauri_plugin_dialog::init())
        .plugin(tauri_plugin_fs::init())
        .plugin(tauri_plugin_shell::init())
        .plugin(tauri_plugin_process::init())
        .manage(AuthState {
            current_profile: Mutex::new(None),
            pkce_verifier: Mutex::new(None),
        })
        .manage(P2PState {
            running: Mutex::new(false),
            network_name: Mutex::new(None),
            virtual_ip: Mutex::new(None),
        })
        .setup(|app| {
            #[cfg(debug_assertions)]
            {
                let window = app.get_webview_window("main").unwrap();
                window.open_devtools();
            }
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::auth::auth_get_ms_login_url,
            commands::auth::auth_microsoft_complete,
            commands::auth::auth_refresh_token,
            commands::auth::auth_offline_login,
            commands::auth::auth_get_profile,
            commands::auth::auth_logout,
            commands::minecraft::fetch_versions,
            commands::minecraft::install_version,
            commands::minecraft::list_installed_versions,
            commands::minecraft::delete_version,
            commands::minecraft::detect_java,
            commands::minecraft::launch_minecraft,
            commands::minecraft::get_game_logs,
            commands::p2p::p2p_create_network,
            commands::p2p::p2p_join_network,
            commands::p2p::p2p_discover_peers,
            commands::p2p::p2p_discover_servers,
            commands::p2p::p2p_get_status,
            commands::p2p::p2p_leave_network,
            commands::p2p::p2p_generate_invite,
            commands::error_analysis::analyze_crash_report,
            commands::error_analysis::analyze_game_log,
            commands::error_analysis::get_error_stats,
            commands::plugin::plugin_load,
            commands::plugin::plugin_unload,
            commands::plugin::plugin_list,
        ])
        .run(tauri::generate_context!())
        .expect("启动 StarCloud Launcher 失败");
}
