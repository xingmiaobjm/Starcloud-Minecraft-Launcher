// Prevents additional console window on Windows in release, DO NOT REMOVE!!
#![cfg_attr(not(debug_assertions), windows_subsystem = "windows")]

fn main() {
    // 先安装依赖（WebView2 Runtime），再启动应用，避免依赖未就绪导致闪退（0xC0000142）
    #[cfg(target_os = "windows")]
    starcloud_launcher_lib::ensure_webview2_runtime();
    starcloud_launcher_lib::run()
}
