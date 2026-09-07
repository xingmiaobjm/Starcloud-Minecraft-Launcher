//! Starcloud Minecraft Launcher - Tauri GUI 入口
//!
//! 后端命令/事件与核心服务层（starcloud-minecraft）桥接。
//! 核心库保持无 tauri 依赖；本 crate 负责 GUI 外壳、命令、事件与插件宿主。

mod commands;
mod events;
mod plugin_host;
mod state;
mod tasks;

use state::AppState;
use std::sync::Arc;
use tauri::{Emitter, Manager};

/// 全局配置文件名（存放于系统配置目录）
const CONFIG_FILE: &str = "config.toml";

// ---------------------------------------------------------------------------
// WebView2 Runtime 依赖检测与安装（Windows）
//
// Tauri 应用依赖系统 WebView2 Runtime。缺失时应用启动会闪退（0xC0000142）。
// 此处用系统自带 reg.exe / curl.exe 检测并静默安装，零新增依赖：
//   启动流程 = 检测 → 缺失则先安装依赖（等待完成）→ 再创建窗口。
// 所有子进程一律带 CREATE_NO_WINDOW 标志：release 主程序无控制台，若不加，
// reg.exe / curl.exe 这类控制台子进程会弹出黑框"闪终端"。
// ---------------------------------------------------------------------------

/// WebView2 Evergreen Runtime 的注册表客户端 ID
#[cfg(target_os = "windows")]
const WEBVIEW2_CLIENT_ID: &str = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

/// CREATE_NO_WINDOW：子进程不创建控制台窗口（完全静默）
#[cfg(target_os = "windows")]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// 检测 WebView2 Runtime 是否已安装（读取注册表 pv 版本号）
#[cfg(target_os = "windows")]
pub fn webview2_installed() -> bool {
    use std::os::windows::process::CommandExt;
    let roots = [
        r"HKLM\SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients",
        r"HKLM\SOFTWARE\Microsoft\EdgeUpdate\Clients",
        r"HKCU\SOFTWARE\Microsoft\EdgeUpdate\Clients",
    ];
    for root in roots {
        let key = format!(r"{}\{}", root, WEBVIEW2_CLIENT_ID);
        if let Ok(out) = std::process::Command::new("reg")
            .args(["query", &key, "/v", "pv"])
            .creation_flags(CREATE_NO_WINDOW)
            .output()
        {
            if out.status.success() {
                let s = String::from_utf8_lossy(&out.stdout);
                // pv 值形如 `1.0.xxxx.xx`（REG_SZ 后面）
                if let Some(idx) = s.find("REG_SZ") {
                    let ver = s[idx + 6..].trim();
                    if !ver.is_empty() {
                        return true;
                    }
                }
            }
        }
    }
    false
}

/// 确保 WebView2 Runtime 已安装；缺失时静默下载官方 Bootstrapper 并安装，等待完成后再返回。
#[cfg(target_os = "windows")]
pub fn ensure_webview2_runtime() {
    use std::os::windows::process::CommandExt;
    if webview2_installed() {
        eprintln!("[依赖] WebView2 Runtime 已安装，跳过依赖安装");
        return;
    }
    eprintln!("[依赖] 未检测到 WebView2 Runtime，先安装依赖再启动...");
    let boot = std::env::temp_dir().join("MicrosoftEdgeWebview2Setup.exe");
    // 微软官方 WebView2 Bootstrapper 下载地址
    let url = "https://go.microsoft.com/fwlink/p/?LinkId=2124703";
    let dl = std::process::Command::new("curl.exe")
        .args(["-L", "--fail", "--silent", "--show-error", "-o"])
        .arg(&boot)
        .arg(url)
        .creation_flags(CREATE_NO_WINDOW)
        .status();
    if !matches!(dl, Ok(st) if st.success()) || !boot.exists() {
        eprintln!("[依赖] WebView2 Bootstrapper 下载失败，跳过依赖安装（应用可能无法启动）");
        return;
    }
    eprintln!("[依赖] 正在静默安装 WebView2 Runtime，请稍候...");
    let install = std::process::Command::new(&boot)
        .args(["/silent", "/install"])
        .creation_flags(CREATE_NO_WINDOW)
        .status();
    if !matches!(install, Ok(st) if st.success()) {
        eprintln!("[依赖] WebView2 静默安装未成功（bootstrapper 退出码非 0）");
    }
    let ok = webview2_installed();
    eprintln!("[依赖] WebView2 安装结束，已安装={}", ok);
    let _ = std::fs::remove_file(&boot);
}

/// 应用入口
#[cfg_attr(mobile, tauri::mobile_entry_point)]
pub fn run() {
    tauri::Builder::default()
        // furry:// 静态资源协议（GUI 插件 view/replacement 前端）
        .register_uri_scheme_protocol("furry", plugin_host::handle_furry_request)
        .setup(|app| {
            // 启动器本体日志：写入数据目录 starcloud-launcher/logs/launcher.log（按日滚动），
            // 开发者模式可在设置页「开发者选项 → 启动器本体日志」查看
            if let Err(e) = starcloud_minecraft::logging::init_logging(
                starcloud_minecraft::logging::default_log_dir(),
                "info",
            ) {
                log::warn!("启动器日志初始化失败: {}", e);
            }
            let config = load_config();
            app.manage(AppState::new(config));
            // 陶瓦联机状态轮询器（会话运行期间周期广播）
            commands::start_terracotta_poller(app.handle().clone());
            // 非透明窗口 + DWM Acrylic 毛玻璃：默认浅色主题，白色玻璃。
            // WebView2 初始化是异步的，多轮重试确保透明背景与毛玻璃最终生效。
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                for (i, delay_ms) in [0u64, 300, 1000, 2500].iter().enumerate() {
                    commands::apply_window_glass(&handle, false);
                    log::info!("窗口毛玻璃第 {} 次应用完成", i + 1);
                    if i < 3 {
                        let _ = tokio::time::sleep(std::time::Duration::from_millis(*delay_ms)).await;
                    }
                }
            });
            log::info!("Starcloud Launcher GUI 已启动");
            // 多下载源测速：启动时后台并发测试各源可达性/速度，自动切换最快的可达源
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                use starcloud_minecraft::download::sources::{
                    active_source, pick_best, probe_all_sources, set_active_source,
                };
                let results = probe_all_sources().await;
                if let Some(best) = pick_best(&results) {
                    set_active_source(best);
                }
                log::info!(
                    "当前下载源: {}（本次测速 {}/{} 个源可达）",
                    active_source().name(),
                    results.iter().filter(|r| r.reachable).count(),
                    results.len()
                );
                let _ = handle.emit("download-sources", &results);
            });
            // 启动时预加载：版本清单 + 热门模组 + 翻译 → 后续页面秒开
            let handle = app.handle().clone();
            tauri::async_runtime::spawn(async move {
                preload_startup_data(handle).await;
            });
            Ok(())
        })
        .invoke_handler(tauri::generate_handler![
            commands::list_versions,
            commands::list_installed,
            commands::install_version,
            commands::cancel_install,
            commands::download_server,
            commands::list_loaders,
            commands::install_loader,
            commands::search_mods,
            commands::translate_mod_titles,
            commands::translate_mod_descriptions,
            commands::list_mod_versions,
            commands::download_mod,
            commands::mcmod_lookup,
            commands::open_url,
            commands::pick_directory,
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
            commands::suggest_memory,
            commands::preflight_check,
            commands::get_gui_plugins,
            commands::install_plugin,
            commands::remove_plugin,
            commands::set_plugin_enabled,
            commands::list_plugins,
            commands::get_game_logs,
            commands::get_launcher_logs,
            commands::app_version,
            commands::check_java,
            commands::list_java_runtimes,
            commands::download_java_runtime,
            commands::remove_java_runtime,
            commands::list_tasks,
            commands::set_task_order,
            commands::remove_task,
            commands::get_version_settings,
            commands::set_version_settings,
            commands::get_download_sources,
            commands::probe_download_sources,
            commands::set_download_source,
            commands::clear_version_cache,
            commands::get_cached_icon,
            commands::load_popular_mods,
            commands::set_window_theme,
            commands::terracotta_status,
            commands::terracotta_install,
            commands::terracotta_start_host,
            commands::terracotta_join_room,
            commands::terracotta_stop,
            commands::terracotta_logs,
        ])
        .build(tauri::generate_context!())
        .expect("构建 Tauri 应用失败")
        .run(|app_handle, event| {
            // 退出前关闭陶瓦联机核心会话（释放子进程与临时端口文件）
            if let tauri::RunEvent::Exit = event {
                if let Some(mut session) = app_handle
                    .state::<AppState>()
                    .terracotta
                    .lock()
                    .unwrap()
                    .take()
                {
                    session.shutdown();
                    log::info!("陶瓦联机核心已随启动器退出而关闭");
                }
            }
        });
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

/// 启动时预加载数据：版本清单 + 热门模组 + 翻译 → 后续页面秒开（< 2s）
async fn preload_startup_data(handle: tauri::AppHandle) {
    use starcloud_minecraft::download::{
        mod_cache, search_modrinth_total, translate_title_zh,
    };
    use starcloud_minecraft::download::manifest::{
        fetch_version_manifest_cached,
    };

    log::info!("预加载启动数据：开始...");

    // 1. 版本清单：后台拉取并覆盖缓存（已有缓存时由 list_versions 命令立即返回）
    let state = handle.state::<AppState>();
    let config = state.config.lock().unwrap().clone();
    let _ = fetch_version_manifest_cached(config.version_cache_enabled).await;

    log::info!("预加载：版本清单已缓存");

    // 2. 热门模组：拉取下载量最高的 60 个模组并缓存
    //    total_count 记录 API 真实总命中数，前端据此判断可继续滚动加载
    match search_modrinth_total("", "", "", "", "", 60, 0).await {
        Ok(result) => {
            let hits = result.hits;
            let total_count = result.total_hits;
            mod_cache::save_popular_mods_cache(&mod_cache::PopularModsCache {
                mods: hits.clone(),
                total_count,
                last_updated: chrono::Utc::now().to_rfc3339(),
            });
            log::info!(
                "预加载：热门模组已缓存，共 {} 个（总命中 {}）",
                hits.len(),
                total_count
            );

            // 3. 预翻译热门模组标题（MCIM → MC百科 → 机翻），并发 6 路加速
            use tokio::sync::Semaphore;
            let sem = Arc::new(Semaphore::new(6));
            let mut tasks = Vec::with_capacity(hits.len());
            for hit in &hits {
                let permit = sem.clone().acquire_owned().await.map_err(|e| {
                    log::warn!("预加载翻译并发信号量错误: {}", e);
                }).ok();
                let title = hit.title.clone();
                tasks.push(tauri::async_runtime::spawn(async move {
                    let _ = permit;
                    let _ = translate_title_zh(&title).await;
                }));
            }
            for t in tasks {
                let _ = t.await;
            }
            log::info!("预加载：热门模组标题翻译已完成");
        }
        Err(e) => {
            log::warn!("预加载热门模组失败: {}", e);
        }
    }

    // 推送事件通知前端缓存已就绪
    let _ = handle.emit("startup-preload-done", true);
    log::info!("预加载启动数据：完成");
}
